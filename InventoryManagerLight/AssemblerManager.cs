using System;
using System.Collections.Generic;
#if TORCH
using Sandbox.ModAPI;
using Sandbox.ModAPI.Ingame;
using VRage.Game;
using VRage.Game.ModAPI.Ingame;
#endif

namespace InventoryManagerLight
{
    // Monitors item-subtype totals and automatically queues deficits in assemblers.
    //
    // Two configuration modes (both may be used simultaneously):
    //
    //   1. Per-assembler tag (CustomData preferred, block name also supported):
    //      Add one or more IML:MIN= lines to an assembler's CustomData OR its block name.
    //      That assembler becomes the exclusive producer for those items.
    //      Example CustomData (multi-line, recommended):
    //        IML:MIN=SteelPlate:1000
    //        IML:MIN=MotorComponent:500,Construction:2000
    //      Example block name (single line only):
    //        Assembler [IML:MIN=SteelPlate:1000,MotorComponent:500]
    //
    //   2. Global config (RuntimeConfig.AssemblerThresholds):
    //      Fallback for items not claimed by any tagged assembler.
    //      The least-loaded assembler in Assembly mode is chosen automatically.
    //
    // All calls must be made from the game thread.
    public class AssemblerManager
    {
        private readonly RuntimeConfig _config;
        private readonly ILogger _logger;

        public AssemblerManager(RuntimeConfig config, ILogger logger)
        {
            _config = config;
            _logger = logger;
        }

        // Parse IML:MIN= entries from an assembler's CustomData (multi-line) or block name (single line fallback).
        // Each value may contain one or more SubtypeId:Amount pairs, comma-separated.
        //   IML:MIN=SteelPlate:1000
        //   IML:MIN=MotorComponent:500,Construction:2000
        private Dictionary<string, int> ParseAssemblerThresholds(string customData, string name = null)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var minKey = _config.ContainerTagPrefix + "MIN=";

            // Primary: scan each line of CustomData
            if (!string.IsNullOrWhiteSpace(customData))
            {
                foreach (var line in customData.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith(minKey, StringComparison.OrdinalIgnoreCase)) continue;
                    var value = trimmed.Substring(minKey.Length).Trim().TrimEnd(']', ')', '>');
                    ParseMinPairs(value, result);
                }
            }

            // Fallback: check block name if CustomData had no MIN= entries
            if (result.Count == 0 && !string.IsNullOrWhiteSpace(name))
            {
                var idx = name.IndexOf(minKey, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var value = name.Substring(idx + minKey.Length).Trim().TrimEnd(']', ')', '>');
                    ParseMinPairs(value, result);
                }
            }

            return result;
        }

        private static void ParseMinPairs(string value, Dictionary<string, int> result)
        {
            foreach (var pair in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Trim().Split(new[] { ':' }, 2);
                if (parts.Length != 2) continue;
                int amount;
                if (int.TryParse(parts[1].Trim(), out amount) && amount > 0)
                    result[parts[0].Trim()] = amount;
            }
        }

#if TORCH
        public List<string> ScanAndQueue(List<Sandbox.ModAPI.IMyTerminalBlock> allBlocks)
        {
            var diag = new List<string>();
            var hasGlobalThresholds = _config.AssemblerThresholds != null && _config.AssemblerThresholds.Count > 0;

            // Collect assemblers in Assembly mode and parse their CustomData/name thresholds
            var assemblers = new List<Sandbox.ModAPI.IMyAssembler>();
            var customDataThresholds = new Dictionary<long, Dictionary<string, int>>(); // entityId -> thresholds

            foreach (var tb in allBlocks)
            {
                var asm = tb as Sandbox.ModAPI.IMyAssembler;
                if (asm == null || !asm.IsFunctional) continue;
                // Check Mode via reflection — avoids hard dependency on SpaceEngineers.Game.ModAPI.Ingame
                try
                {
                    var modeVal = asm.GetType().GetProperty("Mode")?.GetValue(asm);
                    if (modeVal != null && (int)modeVal != 0)
                    {
                        diag.Add($"  SKIP '{asm.CustomName}' — not in Assembly mode");
                        continue;
                    }
                }
                catch { /* include optimistically */ }
                assemblers.Add(asm);

                string cd = null; string asmName = null;
                try { cd = asm.CustomData; } catch { }
                try { asmName = asm.CustomName; } catch { }
                var thresholds = ParseAssemblerThresholds(cd, asmName);
                if (thresholds.Count > 0)
                {
                    customDataThresholds[asm.EntityId] = thresholds;
                    var pairs = new System.Text.StringBuilder();
                    foreach (var kv in thresholds) { if (pairs.Length > 0) pairs.Append(", "); pairs.Append($"{kv.Key}:{kv.Value}"); }
                    diag.Add($"  FOUND '{asmName}' — targets: {pairs}");
                }
            }

            if (assemblers.Count == 0) { diag.Insert(0, "No functional assemblers in Assembly mode found."); return diag; }
            if (!hasGlobalThresholds && customDataThresholds.Count == 0)
            {
                diag.Insert(0, $"Found {assemblers.Count} assembler(s) but none have IML:MIN= tags and no global thresholds are set.");
                return diag;
            }

            // Build inventory totals by item subtype.
            // Only IML-managed containers (tagged with a category) and production block output
            // slots (index 1) are counted. Unmanaged containers — NPC/pirate ships, player
            // containers with no IML tag — are intentionally excluded so foreign loot does not
            // inflate the stock count and falsely suppress queuing.
            // Production block INPUT inventories (index 0) are also excluded — materials already
            // pulled into an assembler/refinery input are committed to the current job.
            var totals = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var tb in allBlocks)
            {
                try
                {
                    if (tb is Sandbox.ModAPI.IMyProductionBlock)
                    {
                        // Production blocks: count output slot only (index 1)
                        if (tb.InventoryCount < 2) continue;
                        try
                        {
                            var inv = tb.GetInventory(1);
                            if (inv == null) continue;
                            var items = new List<VRage.Game.ModAPI.Ingame.MyInventoryItem>();
                            inv.GetItems(items);
                            foreach (var it in items)
                            {
                                var sub = it.Type.SubtypeId;
                                float prev; totals.TryGetValue(sub, out prev);
                                totals[sub] = prev + (float)it.Amount;
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        // Non-production blocks: only count IML-managed containers
                        string name = null; string cd = null;
                        try { name = tb.CustomName; } catch { }
                        try { cd = tb.CustomData; } catch { }
                        var tag = ContainerMatcher.ParseContainerTag(name, cd, _config.ContainerTagPrefix);
                        if (tag.Categories == null || tag.Categories.Length == 0) continue;
                        for (int i = 0; i < tb.InventoryCount; i++)
                        {
                            try
                            {
                                var inv = tb.GetInventory(i);
                                if (inv == null) continue;
                                var items = new List<VRage.Game.ModAPI.Ingame.MyInventoryItem>();
                                inv.GetItems(items);
                                foreach (var it in items)
                                {
                                    var sub = it.Type.SubtypeId;
                                    float prev; totals.TryGetValue(sub, out prev);
                                    totals[sub] = prev + (float)it.Amount;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            // --- Phase 1: CustomData-tagged assemblers ---
            // Each tagged assembler owns its items exclusively. Only that assembler's
            // existing queue is checked, and only that assembler receives new queue items.
            var claimedByCustomData = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var asm in assemblers)
            {
                Dictionary<string, int> thresholds;
                if (!customDataThresholds.TryGetValue(asm.EntityId, out thresholds)) continue;

                // Read this assembler's own queue
                var asmQueued = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var queue = new List<Sandbox.ModAPI.Ingame.MyProductionItem>();
                    asm.GetQueue(queue);
                    foreach (var qi in queue)
                    {
                        var sub = qi.BlueprintId.SubtypeId.String;
                        float prev; asmQueued.TryGetValue(sub, out prev);
                        asmQueued[sub] = prev + (float)qi.Amount;
                    }
                }
                catch { }

                foreach (var kv in thresholds)
                {
                    claimedByCustomData.Add(kv.Key);
                    try
                    {
                        float current; totals.TryGetValue(kv.Key, out current);
                        float alreadyQueued; asmQueued.TryGetValue(kv.Key, out alreadyQueued);
                        float projected = current + alreadyQueued;
                        if (projected >= kv.Value)
                        {
                            diag.Add($"    {kv.Key}: stock={current:N0} queued={alreadyQueued:N0} target={kv.Value:N0} — OK");
                            continue;
                        }

                        float deficit = kv.Value - projected;
                        VRage.Game.MyDefinitionId blueprintId;
                        if (!VRage.Game.MyDefinitionId.TryParse("MyObjectBuilder_BlueprintDefinition", kv.Key, out blueprintId))
                        {
                            _logger?.Debug($"IML: AssemblerManager: no blueprint for '{kv.Key}' in '{asm.CustomName}' — check subtype name");
                            diag.Add($"    {kv.Key}: NO BLUEPRINT — check subtype spelling");
                            continue;
                        }

                        asm.AddQueueItem(blueprintId, (VRage.MyFixedPoint)deficit);
                        _logger?.Info($"IML: Auto-queued {deficit:N0}x {kv.Key} in '{asm.CustomName}' [CustomData] (stock:{current:N0} queued:{alreadyQueued:N0} target:{kv.Value:N0})");
                        diag.Add($"    {kv.Key}: stock={current:N0} queued={alreadyQueued:N0} target={kv.Value:N0} — QUEUED {deficit:N0}");

                        // Keep local queue map consistent for this scan pass
                        float q; asmQueued.TryGetValue(kv.Key, out q);
                        asmQueued[kv.Key] = q + deficit;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"IML: AssemblerManager: exception queuing '{kv.Key}' in '{asm.CustomName}': {ex.Message}");
                        diag.Add($"    {kv.Key}: ERROR — {ex.Message}");
                    }
                }
            }

            // --- Phase 2: Global config fallback ---
            // Items not claimed by any tagged assembler use the least-loaded assembler.
            if (!hasGlobalThresholds) return diag;

            // Sum queued amounts across all assemblers for global items
            var globalQueued = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var asm in assemblers)
            {
                try
                {
                    var queue = new List<Sandbox.ModAPI.Ingame.MyProductionItem>();
                    asm.GetQueue(queue);
                    foreach (var qi in queue)
                    {
                        var sub = qi.BlueprintId.SubtypeId.String;
                        float prev; globalQueued.TryGetValue(sub, out prev);
                        globalQueued[sub] = prev + (float)qi.Amount;
                    }
                }
                catch { }
            }

            diag.Add("  [GlobalConfig]");
            foreach (var kv in _config.AssemblerThresholds)
            {
                if (claimedByCustomData.Contains(kv.Key)) { diag.Add($"    {kv.Key}: claimed by tagged assembler — skipped"); continue; }
                try
                {
                    float current; totals.TryGetValue(kv.Key, out current);
                    float alreadyQueued; globalQueued.TryGetValue(kv.Key, out alreadyQueued);
                    float projected = current + alreadyQueued;
                    if (projected >= kv.Value)
                    {
                        diag.Add($"    {kv.Key}: stock={current:N0} queued={alreadyQueued:N0} target={kv.Value:N0} — OK");
                        continue;
                    }

                    float deficit = kv.Value - projected;
                    VRage.Game.MyDefinitionId blueprintId;
                    if (!VRage.Game.MyDefinitionId.TryParse("MyObjectBuilder_BlueprintDefinition", kv.Key, out blueprintId))
                    {
                        _logger?.Debug($"IML: AssemblerManager: no blueprint for '{kv.Key}' — check subtype name in AssemblerThresholds config");
                        diag.Add($"    {kv.Key}: NO BLUEPRINT — check subtype spelling");
                        continue;
                    }

                    // Pick the assembler with the shortest queue
                    Sandbox.ModAPI.IMyAssembler target = null;
                    int minQueueLen = int.MaxValue;
                    foreach (var asm in assemblers)
                    {
                        try
                        {
                            var q = new List<Sandbox.ModAPI.Ingame.MyProductionItem>();
                            asm.GetQueue(q);
                            if (q.Count < minQueueLen) { minQueueLen = q.Count; target = asm; }
                        }
                        catch { if (target == null) target = asm; }
                    }
                    if (target == null) continue;

                    target.AddQueueItem(blueprintId, (VRage.MyFixedPoint)deficit);
                    _logger?.Info($"IML: Auto-queued {deficit:N0}x {kv.Key} in '{target.CustomName}' [GlobalConfig] (stock:{current:N0} queued:{alreadyQueued:N0} target:{kv.Value:N0})");
                    diag.Add($"    {kv.Key}: stock={current:N0} queued={alreadyQueued:N0} target={kv.Value:N0} — QUEUED {deficit:N0} in '{target.CustomName}'");

                    float q2; globalQueued.TryGetValue(kv.Key, out q2);
                    globalQueued[kv.Key] = q2 + deficit;
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"IML: AssemblerManager: exception for '{kv.Key}': {ex.Message}");
                    diag.Add($"    {kv.Key}: ERROR — {ex.Message}");
                }
            }
            return diag;
        }
#endif
    }
}

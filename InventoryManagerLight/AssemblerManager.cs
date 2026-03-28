using System;
using System.Collections.Generic;
#if TORCH
using Sandbox.Definitions;
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
        private struct PendingQueueAddition
        {
            public Sandbox.ModAPI.IMyAssembler Asm;
            public VRage.Game.MyDefinitionId Blueprint;
            public VRage.MyFixedPoint Amount;
            public string LogMessage;
        }

        private readonly Queue<PendingQueueAddition> _pendingAdditions = new Queue<PendingQueueAddition>();

        // Maps item SubtypeId → blueprint MyDefinitionId (for AddQueueItem).
        // Built lazily from MyDefinitionManager on first scan; handles mods that rename items
        // but keep vanilla blueprint SubtypeIds (e.g. MotorComponent blueprint → Motor item).
        private Dictionary<string, VRage.Game.MyDefinitionId> _itemToBlueprint;
        // Maps blueprint SubtypeId → item SubtypeId (for GetQueue readback).
        private Dictionary<string, string> _blueprintToItemSubtype;

        public List<string> ScanAndQueue(List<Sandbox.ModAPI.IMyTerminalBlock> allBlocks)
        {
            _pendingAdditions.Clear();
            EnsureBlueprintMappings();
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

            // Build inventory totals by item subtype, scoped to the conveyor-connected grid groups
            // that contain our assemblers. ALL inventory blocks in those groups are counted — not
            // just IML-tagged ones — so stock sitting in normal untagged cargo containers is visible.
            // NPC/pirate grids are excluded automatically because they are never in the same
            // conveyor group as a player assembler.
            // Production block INPUT inventories (index 0) are excluded — materials already pulled
            // into an assembler/refinery input are committed to the current job.

            // Determine relevant conveyor group keys (one GetGroup call per unique grid, cached).
            var gridGroupKeyCache = new Dictionary<long, long>();
            var relevantGroupKeys = new HashSet<long>();
            foreach (var asm in assemblers)
            {
                try
                {
                    var grid = asm.CubeGrid as VRage.Game.ModAPI.IMyCubeGrid;
                    if (grid == null) continue;
                    long gk;
                    if (!gridGroupKeyCache.TryGetValue(grid.EntityId, out gk))
                    { gk = InventoryManager.GetConveyorGroupKey(grid); gridGroupKeyCache[grid.EntityId] = gk; }
                    relevantGroupKeys.Add(gk);
                }
                catch { }
            }

            var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var tb in allBlocks)
            {
                try
                {
                    var grid = tb.CubeGrid as VRage.Game.ModAPI.IMyCubeGrid;
                    if (grid == null) continue;
                    long gk;
                    if (!gridGroupKeyCache.TryGetValue(grid.EntityId, out gk))
                    { gk = InventoryManager.GetConveyorGroupKey(grid); gridGroupKeyCache[grid.EntityId] = gk; }
                    if (!relevantGroupKeys.Contains(gk)) continue;

                    bool isProduction = tb is Sandbox.ModAPI.IMyProductionBlock;
                    if (isProduction && tb.InventoryCount < 2) continue; // single-inv production blocks have no output slot
                    int startSlot = isProduction ? 1 : 0; // skip production input (index 0)

                    for (int i = startSlot; i < tb.InventoryCount; i++)
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
                                double prev; totals.TryGetValue(sub, out prev);
                                totals[sub] = prev + (double)it.Amount;
                            }
                        }
                        catch { }
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
                var asmQueued = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var queue = new List<Sandbox.ModAPI.Ingame.MyProductionItem>();
                    asm.GetQueue(queue);
                    foreach (var qi in queue)
                    {
                        var bpSub = qi.BlueprintId.SubtypeId.String;
                        string sub;
                        if (!_blueprintToItemSubtype.TryGetValue(bpSub, out sub)) sub = bpSub;
                        double prev; asmQueued.TryGetValue(sub, out prev);
                        asmQueued[sub] = prev + (double)qi.Amount;
                    }
                }
                catch { }

                foreach (var kv in thresholds)
                {
                    claimedByCustomData.Add(kv.Key);
                    try
                    {
                        double current; totals.TryGetValue(kv.Key, out current);
                        double alreadyQueued; asmQueued.TryGetValue(kv.Key, out alreadyQueued);
                        double projected = current + alreadyQueued;
                        if (projected >= kv.Value)
                        {
                            diag.Add($"    {kv.Key}: stock={current:N0} queued={alreadyQueued:N0} target={kv.Value:N0} — OK");
                            continue;
                        }

                        // Ceiling prevents float-precision truncation causing off-by-one (e.g. 998.9999 → 999)
                        int deficit = (int)Math.Ceiling(kv.Value - projected);
                        VRage.Game.MyDefinitionId blueprintId;
                        if (!ResolveBlueprintForItem(kv.Key, out blueprintId))
                        {
                            _logger?.Debug($"IML: AssemblerManager: no blueprint for '{kv.Key}' in '{asm.CustomName}' — check subtype name");
                            diag.Add($"    {kv.Key}: NO BLUEPRINT — check subtype spelling");
                            continue;
                        }

                        _pendingAdditions.Enqueue(new PendingQueueAddition
                        {
                            Asm = asm,
                            Blueprint = blueprintId,
                            Amount = (VRage.MyFixedPoint)deficit,
                            LogMessage = $"IML: Auto-queued {deficit:N0}x {kv.Key} in '{asm.CustomName}' [CustomData] (stock:{current:N0} queued:{alreadyQueued:N0} target:{kv.Value:N0})"
                        });
                        diag.Add($"    {kv.Key}: stock={current:N0} queued={alreadyQueued:N0} target={kv.Value:N0} — QUEUED {deficit:N0}");

                        // Keep local queue map consistent for this scan pass
                        double q; asmQueued.TryGetValue(kv.Key, out q);
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
            var globalQueued = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var asm in assemblers)
            {
                try
                {
                    var queue = new List<Sandbox.ModAPI.Ingame.MyProductionItem>();
                    asm.GetQueue(queue);
                    foreach (var qi in queue)
                    {
                        var bpSub = qi.BlueprintId.SubtypeId.String;
                        string sub;
                        if (!_blueprintToItemSubtype.TryGetValue(bpSub, out sub)) sub = bpSub;
                        double prev; globalQueued.TryGetValue(sub, out prev);
                        globalQueued[sub] = prev + (double)qi.Amount;
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
                    double current; totals.TryGetValue(kv.Key, out current);
                    double alreadyQueued; globalQueued.TryGetValue(kv.Key, out alreadyQueued);
                    double projected = current + alreadyQueued;
                    if (projected >= kv.Value)
                    {
                        diag.Add($"    {kv.Key}: stock={current:N0} queued={alreadyQueued:N0} target={kv.Value:N0} — OK");
                        continue;
                    }

                    // Ceiling prevents float-precision truncation causing off-by-one
                    int deficit = (int)Math.Ceiling(kv.Value - projected);
                    VRage.Game.MyDefinitionId blueprintId;
                    if (!ResolveBlueprintForItem(kv.Key, out blueprintId))
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

                    _pendingAdditions.Enqueue(new PendingQueueAddition
                    {
                        Asm = target,
                        Blueprint = blueprintId,
                        Amount = (VRage.MyFixedPoint)deficit,
                        LogMessage = $"IML: Auto-queued {deficit:N0}x {kv.Key} in '{target.CustomName}' [GlobalConfig] (stock:{current:N0} queued:{alreadyQueued:N0} target:{kv.Value:N0})"
                    });
                    diag.Add($"    {kv.Key}: stock={current:N0} queued={alreadyQueued:N0} target={kv.Value:N0} — QUEUED {deficit:N0} in '{target.CustomName}'");

                    double q2; globalQueued.TryGetValue(kv.Key, out q2);
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

        // Lazily build two dictionaries from MyDefinitionManager.Static:
        //   _itemToBlueprint       : item SubtypeId  → blueprint MyDefinitionId  (used by AddQueueItem)
        //   _blueprintToItemSubtype: blueprint SubtypeId → item SubtypeId          (used when reading GetQueue)
        // Handles mods that rename vanilla components (e.g. MotorComponent→Motor) while keeping
        // the original blueprint SubtypeId unchanged.
        private void EnsureBlueprintMappings()
        {
            if (_itemToBlueprint != null) return;
            _itemToBlueprint = new Dictionary<string, VRage.Game.MyDefinitionId>(StringComparer.OrdinalIgnoreCase);
            _blueprintToItemSubtype = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var defs = MyDefinitionManager.Static.GetBlueprintDefinitions();
                foreach (var bp in defs)
                {
                    if (bp?.Results == null || bp.Results.Length == 0) continue;
                    var bpSub = bp.Id.SubtypeId.String;
                    var itemSub = bp.Results[0].Id.SubtypeId.String;
                    if (string.IsNullOrEmpty(bpSub) || string.IsNullOrEmpty(itemSub)) continue;

                    _blueprintToItemSubtype[bpSub] = itemSub;

                    // Prefer the blueprint whose SubtypeId matches the item (the canonical one);
                    // only set a cross-name mapping if no canonical mapping exists yet.
                    VRage.Game.MyDefinitionId existing;
                    bool alreadyMapped = _itemToBlueprint.TryGetValue(itemSub, out existing);
                    if (!alreadyMapped || string.Equals(bpSub, itemSub, StringComparison.OrdinalIgnoreCase))
                        _itemToBlueprint[itemSub] = bp.Id;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"IML: AssemblerManager: failed to build blueprint mappings: {ex.Message}");
            }
        }

        // Resolve the blueprint to use when queuing an item by SubtypeId.
        // Falls back to a same-name TryParse so vanilla items continue to work
        // even if EnsureBlueprintMappings encountered an error.
        private bool ResolveBlueprintForItem(string itemSubtype, out VRage.Game.MyDefinitionId blueprintId)
        {
            if (_itemToBlueprint != null && _itemToBlueprint.TryGetValue(itemSubtype, out blueprintId))
                return true;
            return VRage.Game.MyDefinitionId.TryParse("MyObjectBuilder_BlueprintDefinition", itemSubtype, out blueprintId);
        }

        // Apply one deferred AddQueueItem call. Invoke once per game tick so that queue-change
        // replication packets are spread across ticks instead of bursting all at once.
        // A burst of simultaneous queue-change packets can cause clients that have the assembler
        // terminal open to desync and disconnect when they close the K menu.
        public void ApplyOnePendingAddition()
        {
            if (_pendingAdditions.Count == 0) return;
            var item = _pendingAdditions.Dequeue();
            try
            {
                if (item.Asm != null)
                {
                    item.Asm.AddQueueItem(item.Blueprint, item.Amount);
                    if (item.LogMessage != null)
                        _logger?.Info(item.LogMessage);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"IML: AssemblerManager: deferred AddQueueItem failed: {ex.Message}");
            }
        }
#endif
    }
}

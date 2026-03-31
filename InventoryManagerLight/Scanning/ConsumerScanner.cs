using System;
using System.Collections.Generic;
using System.Linq;

#if TORCH
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.Game;
using Ingame = VRage.Game.ModAPI.Ingame;
using VRage.ModAPI;
#endif

namespace InventoryManagerLight
{
    // Scans assemblers/refineries on the game thread and updates the InventoryDemandTracker with missing material estimates.
    public class ConsumerScanner
    {
        private readonly RuntimeConfig _config;
        private readonly CategoryResolver _resolver;
        private readonly ILogger _logger;

        public ConsumerScanner(RuntimeConfig config, CategoryResolver resolver, ILogger logger = null)
        {
            _config = config ?? new RuntimeConfig();
            _resolver = resolver;
            _logger = logger ?? new DefaultLogger();
            _tickCounter = 0;
        }

        // Run on game thread periodically to refresh demand estimates
        private int _tickCounter;

        // Run on game thread periodically to refresh demand estimates. Honors config.ScannerIntervalTicks and applies decay.
        public void ScanAndUpdate(InventoryDemandTracker demand)
        {
            if (demand == null) return;

            // apply decay first (always safe)
            try { demand.ApplyDecay(_config.DemandDecayFactor); } catch { }

            // interval check
            _tickCounter++;
            if (_config.ScannerIntervalTicks > 0 && _tickCounter < _config.ScannerIntervalTicks) return;
            _tickCounter = 0;

#if TORCH
            try
            {
                // GetEntities() only returns top-level entities (grids, characters) — never individual blocks.
                // Walk grids via GetBlocks() instead, the same approach used by InventoryManager.
                var blocks = GetAllTerminalBlocks();

                foreach (var block in blocks)
                {

                    var typeName = block.GetType().Name ?? string.Empty;
                    bool isAssembler = typeName.IndexOf("Assembler", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isRefinery = typeName.IndexOf("Refinery", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isAssembler && !isRefinery) continue;

                    // If assembler try to read its production queue and compute exact missing components
                    if (isAssembler)
                    {
                        try
                        {
                            var queueItems = TryGetAssemblerQueue(block);
                            if (queueItems != null)
                            {
                                foreach (var qi in queueItems)
                                {
                                    try
                                    {
                                        var qiType = qi.GetType();
                                        object blueprintIdObj = qiType.GetProperty("BlueprintId")?.GetValue(qi)
                                            ?? qiType.GetProperty("Blueprint")?.GetValue(qi);
                                        object amountObj = qiType.GetProperty("Amount")?.GetValue(qi) ?? qiType.GetProperty("AmountToBuild")?.GetValue(qi);

                                        if (blueprintIdObj is VRage.Game.MyDefinitionId bpId)
                                        {
                                            int qty = 1;
                                            try { if (amountObj is VRage.MyFixedPoint mfp) qty = mfp.ToIntSafe(); else if (amountObj is int ai) qty = ai; }
                                            catch { }

                                            var bpDef = TryGetBlueprintDefinition(bpId);
                                            if (bpDef != null)
                                            {
                                                var prereqProp = bpDef.GetType().GetProperty("Prerequisites") ?? bpDef.GetType().GetProperty("Prerequisite") ?? bpDef.GetType().GetProperty("RequiredItems");
                                                if (prereqProp != null)
                                                {
                                                    var prereqs = prereqProp.GetValue(bpDef) as System.Collections.IEnumerable;
                                                    if (prereqs != null)
                                                    {
                                                        foreach (var pr in prereqs)
                                                        {
                                                            try
                                                            {
                                                                var idProp = pr.GetType().GetProperty("Id") ?? pr.GetType().GetProperty("BlueprintId");
                                                                var amtProp = pr.GetType().GetProperty("Amount") ?? pr.GetType().GetProperty("Quantity");
                                                                if (idProp == null || amtProp == null) continue;
                                                                var compIdObj = idProp.GetValue(pr);
                                                                var compAmtObj = amtProp.GetValue(pr);
                                                                if (!(compIdObj is VRage.Game.MyDefinitionId compId)) continue;
                                                                int compPer = 1;
                                                                try { if (compAmtObj is VRage.MyFixedPoint mf) compPer = mf.ToIntSafe(); else if (compAmtObj is int ai2) compPer = ai2; }
                                                                catch { }

                                                                int totalReq = compPer * Math.Max(1, qty);

                                                                int presentComp = 0;
                                                                for (int i = 0; i < block.InventoryCount; i++)
                                                                {
                                                                    var inv = block.GetInventory(i);
                                                                    if (inv == null) continue;
                                                                    var items = new List<Ingame.MyInventoryItem>();
                                                                    inv.GetItems(items);
                                                                    foreach (var it in items)
                                                                    {
                                                                        try
                                                                        {
                                                                            var id = GetDefinitionIdFromStack(it);
                                                                            if (id.Equals(compId)) presentComp += it.Amount.ToIntSafe();
                                                                        }
                                                                        catch { }
                                                                    }
                                                                }

                                                                int missing = Math.Max(0, totalReq - presentComp);
                                                                if (missing > 0)
                                                                {
                                                                    demand.AddDemand(compId, missing);
                                                                    demand.AddDemandForOwner(block.EntityId, compId, missing);
                                                                }
                                                            }
                                                            catch { }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch { }
                                }

                                // processed assembler queue; continue to next block
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error("ConsumerScanner (assembler queue) exception: " + ex.Message);
                        }
                    }

                    // For each configured category, compute current amount in block inventories
                    foreach (var cat in _config.CategoryMappings.Keys)
                    {
                        var defs = _resolver?.Resolve(cat)?.ToArray() ?? Array.Empty<VRage.Game.MyDefinitionId>();
                        if (defs.Length == 0) continue;

                        long present = 0;
                        for (int i = 0; i < block.InventoryCount; i++)
                        {
                            var inv = block.GetInventory(i);
                            if (inv == null) continue;
                            var items = new List<Ingame.MyInventoryItem>();
                            inv.GetItems(items);
                            foreach (var it in items)
                            {
                                try
                                {
                                    var id = GetDefinitionIdFromStack(it);
                                    if (defs.Any(d => d.Equals(id)))
                                    {
                                        present += it.Amount.ToIntSafe();
                                    }
                                }
                                catch { }
                            }
                        }

                        int target = 500;
                        if (isRefinery) target = 2000;

                        if (present < target)
                        {
                            int missing = (int)(target - present);
                            int per = Math.Max(1, missing / defs.Length);
                            foreach (var d in defs)
                            {
                                demand.AddDemand(d, per);
                                demand.AddDemandForOwner(block.EntityId, d, per);
                            }
                            _logger.Debug($"ConsumerScanner: block {block.EntityId} needs {missing} of {cat} (present={present})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("ConsumerScanner exception: " + ex.Message);
            }

            // Urgency detection: second pass counting items in managed containers for each
            // category that has an UrgentStockThreshold configured. Runs on the same interval
            // as the main consumer scan so urgency is detected within ScannerIntervalTicks ticks.
            if (_config.UrgentStockThresholds.Count > 0)
            {
                try { UpdateUrgencyState(demand); }
                catch (Exception ex) { _logger.Debug("ConsumerScanner: urgency scan failed: " + ex.Message); }
            }
#else
            // no-op outside TORCH
#endif
        }

#if TORCH
        // Walk all grids and collect every terminal block. GetEntities() only returns top-level
        // entities (grids, floaters, characters) — individual blocks must be fetched via GetBlocks().
        private static List<IMyTerminalBlock> GetAllTerminalBlocks()
        {
            var result = new List<IMyTerminalBlock>();
            try
            {
                var grids = new HashSet<VRage.ModAPI.IMyEntity>();
                MyAPIGateway.Entities.GetEntities(grids, e => e is VRage.Game.ModAPI.IMyCubeGrid);
                foreach (var gridEnt in grids)
                {
                    try
                    {
                        var grid = gridEnt as VRage.Game.ModAPI.IMyCubeGrid;
                        if (grid == null) continue;
                        var slims = new List<VRage.Game.ModAPI.IMySlimBlock>();
                        grid.GetBlocks(slims, b => b?.FatBlock is IMyTerminalBlock);
                        foreach (var slim in slims)
                        {
                            try
                            {
                                var tb = slim?.FatBlock as IMyTerminalBlock;
                                if (tb != null) result.Add(tb);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        private object TryGetBlueprintDefinition(VRage.Game.MyDefinitionId bpId)
        {
            try
            {
                var mgrType = Type.GetType("VRage.Game.MyDefinitionManager, VRage.Game");
                if (mgrType != null)
                {
                    var staticProp = mgrType.GetProperty("Static", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    var mgr = staticProp?.GetValue(null);
                    var tryGet = mgrType.GetMethod("TryGetBlueprintDefinitions", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                 ?? mgrType.GetMethod("TryGetBlueprintDefinition", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                 ?? mgrType.GetMethod("GetBlueprintDefinition", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (tryGet != null)
                    {
                        try
                        {
                            var res = tryGet.Invoke(mgr, new object[] { bpId });
                            return res;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        private IEnumerable<object> TryGetAssemblerQueue(IMyTerminalBlock block)
        {
            try
            {
                var t = block.GetType();
                var queueProp = t.GetProperty("Queue") ?? t.GetProperty("ProductionQueue") ?? t.GetProperty("BlueprintQueue");
                if (queueProp == null) return null;
                var queue = queueProp.GetValue(block) as System.Collections.IEnumerable;
                if (queue == null) return null;
                var list = new List<object>();
                foreach (var q in queue) list.Add(q);
                return list;
            }
            catch { }
            return null;
        }

        private VRage.Game.MyDefinitionId GetDefinitionIdFromStack(Ingame.MyInventoryItem stack)
        {
            try
            {
                // MyInventoryItem.Type holds TypeId (e.g. "MyObjectBuilder_Ingot") and SubtypeId (e.g. "Iron").
                // There is no Content property on this struct — that lives on MyObjectBuilder_PhysicalObject.
                VRage.Game.MyDefinitionId def;
                if (VRage.Game.MyDefinitionId.TryParse(stack.Type.TypeId, stack.Type.SubtypeId, out def))
                    return def;
            }
            catch { }
            return default(VRage.Game.MyDefinitionId);
        }

        // Count items of each urgent category across all IML-managed containers and update
        // the demand tracker's urgency flags. Only categories listed in UrgentStockThresholds
        // are checked, so the scan is cheap when the feature is not configured.
        private void UpdateUrgencyState(InventoryDemandTracker demand)
        {
            var catTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in GetAllTerminalBlocks())
            {
                try
                {
                    string name = null; string cd = null;
                    try { name = block.CustomName; } catch { }
                    try { cd = (string)block.GetType().GetProperty("CustomData")?.GetValue(block); } catch { }
                    var tag = ContainerMatcher.ParseContainerTag(name, cd, _config.ContainerTagPrefix);
                    if (tag.Categories == null || tag.Categories.Length == 0) continue;

                    foreach (var cat in tag.Categories)
                    {
                        if (!_config.UrgentStockThresholds.ContainsKey(cat)) continue;
                        for (int i = 0; i < block.InventoryCount; i++)
                        {
                            var inv = block.GetInventory(i);
                            if (inv == null) continue;
                            var items = new List<Ingame.MyInventoryItem>();
                            inv.GetItems(items);
                            foreach (var it in items)
                            {
                                try
                                {
                                    var id = GetDefinitionIdFromStack(it);
                                    if (_resolver != null && _resolver.ItemMatchesCategory(id, id.ToString(), cat))
                                    {
                                        long prev; catTotals.TryGetValue(cat, out prev);
                                        catTotals[cat] = prev + it.Amount.ToIntSafe();
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            }

            foreach (var kv in _config.UrgentStockThresholds)
            {
                long total; catTotals.TryGetValue(kv.Key, out total);
                bool wasUrgent = demand.IsUrgent(kv.Key);
                bool isNowUrgent = total < kv.Value;
                if (isNowUrgent != wasUrgent)
                {
                    demand.SetUrgent(kv.Key, isNowUrgent);
                    if (isNowUrgent)
                        _logger.Info($"IML: Urgency triggered — {kv.Key} stock {total} < threshold {kv.Value}. Urgent transfers will be prioritised.");
                    else
                        _logger.Info($"IML: Urgency cleared — {kv.Key} stock {total} >= threshold {kv.Value}. Normal scheduling resumed.");
                }
            }
        }
#endif
    }
}

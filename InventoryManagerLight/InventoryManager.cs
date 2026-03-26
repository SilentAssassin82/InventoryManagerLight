using System;
using System.Collections.Concurrent;
#if !NETSTANDARD2_0
using System.Collections.Generic;
#else
using System.Collections.Generic;
#endif
using System.Linq;
#if TORCH
using Sandbox.ModAPI;
using VRage.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Ingame;
#endif

namespace InventoryManagerLight
{
    public class InventoryManager : IDisposable
    {
        private readonly ConcurrentQueue<InventorySnapshot[]> _snapshotQueue = new ConcurrentQueue<InventorySnapshot[]>();
        private readonly ConcurrentQueue<TransferBatch> _batchQueue = new ConcurrentQueue<TransferBatch>();
        private readonly RuntimeConfig _config;
        private readonly Snapshotter _snapshotter;
        private readonly Planner _planner;
        private readonly ThrottledApplier _applier;
        private readonly ILogger _logger;
        private readonly InventoryDemandTracker _demandTracker;
        private readonly ConsumerScanner _consumerScanner;
        private readonly ConveyorScanner _conveyorScanner;
        private int _tickCounter;
        private int _totalSortPasses;
        // Sentinel category used to mark production block output snapshots as drain-only sources.
        // Not in CategoryMappings so ItemMatchesCategory always returns false — every item is misplaced.
        private const string DrainSentinelCategory = "__DRAIN__";

        public InventoryManager(RuntimeConfig config = null)
        {
            _config = config ?? new RuntimeConfig();
            // create logger according to build/runtime config
            Log.CurrentLevel = _config.LoggingLevel;
#if TORCH
            _logger = new NLogLogger(_config.LoggingLevel);
#else
            _logger = new DefaultLogger(_config.LoggingLevel);
#endif

            _snapshotter = new Snapshotter(_snapshotQueue, _config, _logger);
            var resolver = new CategoryResolver(_config);
            _conveyorScanner = new ConveyorScanner(_config, _logger);
            var demand = new InventoryDemandTracker();
            _consumerScanner = new ConsumerScanner(_config, resolver, _logger);
            _demandTracker = demand;
            _planner = new Planner(_snapshotQueue, _batchQueue, _config, _logger, resolver, _conveyorScanner, demand);
#if TORCH
            _applier = new ThrottledApplier(_batchQueue, _config, new TorchInventoryAdapter(_logger), _logger);
#else
            _applier = new ThrottledApplier(_batchQueue, _config, _logger);
#endif
        }

        // Allow constructing with a provided adapter (e.g., real Torch adapter)
        public InventoryManager(RuntimeConfig config, IInventoryAdapter adapter)
        {
            _config = config ?? new RuntimeConfig();
            // create logger according to build/runtime config
            Log.CurrentLevel = _config.LoggingLevel;
#if TORCH
            _logger = new NLogLogger(_config.LoggingLevel);
#else
            _logger = new DefaultLogger(_config.LoggingLevel);
#endif

            _snapshotter = new Snapshotter(_snapshotQueue, _config, _logger);
            var resolver = new CategoryResolver(_config);
            _planner = new Planner(_snapshotQueue, _batchQueue, _config, _logger, resolver);
            _applier = new ThrottledApplier(_batchQueue, _config, adapter ?? new DefaultInventoryAdapter(), _logger);
        }

        public void EnqueueReplan(ReplanRequest req)
        {
            _planner.EnqueueReplanRequest(req);
        }

        public Snapshotter Snapshotter => _snapshotter;

        // Dump runtime state for diagnostics (call from game thread)
        public void DumpState()
        {
            try
            {
                _logger?.Info("IML: DumpState begin");
#if TORCH
                // enumerate terminal blocks and parse container tags
                int managedCount = 0;
                foreach (var tb in GetAllTerminalBlocks())
                {
                    try
                    {
                        string name = null; string cd = null;
                        try { name = tb.CustomName; } catch { }
                        try { cd = tb.CustomData; } catch { }
                        var tag = ContainerMatcher.ParseContainerTag(name, cd, _config.ContainerTagPrefix);
                        if (tag.Categories != null && tag.Categories.Length > 0)
                        {
                            managedCount++;
                            var gridId = tb.CubeGrid != null ? tb.CubeGrid.EntityId.ToString() : "(none)";
                            _logger?.Info($"IML: Managed container {tb.EntityId} name='{name}' grid='{gridId}' cats=[{string.Join(",", tag.Categories)}] group='{tag.Group}'");
                        }
                    }
                    catch { }
                }
                _logger?.Info($"IML: Managed containers found: {managedCount}");
#endif
                // queued batches
                try
                {
                    var arr = _batchQueue.ToArray();
                    _logger?.Info($"IML: Pending batches: {arr.Length}");
                    int bi = 0;
                    foreach (var b in arr)
                    {
                        if (bi++ > 20) break;
                        _logger?.Info($"IML: Batch ops: {b?.Ops?.Count ?? 0}");
                    }
                }
                catch { }
                _logger?.Info("IML: DumpState end");
            }
            catch (Exception ex)
            {
                _logger?.Error("DumpState failed: " + ex.Message);
            }
        }

        // Trigger a sort for a specific owner. Categorization-based routing requires the full
        // set of managed containers as context, so this delegates to TriggerSortAll.
        public void TriggerSortForOwner(long ownerEntityId)
        {
            try { TriggerSortAll(); } catch { }
        }

        // Call from game thread each tick
        public void Tick()
        {
            // refresh consumer demand first (game thread)
            _consumerScanner?.ScanAndUpdate(_demandTracker);
            // Poll for SortNow flags at configured interval
            try
            {
                _tickCounter++;
                if (_config.SortScanIntervalTicks > 0 && (_tickCounter % Math.Max(1, _config.SortScanIntervalTicks)) == 0)
                {
#if TORCH
                    try
                    {
                        foreach (var tb in GetAllTerminalBlocks())
                        {
                            try
                            {
                                string cd = null;
                                try { cd = (string)tb.GetType().GetProperty("CustomData")?.GetValue(tb); } catch { }
                                if (string.IsNullOrEmpty(cd)) continue;
                                var idx = cd.IndexOf(_config.ContainerTagPrefix + "SortNow=", StringComparison.OrdinalIgnoreCase);
                                if (idx >= 0)
                                {
                                    // parse value after equals
                                    var token = cd.Substring(idx + (_config.ContainerTagPrefix?.Length ?? 0));
                                    var line = token.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? token;
                                    var eq = line.IndexOf('=');
                                    if (eq >= 0)
                                    {
                                        var val = line.Substring(eq + 1).Trim();
                                        if (val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase))
                                        {
                                            // trigger sort and clear flag
                                            try { TriggerSortForOwner(tb.EntityId); } catch { }
                                            try
                                            {
                                                // remove the SortNow token from CustomData
                                                var newCd = cd.Replace(_config.ContainerTagPrefix + line, string.Empty);
                                                tb.GetType().GetProperty("CustomData")?.SetValue(tb, newCd);
                                            }
                                            catch { }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
#endif
                }
            }
            catch { }

            // Periodic auto-sort — runs every AutoSortIntervalTicks game ticks.
            if (_config.AutoSortIntervalTicks > 0 && (_tickCounter % Math.Max(1, _config.AutoSortIntervalTicks)) == 0)
            {
                try { TriggerSortAll(); } catch { }
            }

            // LCD panel refresh — runs every LcdUpdateIntervalTicks game ticks.
            if (_config.LcdUpdateIntervalTicks > 0 && (_tickCounter % Math.Max(1, _config.LcdUpdateIntervalTicks)) == 0)
            {
#if TORCH
                try { UpdateLcdPanels(); } catch { }
#endif
            }

            _applier.Tick();
        }

        // Walk all grids and collect every terminal block. Must be called from the game thread.
        // Using grid.GetBlocks() is the correct SE API approach — GetEntities() only returns
        // top-level entities (grids, characters, etc.) and never individual blocks.
#if TORCH
        private static List<Sandbox.ModAPI.IMyTerminalBlock> GetAllTerminalBlocks()
        {
            var result = new List<Sandbox.ModAPI.IMyTerminalBlock>();
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
                        grid.GetBlocks(slims, b => b?.FatBlock is Sandbox.ModAPI.IMyTerminalBlock);
                        foreach (var slim in slims)
                        {
                            try
                            {
                                var tb = slim?.FatBlock as Sandbox.ModAPI.IMyTerminalBlock;
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

        private static VRage.Game.MyDefinitionId GetItemDefinitionId(VRage.Game.ModAPI.Ingame.MyInventoryItem it)
        {
            try
            {
                // MyInventoryItem.Type holds TypeId (e.g. "MyObjectBuilder_Ingot") and SubtypeId (e.g. "Iron").
                // There is no Content property on this struct — that lives on MyObjectBuilder_PhysicalObject.
                VRage.Game.MyDefinitionId def;
                if (VRage.Game.MyDefinitionId.TryParse(it.Type.TypeId, it.Type.SubtypeId, out def))
                    return def;
            }
            catch { }
            return default(VRage.Game.MyDefinitionId);
        }

        // Returns a stable key identifying the conveyor-connected group that contains the given grid.
        // Grids docked via connectors share the same key, so they are sorted together.
        // Falls back to the grid's own entity ID when GridGroups is unavailable.
        private static long GetConveyorGroupKey(VRage.Game.ModAPI.IMyCubeGrid grid)
        {
            if (grid == null) return 0L;
            try
            {
                if (MyAPIGateway.GridGroups != null)
                {
                    // Resolve the conveyor link enum member by name search — the exact member name
                    // varies across SE versions so we match by "convey" substring (same as ConveyorScanner).
                    string enumName = null;
                    try { enumName = Enum.GetNames(typeof(GridLinkTypeEnum)).FirstOrDefault(n => n.IndexOf("convey", StringComparison.OrdinalIgnoreCase) >= 0); } catch { }
                    if (!string.IsNullOrEmpty(enumName))
                    {
                        var enumVal = (GridLinkTypeEnum)Enum.Parse(typeof(GridLinkTypeEnum), enumName);
                        var buf = new List<VRage.Game.ModAPI.IMyCubeGrid>();
                        MyAPIGateway.GridGroups.GetGroup(grid, enumVal, buf);
                        if (buf.Count > 0)
                        {
                            long key = long.MaxValue;
                            foreach (var g in buf)
                                if (g.EntityId < key) key = g.EntityId;
                            return key;
                        }
                    }
                }
            }
            catch { }
            return grid.EntityId;
        }

        // Scan all terminal blocks once: collect category stats from managed containers and
        // find LCD panels tagged [IML:LCD] or [IML:LCD=CATEGORY], then write to each panel.
        private void UpdateLcdPanels()
        {
            var catContainers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var catItems      = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var lcdPanels     = new List<(long entityId, string filter)>();

            foreach (var tb in GetAllTerminalBlocks())
            {
                try
                {
                    string name = null; string cd = null;
                    try { name = tb.CustomName; } catch { }
                    try { cd = tb.CustomData; } catch { }

                    // Accumulate category item totals from managed containers
                    var tag = ContainerMatcher.ParseContainerTag(name, cd, _config.ContainerTagPrefix);
                    if (tag.Categories != null && tag.Categories.Length > 0)
                    {
                        int containerItems = 0;
                        for (int i = 0; i < tb.InventoryCount; i++)
                        {
                            try
                            {
                                var inv = tb.GetInventory(i);
                                if (inv == null) continue;
                                var items = new List<VRage.Game.ModAPI.Ingame.MyInventoryItem>();
                                inv.GetItems(items);
                                foreach (var it in items)
                                    containerItems += it.Amount.ToIntSafe();
                            }
                            catch { }
                        }
                        foreach (var cat in tag.Categories)
                        {
                            int prev;
                            catContainers.TryGetValue(cat, out prev); catContainers[cat] = prev + 1;
                            catItems.TryGetValue(cat, out prev);      catItems[cat]      = prev + containerItems;
                        }
                    }

                    // Collect LCD panels tagged for IML display
                    if (tb is Sandbox.ModAPI.IMyTextPanel)
                    {
                        var filter = ParseLcdTag(name, cd);
                        if (filter != null)
                            lcdPanels.Add((tb.EntityId, filter));
                    }
                }
                catch { }
            }

            foreach (var panel in lcdPanels)
            {
                try
                {
                    var text = BuildLcdContent(catContainers, catItems, panel.filter);
                    LcdManager.Instance.EnqueueUpdate(panel.entityId, text);
                }
                catch { }
            }
            LcdManager.Instance.ApplyPendingUpdates();
        }

        // Returns null  → block has no IML:LCD tag (skip it).
        // Returns ""    → tagged as [IML:LCD] with no filter (show all categories).
        // Returns "CAT" → tagged as [IML:LCD=CAT] (show that category only).
        private string ParseLcdTag(string name, string customData)
        {
            var lcdKey = _config.ContainerTagPrefix + "LCD";
            foreach (var src in new[] { customData, name })
            {
                if (string.IsNullOrEmpty(src)) continue;
                int idx = src.IndexOf(lcdKey, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var after = src.Substring(idx + lcdKey.Length);
                // Guard: the character immediately after "LCD" must be a delimiter, not a letter/digit
                // (avoids false matches on hypothetical tags like "IML:LCDISPLAY")
                if (after.Length > 0 && (char.IsLetterOrDigit(after[0]) || after[0] == '_')) continue;
                if (after.Length > 0 && after[0] == '=')
                {
                    var rest = after.Substring(1);
                    var cat = rest.Split(new[] { ' ', '\n', '\r', ']', ')', '>' }, StringSplitOptions.RemoveEmptyEntries);
                    return cat.Length > 0 ? cat[0].Trim() : string.Empty;
                }
                return string.Empty;
            }
            return null;
        }

        private string BuildLcdContent(Dictionary<string, int> catContainers, Dictionary<string, int> catItems, string filter)
        {
            var sb = new System.Text.StringBuilder();
            if (string.IsNullOrEmpty(filter))
            {
                sb.AppendLine("[IML Status]");
                foreach (var cat in catContainers.Keys)
                {
                    int ctns = catContainers[cat];
                    int total = 0; catItems.TryGetValue(cat, out total);
                    sb.AppendLine($"{cat,-12} {ctns,2}x {total,9:N0}");
                }
                sb.Append($"Moved:{_applier.TotalItemsMoved:N0} Ops:{_applier.TotalOpsCompleted:N0}");
            }
            else
            {
                sb.AppendLine($"[IML: {filter}]");
                int ctns = 0;  catContainers.TryGetValue(filter, out ctns);
                int total = 0; catItems.TryGetValue(filter, out total);
                sb.AppendLine($" {ctns} container(s)");
                sb.Append($" {total:N0} items");
            }
            return sb.ToString().TrimEnd();
        }
#endif

        // Trigger a sort pass for every managed container (those tagged with the IML prefix).
        // Builds one unified snapshot so the Planner sees all containers in a single pass.
        // Returns the number of managed containers found. Must be called from the game thread.
        public int TriggerSortAll()
        {
            int count = 0;
            try
            {
#if TORCH
                var allBlocks = GetAllTerminalBlocks();
                // Group snapshots by conveyor-connected grid group. Each independent cluster
                // (base, docked ships, etc.) gets its own sort pass so unrelated grids never mix.
                var groupSnaps = new Dictionary<long, List<InventorySnapshot>>();

                // Pass 1: managed containers — bucket by conveyor group key
                foreach (var tb in allBlocks)
                {
                    try
                    {
                        string name = null; string cd = null;
                        try { name = tb.CustomName; } catch { }
                        try { cd = tb.CustomData; } catch { }
                        var tag = ContainerMatcher.ParseContainerTag(name, cd, _config.ContainerTagPrefix);
                        if (tag.Categories == null || tag.Categories.Length == 0) continue;
                        count++;
                        var groupKey = GetConveyorGroupKey(tb.CubeGrid);
                        List<InventorySnapshot> snaps;
                        if (!groupSnaps.TryGetValue(groupKey, out snaps)) { snaps = new List<InventorySnapshot>(); groupSnaps[groupKey] = snaps; }
                        bool hasItems = false;
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
                                    try
                                    {
                                        var def = GetItemDefinitionId(it);
                                        snaps.Add(new InventorySnapshot { OwnerId = tb.EntityId, ItemDefinitionId = def, Amount = (float)it.Amount, GridId = tb.CubeGrid?.EntityId ?? 0L, ContainerName = name, ContainerCustomData = cd });
                                        hasItems = true;
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                        // sentinel entry for empty containers so Planner registers them as destinations
                        if (!hasItems)
                            snaps.Add(new InventorySnapshot { OwnerId = tb.EntityId, ItemDefinitionId = default, Amount = 0, GridId = tb.CubeGrid?.EntityId ?? 0L, ContainerName = name, ContainerCustomData = cd });
                    }
                    catch { }
                }

                // Pass 2: production drain — only into groups that already have managed containers
                if (_config.DrainProductionOutputs)
                {
                    var drainContainerName = _config.ContainerTagPrefix + DrainSentinelCategory;
                    var noDrainKey        = _config.ContainerTagPrefix + "NoDrain";
                    foreach (var tb in allBlocks)
                    {
                        try
                        {
                            if (!(tb is Sandbox.ModAPI.IMyProductionBlock)) continue;
                            if (tb.InventoryCount < 2) continue; // single-inventory blocks (e.g. SurvivalKit): can't distinguish input/output
                            string name = null; string cd = null;
                            try { name = tb.CustomName; } catch { }
                            try { cd = tb.CustomData; } catch { }
                            bool excluded = (!string.IsNullOrEmpty(cd)   && cd.IndexOf(noDrainKey,   StringComparison.OrdinalIgnoreCase) >= 0)
                                         || (!string.IsNullOrEmpty(name) && name.IndexOf(noDrainKey, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (excluded) continue;
                            var groupKey = GetConveyorGroupKey(tb.CubeGrid);
                            List<InventorySnapshot> snaps;
                            if (!groupSnaps.TryGetValue(groupKey, out snaps)) continue; // no managed containers in this group — skip
                            var outInv = tb.GetInventory(1);
                            if (outInv == null) continue;
                            var items = new List<VRage.Game.ModAPI.Ingame.MyInventoryItem>();
                            outInv.GetItems(items);
                            foreach (var it in items)
                            {
                                try
                                {
                                    var def = GetItemDefinitionId(it);
                                    snaps.Add(new InventorySnapshot { OwnerId = tb.EntityId, ItemDefinitionId = def, Amount = (float)it.Amount, GridId = tb.CubeGrid?.EntityId ?? 0L, ContainerName = drainContainerName, ContainerCustomData = null });
                                }
                                catch { }
                            }
                            // No sentinel for empty drain sources — production blocks must never be chosen as destinations.
                        }
                        catch { }
                    }
                }

                // Enqueue one independent sort pass per conveyor-connected group
                foreach (var gkv in groupSnaps)
                {
                    if (gkv.Value.Count > 0)
                        _planner.EnqueueForcedSort(gkv.Value.ToArray());
                }
                _totalSortPasses++;
#endif
            }
            catch { }
            return count;
        }

        // Returns a human-readable list of managed containers for the !iml list command.
        // Must be called from the game thread.
        public List<string> GetManagedContainerInfo()
        {
            var result = new List<string>();
            try
            {
#if TORCH
                foreach (var tb in GetAllTerminalBlocks())
                {
                    try
                    {
                        string name = null; string cd = null;
                        try { name = tb.CustomName; } catch { }
                        try { cd = tb.CustomData; } catch { }
                        var tag = ContainerMatcher.ParseContainerTag(name, cd, _config.ContainerTagPrefix);
                        if (tag.Categories != null && tag.Categories.Length > 0)
                        {
                            var gridId = tb.CubeGrid != null ? tb.CubeGrid.EntityId.ToString() : "(none)";
                            var cats = string.Join(",", tag.Categories);
                            var group = string.IsNullOrEmpty(tag.Group) ? "" : $" group='{tag.Group}'";
                            result.Add($"[{tb.EntityId}] '{name}' grid={gridId} cats=[{cats}]{group}");
                        }
                    }
                    catch { }
                }
#endif
            }
            catch { }
            return result;
        }

        // Returns a per-category summary (container count + total items) for the !iml status command.
        // Must be called from the game thread.
        public List<string> GetStatusSummary()
        {
            var result = new List<string>();
            try
            {
#if TORCH
                var catContainers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var catItems     = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var tb in GetAllTerminalBlocks())
                {
                    try
                    {
                        string name = null; string cd = null;
                        try { name = tb.CustomName; } catch { }
                        try { cd = tb.CustomData; } catch { }
                        var tag = ContainerMatcher.ParseContainerTag(name, cd, _config.ContainerTagPrefix);
                        if (tag.Categories == null || tag.Categories.Length == 0) continue;

                        int containerItems = 0;
                        for (int i = 0; i < tb.InventoryCount; i++)
                        {
                            try
                            {
                                var inv = tb.GetInventory(i);
                                if (inv == null) continue;
                                var items = new List<VRage.Game.ModAPI.Ingame.MyInventoryItem>();
                                inv.GetItems(items);
                                foreach (var it in items)
                                    containerItems += it.Amount.ToIntSafe();
                            }
                            catch { }
                        }

                        foreach (var cat in tag.Categories)
                        {
                            int prev;
                            catContainers.TryGetValue(cat, out prev);
                            catContainers[cat] = prev + 1;
                            catItems.TryGetValue(cat, out prev);
                            catItems[cat] = prev + containerItems;
                        }
                    }
                    catch { }
                }

                result.Add($"Passes:{_totalSortPasses:N0}  Ops:{_applier.TotalOpsCompleted:N0}  Moved:{_applier.TotalItemsMoved:N0}");
                foreach (var cat in catContainers.Keys)
                {
                    int containers = catContainers[cat];
                    int items = 0;
                    catItems.TryGetValue(cat, out items);
                    var cLabel = containers == 1 ? "container" : "containers";
                    result.Add($"{cat,-12} {containers,2} {cLabel}, {items,9:N0} item(s)");
                }
#endif
            }
            catch { }
            return result;
        }

        public void Dispose()
        {
            _planner.Dispose();
        }
    }
}

using System;
using System.Collections.Concurrent;
#if !NETSTANDARD2_0
using System.Collections.Generic;
#else
using System.Collections.Generic;
#endif
using System.Diagnostics;
using System.Linq;
#if TORCH
using Sandbox.ModAPI;
using VRage.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Ingame;
using VRageMath;
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
        private int _lastAutoSortTick;
        private bool _lowStockDetected;
        private int _totalSortPasses;
        private readonly AssemblerManager _assemblerManager;
        private int _queueApplyGrace; // ticks remaining before AddQueueItem calls may fire
        // Sentinel category used to mark production block output snapshots as drain-only sources.
        // Not in CategoryMappings so ItemMatchesCategory always returns false — every item is misplaced.
        private const string DrainSentinelCategory = "__DRAIN__";
        private CategoryResolver _categoryResolver;

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
            _categoryResolver = resolver;
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
            _assemblerManager = new AssemblerManager(_config, _logger);
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
            _categoryResolver = resolver;
            _planner = new Planner(_snapshotQueue, _batchQueue, _config, _logger, resolver);
            _applier = new ThrottledApplier(_batchQueue, _config, adapter ?? new DefaultInventoryAdapter(), _logger);
            _assemblerManager = new AssemblerManager(_config, _logger);
        }

        public void EnqueueReplan(ReplanRequest req)
        {
            _planner.EnqueueReplanRequest(req);
        }

        public Snapshotter Snapshotter => _snapshotter;

        // Rebuild the CategoryResolver from current config (call after !iml reload).
        public void RebuildCategoryResolver()
        {
            _categoryResolver?.Rebuild(_config);
        }

#if TORCH
        // Scans all game definitions and returns a sorted list of TypeId/SubtypeId strings that
        // do not match any built-in or custom category. Used by !iml refreshdefs so admins can
        // quickly identify modded subtypes to add to <CustomCategories> in iml-config.xml.
        public List<string> GetUnknownSubtypes()
        {
            var result = new List<string>();
            try
            {
                // Physical-item type fragments — only definitions whose TypeId contains one of
                // these strings are candidate inventory items worth reporting.
                var itemTypeFragments = new[]
                {
                    "Ingot", "Ore", "Component", "AmmoMagazine", "GunObject",
                    "ContainerObject", "SeedItem", "ConsumableItem", "PhysicalItem"
                };

                var mgrType = Type.GetType("Sandbox.Definitions.MyDefinitionManager, Sandbox.Game");
                if (mgrType == null) { result.Add("IML: Could not locate MyDefinitionManager — definitions not yet loaded?"); return result; }
                var staticProp = mgrType.GetProperty("Static", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var mgr = staticProp?.GetValue(null);
                var getAll = mgrType.GetMethod("GetAllDefinitions", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var all = getAll?.Invoke(mgr, null) as System.Collections.IEnumerable;
                if (all == null) { result.Add("IML: GetAllDefinitions returned null — definitions not yet loaded?"); return result; }

                var categories = _categoryResolver?.AllCategoryNames().ToArray() ?? Array.Empty<string>();
                var unknown = new List<string>();

                foreach (var d in all)
                {
                    try
                    {
                        var idProp = d.GetType().GetProperty("Id");
                        if (!(idProp?.GetValue(d) is VRage.Game.MyDefinitionId id)) continue;
                        var idStr = id.ToString();
                        // filter to inventory-item types only
                        bool isItem = false;
                        foreach (var frag in itemTypeFragments)
                            if (idStr.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0) { isItem = true; break; }
                        if (!isItem) continue;
                        // check against all categories
                        bool matched = false;
                        foreach (var cat in categories)
                        {
                            if (_categoryResolver.ItemMatchesCategory(id, idStr, cat)) { matched = true; break; }
                        }
                        if (!matched) unknown.Add(idStr);
                    }
                    catch { }
                }

                unknown.Sort(StringComparer.OrdinalIgnoreCase);
                if (unknown.Count == 0)
                {
                    result.Add("IML: All loaded item definitions are covered by existing categories.");
                }
                else
                {
                    result.Add($"IML: {unknown.Count} item definition(s) not covered by any category:");
                    foreach (var u in unknown)
                        result.Add("  " + u);
                    result.Add("Add subtypes to <CustomCategories> in iml-config.xml and run !iml reload.");
                }
            }
            catch (Exception ex)
            {
                result.Add("IML: refreshdefs failed — " + ex.Message);
            }
            return result;
        }
#endif

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
                        var scanSw = Stopwatch.StartNew();
                        var scanBlocks = GetAllTerminalBlocks();
                        if (scanSw.ElapsedMilliseconds > _config.MaxSortMs)
                        {
                            _logger?.Warn($"IML: SortNow scan aborted — block enumeration took {scanSw.ElapsedMilliseconds}ms (budget: {_config.MaxSortMs}ms). Server under load.");
                        }
                        else
                        {
                            foreach (var tb in scanBlocks)
                            {
                                if (scanSw.ElapsedMilliseconds > _config.MaxSortMs)
                                {
                                    _logger?.Warn($"IML: SortNow scan aborted mid-loop after {scanSw.ElapsedMilliseconds}ms.");
                                    break;
                                }
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
                    }
                    catch { }
#endif
                }
            }
            catch { }

            // Periodic auto-sort — effective interval is the minimum of AutoSortIntervalTicks,
            // any per-category override in CategorySortIntervalTicks, and LowStockSortIntervalTicks
            // when a low-stock condition was detected on the last LCD pass.
            {
                int effectiveInterval = _config.AutoSortIntervalTicks > 0 ? _config.AutoSortIntervalTicks : int.MaxValue;
                if (_lowStockDetected && _config.LowStockSortIntervalTicks > 0)
                    effectiveInterval = Math.Min(effectiveInterval, _config.LowStockSortIntervalTicks);
                foreach (var kv in _config.CategorySortIntervalTicks)
                    if (kv.Value > 0) effectiveInterval = Math.Min(effectiveInterval, kv.Value);
                if (effectiveInterval < int.MaxValue && (_tickCounter - _lastAutoSortTick) >= effectiveInterval)
                {
                    try { TriggerSortAll(); } catch { }
                    _lastAutoSortTick = _tickCounter;
                    _lowStockDetected = false;
                }
            }

            // Assembler auto-queue scan — runs every AssemblerScanIntervalTicks game ticks.
            if (_config.AssemblerScanIntervalTicks > 0 && (_tickCounter % Math.Max(1, _config.AssemblerScanIntervalTicks)) == 0)
            {
#if TORCH
                try
                {
                    var asmSw = Stopwatch.StartNew();
                    var asmBlocks = GetAllTerminalBlocks();
                    if (asmSw.ElapsedMilliseconds > _config.MaxSortMs)
                        _logger?.Warn($"IML: AssemblerScan skipped — block enumeration took {asmSw.ElapsedMilliseconds}ms (budget: {_config.MaxSortMs}ms).");
                    else
                    {
                        _assemblerManager.ScanAndQueue(asmBlocks);
                        _queueApplyGrace = _config.QueueApplyDelayTicks;
                    }
                }
                catch { }
#endif
            }

            // LCD panel refresh — runs every LcdUpdateIntervalTicks game ticks.
            if (_config.LcdUpdateIntervalTicks > 0 && (_tickCounter % Math.Max(1, _config.LcdUpdateIntervalTicks)) == 0)
            {
#if TORCH
                try { UpdateLcdPanels(); } catch { }
#endif
            }

            _applier.Tick();
#if TORCH
            if (_queueApplyGrace > 0)
                _queueApplyGrace--;
            else
                _assemblerManager?.ApplyOnePendingAddition();
#endif
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
        internal static long GetConveyorGroupKey(VRage.Game.ModAPI.IMyCubeGrid grid)
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
            // Per-conveyor-group aggregates so each LCD only shows its own cluster's inventory.
            // Two unconnected grids (even from the same player) are kept completely separate.
            var groupCatContainers    = new Dictionary<long, Dictionary<string, int>>();
            var groupCatItems         = new Dictionary<long, Dictionary<string, int>>();
            var groupCatSubtypeTotals = new Dictionary<long, Dictionary<string, Dictionary<string, int>>>();
            var lcdPanels             = new List<(long entityId, string filter, long groupKey)>();

            foreach (var tb in GetAllTerminalBlocks())
            {
                try
                {
                    string name = null; string cd = null;
                    try { name = tb.CustomName; } catch { }
                    try { cd = tb.CustomData; } catch { }

                    // Collect LCD panels tagged for IML display — text panels are never managed containers
                    if (tb is Sandbox.ModAPI.IMyTextPanel)
                    {
                        var filter = ParseLcdTag(name, cd);
                        if (filter != null)
                            lcdPanels.Add((tb.EntityId, filter, GetConveyorGroupKey(tb.CubeGrid)));
                        continue;
                    }

                    // Accumulate category item totals from managed containers
                    var tag = ContainerMatcher.ParseContainerTag(name, cd, _config.ContainerTagPrefix);
                    if (tag.Categories != null && tag.Categories.Length > 0)
                    {
                        var groupKey = GetConveyorGroupKey(tb.CubeGrid);
                        int containerItems = 0;
                        var containerSubtypes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
                                    int amt = it.Amount.ToIntSafe();
                                    containerItems += amt;
                                    // Store "TypeId/SubtypeId" as key so it doubles as the LCD sprite name.
                                    var sub = it.Type.TypeId + "/" + it.Type.SubtypeId;
                                    int prevSub; containerSubtypes.TryGetValue(sub, out prevSub);
                                    containerSubtypes[sub] = prevSub + amt;
                                }
                            }
                            catch { }
                        }

                        // Ensure per-group dicts exist for this group key
                        Dictionary<string, int> catContainers, catItems;
                        Dictionary<string, Dictionary<string, int>> catSubtypeTotals;
                        if (!groupCatContainers.TryGetValue(groupKey, out catContainers))
                        { catContainers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); groupCatContainers[groupKey] = catContainers; }
                        if (!groupCatItems.TryGetValue(groupKey, out catItems))
                        { catItems = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); groupCatItems[groupKey] = catItems; }
                        if (!groupCatSubtypeTotals.TryGetValue(groupKey, out catSubtypeTotals))
                        { catSubtypeTotals = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase); groupCatSubtypeTotals[groupKey] = catSubtypeTotals; }

                        foreach (var cat in tag.Categories)
                        {
                            int prev;
                            catContainers.TryGetValue(cat, out prev); catContainers[cat] = prev + 1;
                            catItems.TryGetValue(cat, out prev);      catItems[cat]      = prev + containerItems;
                            Dictionary<string, int> subtypeMap;
                            if (!catSubtypeTotals.TryGetValue(cat, out subtypeMap))
                            { subtypeMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); catSubtypeTotals[cat] = subtypeMap; }
                            foreach (var kv in containerSubtypes)
                            { int ps; subtypeMap.TryGetValue(kv.Key, out ps); subtypeMap[kv.Key] = ps + kv.Value; }
                        }
                    }
                }
                catch { }
            }

            var emptyCatMap     = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var emptySubtypeMap = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var panel in lcdPanels)
            {
                try
                {
                    Dictionary<string, int> catContainers, catItems;
                    Dictionary<string, Dictionary<string, int>> catSubtypeTotals;
                    groupCatContainers.TryGetValue(panel.groupKey, out catContainers);
                    groupCatItems.TryGetValue(panel.groupKey, out catItems);
                    groupCatSubtypeTotals.TryGetValue(panel.groupKey, out catSubtypeTotals);
                    bool isAlert;
                    var rows = BuildLcdContent(
                        catContainers    ?? emptyCatMap,
                        catItems         ?? emptyCatMap,
                        catSubtypeTotals ?? emptySubtypeMap,
                        panel.filter, out isAlert);
                    LcdManager.Instance.EnqueueUpdate(panel.entityId, rows, isAlert);
                }
                catch { }
            }

            // Log a server-side warning for every category/subtype currently below its threshold (per group).
            foreach (var groupKey in groupCatItems.Keys)
            {
                var groupItems = groupCatItems[groupKey];
                Dictionary<string, Dictionary<string, int>> groupSubtypes;
                groupCatSubtypeTotals.TryGetValue(groupKey, out groupSubtypes);

                foreach (var cat in groupItems.Keys)
                {
                    int total; groupItems.TryGetValue(cat, out total);
                    int threshold;
                    if (_config.MinStockThresholds.TryGetValue(cat, out threshold) && total < threshold)
                    {
                        _logger?.Warn($"IML: Low stock — {cat}: {total:N0}/{threshold:N0}");
                        _lowStockDetected = true;
                    }
                }

                if (groupSubtypes != null)
                {
                    foreach (var kv in _config.MinStockThresholds)
                    {
                        if (groupItems.ContainsKey(kv.Key)) continue; // already handled as category
                        int total = GetSubtypeTotal(groupSubtypes, kv.Key);
                        if (total < kv.Value)
                        {
                            _logger?.Warn($"IML: Low stock — {kv.Key}: {total:N0}/{kv.Value:N0}");
                            _lowStockDetected = true;
                        }
                    }
                }
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

        private LcdSpriteRow[] BuildLcdContent(Dictionary<string, int> catContainers, Dictionary<string, int> catItems, Dictionary<string, Dictionary<string, int>> catSubtypeTotals, string filter, out bool isAlert)
        {
            isAlert = false;
            var rows  = new List<LcdSpriteRow>();
            var cyan  = new Color(0, 200, 255);
            var green = new Color(0, 180, 60);
            var amber = new Color(255, 140, 0);
            var white = Color.White;

            if (string.Equals(filter, "SUMMARY", StringComparison.OrdinalIgnoreCase))
            {
                // SUMMARY: categories sorted by worst deficit first, then no-threshold categories by item count.
                rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Header,    Text = "[IML Summary]", TextColor = cyan });
                rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Separator });
                var withThreshold    = new List<string>();
                var withoutThreshold = new List<string>();
                foreach (var cat in catContainers.Keys)
                {
                    if (_config.MinStockThresholds.ContainsKey(cat)) withThreshold.Add(cat);
                    else                                               withoutThreshold.Add(cat);
                }
                withThreshold.Sort((a, b) =>
                {
                    int ta, tb2;
                    _config.MinStockThresholds.TryGetValue(a, out ta);
                    _config.MinStockThresholds.TryGetValue(b, out tb2);
                    int ia = 0; catItems.TryGetValue(a, out ia);
                    int ib = 0; catItems.TryGetValue(b, out ib);
                    double ra = ta > 0 ? (double)ia / ta : 1.0;
                    double rb = tb2 > 0 ? (double)ib / tb2 : 1.0;
                    return ra.CompareTo(rb); // ascending: worst (lowest ratio) first
                });
                withoutThreshold.Sort((a, b) =>
                {
                    int ia = 0; catItems.TryGetValue(a, out ia);
                    int ib = 0; catItems.TryGetValue(b, out ib);
                    return ib.CompareTo(ia); // descending: highest count first
                });
                foreach (var cat in withThreshold)
                {
                    int total = 0; catItems.TryGetValue(cat, out total);
                    int threshold; _config.MinStockThresholds.TryGetValue(cat, out threshold);
                    bool isLow = total < threshold;
                    if (isLow) isAlert = true;
                    float fill = threshold > 0 ? (float)Math.Min(1.0, (double)total / threshold) : 1f;
                    int pct = threshold > 0 ? (int)Math.Round((double)total / threshold * 100) : 100;
                    rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.ItemBar, Text = cat, StatText = $"{pct}%  {total:N0}/{threshold:N0}", TextColor = white, ShowAlert = isLow, BarFill = fill, BarFillColor = isLow ? amber : green });
                }
                foreach (var cat in withoutThreshold)
                {
                    int total = 0; catItems.TryGetValue(cat, out total);
                    rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Item, Text = $"{cat}  {total:N0}", TextColor = white });
                }
                foreach (var kv in _config.MinStockThresholds)
                {
                    if (catContainers.ContainsKey(kv.Key)) continue; // already rendered as a category
                    int total = GetSubtypeTotal(catSubtypeTotals, kv.Key);
                    int threshold = kv.Value;
                    bool isLow = total < threshold;
                    if (isLow) isAlert = true;
                    float fill = threshold > 0 ? (float)Math.Min(1.0, (double)total / threshold) : 1f;
                    int pct = threshold > 0 ? (int)Math.Round((double)total / threshold * 100) : 100;
                    rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.ItemBar, Text = kv.Key, StatText = $"{pct}%  {total:N0}/{threshold:N0}", TextColor = white, ShowAlert = isLow, BarFill = fill, BarFillColor = isLow ? amber : green });
                }
                rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Separator });
                rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Footer,   Text = $"Moved:{_applier.TotalItemsMoved:N0} Ops:{_applier.TotalOpsCompleted:N0}" });
            }
            else if (string.IsNullOrEmpty(filter))
            {
                rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Header,    Text = "[IML Status]", TextColor = cyan });
                rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Separator });
                foreach (var cat in catContainers.Keys)
                {
                    int ctns  = catContainers[cat];
                    int total = 0; catItems.TryGetValue(cat, out total);
                    int threshold;
                    bool isLow = _config.MinStockThresholds.TryGetValue(cat, out threshold) && total < threshold;
                    if (isLow) isAlert = true;
                    string boxes = ctns == 1 ? "1 box" : $"{ctns} boxes";
                    if (threshold > 0)
                    {
                        float fill = (float)Math.Min(1.0, (double)total / threshold);
                        int pct = (int)Math.Round((double)total / threshold * 100);
                        rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.ItemBar, Text = $"{cat} ({boxes})", StatText = $"{pct}%  {total:N0}/{threshold:N0}", TextColor = white, ShowAlert = isLow, BarFill = fill, BarFillColor = isLow ? amber : green });
                    }
                    else
                    {
                        rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Item, Text = $"{cat} ({boxes})", TextColor = white });
                        rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Stat, Text = $"  {total:N0}", TextColor = white });
                    }
                }
                // Subtype thresholds don't render rows here — drill into [IML:LCD=CATEGORY] to see them.
                // Still drive isAlert so the LCD header flashes when any subtype is low.
                foreach (var kv in _config.MinStockThresholds)
                {
                    if (catContainers.ContainsKey(kv.Key)) continue;
                    if (GetSubtypeTotal(catSubtypeTotals, kv.Key) < kv.Value) isAlert = true;
                }
                rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Separator });
                rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Footer,   Text = $"Moved:{_applier.TotalItemsMoved:N0} Ops:{_applier.TotalOpsCompleted:N0}" });
            }
            else
            {
                // DETAIL: per-subtype items with icons, category bar + threshold summary.
                rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Header,    Text = $"[IML: {filter}]", TextColor = cyan });
                rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Separator });
                Dictionary<string, int> subtypeMap;
                catSubtypeTotals.TryGetValue(filter, out subtypeMap);
                if (subtypeMap != null && subtypeMap.Count > 0)
                {
                    var sorted = new List<KeyValuePair<string, int>>(subtypeMap);
                    sorted.Sort((a, b) => b.Value.CompareTo(a.Value));

                    // Count how many entries share the same SubtypeId so we can disambiguate
                    // e.g. MyObjectBuilder_Ingot/Nickel vs MyObjectBuilder_Ore/Nickel both → "Nickel"
                    var subtypeCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in sorted)
                    {
                        int slash = kv.Key.IndexOf('/');
                        var sn = slash >= 0 ? kv.Key.Substring(slash + 1) : kv.Key;
                        int c; subtypeCount.TryGetValue(sn, out c); subtypeCount[sn] = c + 1;
                    }

                    foreach (var kv in sorted)
                    {
                        // kv.Key is "MyObjectBuilder_Ingot/Iron" — strip TypeId prefix for the display name
                        int slash = kv.Key.IndexOf('/');
                        var subtype     = slash >= 0 ? kv.Key.Substring(slash + 1) : kv.Key;
                        int c; subtypeCount.TryGetValue(subtype, out c);
                        string displayName;
                        if (c > 1)
                        {
                            // Two different TypeIds share this SubtypeId — add a short qualifier
                            var typeId = slash >= 0 ? kv.Key.Substring(0, slash) : "";
                            var prefix = typeId.StartsWith("MyObjectBuilder_") ? typeId.Substring("MyObjectBuilder_".Length) : typeId;
                            displayName = $"{subtype} [{prefix}]";
                        }
                        else
                        {
                            displayName = subtype;
                        }
                        int subtypeThreshold = 0;
                        foreach (var thr in _config.MinStockThresholds)
                            if (!catContainers.ContainsKey(thr.Key) && ThresholdKeyMatches(thr.Key, kv.Key))
                            { subtypeThreshold = thr.Value; break; }
                        bool subtypeLow = subtypeThreshold > 0 && kv.Value < subtypeThreshold;
                        if (subtypeLow) isAlert = true;
                        if (subtypeThreshold > 0)
                        {
                            float subFill = (float)Math.Min(1.0, (double)kv.Value / subtypeThreshold);
                            int subPct = (int)Math.Round((double)kv.Value / subtypeThreshold * 100);
                            rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.ItemBar, IconSprite = kv.Key, Text = displayName, StatText = $"{subPct}%  {kv.Value:N0}/{subtypeThreshold:N0}", TextColor = white, ShowAlert = subtypeLow, BarFill = subFill, BarFillColor = subtypeLow ? amber : green });
                        }
                        else
                        {
                            rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Item, IconSprite = kv.Key, Text = $"{displayName}  {kv.Value:N0}", TextColor = white });
                        }
                    }
                    int total = 0; foreach (var kv in subtypeMap) total += kv.Value;
                    int threshold;
                    bool isLow = _config.MinStockThresholds.TryGetValue(filter, out threshold) && total < threshold;
                    if (isLow) isAlert = true;
                    rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Separator });
                    if (threshold > 0)
                    {
                        float fill = (float)Math.Min(1.0, (double)total / threshold);
                        int pct = (int)Math.Round((double)total / threshold * 100);
                        rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.ItemBar, Text = "Total", StatText = $"{pct}%  {total:N0}/{threshold:N0}", TextColor = white, ShowAlert = isLow, BarFill = fill, BarFillColor = isLow ? amber : green });
                    }
                    else
                    {
                        rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Item, Text = $"Total: {total:N0}", TextColor = white });
                    }
                }
                else
                {
                    int ctns = 0; catContainers.TryGetValue(filter, out ctns);
                    rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Stat, Text = $" {ctns} container(s)  (empty)", TextColor = white });
                }
                rows.Add(new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Footer, Text = $"Moved:{_applier.TotalItemsMoved:N0} Ops:{_applier.TotalOpsCompleted:N0}" });
            }
            return rows.ToArray();
        }

        // Returns the total item count for a given SubtypeId summed across all categories.
        // catSubtypeTotals keys use "TypeId/SubtypeId" format; subtypeKey is just the SubtypeId portion.
        private static int GetSubtypeTotal(Dictionary<string, Dictionary<string, int>> catSubtypeTotals, string subtypeKey)
        {
            int total = 0;
            foreach (var catMap in catSubtypeTotals.Values)
                foreach (var kv in catMap)
                    if (ThresholdKeyMatches(subtypeKey, kv.Key))
                        total += kv.Value;
            return total;
        }

        // Returns true if a config threshold key matches a full "TypeId/SubtypeId" item key.
        // configKey may be:
        //   "Platinum"       — matches any TypeId that has SubtypeId Platinum
        //   "Ingot/Platinum" — matches only MyObjectBuilder_Ingot/Platinum
        private static bool ThresholdKeyMatches(string configKey, string fullItemKey)
        {
            int itemSlash = fullItemKey.IndexOf('/');
            var itemSubtype = itemSlash >= 0 ? fullItemKey.Substring(itemSlash + 1) : fullItemKey;
            int cfgSlash = configKey.IndexOf('/');
            if (cfgSlash >= 0)
            {
                var cfgType    = configKey.Substring(0, cfgSlash);
                var cfgSubtype = configKey.Substring(cfgSlash + 1);
                var itemTypeRaw   = itemSlash >= 0 ? fullItemKey.Substring(0, itemSlash) : "";
                var itemTypeShort = itemTypeRaw.StartsWith("MyObjectBuilder_", StringComparison.OrdinalIgnoreCase)
                    ? itemTypeRaw.Substring("MyObjectBuilder_".Length) : itemTypeRaw;
                return string.Equals(cfgType, itemTypeShort, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(cfgSubtype, itemSubtype, StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(configKey, itemSubtype, StringComparison.OrdinalIgnoreCase);
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
                var sw = Stopwatch.StartNew();
                var allBlocks = GetAllTerminalBlocks();
                if (sw.ElapsedMilliseconds > _config.MaxSortMs)
                {
                    _logger?.Warn($"IML: SortAll aborted \u2014 block enumeration took {sw.ElapsedMilliseconds}ms (budget: {_config.MaxSortMs}ms). Server under load. Increase MaxSortMs in config if needed.");
                    return -1;
                }
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
                        if (tb is Sandbox.ModAPI.IMyTextPanel) continue; // text panels have no inventory
                        var tag = ContainerMatcher.ParseContainerTag(name, cd, _config.ContainerTagPrefix);
                        if (tag.Categories == null || tag.Categories.Length == 0) continue;
                        if (tag.IsLocked) continue; // IML:LOCKED — skip as both source and destination
                        count++;
                        var groupKey = GetConveyorGroupKey(tb.CubeGrid);
                        List<InventorySnapshot> snaps;
                        if (!groupSnaps.TryGetValue(groupKey, out snaps)) { snaps = new List<InventorySnapshot>(); groupSnaps[groupKey] = snaps; }
                        bool hasItems = false;
                        float volFraction = 0f;
                        try
                        {
                            var inv0 = tb.GetInventory(0);
                            if (inv0 != null)
                            {
                                float maxVol = (float)inv0.MaxVolume;
                                if (maxVol > 0f) volFraction = (float)inv0.CurrentVolume / maxVol;
                            }
                        }
                        catch { }
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
                                        snaps.Add(new InventorySnapshot { OwnerId = tb.EntityId, ItemDefinitionId = def, Amount = (float)it.Amount, GridId = tb.CubeGrid?.EntityId ?? 0L, ContainerName = name, ContainerCustomData = cd, CurrentVolumeFraction = volFraction });
                                        hasItems = true;
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                        // sentinel entry for empty containers so Planner registers them as destinations
                        if (!hasItems)
                            snaps.Add(new InventorySnapshot { OwnerId = tb.EntityId, ItemDefinitionId = default, Amount = 0, GridId = tb.CubeGrid?.EntityId ?? 0L, ContainerName = name, ContainerCustomData = cd, CurrentVolumeFraction = volFraction });
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

        // Immediately runs the assembler auto-queue scan and returns per-assembler diagnostic lines.
        // Must be called from the game thread.
        public List<string> TriggerAssemblerScan()
        {
            var result = new List<string>();
            try
            {
#if TORCH
                var sw = Stopwatch.StartNew();
                var blocks = GetAllTerminalBlocks();
                if (sw.ElapsedMilliseconds > _config.MaxSortMs)
                {
                    result.Add($"WARNING: block enumeration took {sw.ElapsedMilliseconds}ms (budget: {_config.MaxSortMs}ms) — scan aborted to protect frame time.");
                    return result;
                }
                var lines = _assemblerManager.ScanAndQueue(blocks);
                if (lines != null) result.AddRange(lines);
                _queueApplyGrace = _config.QueueApplyDelayTicks;
#endif
            }
            catch (Exception ex)
            {
                result.Add("ERROR: " + ex.Message);
            }
            return result;
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
                var catItems      = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var catLocked     = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var allSubtypes   = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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
                        if (!tag.IsLocked)
                        {
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
                                        int amt = it.Amount.ToIntSafe();
                                        containerItems += amt;
                                        var sub = it.Type.TypeId + "/" + it.Type.SubtypeId;
                                        int prevSub; allSubtypes.TryGetValue(sub, out prevSub);
                                        allSubtypes[sub] = prevSub + amt;
                                    }
                                }
                                catch { }
                            }
                        }

                        foreach (var cat in tag.Categories)
                        {
                            int prev;
                            catContainers.TryGetValue(cat, out prev);
                            catContainers[cat] = prev + 1;
                            catItems.TryGetValue(cat, out prev);
                            catItems[cat] = prev + containerItems;
                            if (tag.IsLocked)
                            {
                                catLocked.TryGetValue(cat, out prev);
                                catLocked[cat] = prev + 1;
                            }
                        }
                    }
                    catch { }
                }

                result.Add($"Passes:{_totalSortPasses:N0}  Ops:{_applier.TotalOpsCompleted:N0}  Moved:{_applier.TotalItemsMoved:N0}");
                int lowCount = 0;
                foreach (var cat in catContainers.Keys)
                {
                    int containers = catContainers[cat];
                    int items = 0;
                    catItems.TryGetValue(cat, out items);
                    int locked = 0;
                    catLocked.TryGetValue(cat, out locked);
                    var cLabel = containers == 1 ? "container" : "containers";
                    int threshold;
                    bool isLow = _config.MinStockThresholds.TryGetValue(cat, out threshold) && items < threshold;
                    if (isLow) lowCount++;
                    var lowNote    = isLow   ? $"  [LOW: {items:N0}/{threshold:N0}]" : "";
                    var lockedNote = locked > 0 ? $"  [{locked} LOCKED]" : "";
                    result.Add($"{cat,-12} {containers,2} {cLabel}, {items,9:N0} item(s){lowNote}{lockedNote}");
                }
                if (lowCount > 0)
                    result.Add($"⚠ {lowCount} category/categories below minimum stock threshold.");

                int subtypeLowCount = 0;
                foreach (var kv in _config.MinStockThresholds)
                {
                    if (catContainers.ContainsKey(kv.Key)) continue; // already reported as category
                    int total = 0;
                    foreach (var sub in allSubtypes)
                        if (ThresholdKeyMatches(kv.Key, sub.Key))
                            total += sub.Value;
                    bool isLow = total < kv.Value;
                    if (isLow) subtypeLowCount++;
                    var lowNote = isLow ? $"  [LOW: {total:N0}/{kv.Value:N0}]" : $"  {total:N0}/{kv.Value:N0}";
                    result.Add($"{kv.Key,-12}   subtype threshold{lowNote}");
                }
                if (subtypeLowCount > 0)
                    result.Add($"⚠ {subtypeLowCount} subtype(s) below minimum stock threshold.");
#endif
            }
            catch { }
            return result;
        }

        // ── Bulk tagging helpers ─────────────────────────────────────────────────
#if TORCH
        // Resolve grids by numeric entity ID or case-insensitive name substring.
        // Multiple grids may match on name; a single grid is always returned for an exact ID.
        private static List<VRage.Game.ModAPI.IMyCubeGrid> FindGrids(string gridIdOrName)
        {
            var results = new List<VRage.Game.ModAPI.IMyCubeGrid>();
            try
            {
                var entities = new HashSet<VRage.ModAPI.IMyEntity>();
                MyAPIGateway.Entities.GetEntities(entities, e => e is VRage.Game.ModAPI.IMyCubeGrid);
                long entityId;
                bool isId = long.TryParse(gridIdOrName, out entityId);
                foreach (var ent in entities)
                {
                    var grid = ent as VRage.Game.ModAPI.IMyCubeGrid;
                    if (grid == null) continue;
                    if (isId)
                    {
                        if (grid.EntityId == entityId) { results.Add(grid); break; }
                    }
                    else if (grid.DisplayName != null &&
                             grid.DisplayName.IndexOf(gridIdOrName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        results.Add(grid);
                    }
                }
            }
            catch { }
            return results;
        }

        // Returns true if the block type is a production machine (assembler, refinery, gas generator)
        // or an LCD panel — blocks that should never be bulk-tagged as item storage.
        private static bool IsProductionOrDisplayBlock(Sandbox.ModAPI.IMyTerminalBlock tb)
        {
            if (tb is Sandbox.ModAPI.IMyTextPanel) return true;
            var t = tb.GetType().Name;
            return t.IndexOf("Assembler",   StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("Refinery",    StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("GasGenerator",StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Adds <tag> to CustomData of every eligible inventory block on the matching grid(s).
        // Skips: locked blocks, production/display blocks, blocks already carrying the tag.
        // Returns human-readable result lines for the command response.
        public List<string> BulkTagGrid(string gridIdOrName, string tag)
        {
            var result = new List<string>();
            try
            {
                var grids = FindGrids(gridIdOrName);
                if (grids.Count == 0)
                {
                    result.Add($"IML: No grid found matching '{gridIdOrName}'. Use '!iml list' to see entity IDs.");
                    return result;
                }

                // Ensure the tag carries the configured prefix (accept bare category name too).
                var prefix = _config.ContainerTagPrefix ?? "IML:";
                var fullTag = tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? tag : prefix + tag;

                int tagged = 0, alreadyTagged = 0, skipped = 0;
                foreach (var grid in grids)
                {
                    var slims = new List<VRage.Game.ModAPI.IMySlimBlock>();
                    grid.GetBlocks(slims, b => b?.FatBlock is Sandbox.ModAPI.IMyTerminalBlock);
                    foreach (var slim in slims)
                    {
                        try
                        {
                            var tb = slim.FatBlock as Sandbox.ModAPI.IMyTerminalBlock;
                            if (tb == null || tb.InventoryCount == 0) continue;
                            if (IsProductionOrDisplayBlock(tb)) { skipped++; continue; }
                            var cd   = tb.CustomData ?? string.Empty;
                            var name = tb.CustomName  ?? string.Empty;
                            // Skip locked containers
                            if (cd.IndexOf(prefix + "LOCKED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf(prefix + "LOCKED", StringComparison.OrdinalIgnoreCase) >= 0)
                            { skipped++; continue; }
                            // Skip if tag already present
                            if (cd.IndexOf(fullTag, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf(fullTag, StringComparison.OrdinalIgnoreCase) >= 0)
                            { alreadyTagged++; continue; }
                            tb.CustomData = string.IsNullOrWhiteSpace(cd) ? fullTag : cd.TrimEnd() + "\n" + fullTag;
                            tagged++;
                        }
                        catch { }
                    }
                }

                var gridLabel = grids.Count == 1 ? $"'{grids[0].DisplayName}'" : $"{grids.Count} grids";
                result.Add($"IML: tagall on {gridLabel} with tag '{fullTag}':");
                result.Add($"  Tagged: {tagged}  Already tagged: {alreadyTagged}  Skipped: {skipped}");
            }
            catch (Exception ex) { result.Add("IML: tagall failed — " + ex.Message); }
            return result;
        }

        // Removes all IML:* lines from CustomData on every eligible block on the matching grid(s).
        // Leaves block names untouched; skips locked containers.
        public List<string> ClearTagsOnGrid(string gridIdOrName)
        {
            var result = new List<string>();
            try
            {
                var grids = FindGrids(gridIdOrName);
                if (grids.Count == 0)
                {
                    result.Add($"IML: No grid found matching '{gridIdOrName}'. Use '!iml list' to see entity IDs.");
                    return result;
                }

                var prefix = _config.ContainerTagPrefix ?? "IML:";
                int cleared = 0, skipped = 0;
                foreach (var grid in grids)
                {
                    var slims = new List<VRage.Game.ModAPI.IMySlimBlock>();
                    grid.GetBlocks(slims, b => b?.FatBlock is Sandbox.ModAPI.IMyTerminalBlock);
                    foreach (var slim in slims)
                    {
                        try
                        {
                            var tb = slim.FatBlock as Sandbox.ModAPI.IMyTerminalBlock;
                            if (tb == null) continue;
                            var cd   = tb.CustomData ?? string.Empty;
                            var name = tb.CustomName  ?? string.Empty;
                            if (cd.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            // Skip locked containers
                            if (cd.IndexOf(prefix + "LOCKED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf(prefix + "LOCKED", StringComparison.OrdinalIgnoreCase) >= 0)
                            { skipped++; continue; }
                            // Remove every line that starts with the IML prefix
                            var lines = cd.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                            var kept  = System.Linq.Enumerable.ToArray(
                                System.Linq.Enumerable.Where(lines,
                                    l => l.Trim().IndexOf(prefix, StringComparison.OrdinalIgnoreCase) != 0));
                            var newCd = string.Join("\n", kept);
                            if (newCd != cd) { tb.CustomData = newCd; cleared++; }
                        }
                        catch { }
                    }
                }

                var gridLabel = grids.Count == 1 ? $"'{grids[0].DisplayName}'" : $"{grids.Count} grids";
                result.Add($"IML: cleartags on {gridLabel}: {cleared} block(s) cleared, {skipped} locked block(s) skipped.");
            }
            catch (Exception ex) { result.Add("IML: cleartags failed — " + ex.Message); }
            return result;
        }

        // Exports all current IML tags (CustomData and block names) across every grid to a text file.
        // Returns summary lines for the command response.
        public List<string> BackupTags(string outputPath)
        {
            var result = new List<string>();
            try
            {
                var prefix = _config.ContainerTagPrefix ?? "IML:";
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"# IML tag backup — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"# Format: GridName | EntityId | BlockName | IML lines from CustomData/Name");
                sb.AppendLine();

                int blockCount = 0;
                var entities = new HashSet<VRage.ModAPI.IMyEntity>();
                MyAPIGateway.Entities.GetEntities(entities, e => e is VRage.Game.ModAPI.IMyCubeGrid);
                foreach (var ent in entities)
                {
                    var grid = ent as VRage.Game.ModAPI.IMyCubeGrid;
                    if (grid == null) continue;
                    var slims = new List<VRage.Game.ModAPI.IMySlimBlock>();
                    grid.GetBlocks(slims, b => b?.FatBlock is Sandbox.ModAPI.IMyTerminalBlock);
                    foreach (var slim in slims)
                    {
                        try
                        {
                            var tb = slim.FatBlock as Sandbox.ModAPI.IMyTerminalBlock;
                            if (tb == null) continue;
                            var cd   = tb.CustomData ?? string.Empty;
                            var name = tb.CustomName  ?? string.Empty;
                            // Collect IML lines from CustomData
                            var imlLines = new List<string>();
                            foreach (var ln in cd.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                                if (ln.Trim().IndexOf(prefix, StringComparison.OrdinalIgnoreCase) == 0)
                                    imlLines.Add("CD:" + ln.Trim());
                            // Collect IML tokens from block name
                            if (name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                                imlLines.Add("NAME:" + name);
                            if (imlLines.Count == 0) continue;
                            sb.AppendLine($"{grid.DisplayName} | {tb.EntityId} | {name} | {string.Join(" | ", imlLines)}");
                            blockCount++;
                        }
                        catch { }
                    }
                }

                System.IO.File.WriteAllText(outputPath, sb.ToString());
                result.Add($"IML: Backup written — {blockCount} tagged block(s) → {outputPath}");
            }
            catch (Exception ex) { result.Add("IML: backuptags failed — " + ex.Message); }
            return result;
        }
#endif

        // Walks every inventory slot on every block and returns a per-slot breakdown of a specific
        // item subtype. Used by !iml stockdump to diagnose phantom or unexpected stock counts.
        // Slots inside production-block input inventories (index 0) are flagged as
        // [SKIPPED by ScanAndQueue] — they are excluded from the auto-queue totals but still shown
        // here so you can see if items are hiding there.
        // Must be called from the game thread.
        public List<string> StockDump(string subtype)
        {
            var result = new List<string>();
            try
            {
#if TORCH
                var allBlocks = GetAllTerminalBlocks();

                // Determine which conveyor groups contain at least one IML-managed container.
                // Slots outside these groups are invisible to the sorter and assembler manager.
                var gcKeyCache = new Dictionary<long, long>();
                var managedGroupKeys = new HashSet<long>();
                foreach (var tb2 in allBlocks)
                {
                    try
                    {
                        string n2 = null, cd2 = null;
                        try { n2 = tb2.CustomName; } catch { }
                        try { cd2 = tb2.CustomData; } catch { }
                        var t2 = ContainerMatcher.ParseContainerTag(n2, cd2, _config.ContainerTagPrefix);
                        if (t2.Categories == null || t2.Categories.Length == 0) continue;
                        var g2 = tb2.CubeGrid as VRage.Game.ModAPI.IMyCubeGrid;
                        if (g2 == null) continue;
                        long gk;
                        if (!gcKeyCache.TryGetValue(g2.EntityId, out gk))
                        { gk = GetConveyorGroupKey(g2); gcKeyCache[g2.EntityId] = gk; }
                        managedGroupKeys.Add(gk);
                    }
                    catch { }
                }

                float grandTotal = 0f;
                float accessibleTotal = 0f;
                var lines = new List<(float amount, string line)>();
                foreach (var tb in allBlocks)
                {
                    try
                    {
                        string name = null;
                        try { name = tb.CustomName; } catch { }
                        bool isProd = tb is Sandbox.ModAPI.IMyProductionBlock;

                        long blockGk = 0;
                        var blockGrid = tb.CubeGrid as VRage.Game.ModAPI.IMyCubeGrid;
                        if (blockGrid != null)
                        {
                            if (!gcKeyCache.TryGetValue(blockGrid.EntityId, out blockGk))
                            { blockGk = GetConveyorGroupKey(blockGrid); gcKeyCache[blockGrid.EntityId] = blockGk; }
                        }
                        bool inManagedGroup = managedGroupKeys.Contains(blockGk);

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
                                    if (!string.Equals(it.Type.SubtypeId, subtype, StringComparison.OrdinalIgnoreCase)) continue;
                                    float amt = (float)it.Amount;
                                    grandTotal += amt;
                                    if (inManagedGroup) accessibleTotal += amt;
                                    string slotLabel = isProd ? (i == 0 ? "input[0]" : "output[1]") : $"inv[{i}]";
                                    string skipped = (isProd && i == 0) ? " [SKIPPED by ScanAndQueue]" : "";
                                    string groupNote = inManagedGroup ? "" : " [SEPARATE GRID]";
                                    lines.Add((amt, $"  {amt,10:N0}  '{name}' (id:{tb.EntityId}) {slotLabel}{skipped}{groupNote}"));
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
                lines.Sort((a, b) => b.amount.CompareTo(a.amount));
                string accessNote = accessibleTotal < grandTotal ? $", accessible to sorter: {accessibleTotal:N0}" : "";
                result.Add($"StockDump '{subtype}': {lines.Count} slot(s) found, grand total = {grandTotal:N0}{accessNote}");
                foreach (var entry in lines)
                    result.Add(entry.line);
                if (lines.Count == 0)
                    result.Add("  (not found in any inventory — item does not physically exist anywhere)");
#endif
            }
            catch (Exception ex)
            {
                result.Add("ERROR: " + ex.Message);
            }
            return result;
        }

        public void Dispose()
        {
            _planner.Dispose();
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VRage.Game;

namespace InventoryManagerLight
{
    // Planner runs on a background thread. It consumes snapshots and produces TransferBatch instances.
    public class Planner : IDisposable
    {
        private readonly ConcurrentQueue<ReplanRequest> _replanQueue = new ConcurrentQueue<ReplanRequest>();
        private readonly ConcurrentQueue<InventorySnapshot[]> _snapshotQueue;
        private readonly ConcurrentQueue<TransferBatch> _batchQueue;
        private readonly RuntimeConfig _config;
        private readonly CategoryResolver _categoryResolver;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _task;
        private readonly ILogger _logger;
        private readonly ConveyorScanner _conveyorScanner = null;
        private readonly InventoryDemandTracker _demandTracker = null;
        // previous aggregated snapshot (owner+item -> amount)
        private readonly Dictionary<ItemKey, int> _previous = new Dictionary<ItemKey, int>();
        // queue for forced categorization sorts (bypasses the diff algorithm)
        private readonly ConcurrentQueue<InventorySnapshot[]> _forcedSortQueue = new ConcurrentQueue<InventorySnapshot[]>();

        // metrics
        private long _plannedOpsCount;

        private struct ItemKey
        {
            public long OwnerId;
            public MyDefinitionId ItemDef;

            public ItemKey(long ownerId, MyDefinitionId itemDef)
            {
                OwnerId = ownerId;
                ItemDef = itemDef;
            }

            public override bool Equals(object obj)
            {
                if (!(obj is ItemKey)) return false;
                var o = (ItemKey)obj;
                return OwnerId == o.OwnerId && ItemDef.Equals(o.ItemDef);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (OwnerId.GetHashCode()*397) ^ ItemDef.GetHashCode();
                }
            }
        }

        public Planner(ConcurrentQueue<InventorySnapshot[]> snapshotQueue, ConcurrentQueue<TransferBatch> batchQueue, RuntimeConfig config, ILogger logger, CategoryResolver categoryResolver = null, ConveyorScanner conveyorScanner = null, InventoryDemandTracker demandTracker = null)
        {
            _snapshotQueue = snapshotQueue;
            _batchQueue = batchQueue;
            _config = config;
            _logger = logger ?? new DefaultLogger();
            _categoryResolver = categoryResolver;
            _conveyorScanner = conveyorScanner;
            _demandTracker = demandTracker;
            _task = Task.Run(Run, _cts.Token);
        }

        // Allow external enqueuing of replan requests (from applier)
        public void EnqueueReplanRequest(ReplanRequest req)
        {
            _replanQueue.Enqueue(req);
        }

        // Enqueue a full categorization sort across all managed containers (bypasses diff algorithm)
        public void EnqueueForcedSort(InventorySnapshot[] snap)
        {
            if (snap != null) _forcedSortQueue.Enqueue(snap);
        }

        private void Run()
        {
            var token = _cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    bool didWork = false;
                    InventorySnapshot[] forcedSnap;
                    if (_forcedSortQueue.TryDequeue(out forcedSnap))
                    {
                        var batch = PlanForcedCategorization(forcedSnap);
                        if (batch != null && batch.Ops.Count > 0)
                            _batchQueue.Enqueue(batch);
                        didWork = true;
                    }
                    InventorySnapshot[] snap;
                    if (_snapshotQueue.TryDequeue(out snap))
                    {
                        var batch = PlanFromSnapshot(snap);
                        if (batch != null && batch.Ops.Count > 0)
                            _batchQueue.Enqueue(batch);
                        ProcessReplanRequests();
                        didWork = true;
                    }
                    if (!didWork)
                        Thread.Sleep(_config.PlannerSleepMs);
                }
            }
            catch (OperationCanceledException) { }
        }

        private TransferBatch PlanFromSnapshot(InventorySnapshot[] snap)
        {
            // Aggregate current snapshot by owner+item
            var current = new Dictionary<ItemKey, int>();
            // remember categories per owner from snapshots
            var ownerCategories = new Dictionary<long, string[]>();
            // remember optional container-level group per owner
            var ownerGroups = new Dictionary<long, string>();
            foreach (var s in snap)
            {
                // parse container tag (categories + optional container group/subgroup)
                var tag = ContainerMatcher.ParseContainerTag(s.ContainerName, s.ContainerCustomData, _config.ContainerTagPrefix);
                if ((tag.Categories == null || tag.Categories.Length == 0)) continue; // unmanaged
                ownerCategories[s.OwnerId] = tag.Categories;
                if (!string.IsNullOrEmpty(tag.Group)) ownerGroups[s.OwnerId] = tag.Group;
                var k = new ItemKey(s.OwnerId, s.ItemDefinitionId);
                int prev;
                current.TryGetValue(k, out prev);
                current[k] = prev + s.Amount;
            }

            // Build diffs per (owner,item): diff = current - previous
            // For each itemDef, collect sources (diff < 0) and sinks (diff > 0)
            var sourcesByDef = new Dictionary<MyDefinitionId, List<KeyValuePair<long,int>>>(); // itemdef -> list of (owner, available)
            var sinksByDef = new Dictionary<MyDefinitionId, List<KeyValuePair<long,int>>>();   // itemdef -> list of (owner, need)

            // consider keys present in either previous or current
            var keys = new HashSet<ItemKey>(_previous.Keys);
            foreach (var k in current.Keys) keys.Add(k);

            foreach (var k in keys)
            {
                int prevAmt = 0;
                _previous.TryGetValue(k, out prevAmt);
                int currAmt = 0;
                current.TryGetValue(k, out currAmt);
                int diff = currAmt - prevAmt;
                if (diff == 0) continue;
                if (diff > 0)
                {
                    List<KeyValuePair<long,int>> list;
                    if (!sinksByDef.TryGetValue(k.ItemDef, out list))
                    {
                        list = new List<KeyValuePair<long,int>>();
                        sinksByDef[k.ItemDef] = list;
                    }
                    list.Add(new KeyValuePair<long,int>(k.OwnerId, diff));
                }
                else
                {
                    List<KeyValuePair<long,int>> list;
                    if (!sourcesByDef.TryGetValue(k.ItemDef, out list))
                    {
                        list = new List<KeyValuePair<long,int>>();
                        sourcesByDef[k.ItemDef] = list;
                    }
                    list.Add(new KeyValuePair<long,int>(k.OwnerId, -diff));
                }
            }

            var batch = new TransferBatch();

            // For each item def, match sources to sinks
            foreach (var kv in sinksByDef)
            {
                var itemDef = kv.Key;
                var sinksList = kv.Value;
                // get available sources for this item
                List<KeyValuePair<long,int>> srcs;
                if (!sourcesByDef.TryGetValue(itemDef, out srcs)) continue;
                // compute min conveyor distance from any source to each sink (cheap fallback if scanner missing)
                var sinkOwners = sinksList.Select(p => p.Key).ToArray();
                var minDist = new Dictionary<long,int>();
                foreach (var o in sinkOwners) minDist[o] = int.MaxValue;
                if (_conveyorScanner != null)
                {
                    foreach (var s in srcs)
                    {
                        try
                        {
                            var dmap = _conveyorScanner.GetDistances(s.Key, sinkOwners);
                            foreach (var dkv in dmap)
                            {
                                if (minDist.ContainsKey(dkv.Key)) minDist[dkv.Key] = Math.Min(minDist[dkv.Key], dkv.Value);
                            }
                        }
                        catch { }
                    }
                    // treat same container-group as distance 0 (prefer local loop) regardless of conveyor path
                    try
                    {
                        // if any source and sink share a non-empty container-level group name, prefer them
                        foreach (var src in srcs)
                        {
                            string sgroup = null; ownerGroups.TryGetValue(src.Key, out sgroup);
                            if (string.IsNullOrEmpty(sgroup)) continue;
                            foreach (var o in sinkOwners)
                            {
                                string ogroup = null; ownerGroups.TryGetValue(o, out ogroup);
                                if (!string.IsNullOrEmpty(ogroup) && string.Equals(sgroup, ogroup, StringComparison.OrdinalIgnoreCase))
                                {
                                    minDist[o] = Math.Min(minDist[o], 0);
                                }
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    // fallback: same-owner -> 0, otherwise 1 if any source exists
                    foreach (var s in srcs)
                    {
                        foreach (var o in sinkOwners)
                        {
                            int d = (s.Key == o) ? 0 : 1;
                            minDist[o] = Math.Min(minDist[o], d);
                        }
                    }
                }
                // normalize unreachable distances
                foreach (var k2 in sinkOwners)
                {
                    if (minDist[k2] == int.MaxValue) minDist[k2] = 1000;
                }

                // compute weighted score = demand*DemandWeight - distance*DistanceWeight
                if (sinksList.Count > 1)
                {
                    double dw = _config?.DemandWeight ?? 1.0;
                    double xw = _config?.DistanceWeight ?? 0.5;
                    sinksList.Sort((a, b) =>
                    {
                        double da = _demandTracker != null ? _demandTracker.GetDemandForOwner(a.Key, itemDef) : 0;
                        double db = _demandTracker != null ? _demandTracker.GetDemandForOwner(b.Key, itemDef) : 0;
                        int daDist = minDist.ContainsKey(a.Key) ? minDist[a.Key] : 1000;
                        int dbDist = minDist.ContainsKey(b.Key) ? minDist[b.Key] : 1000;
                        double sa = da * dw - daDist * xw;
                        double sb = db * dw - dbDist * xw;
                        int cmp = sb.CompareTo(sa); // descending score
                        if (cmp != 0) return cmp;
                        // tie-breaker deterministic
                        return a.Key.CompareTo(b.Key);
                    });
                }

                // We'll match sinks grouped by container-level group token so production loops stay isolated.
                // Build mutable availability map for sources
                var availBySource = new Dictionary<long,int>();
                foreach (var s in srcs) availBySource[s.Key] = s.Value;

                // Group sinks by their container-level group (if any)
                var sinksByGroup = new Dictionary<string, List<KeyValuePair<long,int>>>(StringComparer.OrdinalIgnoreCase);
                foreach (var sn in sinksList)
                {
                    string g = null; ownerGroups.TryGetValue(sn.Key, out g);
                    if (!sinksByGroup.TryGetValue(g, out var list)) { list = new List<KeyValuePair<long,int>>(); sinksByGroup[g] = list; }
                    list.Add(sn);
                }

                // For each group, match its sinks against candidate sources (prefer same-group sources)
                foreach (var gkv in sinksByGroup)
                {
                    var groupName = gkv.Key; // may be null
                    var sinksForGroup = gkv.Value;

                    // pick candidate sources: prefer sources that share the same container-level group
                    List<KeyValuePair<long,int>> candidates;
                    if (!string.IsNullOrEmpty(groupName))
                    {
                        candidates = srcs.Where(s => { string sg = null; ownerGroups.TryGetValue(s.Key, out sg); return string.Equals(sg, groupName, StringComparison.OrdinalIgnoreCase); }).ToList();
                        if (candidates.Count == 0)
                        {
                            // nothing in same group; if strict mode, skip these sinks; otherwise allow any source
                            if (_config != null && _config.RequireContainerGroupMatch) continue;
                            candidates = srcs.ToList();
                        }
                    }
                    else
                    {
                        // sinks with no group may draw from any source
                        candidates = srcs.ToList();
                    }

                    // rebuild minDist only against candidate sources
                    var localSinkOwners = sinksForGroup.Select(p => p.Key).ToArray();
                    var localMinDist = new Dictionary<long,int>();
                    foreach (var o in localSinkOwners) localMinDist[o] = int.MaxValue;
                    if (_conveyorScanner != null)
                    {
                        foreach (var s in candidates)
                        {
                            try
                            {
                                var dmap = _conveyorScanner.GetDistances(s.Key, localSinkOwners);
                                foreach (var dkv in dmap)
                                {
                                    if (localMinDist.ContainsKey(dkv.Key)) localMinDist[dkv.Key] = Math.Min(localMinDist[dkv.Key], dkv.Value);
                                }
                            }
                            catch { }
                        }
                        // treat same container-group as distance 0
                        try
                        {
                            foreach (var s in candidates)
                            {
                                string sgroup = null; ownerGroups.TryGetValue(s.Key, out sgroup);
                                if (string.IsNullOrEmpty(sgroup)) continue;
                                foreach (var o in localSinkOwners)
                                {
                                    string ogroup = null; ownerGroups.TryGetValue(o, out ogroup);
                                    if (!string.IsNullOrEmpty(ogroup) && string.Equals(sgroup, ogroup, StringComparison.OrdinalIgnoreCase))
                                    {
                                        localMinDist[o] = Math.Min(localMinDist[o], 0);
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        foreach (var s in candidates)
                        {
                            foreach (var o in localSinkOwners)
                            {
                                int d = (s.Key == o) ? 0 : 1;
                                localMinDist[o] = Math.Min(localMinDist[o], d);
                            }
                        }
                    }
                    foreach (var k2 in localSinkOwners)
                    {
                        if (localMinDist[k2] == int.MaxValue) localMinDist[k2] = 1000;
                    }

                    // scoring and sorting for sinks in this group
                    if (sinksForGroup.Count > 1)
                    {
                        double dw = _config?.DemandWeight ?? 1.0;
                        double xw = _config?.DistanceWeight ?? 0.5;
                        sinksForGroup.Sort((a, b) =>
                        {
                            double da = _demandTracker != null ? _demandTracker.GetDemandForOwner(a.Key, itemDef) : 0;
                            double db = _demandTracker != null ? _demandTracker.GetDemandForOwner(b.Key, itemDef) : 0;
                            int daDist = localMinDist.ContainsKey(a.Key) ? localMinDist[a.Key] : 1000;
                            int dbDist = localMinDist.ContainsKey(b.Key) ? localMinDist[b.Key] : 1000;
                            double sa = da * dw - daDist * xw;
                            double sb = db * dw - dbDist * xw;
                            int cmp = sb.CompareTo(sa);
                            if (cmp != 0) return cmp;
                            return a.Key.CompareTo(b.Key);
                        });
                    }

                    // perform matching between sinksForGroup and candidate sources using availBySource
                    var sinksQ = new Queue<KeyValuePair<long,int>>(sinksForGroup);
                    // build a sources queue from candidates using current availability
                    var srcQueue = new Queue<KeyValuePair<long,int>>();
                    foreach (var c in candidates)
                    {
                        if (availBySource.TryGetValue(c.Key, out var av) && av > 0) srcQueue.Enqueue(new KeyValuePair<long,int>(c.Key, av));
                    }

                    while (sinksQ.Count > 0 && srcQueue.Count > 0)
                    {
                        var sink = sinksQ.Dequeue();
                        long sinkOwner = sink.Key;
                        int need = sink.Value;
                        while (need > 0 && srcQueue.Count > 0)
                        {
                            var src = srcQueue.Dequeue();
                            long srcOwner = src.Key;
                            int avail = src.Value;
                            int move = Math.Min(avail, need);
                            if (move > 0)
                            {
                                batch.Ops.Add(new TransferOp { SourceOwner = srcOwner, DestinationOwner = sinkOwner, ItemDefinitionId = itemDef, Amount = move });
                                Interlocked.Add(ref _plannedOpsCount, 1);
                                // reduce global availability
                                availBySource[srcOwner] = availBySource[srcOwner] - move;
                            }
                            avail -= move;
                            need -= move;
                            if (avail > 0) srcQueue.Enqueue(new KeyValuePair<long,int>(srcOwner, avail));
                        }
                    }
                }
            }

            // coalesce ops by (src,dst,item)
            if (batch.Ops.Count > 1)
            {
                var map = new Dictionary<Tuple<long,long,MyDefinitionId>, int>();
                foreach (var op in batch.Ops)
                {
                    var key = Tuple.Create(op.SourceOwner, op.DestinationOwner, op.ItemDefinitionId);
                    int v;
                    if (!map.TryGetValue(key, out v)) v = 0;
                    map[key] = v + op.Amount;
                }
                var merged = new List<TransferOp>(map.Count);
                foreach (var kv2 in map)
                {
                    merged.Add(new TransferOp { SourceOwner = kv2.Key.Item1, DestinationOwner = kv2.Key.Item2, ItemDefinitionId = kv2.Key.Item3, Amount = kv2.Value });
                }
                batch.Ops = merged;
            }

            // update previous to current for next diff
            _previous.Clear();
            foreach (var c in current)
            {
                _previous[c.Key] = c.Value;
            }

            return batch;
        }

        private TransferBatch PlanForcedCategorization(InventorySnapshot[] snap)
        {
            var containerCategories = new Dictionary<long, string[]>();
            var containerGroups = new Dictionary<long, string>();
            var containerContents = new Dictionary<long, Dictionary<MyDefinitionId, int>>();
            var containerDenySubtypes = new Dictionary<long, HashSet<string>>();
            var containerAllowSubtypes = new Dictionary<long, HashSet<string>>();

            foreach (var s in snap)
            {
                var tag = ContainerMatcher.ParseContainerTag(s.ContainerName, s.ContainerCustomData, _config.ContainerTagPrefix);
                if (tag.Categories == null || tag.Categories.Length == 0) continue;

                // register as a managed destination even if empty
                containerCategories[s.OwnerId] = tag.Categories;
                if (!string.IsNullOrEmpty(tag.Group)) containerGroups[s.OwnerId] = tag.Group;
                if (tag.DenySubtypes != null && tag.DenySubtypes.Length > 0)
                    containerDenySubtypes[s.OwnerId] = new HashSet<string>(tag.DenySubtypes, StringComparer.OrdinalIgnoreCase);
                if (tag.AllowSubtypes != null && tag.AllowSubtypes.Length > 0)
                    containerAllowSubtypes[s.OwnerId] = new HashSet<string>(tag.AllowSubtypes, StringComparer.OrdinalIgnoreCase);

                if (s.Amount <= 0) continue; // sentinel entry for empty containers

                Dictionary<MyDefinitionId, int> dict;
                if (!containerContents.TryGetValue(s.OwnerId, out dict))
                {
                    dict = new Dictionary<MyDefinitionId, int>();
                    containerContents[s.OwnerId] = dict;
                }
                int prev;
                dict.TryGetValue(s.ItemDefinitionId, out prev);
                dict[s.ItemDefinitionId] = prev + s.Amount;
            }

            // Seed _previous so subsequent periodic diff-based scans have a correct baseline
            _previous.Clear();
            foreach (var cv in containerContents)
                foreach (var iv in cv.Value)
                    _previous[new ItemKey(cv.Key, iv.Key)] = iv.Value;

            var batch = new TransferBatch();
            // Items that are misplaced but have no container configured for their category.
            var unsortable = new List<string>();

            // For each managed container, move any item that doesn't belong to the correct container
            foreach (var cv in containerContents)
            {
                long srcId = cv.Key;
                string[] srcCats;
                if (!containerCategories.TryGetValue(srcId, out srcCats)) continue;
                string srcGroup = null;
                containerGroups.TryGetValue(srcId, out srcGroup);

                foreach (var iv in cv.Value)
                {
                    var itemDef = iv.Key;
                    int amount = iv.Value;
                    if (amount <= 0) continue;
                    var itemStr = itemDef.ToString();

                    // Does this item belong in this container?
                    bool belongs = false;
                    foreach (var cat in srcCats)
                    {
                        if (_categoryResolver != null && _categoryResolver.ItemMatchesCategory(itemDef, itemStr, cat))
                        { belongs = true; break; }
                    }
                    if (belongs)
                    {
                        int si = itemStr.IndexOf('/');
                        string subtype = si >= 0 ? itemStr.Substring(si + 1) : itemStr;
                        HashSet<string> denySet;
                        if (containerDenySubtypes.TryGetValue(srcId, out denySet) && denySet.Contains(subtype))
                            belongs = false;
                        HashSet<string> allowSet;
                        if (belongs && containerAllowSubtypes.TryGetValue(srcId, out allowSet) && !allowSet.Contains(subtype))
                            belongs = false;
                    }
                    if (belongs) continue;

                    // Item is misplaced — find ALL managed containers that accept it.
                    // One op per destination lets items spill across multiple containers when
                    // the first one fills up. Transfer() stops once the source is exhausted.
                    var destIds = new List<long>();
                    foreach (var other in containerCategories)
                    {
                        if (other.Key == srcId) continue;
                        // group isolation: don't cross non-empty group boundaries
                        string oGroup = null;
                        containerGroups.TryGetValue(other.Key, out oGroup);
                        if (!string.IsNullOrEmpty(srcGroup) && !string.IsNullOrEmpty(oGroup)
                            && !string.Equals(srcGroup, oGroup, StringComparison.OrdinalIgnoreCase))
                            continue;
                        bool fits = false;
                        foreach (var oCat in other.Value)
                        {
                            if (_categoryResolver != null && _categoryResolver.ItemMatchesCategory(itemDef, itemStr, oCat))
                            { fits = true; break; }
                        }
                        if (fits)
                        {
                            int si = itemStr.IndexOf('/');
                            string subtype = si >= 0 ? itemStr.Substring(si + 1) : itemStr;
                            HashSet<string> denySet;
                            if (containerDenySubtypes.TryGetValue(other.Key, out denySet) && denySet.Contains(subtype))
                                fits = false;
                            HashSet<string> allowSet;
                            if (fits && containerAllowSubtypes.TryGetValue(other.Key, out allowSet) && !allowSet.Contains(subtype))
                                fits = false;
                        }
                        if (fits) destIds.Add(other.Key);
                    }

                    // No specific category match — fall back to any [IML: MISC] container.
                    // MISC is intentionally not in CategoryMappings so it never pulls items
                    // out of correctly-categorised containers; it only receives overflow.
                    if (destIds.Count == 0)
                    {
                        foreach (var other in containerCategories)
                        {
                            if (other.Key == srcId) continue;
                            string oGroup = null;
                            containerGroups.TryGetValue(other.Key, out oGroup);
                            if (!string.IsNullOrEmpty(srcGroup) && !string.IsNullOrEmpty(oGroup)
                                && !string.Equals(srcGroup, oGroup, StringComparison.OrdinalIgnoreCase))
                                continue;
                            foreach (var oCat in other.Value)
                            {
                                if (string.Equals(oCat, "MISC", StringComparison.OrdinalIgnoreCase))
                                { destIds.Add(other.Key); break; }
                            }
                        }
                    }

                    foreach (var destId in destIds)
                    {
                        batch.Ops.Add(new TransferOp { SourceOwner = srcId, DestinationOwner = destId, ItemDefinitionId = itemDef, Amount = amount });
                        Interlocked.Add(ref _plannedOpsCount, 1);
                    }
                    if (destIds.Count == 0)
                        unsortable.Add($"{itemStr}/{amount} in [{string.Join(",", srcCats)}]");
                }
            }

            try
            {
                int totalItems = containerContents.Values.Sum(d => d.Values.Sum());
                _logger?.Info($"IML: ForcedSort planned {batch.Ops.Count} op(s) across {containerCategories.Count} container(s), {totalItems} total item(s) scanned");
                if (unsortable.Count > 0)
                    _logger?.Info($"IML: {unsortable.Count} item type(s) are misplaced but have no matching destination — add a container tagged for their category or update CategoryMappings in config");
                foreach (var cv in containerContents)
                {
                    string[] cats; containerCategories.TryGetValue(cv.Key, out cats);
                    var catStr = cats != null ? string.Join(",", cats) : "?";
                    var items = string.Join(", ", cv.Value.Select(iv => $"{iv.Key}/{iv.Value}"));
                    _logger?.Info($"IML:   Container {cv.Key} [{catStr}]: {items}");
                }
                foreach (var u in unsortable)
                    _logger?.Debug($"IML:   No destination: {u}");
            }
            catch { }
            return batch;
        }

        private void ProcessReplanRequests()
        {
            // Simple handling: for each replan request, create a single-op batch for the remaining op
            while (_replanQueue.TryDequeue(out var req))
            {
                var b = new TransferBatch();
                b.Ops.Add(req.RemainingOp);
                _batchQueue.Enqueue(b);
            }
        }

        // Called by applier when a replan is required
        public void EnqueueReplan(ReplanRequest req)
        {
            _replanQueue.Enqueue(req);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _task.Wait(1000); } catch { }
            _cts.Dispose();
        }
    }
}

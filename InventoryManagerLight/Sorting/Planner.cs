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
        private readonly Dictionary<ItemKey, float> _previous = new Dictionary<ItemKey, float>();
        // queue for forced categorization sorts (bypasses the diff algorithm)
        private readonly ConcurrentQueue<InventorySnapshot[]> _forcedSortQueue = new ConcurrentQueue<InventorySnapshot[]>();

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
            var current = new Dictionary<ItemKey, float>();
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
                float prev;
                current.TryGetValue(k, out prev);
                current[k] = prev + s.Amount;
            }

            // Build diffs per (owner,item): diff = current - previous
            // For each itemDef, collect sources (diff < 0) and sinks (diff > 0)
            var sourcesByDef = new Dictionary<MyDefinitionId, List<KeyValuePair<long,float>>>(); // itemdef -> list of (owner, available)
            var sinksByDef = new Dictionary<MyDefinitionId, List<KeyValuePair<long,float>>>();   // itemdef -> list of (owner, need)

            // consider keys present in either previous or current
            var keys = new HashSet<ItemKey>(_previous.Keys);
            foreach (var k in current.Keys) keys.Add(k);

            foreach (var k in keys)
            {
                float prevAmt = 0f;
                _previous.TryGetValue(k, out prevAmt);
                float currAmt = 0f;
                current.TryGetValue(k, out currAmt);
                float diff = currAmt - prevAmt;
                if (diff == 0f) continue;
                if (diff > 0f)
                {
                    List<KeyValuePair<long,float>> list;
                    if (!sinksByDef.TryGetValue(k.ItemDef, out list))
                    {
                        list = new List<KeyValuePair<long,float>>();
                        sinksByDef[k.ItemDef] = list;
                    }
                    list.Add(new KeyValuePair<long,float>(k.OwnerId, diff));
                }
                else
                {
                    List<KeyValuePair<long,float>> list;
                    if (!sourcesByDef.TryGetValue(k.ItemDef, out list))
                    {
                        list = new List<KeyValuePair<long,float>>();
                        sourcesByDef[k.ItemDef] = list;
                    }
                    list.Add(new KeyValuePair<long,float>(k.OwnerId, -diff));
                }
            }

            var batch = new TransferBatch();

            // For each item def, match sources to sinks
            foreach (var kv in sinksByDef)
            {
                var itemDef = kv.Key;
                var sinksList = kv.Value;
                // get available sources for this item
                List<KeyValuePair<long,float>> srcs;
                if (!sourcesByDef.TryGetValue(itemDef, out srcs)) continue;

                // We'll match sinks grouped by container-level group token so production loops stay isolated.
                // Build mutable availability map for sources
                var availBySource = new Dictionary<long,float>();
                foreach (var s in srcs) availBySource[s.Key] = s.Value;

                // Group sinks by their container-level group (if any)
                var sinksByGroup = new Dictionary<string, List<KeyValuePair<long,float>>>(StringComparer.OrdinalIgnoreCase);
                foreach (var sn in sinksList)
                {
                    string g = null; ownerGroups.TryGetValue(sn.Key, out g);
                    if (!sinksByGroup.TryGetValue(g, out var list)) { list = new List<KeyValuePair<long,float>>(); sinksByGroup[g] = list; }
                    list.Add(sn);
                }

                // For each group, match its sinks against candidate sources (prefer same-group sources)
                foreach (var gkv in sinksByGroup)
                {
                    var groupName = gkv.Key; // may be null
                    var sinksForGroup = gkv.Value;

                    // pick candidate sources: prefer sources that share the same container-level group
                    List<KeyValuePair<long,float>> candidates;
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
                            catch (Exception ex) { _logger?.Debug("Planner: GetDistances (group) failed: " + ex.Message); }
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
                        catch (Exception ex) { _logger?.Debug("Planner: container-group distance override (group) failed: " + ex.Message); }
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
                    var sinksQ = new Queue<KeyValuePair<long,float>>(sinksForGroup);
                    // build a sources queue from candidates using current availability
                    var srcQueue = new Queue<KeyValuePair<long,float>>();
                    foreach (var c in candidates)
                    {
                        if (availBySource.TryGetValue(c.Key, out var av) && av > 0f) srcQueue.Enqueue(new KeyValuePair<long,float>(c.Key, av));
                    }

                    while (sinksQ.Count > 0 && srcQueue.Count > 0)
                    {
                        var sink = sinksQ.Dequeue();
                        long sinkOwner = sink.Key;
                        float need = sink.Value;
                        while (need > 0f && srcQueue.Count > 0)
                        {
                            var src = srcQueue.Dequeue();
                            long srcOwner = src.Key;
                            float avail = src.Value;
                            float move = Math.Min(avail, need);
                            if (move > 0f)
                            {
                                batch.Ops.Add(new TransferOp { SourceOwner = srcOwner, DestinationOwner = sinkOwner, ItemDefinitionId = itemDef, Amount = move });
                                // reduce global availability
                                availBySource[srcOwner] = availBySource[srcOwner] - move;
                            }
                            avail -= move;
                            need -= move;
                            if (avail > 0f) srcQueue.Enqueue(new KeyValuePair<long,float>(srcOwner, avail));
                        }
                    }
                }
            }

            // coalesce ops by (src,dst,item)
            if (batch.Ops.Count > 1)
            {
                var map = new Dictionary<Tuple<long,long,MyDefinitionId>, float>();
                foreach (var op in batch.Ops)
                {
                    var key = Tuple.Create(op.SourceOwner, op.DestinationOwner, op.ItemDefinitionId);
                    float v;
                    if (!map.TryGetValue(key, out v)) v = 0f;
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

            ApplyUrgencyOrdering(batch);
            return batch;
        }

        private TransferBatch PlanForcedCategorization(InventorySnapshot[] snap)
        {
            var containerCategories = new Dictionary<long, string[]>();
            var containerGroups = new Dictionary<long, string>();
            var containerContents = new Dictionary<long, Dictionary<MyDefinitionId, float>>();
            var containerDenySubtypes = new Dictionary<long, HashSet<string>>();
            var containerAllowSubtypes = new Dictionary<long, HashSet<string>>();
            var containerFillLimit = new Dictionary<long, float>();
            var containerCurrentFill = new Dictionary<long, float>();
            var containerPriority = new Dictionary<long, int>();

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
                if (tag.FillLimit > 0f && tag.FillLimit < 1.0f)
                    containerFillLimit[s.OwnerId] = tag.FillLimit;
                if (tag.Priority != 0)
                    containerPriority[s.OwnerId] = tag.Priority;
                // track max fill fraction seen across all entries for this owner
                float existingFill;
                if (!containerCurrentFill.TryGetValue(s.OwnerId, out existingFill) || s.CurrentVolumeFraction > existingFill)
                    containerCurrentFill[s.OwnerId] = s.CurrentVolumeFraction;

                if (s.Amount <= 0) continue; // sentinel entry for empty containers

                Dictionary<MyDefinitionId, float> dict;
                if (!containerContents.TryGetValue(s.OwnerId, out dict))
                {
                    dict = new Dictionary<MyDefinitionId, float>();
                    containerContents[s.OwnerId] = dict;
                }
                float prev;
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
                    float amount = iv.Value;
                    if (amount <= 0f) continue;
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
                            // IML:FILL= — skip destination if already at or above the fill limit
                            if (fits)
                            {
                                float fillLimit; if (!containerFillLimit.TryGetValue(other.Key, out fillLimit)) fillLimit = 1.0f;
                                float currentFill; containerCurrentFill.TryGetValue(other.Key, out currentFill);
                                if (currentFill >= fillLimit) fits = false;
                            }
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

                    // IML:PRIORITY= — fill higher-priority containers first
                    if (destIds.Count > 1)
                        destIds.Sort((a, b) =>
                        {
                            int pa; if (!containerPriority.TryGetValue(a, out pa)) pa = 0;
                            int pb; if (!containerPriority.TryGetValue(b, out pb)) pb = 0;
                            return pb.CompareTo(pa); // descending
                        });

                    foreach (var destId in destIds)
                    {
                        batch.Ops.Add(new TransferOp { SourceOwner = srcId, DestinationOwner = destId, ItemDefinitionId = itemDef, Amount = amount });
                    }
                    if (destIds.Count == 0)
                        unsortable.Add($"{itemStr}/{amount} in [{string.Join(",", srcCats)}]");
                }
            }

            try
            {
                float totalItems = containerContents.Values.Sum(d => d.Values.Sum());
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
            catch (Exception ex) { _logger?.Debug("Planner: ForcedSort logging failed: " + ex.Message); }
            ApplyUrgencyOrdering(batch);
            return batch;
        }

        // Returns true when the op's item belongs
        private bool IsOpUrgent(TransferOp op)
        {
            if (_demandTracker == null || _categoryResolver == null) return false;
            var urgent = _demandTracker.UrgentCategories;
            if (urgent.Length == 0) return false;
            var itemStr = op.ItemDefinitionId.ToString();
            foreach (var cat in urgent)
            {
                if (_categoryResolver.ItemMatchesCategory(op.ItemDefinitionId, itemStr, cat))
                    return true;
            }
            return false;
        }

        // Sorts a batch so urgent-category ops come first. When ExcludeNonUrgentWhenUrgent is
        // true, non-urgent ops are dropped entirely until urgency clears (hard pause).
        private void ApplyUrgencyOrdering(TransferBatch batch)
        {
            if (batch == null || batch.Ops.Count <= 1) return;
            if (_demandTracker == null || !_demandTracker.IsAnyUrgent) return;

            if (_config != null && _config.ExcludeNonUrgentWhenUrgent)
            {
                batch.Ops.RemoveAll(op => !IsOpUrgent(op));
                return;
            }

            batch.Ops.Sort((a, b) =>
            {
                bool au = IsOpUrgent(a);
                bool bu = IsOpUrgent(b);
                if (au == bu) return 0;
                return au ? -1 : 1;
            });
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

        public void Dispose()
        {
            _cts.Cancel();
            try { _task.Wait(1000); } catch { }
            _cts.Dispose();
        }
    }
}

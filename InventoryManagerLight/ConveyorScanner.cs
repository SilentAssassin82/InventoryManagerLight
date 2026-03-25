using System;
using System.Collections.Generic;
using System.Linq;

#if TORCH
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage;
using VRage.Game;
#endif

namespace InventoryManagerLight
{
    // Lightweight conveyor scanner / heuristic distance calculator.
    // Full conveyor graph traversal is complex; this class provides a simple proximity metric
    // that prefers same-grid sources (distance 0) and other grids (distance 1).
    // When TORCH is available it uses block.CubeGrid to detect same-grid membership.
    public class ConveyorScanner
    {
        private readonly ILogger _logger;
        private readonly RuntimeConfig _config;
        // reusable buffer for GridGroups.GetGroup to avoid allocations
        private readonly List<IMyCubeGrid> _gridGroupBuf = new List<IMyCubeGrid>();
        // cached adjacency graph (gridId -> neighbor gridIds) stored as arrays for memory efficiency
        private Dictionary<long, long[]> _cachedAdjacency;
        private readonly object _adjLock = new object();
        private bool _subscribedGridGroups;
        // mapping from group name to synthetic negative id
        private readonly Dictionary<string, long> _groupNameToId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private long _nextSyntheticGroupId = -1;

        public ConveyorScanner(RuntimeConfig config = null, ILogger logger = null)
        {
            _logger = logger ?? new DefaultLogger();
            _config = config ?? new RuntimeConfig();
            // subscribe to GridGroups events to invalidate cache when topology changes
            try
            {
                if (!_subscribedGridGroups && MyAPIGateway.GridGroups != null)
                {
                    MyAPIGateway.GridGroups.OnGridGroupCreated += OnGridGroupChanged;
                    MyAPIGateway.GridGroups.OnGridGroupDestroyed += OnGridGroupChanged;
                    _subscribedGridGroups = true;
                }
            }
            catch { }
        }

        // Compute distances from a source owner to a set of candidate owners.
        // Returns a map ownerId -> distance (lower is preferred).
        // This is intentionally conservative and cheap; it can be replaced by a full
        // conveyor-graph BFS later.
        public Dictionary<long,int> GetDistances(long sourceOwnerId, IEnumerable<long> candidateOwnerIds)
        {
            var result = new Dictionary<long,int>();
            if (candidateOwnerIds == null) return result;

#if TORCH
            try
            {
                // Try to detect conveyor-connected grids via GridGroups (Conveyor) if available.
                var srcEnt = MyAPIGateway.Entities.GetEntityById(sourceOwnerId) as IMyTerminalBlock;
                var srcGrid = srcEnt?.CubeGrid;
                // Resolve a canonical group id for the source grid (defaults to the grid EntityId)
                long srcGroupId = srcGrid != null ? ResolveGroupId(srcGrid) : 0L;

                // Attempt reflection-based GridGroups -> GetGroup(GridLinkTypeEnum.Conveyor, grid)
                HashSet<long> conveyorGroupGridIds = null;
                // Preferred fast path: use GetGroup(IMyCubeGrid, GridLinkTypeEnum, ICollection<IMyCubeGrid>) to avoid allocations
                try
                {
                    if (MyAPIGateway.GridGroups != null && srcGrid != null)
                    {
                        try
                        {
                            // Use reflection to avoid compiling against a specific enum member name
                            _gridGroupBuf.Clear();
                            var ggObj = MyAPIGateway.GridGroups;
                            if (ggObj != null)
                            {
                                // find enum member name that contains "convey"
                                string enumName = null;
                                try { enumName = Enum.GetNames(typeof(GridLinkTypeEnum)).FirstOrDefault(n => n.IndexOf("convey", StringComparison.OrdinalIgnoreCase) >= 0); } catch { }
                                if (!string.IsNullOrEmpty(enumName))
                                {
                                    var enumVal = Enum.Parse(typeof(GridLinkTypeEnum), enumName);
                                    var ggType = ggObj.GetType();
                                    var getGroupMethod = ggType.GetMethod("GetGroup", new Type[] { typeof(IMyCubeGrid), typeof(GridLinkTypeEnum), typeof(ICollection<IMyCubeGrid>) });
                                    if (getGroupMethod != null)
                                    {
                                        getGroupMethod.Invoke(ggObj, new object[] { srcGrid, enumVal, _gridGroupBuf });
                                        if (_gridGroupBuf.Count > 0)
                                        {
                                            var set = new HashSet<long>();
                                            foreach (var g in _gridGroupBuf) set.Add(g.EntityId);
                                            conveyorGroupGridIds = set;
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug("ConveyorScanner: GridGroups.GetGroup fast-path failed: " + ex.Message);
                        }
                    }
                }
                catch { }
                try
                {
                    var gatewayType = typeof(MyAPIGateway);
                    var gridGroupsProp = gatewayType.GetProperty("GridGroups");
                    if (gridGroupsProp != null)
                    {
                        var gridGroups = gridGroupsProp.GetValue(null);
                        if (gridGroups != null)
                        {
                            // Find GridLinkTypeEnum type
                            var enumType = Type.GetType("VRage.Game.GridLinkTypeEnum, VRage.Game")
                                           ?? Type.GetType("VRage.Groups.GridLinkTypeEnum, VRage")
                                           ?? Type.GetType("VRage.Game.GridLinkTypeEnum");

                            if (enumType != null && srcGrid != null)
                            {
                                object conveyorEnumVal = null;
                                try
                                {
                                    conveyorEnumVal = Enum.Parse(enumType, "Conveyor");
                                }
                                catch { }

                                // Try to find a GetGroup method
                                var ggType = gridGroups.GetType();
                                var getGroup = ggType.GetMethod("GetGroup") ?? ggType.GetMethod("GetGroupForGrid");
                                if (getGroup != null && conveyorEnumVal != null)
                                {
                                    try
                                    {
                                        var groupObj = getGroup.Invoke(gridGroups, new object[] { conveyorEnumVal, srcGrid });
                                        if (groupObj != null)
                                        {
                                            // Try to extract grids from the group object via common members
                                            var gridsSet = new HashSet<long>();
                                            // Common property names that may hold grids
                                            var prop = groupObj.GetType().GetProperty("Grids") ?? groupObj.GetType().GetProperty("Nodes") ?? groupObj.GetType().GetProperty("m_nodes");
                                            if (prop != null)
                                            {
                                                var val = prop.GetValue(groupObj) as System.Collections.IEnumerable;
                                                if (val != null)
                                                {
                                                    foreach (var g in val)
                                                    {
                                                        if (g == null) continue;
                                                        // g may be IMyCubeGrid or a wrapper with Grid property
                                                        var asGrid = g as IMyCubeGrid;
                                                        if (asGrid != null) gridsSet.Add(asGrid.EntityId);
                                                        else
                                                        {
                                                            var gridProp = g.GetType().GetProperty("Grid") ?? g.GetType().GetProperty("CubeGrid");
                                                            if (gridProp != null)
                                                            {
                                                                var gg = gridProp.GetValue(g) as IMyCubeGrid;
                                                                if (gg != null) gridsSet.Add(gg.EntityId);
                                                            }
                                                        }
                                                    }
                                                }
                                            }

                                            // Some implementations may expose a method to enumerate grids
                                            if (gridsSet.Count == 0)
                                            {
                                                var enumMethod = groupObj.GetType().GetMethod("GetGrids");
                                                if (enumMethod != null)
                                                {
                                                    try
                                                    {
                                                        var gcol = enumMethod.Invoke(groupObj, null) as System.Collections.IEnumerable;
                                                        if (gcol != null)
                                                        {
                                                            foreach (var gg in gcol)
                                                            {
                                                                var asGrid2 = gg as IMyCubeGrid;
                                                                if (asGrid2 != null) gridsSet.Add(asGrid2.EntityId);
                                                            }
                                                        }
                                                    }
                                                    catch { }
                                                }
                                            }

                                            if (gridsSet.Count > 0) conveyorGroupGridIds = gridsSet;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug("ConveyorScanner: GridGroups reflection failed: " + ex.Message);
                }

                // If we have conveyor group membership we can do a cheap classification first
                if (conveyorGroupGridIds != null)
                {
                    // Map conveyor group grid entity ids to canonical group ids
                    var conveyorGroupKeys = new HashSet<long>();
                    foreach (var g in conveyorGroupGridIds)
                    {
                        try { conveyorGroupKeys.Add(ResolveGroupIdByEntityId(g)); } catch { }
                    }
                    foreach (var id in candidateOwnerIds)
                    {
                        if (id == sourceOwnerId) { result[id] = 0; continue; }
                        try
                        {
                            var ent = MyAPIGateway.Entities.GetEntityById(id) as IMyTerminalBlock;
                            var gid = ent?.CubeGrid?.EntityId ?? 0L;
                            var key = gid != 0 ? ResolveGroupIdByEntityId(gid) : 0L;
                            if (key != 0 && conveyorGroupKeys.Contains(key)) result[id] = 0;
                            else result[id] = 1;
                        }
                        catch { result[id] = 1; }
                    }
                    return result;
                }

                // If GridGroups path was not available, fall back to building a conveyor endpoint graph and BFS to compute accurate hop distances.
                try
                {
                    // Build or get cached adjacency map: gridId -> set of neighbor gridIds (via conveyors/sorters)
                    var adjacency = GetOrBuildAdjacency();

                    // Determine source grid id
                    var srcEnt2 = MyAPIGateway.Entities.GetEntityById(sourceOwnerId) as IMyTerminalBlock;
                    var sourceGrid = srcEnt2?.CubeGrid;
                    var sourceGroupId = sourceGrid != null ? ResolveGroupId(sourceGrid) : 0L;

                    // If no source grid or adjacency empty, fallback to same-grid heuristic
                    if (sourceGroupId == 0 || adjacency.Count == 0)
                    {
                        foreach (var id in candidateOwnerIds)
                        {
                            if (id == sourceOwnerId) { result[id] = 0; continue; }
                            try
                            {
                                var ent = MyAPIGateway.Entities.GetEntityById(id) as IMyTerminalBlock;
                                var gid = ent?.CubeGrid?.EntityId ?? 0L;
                                var key = gid != 0 ? ResolveGroupIdByEntityId(gid) : 0L;
                                result[id] = (key != 0 && key == sourceGroupId) ? 0 : 1;
                            }
                            catch { result[id] = 1; }
                        }
                        return result;
                    }

                    // BFS from sourceGridId to compute hop distances
                    var distances = new Dictionary<long,int>();
                    var q = new Queue<long>();
                    distances[sourceGroupId] = 0;
                    q.Enqueue(sourceGroupId);
                    while (q.Count > 0)
                    {
                        var g = q.Dequeue();
                        int d = distances[g];
                        if (!adjacency.TryGetValue(g, out var neigh)) continue;
                        foreach (var n in neigh)
                        {
                            if (distances.ContainsKey(n)) continue;
                            distances[n] = d + 1;
                            q.Enqueue(n);
                        }
                    }

                    // Map candidate owners to nearest grid distance (default large)
                    foreach (var id in candidateOwnerIds)
                    {
                        if (id == sourceOwnerId) { result[id] = 0; continue; }
                        try
                        {
                            var ent = MyAPIGateway.Entities.GetEntityById(id) as IMyTerminalBlock;
                            var gid = ent?.CubeGrid?.EntityId ?? 0L;
                            var key = gid != 0 ? ResolveGroupIdByEntityId(gid) : 0L;
                            if (key != 0 && distances.TryGetValue(key, out var dist)) result[id] = dist;
                            else
                            {
                                // If configured to restrict to conveyor-connected grids, mark unreachable as very large
                                if (_config != null && _config.RestrictToConveyorConnectedGrids)
                                {
                                    result[id] = int.MaxValue / 4; // treated as unreachable/infinite
                                }
                                else
                                {
                                    // allow non-connected grids but penalize heavily (distance 100)
                                    result[id] = 100;
                                }
                            }
                        }
                        catch { result[id] = int.MaxValue / 4; }
                    }
                    return result;
                }
                catch (Exception ex)
                {
                    _logger?.Debug("ConveyorScanner: adjacency BFS failed: " + ex.Message);
                    // fallthrough to fallback below
                }
            }
            catch (Exception ex)
            {
                _logger?.Error("ConveyorScanner exception: " + ex.Message);
                // fallback: mark everything as distance 1 except source
                foreach (var id in candidateOwnerIds)
                {
                    result[id] = id == sourceOwnerId ? 0 : 1;
                }
            }
#else
            // Non-Torch builds: simple heuristic
            foreach (var id in candidateOwnerIds)
            {
                result[id] = id == sourceOwnerId ? 0 : 1;
            }
#endif
            return result;
        }

#if TORCH
        // Build adjacency map of gridId -> neighbor gridIds via conveyor endpoint connections
        // Returns arrays for memory efficiency
        private Dictionary<long, long[]> BuildConveyorAdjacency()
        {
            // temp maps canonical group id -> neighbor canonical group ids
            var temp = new Dictionary<long, HashSet<long>>();
            try
            {
                // Collect all terminal blocks and filter to conveyor endpoints via the typed interface
                var all = new HashSet<VRage.ModAPI.IMyEntity>();
                MyAPIGateway.Entities.GetEntities(all, e => e is IMyTerminalBlock);

                var endpoints = new List<Sandbox.Game.GameSystems.Conveyors.IMyConveyorEndpointBlock>();
                foreach (var e in all)
                {
                    try
                    {
                        var tb = e as IMyTerminalBlock;
                        if (tb == null) continue;
                        var endpoint = tb as Sandbox.Game.GameSystems.Conveyors.IMyConveyorEndpointBlock;
                        if (endpoint != null) endpoints.Add(endpoint);
                    }
                    catch { }
                }

                // Build connectivity using typed IMyConveyorEndpointBlock methods
                foreach (var ep in endpoints)
                {
                    try
                    {
                        var epTb = ep as IMyTerminalBlock;
                        if (epTb == null) continue;
                        var srcGrid = epTb.CubeGrid;
                        if (srcGrid == null) continue;
                        // use canonical group id key instead of raw grid id
                        var srcKey = ResolveGroupId(srcGrid);
                        if (!temp.TryGetValue(srcKey, out var set)) { set = new HashSet<long>(); temp[srcKey] = set; }

                        // Call typed method - use dynamic to tolerate minor signature differences at runtime
                        try
                        {
                            dynamic dyn = epTb;
                            var attached = dyn.GetAttachedEntities();
                            if (attached is System.Collections.IEnumerable aEnum)
                            {
                                foreach (var a in aEnum)
                                {
                                    if (a == null) continue;
                                    try
                                    {
                                        if (a is VRage.ModAPI.IMyEntity ae)
                                        {
                                            var otherGrid = (ae as IMyTerminalBlock)?.CubeGrid ?? (ae as IMyCubeGrid);
                                        if (otherGrid != null)
                                        {
                                            var otherKey = ResolveGroupId(otherGrid);
                                            if (otherKey != srcKey)
                                            {
                                                set.Add(otherKey);
                                                if (!temp.TryGetValue(otherKey, out var set2)) { set2 = new HashSet<long>(); temp[otherKey] = set2; }
                                                set2.Add(srcKey);
                                            }
                                        }
                                        }
                                        else if (a is IMyCubeGrid ag)
                                        {
                                            try
                                            {
                                                var otherKey2 = ResolveGroupId(ag);
                                                if (otherKey2 != srcKey)
                                                {
                                                    set.Add(otherKey2);
                                                    if (!temp.TryGetValue(otherKey2, out var set2)) { set2 = new HashSet<long>(); temp[otherKey2] = set2; }
                                                    set2.Add(srcKey);
                                                }
                                            }
                                            catch { }
                                        }
                                        else if (a is long id)
                                        {
                                            var ent = MyAPIGateway.Entities.GetEntityById(id) as IMyTerminalBlock;
                                            var otherGrid = ent?.CubeGrid;
                                            if (otherGrid != null)
                                            {
                                                var otherKey = ResolveGroupId(otherGrid);
                                                if (otherKey != srcKey)
                                                {
                                                    set.Add(otherKey);
                                                    if (!temp.TryGetValue(otherKey, out var set2)) { set2 = new HashSet<long>(); temp[otherKey] = set2; }
                                                    set2.Add(srcKey);
                                                }
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch
                        {
                            // fall back to trying reflection on ep if dynamic call fails
                            try
                            {
                                var t = ep.GetType();
                                var m1 = t.GetMethod("GetAttachedEntities") ?? t.GetMethod("GetConnectedEntities") ?? t.GetMethod("GetAttachedEndpoints");
                                System.Collections.IEnumerable attached = null;
                                if (m1 != null) { attached = m1.Invoke(ep, null) as System.Collections.IEnumerable; }
                                if (attached != null)
                                {
                                    foreach (var a in attached)
                                    {
                                        if (a == null) continue;
                                        var asEnt = a as VRage.ModAPI.IMyEntity;
                                        if (asEnt != null)
                                        {
                                            var otherGrid = (asEnt as IMyTerminalBlock)?.CubeGrid ?? (asEnt as IMyCubeGrid);
                                            if (otherGrid != null)
                                            {
                                                var otherKey = ResolveGroupId(otherGrid);
                                                if (otherKey != srcKey)
                                                {
                                                    set.Add(otherKey);
                                                    if (!temp.TryGetValue(otherKey, out var set2)) { set2 = new HashSet<long>(); temp[otherKey] = set2; }
                                                    set2.Add(srcKey);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug("BuildConveyorAdjacency failed: " + ex.Message);
            }

            // convert to memory-efficient arrays
            var result = new Dictionary<long, long[]>();
            foreach (var kv in temp)
            {
                try
                {
                    result[kv.Key] = kv.Value == null ? Array.Empty<long>() : kv.Value.ToArray();
                }
                catch { result[kv.Key] = Array.Empty<long>(); }
            }
            return result;
        }

        private void OnGridGroupChanged(IMyGridGroupData obj)
        {
            lock (_adjLock)
            {
                _cachedAdjacency = null;
            }
        }

        // Resolve a canonical group id for a grid. By default returns the grid.EntityId
        // but will map if the grid has a CustomData tag like "IML:Group=Name" or if a
        // manual mapping was added. Group ids allocated here are negative synthetic ids.
        private long ResolveGroupId(IMyCubeGrid grid)
        {
            try
            {
                if (grid == null) return 0L;
                // check CustomData for a group tag
                var tagPrefix = _config?.ContainerTagPrefix ?? "IML:";
                var cd = string.Empty;
                try
                {
                    var t = grid.GetType();
                    var prop = t.GetProperty("CustomData");
                    if (prop != null)
                    {
                        var val = prop.GetValue(grid) as string;
                        cd = val ?? string.Empty;
                    }
                }
                catch { }
                if (!string.IsNullOrEmpty(cd))
                {
                    // look for lines like "IML:Group=FactoryA"
                    var parts = cd.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var p in parts)
                    {
                        var t = p.Trim();
                        if (t.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            var body = t.Substring(tagPrefix.Length);
                            if (body.StartsWith("Group=", StringComparison.OrdinalIgnoreCase))
                            {
                                var groupName = body.Substring(6).Trim();
                                if (!string.IsNullOrEmpty(groupName)) return GetOrCreateSyntheticGroupId(groupName);
                            }
                        }
                    }
                }

                // fallback: use grid display name (simple, may collide)
                string name = null;
                try
                {
                    var t2 = grid.GetType();
                    var p1 = t2.GetProperty("DisplayNameText");
                    var p2 = t2.GetProperty("DisplayName");
                    if (p1 != null) name = p1.GetValue(grid) as string;
                    if (string.IsNullOrEmpty(name) && p2 != null) name = p2.GetValue(grid) as string;
                }
                catch { }
                if (!string.IsNullOrEmpty(name))
                {
                    return GetOrCreateSyntheticGroupId(name);
                }

                // default: use real entity id
                return grid.EntityId;
            }
            catch { return grid?.EntityId ?? 0L; }
        }

        private long ResolveGroupIdByEntityId(long entityId)
        {
            try
            {
                var ent = MyAPIGateway.Entities.GetEntityById(entityId) as IMyCubeGrid;
                if (ent != null) return ResolveGroupId(ent);
            }
            catch { }
            return entityId;
        }

        private long GetOrCreateSyntheticGroupId(string name)
        {
            lock (_adjLock)
            {
                if (_groupNameToId.TryGetValue(name, out var id)) return id;
                var nid = _nextSyntheticGroupId--;
                _groupNameToId[name] = nid;
                return nid;
            }
        }

        private Dictionary<long, long[]> GetOrBuildAdjacency()
        {
            lock (_adjLock)
            {
                if (_cachedAdjacency != null) return _cachedAdjacency;
                try
                {
                    _cachedAdjacency = BuildConveyorAdjacency();
                }
                catch (Exception ex)
                {
                    _logger?.Debug("GetOrBuildAdjacency failed: " + ex.Message);
                    _cachedAdjacency = new Dictionary<long, long[]>();
                }
                return _cachedAdjacency;
            }
        }
#if TORCH
        // Expose adjacency snapshot for diagnostics
        public Dictionary<long, long[]> GetAdjacencySnapshot()
        {
            return GetOrBuildAdjacency();
        }
#endif
#endif
    }
}

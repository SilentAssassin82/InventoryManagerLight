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
    // Lightweight conveyor scanner.
    // Uses GridGroups.GetGroup(Conveyor) via reflection to determine same-network membership,
    // falling back to same-grid heuristic when the API is unavailable.
    // Distance 0 = same conveyor network, 1 = different or unknown network.
    public class ConveyorScanner
    {
        private readonly ILogger _logger;
        private readonly RuntimeConfig _config;
        // reusable buffer for GridGroups.GetGroup to avoid allocations
        private readonly List<IMyCubeGrid> _gridGroupBuf = new List<IMyCubeGrid>();

        public ConveyorScanner(RuntimeConfig config = null, ILogger logger = null)
        {
            _logger = logger ?? new DefaultLogger();
            _config = config ?? new RuntimeConfig();
        }

        // Returns a map ownerId -> distance: 0 = same conveyor network, 1 = different/unknown.
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

                // Fast path: use GridGroups.GetGroup via reflection to find all grids in the same conveyor network.
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
                catch (Exception ex)
                {
                    _logger?.Debug("ConveyorScanner: GridGroups.GetGroup failed: " + ex.Message);
                }

                if (conveyorGroupGridIds != null)
                {
                    foreach (var id in candidateOwnerIds)
                    {
                        if (id == sourceOwnerId) { result[id] = 0; continue; }
                        try
                        {
                            var ent = MyAPIGateway.Entities.GetEntityById(id) as IMyTerminalBlock;
                            var gid = ent?.CubeGrid?.EntityId ?? 0L;
                            result[id] = (gid != 0 && conveyorGroupGridIds.Contains(gid)) ? 0 : 1;
                        }
                        catch { result[id] = 1; }
                    }
                    return result;
                }

                // GridGroups unavailable: fall back to same-grid heuristic.
                var srcGridId = srcGrid?.EntityId ?? 0L;
                foreach (var id in candidateOwnerIds)
                {
                    if (id == sourceOwnerId) { result[id] = 0; continue; }
                    try
                    {
                        var ent = MyAPIGateway.Entities.GetEntityById(id) as IMyTerminalBlock;
                        var gid = ent?.CubeGrid?.EntityId ?? 0L;
                        result[id] = (srcGridId != 0 && gid == srcGridId) ? 0 : 1;
                    }
                    catch { result[id] = 1; }
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

    }
}

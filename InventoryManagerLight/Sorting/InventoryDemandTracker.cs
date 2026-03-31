using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace InventoryManagerLight
{
    // Tracks demand for item types from consumers like assemblers/refineries.
    // Currently a simple counter of requested amounts; integrable with planning heuristics.
    public class InventoryDemandTracker
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<VRage.Game.MyDefinitionId, long> _demand = new System.Collections.Concurrent.ConcurrentDictionary<VRage.Game.MyDefinitionId, long>(new MyDefinitionIdComparer());
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Tuple<long, VRage.Game.MyDefinitionId>, long> _ownerDemand = new System.Collections.Concurrent.ConcurrentDictionary<Tuple<long, VRage.Game.MyDefinitionId>, long>();
        // Categories currently in urgent mode (stock below configured threshold).
        private readonly ConcurrentDictionary<string, bool> _urgentCategories = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public void AddDemand(VRage.Game.MyDefinitionId def, int amount)
        {
            if (amount <= 0) return;
            _demand.AddOrUpdate(def, amount, (k, v) => v + amount);
        }

        public void AddDemandForOwner(long ownerId, VRage.Game.MyDefinitionId def, int amount)
        {
            if (amount <= 0) return;
            var key = Tuple.Create(ownerId, def);
            _ownerDemand.AddOrUpdate(key, amount, (k, v) => v + amount);
        }

        public long GetDemand(VRage.Game.MyDefinitionId def)
        {
            _demand.TryGetValue(def, out var v);
            return v;
        }

        public long GetDemandForOwner(long ownerId, VRage.Game.MyDefinitionId def)
        {
            var key = Tuple.Create(ownerId, def);
            _ownerDemand.TryGetValue(key, out var v);
            return v;
        }

        public void ClearDemand(VRage.Game.MyDefinitionId def)
        {
            _demand.TryRemove(def, out _);
        }

        public void ClearDemandForOwner(long ownerId, VRage.Game.MyDefinitionId def)
        {
            var key = Tuple.Create(ownerId, def);
            _ownerDemand.TryRemove(key, out _);
        }

        public void ClearAll()
        {
            _demand.Clear();
            _ownerDemand.Clear();
        }

        // Mark or clear a category as urgent. Thread-safe.
        public void SetUrgent(string category, bool urgent)
        {
            if (string.IsNullOrEmpty(category)) return;
            if (urgent) _urgentCategories[category] = true;
            else { bool dummy; _urgentCategories.TryRemove(category, out dummy); }
        }

        public bool IsUrgent(string category)
        {
            bool v;
            return _urgentCategories.TryGetValue(category, out v) && v;
        }

        public bool IsAnyUrgent { get { return !_urgentCategories.IsEmpty; } }

        // Snapshot of currently urgent category names (safe to iterate on any thread).
        public string[] UrgentCategories { get { return _urgentCategories.Keys.ToArray(); } }

        // Apply decay factor to all recorded demands. Factor should be in (0..1]; values are multiplied by factor.
        public void ApplyDecay(double factor)
        {
            if (factor >= 1.0) return; // no decay
            if (factor <= 0.0)
            {
                ClearAll();
                return;
            }

            foreach (var k in _demand.Keys)
            {
                _demand.AddOrUpdate(k, 0, (key, old) => (long)Math.Max(0, Math.Round(old * factor)));
            }

            foreach (var k in _ownerDemand.Keys)
            {
                _ownerDemand.AddOrUpdate(k, 0, (key, old) => (long)Math.Max(0, Math.Round(old * factor)));
            }
        }

        private class MyDefinitionIdComparer : IEqualityComparer<VRage.Game.MyDefinitionId>
        {
            public bool Equals(VRage.Game.MyDefinitionId x, VRage.Game.MyDefinitionId y)
            {
                return x.Equals(y);
            }

            public int GetHashCode(VRage.Game.MyDefinitionId obj)
            {
                return obj.GetHashCode();
            }
        }
    }
}

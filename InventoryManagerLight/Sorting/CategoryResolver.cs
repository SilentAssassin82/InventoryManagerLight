using System;
using System.Collections.Generic;
using System.Linq;
#if TORCH
using VRage.Game;
using VRage.ObjectBuilders;
#endif

namespace InventoryManagerLight
{
    // Resolve category name -> concrete MyDefinitionId set using RuntimeConfig.CategoryMappings.
    // When running under TORCH this will attempt to query game definitions; in non-TORCH builds
    // it will simply parse substring tokens into lightweight MyDefinitionId stubs.
    public class CategoryResolver
    {
        private Dictionary<string, List<VRage.Game.MyDefinitionId>> _map = new Dictionary<string, List<VRage.Game.MyDefinitionId>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string[]> _rawTokens = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        // Exact-subtype map for admin-defined custom categories (SubtypeId comparison, case-insensitive).
        private Dictionary<string, HashSet<string>> _customExactSubtypes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        public CategoryResolver(RuntimeConfig config)
        {
            Build(config);
        }

        // Re-populate all internal maps from the supplied config (call after !iml reload).
        public void Rebuild(RuntimeConfig config)
        {
            Build(config);
        }

        private void Build(RuntimeConfig config)
        {
            if (config == null) return;

            var newMap = new Dictionary<string, List<VRage.Game.MyDefinitionId>>(StringComparer.OrdinalIgnoreCase);
            var newTokens = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in config.CategoryMappings)
            {
                var cat = kv.Key;
                var tokens = (kv.Value ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
#if TORCH
                var defs = new List<VRage.Game.MyDefinitionId>();
                // Try to resolve using game definitions via reflection so this compiles cleanly.
                try
                {
                    var mgrType = Type.GetType("VRage.Game.MyDefinitionManager, VRage.Game");
                    if (mgrType != null)
                    {
                        var staticProp = mgrType.GetProperty("Static", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        var mgr = staticProp?.GetValue(null);
                        var getAll = mgrType.GetMethod("GetAllDefinitions", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        var all = getAll?.Invoke(mgr, null) as System.Collections.IEnumerable;
                        if (all != null)
                        {
                            foreach (var d in all)
                            {
                                try
                                {
                                    var idProp = d.GetType().GetProperty("Id");
                                    var idObj = idProp?.GetValue(d);
                                    if (idObj is VRage.Game.MyDefinitionId id)
                                    {
                                        var text = id.ToString();
                                        foreach (var tok in tokens)
                                        {
                                            if (text.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)
                                            {
                                                defs.Add(id);
                                                break;
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
                newMap[cat] = defs;
#else
                var defs = new List<VRage.Game.MyDefinitionId>();
                foreach (var tok in tokens)
                {
                    defs.Add(VRage.Game.MyDefinitionId.FromString(tok));
                }
                newMap[cat] = defs;
#endif
                newTokens[cat] = tokens;
            }

            // Build exact-subtype map for custom categories.
            var newCustom = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in config.CustomCategories)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sub in kv.Value)
                    if (!string.IsNullOrWhiteSpace(sub)) set.Add(sub.Trim());
                if (set.Count > 0)
                    newCustom[kv.Key] = set;
            }

            // Atomic swap — readers on other threads see either the old or new maps, never a torn state.
            _map = newMap;
            _rawTokens = newTokens;
            _customExactSubtypes = newCustom;
        }

        public IEnumerable<VRage.Game.MyDefinitionId> Resolve(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return Array.Empty<VRage.Game.MyDefinitionId>();
            if (_map.TryGetValue(category, out var list)) return list;
#if !TORCH
            // Custom categories: return stubs for non-Torch builds only (fields are readonly in TORCH).
            if (_customExactSubtypes.TryGetValue(category, out var subs))
                return subs.Select(s => new VRage.Game.MyDefinitionId { SubtypeId = s }).ToList();
#endif
            return Array.Empty<VRage.Game.MyDefinitionId>();
        }

        // Returns true if itemDef belongs to the given category name.
        // Custom categories are checked first using exact SubtypeId matching.
        // Built-in categories fall back to the resolved def list and raw substring tokens.
        public bool ItemMatchesCategory(VRage.Game.MyDefinitionId itemDef, string itemStr, string categoryName)
        {
            if (string.IsNullOrEmpty(categoryName)) return false;
            // Custom category: exact SubtypeId match (uses itemStr to avoid MyStringHash/string type differences).
            if (_customExactSubtypes.TryGetValue(categoryName, out var subs))
            {
                int si = itemStr.IndexOf('/');
                var subtypeStr = si >= 0 ? itemStr.Substring(si + 1) : itemStr;
                if (!string.IsNullOrEmpty(subtypeStr) && subs.Contains(subtypeStr))
                    return true;
            }
            if (_map.TryGetValue(categoryName, out var defs) && defs.Count > 0)
            {
                foreach (var d in defs)
                    if (d.Equals(itemDef)) return true;
            }
            // fallback: substring match against raw config tokens (e.g. "Ingot", "Ore")
            if (_rawTokens.TryGetValue(categoryName, out var toks))
            {
                foreach (var tok in toks)
                    if (!string.IsNullOrEmpty(tok) && itemStr.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
            }
            return false;
        }

        // Returns all known category names (built-in + custom).
        public IEnumerable<string> AllCategoryNames()
        {
            foreach (var k in _rawTokens.Keys) yield return k;
            foreach (var k in _customExactSubtypes.Keys)
                if (!_rawTokens.ContainsKey(k)) yield return k;
        }

        // Returns true if the given category is a custom (exact-subtype) category.
        public bool IsCustomCategory(string categoryName) =>
            !string.IsNullOrEmpty(categoryName) && _customExactSubtypes.ContainsKey(categoryName);
    }
}

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
        private readonly Dictionary<string, List<VRage.Game.MyDefinitionId>> _map = new Dictionary<string, List<VRage.Game.MyDefinitionId>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> _rawTokens = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        public CategoryResolver(RuntimeConfig config)
        {
            if (config == null) return;
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
                _map[cat] = defs;
#else
                var defs = new List<VRage.Game.MyDefinitionId>();
                foreach (var tok in tokens)
                {
                    defs.Add(VRage.Game.MyDefinitionId.FromString(tok));
                }
                _map[cat] = defs;
#endif
                _rawTokens[cat] = tokens;
            }
        }

        public IEnumerable<VRage.Game.MyDefinitionId> Resolve(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return Array.Empty<VRage.Game.MyDefinitionId>();
            if (_map.TryGetValue(category, out var list)) return list;
            return Array.Empty<VRage.Game.MyDefinitionId>();
        }

        // Returns true if itemDef belongs to the given category name.
        // Uses the resolved def list first, then falls back to raw substring token matching
        // (so it works even when game definitions haven't been fully loaded yet).
        public bool ItemMatchesCategory(VRage.Game.MyDefinitionId itemDef, string itemStr, string categoryName)
        {
            if (string.IsNullOrEmpty(categoryName)) return false;
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
    }
}

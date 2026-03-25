using System;
using System.Collections.Generic;
using System.Linq;

namespace InventoryManagerLight
{
    // Responsible for discovering container categories from block CustomName or CustomData
    public static class ContainerMatcher
    {
        // Result of parsing a container tag: categories and optional group/subgroup
        public struct ContainerTagInfo
        {
            public string[] Categories; // upper-case
            public string Group; // optional group name or subgroup token
            public string[] DenySubtypes;  // subtypes blocked from this container (e.g. "Uranium")
            public string[] AllowSubtypes; // if non-empty, only these subtypes are accepted
        }

        // Parse a container tag supporting variants:
        //  - IML:INGOTS
        //  - IML:INGOTS:L1  (category + subgroup)
        //  - IML:L1:INGOTS  (subgroup + category)
        //  - IML:Group=Name  (explicit group)
        // Priority: CustomData first, then name.
        public static ContainerTagInfo ParseContainerTag(string name, string customData, string prefix)
        {
            var res = new ContainerTagInfo { Categories = Array.Empty<string>(), Group = null };
            if (string.IsNullOrWhiteSpace(prefix)) return res;
            // helper to process a token after prefix
            ContainerTagInfo proc(string token)
            {
                var r = new ContainerTagInfo { Categories = Array.Empty<string>(), Group = null };
                if (string.IsNullOrWhiteSpace(token)) return r;
                // strip trailing bracket/punctuation so "[IML:INGOTS]" and "(IML:INGOTS)" parse correctly
                var line = token.Trim().TrimEnd(']', ')', '>', ' ', '\t');
                // explicit Group= syntax
                if (line.StartsWith("Group=", StringComparison.OrdinalIgnoreCase))
                {
                    var g = line.Substring(6).Trim();
                    r.Group = string.IsNullOrEmpty(g) ? null : g;
                    return r;
                }
                // split by ':' to support subgroup notation
                var parts = line.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToArray();
                if (parts.Length == 1)
                {
                    // single token -> could be comma-separated categories or a pure subgroup like "L1"
                    var tokenSingle = parts[0];
                    // if token looks like a short subgroup (contains digits and letters, short), treat as group-only
                    bool looksLikeSubgroup = tokenSingle.Length <= 4 && tokenSingle.Any(char.IsDigit) && tokenSingle.Any(char.IsLetter);
                    if (looksLikeSubgroup)
                    {
                        r.Group = tokenSingle;
                        return r;
                    }
                    r.Categories = parts[0].Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToUpperInvariant()).ToArray();
                    return r;
                }
                if (parts.Length == 2)
                {
                    // ambiguous: treat first as category unless it equals "GROUP" or matches known category mapping by caller
                    // We'll assume pattern Category:Sub or Sub:Category; detect which looks like a category by presence of comma or by all-letters
                    var a = parts[0]; var b = parts[1];
                    // if first contains comma it's categories
                    if (a.Contains(",")) { r.Categories = a.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToUpperInvariant()).ToArray(); r.Group = b; return r; }
                    // simple heuristic: if b looks like a single short token (L1) prefer a:category, b:group
                    if (b.Length <= 3) { r.Categories = new[] { a.ToUpperInvariant() }; r.Group = b; return r; }
                    // otherwise assume a is subgroup and b is category
                    r.Categories = new[] { b.ToUpperInvariant() }; r.Group = a; return r;
                }
                // 3+ parts: try to find a part that equals Group= or contains known keywords; fallback: last is category, first is subgroup
                r.Categories = new[] { parts.Last().ToUpperInvariant() };
                r.Group = parts.First();
                return r;
            }

            // check customData first (line-based)
            if (!string.IsNullOrWhiteSpace(customData))
            {
                var idx = customData.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var token = customData.Substring(idx + prefix.Length);
                    var firstLine = token.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? token;
                    var r = proc(firstLine);
                    ScanFilters(customData, ref r);
                    return r;
                }
            }

            // fallback to name
            if (!string.IsNullOrWhiteSpace(name))
            {
                var idx2 = name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (idx2 >= 0)
                {
                    var token = name.Substring(idx2 + prefix.Length);
                    var r = proc(token);
                    if (!string.IsNullOrWhiteSpace(customData)) ScanFilters(customData, ref r);
                    return r;
                }
            }

            return res;
        }
        // Parse categories from name: e.g., "IML:INGOTS,COMPONENTS"
        public static string[] GetCategoriesFromName(string name, string prefix)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(prefix)) return Array.Empty<string>();
            var idx = name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return Array.Empty<string>();
            var token = name.Substring(idx + prefix.Length).Trim();
            if (string.IsNullOrWhiteSpace(token)) return Array.Empty<string>();
            return token.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToUpperInvariant()).ToArray();
        }

        // Parse categories from CustomData. Accept a simple key/value like "IML:INGOTS,COMPONENTS" or JSON fallback.
        public static string[] GetCategoriesFromCustomData(string customData, string prefix)
        {
            if (string.IsNullOrWhiteSpace(customData)) return Array.Empty<string>();
            // simple search for prefix
            var idx = customData.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var token = customData.Substring(idx + prefix.Length);
                var firstLine = token.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? token;
                return firstLine.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToUpperInvariant()).ToArray();
            }
            return Array.Empty<string>();
        }

        // Returns categories from customData if present, otherwise from name. Priority: customData > name.
        public static string[] GetCategories(string name, string customData, string prefix)
        {
            var cd = GetCategoriesFromCustomData(customData, prefix);
            if (cd.Length > 0) return cd;
            return GetCategoriesFromName(name, prefix);
        }

        // Returns true if block is managed (has any categories)
        public static bool IsManaged(string name, string customData, string prefix)
        {
            return GetCategories(name, customData, prefix).Length > 0;
        }

        // Scan all lines of customData for IML:DENY= and IML:ALLOW= tokens.
        private static void ScanFilters(string customData, ref ContainerTagInfo tag)
        {
            foreach (var line in customData.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("IML:DENY=", StringComparison.OrdinalIgnoreCase))
                {
                    tag.DenySubtypes = trimmed.Substring(9)
                        .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
                }
                else if (trimmed.StartsWith("IML:ALLOW=", StringComparison.OrdinalIgnoreCase))
                {
                    tag.AllowSubtypes = trimmed.Substring(10)
                        .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
                }
            }
        }
    }
}

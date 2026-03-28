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
            public bool IsLocked;          // IML:LOCKED — skip as both source and destination
            public float FillLimit;           // IML:FILL=75 → 0.75f; destination skipped when at/above this fraction
            public int Priority;              // IML:PRIORITY=n; higher value fills first (default 0)
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

            // check customData first (line-based) — scan each line so directive-only lines
            // (IML:MIN=, IML:DENY=, IML:NoDrain, etc.) are skipped and never parsed as categories.
            if (!string.IsNullOrWhiteSpace(customData))
            {
                foreach (var cdLine in customData.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmedCd = cdLine.Trim();
                    var prefIdx = trimmedCd.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                    if (prefIdx < 0) continue;
                    var tokenAfterPrefix = trimmedCd.Substring(prefIdx + prefix.Length);
                    if (IsDirectiveToken(tokenAfterPrefix.Trim())) continue;
                    var r = proc(tokenAfterPrefix);
                    ScanFilters(customData, name, ref r);
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
                    ScanFilters(customData, name, ref r);
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
            if (string.IsNullOrWhiteSpace(token) || IsDirectiveToken(token)) return Array.Empty<string>();
            return token.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToUpperInvariant()).ToArray();
        }

        // Parse categories from CustomData. Accept a simple key/value like "IML:INGOTS,COMPONENTS" or JSON fallback.
        public static string[] GetCategoriesFromCustomData(string customData, string prefix)
        {
            if (string.IsNullOrWhiteSpace(customData)) return Array.Empty<string>();
            foreach (var cdLine in customData.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmedCd = cdLine.Trim();
                var prefIdx = trimmedCd.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (prefIdx < 0) continue;
                var token = trimmedCd.Substring(prefIdx + prefix.Length).Trim();
                if (IsDirectiveToken(token)) continue;
                return token.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToUpperInvariant()).ToArray();
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

        // Tokens that follow the prefix but are NOT category names — used to skip directive lines
        // when searching for a container category tag.
        private static readonly string[] _directiveTokenPrefixes = new[]
        {
            "MIN=", "DENY=", "ALLOW=", "NoDrain", "LCD", "SortNow", "LOCKED", "FILL=", "PRIORITY="
        };

        private static bool IsDirectiveToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            foreach (var d in _directiveTokenPrefixes)
            {
                if (token.StartsWith(d, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Scan all lines of customData (and the block name) for IML:DENY=, IML:ALLOW=, and IML:LOCKED.
        private static void ScanFilters(string customData, string name, ref ContainerTagInfo tag)
        {
            // check name for LOCKED
            if (!string.IsNullOrEmpty(name) && name.IndexOf("IML:LOCKED", StringComparison.OrdinalIgnoreCase) >= 0)
                tag.IsLocked = true;

            if (string.IsNullOrEmpty(customData)) return;
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
                else if (trimmed.Equals("IML:LOCKED", StringComparison.OrdinalIgnoreCase))
                {
                    tag.IsLocked = true;
                }
                else if (trimmed.StartsWith("IML:FILL=", StringComparison.OrdinalIgnoreCase))
                {
                    float pct;
                    if (float.TryParse(trimmed.Substring(9).Trim(), out pct))
                        tag.FillLimit = Math.Max(0f, Math.Min(100f, pct)) / 100f;
                }
                else if (trimmed.StartsWith("IML:PRIORITY=", StringComparison.OrdinalIgnoreCase))
                {
                    int pri;
                    if (int.TryParse(trimmed.Substring(13).Trim(), out pri))
                        tag.Priority = pri;
                }
            }
        }
    }
}

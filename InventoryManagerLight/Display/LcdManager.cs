using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
#if TORCH
using Sandbox.ModAPI;
using VRage.ModAPI;
using VRage.Game.GUI.TextPanel;
using VRageMath;
#endif

namespace InventoryManagerLight
{
    // Queues sprite-based LCD updates built on the game thread and applies them via DrawFrame.
    // Thread-safe queue; ApplyPendingUpdates must be called on the game thread.
    public class LcdManager
    {
        private static readonly Lazy<LcdManager> _lazy = new Lazy<LcdManager>(() => new LcdManager());
        public static LcdManager Instance => _lazy.Value;

        private readonly ConcurrentQueue<LcdUpdate> _queue = new ConcurrentQueue<LcdUpdate>();
        private ILogger _logger;
        private static ILogger _pendingLogger;

        #if TORCH
        // Snapshot infrastructure — captures resolved sprites for the external layout editor.
        private readonly ConcurrentDictionary<long, string> _pendingSnapshots = new ConcurrentDictionary<long, string>();
        private readonly ConcurrentDictionary<long, List<MySprite>> _capturedSprites = new ConcurrentDictionary<long, List<MySprite>>();
        private string _pluginDir;
        private string _lastSnapshotPath;

        /// <summary>Returns the full file path of the most recent snapshot written, or null if none.</summary>
        internal string LastSnapshotPath => _lastSnapshotPath;

        /// <summary>Sets the plugin directory used for snapshot file output.</summary>
        internal void SetPluginDir(string dir) { _pluginDir = dir; }
#endif

        // Layout constants designed for a 512×512 surface; all values are scaled at render time.
        private const float BASE   = 512f;
        private const float PAD    = 10f;
        private const float ROW_H  = 26f;
        private const float BAR_H  = 13f;
        private const float ICON_SZ = 22f;

        private LcdManager()
        {
            _logger = _pendingLogger ?? new DefaultLogger();
        }

        // Initialize global LCD manager with a specific logger. Call once during startup.
        public static void Initialize(ILogger logger)
        {
            _pendingLogger = logger;
            if (logger != null && _lazy.IsValueCreated)
                _lazy.Value.SetLogger(logger);
        }

        private void SetLogger(ILogger logger)
        {
            _logger = logger ?? new DefaultLogger();
        }

#if TORCH
        internal void EnqueueUpdate(long lcdEntityId, LcdSpriteRow[] rows, bool isAlert = false)
        {
            if (rows == null || rows.Length == 0) return;
            _queue.Enqueue(new LcdUpdate { EntityId = lcdEntityId, Rows = rows, IsAlert = isAlert });
            _logger?.Debug($"Enqueued LCD update for {lcdEntityId} rows={rows.Length} alert={isAlert}");
        }
#else
        internal void EnqueueUpdate(long lcdEntityId, string text, bool isAlert = false)
        {
            _queue.Enqueue(new LcdUpdate { EntityId = lcdEntityId, IsAlert = isAlert });
            _logger?.Debug($"Enqueued LCD stub for {lcdEntityId}");
        }
#endif

        // Must be called on game thread
        public void ApplyPendingUpdates()
        {
#if TORCH
            while (_queue.TryDequeue(out var upd))
            {
                try
                {
                    var ent     = MyAPIGateway.Entities.GetEntityById(upd.EntityId) as IMyEntity;
                    var surface = ent as IMyTextSurface;
                    if (surface == null) continue;

                    surface.ContentType          = ContentType.SCRIPT;
                    surface.BackgroundColor       = new Color(6, 6, 8); // TEXT_AND_IMAGE mode
                    surface.ScriptBackgroundColor = new Color(6, 6, 8); // SCRIPT mode (this is the one that matters)

                    var   size = surface.SurfaceSize;
                    float sc   = size.X / BASE;

                    // Measure total content height (in BASE units) to compute shrink-to-fit factor
                    float neededH = 0f;
                    foreach (var r in upd.Rows) neededH += GetRowHeightBase(r.RowKind);
                    float availH = size.Y / sc - PAD;
                    float fs     = availH < neededH ? Math.Max(0.4f, availH / neededH) : 1.0f;

                    // When shrink-to-fit would make content unreadably small, page instead.
                    var rowsToRender = upd.Rows;
                    if (fs < PAGE_FS_THRESHOLD)
                    {
                        fs           = 1.0f;
                        rowsToRender = GetPageRows(upd.EntityId, upd.Rows, availH);
                    }

                    float pad  = PAD     * sc * fs;
                    float rh   = ROW_H   * sc * fs;
                    float bh   = BAR_H   * sc * fs;
                    float iz   = ICON_SZ * sc * fs;
                    float x    = pad;
                    float y    = pad;
                    float w    = size.X - 2f * pad;

                    // Pre-calculate snap-to-bottom Y for trailing Footer rows.
                    float footerH = 0f;
                    for (int i = rowsToRender.Length - 1; i >= 0 && rowsToRender[i].RowKind == LcdSpriteRow.Kind.Footer; i--)
                        footerH += GetRowHeightBase(LcdSpriteRow.Kind.Footer) * sc * fs;
                    float bottomY = size.Y - pad - footerH;
                    bool snappedToBottom = false;

                    // Build sprite list — render to frame and optionally capture for snapshot.
                    bool capturing = _pendingSnapshots.ContainsKey(upd.EntityId);
                    var spriteList = new List<MySprite>();

                    foreach (var row in rowsToRender)
                    {
                        switch (row.RowKind)
                        {
                            case LcdSpriteRow.Kind.Header:
                            {
                                var s = MySprite.CreateText(row.Text, "White", row.TextColor, 0.85f * sc * fs, TextAlignment.LEFT);
                                s.Position = new Vector2(x, y);
                                spriteList.Add(s);
                                y += rh * 1.1f;
                                break;
                            }
                            case LcdSpriteRow.Kind.Separator:
                            {
                                spriteList.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple",
                                    new Vector2(x + w / 2f, y + sc * fs),
                                    new Vector2(w, Math.Max(1f, 2f * sc * fs)), new Color(55, 55, 60)));
                                y += 6f * sc * fs;
                                break;
                            }
                            case LcdSpriteRow.Kind.Item:
                            {
                                float tx = x;
                                if (row.IconSprite != null)
                                {
                                    spriteList.Add(new MySprite(SpriteType.TEXTURE, row.IconSprite,
                                        new Vector2(x + iz / 2f, y + iz / 2f),
                                        new Vector2(iz, iz), Color.White));
                                    tx = x + iz + 4f * sc * fs;
                                }
                                var ts = MySprite.CreateText(row.Text, "White", row.TextColor, 0.72f * sc * fs, TextAlignment.LEFT);
                                ts.Position = new Vector2(tx, y);
                                spriteList.Add(ts);
                                if (row.StatText != null)
                                {
                                    var st = MySprite.CreateText(row.StatText, "White", row.TextColor, 0.68f * sc * fs, TextAlignment.RIGHT);
                                    st.Position = new Vector2(x + w, y);
                                    spriteList.Add(st);
                                }
                                else if (row.ShowAlert)
                                {
                                    float badgeSz = rh * 0.82f;
                                    float bx = x + w - badgeSz * 0.5f;
                                    float by = y + rh * 0.5f - badgeSz * 1.03f;
                                    spriteList.Add(new MySprite(SpriteType.TEXTURE, "Triangle",
                                        new Vector2(bx, by), new Vector2(badgeSz, badgeSz), new Color(220, 30, 0)));
                                    spriteList.Add(new MySprite(SpriteType.TEXTURE, "Triangle",
                                        new Vector2(bx, by + badgeSz * 0.06f), new Vector2(badgeSz * 0.70f, badgeSz * 0.70f), new Color(6, 6, 8)));
                                    var al = MySprite.CreateText("!", "White", new Color(220, 30, 0), 0.75f * sc * fs, TextAlignment.CENTER);
                                    al.Position = new Vector2(bx, by - badgeSz * 0.33f);
                                    spriteList.Add(al);
                                }
                                y += rh;
                                break;
                            }
                            case LcdSpriteRow.Kind.Bar:
                            {
                                float fill  = Math.Max(0f, Math.Min(1f, row.BarFill));
                                float fillW = fill * w;
                                if (fillW > 1f)
                                    spriteList.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple",
                                        new Vector2(x + fillW / 2f, y + bh / 2f),
                                        new Vector2(fillW, bh), row.BarFillColor));
                                if (fillW < w - 1f)
                                    spriteList.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple",
                                        new Vector2(x + fillW + (w - fillW) / 2f, y + bh / 2f),
                                        new Vector2(w - fillW, bh), new Color(35, 35, 40)));
                                y += bh + 4f * sc * fs;
                                break;
                            }
                            case LcdSpriteRow.Kind.ItemBar:
                            {
                                float rowH  = rh * 1.15f;
                                float halfX = x + w / 2f; // bar starts at horizontal midpoint
                                float barW  = w / 2f;

                                // Left half: icon + name (no background, transparent)
                                float tx = x + 4f * sc * fs;
                                if (row.IconSprite != null)
                                {
                                    float isz = iz * 0.85f;
                                    spriteList.Add(new MySprite(SpriteType.TEXTURE, row.IconSprite,
                                        new Vector2(x + isz / 2f + 3f * sc * fs, y + rowH / 2f),
                                        new Vector2(isz, isz), Color.White));
                                    tx = x + isz + 7f * sc * fs;
                                }
                                float ty = y + rowH * 0.12f;
                                var nameColor = row.ShowAlert ? new Color(255, 160, 0) : Color.White;
                                var lt = MySprite.CreateText(row.Text ?? "", "White", nameColor, 0.68f * sc * fs, TextAlignment.LEFT);
                                lt.Position = new Vector2(tx, ty);
                                spriteList.Add(lt);
                                // Alert warning-triangle left of bar edge
                                if (row.ShowAlert)
                                {
                                    float badgeSz = rowH * 0.88f;
                                    float bx = halfX - 5f * sc * fs - badgeSz * 0.5f;
                                    float by = y + rowH * 0.5f - badgeSz * 0.03f;
                                    spriteList.Add(new MySprite(SpriteType.TEXTURE, "Triangle",
                                        new Vector2(bx, by), new Vector2(badgeSz, badgeSz), new Color(220, 30, 0)));
                                    spriteList.Add(new MySprite(SpriteType.TEXTURE, "Triangle",
                                        new Vector2(bx, by + badgeSz * 0.06f), new Vector2(badgeSz * 0.70f, badgeSz * 0.70f), new Color(6, 6, 8)));
                                    var al = MySprite.CreateText("!", "White", new Color(220, 30, 0), 0.75f * sc * fs, TextAlignment.CENTER);
                                    al.Position = new Vector2(bx, by + badgeSz * -0.33f);
                                    spriteList.Add(al);
                                }

                                // Right half: bar with stat text centered inside
                                float fill  = Math.Max(0f, Math.Min(1f, row.BarFill));
                                float fillW = fill * barW;
                                var fc = row.BarFillColor;
                                var fillColor = new Color((int)(fc.R * 0.45f), (int)(fc.G * 0.45f), (int)(fc.B * 0.45f), 200);
                                // Background track
                                spriteList.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple",
                                    new Vector2(halfX + barW / 2f, y + rowH / 2f),
                                    new Vector2(barW, rowH), new Color(30, 30, 35, 220)));
                                // Fill
                                if (fillW > 1f)
                                    spriteList.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple",
                                        new Vector2(halfX + fillW / 2f, y + rowH / 2f),
                                        new Vector2(fillW, rowH), fillColor));
                                // Stat text centered inside the bar
                                if (row.StatText != null)
                                {
                                    var rt = MySprite.CreateText(row.StatText, "White", Color.White, 0.62f * sc * fs, TextAlignment.CENTER);
                                    rt.Position = new Vector2(halfX + barW / 2f, ty);
                                    spriteList.Add(rt);
                                }
                                y += rowH + 2f * sc * fs;
                                break;
                            }
                            case LcdSpriteRow.Kind.Stat:
                            {
                                var s = MySprite.CreateText(row.Text, "White", row.TextColor, 0.68f * sc * fs, TextAlignment.LEFT);
                                s.Position = new Vector2(x, y);
                                spriteList.Add(s);
                                y += rh * 0.9f;
                                break;
                            }
                            case LcdSpriteRow.Kind.Footer:
                            {
                                if (!snappedToBottom) { y = Math.Max(y, bottomY); snappedToBottom = true; }
                                var textColor = row.TextColor.A > 0 ? row.TextColor : new Color(110, 110, 115);
                                var s = MySprite.CreateText(row.Text ?? "", "White", textColor, 0.6f * sc * fs, TextAlignment.LEFT);
                                s.Position = new Vector2(x, y);
                                spriteList.Add(s);
                                if (row.StatText != null)
                                {
                                    var st = MySprite.CreateText(row.StatText, "White", textColor, 0.6f * sc * fs, TextAlignment.RIGHT);
                                    st.Position = new Vector2(x + w, y);
                                    spriteList.Add(st);
                                }
                                y += rh * 0.85f;
                                break;
                            }
                        }
                    }

                    // Flush to frame
                    using (var frame = surface.DrawFrame())
                    {
                        foreach (var spr in spriteList)
                            frame.Add(spr);
                    }

                    // Capture for snapshot if requested — auto-write to file
                    if (capturing)
                    {
                        SnapshotCollect(upd.EntityId, spriteList);
                        if (!string.IsNullOrEmpty(_pluginDir))
                            _lastSnapshotPath = SnapshotLcd(upd.EntityId, _pluginDir);
                    }

                    _logger?.Debug($"LCD {upd.EntityId}: drew {upd.Rows.Length} rows alert={upd.IsAlert}");
                }
                catch (Exception ex)
                {
                    _logger?.Error($"LCD write failed for {upd.EntityId}: {ex.Message}");
                }
            }
#else
            while (_queue.TryDequeue(out var upd))
            {
                _logger?.Debug($"Applying LCD update to {upd.EntityId}");
            }
#endif
        }

        private class LcdUpdate
        {
            public long EntityId;
            public bool IsAlert;
#if TORCH
            public LcdSpriteRow[] Rows;
#else
            public string Text;
#endif
        }

#if TORCH
        // When shrink-to-fit would scale below this factor, paginate instead.
        private const float  PAGE_FS_THRESHOLD  = 0.65f;
        // Seconds each page is shown before advancing to the next.
        private const double PAGE_INTERVAL_SECS = 8.0;
        private readonly Dictionary<long, PageState> _pageStates = new Dictionary<long, PageState>();

        private class PageState
        {
            public int      PageIndex;
            public DateTime LastFlip;
            public PageState() { LastFlip = DateTime.UtcNow; }
        }

        // Returns the BASE-unit height contributed by a single row of the given kind.
        // Must stay in sync with the ItemBar/Header/etc. layout constants.
        private static float GetRowHeightBase(LcdSpriteRow.Kind k)
        {
            switch (k)
            {
                case LcdSpriteRow.Kind.Header:    return ROW_H * 1.1f;
                case LcdSpriteRow.Kind.Separator: return 6f;
                case LcdSpriteRow.Kind.Item:      return ROW_H;
                case LcdSpriteRow.Kind.Bar:       return BAR_H + 4f;
                case LcdSpriteRow.Kind.Stat:      return ROW_H * 0.9f;
                case LcdSpriteRow.Kind.Footer:    return ROW_H * 0.85f;
                case LcdSpriteRow.Kind.ItemBar:   return ROW_H * 1.15f;
                default:                          return ROW_H;
            }
        }

        // Splits rows into pages that each fit within availH (BASE units) at fs=1.
        // Header/Separator prefix and Footer suffix are pinned on every page.
        // The page index advances every PAGE_INTERVAL_SECS seconds.
        // Returns the rows to render for the current page, with "n/total" appended to
        // the Header text so the player knows more pages exist.
        private LcdSpriteRow[] GetPageRows(long entityId, LcdSpriteRow[] rows, float availH)
        {
            // Pinned prefix: leading Header and Separator rows shown on every page.
            int prefixEnd = 0;
            while (prefixEnd < rows.Length &&
                   (rows[prefixEnd].RowKind == LcdSpriteRow.Kind.Header ||
                    rows[prefixEnd].RowKind == LcdSpriteRow.Kind.Separator))
                prefixEnd++;

            // Pinned suffix: trailing Footer rows shown on every page.
            int suffixStart = rows.Length;
            while (suffixStart > prefixEnd &&
                   rows[suffixStart - 1].RowKind == LcdSpriteRow.Kind.Footer)
                suffixStart--;

            // Height budget available for paginated content on each page.
            float pinnedH = 0f;
            for (int i = 0; i < prefixEnd; i++)          pinnedH += GetRowHeightBase(rows[i].RowKind);
            for (int i = suffixStart; i < rows.Length; i++) pinnedH += GetRowHeightBase(rows[i].RowKind);
            float contentAvailH = availH - pinnedH;
            if (contentAvailH <= 0f) return rows;

            // Split content rows into pages.
            var pages = new List<List<int>>();
            int ci = prefixEnd;
            while (ci < suffixStart)
            {
                float pageH = 0f;
                var page = new List<int>();
                while (ci < suffixStart)
                {
                    float rowH = GetRowHeightBase(rows[ci].RowKind);
                    if (pageH + rowH > contentAvailH && page.Count > 0) break;
                    pageH += rowH;
                    page.Add(ci++);
                }
                pages.Add(page);
            }

            int totalPages = pages.Count;
            if (totalPages <= 1) return rows;

            // Advance page index on a timer.
            PageState ps;
            if (!_pageStates.TryGetValue(entityId, out ps))
            {
                ps = new PageState();
                _pageStates[entityId] = ps;
            }
            if ((DateTime.UtcNow - ps.LastFlip).TotalSeconds >= PAGE_INTERVAL_SECS)
            {
                ps.PageIndex = (ps.PageIndex + 1) % totalPages;
                ps.LastFlip  = DateTime.UtcNow;
            }
            int currentPage = ps.PageIndex % totalPages;

            // Assemble: prefix (header gets "n/total" appended) + page content + suffix.
            var result = new List<LcdSpriteRow>();
            for (int i = 0; i < prefixEnd; i++)
            {
                if (rows[i].RowKind == LcdSpriteRow.Kind.Header)
                {
                    var r = rows[i];
                    r.Text = $"{r.Text}  {currentPage + 1}/{totalPages}";
                    result.Add(r);
                }
                else
                {
                    result.Add(rows[i]);
                }
            }
            foreach (var idx in pages[currentPage]) result.Add(rows[idx]);
            for (int i = suffixStart; i < rows.Length; i++) result.Add(rows[i]);
            return result.ToArray();
        }

        // ── Snapshot helpers (for external SE sprite layout editor) ──────────────

        /// <summary>
        /// Requests a snapshot of the resolved sprites the next time the given LCD entity is rendered.
        /// <paramref name="label"/> is used as the output file name stem.
        /// </summary>
        internal void RequestSnapshot(long lcdEntityId, string label)
        {
            _pendingSnapshots[lcdEntityId] = label ?? "snapshot";
            _logger?.Info($"Snapshot requested for LCD {lcdEntityId} label='{label}'");
        }

        /// <summary>
        /// Called inside the draw loop after all sprites are added to the frame.
        /// If a snapshot is pending for this entity, captures the sprite list.
        /// </summary>
        private void SnapshotCollect(long entityId, List<MySprite> sprites)
        {
            if (!_pendingSnapshots.ContainsKey(entityId)) return;
            _capturedSprites[entityId] = new List<MySprite>(sprites);
        }

        /// <summary>
        /// Serialises the captured sprites into literal C# <c>new MySprite { ... }</c> code.
        /// Position/Size Vector2 values are stripped so the layout editor can re-resolve them.
        /// </summary>
        private string SerializeSnapshot(long entityId)
        {
            List<MySprite> sprites;
            if (!_capturedSprites.TryGetValue(entityId, out sprites) || sprites.Count == 0)
                return "// No sprites captured.";

            var sb = new StringBuilder();
            sb.AppendLine($"// Snapshot: {sprites.Count} sprite(s)");
            sb.AppendLine($"// Captured: {DateTime.UtcNow:u}");
            sb.AppendLine();
            sb.AppendLine("var sprites = new List<MySprite>");
            sb.AppendLine("{");
            for (int i = 0; i < sprites.Count; i++)
            {
                var s = sprites[i];
                sb.AppendLine("    new MySprite");
                sb.AppendLine("    {");
                sb.AppendLine($"        Type = SpriteType.{s.Type},");
                if (!string.IsNullOrEmpty(s.Data))
                    sb.AppendLine($"        Data = \"{s.Data}\",");
                if (s.Position.HasValue)
                    sb.AppendLine($"        Position = new Vector2({s.Position.Value.X:F1}f, {s.Position.Value.Y:F1}f),");
                if (s.Size.HasValue)
                    sb.AppendLine($"        Size = new Vector2({s.Size.Value.X:F1}f, {s.Size.Value.Y:F1}f),");
                if (s.Color.HasValue)
                {
                    var c = s.Color.Value;
                    sb.AppendLine($"        Color = new Color({c.R}, {c.G}, {c.B}, {c.A}),");
                }
                if (!string.IsNullOrEmpty(s.FontId))
                    sb.AppendLine($"        FontId = \"{s.FontId}\",");
                if (s.Alignment != TextAlignment.LEFT)
                    sb.AppendLine($"        Alignment = TextAlignment.{s.Alignment},");
                if (Math.Abs(s.RotationOrScale - 1f) > 0.001f)
                    sb.AppendLine($"        RotationOrScale = {s.RotationOrScale:F4}f,");
                sb.AppendLine("    },");
            }
            sb.AppendLine("};");
            return sb.ToString();
        }

        /// <summary>
        /// Writes the snapshot to a .cs file in the plugin folder and logs to NLog.
        /// Call from a chat command handler after the next render pass has run.
        /// </summary>
        internal string SnapshotLcd(long entityId, string pluginDir)
        {
            string label;
            _pendingSnapshots.TryRemove(entityId, out label);
            label = label ?? "snapshot";

            var code = SerializeSnapshot(entityId);

            // Clean up captured data
            List<MySprite> removed;
            _capturedSprites.TryRemove(entityId, out removed);

            // Write to file
            try
            {
                var safeName = label.Replace(' ', '_').Replace('\\', '_').Replace('/', '_')
                                    .Replace(':', '_').Replace('*', '_').Replace('?', '_')
                                    .Replace('"', '_').Replace('<', '_').Replace('>', '_').Replace('|', '_');
                var fileName = $"iml-snapshot-{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.cs";
                var filePath = Path.Combine(pluginDir, fileName);
                File.WriteAllText(filePath, code);
                _logger?.Info($"Snapshot written to {filePath}");
                return filePath;
            }
            catch (Exception ex)
            {
                _logger?.Error($"Snapshot file write failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Returns true if there is a pending (not yet rendered) snapshot for the given entity.</summary>
        internal bool HasPendingSnapshot(long entityId)
        {
            return _pendingSnapshots.ContainsKey(entityId);
        }

        /// <summary>Returns true if a snapshot has been captured and is ready to serialise.</summary>
        internal bool HasCapturedSnapshot(long entityId)
        {
            return _capturedSprites.ContainsKey(entityId);
        }
#endif
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

                    using (var frame = surface.DrawFrame())
                    {
                        foreach (var row in rowsToRender)
                        {
                            switch (row.RowKind)
                            {
                                case LcdSpriteRow.Kind.Header:
                                {
                                    var s = MySprite.CreateText(row.Text, "White", row.TextColor, 0.85f * sc * fs, TextAlignment.LEFT);
                                    s.Position = new Vector2(x, y);
                                    frame.Add(s);
                                    y += rh * 1.1f;
                                    break;
                                }
                                case LcdSpriteRow.Kind.Separator:
                                {
                                    frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple",
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
                                        frame.Add(new MySprite(SpriteType.TEXTURE, row.IconSprite,
                                            new Vector2(x + iz / 2f, y + iz / 2f),
                                            new Vector2(iz, iz), Color.White));
                                        tx = x + iz + 4f * sc * fs;
                                    }
                                    var ts = MySprite.CreateText(row.Text, "White", row.TextColor, 0.72f * sc * fs, TextAlignment.LEFT);
                                    ts.Position = new Vector2(tx, y);
                                    frame.Add(ts);
                                    if (row.StatText != null)
                                    {
                                        var st = MySprite.CreateText(row.StatText, "White", row.TextColor, 0.68f * sc * fs, TextAlignment.RIGHT);
                                        st.Position = new Vector2(x + w, y);
                                        frame.Add(st);
                                    }
                                    else if (row.ShowAlert)
                                    {
                                        float badgeSz = rh * 0.82f;
                                        float bx = x + w - badgeSz * 0.5f;
                                        float by = y + rh * 0.5f - badgeSz * 1.03f;
                                        frame.Add(new MySprite(SpriteType.TEXTURE, "Triangle",
                                            new Vector2(bx, by), new Vector2(badgeSz, badgeSz), new Color(220, 30, 0)));
                                        frame.Add(new MySprite(SpriteType.TEXTURE, "Triangle",
                                            new Vector2(bx, by + badgeSz * 0.06f), new Vector2(badgeSz * 0.70f, badgeSz * 0.70f), new Color(6, 6, 8)));
                                        var al = MySprite.CreateText("!", "White", new Color(220, 30, 0), 0.75f * sc * fs, TextAlignment.CENTER);
                                        al.Position = new Vector2(bx, by - badgeSz * 0.33f);
                                        frame.Add(al);
                                    }
                                    y += rh;
                                    break;
                                }
                                case LcdSpriteRow.Kind.Bar:
                                {
                                    float fill  = Math.Max(0f, Math.Min(1f, row.BarFill));
                                    float fillW = fill * w;
                                    if (fillW > 1f)
                                        frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple",
                                            new Vector2(x + fillW / 2f, y + bh / 2f),
                                            new Vector2(fillW, bh), row.BarFillColor));
                                    if (fillW < w - 1f)
                                        frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple",
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
                                        frame.Add(new MySprite(SpriteType.TEXTURE, row.IconSprite,
                                            new Vector2(x + isz / 2f + 3f * sc * fs, y + rowH / 2f),
                                            new Vector2(isz, isz), Color.White));
                                        tx = x + isz + 7f * sc * fs;
                                    }
                                    float ty = y + rowH * 0.12f;
                                    var nameColor = row.ShowAlert ? new Color(255, 160, 0) : Color.White;
                                    var lt = MySprite.CreateText(row.Text ?? "", "White", nameColor, 0.68f * sc * fs, TextAlignment.LEFT);
                                    lt.Position = new Vector2(tx, ty);
                                    frame.Add(lt);
                                    // Alert warning-triangle left of bar edge
                                    if (row.ShowAlert)
                                    {
                                        float badgeSz = rowH * 0.88f;
                                        float bx = halfX - 5f * sc * fs - badgeSz * 0.5f;
                                        float by = y + rowH * 0.5f - badgeSz * 0.03f;
                                        frame.Add(new MySprite(SpriteType.TEXTURE, "Triangle",
                                            new Vector2(bx, by), new Vector2(badgeSz, badgeSz), new Color(220, 30, 0)));
                                        frame.Add(new MySprite(SpriteType.TEXTURE, "Triangle",
                                            new Vector2(bx, by + badgeSz * 0.06f), new Vector2(badgeSz * 0.70f, badgeSz * 0.70f), new Color(6, 6, 8)));
                                        var al = MySprite.CreateText("!", "White", new Color(220, 30, 0), 0.75f * sc * fs, TextAlignment.CENTER);
                                        al.Position = new Vector2(bx, by + badgeSz * -0.33f);
                                        frame.Add(al);
                                    }

                                    // Right half: bar with stat text centered inside
                                    float fill  = Math.Max(0f, Math.Min(1f, row.BarFill));
                                    float fillW = fill * barW;
                                    var fc = row.BarFillColor;
                                    var fillColor = new Color((int)(fc.R * 0.45f), (int)(fc.G * 0.45f), (int)(fc.B * 0.45f), 200);
                                    // Background track
                                    frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple",
                                        new Vector2(halfX + barW / 2f, y + rowH / 2f),
                                        new Vector2(barW, rowH), new Color(30, 30, 35, 220)));
                                    // Fill
                                    if (fillW > 1f)
                                        frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple",
                                            new Vector2(halfX + fillW / 2f, y + rowH / 2f),
                                            new Vector2(fillW, rowH), fillColor));
                                    // Stat text centered inside the bar
                                    if (row.StatText != null)
                                    {
                                        var rt = MySprite.CreateText(row.StatText, "White", Color.White, 0.62f * sc * fs, TextAlignment.CENTER);
                                        rt.Position = new Vector2(halfX + barW / 2f, ty);
                                        frame.Add(rt);
                                    }
                                    y += rowH + 2f * sc * fs;
                                    break;
                                }
                                case LcdSpriteRow.Kind.Stat:
                                {
                                    var s = MySprite.CreateText(row.Text, "White", row.TextColor, 0.68f * sc * fs, TextAlignment.LEFT);
                                    s.Position = new Vector2(x, y);
                                    frame.Add(s);
                                    y += rh * 0.9f;
                                    break;
                                }
                                case LcdSpriteRow.Kind.Footer:
                                {
                                    if (!snappedToBottom) { y = Math.Max(y, bottomY); snappedToBottom = true; }
                                    var textColor = row.TextColor.A > 0 ? row.TextColor : new Color(110, 110, 115);
                                    var s = MySprite.CreateText(row.Text ?? "", "White", textColor, 0.6f * sc * fs, TextAlignment.LEFT);
                                    s.Position = new Vector2(x, y);
                                    frame.Add(s);
                                    if (row.StatText != null)
                                    {
                                        var st = MySprite.CreateText(row.StatText, "White", textColor, 0.6f * sc * fs, TextAlignment.RIGHT);
                                        st.Position = new Vector2(x + w, y);
                                        frame.Add(st);
                                    }
                                    y += rh * 0.85f;
                                    break;
                                }
                            }
                        }
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
#endif
    }
}

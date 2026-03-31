using System;
using System.Collections.Concurrent;
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
                    foreach (var r in upd.Rows)
                    {
                        switch (r.RowKind)
                        {
                            case LcdSpriteRow.Kind.Header:    neededH += ROW_H * 1.1f;  break;
                            case LcdSpriteRow.Kind.Separator: neededH += 6f;             break;
                            case LcdSpriteRow.Kind.Item:      neededH += ROW_H;          break;
                            case LcdSpriteRow.Kind.Bar:       neededH += BAR_H + 4f;     break;
                            case LcdSpriteRow.Kind.Stat:      neededH += ROW_H * 0.9f;  break;
                            case LcdSpriteRow.Kind.Footer:    neededH += ROW_H * 0.85f; break;
                            case LcdSpriteRow.Kind.ItemBar:   neededH += ROW_H * 1.15f; break;
                        }
                    }
                    float availH = size.Y / sc - PAD;
                    float fs     = availH < neededH ? Math.Max(0.4f, availH / neededH) : 1.0f;

                    float pad  = PAD     * sc * fs;
                    float rh   = ROW_H   * sc * fs;
                    float bh   = BAR_H   * sc * fs;
                    float iz   = ICON_SZ * sc * fs;
                    float x    = pad;
                    float y    = pad;
                    float w    = size.X - 2f * pad;

                    using (var frame = surface.DrawFrame())
                    {
                        foreach (var row in upd.Rows)
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
                                        var al = MySprite.CreateText("!", "White", new Color(255, 140, 0), 0.85f * sc * fs, TextAlignment.RIGHT);
                                        al.Position = new Vector2(x + w, y);
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
                                    // Alert "!" right-aligned against the bar edge
                                    if (row.ShowAlert)
                                    {
                                        var al = MySprite.CreateText("!", "White", new Color(255, 160, 0), 0.78f * sc * fs, TextAlignment.RIGHT);
                                        al.Position = new Vector2(halfX - 3f * sc * fs, ty);
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
                                    var s = MySprite.CreateText(row.Text, "White", new Color(110, 110, 115), 0.6f * sc * fs, TextAlignment.LEFT);
                                    s.Position = new Vector2(x, y);
                                    frame.Add(s);
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
    }
}

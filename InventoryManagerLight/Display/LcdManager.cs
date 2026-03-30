using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
#if TORCH
using Sandbox.ModAPI;
using VRage.ModAPI;
using VRageMath;
#endif

namespace InventoryManagerLight
{
    // Simple LCD manager to queue text updates to LCDs (or other displays).
    // This is a lightweight thread-safe queue; actual rendering must happen on game thread.
    public class LcdManager
    {
        private static readonly Lazy<LcdManager> _lazy = new Lazy<LcdManager>(() => new LcdManager());
        public static LcdManager Instance => _lazy.Value;

        private readonly ConcurrentQueue<LcdUpdate> _queue = new ConcurrentQueue<LcdUpdate>();
        private ILogger _logger;
        private static ILogger _pendingLogger;

        private LcdManager()
        {
            _logger = _pendingLogger ?? new DefaultLogger();
        }

        // Initialize global LCD manager with a specific logger. Call once during startup.
        public static void Initialize(ILogger logger)
        {
            _pendingLogger = logger;
            if (logger != null && _lazy.IsValueCreated)
            {
                _lazy.Value.SetLogger(logger);
            }
        }

        private void SetLogger(ILogger logger)
        {
            _logger = logger ?? new DefaultLogger();
        }

        public void EnqueueUpdate(long lcdEntityId, string text, bool isAlert = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            _queue.Enqueue(new LcdUpdate { EntityId = lcdEntityId, Text = text, IsAlert = isAlert });
            _logger?.Debug($"Enqueued LCD update for {lcdEntityId} len={text.Length} alert={isAlert}");
        }

        // Must be called on game thread
        public void ApplyPendingUpdates()
        {
#if TORCH
            while (_queue.TryDequeue(out var upd))
            {
                try
                {
                    var ent = MyAPIGateway.Entities.GetEntityById(upd.EntityId) as IMyEntity;
                    var panel = ent as IMyTextPanel;
                    if (panel == null) continue;
                    panel.WriteText(upd.Text);
                    // Always reset FontColor to white — SE multiplies the panel's FontColor by each
                    // embedded 0xE100 colour char, so any non-white base tint corrupts the palette.
                    // Alert state is communicated entirely through the embedded colour chars in the text.
                    panel.FontColor = Color.White;
                    _logger?.Debug($"LCD {upd.EntityId}: wrote {upd.Text?.Length ?? 0} chars alert={upd.IsAlert}");
                }
                catch (Exception ex)
                {
                    _logger?.Error($"LCD write failed for {upd.EntityId}: {ex.Message}");
                }
            }
#else
            while (_queue.TryDequeue(out var upd))
            {
                _logger?.Debug($"Applying LCD update to {upd.EntityId} len={upd.Text?.Length ?? 0}");
            }
#endif
        }

        private class LcdUpdate
        {
            public long EntityId;
            public string Text;
            public bool IsAlert;
        }
    }
}

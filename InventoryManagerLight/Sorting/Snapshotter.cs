using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace InventoryManagerLight
{
    // Snapshotter runs on game thread and enqueues lightweight snapshots for planning
    public class Snapshotter
    {
        private readonly ConcurrentQueue<InventorySnapshot[]> _queue;
        private readonly RuntimeConfig _config;
        private readonly ILogger _logger;

        public Snapshotter(ConcurrentQueue<InventorySnapshot[]> queue, RuntimeConfig config, ILogger logger)
        {
            _queue = queue;
            _config = config;
            _logger = logger ?? new DefaultLogger();
        }

        // Call from game thread periodically (e.g., each tick)
        // For the scaffold this method accepts a prebuilt snapshot array to enqueue
        public void EnqueueSnapshot(InventorySnapshot[] snapshot)
        {
            if (snapshot == null || snapshot.Length == 0) return;
            // filter/annotate snapshots by container tags using ContainerMatcher
            for (int i = 0; i < snapshot.Length; i++)
            {
                var s = snapshot[i];
                // s.ContainerName and ContainerCustomData should be set by caller (game thread) to the block's values
                var cats = ContainerMatcher.GetCategories(s.ContainerName, s.ContainerCustomData, _config.ContainerTagPrefix);
                if (cats.Length == 0)
                {
                    // mark amount 0 to indicate not managed (simple approach); caller may choose to skip entirely
                    // We'll leave snapshot entries unchanged but planner will use mapping to ignore unmanaged items.
                }
                // leave annotation for future improvements
            }
            _queue.Enqueue(snapshot);
        }
    }
}

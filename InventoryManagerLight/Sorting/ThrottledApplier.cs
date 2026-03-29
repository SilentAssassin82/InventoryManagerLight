using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace InventoryManagerLight
{
    // Applier must be invoked on the game thread. It consumes TransferBatch and applies them throttled.
    public class ThrottledApplier
    {
        private readonly ConcurrentQueue<TransferBatch> _batchQueue;
        private readonly RuntimeConfig _config;
        private readonly ConcurrentQueue<ReplanRequest> _replanQueue = new ConcurrentQueue<ReplanRequest>();
        private readonly IInventoryAdapter _adapter;
        private readonly ILogger _logger;
        private long _totalItemsMoved;
        private long _totalOpsCompleted;
        public long TotalItemsMoved { get { return Interlocked.Read(ref _totalItemsMoved); } }
        public long TotalOpsCompleted { get { return Interlocked.Read(ref _totalOpsCompleted); } }

        public ThrottledApplier(ConcurrentQueue<TransferBatch> batchQueue, RuntimeConfig config, ILogger logger = null)
        {
            _batchQueue = batchQueue;
            _config = config;
            _adapter = new DefaultInventoryAdapter();
            // create logger according to build; if provided use that and respect its enabled checkers
            _logger = logger ??
#if TORCH
                (ILogger)new NLogLogger(config.LoggingLevel);
#else
                (ILogger)new DefaultLogger(config.LoggingLevel);
#endif
        }

        // Allow injecting a real game adapter (useful in tests or when running in-game)
        public ThrottledApplier(ConcurrentQueue<TransferBatch> batchQueue, RuntimeConfig config, IInventoryAdapter adapter, ILogger logger = null)
        {
            _batchQueue = batchQueue;
            _config = config;
            _adapter = adapter ?? new DefaultInventoryAdapter();
            // keep provided adapter but create logger according to config
            _logger = logger ??
#if TORCH
                (ILogger)new NLogLogger(config.LoggingLevel);
#else
                (ILogger)new DefaultLogger(config.LoggingLevel);
#endif
        }

        // Call from game thread each tick
        public void Tick()
        {
            // Apply any pending LCD/display updates first (must run on game thread)
            LcdManager.Instance.ApplyPendingUpdates();

            var sw = Stopwatch.StartNew();
            int applied = 0;

            while (applied < _config.TransfersPerTick && sw.ElapsedMilliseconds < _config.MsBudgetPerTick)
            {
                if (!_batchQueue.TryPeek(out var batch)) break;
                if (batch == null)
                {
                    // consume and continue
                    _batchQueue.TryDequeue(out _);
                    continue;
                }

                // apply ops from the batch until we hit a limit
                int i = 0;
                while (i < batch.Ops.Count && applied < _config.TransfersPerTick && sw.ElapsedMilliseconds < _config.MsBudgetPerTick)
                {
                    var op = batch.Ops[i];
                    // actual calls to game API must happen here on the game thread.
                    var result = ApplyOp(op);
                    _logger.Debug($"ApplyOp attempt src:{op.SourceOwner} dst:{op.DestinationOwner} item:{op.ItemDefinitionId} amt:{op.Amount} -> {result.Status}/{result.Moved}");
                    if (result.Status == TransferStatus.Success)
                    {
                        // fully applied
                    }
                    else if (result.Status == TransferStatus.Partial)
                    {
                        // enqueue remaining portion for replanning
                        var remaining = new TransferOp { SourceOwner = op.SourceOwner, DestinationOwner = op.DestinationOwner, ItemDefinitionId = op.ItemDefinitionId, Amount = op.Amount - result.Moved };
                        var req = new ReplanRequest { RemainingOp = remaining, Reason = "PartialMove" };
                        _replanQueue.Enqueue(req);
                    }
                    else // failed
                    {
                        var req2 = new ReplanRequest { RemainingOp = op, Reason = "Failed" };
                        _replanQueue.Enqueue(req2);
                    }
                    i++;
                    applied++;
                }

                if (i >= batch.Ops.Count)
                {
                    // fully consumed
                    _batchQueue.TryDequeue(out _);
                }
                else
                {
                    // trim the batch
                    batch.Ops.RemoveRange(0, i);
                    break;
                }
            }
        }

        private void ApplyOpStub(TransferOp op)
        {
            // Place holder where MyInventory.TransferItemTo or similar would be called on the game thread.
            // Keep very small and fast.
        }

        // Replaces ApplyOpStub. Runs on game thread. Returns moved count and status.
        private TransferResult ApplyOp(TransferOp op)
        {
            // Source gone or already empty — op is vacuously satisfied.
            float available = 0f;
            if (!_adapter.TryGetTotalAmount(op.SourceOwner, op.ItemDefinitionId, out available) || available <= 0f)
            {
                return new TransferResult { Moved = 0f, Status = TransferStatus.Success };
            }

            float want = op.Amount;
            float toMove = Math.Min(want, available);
            // enforce max chunk
            toMove = Math.Min(toMove, _config.MaxTransferChunk);

            float moved = _adapter.Transfer(op.SourceOwner, op.DestinationOwner, op.ItemDefinitionId, toMove);
            // Treat 0-moved as Success (not Failed) so we don't re-enqueue the same
            // src→dest pair forever when the destination is full. The next op in the
            // batch targets a different destination and will carry the overflow.
            if (moved <= 0f) return new TransferResult { Moved = 0f, Status = TransferStatus.Success };
            Interlocked.Add(ref _totalItemsMoved, (long)moved);
            Interlocked.Increment(ref _totalOpsCompleted);
            if (moved < op.Amount) return new TransferResult { Moved = moved, Status = TransferStatus.Partial };
            return new TransferResult { Moved = moved, Status = TransferStatus.Success };
        }
    }
}

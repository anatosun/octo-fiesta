namespace octo_fiesta.Services.Common;

/// <summary>
/// Limits how many tracks are pulled from a provider account at once. Providers flag
/// accounts that fetch several tracks in parallel, which no official app does.
/// Waiting playback downloads are let in before waiting background ones.
/// </summary>
internal sealed class DownloadGate
{
    private readonly object _lock = new();
    private readonly LinkedList<Slot> _playbackQueue = new();
    private readonly LinkedList<Slot> _backgroundQueue = new();
    private int _free;

    public DownloadGate(int slots)
    {
        _free = Math.Max(1, slots);
    }

    /// <summary>
    /// Queues for a slot. Await <see cref="Slot.Granted"/>, and dispose the slot once the
    /// provider download is over (or abandoned) to let the next one in.
    /// </summary>
    public Slot Enter(bool playback)
    {
        var slot = new Slot(this);
        lock (_lock)
        {
            if (_free > 0)
            {
                _free--;
                slot.Grant();
            }
            else
            {
                slot.Node = (playback ? _playbackQueue : _backgroundQueue).AddLast(slot);
            }
        }
        return slot;
    }

    private void Promote(Slot slot)
    {
        lock (_lock)
        {
            if (slot.Node is { List: var queue } node && queue == _backgroundQueue)
            {
                _backgroundQueue.Remove(node);
                slot.Node = _playbackQueue.AddLast(slot);
            }
        }
    }

    private void Leave(Slot slot)
    {
        lock (_lock)
        {
            if (slot.Node is { List: { } queue } node)
            {
                // Still waiting: just drop out of the queue
                queue.Remove(node);
                slot.Node = null;
                return;
            }
            if (!slot.IsGranted)
            {
                return;
            }

            var queueToServe = _playbackQueue.Count > 0 ? _playbackQueue : _backgroundQueue;
            // Handed over under the lock, so the next slot is never seen neither queued nor granted.
            // Its continuations run asynchronously, outside this lock.
            if (queueToServe.First is { } first)
            {
                queueToServe.RemoveFirst();
                first.Value.Node = null;
                first.Value.Grant();
            }
            else
            {
                _free++;
            }
        }
    }

    public sealed class Slot : IDisposable
    {
        private readonly DownloadGate _gate;
        private readonly TaskCompletionSource _granted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        internal Slot(DownloadGate gate) => _gate = gate;

        internal LinkedListNode<Slot>? Node { get; set; }

        internal bool IsGranted => _granted.Task.IsCompleted;

        public Task Granted => _granted.Task;

        internal void Grant() => _granted.TrySetResult();

        /// <summary>Moves a waiting background slot into the playback queue.</summary>
        public void Promote() => _gate.Promote(this);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _gate.Leave(this);
            }
        }
    }
}

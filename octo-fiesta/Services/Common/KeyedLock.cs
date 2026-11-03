namespace octo_fiesta.Services.Common;

/// <summary>
/// One async mutex per key, created on demand and dropped once nobody holds or waits for it.
/// </summary>
public sealed class KeyedLock
{
    private readonly Dictionary<string, Entry> _entries = new();

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }
            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Release(key, entry, held: false);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private void Release(string key, Entry entry, bool held)
    {
        lock (_entries)
        {
            if (held)
            {
                entry.Semaphore.Release();
            }
            if (--entry.References == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int References;
    }

    private sealed class Releaser(KeyedLock owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(key, entry, held: true);
            }
        }
    }
}

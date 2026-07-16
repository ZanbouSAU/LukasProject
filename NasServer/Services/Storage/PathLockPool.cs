// NasServer/Services/Storage/PathLockPool.cs

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace NasServer.Services.Storage;

/// <summary>
/// 按路径串行化写操作的互斥锁池，作用同 <c>FileServer.Program.NameLocks</c>：
/// 防止并发请求同时写同一个目标文件把数据写坏。
///
/// 相比 FileServer 的版本做了改进：信号量带引用计数，最后一个使用者释放时从字典移除，
/// 长期运行不会因为路径数量增长而泄漏信号量。
/// </summary>
public sealed class PathLockPool
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
        public bool Removed;
    }

    private readonly ConcurrentDictionary<string, Entry> _locks = new(StringComparer.Ordinal);

    /// <summary>获取 <paramref name="key"/> 的互斥锁；释放返回的 <see cref="Releaser"/> 即解锁。</summary>
    public async ValueTask<Releaser> AcquireAsync(string key, CancellationToken ct = default)
    {
        Entry entry;
        while (true)
        {
            entry = _locks.GetOrAdd(key, static _ => new Entry());
            lock (entry)
            {
                if (entry.Removed)
                    continue; // 拿到的是刚被摘除的旧条目，重试取新条目。
                entry.RefCount++;
            }
            break;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            ReleaseRef(key, entry, releaseSemaphore: false);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private void ReleaseRef(string key, Entry entry, bool releaseSemaphore)
    {
        if (releaseSemaphore)
            entry.Semaphore.Release();

        lock (entry)
        {
            if (--entry.RefCount == 0)
            {
                entry.Removed = true;
                _locks.TryRemove(key, out _);
            }
        }
    }

    /// <summary>锁的持有凭据；Dispose 即释放锁并归还引用计数。</summary>
    public readonly struct Releaser(PathLockPool pool, string key, object entry) : IDisposable
    {
        public void Dispose() => pool.ReleaseRef(key, (Entry)entry, releaseSemaphore: true);
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MiniVault.Client.Internal;

/// <summary>An in-process, thread-safe cache of secrets keyed by name (ordinal comparison).</summary>
internal sealed class MemoryCache
{
    private readonly ConcurrentDictionary<string, CachedSecret> _entries = new ConcurrentDictionary<string, CachedSecret>(StringComparer.Ordinal);

    public bool TryGet(string name, out CachedSecret entry) => _entries.TryGetValue(name, out entry!);

    public void Set(CachedSecret entry)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        _entries[entry.Name] = entry;
    }

    public void Remove(string name) => _entries.TryRemove(name, out _);

    public IReadOnlyList<CachedSecret> Snapshot() => _entries.Values.ToList();
}

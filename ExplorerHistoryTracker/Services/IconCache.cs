using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;

namespace ExplorerHistoryTracker.Services
{
    /// <summary>
    /// Bounded LRU (Least Recently Used) cache for file/folder icons.
    /// Automatically evicts and disposes the least-recently-used Bitmap when the cache is full.
    /// This prevents unbounded memory growth from per-item icon caching.
    /// Thread-safe.
    /// </summary>
    public static class IconCache
    {
        /// <summary>
        /// Maximum number of cached icons. Covers visible + scrolled items comfortably.
        /// </summary>
        private const int MaxEntries = 64;

        private static readonly Dictionary<string, LinkedListNode<Entry>> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly LinkedList<Entry> _lruList = new();
        private static readonly object _lock = new();

        private sealed class Entry
        {
            public string Path;
            public Bitmap Bitmap;
        }

        /// <summary>
        /// Retrieves a cached icon, or creates and caches one via the provided factory.
        /// </summary>
        public static Bitmap? GetOrCreate(string path, Func<string, Bitmap?> factory)
        {
            if (string.IsNullOrEmpty(path)) return null;

            lock (_lock)
            {
                // Cache hit: move to front (most recently used)
                if (_cache.TryGetValue(path, out var node))
                {
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    return node.Value.Bitmap;
                }

                // Cache miss: create via factory
                var bitmap = factory(path);
                if (bitmap == null) return null;

                // Evict least recently used when at capacity
                while (_cache.Count >= MaxEntries && _lruList.Last != null)
                {
                    var last = _lruList.Last;
                    _lruList.RemoveLast();
                    _cache.Remove(last.Value.Path);
                    last.Value.Bitmap.Dispose();
                }

                // Store new entry
                var entry = new Entry { Path = path, Bitmap = bitmap };
                var newNode = _lruList.AddFirst(entry);
                _cache[path] = newNode;

                return bitmap;
            }
        }

        /// <summary>
        /// Removes a specific icon from the cache and disposes its bitmap.
        /// </summary>
        public static void Remove(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            lock (_lock)
            {
                if (_cache.TryGetValue(path, out var node))
                {
                    _lruList.Remove(node);
                    _cache.Remove(path);
                    node.Value.Bitmap.Dispose();
                }
            }
        }

        /// <summary>
        /// Clears all cached icons, disposing every bitmap.
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                foreach (var entry in _lruList)
                    entry.Bitmap.Dispose();

                _cache.Clear();
                _lruList.Clear();
            }
        }

        /// <summary>
        /// Returns the current number of cached entries (for diagnostics).
        /// </summary>
        public static int Count
        {
            get { lock (_lock) { return _cache.Count; } }
        }
    }
}

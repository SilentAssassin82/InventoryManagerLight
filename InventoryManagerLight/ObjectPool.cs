using System;
using System.Collections.Concurrent;

namespace InventoryManagerLight
{
    // Very small generic object pool for reusable objects
    public class ObjectPool<T> where T : class, new()
    {
        private readonly ConcurrentBag<T> _items = new ConcurrentBag<T>();
        private readonly ILogger _logger;

        public ObjectPool() : this(null) { }

        public ObjectPool(ILogger logger)
        {
            _logger = logger ?? new DefaultLogger();
        }

        public T Rent()
        {
            if (_items.TryTake(out var item)) return item;
            return new T();
        }

        public void Return(T item)
        {
            _items.Add(item);
            _logger?.Debug($"ObjectPool: returned instance of {typeof(T).Name}");
        }
    }
}

using System;
using System.Buffers;
using System.Threading;

namespace NdgrClientSharp.Utilities
{
    public struct PooledArray<T> : IDisposable
    {
        public T[] Array => _array ?? throw new ObjectDisposedException(nameof(PooledArray<T>));
        private readonly ArrayPool<T> _pool;
        private readonly int _length;

        private T[]? _array;
        private int _disposed;

        public int Length => _length;
        public Span<T> Span => Array.AsSpan(0, _length);

        public PooledArray(int length, ArrayPool<T>? pool = null)
        {
            _pool = pool ?? ArrayPool<T>.Shared;
            _array = _pool.Rent(length);
            _length = length;
            _disposed = 0;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                if (_array != null)
                {
                    _pool.Return(_array);
                    _array = null;
                }
            }
        }
    }
}
using System;
using System.Buffers;

namespace NdgrClientSharp.Utilities
{
    public readonly struct PooledArray<T> : IDisposable
    {
        public T[] Array { get; }
        private readonly ArrayPool<T> _pool;

        public PooledArray(int length, ArrayPool<T>? pool = null)
        {
            _pool = pool ?? ArrayPool<T>.Shared;
            Array = _pool.Rent(length);
        }

        public void Dispose()
        {
            _pool.Return(Array);
        }

        public Span<T> AsSpan() => Array.AsSpan();
        public Span<T> AsSpan(int start, int length) => Array.AsSpan(start, length);
    }
}
using System;
using System.Buffers;
using System.Threading;

namespace NdgrClientSharp.Utilities
{
    public struct PooledBuffer : IDisposable
    {
        public byte[] Array { get; }
        public int Length { get; }
        private readonly ArrayPool<byte> _pool;
        private int _disposed;

        public PooledBuffer(byte[] array, int length, ArrayPool<byte> pool)
        {
            Array = array;
            Length = length;
            _pool = pool;
            _disposed = 0;
        }

        public ReadOnlySpan<byte> Span => Array.AsSpan(0, Length);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _pool.Return(Array);
            }
        }
    }
}
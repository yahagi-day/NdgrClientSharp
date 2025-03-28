using System;
using System.Buffers;
using System.IO;

namespace NdgrClientSharp.Utilities
{
    internal sealed class NdgrProtobufStreamReader : IDisposable
    {
        private readonly MemoryStream _bufferStream = new MemoryStream();

        public void AddNewChunk(byte[] chunk)
        {
            _bufferStream.Write(chunk, 0, chunk.Length);
        }

        // 参考: https://github.com/protocolbuffers/protobuf/blob/384fabf35a9b5d1f1502edaf9a139b1f51551a01/csharp/src/Google.Protobuf/ParsingPrimitives.cs#L720-L732
        internal (int shift, uint result)? ReadVarint()
        {
            var result = 0;
            var offset = 0;
            var shift = 0;

            _bufferStream.Position = 0;

            for (; offset < 32; offset += 7)
            {
                int b = _bufferStream.ReadByte();
                if (b == -1)
                {
                    return null;
                }

                shift++;
                result |= (b & 0x7f) << offset;
                if ((b & 0x80) == 0)
                {
                    return (shift, (uint)result);
                }
            }

            for (; offset < 64; offset += 7)
            {
                int b = _bufferStream.ReadByte();
                if (b == -1)
                {
                    return null;
                }

                shift++;

                if ((b & 0x80) == 0)
                {
                    return (shift, (uint)result);
                }
            }

            // Varintの読み取りに失敗した
            throw new NdgrProtobufStreamReaderException("Failed to read varint from the stream.");
        }

        /// <summary>
        /// Bufferの先頭からVarintを読み取り、その値に基づいてバッファからメッセージを取り出す
        /// falseが返却された場合は、バッファに十分なデータが存在しないことを示す
        /// </summary>
        /// <param name="messageBuffer">戻り値がtrueの場合はProtoBuffのバイナリ列が格納されている</param>
        /// <returns>正常に読み取れたか、読み取れた場合はバイトサイズも返す</returns>
        /// <exception cref="NdgrProtobufStreamReaderException"></exception>
        public (bool isValid, int messageSize) UnshiftChunk(byte[] messageBuffer)
        {
            var readVarint = ReadVarint();
            if (readVarint == null) return (false, 0);

            var (offset, varint) = readVarint.Value;

            if (offset + varint > _bufferStream.Length)
            {
                return (false, 0);
            }

            if (varint > messageBuffer.Length)
            {
                // 渡されたバッファ以上のサイズのメッセージが存在する場合は例外を投げる
                throw new NdgrProtobufStreamReaderException("Message size is too large.");
            }


            // Read the message bytes directly into the array
            _bufferStream.Position = offset;
            _bufferStream.Read(messageBuffer, 0, (int)varint);

            // Shift the buffer content
            var remainingLength = (int)(_bufferStream.Length - offset - varint);
            var rentedBuffer = ArrayPool<byte>.Shared.Rent(remainingLength);

            try
            {
                _bufferStream.Position = offset + varint;
                _bufferStream.Read(rentedBuffer, 0, remainingLength);

                // Reset and refill the stream with the remaining data
                _bufferStream.SetLength(0);
                _bufferStream.Write(rentedBuffer, 0, remainingLength);
            }
            finally
            {
                // Return the rented buffer to the pool
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }

            return (true, (int)varint);
        }

        public void Dispose()
        {
            _bufferStream.Dispose();
        }
    }

    public class NdgrProtobufStreamReaderException : Exception
    {
        public NdgrProtobufStreamReaderException(string message) : base(message)
        {
        }
    }
}
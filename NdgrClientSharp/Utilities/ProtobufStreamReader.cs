using System;
using System.IO;

namespace NdgrClientSharp.Utilities
{
    // このコードを参考にC#用に実装したもの
    // https://github.com/rinsuki-lab/ndgr-reader/blob/main/src/protobuf-stream-reader.ts
    internal sealed class ProtobufStreamReader : IDisposable
    {
        private readonly MemoryStream _bufferStream = new MemoryStream();

        public void AddNewChunk(byte[] chunk)
        {
            _bufferStream.Write(chunk, 0, chunk.Length);
        }

        //Googleの公式実装を参考
        // https://github.com/protocolbuffers/protobuf/blob/384fabf35a9b5d1f1502edaf9a139b1f51551a01/csharp/src/Google.Protobuf/ParsingPrimitives.cs#L720-L732
        private (int offset, int result)? ReadVarint()
        {
            var offset = 0;
            var result = 0;
            var shift = 0;

            _bufferStream.Position = 0;

            //Byteを読み込んで先頭が1かチェック(1bitから32bitの範囲)
            for (; shift < 32; shift += 7)
            {
                var b = _bufferStream.ReadByte();
                {
                    if (b == -1)
                    {
                        return null;
                    }
                }
                result |= (b & 0x7F) << shift;
                offset++;
                
                if ((b & 0x80) == 0)
                {
                    return (offset, result);
                }
            }
            //ない場合は64bitまで読み範囲内に0x80があれば1~32bitを渡す
            for (; shift < 64; shift += 7)
            {
                var b = _bufferStream.ReadByte();
                if (b == -1)
                {
                    return null;
                }

                if ((b & 0x80) == 0)
                {
                    return (offset, result);
                }
            }
            
            //ない場合は不正な値 例外を通知したところでハンドリングのしようがないので握りつぶしてnullを返す
            return null;
        }

        public byte[]? UnshiftChunk()
        {
            var readVariant = ReadVarint();
            if (readVariant == null) return null;

            var (offset, variant) = readVariant.Value;

            if (offset + variant > _bufferStream.Length)
            {
                return null;
            }

            var message = new byte[variant];

            // Read the message bytes directly into the array
            _bufferStream.Position = offset;
            _bufferStream.Read(message, 0, variant);

            // Shift the buffer content
            var remainingBuffer = new byte[_bufferStream.Length - offset - variant];
            _bufferStream.Position = offset + variant;
            _bufferStream.Read(remainingBuffer, 0, remainingBuffer.Length);

            // Reset and refill the stream with the remaining data
            _bufferStream.SetLength(0);
            _bufferStream.Write(remainingBuffer, 0, remainingBuffer.Length);

            return message;
        }

        public void Dispose()
        {
            _bufferStream.Dispose();
        }
    }
}
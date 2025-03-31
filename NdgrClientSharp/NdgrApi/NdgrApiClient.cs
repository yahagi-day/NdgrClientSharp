using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Dwango.Nicolive.Chat.Service.Edge;
using NdgrClientSharp.Utilities;
using ReadyForNext = Dwango.Nicolive.Chat.Service.Edge.ChunkedEntry.Types.ReadyForNext;

namespace NdgrClientSharp.NdgrApi
{
    public class NdgrApiClient : INdgrApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly bool _needDisposeHttpClient;
        private readonly CancellationTokenSource _mainCts = new CancellationTokenSource();

        public NdgrApiClient()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "ndgr-client-sharp");
            _needDisposeHttpClient = true;
        }

        /// <summary>
        /// HttpClientを指定して初期化する
        /// このHttpClientのDisposeはNdgrApiClientでは行わない
        /// </summary>
        public NdgrApiClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _needDisposeHttpClient = false;
        }

        private static string TrimQuery(string uri)
        {
            // URIからクエリパラメータを除外
            return uri.Split('?')[0];
        }


        #region view api

        /// <summary>
        /// コメント取得の起点
        /// GET /api/view/v4/:view?at=now を実行して、次のコメント取得のための情報を取得する
        /// クエリパラメータはTrimしてから実行するため含まれていても問題ない
        /// </summary>
        public async ValueTask<ReadyForNext> FetchViewAtNowAsync(string viewApiUri, CancellationToken token = default)
        {
            var ct = CreateLinkedToken(token);

            // URIからクエリパラメータを除外
            var uri = TrimQuery(viewApiUri);

            var response = await _httpClient.GetAsync(
                $"{uri}?at=now",
                HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new NdgrApiClientHttpException(response.StatusCode);
            }


            await foreach (var chunk in ReadProtoBuffBytesAsync(await response.Content.ReadAsStreamAsync())
                               .WithCancellation(ct))
            {
                var message = ParseChunkedEntry(chunk.message, chunk.messageSize);

                // at=nowの場合はほぼ現在時刻のunixtimeが返ってくる
                return message.Next;
            }

            throw new NdgrApiClientByteReadException("Failed to read bytes from stream.");
        }

        private static ChunkedEntry ParseChunkedEntry(byte[] message, int messageSize)
        {
            var messageSpan = new ReadOnlySpan<byte>(message, 0, messageSize);
            return ChunkedEntry.Parser.ParseFrom(messageSpan);
        }


        /// <summary>
        /// 指定時刻付近のコメント取得のための情報を取得する
        /// backward,previous,segment,nextが非同期的に返ってくる
        /// </summary>
        public async IAsyncEnumerable<ChunkedEntry> FetchViewAtAsync(string viewApiUri,
            long unixTime,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            var ct = CreateLinkedToken(token);

            var uri = TrimQuery(viewApiUri);
            var response =
                await _httpClient.GetAsync(
                    $"{uri}?at={unixTime}",
                    HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new NdgrApiClientHttpException(response.StatusCode);
            }

            var stream = await response.Content.ReadAsStreamAsync();
            await foreach (var chunk in ReadProtoBuffBytesAsync(stream, ct))
            {
                var message = ParseChunkedEntry(chunk.message, chunk.messageSize);
                yield return message;
            }
        }

        #endregion


        /// <summary>
        /// ChunkedMessage（コメント）を取得する
        /// Segment, Previous APIが対応
        /// </summary>
        public async IAsyncEnumerable<ChunkedMessage> FetchChunkedMessagesAsync(
            string apiUri,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            var ct = CreateLinkedToken(token);

#if NETSTANDARD2_1
            await using var response = await _httpClient.GetStreamAsync(apiUri);
#else
            using var response = await _httpClient.GetStreamAsync(apiUri);
#endif
            await foreach (var chunk in ReadProtoBuffBytesAsync(response, ct))
            {
                ct.ThrowIfCancellationRequested();
                ChunkedMessage message;
                try
                {
                    message = ParseChunkedMessage(chunk.message, chunk.messageSize);
                }
                catch
                {
                    // ignore...
                    continue;
                }

                yield return message;
            }
        }

        private static ChunkedMessage ParseChunkedMessage(byte[] message, int messageSize)
        {
            var messageSpan = new ReadOnlySpan<byte>(message, 0, messageSize);
            return ChunkedMessage.Parser.ParseFrom(messageSpan);
        }


        /// <summary>
        /// PackedSegmentを取得する
        /// </summary>
        public async ValueTask<PackedSegment> FetchPackedSegmentAsync(
            string uri,
            CancellationToken token = default)
        {
            var ct = CreateLinkedToken(token);
            var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var message = PackedSegment.Parser.ParseFrom(await response.Content.ReadAsStreamAsync());
            return message;
        }

        #region read bytes utils

        /// <summary>
        /// Streamから非同期的に生のバイト配列を読み取る
        /// </summary>
        private static async IAsyncEnumerable<PooledBuffer> ReadRawBytesAsync(
            Stream stream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var pool = ArrayPool<byte>.Shared;

            while (true)
            {
                var buffer = pool.Rent(1024);
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                }
                catch
                {
                    pool.Return(buffer);
                    throw;
                }

                if (read == 0)
                {
                    pool.Return(buffer); 
                    yield break;
                }

                yield return new PooledBuffer(buffer, read, pool);
            }
        }

        /// <summary>
        /// Streamを「ProtoBuffのメッセージとして解釈可能なバイト配列単位」で読み取る
        /// </summary>
        private static async IAsyncEnumerable<(byte[] message, int messageSize)> ReadProtoBuffBytesAsync(
            Stream stream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // 正常に動作している場合はこのサイズで十分足りるはずであり、オーバーする場合は受信したデータが破損している可能性がある
            using var messageBuffer = new PooledArray<byte>(1024);

            using var reader = new NdgrProtobufStreamReader();
            await foreach (var chunk in ReadRawBytesAsync(stream).WithCancellation(ct))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    reader.AddNewChunk(chunk.Array, chunk.Length);
                }
                finally
                {
                    chunk.Dispose();
                }

                while (true)
                {
                    int size;
                    try
                    {
                        bool isValid;
                        (isValid, size) = reader.UnshiftChunk(messageBuffer.Array);
                        if (!isValid)
                        {
                            break;
                        }
                    }
                    catch (NdgrProtobufStreamReaderException)
                    {
                        throw new NdgrApiClientByteReadException("Failed to read varint from the stream.");
                    }

                    yield return (messageBuffer.Array, size);
                }
            }
        }

        #endregion

        private CancellationToken CreateLinkedToken(CancellationToken ct)
        {
            if (!ct.CanBeCanceled) return _mainCts.Token;
            return CancellationTokenSource.CreateLinkedTokenSource(_mainCts.Token, ct).Token;
        }

        public void Dispose()
        {
            if (_needDisposeHttpClient)
            {
                _httpClient.Dispose();
            }

            _mainCts.Cancel();
            _mainCts.Dispose();
        }
    }


    public sealed class NdgrApiClientByteReadException : NdgrApiClientException
    {
        public NdgrApiClientByteReadException(string message) : base(message)
        {
        }
    }


    public sealed class NdgrApiClientHttpException : Exception
    {
        public HttpStatusCode HttpStatusCode { get; }

        public NdgrApiClientHttpException(HttpStatusCode httpStatusCode)
        {
            HttpStatusCode = httpStatusCode;
        }

        public override string ToString()
        {
            return $"{base.ToString()}, {nameof(HttpStatusCode)}: {HttpStatusCode}";
        }
    }
}
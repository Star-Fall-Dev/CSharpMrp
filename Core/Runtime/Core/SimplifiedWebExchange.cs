using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CSharpMrp
{
    // One bounded HTTP request per guest proxy socket. Workers never access the VM.
    internal sealed class SimplifiedWebExchange : IDisposable
    {
        private const int MaximumRequest = 32 * 1024;
        private const int MaximumBody = 512 * 1024;
        private readonly MemoryStream request = new MemoryStream();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Task<Result>? response;
        private int offset;
        private bool reported;
        private readonly bool uc;

        internal SimplifiedWebExchange(bool uc = false)
        {
            this.uc = uc;
        }

        private sealed class Result
        {
            internal readonly byte[] Bytes;
            internal readonly string Summary;

            internal Result(string wml, string summary, bool uc, string address)
            {
                byte[] body = uc ? UcWebPage.Encode(wml, address) : Encoding.UTF8.GetBytes(wml);
                byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n" +
                    (uc ? "Status: 200\r\nContent-Type: application/octet-stream\r\n" :
                        "Content-Type: text/vnd.wap.wml; charset=utf-8\r\n") +
                    "Content-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                Bytes = new byte[header.Length + body.Length];
                Buffer.BlockCopy(header, 0, Bytes, 0, header.Length);
                Buffer.BlockCopy(body, 0, Bytes, header.Length, body.Length);
                Summary = summary;
            }
        }

        internal static bool IsRequestPrefix(byte[] bytes)
        {
            const string prefix = "GET ";
            for (int index = 0; index < Math.Min(bytes.Length, prefix.Length); index++)
            {
                if (bytes[index] != prefix[index])
                {
                    return false;
                }
            }
            return true;
        }

        internal uint Send(byte[] bytes)
        {
            if (response != null || request.Length + bytes.Length > MaximumRequest)
            {
                return uint.MaxValue;
            }
            request.Write(bytes, 0, bytes.Length);
            byte[] buffer = request.GetBuffer();
            int length = (int)request.Length;
            for (int index = Math.Max(0, length - bytes.Length - 3); index + 3 < length; index++)
            {
                if (buffer[index] == 13 && buffer[index + 1] == 10 &&
                    buffer[index + 2] == 13 && buffer[index + 3] == 10)
                {
                    byte[] complete = request.ToArray();
                    cancellation.CancelAfter(20000);
                    CancellationToken token = cancellation.Token;
                    response = Task.Run(() => Fetch(complete, token, uc));
                    _ = response.ContinueWith(static completed =>
                    {
                        _ = completed.Exception;
                    }, TaskContinuationOptions.OnlyOnFaulted);
                    break;
                }
            }
            return (uint)bytes.Length;
        }

        internal uint Receive(GuestMemory memory, uint pointer, int length, out string? summary)
        {
            summary = null;
            if (response == null || !response.IsCompleted)
            {
                return 0;
            }
            Result result = response.GetAwaiter().GetResult();
            if (!reported)
            {
                summary = result.Summary;
                reported = true;
            }
            int count = Math.Min(length, result.Bytes.Length - offset);
            memory.Write(pointer, result.Bytes, offset, count);
            offset += count;
            return (uint)count;
        }

        private static async Task<Result> Fetch(byte[] requestBytes, CancellationToken token, bool uc)
        {
            string address = "";
            try
            {
                string requestText = Encoding.ASCII.GetString(requestBytes);
                string[] lines = requestText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] first = lines[0].Split(' ');
                if (uc)
                {
                    if (!UcWebPage.TryRequest(requestBytes, out address))
                    {
                        throw new IOException("未识别的 UC 浏览请求。");
                    }
                }
                else if (first.Length != 3 || first[0] != "GET" || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                {
                    throw new IOException("只支持普通 GET 网页请求。");
                }
                else
                {
                    address = first[1];
                }
                Uri target = SimplifiedWebPage.Target(address);
                string userAgent = "MAUI WAP Browser";
                foreach (string line in lines)
                {
                    if (line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))
                    {
                        userAgent = line.Substring(11).Trim();
                    }
                    if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) ||
                        !uc && line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                        line.Substring(15).Trim() != "0")
                    {
                        throw new IOException("简化浏览不支持上传请求。");
                    }
                }
                using var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                for (int redirect = 0; redirect <= 8; redirect++)
                {
                    token.ThrowIfCancellationRequested();
                    using var message = new HttpRequestMessage(HttpMethod.Get, target);
                    message.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                    message.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,text/vnd.wap.wml,text/plain");
                    using HttpResponseMessage reply = await client.SendAsync(message,
                        HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    int status = (int)reply.StatusCode;
                    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                    {
                        if (redirect == 8 || reply.Headers.Location == null)
                        {
                            throw new IOException("网页重定向次数过多或缺少目标地址。");
                        }
                        target = SimplifiedWebPage.Validate(new Uri(target, reply.Headers.Location));
                        continue;
                    }
                    if (reply.Content.Headers.ContentLength > MaximumBody)
                    {
                        throw new IOException("网页超过简化浏览的 512 KB 大小上限。");
                    }
                    string media = reply.Content.Headers.ContentType?.MediaType ?? "text/html";
                    if (!media.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
                        media != "application/xhtml+xml" && media != "application/vnd.wap.xhtml+xml")
                    {
                        throw new IOException("此内容类型不支持简化显示：" + media);
                    }
                    using Stream input = await reply.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var body = new MemoryStream();
                    var chunk = new byte[8192];
                    while (true)
                    {
                        int count = await input.ReadAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false);
                        if (count == 0)
                        {
                            break;
                        }
                        if (body.Length + count > MaximumBody)
                        {
                            throw new IOException("解压后的网页超过 512 KB 大小上限。");
                        }
                        body.Write(chunk, 0, count);
                    }
                    byte[] data = body.ToArray();
                    string? charset = reply.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
                    if (string.IsNullOrEmpty(charset))
                    {
                        string prefix = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 8192));
                        Match match = Regex.Match(prefix, @"charset\s*=\s*[""']?\s*([a-zA-Z0-9_-]+)",
                            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                        charset = match.Success ? match.Groups[1].Value : "utf-8";
                    }
                    Encoding encoding = Encoding.GetEncoding(charset, EncoderFallback.ExceptionFallback,
                        DecoderFallback.ExceptionFallback);
                    string html = encoding.GetString(data);
                    string wml = SimplifiedWebPage.Convert(html, target, status, media == "text/plain");
                    return new Result(wml, $"{(uc ? "UC " : "")}simplified web HTTP {status}: {target}; " +
                        $"source={data.Length}, wml={Encoding.UTF8.GetByteCount(wml)}", uc, address);
                }
                throw new IOException("网页重定向失败。");
            }
            catch (Exception error) when (error is HttpRequestException || error is IOException ||
                error is OperationCanceledException || error is ArgumentException || error is FormatException ||
                error is NotSupportedException || error is RegexMatchTimeoutException)
            {
                string reason = error is OperationCanceledException ? "网页获取超时或已取消。" : error.Message;
                string wml = SimplifiedWebPage.Error(reason);
                return new Result(wml, $"{(uc ? "UC " : "")}simplified web failed: {address}; " +
                    $"{error.GetType().Name}: {error.Message}", uc, address);
            }
        }

        public void Dispose()
        {
            request.Dispose();
            cancellation.Cancel();
            if (response == null)
            {
                cancellation.Dispose();
            }
            else
            {
                _ = response.ContinueWith(static (_, state) =>
                {
                    ((CancellationTokenSource)state!).Dispose();
                }, cancellation, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
}

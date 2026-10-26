using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Reptar.Hosting;

public static class KestrelHost
{
    public static async Task RunAsync(this ReptarApp app, string[]? args = null)
    {
        if (args is { Length: > 0 })
        {
            if (args.Contains("--schema", StringComparer.OrdinalIgnoreCase))
            {
                var outIdx = Array.IndexOf(args, "--out");
                if (outIdx >= 0 && outIdx + 1 < args.Length)
                {
                    var outPath = Path.GetFullPath(args[outIdx + 1]);
                    var dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(outPath, app.ExportSchemaJson());
                }
                else
                {
                    Console.WriteLine(app.ExportSchemaJson());
                }
                return;
            }

            if (string.Equals(args[0], "db", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[0], "--db", StringComparison.OrdinalIgnoreCase))
            {
                var exitCode = await app.RunMigrationCliAsync(args);
                Environment.ExitCode = exitCode;
                return;
            }
        }

        var builder = WebApplication.CreateSlimBuilder(args ?? []);
        if (Environment.GetEnvironmentVariable("BENCH_MIDDLEWARE") != "1")
        {
            builder.Logging.ClearProviders();
        }

        // Kestrel-only response compression. Cloudflare compresses at the edge;
        // this covers containers/VPS. SSE is intentionally excluded.
        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.MimeTypes =
            [
                "application/json",
                "application/problem+json",
                "text/plain",
                "text/html",
                "text/css",
                "application/javascript",
                "text/javascript",
                "image/svg+xml",
            ];
        });

        var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var configuredPort)
            ? configuredPort
            : 8787;

        // HOST pins the listen address (e.g. HOST=127.0.0.1 or HOST=0.0.0.0).
        // On macOS 26 + .NET 10, dual-stack (IPv6) listeners can crash or stop
        // accepting when aborted connects return a zero-length sockaddr from
        // accept(2) — see dotnet/runtime#121848 and #130284. Binding IPv4
        // (HOST=0.0.0.0) avoids the AF_INET6 accept path entirely.
        var host = Environment.GetEnvironmentVariable("HOST");

        builder.WebHost.ConfigureKestrel(options =>
        {
            if (host is not null && System.Net.IPAddress.TryParse(host, out var address))
            {
                options.Listen(address, port);
            }
            else
            {
                options.ListenAnyIP(port);
            }
        });

        var web = builder.Build();

        // One-line identity banner: in multi-agent/multi-process repos,
        // knowing exactly which build is on which port (and its pid) is the
        // difference between debugging and ghost-hunting.
        Console.WriteLine(
            $"Reptar listening on http://{(host is null ? "[any]" : host)}:{port} " +
            $"(pid {Environment.ProcessId}, .NET {Environment.Version})");

        var kestrelTransport = new KestrelWebSocketTransport();
        app.SetWebSocketTransport(kestrelTransport);

        web.UseResponseCompression();
        web.UseWebSockets();

        web.Use(async (HttpContext http, RequestDelegate _) =>
        {
            var path = http.Request.Path.Value ?? "/";
            if (http.WebSockets.IsWebSocketRequest && app.HasWebSocketHandler(path))
            {
                string? subProtocol = null;
                if (http.WebSockets.WebSocketRequestedProtocols.Contains("actioncable-v1-json"))
                {
                    subProtocol = "actioncable-v1-json";
                }

                using var socket = await http.WebSockets.AcceptWebSocketAsync(subProtocol);
                var sessionId = Guid.NewGuid().ToString("N");
                kestrelTransport.Register(sessionId, socket);

                var wsRequest = ToWebSocketReptarRequest(http);
                await app.HandleWebSocketOpenAsync(sessionId, wsRequest);

                // Matches the Workers runtime's inbound WebSocket frame cap so
                // both hosts reject oversized messages instead of letting a
                // single connection accumulate unbounded memory.
                const int MaxWebSocketMessageBytes = 1024 * 1024;

                var buffer = new byte[8192];
                try
                {
                    while (socket.State == System.Net.WebSockets.WebSocketState.Open)
                    {
                        using var ms = new MemoryStream();
                        System.Net.WebSockets.WebSocketReceiveResult result;
                        var overLimit = false;
                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), http.RequestAborted);
                            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                            ms.Write(buffer, 0, result.Count);

                            if (ms.Length > MaxWebSocketMessageBytes)
                            {
                                overLimit = true;
                                break;
                            }
                        } while (!result.EndOfMessage);

                        if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                        {
                            break;
                        }

                        if (overLimit)
                        {
                            try
                            {
                                await socket.CloseAsync(
                                    System.Net.WebSockets.WebSocketCloseStatus.MessageTooBig,
                                    "Frame exceeds 1 MiB limit",
                                    http.RequestAborted);
                            }
                            catch
                            {
                                // Socket already aborted by the peer.
                            }
                            break;
                        }

                        var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                        await app.HandleWebSocketMessageAsync(sessionId, text);
                    }
                }
                catch
                {
                    // Client disconnected or socket aborted
                }
                finally
                {
                    await app.HandleWebSocketCloseAsync(sessionId);
                    kestrelTransport.Unregister(sessionId);
                }

                return;
            }

            var maxBodyBytes = app.GetMaxBodyBytes(http.Request.Method, http.Request.Path.Value ?? "/");
            var request = await ToReptarRequest(http, maxBodyBytes);
            var response = await app.HandleAsync(request);
            await WriteResponse(http, response);
        });

        await app.StartModulesAsync();

        // Background workers: the queue poller drains the configured
        // consumer adapter and the cron scheduler evaluates OnScheduled
        // expressions. Workers never needs these — the platform invokes you.
        var backgroundCts = new CancellationTokenSource();
        web.Lifetime.ApplicationStopping.Register(() => backgroundCts.Cancel());

        if (app.Queues is IQueueConsumer && app.QueueNames.Count > 0)
        {
            Console.WriteLine(
                $"Reptar queue poller: {app.QueueNames.Count} queue(s), " +
                $"interval {EnvInt("REPTAR_QUEUE_POLL_SECS", 2)}s, " +
                $"batch {EnvInt("REPTAR_QUEUE_BATCH", 25)}");
            _ = Task.Run(() => PollQueuesAsync(app, backgroundCts.Token), CancellationToken.None);
        }

        if (app.CronExpressions.Count > 0)
        {
            Console.WriteLine(
                $"Reptar cron scheduler: {app.CronExpressions.Count} schedule(s), " +
                $"tick {EnvInt("REPTAR_CRON_TICK_SECS", 15)}s");
            _ = Task.Run(() => RunCronAsync(app, backgroundCts.Token), CancellationToken.None);
        }

        try
        {
            await web.RunAsync();
        }
        finally
        {
            backgroundCts.Cancel();
            await app.DisposeAsync();
        }
    }

    private static async Task PollQueuesAsync(ReptarApp app, CancellationToken ct)
    {
        if (app.Queues is not IQueueConsumer consumer)
            return;

        var queues = app.QueueNames;
        if (queues.Count == 0)
            return;

        if (consumer is DbQueueClient dbQueue)
            await dbQueue.EnsureCreatedAsync();

        var pollSeconds = Math.Max(1, EnvInt("REPTAR_QUEUE_POLL_SECS", 2));
        var batchSize = Math.Max(1, EnvInt("REPTAR_QUEUE_BATCH", 25));

        while (!ct.IsCancellationRequested)
        {
            var idle = true;
            try
            {
                foreach (var queue in queues)
                {
                    var batch = await consumer.ReceiveAsync(queue, batchSize, ct);
                    if (batch is null || batch.Messages.Count == 0)
                        continue;

                    idle = false;
                    await HandleBatchAsync(app, consumer, queue, batch);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[QueuePoller] {ex.Message}");
            }

            // Drain continuously while work exists; back off when idle so an
            // empty queue costs one near-nothing wake per interval.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(idle ? pollSeconds : 0.1), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task HandleBatchAsync(
        ReptarApp app,
        IQueueConsumer consumer,
        string queue,
        QueueBatch batch)
    {
        var result = await app.HandleQueueBatchAsync(batch);

        if (result.Success)
        {
            await consumer.AckAsync(queue, batch);
            return;
        }

        var failedIds = new HashSet<string>(result.FailedMessageIds, StringComparer.Ordinal);
        var delays = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var retry in result.Retries ?? [])
            delays[retry.MessageId] = retry.DelaySeconds;

        var succeeded = new List<QueueMessage>();
        var failed = new List<QueueMessage>();
        foreach (var message in batch.Messages)
        {
            if (failedIds.Contains(message.Id))
                failed.Add(message);
            else
                succeeded.Add(message);
        }

        if (succeeded.Count > 0)
            await consumer.AckAsync(queue, new QueueBatch(Ids.New("qbatch"), queue, succeeded));

        if (failed.Count > 0)
            await consumer.RetryAsync(
                queue,
                new QueueBatch(Ids.New("qbatch"), queue, failed),
                delays);
    }

    private static async Task RunCronAsync(ReptarApp app, CancellationToken ct)
    {
        var crons = app.CronExpressions;
        if (crons.Count == 0)
            return;

        var tickSeconds = Math.Max(1, EnvInt("REPTAR_CRON_TICK_SECS", 15));
        var lastFired = new Dictionary<string, string>(StringComparer.Ordinal);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var cron in crons)
                {
                    if (!CronExpr.Matches(cron, now))
                        continue;

                    // Fire at most once per matched tick: 5-field expressions
                    // dedupe per minute; 6-field expressions include seconds.
                    var key = CronExpr.HasSeconds(cron)
                        ? $"{now.ToUnixTimeSeconds() / 60}:{now.Second}"
                        : $"{now.ToUnixTimeSeconds() / 60}";

                    if (lastFired.TryGetValue(cron, out var last) && last == key)
                        continue;

                    lastFired[cron] = key;
                    await app.HandleScheduledAsync(new ScheduledEvent(cron, now.ToString("O")));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Cron] {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(tickSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static int EnvInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }

    internal sealed class KestrelWebSocketTransport : IWebSocketTransport
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, KestrelWebSocketConnection> _connections = new();

        public void Register(string sessionId, System.Net.WebSockets.WebSocket socket) =>
            _connections[sessionId] = new KestrelWebSocketConnection(socket);

        public void Unregister(string sessionId)
        {
            if (_connections.TryRemove(sessionId, out var conn))
            {
                _ = conn.CloseAsync(1000, "Normal closure");
            }
        }

        public void Send(string sessionId, string message)
        {
            if (_connections.TryGetValue(sessionId, out var conn))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(message);
                conn.TryEnqueue(bytes);
            }
        }

        public void Close(string sessionId, int code, string reason)
        {
            if (_connections.TryRemove(sessionId, out var conn))
            {
                _ = conn.CloseAsync(code, reason);
            }
        }
    }

    internal sealed class KestrelWebSocketConnection
    {
        private readonly System.Net.WebSockets.WebSocket _socket;
        private readonly System.Threading.Channels.Channel<byte[]> _channel;
        private readonly CancellationTokenSource _cts = new();
        private long _sent;
        private long _dropped;
        private long _errors;

        public KestrelWebSocketConnection(System.Net.WebSockets.WebSocket socket)
        {
            _socket = socket;
            _channel = System.Threading.Channels.Channel.CreateBounded<byte[]>(
                new System.Threading.Channels.BoundedChannelOptions(1024)
                {
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });

            _ = Task.Run(SendPumpAsync);
        }

        public long Sent => Volatile.Read(ref _sent);
        public long Dropped => Volatile.Read(ref _dropped);
        public long Errors => Volatile.Read(ref _errors);

        public bool TryEnqueue(byte[] message)
        {
            if (_socket.State != System.Net.WebSockets.WebSocketState.Open)
                return false;

            if (_channel.Writer.TryWrite(message))
                return true;

            Interlocked.Increment(ref _dropped);
            return false;
        }

        private async Task SendPumpAsync()
        {
            var reader = _channel.Reader;
            try
            {
                while (await reader.WaitToReadAsync(_cts.Token))
                {
                    while (reader.TryRead(out var frame))
                    {
                        if (_socket.State != System.Net.WebSockets.WebSocketState.Open)
                            return;

                        await _socket.SendAsync(
                            new ArraySegment<byte>(frame),
                            System.Net.WebSockets.WebSocketMessageType.Text,
                            true,
                            _cts.Token);

                        Interlocked.Increment(ref _sent);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal closure
            }
            catch
            {
                Interlocked.Increment(ref _errors);
            }
        }

        public async Task CloseAsync(int code, string reason)
        {
            try
            {
                _channel.Writer.TryComplete();
                _cts.Cancel();

                if (_socket.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    await _socket.CloseAsync(
                        (System.Net.WebSockets.WebSocketCloseStatus)code,
                        reason,
                        CancellationToken.None);
                }
            }
            catch
            {
                // Ignore disconnect exceptions
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }

    private static readonly Dictionary<string, string> EmptyCookies = new(StringComparer.OrdinalIgnoreCase);

    private static async ValueTask<(byte[]? Body, bool TooLarge)> ReadBodyAsync(
        HttpRequest request,
        long maxBodyBytes)
    {
        var contentLength = request.ContentLength;
        if (contentLength is <= 0)
            return (null, false);

        var method = request.Method;
        var hasNoBodyByDefault = HttpMethods.IsGet(method) ||
                                 HttpMethods.IsHead(method) ||
                                 HttpMethods.IsOptions(method) ||
                                 HttpMethods.IsTrace(method);

        if (!contentLength.HasValue && hasNoBodyByDefault && !request.Headers.ContainsKey("Transfer-Encoding"))
            return (null, false);

        var reader = request.BodyReader;

        if (maxBodyBytes > 0 && contentLength.HasValue && contentLength.Value > maxBodyBytes)
            return (null, true);

        if (contentLength is { } len && len <= 65536)
        {
            var buffer = GC.AllocateUninitializedArray<byte>((int)len);
            var totalRead = 0;
            while (totalRead < len)
            {
                var result = await reader.ReadAsync();
                var readOnlySequence = result.Buffer;
                var toCopy = (int)Math.Min(readOnlySequence.Length, len - totalRead);
                readOnlySequence.Slice(0, toCopy).CopyTo(buffer.AsSpan(totalRead));
                totalRead += toCopy;
                reader.AdvanceTo(readOnlySequence.GetPosition(toCopy));
                if (result.IsCompleted || result.IsCanceled)
                    break;
            }
            return (totalRead == len ? buffer : buffer[..totalRead], false);
        }

        using var memory = contentLength.HasValue
            ? new MemoryStream((int)Math.Min(contentLength.Value, 1_048_576))
            : new MemoryStream();

        while (true)
        {
            var result = await reader.ReadAsync();
            var buffer = result.Buffer;

            foreach (var segment in buffer)
            {
                if (maxBodyBytes > 0 && memory.Length + segment.Length > maxBodyBytes)
                {
                    reader.AdvanceTo(buffer.End);
                    return (null, true);
                }

                memory.Write(segment.Span);
            }

            reader.AdvanceTo(buffer.End);

            if (result.IsCompleted || result.IsCanceled)
                break;
        }

        return (memory.ToArray(), false);
    }

    private static async Task<ReptarRequest> ToReptarRequest(HttpContext http, long maxBodyBytes = 0)
    {
        var (body, bodyTooLarge) = await ReadBodyAsync(http.Request, maxBodyBytes);

        var cookies = EmptyCookies;

        if (http.Request.Headers.TryGetValue("Cookie", out var cookieHeader))
        {
            var headerStr = cookieHeader.ToString();
            if (!string.IsNullOrEmpty(headerStr))
            {
                cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var segment in headerStr.Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var separator = segment.IndexOf('=');

                    if (separator > 0)
                        cookies[segment[..separator]] = segment[(separator + 1)..];
                }
            }
        }

        return new ReptarRequest
        {
            Method = http.Request.Method,
            Path = http.Request.Path.Value ?? "/",
            Query = http.Request.QueryString.HasValue
                ? http.Request.QueryString.Value!.TrimStart('?')
                : "",
            RemoteIp = http.Connection.RemoteIpAddress?.ToString(),
            HeaderLookup = name => http.Request.Headers.TryGetValue(name, out var v) ? v.ToString() : null,
            HeadersFactory = () =>
            {
                var dict = new Dictionary<string, string>(http.Request.Headers.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in http.Request.Headers)
                    dict[key] = value.ToString();
                return dict;
            },
            Cookies = cookies,
            Body = body,
            BodyTooLarge = bodyTooLarge,
            Aborted = http.RequestAborted,
        };
    }

    private static ReptarRequest ToWebSocketReptarRequest(HttpContext http)
    {
        var cookies = EmptyCookies;

        if (http.Request.Headers.TryGetValue("Cookie", out var cookieHeader))
        {
            var headerStr = cookieHeader.ToString();
            if (!string.IsNullOrEmpty(headerStr))
            {
                cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var segment in headerStr.Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var separator = segment.IndexOf('=');

                    if (separator > 0)
                        cookies[segment[..separator]] = segment[(separator + 1)..];
                }
            }
        }

        return new ReptarRequest
        {
            Method = http.Request.Method,
            Path = http.Request.Path.Value ?? "/",
            Query = http.Request.QueryString.HasValue
                ? http.Request.QueryString.Value!.TrimStart('?')
                : "",
            RemoteIp = http.Connection.RemoteIpAddress?.ToString(),
            HeaderLookup = name => http.Request.Headers.TryGetValue(name, out var v) ? v.ToString() : null,
            HeadersFactory = () =>
            {
                var dict = new Dictionary<string, string>(http.Request.Headers.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in http.Request.Headers)
                    dict[key] = value.ToString();
                return dict;
            },
            Cookies = cookies,
            Body = [],
            BodyTooLarge = false,
            Aborted = http.RequestAborted,
        };
    }

    private static async Task WriteResponse(HttpContext http, ReptarResponse response)
    {
        http.Response.StatusCode = response.Status;

        if (response.Headers.Count > 0)
        {
            foreach (var (key, value) in response.Headers)
                http.Response.Headers[key] = value;
        }

        if (response.ContentType is not null)
            http.Response.ContentType = response.ContentType;

        if (response.SetCookies.Count > 0)
        {
            foreach (var cookie in response.SetCookies)
                http.Response.Headers.Append("Set-Cookie", cookie);
        }

        if (response.BodyStream is not null)
        {
            await foreach (var chunk in response.BodyStream(http.RequestAborted))
            {
                await http.Response.BodyWriter.WriteAsync(chunk, http.RequestAborted);
                await http.Response.BodyWriter.FlushAsync(http.RequestAborted);
            }

            return;
        }

        if (response.Body is { Length: > 0 } body)
        {
            http.Response.ContentLength = body.Length;
            await http.Response.BodyWriter.WriteAsync(body, http.RequestAborted);
        }
        else if (response.Status is not 204 and not 304 &&
                 !http.Response.Headers.ContainsKey("Content-Length"))
        {
            // HEAD responses carry the representation's Content-Length from
            // ctx.File; do not overwrite it with zero.
            http.Response.ContentLength = 0;
        }
    }
}

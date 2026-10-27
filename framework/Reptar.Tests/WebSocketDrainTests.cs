using System.Collections.Concurrent;
using System.Net.WebSockets;
using Reptar.Hosting;

namespace Reptar.Tests;

public sealed class WebSocketDrainTests
{
    [Fact]
    public async Task Application_close_flushes_queued_final_frame_before_close_output()
    {
        using var socket = new ControlledSocket();
        var connection = new KestrelHost.KestrelWebSocketConnection(socket);
        Assert.True(connection.TryEnqueue("bye"u8.ToArray()));
        await socket.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var closing = connection.CloseAsync(1000, "leave");
        Assert.False(closing.IsCompleted);
        Assert.Equal(0, socket.CloseOutputs);
        socket.Release.TrySetResult();
        await closing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("bye"u8.ToArray(), Assert.Single(socket.Frames));
        Assert.Equal(1, connection.Sent);
        Assert.Equal(1, socket.CloseOutputs);
    }

    [Fact]
    public async Task Drop_oldest_counts_actual_eviction_and_retains_newest_frame()
    {
        using var socket = new ControlledSocket();
        var connection = new KestrelHost.KestrelWebSocketConnection(socket);
        Assert.True(connection.TryEnqueue([0]));
        await socket.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var number = 1; number <= 1025; number++)
            Assert.True(connection.TryEnqueue(BitConverter.GetBytes(number)));
        Assert.Equal(1, connection.Dropped);
        socket.Release.TrySetResult();
        await connection.CloseAsync(1000, "done").WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1025, connection.Sent);
        Assert.Equal(1025, socket.Frames.Count);
        Assert.Equal(1025, BitConverter.ToInt32(socket.Frames.Last()));
    }

    [Fact]
    public async Task Unresponsive_peer_is_aborted_at_the_close_deadline()
    {
        using var socket = new ControlledSocket();
        var connection = new KestrelHost.KestrelWebSocketConnection(socket);
        Assert.True(connection.TryEnqueue([1]));
        await socket.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await connection.CloseAsync(1000, "deadline").WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(socket.Aborted);
        Assert.Empty(socket.Frames);
    }

    private sealed class ControlledSocket : WebSocket
    {
        private int _state = (int)WebSocketState.Open;
        private int _closeOutputs;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<byte[]> Frames { get; } = new();
        public int CloseOutputs => Volatile.Read(ref _closeOutputs);
        public bool Aborted { get; private set; }
        public override WebSocketState State => (WebSocketState)Volatile.Read(ref _state);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort()
        {
            Aborted = true;
            Volatile.Write(ref _state, (int)WebSocketState.Aborted);
        }
        public override void Dispose()
        {
            Release.TrySetCanceled();
            Volatile.Write(ref _state, (int)WebSocketState.Closed);
        }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The request owns ReceiveAsync; use close-output only.");
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            if (Frames.IsEmpty)
                throw new InvalidOperationException("Closed before the final application frame.");
            Interlocked.Increment(ref _closeOutputs);
            Volatile.Write(ref _state, (int)WebSocketState.Closed);
            return Task.CompletedTask;
        }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A second receiver is forbidden.");
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            Frames.Enqueue(buffer.ToArray());
        }
    }
}

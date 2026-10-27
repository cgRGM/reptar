using Reptar;

namespace Reptar.Tests;

public sealed class BackgroundCancellationTests
{
    [Fact]
    public async Task Queue_handler_receives_shutdown_token_without_turning_cancellation_into_a_retry()
    {
        await using var app = ReptarApp.Create();
        using var cancellation = new CancellationTokenSource();
        app.OnQueue("work", (ctx, _) =>
        {
            Assert.Equal(cancellation.Token, ctx.RequestAborted);
            Assert.Equal(cancellation.Token, ctx.Req.Aborted);
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        });
        var batch = new QueueBatch("batch", "work", [new QueueMessage("message", "body", "now", 1)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.HandleQueueBatchAsync(batch, cancellation.Token));
    }

    [Fact]
    public async Task Cancellation_between_messages_prevents_starting_more_work()
    {
        await using var app = ReptarApp.Create();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        app.OnQueue("work", (_, _) =>
        {
            calls++;
            cancellation.Cancel();
            return Task.CompletedTask;
        });
        var batch = new QueueBatch("batch", "work", [
            new QueueMessage("first", "body", "now", 1), new QueueMessage("second", "body", "now", 1)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.HandleQueueBatchAsync(batch, cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Cron_handler_receives_shutdown_token_and_propagates_cooperative_cancellation()
    {
        await using var app = ReptarApp.Create();
        using var cancellation = new CancellationTokenSource();
        app.OnScheduled("* * * * *", (ctx, _) =>
        {
            Assert.Equal(cancellation.Token, ctx.RequestAborted);
            Assert.Equal(cancellation.Token, ctx.Req.Aborted);
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            app.HandleScheduledAsync(new ScheduledEvent("* * * * *", "now"), cancellation.Token));
    }
}

using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for the web run registry. Steer and abort only work because a second HTTP
/// request can reach the run started by the first, so these pin that reachability.
/// </summary>
public sealed class AgentRunRegistryTests
{
    [Fact]
    public void a_session_with_no_run_reports_idle()
    {
        Assert.False(AgentRunRegistry.Status("nobody").Running);
        Assert.False(AgentRunRegistry.Abort("nobody"));
        Assert.False(AgentRunRegistry.Steer("nobody", "hi"));
    }

    [Fact]
    public void beginning_a_run_makes_it_visible()
    {
        AgentRunRegistry.Begin("s1", out var token);
        try
        {
            Assert.True(AgentRunRegistry.Status("s1").Running);
            Assert.False(token.IsCancellationRequested);
        }
        finally
        {
            AgentRunRegistry.End("s1");
        }

        Assert.False(AgentRunRegistry.Status("s1").Running);
    }

    [Fact]
    public void abort_cancels_the_run_token()
    {
        AgentRunRegistry.Begin("s2", out var token);
        try
        {
            Assert.True(AgentRunRegistry.Abort("s2"));
            Assert.True(token.IsCancellationRequested);
        }
        finally
        {
            AgentRunRegistry.End("s2");
        }
    }

    [Fact]
    public void steering_is_drained_once()
    {
        AgentRunRegistry.Begin("s3", out _);
        try
        {
            Assert.True(AgentRunRegistry.Steer("s3", "first"));
            Assert.True(AgentRunRegistry.Steer("s3", "second"));

            var drained = AgentRunRegistry.DrainSteering("s3");

            Assert.Equal(["first", "second"], drained.Select(m => m.Content));
            Assert.All(drained, m => Assert.Equal(AgentRoles.User, m.Role));

            // A second drain finds nothing: the queue is consumed, not replayed.
            Assert.Empty(AgentRunRegistry.DrainSteering("s3"));
        }
        finally
        {
            AgentRunRegistry.End("s3");
        }
    }

    [Fact]
    public void ending_a_run_clears_its_queue()
    {
        AgentRunRegistry.Begin("s4", out _);
        AgentRunRegistry.Steer("s4", "queued");
        AgentRunRegistry.End("s4");

        Assert.Empty(AgentRunRegistry.DrainSteering("s4"));
    }

    [Fact]
    public void beginning_twice_rejects_the_duplicate_without_orphaning_the_first()
    {
        AgentRunRegistry.Begin("s5", out var first);
        try
        {
            Assert.False(AgentRunRegistry.TryBegin("s5", out var duplicate));
            Assert.Null(duplicate);
            Assert.Throws<InvalidOperationException>(() => AgentRunRegistry.Begin("s5", out _));
            Assert.True(AgentRunRegistry.Abort("s5"));
            Assert.True(first.IsCancellationRequested);
        }
        finally
        {
            AgentRunRegistry.End("s5");
        }
    }

    [Fact]
    public void late_lease_cleanup_cannot_remove_or_drain_a_new_run()
    {
        const string key = "lease-replacement";
        Assert.True(AgentRunRegistry.TryBegin(key, out var first));
        first.Dispose();
        Assert.True(AgentRunRegistry.TryBegin(key, out var second));
        using (second)
        {
            Assert.True(AgentRunRegistry.Steer(key, "new run"));
            first.Dispose();
            Assert.True(AgentRunRegistry.Status(key).Running);
            Assert.Empty(first.DrainSteering());
            Assert.Equal("new run", Assert.Single(second.DrainSteering()).Content);
            Assert.True(AgentRunRegistry.Abort(key));
            Assert.True(second.Token.IsCancellationRequested);
        }
    }

    [Fact]
    public async Task abort_and_cleanup_can_race_without_disposing_an_inflight_cancellation()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var key = $"abort-race-{iteration}";
            Assert.True(AgentRunRegistry.TryBegin(key, out var run));
            await Task.WhenAll(Task.Run(() => AgentRunRegistry.Abort(key)), Task.Run(run.Dispose));
            Assert.False(AgentRunRegistry.Status(key).Running);
        }
    }

    [Fact]
    public async Task cancellation_callback_can_end_its_own_run_without_a_lock_deadlock()
    {
        const string key = "callback-cleanup";
        Assert.True(AgentRunRegistry.TryBegin(key, out var run));
        using var callback = run.Token.Register(run.Dispose);
        Assert.True(await Task.Run(() => AgentRunRegistry.Abort(key)).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(AgentRunRegistry.Status(key).Running);
    }

    [Fact]
    public async Task parallel_starts_reserve_only_one_run()
    {
        const string key = "parallel-reservation";
        var leases = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => AgentRunRegistry.TryBegin(key, out var run) ? run : null)));
        var winner = Assert.Single(leases.Where(run => run is not null));
        winner!.Dispose();
        Assert.False(AgentRunRegistry.Status(key).Running);
    }

    [Fact]
    public void status_reports_elapsed_time_for_a_live_run()
    {
        AgentRunRegistry.Begin("s6", out _);
        try
        {
            var (running, elapsed) = AgentRunRegistry.Status("s6");
            Assert.True(running);
            Assert.True(elapsed >= 0);
        }
        finally
        {
            AgentRunRegistry.End("s6");
        }
    }
}

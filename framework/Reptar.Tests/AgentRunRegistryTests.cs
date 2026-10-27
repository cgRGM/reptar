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
    public void beginning_twice_replaces_the_previous_run()
    {
        AgentRunRegistry.Begin("s5", out var first);
        AgentRunRegistry.Begin("s5", out var second);
        try
        {
            Assert.NotEqual(first, second);

            // Aborting hits the current run, not the replaced one.
            AgentRunRegistry.Abort("s5");
            Assert.True(second.IsCancellationRequested);
            Assert.False(first.IsCancellationRequested);
        }
        finally
        {
            AgentRunRegistry.End("s5");
        }
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

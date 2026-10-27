using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for <see cref="AgentMessages"/>: the C# port of pi's <c>convertToLlm</c>.
/// The stored transcript carries roles no provider accepts; these tests pin the flattening rules.
/// </summary>
public sealed class AgentMessagesTests
{
    private static ChatMessage User(string text) => new(AgentRoles.User, text);
    private static ChatMessage Assistant(string text) => new(AgentRoles.Assistant, text);

    [Fact]
    public void bash_execution_reaches_the_model_as_user_text_describing_the_command()
    {
        var message = AgentMessages.BashExecution(
            "git status",
            "On branch main\nnothing to commit",
            "",
            exitCode: 0);

        var flattened = AgentMessages.ConvertToLlm([message]);

        var single = Assert.Single(flattened);
        Assert.Equal(AgentRoles.User, single.Role);
        Assert.Contains("Ran `git status`", single.Content);
        Assert.Contains("nothing to commit", single.Content);
        Assert.DoesNotContain("exited with code", single.Content);
    }

    [Fact]
    public void bash_execution_marks_a_nonzero_exit_and_a_timeout()
    {
        var failed = AgentMessages.BashExecution("dotnet test", "", "boom", exitCode: 1);
        var timedOut = AgentMessages.BashExecution("sleep 100", "", "", exitCode: null, cancelled: true);

        var flattened = AgentMessages.ConvertToLlm([failed, timedOut]);

        Assert.Contains("Command exited with code 1", flattened[0].Content);
        Assert.Contains("boom", flattened[0].Content);
        Assert.Contains("(command cancelled)", flattened[1].Content);
    }

    [Fact]
    public void bash_execution_reports_no_output_and_the_spill_path()
    {
        var message = AgentMessages.BashExecution(
            "true", "", "", exitCode: 0, fullOutputPath: "/tmp/full.log");

        var text = AgentMessages.ConvertToLlm([message])[0].Content;

        Assert.Contains("(no output)", text);
        Assert.Contains("/tmp/full.log", text);
    }

    [Fact]
    public void double_bang_excludes_the_command_from_model_context()
    {
        var excluded = AgentMessages.BashExecution(
            "cat .env", "SECRET=hunter2", "", exitCode: 0, excludeFromContext: true);

        var flattened = AgentMessages.ConvertToLlm([User("hi"), excluded, Assistant("ok")]);

        Assert.Equal(2, flattened.Count);
        Assert.DoesNotContain(flattened, m => m.Content.Contains("hunter2"));
    }

    [Fact]
    public void compaction_and_branch_summaries_are_wrapped_so_they_are_not_read_as_requests()
    {
        var compaction = new ChatMessage(AgentRoles.CompactionSummary, "## Goal\nShip it.");
        var branch = new ChatMessage(AgentRoles.BranchSummary, "Explored approach A.");

        var flattened = AgentMessages.ConvertToLlm([compaction, branch]);

        Assert.All(flattened, m => Assert.Equal(AgentRoles.User, m.Role));
        Assert.Contains("<summary>", flattened[0].Content);
        Assert.Contains("Ship it.", flattened[0].Content);
        Assert.Contains("came back from", flattened[1].Content);
    }

    [Fact]
    public void custom_context_becomes_a_user_message()
    {
        var custom = new ChatMessage(AgentRoles.Custom, "SKILL: how to write a migration");

        var flattened = AgentMessages.ConvertToLlm([custom]);

        Assert.Equal(AgentRoles.User, flattened[0].Role);
        Assert.Equal("SKILL: how to write a migration", flattened[0].Content);
    }

    [Fact]
    public void provider_roles_pass_through_untouched()
    {
        var messages = new List<ChatMessage>
        {
            new(AgentRoles.System, "you are a coding agent"),
            User("hi"),
            Assistant("hello"),
            new(AgentRoles.Tool, "file contents", Name: "read_file", ToolCallId: "c1")
        };

        var flattened = AgentMessages.ConvertToLlm(messages);

        Assert.Equal(messages.Count, flattened.Count);
        for (var i = 0; i < messages.Count; i++)
            Assert.Same(messages[i], flattened[i]);
    }

    [Fact]
    public void system_messages_can_be_dropped_when_the_caller_supplies_its_own_prompt()
    {
        var flattened = AgentMessages.ConvertToLlm(
            [new ChatMessage(AgentRoles.System, "prompt"), User("hi")],
            includeSystem: false);

        Assert.DoesNotContain(flattened, m => m.Role == AgentRoles.System);
    }

    [Fact]
    public void unknown_roles_are_kept_as_context_rather_than_silently_dropped()
    {
        var flattened = AgentMessages.ConvertToLlm([new ChatMessage("notification", "build finished")]);

        var single = Assert.Single(flattened);
        Assert.Equal(AgentRoles.User, single.Role);
        Assert.Equal("build finished", single.Content);
    }

    [Fact]
    public void orphaned_tool_results_are_dropped()
    {
        var messages = new List<ChatMessage>
        {
            User("read it"),
            new(AgentRoles.Assistant, "", ToolCalls: [new AiToolCall("c1", "read_file", "{}")]),
            new(AgentRoles.Tool, "contents", Name: "read_file", ToolCallId: "c1"),
            new(AgentRoles.Tool, "stray", Name: "read_file", ToolCallId: "c99")
        };

        var cleaned = AgentMessages.DropOrphanedToolResults(messages);

        Assert.Equal(3, cleaned.Count);
        Assert.DoesNotContain(cleaned, m => m.ToolCallId == "c99");
    }

    [Fact]
    public async Task loop_flattens_stored_roles_before_calling_the_provider()
    {
        var captured = new List<IReadOnlyList<ChatMessage>>();
        var client = new ScriptedEventLlmClient(ScriptedEvents.TextEvents("ok"));

        await AgentLoop.RunAsync(
            new List<ChatMessage>
            {
                User("what changed?"),
                AgentMessages.BashExecution("git diff", "1 file changed", "", 0)
            },
            new AgentLoopConfig { Model = "test", MaxSteps = 2 },
            (messages, tools, ct) =>
            {
                captured.Add([.. messages]);
                return client.StreamAsync(messages, tools, ct);
            },
            []);

        var sent = Assert.Single(captured);
        Assert.All(sent, m => Assert.Contains(m.Role, new[] { AgentRoles.System, AgentRoles.User, AgentRoles.Assistant, AgentRoles.Tool }));
        Assert.Contains(sent, m => m.Content.Contains("Ran `git diff`"));
    }
}

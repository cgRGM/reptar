using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// A complete two-step workflow: gather → summarize → summary leaves as output.
/// </summary>
public static class ResearchWorkflowFixture
{
    [WorkflowStep("gather", Produces = ["raw_notes"])]
    public static string Gather(string task) => task;

    [WorkflowStep("summarize", Consumes = ["raw_notes"], Produces = ["summary"], IsOutput = true)]
    public static string Summarize(string notes) => notes;
}

/// <summary>
/// A broken workflow: fetch produces raw_html that no step consumes and the
/// producer is not marked IsOutput — the generator must flag it.
/// </summary>
public static class BrokenWorkflowFixture
{
    [WorkflowStep("fetch", Produces = ["raw_html"])]
    public static string Fetch(string url) => url;
}

/// <summary>
/// An orphan workflow: the step consumes a message nobody produces.
/// </summary>
public static class OrphanWorkflowFixture
{
    [WorkflowStep("waiter", Consumes = ["ghost_message"], Produces = ["done"], IsOutput = true)]
    public static string Wait(string message) => message;
}

public sealed class WorkflowGraphTests
{
    [Fact]
    public void workflow_graph_is_complete_when_every_message_flows()
    {
        Assert.True(ResearchWorkflowFixtureGraph.IsComplete);
        Assert.Equal("ResearchWorkflowFixture", ResearchWorkflowFixtureGraph.WorkflowName);
        Assert.Equal(["gather", "summarize"], ResearchWorkflowFixtureGraph.Steps);
        Assert.Equal(["raw_notes", "summary"], ResearchWorkflowFixtureGraph.ProducedMessages);
        Assert.Equal(["raw_notes"], ResearchWorkflowFixtureGraph.ConsumedMessages);
        Assert.Contains(ResearchWorkflowFixtureGraph.Edges,
            edge => edge is { From: "gather", Message: "raw_notes", To: "summarize" });
        Assert.Empty(ResearchWorkflowFixtureGraph.UnhandledMessages);
        Assert.Empty(ResearchWorkflowFixtureGraph.OrphanConsumers);
    }

    [Fact]
    public void workflow_graph_flags_produced_messages_with_no_consumer()
    {
        // The generated metadata exposes exactly what the REPTARWF001 build
        // warning reports, so the graph rule itself is unit-testable.
        Assert.False(BrokenWorkflowFixtureGraph.IsComplete);
        Assert.Equal(["raw_html"], BrokenWorkflowFixtureGraph.UnhandledMessages);
        Assert.Empty(BrokenWorkflowFixtureGraph.OrphanConsumers);
    }

    [Fact]
    public void workflow_graph_flags_consumed_messages_with_no_producer()
    {
        Assert.False(OrphanWorkflowFixtureGraph.IsComplete);
        Assert.Equal(["ghost_message"], OrphanWorkflowFixtureGraph.OrphanConsumers);
        Assert.Empty(OrphanWorkflowFixtureGraph.UnhandledMessages);
    }

    // ------------------------------------------------------------------
    // G17 — Declarative JSON workflows
    // ------------------------------------------------------------------

    private const string ValidWorkflowJson = """
        {
          "name": "research-pipeline",
          "instructions": "You are a research pipeline stage.",
          "steps": [
            { "id": "gather", "agent": "researcher", "produces": ["raw_notes"] },
            { "id": "summarize", "agent": "writer", "consumes": ["raw_notes"], "produces": ["report"], "isOutput": true }
          ]
        }
        """;

    [Fact]
    public void declarative_workflow_parses_and_validates()
    {
        var workflow = DeclarativeWorkflow.FromJson(ValidWorkflowJson);

        Assert.Equal("research-pipeline", workflow.Name);
        Assert.Equal(2, workflow.Steps.Count);
        Assert.Equal("researcher", workflow.Steps[0].Agent);
        Assert.Equal(["raw_notes"], workflow.Steps[0].Produces);
        Assert.True(workflow.Steps[1].IsOutput);

        var (unhandled, orphans) = workflow.Validate();
        Assert.Empty(unhandled);
        Assert.Empty(orphans);
    }

    [Fact]
    public void declarative_workflow_validation_matches_graph_rules()
    {
        var broken = """
            {
              "name": "broken",
              "steps": [
                { "id": "a", "agent": "x", "produces": ["never_handled"] },
                { "id": "b", "agent": "x", "consumes": ["ghost"] }
              ]
            }
            """;

        var workflow = DeclarativeWorkflow.FromJson(broken);
        var (unhandled, orphans) = workflow.Validate();
        Assert.Equal(["never_handled"], unhandled);
        Assert.Equal(["ghost"], orphans);
    }

    [Fact]
    public async Task declarative_workflow_runs_in_dependency_order()
    {
        var workflow = DeclarativeWorkflow.FromJson(ValidWorkflowJson);

        var researcher = new ScriptedLlmClient("gathered: the answer is 42");
        var writer = new ScriptedLlmClient("report: everything gathered");

        var result = await DeclarativeWorkflowRunner.RunAsync(
            workflow,
            "find the answer",
            agentName => agentName switch
            {
                "researcher" => researcher,
                "writer" => writer,
                _ => throw new InvalidOperationException($"unknown agent {agentName}"),
            });

        Assert.True(result.Completed);
        Assert.Equal("report: everything gathered", result.Outputs["report"]);

        // The gather step's output fed the summarize step's input.
        Assert.Contains("gathered: the answer is 42", writer.Calls[0][1].Content);
        Assert.Contains("find the answer", researcher.Calls[0][1].Content);
    }

    [Fact]
    public async Task declarative_workflow_reports_stalled_cycles()
    {
        // A true cycle passes validation (every message produced and
        // consumed) but can never make progress at runtime.
        var cycleJson = """
            {
              "name": "cycle",
              "steps": [
                { "id": "a", "agent": "x", "consumes": ["b_msg"], "produces": ["a_msg"], "isOutput": true },
                { "id": "b", "agent": "x", "consumes": ["a_msg"], "produces": ["b_msg"] }
              ]
            }
            """;
        var workflow = DeclarativeWorkflow.FromJson(cycleJson);

        var (unhandled, orphans) = workflow.Validate();
        Assert.Empty(unhandled);
        Assert.Empty(orphans);

        var result = await DeclarativeWorkflowRunner.RunAsync(
            workflow, "input", _ => new ScriptedLlmClient("x"));

        Assert.False(result.Completed);
        Assert.NotNull(result.StalledStep);
        Assert.Contains(result.StalledStep!, new[] { "a", "b" });
        Assert.Empty(result.Outputs);
    }
}

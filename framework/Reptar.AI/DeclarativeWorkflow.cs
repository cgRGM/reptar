#if !NETWASM
using System.Text.Json;

namespace Reptar.AI;

/// <summary>
/// G17 — Declarative agent workflows in Reptar form. The MSFT Declarative
/// Workflows platform is a YAML/PowerFx no-code system (a confirmed
/// leave-behind); what fits "what we do" is the file-based idea with zero
/// dependencies: an <c>agent.json</c> describing named steps over agents,
/// parsed with <c>JsonDocument</c> (no reflection), validated with the same
/// graph rules as the G16 compile-time generator, and executed by resolving
/// each step's agent to an <see cref="ILlmClient"/> at runtime.
/// </summary>
public sealed record DeclarativeWorkflowStep(
    string Id,
    string Agent,
    string[] Consumes,
    string[] Produces,
    bool IsOutput,
    string Instructions);

/// <summary>
/// A parsed declarative workflow definition.
/// </summary>
public sealed record DeclarativeWorkflow(
    string Name,
    string Instructions,
    IReadOnlyList<DeclarativeWorkflowStep> Steps)
{
    /// <summary>
    /// Parses an <c>agent.json</c> workflow definition. Zero reflection:
    /// shape errors surface as structured <see cref="InvalidOperationException"/>s
    /// with the offending property path.
    /// </summary>
    public static DeclarativeWorkflow FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind is not JsonValueKind.Object)
            throw new InvalidOperationException("Workflow JSON must be an object.");

        var name = root.TryGetProperty("name", out var nameElement) &&
                   nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString() ?? ""
            : "";
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Workflow 'name' is required.");

        var instructions = root.TryGetProperty("instructions", out var instructionsElement) &&
                           instructionsElement.ValueKind == JsonValueKind.String
            ? instructionsElement.GetString() ?? ""
            : "";

        var steps = new List<DeclarativeWorkflowStep>();
        if (root.TryGetProperty("steps", out var stepsElement) &&
            stepsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var stepElement in stepsElement.EnumerateArray())
            {
                var stepId = stepElement.TryGetProperty("id", out var idElement) &&
                             idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString() ?? ""
                    : "";
                if (string.IsNullOrWhiteSpace(stepId))
                    throw new InvalidOperationException("Every workflow step requires an 'id'.");

                var agent = stepElement.TryGetProperty("agent", out var agentElement) &&
                            agentElement.ValueKind == JsonValueKind.String
                    ? agentElement.GetString() ?? ""
                    : "";
                if (string.IsNullOrWhiteSpace(agent))
                    throw new InvalidOperationException($"Step '{stepId}' requires an 'agent' name.");

                steps.Add(new DeclarativeWorkflowStep(
                    stepId,
                    agent,
                    ReadStrings(stepElement, "consumes"),
                    ReadStrings(stepElement, "produces"),
                    stepElement.TryGetProperty("isOutput", out var outputElement) &&
                    outputElement.ValueKind == JsonValueKind.True,
                    stepElement.TryGetProperty("instructions", out var stepInstructions) &&
                    stepInstructions.ValueKind == JsonValueKind.String
                        ? stepInstructions.GetString() ?? ""
                        : ""));
            }
        }

        if (steps.Count == 0)
            throw new InvalidOperationException("Workflow requires at least one step.");

        return new DeclarativeWorkflow(name, instructions, steps);
    }

    private static string[] ReadStrings(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
            {
                values.Add(value);
            }
        }

        return [.. values];
    }

    /// <summary>
    /// Validates the graph with the same rules as the G16 generator: every
    /// produced message must be consumed by another step or be a declared
    /// output, and every consumed message must be produced by some step.
    /// </summary>
    public (string[] Unhandled, string[] OrphanConsumers) Validate()
    {
        var produced = new HashSet<string>(StringComparer.Ordinal);
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var terminal = new HashSet<string>(StringComparer.Ordinal);

        foreach (var step in Steps)
        {
            foreach (var message in step.Produces)
            {
                produced.Add(message);
                if (step.IsOutput)
                {
                    terminal.Add(message);
                }
            }

            foreach (var message in step.Consumes)
            {
                consumed.Add(message);
            }
        }

        var unhandled = produced
            .Where(message => !consumed.Contains(message) && !terminal.Contains(message))
            .OrderBy(message => message)
            .ToArray();
        var orphans = consumed
            .Where(message => !produced.Contains(message))
            .OrderBy(message => message)
            .ToArray();

        return (unhandled, orphans);
    }
}

/// <summary>
/// The outcome of a declarative workflow run.
/// </summary>
public sealed record DeclarativeRunResult(
    bool Completed,
    string? StalledStep,
    IReadOnlyDictionary<string, string> Outputs);

/// <summary>
/// Executes a <see cref="DeclarativeWorkflow"/> by resolving each step's agent
/// name to an <see cref="ILlmClient"/> and running the steps in message
/// dependency order. Source steps (no consumed messages) receive the run's
/// input text; every other step receives the text of its consumed messages.
/// </summary>
public static class DeclarativeWorkflowRunner
{
    public static async Task<DeclarativeRunResult> RunAsync(
        DeclarativeWorkflow workflow,
        string input,
        Func<string, ILlmClient> resolveAgent,
        Func<string, Task<bool>>? validateStepInput = null,
        CancellationToken cancellationToken = default)
    {
        var (unhandled, orphans) = workflow.Validate();
        if (unhandled.Length > 0)
            throw new InvalidOperationException(
                $"Workflow '{workflow.Name}' has unhandled messages: {string.Join(", ", unhandled)}.");
        if (orphans.Length > 0)
            throw new InvalidOperationException(
                $"Workflow '{workflow.Name}' consumes unproduced messages: {string.Join(", ", orphans)}.");

        var bus = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = workflow.Steps.ToList();

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Topological step: run every step whose consumed messages are
            // all on the bus. If nothing can run, the workflow is stalled on
            // a step (a cycle or an unresolved dependency) — report it.
            var runnable = pending
                .Where(step => step.Consumes.All(bus.ContainsKey))
                .ToList();

            if (runnable.Count == 0)
            {
                return new DeclarativeRunResult(
                    Completed: false,
                    StalledStep: pending[0].Id,
                    Outputs: SnapshotOutputs(workflow, bus));
            }

            foreach (var step in runnable)
            {
                var agent = resolveAgent(step.Agent) ??
                    throw new InvalidOperationException(
                        $"Step '{step.Id}' names agent '{step.Agent}', which resolved to nothing.");

                var consumedText = step.Consumes.Length == 0
                    ? input
                    : string.Join("\n\n", step.Consumes.Select(message =>
                        $"{message}:\n{bus[message]}"));

                if (validateStepInput is not null && !await validateStepInput(consumedText))
                {
                    throw new InvalidOperationException(
                        $"Step '{step.Id}' input was rejected by the validator.");
                }

                var messages = new List<ChatMessage>();
                var systemPrompt = string.IsNullOrWhiteSpace(step.Instructions)
                    ? workflow.Instructions
                    : workflow.Instructions + "\n\n" + step.Instructions;
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                {
                    messages.Add(new ChatMessage("system", systemPrompt));
                }

                messages.Add(new ChatMessage("user", consumedText));
                var response = await agent.CompleteAsync(messages, cancellationToken);

                foreach (var message in step.Produces)
                {
                    bus[message] = response;
                }

                pending.Remove(step);
            }
        }

        return new DeclarativeRunResult(
            Completed: true,
            StalledStep: null,
            Outputs: SnapshotOutputs(workflow, bus));
    }

    private static IReadOnlyDictionary<string, string> SnapshotOutputs(
        DeclarativeWorkflow workflow,
        Dictionary<string, string> bus)
    {
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in workflow.Steps)
        {
            if (!step.IsOutput)
            {
                continue;
            }

            foreach (var message in step.Produces)
            {
                if (bus.TryGetValue(message, out var value))
                {
                    outputs[message] = value;
                }
            }
        }

        return outputs;
    }
}
#endif

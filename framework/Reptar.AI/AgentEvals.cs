#if !NETWASM
using System.Data;
using System.Text.Json;

namespace Reptar.AI;

/// <summary>
/// Standard eval TableDef: one row per scored eval case, grouped by run.
/// Point the runner at a mock provider (e.g. the ai-gateway's mock models or
/// the Echo client) and evals become deterministic, free, and CI-runnable;
/// real providers are opt-in per run.
/// </summary>
public static class AgentEvalSchema
{
    public static TableDef Evals(string tableName = "agent_evals") =>
        ReptarApi.DefineTable(tableName,
            ReptarApi.Col.Text("id").PrimaryKey(),
            ReptarApi.Col.Text("run_id").NotNull().Indexed(),
            ReptarApi.Col.Text("question").NotNull(),
            ReptarApi.Col.Text("expected"),
            ReptarApi.Col.Text("response").NotNull(),
            ReptarApi.Col.Real("score"),
            ReptarApi.Col.Int("passed").NotNull(),
            ReptarApi.Col.Text("metrics_json").NotNull(),
            ReptarApi.Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"));
}

/// <summary>
/// A persisted eval result row. Manual mapping (zero reflection, AOT safe).
/// </summary>
public sealed record EvalRow(
    string Id,
    string RunId,
    string Question,
    string? Expected,
    string Response,
    double Score,
    int Passed,
    string MetricsJson,
    string CreatedAt) : IReptarEntity
{
    static EvalRow()
    {
        EntityMapper<EvalRow>.DirectMap = Map;
        EntityMapper<EvalRow>.Factory = _ => Map;
    }

    public static EvalRow Map(IDataRecord record)
    {
        string id = "", runId = "", question = "", response = "", metricsJson = "", createdAt = "";
        string? expected = null;
        double score = 0;
        int passed = 0;

        for (var i = 0; i < record.FieldCount; i++)
        {
            if (record.IsDBNull(i))
                continue;
            switch (Normalize(record.GetName(i)))
            {
                case "id": id = record.GetString(i); break;
                case "runid" or "run_id": runId = record.GetString(i); break;
                case "question": question = record.GetString(i); break;
                case "expected": expected = record.GetValue(i).ToString(); break;
                case "response": response = record.GetString(i); break;
                case "score": score = record.GetDouble(i); break;
                case "passed": passed = record.GetInt32(i); break;
                case "metricsjson" or "metrics_json": metricsJson = record.GetString(i); break;
                case "createdat" or "created_at": createdAt = record.GetValue(i).ToString() ?? ""; break;
            }
        }

        return new EvalRow(id, runId, question, expected, response, score, passed, metricsJson, createdAt);
    }

    public (string Name, object? Value)[] ToDbParameters() =>
    [
        ("Id", Id),
        ("RunId", RunId),
        ("Question", Question),
        ("Expected", Expected),
        ("Response", Response),
        ("Score", Score),
        ("Passed", Passed),
        ("MetricsJson", MetricsJson),
        ("CreatedAt", CreatedAt)
    ];

    private static string Normalize(string name) =>
        name.Replace("_", "").ToLowerInvariant();
}

/// <summary>
/// Durable eval results over any <see cref="IDbClient"/>.
/// </summary>
public sealed class AgentEvalStore(IDbClient db, TableDef evals)
{
    public TableDef Evals => evals;

    public Task EnsureCreatedAsync() => db.EnsureCreatedAsync(evals);

    public Task AppendAsync(string runId, EvalCaseResult result, string? expected)
    {
        var row = new EvalRow(
            Ids.New("eval"),
            runId,
            result.Question,
            expected,
            result.Response,
            result.Score,
            result.Passed ? 1 : 0,
            SerializeMetrics(result.Metrics),
            DateTimeOffset.UtcNow.ToString("O"));

        return db.InsertAsync(evals, row);
    }

    /// <summary>
    /// Renders the metric list without reflection, so an AOT build does not throw here.
    /// </summary>
    private static string SerializeMetrics(IReadOnlyList<EvalMetricResult> metrics)
    {
        var array = new System.Text.Json.Nodes.JsonArray();

        foreach (var metric in metrics)
        {
            array.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = metric.Name,
                ["score"] = metric.Score,
                ["passed"] = metric.Passed,
                ["notes"] = metric.Notes
            });
        }

        return array.ToJsonString();
    }

    public Task<List<EvalRow>> ListRunAsync(string runId) =>
        db.ListAsync<EvalRow>(
            evals, "where run_id = @runId order by rowid", ("runId", runId));
}

/// <summary>A single eval case: a prompt and an optional expected answer.</summary>
public sealed record EvalCase(string Question, string? Expected = null);

/// <summary>One metric's verdict on one case.</summary>
public sealed record EvalMetricResult(string Name, double Score, bool Passed, string? Notes = null);

/// <summary>All metrics for one case. A case passes when every scorer passes.</summary>
public sealed record EvalCaseResult(
    string Question,
    string Response,
    IReadOnlyList<EvalMetricResult> Metrics,
    bool Passed)
{
    public double Score => Metrics.Count == 0
        ? 0
        : Metrics.Average(metric => metric.Score);
}

/// <summary>
/// The outcome of one eval run over a case list.
/// </summary>
public sealed record EvalRunResult(
    string RunId,
    IReadOnlyList<EvalCaseResult> Cases,
    double PassRate)
{
    public int Passed => Cases.Count(caseResult => caseResult.Passed);
    public int Total => Cases.Count;
}

/// <summary>A scorer judges one (question, response, expected) triple.</summary>
public interface IEvalScorer
{
    string Name { get; }

    Task<EvalMetricResult> ScoreAsync(EvalCase evalCase, string response, CancellationToken cancellationToken = default);
}

/// <summary>
/// Compares the response against <see cref="EvalCase.Expected"/> using
/// exact, contains, or whitespace/case-normalized-contains matching.
/// </summary>
public sealed class ExpectedOutputScorer(string matchMode = "normalized-contains") : IEvalScorer
{
    public string Name => "expected_output";

    public Task<EvalMetricResult> ScoreAsync(
        EvalCase evalCase, string response, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(evalCase.Expected))
        {
            return Task.FromResult(new EvalMetricResult(Name, 1, true, "no expected output defined"));
        }

        var expected = evalCase.Expected;
        var passed = matchMode switch
        {
            "exact" => string.Equals(response.Trim(), expected.Trim(), StringComparison.Ordinal),
            "contains" => response.Contains(expected, StringComparison.Ordinal),
            _ => Normalize(response).Contains(Normalize(expected), StringComparison.Ordinal),
        };

        return Task.FromResult(new EvalMetricResult(
            Name, passed ? 1 : 0, passed, passed ? null : $"expected '{expected[..Math.Min(60, expected.Length)]}…'"));
    }

    private static string Normalize(string value) =>
        string.Join(' ', value
            .Split(['\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>A host-defined predicate scorer.</summary>
public sealed class PredicateScorer(string name, Func<string, string, bool> test) : IEvalScorer
{
    public string Name { get; } = name;

    public Task<EvalMetricResult> ScoreAsync(
        EvalCase evalCase, string response, CancellationToken cancellationToken = default)
    {
        var passed = test(evalCase.Question, response);
        return Task.FromResult(new EvalMetricResult(Name, passed ? 1 : 0, passed));
    }
}

/// <summary>
/// The structured verdict the LLM judge returns. Parsed and validated with
/// the G11 typed generation path.
/// </summary>
public sealed record JudgeVerdict(double Score, bool Passed, string Reasoning);

/// <summary>
/// LLM-as-judge scorer — opt-in: it costs a second model call per case.
/// The verdict comes back through <see cref="AgentStructuredOutput.GenerateAsync{T}"/>,
/// so the judge's reply is schema-validated and retried before it can fail
/// an eval.
/// </summary>
public sealed class LlmJudgeScorer(ILlmClient judge, double threshold = 0.7, string? rubric = null) : IEvalScorer
{
    public string Name => "llm_judge";

    private static readonly Validator<JudgeVerdict> VerdictValidator = ReptarApi.Rule<JudgeVerdict>()
        .Prop(x => x.Score, r => r.Min(0).Max(1))
        .Prop(x => x.Reasoning, r => r.NotNull().Min(1));

    public async Task<EvalMetricResult> ScoreAsync(
        EvalCase evalCase, string response, CancellationToken cancellationToken = default)
    {
        var prompt = $"""
            You are an impartial judge evaluating an AI response.

            Question:
            {evalCase.Question}

            Response to evaluate:
            {response}

            {(string.IsNullOrWhiteSpace(rubric)
                ? "Score how well the response answers the question: 0.0 (wrong or useless) to 1.0 (excellent)."
                : $"Grading rubric:\n{rubric}")}
            """;

        var verdict = await judge.GenerateAsync(
            prompt, VerdictValidator, maxRetries: 1, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (verdict.Succeeded && verdict.Value is { } value)
        {
            var passed = value.Passed || value.Score >= threshold;
            return new EvalMetricResult(Name, value.Score, passed, value.Reasoning);
        }

        return new EvalMetricResult(Name, 0, false,
            "judge did not return a valid verdict: " + string.Join("; ", verdict.Issues.Select(i => i.Message)));
    }
}

/// <summary>
/// G19 — Runs a list of eval cases through a model and scores the responses.
/// With a mock/echo provider this is a deterministic, zero-cost CI gate for
/// prompts and agent behavior; real providers and LLM judges are opt-in.
/// </summary>
public static class AgentEvalRunner
{
    public static async Task<EvalRunResult> RunAsync(
        ILlmClient client,
        IReadOnlyList<EvalCase> cases,
        IReadOnlyList<IEvalScorer> scorers,
        string? systemPrompt = null,
        string? runId = null,
        Action<EvalCaseResult, EvalCase>? onCaseResult = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<EvalCaseResult>();

        foreach (var evalCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var messages = new List<ChatMessage>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                messages.Add(new ChatMessage("system", systemPrompt));
            }
            messages.Add(new ChatMessage("user", evalCase.Question));

            var response = await client.CompleteAsync(messages, cancellationToken).ConfigureAwait(false);

            var metrics = new List<EvalMetricResult>();
            foreach (var scorer in scorers)
            {
                metrics.Add(await scorer.ScoreAsync(evalCase, response, cancellationToken).ConfigureAwait(false));
            }

            var caseResult = new EvalCaseResult(
                evalCase.Question,
                response,
                metrics,
                metrics.All(metric => metric.Passed));

            onCaseResult?.Invoke(caseResult, evalCase);
            results.Add(caseResult);
        }

        var passRate = cases.Count == 0
            ? 1
            : results.Count(result => result.Passed) / (double)cases.Count;

        return new EvalRunResult(runId ?? Ids.New("evalrun"), results, passRate);
    }
}
#endif

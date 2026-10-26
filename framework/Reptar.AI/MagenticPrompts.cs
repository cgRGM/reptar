#if !NETWASM
namespace Reptar.AI;

/// <summary>
/// G15 — The Magentic task-ledger prompt pack, adapted from the Microsoft
/// Agent Framework's <c>MagenticDefaultPrompts</c> (which itself encodes the
/// Magentic-One research). These are the battle-tested templates for
/// multi-agent orchestration with a shared fact sheet and plan.
///
/// Placeholders use single braces (<c>{task}</c>, <c>{team}</c>, <c>{facts}</c>,
/// <c>{plan}</c>, <c>{old_facts}</c>, <c>{questions}</c>) substituted with
/// <see cref="Render"/>. A progress-ledger template <b>must keep</b>
/// <c>{schema}</c> so the JSON schema the verdict is parsed against can be
/// injected — the same rule the MSFT templates enforce.
/// </summary>
public static class MagenticPrompts
{
    /// <summary>
    /// The initial fact-sheet survey taken before planning.
    /// Placeholders: <c>{task}</c>.
    /// </summary>
    public static readonly string TaskLedgerFactsPrompt = """
        Below I will present you a request.

        Before we begin addressing the request, please answer the following pre-survey to the best of your ability.
        Keep in mind that you are Ken Jennings-level with trivia, and Mensa-level with puzzles, so there should be a deep well to draw from.

        Here is the request:

        {task}

        Here is the pre-survey:

        1. Please list any specific facts or figures that are GIVEN in the request itself. It is possible that there are none.
        2. Please list any facts that may need to be looked up, and WHERE SPECIFICALLY they might be found.
           In some cases, authoritative sources are mentioned in the request itself.
        3. Please list any facts that may need to be derived (e.g., via logical deduction, simulation, or computation).
        4. Please list any facts that are recalled from memory, hunches, well-reasoned guesses, etc.

        When answering this survey, keep in mind that "facts" will typically be specific names, dates, statistics, etc.
        Your answer should use headings:

        1. GIVEN OR VERIFIED FACTS
        2. FACTS TO LOOK UP
        3. FACTS TO DERIVE
        4. EDUCATED GUESSES

        DO NOT include any other headings or sections in your response. DO NOT list next steps or plans until asked to do so.
        """;

    /// <summary>
    /// Rebuilds the fact sheet on a replan, carrying forward what was learned.
    /// Placeholders: <c>{task}</c>, <c>{old_facts}</c>.
    /// </summary>
    public static readonly string TaskLedgerFactsUpdatePrompt = """
        As a reminder, we are working to solve the following task:

        {task}

        It is clear we are not making as much progress as we would like, but we may have learned something new.
        Please rewrite the following fact sheet, updating it to include anything new we have learned that may be helpful.

        Example edits can include (but are not limited to) adding new guesses, moving educated guesses to verified facts
        if appropriate, etc. Updates may be made to any section of the fact sheet, and more than one section of the fact
        sheet can be edited. This is an especially good time to update educated guesses, so please at least add or update
        one educated guess or hunch, and explain your reasoning.

        Here is the old fact sheet:

        {old_facts}
        """;

    /// <summary>
    /// Creates the initial plan from the fact sheet and team.
    /// Placeholders: <c>{task}</c>, <c>{team}</c>, <c>{facts}</c>.
    /// </summary>
    public static readonly string TaskLedgerPlanPrompt = """
        We are working to address the following user request:

        {task}

        To answer this request we have assembled the following team:

        {team}

        Here is an initial fact sheet to consider:

        {facts}

        Based on the team composition, and known and unknown facts, please devise a short bullet-point plan for addressing
        the original request. Remember, there is no requirement to involve all team members. A team member's particular
        expertise may not be needed for this task.
        """;

    /// <summary>
    /// The replan prompt after a failed attempt. Placeholders: <c>{team}</c>.
    /// The "root cause, then a plan that avoids repeating the mistake"
    /// discipline is the part worth keeping.
    /// </summary>
    public static readonly string TaskLedgerPlanUpdatePrompt = """
        Please briefly explain what went wrong on this last run
        (the root cause of the failure), and then come up with a new plan that takes steps and includes hints to overcome prior
        challenges and especially avoids repeating the same mistakes. As before, the new plan should be concise, expressed in
        bullet-point form, and consider the following team composition:

        {team}
        """;

    /// <summary>
    /// The full task ledger (task + team + facts + plan) handed to each
    /// executor. Placeholders: <c>{task}</c>, <c>{team}</c>, <c>{facts}</c>,
    /// <c>{plan}</c>.
    /// </summary>
    public static readonly string TaskLedgerFullPrompt = """
        We are working to address the following user request:

        {task}

        To answer this request we have assembled the following team:

        {team}

        Here is an initial fact sheet to consider:

        {facts}

        Here is the plan to follow as best as possible:

        {plan}
        """;

    /// <summary>
    /// The progress-ledger check after each executor turn. The model scores
    /// whether the goal is complete, in progress, or invalid, and names the
    /// next team member to act.
    ///
    /// Placeholders: <c>{task}</c>, <c>{team}</c>, <c>{questions}</c>,
    /// <c>{schema}</c>. An override MUST keep <c>{schema}</c> so the JSON
    /// schema the response is parsed against can be injected.
    /// </summary>
    public static readonly string ProgressLedgerPrompt = """
        Below is a ledger of the progress made on the task:

        {questions}

        Given this ledger and the original task, answer the following questions:

        1. Is the goal complete as written?
        2. Is the goal still in progress?
        3. Is the goal invalid as written (impossible to answer)?

        If the goal is not complete, name the team member from the list below who is best suited to advance the goal next,
        including a short instruction for them.

        Team:

        {team}

        Respond with a JSON object matching this schema:

        {schema}
        """;

    /// <summary>
    /// Renders a template by substituting the given placeholder values
    /// (keys without braces, e.g. <c>Render(TaskLedgerPlanPrompt, ("task", request))</c>).
    /// </summary>
    public static string Render(string template, params (string Placeholder, string Value)[] substitutions)
    {
        var result = template;
        foreach (var (placeholder, value) in substitutions)
        {
            result = result.Replace(
                "{" + placeholder + "}",
                value ?? "",
                StringComparison.Ordinal);
        }

        return result;
    }
}
#endif

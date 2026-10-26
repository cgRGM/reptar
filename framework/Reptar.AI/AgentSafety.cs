#if !NETWASM
using System.Text.RegularExpressions;

namespace Reptar.AI;

/// <summary>
/// G18 — A filter applied to every message before it is persisted to session
/// history. Return the (possibly redacted) message to store it, or null to
/// drop it entirely. Register on
/// <see cref="AgentSessionStore"/>.
/// </summary>
public interface IMessagePersistFilter
{
    ChatMessage? BeforePersist(ChatMessage message, string sessionId);
}

/// <summary>
/// Built-in persistence filters: redaction of common secret shapes and
/// predicate-based dropping of tool results.
/// </summary>
public static class PersistFilters
{
    // Top-level regex literals (compiled once per process, AOT-safe).
    private static readonly Regex OpenAiKeyPattern = new("sk-[A-Za-z0-9_-]{16,}", RegexOptions.Compiled);
    private static readonly Regex AnthropicKeyPattern = new("sk-ant-[A-Za-z0-9_-]{16,}", RegexOptions.Compiled);
    private static readonly Regex BearerTokenPattern = new("(?i)bearer\\s+[A-Za-z0-9._-]{16,}", RegexOptions.Compiled);
    private static readonly Regex AwsKeyPattern = new("AKIA[0-9A-Z]{16}", RegexOptions.Compiled);
    private static readonly Regex HexTokenPattern = new("(?<![A-Za-z0-9])[A-Fa-f0-9]{40}(?![A-Za-z0-9])", RegexOptions.Compiled);

    /// <summary>
    /// Masks common credential shapes (OpenAI/Anthropic-style keys, bearer
    /// tokens, AWS access keys, 40-char hex secrets) before storage.
    /// </summary>
    public static IMessagePersistFilter RedactSecrets() => new RedactSecretsFilter();

    /// <summary>
    /// Drops tool-result messages for the named tools entirely — for tools
    /// whose outputs must never touch stored history at all.
    /// </summary>
    public static IMessagePersistFilter DropToolResults(params string[] toolNames) =>
        new DropToolResultsFilter([.. toolNames]);

    /// <summary>
    /// Composes filters in order; the first null result drops the message.
    /// </summary>
    public static IMessagePersistFilter Compose(params IMessagePersistFilter[] filters) =>
        new CompositeFilter(filters);

    private sealed class RedactSecretsFilter : IMessagePersistFilter
    {
        public ChatMessage? BeforePersist(ChatMessage message, string sessionId)
        {
            if (message.Role is not ("user" or "assistant" or "tool") ||
                !ContainsSecret(message.Content))
            {
                return message;
            }

            var redacted = message.Content;
            redacted = AnthropicKeyPattern.Replace(redacted, "sk-ant-[REDACTED]");
            redacted = OpenAiKeyPattern.Replace(redacted, "sk-[REDACTED]");
            redacted = BearerTokenPattern.Replace(redacted, "Bearer [REDACTED]");
            redacted = AwsKeyPattern.Replace(redacted, "[REDACTED-AWS-KEY]");
            redacted = HexTokenPattern.Replace(redacted, "[REDACTED]");

            return message with { Content = redacted };
        }

        private static bool ContainsSecret(string content) =>
            OpenAiKeyPattern.IsMatch(content) ||
            BearerTokenPattern.IsMatch(content) ||
            AwsKeyPattern.IsMatch(content) ||
            HexTokenPattern.IsMatch(content);
    }

    private sealed class DropToolResultsFilter(HashSet<string> toolNames)
        : IMessagePersistFilter
    {
        public ChatMessage? BeforePersist(ChatMessage message, string sessionId) =>
            message.Role == "tool" && message.Name is { } name && toolNames.Contains(name)
                ? null
                : message;
    }

    private sealed class CompositeFilter(IMessagePersistFilter[] filters) : IMessagePersistFilter
    {
        public ChatMessage? BeforePersist(ChatMessage message, string sessionId)
        {
            var current = message;
            foreach (var filter in filters)
            {
                if (filter.BeforePersist(current, sessionId) is not { } filtered)
                {
                    return null;
                }

                current = filtered;
            }

            return current;
        }
    }
}

/// <summary>
/// G21 — Isolation-key-scoped session store. Every session id is composed
/// with the caller's isolation key (user id, tenant id), so one tenant can
/// never address another tenant's sessions even by guessing ids: all lookups
/// go through <c>{isolationKey}:{sessionId}</c>.
///
/// Use one instance per authenticated principal:
/// <code>
/// var store = new ScopedAgentSessionStore(inner, $"user:{ctx.User!.Id}");
/// </code>
/// </summary>
public sealed class ScopedAgentSessionStore
{
    private readonly AgentSessionStore _inner;
    private readonly string _key;

    public ScopedAgentSessionStore(AgentSessionStore inner, string isolationKey)
    {
        if (string.IsNullOrWhiteSpace(isolationKey))
            throw new ArgumentException("Isolation key must be non-empty.", nameof(isolationKey));

        _inner = inner;
        _key = isolationKey.Trim();
    }

    public string IsolationKey => _key;

    public AgentSessionStore Inner => _inner;

    public Task EnsureCreatedAsync() => _inner.EnsureCreatedAsync();

    /// <summary>The bare id as the caller sees it; the scoped id never leaks.</summary>
    public string ScopedId(string sessionId) =>
        sessionId.StartsWith($"{_key}:", StringComparison.Ordinal) ? sessionId : $"{_key}:{sessionId}";

    public async Task<AgentSessionRow> CreateSessionAsync(string title)
    {
        var scopedId = ScopedId(Ids.New("sess"));
        var row = await _inner.CreateSessionAsync(title, scopedId);
        // The caller only ever sees the bare id; the isolation key stays
        // internal to the store.
        return row with { Id = Unscope(row.Id) };
    }

    public async Task<AgentSessionRow?> FindSessionAsync(string sessionId)
    {
        var row = await _inner.FindSessionAsync(ScopedId(sessionId));
        if (row is null)
        {
            return null;
        }

        // A row found under this scope always belongs to this tenant.
        return row with { Id = Unscope(row.Id) };
    }

    public async Task<List<AgentSessionRow>> ListSessionsAsync(int limit = 25)
    {
        var rows = await _inner.Db.ListAsync<AgentSessionRow>(
            _inner.Sessions,
            "where id like @prefix order by rowid desc",
            ("prefix", $"{_key}:%"),
            limit: limit);

        return rows.Select(row => row with { Id = Unscope(row.Id) }).ToList();
    }

    public Task<string> AppendMessageAsync(string sessionId, ChatMessage message) =>
        _inner.AppendMessageAsync(ScopedId(sessionId), message);

    public Task<List<ChatMessage>> LoadMessagesAsync(string sessionId) =>
        _inner.LoadMessagesAsync(ScopedId(sessionId));

    public Task<bool> DeleteSessionAsync(string sessionId) =>
        _inner.DeleteSessionAsync(ScopedId(sessionId));

    private string Unscope(string scopedId) =>
        scopedId.StartsWith($"{_key}:", StringComparison.Ordinal)
            ? scopedId[(_key.Length + 1)..]
            : scopedId;
}
#endif

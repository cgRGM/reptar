#:project ../../../framework/Reptar/Reptar.csproj
#:project ../../../framework/Reptar.AI/Reptar.AI.csproj

using Reptar;
using Reptar.AI;

if (args.Length < 3)
    throw new ArgumentException("Usage: app <append|verify|fork|seed-flat|import> <db> <session> [count|target]");
var mode = args[0];
var session = args[2];
using var db = new SqliteDbClient(args[1]);
var store = new SessionTreeStore(db);
await store.EnsureCreatedAsync();
switch (mode)
{
    case "append":
        var count = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        for (var index = 0; index < count; index++)
        {
            var id = await store.AppendAsync(session, new ChatMessage(AgentRoles.User, $"message-{Environment.ProcessId}-{index}"));
            Console.WriteLine(id); // Emitted only after the append transaction commits.
            await Console.Out.FlushAsync();
        }
        break;
    case "verify":
        var entries = await store.EntriesAsync(session);
        var branch = await store.BranchEntriesAsync(session);
        var messages = await store.LoadBranchAsync(session);
        if (entries.Select(entry => entry.Id).Distinct().Count() != entries.Count || branch.Count != entries.Count)
            throw new InvalidOperationException("History is duplicated, orphaned, or disconnected from its active leaf.");
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].Seq != index + 1 || entries[index].SessionId != session ||
                entries[index].ParentId != (index == 0 ? null : entries[index - 1].Id))
                throw new InvalidOperationException("Append sequence or parent chain is corrupt.");
            if (entries[index].ToolCalls is not null && messages[index].ToolCalls?.Any() != true)
                throw new InvalidOperationException("Cold-read tool-call metadata was lost.");
            Console.WriteLine(entries[index].Id);
        }
        if (entries.Count > 0 && await store.LeafAsync(session) != entries[^1].Id)
            throw new InvalidOperationException("Leaf does not identify the last committed append.");
        break;
    case "fork":
        if (!await store.ForkAsync(session, args[3]))
            throw new InvalidOperationException("Fork was rejected.");
        break;
    case "seed-flat":
        await db.EnsureCreatedAsync(ReptarAgentSchema.Sessions, ReptarAgentSchema.Messages);
        var legacy = new AgentSessionStore(db, ReptarAgentSchema.Sessions, ReptarAgentSchema.Messages);
        await legacy.CreateSessionAsync("previous release", session);
        await legacy.AppendMessageAsync(session, new ChatMessage(AgentRoles.User, "legacy user"));
        await legacy.AppendMessageAsync(session, new ChatMessage(AgentRoles.Assistant, "legacy reply",
            ToolCalls: [new AiToolCall("legacy_call", "read_file", "{\"path\":\"legacy.txt\"}")]));
        break;
    case "import":
        // Constructing the flat store registers its cold-read projection mappers.
        _ = new AgentSessionStore(db, ReptarAgentSchema.Sessions, ReptarAgentSchema.Messages);
        Console.WriteLine(await store.ImportFlatAsync(session, ReptarAgentSchema.Messages));
        break;
    default:
        throw new ArgumentException($"Unknown durability probe mode: {mode}");
}

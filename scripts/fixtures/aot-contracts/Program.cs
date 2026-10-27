using Reptar;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

try { await RunAsync(args); }
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

static async Task RunAsync(string[] args)
{
    if (RuntimeFeature.IsDynamicCodeSupported)
        throw new InvalidOperationException("This gate requires a published Native AOT executable.");
    var mode = args[0];
    using var db = new SqliteDbClient(args[1]);
    if (mode == "seed")
    {
        await db.ExecuteAsync("CREATE TABLE contracts (id TEXT PRIMARY KEY, display_name TEXT NOT NULL)");
        await db.ExecuteAsync("INSERT INTO contracts VALUES (@id, @name)", ("id", "owned"), ("name", "cold-native"));
    }
    else if (mode == "cold")
    {
        // No ColdRow instance has been constructed in this process. Registration
        // must come from the actual source generator's module initializer.
        var row = await db.QuerySingleOrDefaultAsync<ColdRow>("SELECT id, display_name FROM contracts");
        if (row?.Id != "owned" || row.DisplayName != "cold-native" || EntityMapper<ColdRow>.Factory is null)
            throw new InvalidOperationException("Cold generated mapping failed.");
        var reordered = await db.QuerySingleOrDefaultAsync<ColdRow>("SELECT display_name, id FROM contracts");
        if (reordered != row)
            throw new InvalidOperationException("Generated ordinal mapping failed.");
        var codec = new SourceGenJsonCodec(ContractJson.Default);
        var text = codec.Serialize(row);
        if (codec.Deserialize<ColdRow>(text) != row)
            throw new InvalidOperationException("Generated JSON metadata roundtrip failed.");
        var count = await db.QuerySingleOrDefaultAsync<long>("SELECT COUNT(*) FROM contracts");
        if (count != 1 || await db.CountAsync(ContractsTable()) != 1)
            throw new InvalidOperationException("Scalar/built-in count mapping failed.");
        await db.ExecuteAsync("UPDATE contracts SET display_name = @DisplayName WHERE id = @Id", row);
    }
    else if (mode == "bad-row")
    {
        await RejectAsync(async () => { await db.QuerySingleOrDefaultAsync<UnregisteredRow>("SELECT display_name AS value FROM contracts"); }, "source-generated row mapper");
    }
    else if (mode == "bad-parameter")
    {
        await RejectAsync(async () => { await db.ExecuteAsync("UPDATE contracts SET display_name = @name", new { name = "must-not-write" }); }, "SQL parameter");
        var value = await db.QuerySingleOrDefaultAsync<string>("SELECT display_name FROM contracts");
        if (value != "cold-native")
            throw new InvalidOperationException("Rejected binding mutated data.");
    }
    else if (mode is "bad-insert" or "bad-update")
    {
        var table = ContractsTable();
        await RejectAsync(async () =>
        {
            if (mode == "bad-insert")
                await db.InsertAsync(table, new { Id = "forbidden", DisplayName = "must-not-write" });
            else
                await db.UpdateAsync(table, new { DisplayName = "must-not-write" }, "owned");
        }, "SQL parameter");
        if (await db.CountAsync(table) != 1 ||
            await db.QuerySingleOrDefaultAsync<string>("SELECT display_name FROM contracts") != "cold-native")
            throw new InvalidOperationException("Rejected table write mutated data.");
    }
    else if (mode == "bad-json")
    {
        await RejectAsync(() =>
        {
            _ = new DefaultJsonCodec().Serialize(new UnknownJson("must-not-disappear"));
            return Task.CompletedTask;
        }, "JSON metadata");
        await RejectAsync(() => { _ = new DefaultJsonCodec().SerializeUtf8(new UnknownJson("must-not-disappear")); return Task.CompletedTask; }, "JSON metadata");
        await RejectAsync(() => { _ = new DefaultJsonCodec().Deserialize<UnknownJson>("{\"value\":\"must-not-disappear\"}"); return Task.CompletedTask; }, "JSON metadata");
        await RejectAsync(() => { _ = new DefaultJsonCodec().Deserialize<UnknownJson>("{\"value\":\"must-not-disappear\"}"u8); return Task.CompletedTask; }, "JSON metadata");
        await RejectAsync(() => { _ = ReptarJson.Deserialize<UnknownJson>("{\"value\":\"must-not-disappear\"}"); return Task.CompletedTask; }, "JSON metadata");
    }
    else
    {
        throw new ArgumentException("Unknown fixture mode.");
    }
    Console.WriteLine($"PASS {mode}");
}

static TableDef ContractsTable() => TableDef.Define("contracts",
    ReptarApi.Col.Text("id").PrimaryKey(), ReptarApi.Col.Text("display_name"));

static async Task RejectAsync(Func<Task> operation, string diagnostic)
{
    try { await operation(); }
    catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
    {
        if (!error.Message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Missing actionable {diagnostic} diagnostic: {error.Message}", error);
        return;
    }
    throw new InvalidOperationException($"Unsupported {diagnostic} shape was silently accepted.");
}

[ReptarEntity]
public sealed partial record ColdRow(string Id, string DisplayName);

public sealed class UnregisteredRow
{
    private UnregisteredRow() { }
    public string? Value { get; init; }
}
public sealed record UnknownJson(string Value);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ColdRow))]
internal partial class ContractJson : JsonSerializerContext;

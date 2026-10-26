using Reptar;
using Xunit;

namespace Reptar.Tests;

[ReptarEntity]
public sealed partial record ProbeRow(int Id, string Title);

/// <summary>
/// Regression tests for the database-layer audit fixes: Server-Timing
/// composition, transaction instrumentation, SqlParamParser caching,
/// parameterized LIMIT/OFFSET, and N+1 query detection.
/// </summary>
public sealed class DbAuditFixTests
{
    private static readonly TableDef Probe = ReptarApi.DefineTable("pagination_probe", new
    {
        Id = ReptarApi.Col.Int("id").PrimaryKey(),
        Title = ReptarApi.Col.Text("title").NotNull(),
    });

    private sealed class RecordingLogger : IAppLogger
    {
        public List<RequestTelemetry> Requests { get; } = [];
        public List<string> Warnings { get; } = [];

        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message) { }
        public void Request(in RequestTelemetry telemetry) => Requests.Add(telemetry);
    }

    private static async Task SeedProbeAsync(IDbClient db, int count)
    {
        var statements = new List<DbStatement>();

        for (var i = 1; i <= count; i++)
        {
            statements.Add(new DbStatement(
                "INSERT INTO \"pagination_probe\" (\"id\", \"title\") VALUES (@id, @title)",
                new (string, object?)[] { ("id", i), ("title", $"row {i}") }));
        }

        await db.BatchAsync(statements);
    }

    private static async Task<ReptarApp> CreateProbeAppAsync(int rowCount)
    {
        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
        });

        await app.Db.EnsureCreatedAsync(Probe);
        await SeedProbeAsync(app.Db, rowCount);
        return app;
    }

    [Fact]
    public async Task ListAsync_pagination_binds_limit_offset_and_keeps_templates_stable()
    {
        var app = await CreateProbeAppAsync(25);

        app.Get("/page", async ctx =>
        {
            var page1 = await ctx.Db.ListAsync<ProbeRow>(Probe, "ORDER BY id", limit: 10);
            var page2 = await ctx.Db.ListAsync<ProbeRow>(Probe, "ORDER BY id", limit: 10, offset: 10);
            var page3 = await ctx.Db.ListAsync<ProbeRow>(Probe, "ORDER BY id", limit: 10, offset: 20);

            return ctx.Text(string.Join(
                "|",
                string.Join(",", page1.Select(row => row.Id)),
                string.Join(",", page2.Select(row => row.Id)),
                string.Join(",", page3.Select(row => row.Id))));
        });

        var client = app.CreateTestClient();
        var response = await client.GetAsync("/page");

        Assert.Equal(200, response.Status);
        Assert.Equal("1,2,3,4,5,6,7,8,9,10|11,12,13,14,15,16,17,18,19,20|21,22,23,24,25", response.BodyText);

        // Different pages must execute the SAME SQL template (bound
        // parameters), which is what keeps mapper caches bounded and lets
        // N+1 detection group repeated statements. The instrumented client
        // records ListAsync under a stable per-table label, so three pages
        // surface as one template executed three times.
        var metrics = client.LastDbMetrics;
        Assert.NotNull(metrics);
        Assert.True(metrics!.TryGetRepeatedTemplate(3, out var template, out var executions));
        Assert.Contains("pagination_probe", template);
        Assert.Equal(3, executions);
    }

    [Fact]
    public async Task TableQuery_pagination_binds_filters_and_pages_together()
    {
        var app = await CreateProbeAppAsync(25);

        var page = await app.Db
            .From<ProbeRow>(Probe)
            .Where(Probe.Col<int>("id").Gt(5))
            .OrderBy(Probe.Col<int>("id").Asc())
            .Limit(10)
            .Offset(10)
            .ToListAsync();

        // Rows 6..25 ordered, skip 10 (6..15), take 10 -> 16..25.
        Assert.Equal(10, page.Count);
        Assert.Equal(16, page[0].Id);
        Assert.Equal(25, page[^1].Id);
    }

    [Fact]
    public async Task TransactionAsync_inner_statements_are_instrumented()
    {
        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
        });

        await app.Db.ExecuteAsync("CREATE TABLE txn_probe (id INTEGER PRIMARY KEY, name TEXT)");

        app.Get("/txn", async ctx =>
        {
            await ctx.Db.TransactionAsync(async tx =>
            {
                await tx.ExecuteAsync(
                    "INSERT INTO txn_probe (name) VALUES (@name)",
                    ("name", "alpha"));
                var row = await tx.QuerySingleOrDefaultAsync<DbCount>(
                    "SELECT COUNT(*) AS count FROM txn_probe");
                return row?.Count ?? 0;
            });

            return ctx.Text("ok");
        });

        var client = app.CreateTestClient();
        var response = await client.GetAsync("/txn");

        Assert.Equal(200, response.Status);
        var metrics = client.LastDbMetrics;
        Assert.NotNull(metrics);
        // Both statements inside the transaction must be visible to
        // Server-Timing / telemetry (previously the pass-through client
        // recorded nothing inside transactions).
        Assert.True(metrics!.QueryCount >= 2, $"expected >= 2 instrumented queries, got {metrics.QueryCount}");
    }

    [Fact]
    public async Task TransactionAsync_real_transactions_add_no_synthetic_batch_entry()
    {
        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
        });

        app.Get("/real-txn", async ctx =>
        {
            await ctx.Db.TransactionAsync(async tx =>
            {
                await tx.ExecuteAsync("CREATE TABLE IF NOT EXISTS buf_probe (id INTEGER)");
                await tx.ExecuteAsync("CREATE TABLE IF NOT EXISTS buf_probe2 (id INTEGER)");
            });

            return ctx.Text("ok");
        });

        var client = app.CreateTestClient();
        var response = await client.GetAsync("/real-txn");

        Assert.Equal(200, response.Status);
        var metrics = client.LastDbMetrics;
        Assert.NotNull(metrics);

        // SQLite exposes a real transaction: the two inner statements are
        // recorded individually and no synthetic "transaction (batched)"
        // entry is added on top of them.
        Assert.Equal(2, metrics!.QueryCount);
    }

    [Fact]
    public async Task NPlus1_loop_is_detected_and_reported()
    {
        var logger = new RecordingLogger();
        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
            Log = logger,
        });

        await app.Db.ExecuteAsync("CREATE TABLE n_plus_one_probe (id INTEGER)");

        app.Use(ReptarApi.Telemetry(new TelemetryOptions
        {
            SampleRate = 1,
            SlowRequestMs = 0,
        }));

        app.Get("/loop", async ctx =>
        {
            for (var i = 0; i < 12; i++)
            {
                await ctx.Db.ExecuteAsync(
                    "INSERT INTO n_plus_one_probe (id) VALUES (@id)",
                    ("id", i));
            }

            return ctx.Text("ok");
        });

        var client = app.CreateTestClient();
        var response = await client.GetAsync("/loop");

        Assert.Equal(200, response.Status);

        Assert.True(client.LastDbMetrics!.TryGetRepeatedTemplate(10, out var template, out var executions));
        Assert.Equal(12, executions);
        Assert.Contains("n_plus_one_probe", template);

        Assert.Contains(
            logger.Warnings,
            warning => warning.Contains("n_plus_1_query") && warning.Contains("12x"));
    }

    [Fact]
    public void SqlParamParser_caches_rewrites_per_sql()
    {
        const string sql = "SELECT * FROM posts WHERE id = @id AND title = @title";

        var first = SqlParamParser.ParseNamedParameters(sql);
        var second = SqlParamParser.ParseNamedParameters(sql);

        Assert.Same(first.Sql, second.Sql);
        Assert.Same(first.ParamNames, second.ParamNames);
        Assert.Equal("SELECT * FROM posts WHERE id = ? AND title = ?", first.Sql);
        Assert.Equal(["id", "title"], first.ParamNames);

        var postgresFirst = SqlParamParser.ParsePostgresParameters(sql);
        var postgresSecond = SqlParamParser.ParsePostgresParameters(sql);

        Assert.Same(postgresFirst.Sql, postgresSecond.Sql);
        Assert.Equal("SELECT * FROM posts WHERE id = $1 AND title = $2", postgresFirst.Sql);

        var other = SqlParamParser.ParseNamedParameters("SELECT 1");
        Assert.NotSame(first.Sql, other.Sql);
    }

    [Fact]
    public async Task PostgresDbClient_binds_pages_with_named_and_positional_parameters()
    {
        var client = new PostgresDbClient("mock:");

        await client.ListAsync<Widget>(TestSchema.Widgets, limit: 5);
        Assert.Equal("SELECT * FROM \"widgets\" LIMIT $1", client.LastExecutedSql);
        Assert.Equal(new object?[] { 5 }, client.LastExecutedParams);

        await client.ListAsync<Widget>(
            TestSchema.Widgets,
            "\"count\" > $1",
            new object?[] { 7 },
            limit: 10,
            offset: 20);
        Assert.Equal(
            "SELECT * FROM \"widgets\" WHERE \"count\" > $1 LIMIT $2 OFFSET $3",
            client.LastExecutedSql);
        Assert.Equal(new object?[] { 7, 10, 20 }, client.LastExecutedParams);
    }

    [Fact]
    public async Task MySqlDbClient_binds_pages_with_named_parameters()
    {
        var client = new MySqlDbClient("mock:");

        await client.ListAsync<Widget>(TestSchema.Widgets, limit: 10, offset: 5);
        Assert.Equal("SELECT * FROM `widgets` LIMIT @__limit OFFSET @__offset", client.LastExecutedSql);
    }

    [Fact]
    public void Exotic_parameter_shapes_keep_literal_paging()
    {
        // Positional value arrays and raw POCOs are not named shapes; the
        // drivers keep the previous literal LIMIT/OFFSET behavior for them.
        Assert.False(SqlPagination.SupportsNamedParams(new object[] { 1, 2 }));
        Assert.False(SqlPagination.SupportsNamedParams(new Widget("w", "name", 1)));

        // Named shapes are all supported.
        Assert.True(SqlPagination.SupportsNamedParams(null));
        Assert.True(SqlPagination.SupportsNamedParams(new Dictionary<string, object?>()));
        Assert.True(SqlPagination.SupportsNamedParams(("id", 1)));
        Assert.True(SqlPagination.SupportsNamedParams(new (string, object?)[] { ("id", 1), ("title", "t") }));

        // Mutation safety: the caller's parameter object is never modified.
        var original = new Dictionary<string, object?> { ["id"] = 1 };
        var merged = SqlPagination.MergeNamed(original, limit: 10, offset: 20);
        Assert.Single(original);
        Assert.Equal(1, original["id"]);
        Assert.Equal(10, merged["__limit"]);
        Assert.Equal(20, merged["__offset"]);
    }
}

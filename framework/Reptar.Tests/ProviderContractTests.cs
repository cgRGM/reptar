using static Reptar.ReptarApi;

namespace Reptar.Tests;

/// <summary>
/// The same data-layer contract against every native provider. SQLite always
/// runs; Postgres/MySQL run when their server is reachable and fail hard when
/// *_TEST=1 is set (CI uses that to prevent silent skips).
/// </summary>
public abstract class ProviderContractTests
{
    protected abstract string ProviderName { get; }
    protected abstract ISqlDialect Dialect { get; }
    protected abstract bool RequireServer { get; }
    protected abstract IDbClient CreateClient();

    protected static readonly TableDef Widgets = TableDef.Define(
        "provider_widgets",
        Col.Text("id").PrimaryKey(),
        Col.Text("name").NotNull(),
        Col.Int("count").NotNull().Default(0));

    protected sealed class ClientScope : IDisposable
    {
        public ClientScope(IDbClient db) => Db = db;

        public IDbClient Db { get; }

        public void Dispose()
        {
            if (Db is IDisposable disposable)
                disposable.Dispose();
        }
    }

    protected async Task<ClientScope?> TryCreateScopeAsync()
    {
        ClientScope? scope = null;

        try
        {
            scope = new ClientScope(CreateClient());
            await scope.Db.QuerySingleOrDefaultAsync<DbCount>("SELECT 1 AS count");
            return scope;
        }
        catch when (!RequireServer)
        {
            scope?.Dispose();
            return null;
        }
    }

    private string Q(string name) => Dialect.QuoteIdentifier(name);

    private async Task ResetAsync(IDbClient db)
    {
        await db.ExecuteAsync($"DROP TABLE IF EXISTS {Q("provider_widgets")}");
    }

    [Fact]
    public async Task Crud_round_trip()
    {
        using var scope = await TryCreateScopeAsync();
        if (scope is null)
            return;

        var db = scope.Db;
        await ResetAsync(db);
        await db.EnsureCreatedAsync(Widgets);

        await db.InsertAsync(Widgets, new Widget("w1", "Gear", 10));
        await db.InsertAsync(Widgets, new Widget("w2", "Sprocket", 25));

        var fetched = await db.GetAsync<Widget>(Widgets, "w1");
        Assert.NotNull(fetched);
        Assert.Equal("Gear", fetched.Name);
        Assert.Equal(10, fetched.Count);

        var list = await db.ListAsync<Widget>(Widgets, "WHERE count > 15");
        Assert.Single(list);
        Assert.Equal("w2", list[0].Id);

        Assert.Equal(1, await db.UpdateAsync(
            Widgets,
            new { Name = "Super Gear", Count = 99 },
            "w1"));

        var updated = await db.GetAsync<Widget>(Widgets, "w1");
        Assert.NotNull(updated);
        Assert.Equal("Super Gear", updated.Name);
        Assert.Equal(99, updated.Count);

        Assert.True(await db.ExistsAsync(Widgets, "id = 'w1'"));
        Assert.Equal(2, await db.CountAsync(Widgets));

        Assert.Equal(1, await db.DeleteAsync(Widgets, "w2"));
        Assert.Null(await db.GetAsync<Widget>(Widgets, "w2"));
    }

    [Fact]
    public async Task Transaction_commit_is_visible_outside()
    {
        using var scope = await TryCreateScopeAsync();
        if (scope is null)
            return;

        var db = scope.Db;
        await ResetAsync(db);
        await db.EnsureCreatedAsync(Widgets);

        await db.TransactionAsync(async tx =>
        {
            await tx.InsertAsync(Widgets, new Widget("tx1", "Committed", 1));

            var inside = await tx.GetAsync<Widget>(Widgets, "tx1");
            Assert.NotNull(inside);
            Assert.Equal("Committed", inside.Name);
        });

        var outside = await db.GetAsync<Widget>(Widgets, "tx1");
        Assert.NotNull(outside);
        Assert.Equal("Committed", outside.Name);
    }

    [Fact]
    public async Task Transaction_rollback_hides_writes()
    {
        using var scope = await TryCreateScopeAsync();
        if (scope is null)
            return;

        var db = scope.Db;
        await ResetAsync(db);
        await db.EnsureCreatedAsync(Widgets);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await db.TransactionAsync(async tx =>
            {
                await tx.InsertAsync(Widgets, new Widget("tx2", "RolledBack", 1));

                var inside = await tx.GetAsync<Widget>(Widgets, "tx2");
                Assert.NotNull(inside);

                throw new InvalidOperationException("rollback");
            });
        });

        Assert.Null(await db.GetAsync<Widget>(Widgets, "tx2"));
    }

    [Fact]
    public async Task Nested_transaction_throws()
    {
        using var scope = await TryCreateScopeAsync();
        if (scope is null)
            return;

        var db = scope.Db;
        await ResetAsync(db);
        await db.EnsureCreatedAsync(Widgets);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await db.TransactionAsync(async tx =>
            {
                await tx.TransactionAsync(_ => Task.CompletedTask);
            });
        });
    }

    [Fact]
    public async Task Batch_is_atomic()
    {
        using var scope = await TryCreateScopeAsync();
        if (scope is null)
            return;

        var db = scope.Db;
        await ResetAsync(db);
        await db.EnsureCreatedAsync(Widgets);

        var insert = $"INSERT INTO {Q("provider_widgets")} " +
            $"({Q("id")}, {Q("name")}, {Q("count")}) VALUES ('batch1', 'Batch', 1)";
        var bad = $"INSERT INTO {Q("provider_missing_table")} " +
            $"({Q("id")}) VALUES ('x')";

        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.BatchAsync([new DbStatement(insert), new DbStatement(bad)]));

        Assert.Null(await db.GetAsync<Widget>(Widgets, "batch1"));
    }

    [Fact]
    public async Task Concurrent_inserts_all_land()
    {
        using var scope = await TryCreateScopeAsync();
        if (scope is null)
            return;

        var db = scope.Db;
        await ResetAsync(db);
        await db.EnsureCreatedAsync(Widgets);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            db.InsertAsync(
                Widgets,
                new Widget($"concurrent-{index}", $"Worker {index}", index))));

        Assert.Equal(16, await db.CountAsync(Widgets));
    }

    [Fact]
    public async Task Migration_apply_check_checksum_and_lock_release()
    {
        using var scope = await TryCreateScopeAsync();
        if (scope is null)
            return;

        var db = scope.Db;
        var tableName = $"provider_migration_{Guid.NewGuid():n}"[..32];
        var table = TableDef.Define(tableName, Col.Text("id").PrimaryKey());
        var migrationId = $"mig_{Guid.NewGuid():n}"[..20];
        var upSql = Dialect.CreateTable(table);
        var downSql = $"DROP TABLE IF EXISTS {Q(tableName)}";
        var migration = new SqlMigration(migrationId, upSql, downSql);

        try
        {
            var applied = await MigrationRunner.MigrateAsync(db, [migration]);
            Assert.Single(applied);

            var secondRun = await MigrationRunner.MigrateAsync(db, [migration]);
            Assert.Empty(secondRun);

            var checkExit = await MigrationRunner.RunCliAsync(
                db,
                ["db", "check"],
                [migration]);
            Assert.Equal(0, checkExit);

            var edited = new SqlMigration(migrationId, upSql + "\n-- edited");
            var editedCheckExit = await MigrationRunner.RunCliAsync(
                db,
                ["db", "check"],
                [edited]);
            Assert.Equal(2, editedCheckExit);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                MigrationRunner.MigrateAsync(db, [edited]));

            var lockRows = await db.QuerySingleOrDefaultAsync<DbCount>(
                $"SELECT COUNT(*) AS count FROM {Q("__reptar_migrations_lock")}");
            Assert.Equal(0, lockRows?.Count ?? -1);
        }
        finally
        {
            await db.ExecuteAsync(downSql);
        }
    }
}

public sealed class SqliteProviderContractTests : ProviderContractTests
{
    protected override string ProviderName => "sqlite";
    protected override ISqlDialect Dialect => SqlDialects.Sqlite;
    protected override bool RequireServer => true;
    protected override IDbClient CreateClient() => new SqliteDbClient(":memory:");
}

public sealed class PostgresProviderContractTests : ProviderContractTests
{
    protected override string ProviderName => "postgres";
    protected override ISqlDialect Dialect => SqlDialects.Postgres;
    protected override bool RequireServer =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST") == "1";

    protected override IDbClient CreateClient() =>
        new PostgresDbClient(
            Environment.GetEnvironmentVariable("POSTGRES_URL")
            ?? "Host=localhost;Database=reptar_test;Username=postgres;Password=postgres;");
}

public sealed class MySqlProviderContractTests : ProviderContractTests
{
    protected override string ProviderName => "mysql";
    protected override ISqlDialect Dialect => SqlDialects.MySql;
    protected override bool RequireServer =>
        Environment.GetEnvironmentVariable("MYSQL_TEST") == "1";

    protected override IDbClient CreateClient() =>
        new MySqlDbClient(
            Environment.GetEnvironmentVariable("MYSQL_URL")
            ?? "Server=localhost;Port=3306;Database=reptar_test;User ID=root;Password=root;");
}

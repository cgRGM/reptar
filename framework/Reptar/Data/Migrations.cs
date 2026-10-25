using System.Diagnostics;
using System.Globalization;

namespace Reptar;

public interface IMigration
{
    string Id { get; }
    string Name { get; }
    Task UpAsync(IDbClient db);
    Task DownAsync(IDbClient db);
}

public abstract class Migration : IMigration
{
    public abstract string Id { get; }
    public virtual string Name => GetType().Name;
    public abstract Task UpAsync(IDbClient db);
    public virtual Task DownAsync(IDbClient db) => Task.CompletedTask;
}

public sealed class SqlMigration : IMigration
{
    public string Id { get; }
    public string Name { get; }
    private readonly string _upSql;
    private readonly string? _downSql;

    public SqlMigration(string id, string upSql, string? downSql = null, string? name = null)
    {
        Id = id;
        Name = name ?? id;
        _upSql = upSql;
        _downSql = downSql;
    }

    public Task UpAsync(IDbClient db) => db.ExecuteAsync(_upSql);
    public Task DownAsync(IDbClient db) => _downSql != null ? db.ExecuteAsync(_downSql) : Task.CompletedTask;
}

public sealed record MigrationResult(
    string Id,
    string Name,
    bool Applied,
    TimeSpan Duration,
    string? Error = null);

public static class MigrationRunner
{
    public static async Task EnsureMigrationTableAsync(IDbClient db)
    {
        if (db is MySqlDbClient)
        {
            await db.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS `__reptar_migrations` (
                    `id` VARCHAR(255) PRIMARY KEY,
                    `name` VARCHAR(255) NOT NULL,
                    `applied_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
            ");
            return;
        }

        if (db is PostgresDbClient)
        {
            await db.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS ""__reptar_migrations"" (
                    ""id"" TEXT PRIMARY KEY,
                    ""name"" TEXT NOT NULL,
                    ""applied_at"" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
            ");
            return;
        }

        // SQLite & D1 fallback
        await db.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS ""__reptar_migrations"" (
                ""id"" TEXT PRIMARY KEY,
                ""name"" TEXT NOT NULL,
                ""applied_at"" TEXT NOT NULL
            );
        ");
    }

    public static async Task<HashSet<string>> GetAppliedMigrationIdsAsync(IDbClient db)
    {
        await EnsureMigrationTableAsync(db);

        var sql = db is MySqlDbClient
            ? "SELECT `id` FROM `__reptar_migrations`"
            : @"SELECT ""id"" FROM ""__reptar_migrations""";

        var rows = await db.QueryAsync<MigrationRow>(sql);
        return new HashSet<string>(rows.Select(r => r.Id), StringComparer.Ordinal);
    }

    public static async Task<IReadOnlyList<MigrationResult>> MigrateAsync(
        IDbClient db,
        IEnumerable<IMigration> migrations)
    {
        await EnsureMigrationTableAsync(db);
        var applied = await GetAppliedMigrationIdsAsync(db);
        var ordered = migrations.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        var results = new List<MigrationResult>();

        foreach (var migration in ordered)
        {
            if (applied.Contains(migration.Id))
            {
                continue;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                await migration.UpAsync(db);
                sw.Stop();

                var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                var insertSql = db is MySqlDbClient
                    ? "INSERT INTO `__reptar_migrations` (`id`, `name`, `applied_at`) VALUES (@id, @name, @applied_at)"
                    : @"INSERT INTO ""__reptar_migrations"" (""id"", ""name"", ""applied_at"") VALUES (@id, @name, @applied_at)";

                await db.ExecuteAsync(insertSql, new Dictionary<string, object?>
                {
                    ["id"] = migration.Id,
                    ["name"] = migration.Name,
                    ["applied_at"] = now
                });

                results.Add(new MigrationResult(migration.Id, migration.Name, true, sw.Elapsed));
            }
            catch (Exception ex)
            {
                sw.Stop();
                results.Add(new MigrationResult(migration.Id, migration.Name, false, sw.Elapsed, ex.Message));
                throw;
            }
        }

        return results;
    }

    public static async Task<IReadOnlyList<MigrationResult>> RollbackAsync(
        IDbClient db,
        IEnumerable<IMigration> migrations,
        int steps = 1)
    {
        if (steps <= 0) return Array.Empty<MigrationResult>();

        await EnsureMigrationTableAsync(db);
        var applied = await GetAppliedMigrationIdsAsync(db);
        var migrationMap = migrations.ToDictionary(m => m.Id, StringComparer.Ordinal);

        // Find applied migrations sorted descending by ID
        var toRollback = applied
            .Where(id => migrationMap.ContainsKey(id))
            .OrderByDescending(id => id, StringComparer.Ordinal)
            .Take(steps)
            .Select(id => migrationMap[id])
            .ToList();

        var results = new List<MigrationResult>();

        foreach (var migration in toRollback)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await migration.DownAsync(db);
                sw.Stop();

                var deleteSql = db is MySqlDbClient
                    ? "DELETE FROM `__reptar_migrations` WHERE `id` = @id"
                    : @"DELETE FROM ""__reptar_migrations"" WHERE ""id"" = @id";

                await db.ExecuteAsync(deleteSql, new Dictionary<string, object?> { ["id"] = migration.Id });
                results.Add(new MigrationResult(migration.Id, migration.Name, true, sw.Elapsed));
            }
            catch (Exception ex)
            {
                sw.Stop();
                results.Add(new MigrationResult(migration.Id, migration.Name, false, sw.Elapsed, ex.Message));
                throw;
            }
        }

        return results;
    }

    public sealed record MigrationRow(string Id);
}

public static class MigrationAppExtensions
{
    private static readonly List<IMigration> _registeredMigrations = new();

    public static ReptarApp AddMigration(this ReptarApp app, IMigration migration)
    {
        _registeredMigrations.Add(migration);
        return app;
    }

    public static ReptarApp AddMigrations(this ReptarApp app, params IMigration[] migrations)
    {
        _registeredMigrations.AddRange(migrations);
        return app;
    }

    public static ReptarApp AddMigration(
        this ReptarApp app,
        string id,
        string upSql,
        string? downSql = null,
        string? name = null)
    {
        _registeredMigrations.Add(new SqlMigration(id, upSql, downSql, name));
        return app;
    }

    public static Task<IReadOnlyList<MigrationResult>> MigrateAsync(this ReptarApp app)
    {
        return MigrationRunner.MigrateAsync(app.Db, _registeredMigrations);
    }

    public static Task<IReadOnlyList<MigrationResult>> RollbackMigrationAsync(this ReptarApp app, int steps = 1)
    {
        return MigrationRunner.RollbackAsync(app.Db, _registeredMigrations, steps);
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Reptar;

public interface IMigration
{
    string Id { get; }
    string Name { get; }
    Task UpAsync(IDbClient db);
    Task DownAsync(IDbClient db);

    /// <summary>
    /// Optional checksum of the migration body. SQL migrations provide one;
    /// custom migrations may leave it null.
    /// </summary>
    string? Checksum => null;
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
    public string UpSql => _upSql;
    public string? Checksum { get; }
    private readonly string _upSql;
    private readonly string? _downSql;

    public SqlMigration(string id, string upSql, string? downSql = null, string? name = null)
    {
        Id = id;
        Name = name ?? id;
        _upSql = upSql;
        _downSql = downSql;
        Checksum = ComputeChecksum(upSql);
    }

    private static string ComputeChecksum(string sql) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)))
            .ToLowerInvariant();

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
    private static IDbClient Unwrap(IDbClient db) =>
        db is InstrumentedDbClient instrumented ? instrumented.Inner : db;

    private static async Task EnsureMigrationLockTableAsync(IDbClient db)
    {
        var sql = db is MySqlDbClient
            ? """
              CREATE TABLE IF NOT EXISTS `__reptar_migrations_lock` (
                  `id` VARCHAR(64) PRIMARY KEY,
                  `owner` VARCHAR(128) NOT NULL,
                  `expires_at` VARCHAR(64) NOT NULL
              )
              """
            : """
              CREATE TABLE IF NOT EXISTS "__reptar_migrations_lock" (
                  "id" TEXT PRIMARY KEY,
                  "owner" TEXT NOT NULL,
                  "expires_at" TEXT NOT NULL
              )
              """;

        await db.ExecuteAsync(sql);
    }

    private static async Task<string> AcquireMigrationLockAsync(IDbClient db)
    {
        await EnsureMigrationLockTableAsync(db);

        var owner = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O");

        var upsert = db is MySqlDbClient
            ? """
              INSERT INTO `__reptar_migrations_lock` (`id`, `owner`, `expires_at`)
              VALUES ('migrations', @owner, @expiresAt)
              ON DUPLICATE KEY UPDATE
                  `owner` = IF(`expires_at` < @now, VALUES(`owner`), `owner`),
                  `expires_at` = IF(`expires_at` < @now, VALUES(`expires_at`), `expires_at`)
              """
            : """
              INSERT INTO "__reptar_migrations_lock" ("id", "owner", "expires_at")
              VALUES ('migrations', @owner, @expiresAt)
              ON CONFLICT("id") DO UPDATE SET
                  "owner" = excluded."owner",
                  "expires_at" = excluded."expires_at"
              WHERE "__reptar_migrations_lock"."expires_at" < @now
              """;

        await db.ExecuteAsync(upsert, new Dictionary<string, object?>
        {
            ["owner"] = owner,
            ["expiresAt"] = expiresAt,
            ["now"] = now,
        });

        var select = db is MySqlDbClient
            ? "SELECT `owner`, `expires_at` FROM `__reptar_migrations_lock` WHERE `id` = 'migrations'"
            : """SELECT "owner", "expires_at" FROM "__reptar_migrations_lock" WHERE "id" = 'migrations'""";

        var current = await db.QuerySingleOrDefaultAsync<MigrationLockRow>(select);
        if (!string.Equals(current?.Owner, owner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Another migration runner holds the lock (owner {current?.Owner ?? "unknown"}, " +
                $"expires {current?.ExpiresAt ?? "unknown"}). Retry after it finishes.");
        }

        return owner;
    }

    private static Task ReleaseMigrationLockAsync(IDbClient db, string owner)
    {
        var sql = db is MySqlDbClient
            ? "DELETE FROM `__reptar_migrations_lock` WHERE `id` = 'migrations' AND `owner` = @owner"
            : """DELETE FROM "__reptar_migrations_lock" WHERE "id" = 'migrations' AND "owner" = @owner""";

        return db.ExecuteAsync(sql, new Dictionary<string, object?> { ["owner"] = owner });
    }

    public static async Task EnsureMigrationTableAsync(IDbClient db)
    {
        db = Unwrap(db);
        await EnsureMigrationLockTableAsync(db);

        if (db is MySqlDbClient)
        {
            await db.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS `__reptar_migrations` (
                    `id` VARCHAR(255) PRIMARY KEY,
                    `name` VARCHAR(255) NOT NULL,
                    `applied_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    `checksum` VARCHAR(128) NULL
                );
            ");

            try
            {
                await db.ExecuteAsync(
                    "ALTER TABLE `__reptar_migrations` ADD COLUMN `checksum` VARCHAR(128) NULL");
            }
            catch
            {
                // Column already exists on a database created by a newer build.
            }

            return;
        }

        if (db is PostgresDbClient)
        {
            await db.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS ""__reptar_migrations"" (
                    ""id"" TEXT PRIMARY KEY,
                    ""name"" TEXT NOT NULL,
                    ""applied_at"" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    ""checksum"" TEXT
                );
            ");
            await db.ExecuteAsync(
                @"ALTER TABLE ""__reptar_migrations"" ADD COLUMN IF NOT EXISTS ""checksum"" TEXT");
            return;
        }

        // SQLite & D1 fallback
        await db.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS ""__reptar_migrations"" (
                ""id"" TEXT PRIMARY KEY,
                ""name"" TEXT NOT NULL,
                ""applied_at"" TEXT NOT NULL,
                ""checksum"" TEXT
            );
        ");

        try
        {
            await db.ExecuteAsync(
                @"ALTER TABLE ""__reptar_migrations"" ADD COLUMN ""checksum"" TEXT");
        }
        catch
        {
            // Column already exists on a database created by a newer build.
        }
    }

    public static async Task<Dictionary<string, string?>> GetAppliedMigrationChecksumsAsync(
        IDbClient db)
    {
        db = Unwrap(db);
        await EnsureMigrationTableAsync(db);

        var sql = db is MySqlDbClient
            ? "SELECT `id`, `checksum` FROM `__reptar_migrations`"
            : @"SELECT ""id"", ""checksum"" FROM ""__reptar_migrations""";

        var rows = await db.QueryAsync<MigrationChecksumRow>(sql);
        return rows.ToDictionary(
            row => row.Id,
            row => row.Checksum,
            StringComparer.Ordinal);
    }

    public static async Task<HashSet<string>> GetAppliedMigrationIdsAsync(IDbClient db)
    {
        var applied = await GetAppliedMigrationChecksumsAsync(db);
        return new HashSet<string>(applied.Keys, StringComparer.Ordinal);
    }

    public static async Task<IReadOnlyList<MigrationResult>> MigrateAsync(
        IDbClient db,
        IEnumerable<IMigration> migrations)
    {
        db = Unwrap(db);
        var owner = await AcquireMigrationLockAsync(db);

        try
        {
            return await MigrateCoreAsync(db, migrations);
        }
        finally
        {
            await ReleaseMigrationLockAsync(db, owner);
        }
    }

    private static async Task<IReadOnlyList<MigrationResult>> MigrateCoreAsync(
        IDbClient db,
        IEnumerable<IMigration> migrations)
    {
        db = Unwrap(db);
        await EnsureMigrationTableAsync(db);
        var applied = await GetAppliedMigrationChecksumsAsync(db);
        var ordered = migrations.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        var results = new List<MigrationResult>();

        foreach (var migration in ordered)
        {
            if (applied.TryGetValue(migration.Id, out var appliedChecksum))
            {
                if (migration.Checksum is not null &&
                    appliedChecksum is not null &&
                    !string.Equals(
                        migration.Checksum,
                        appliedChecksum,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Migration '{migration.Id}' was modified after it was applied. " +
                        "Create a new migration instead of editing an applied one.");
                }

                continue;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                await migration.UpAsync(db);
                sw.Stop();

                var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                var insertSql = db is MySqlDbClient
                    ? "INSERT INTO `__reptar_migrations` (`id`, `name`, `applied_at`, `checksum`) VALUES (@id, @name, @applied_at, @checksum)"
                    : @"INSERT INTO ""__reptar_migrations"" (""id"", ""name"", ""applied_at"", ""checksum"") VALUES (@id, @name, @applied_at, @checksum)";

                await db.ExecuteAsync(insertSql, new Dictionary<string, object?>
                {
                    ["id"] = migration.Id,
                    ["name"] = migration.Name,
                    ["applied_at"] = now,
                    ["checksum"] = migration.Checksum
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
        if (steps <= 0)
            return Array.Empty<MigrationResult>();

        db = Unwrap(db);
        var owner = await AcquireMigrationLockAsync(db);

        try
        {
            return await RollbackCoreAsync(db, migrations, steps);
        }
        finally
        {
            await ReleaseMigrationLockAsync(db, owner);
        }
    }

    private static async Task<IReadOnlyList<MigrationResult>> RollbackCoreAsync(
        IDbClient db,
        IEnumerable<IMigration> migrations,
        int steps)
    {
        if (steps <= 0) return Array.Empty<MigrationResult>();

        db = Unwrap(db);
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

    public sealed record MigrationChecksumRow(string Id, string? Checksum);

    public sealed record MigrationLockRow(string Owner, string ExpiresAt);

    public sealed record MigrationHistoryRow(string Id, string Name, string AppliedAt);

    public static async Task<IReadOnlyList<IMigration>> LoadFromDirectoryAsync(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return Array.Empty<IMigration>();

        var files = Directory.GetFiles(directoryPath, "*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        var migrations = new List<IMigration>(files.Length);
        foreach (var file in files)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var content = await File.ReadAllTextAsync(file);

            string upSql = content;
            string? downSql = null;

            const string downMarker = "-- +reptar Down";
            const string upMarker = "-- +reptar Up";

            var downIdx = content.IndexOf(downMarker, StringComparison.OrdinalIgnoreCase);
            if (downIdx >= 0)
            {
                upSql = content[..downIdx].Replace(upMarker, "", StringComparison.OrdinalIgnoreCase).Trim();
                downSql = content[(downIdx + downMarker.Length)..].Trim();
            }
            else if (content.Contains(upMarker, StringComparison.OrdinalIgnoreCase))
            {
                upSql = content.Replace(upMarker, "", StringComparison.OrdinalIgnoreCase).Trim();
            }

            migrations.Add(new SqlMigration(id, upSql, downSql, id));
        }

        return migrations;
    }

    public static async Task<int> RunCliAsync(
        IDbClient db,
        string[] args,
        IEnumerable<IMigration>? registeredMigrations = null,
        string? defaultMigrationsDir = null)
    {
        var dbIdx = Array.FindIndex(args, a => string.Equals(a, "db", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--db", StringComparison.OrdinalIgnoreCase));
        var subCommand = dbIdx >= 0 && dbIdx + 1 < args.Length
            ? args[dbIdx + 1].ToLowerInvariant()
            : "migrate";

        var dirIdx = Array.FindIndex(args, a => string.Equals(a, "--dir", StringComparison.OrdinalIgnoreCase));
        var migrationsDir = dirIdx >= 0 && dirIdx + 1 < args.Length
            ? args[dirIdx + 1]
            : defaultMigrationsDir ?? Path.Combine(Directory.GetCurrentDirectory(), "migrations");

        var allMigrations = new Dictionary<string, IMigration>(StringComparer.Ordinal);
        if (registeredMigrations is not null)
        {
            foreach (var m in registeredMigrations)
                allMigrations[m.Id] = m;
        }

        if (Directory.Exists(migrationsDir))
        {
            var dirMigrations = await LoadFromDirectoryAsync(migrationsDir);
            foreach (var m in dirMigrations)
            {
                allMigrations.TryAdd(m.Id, m);
            }
        }

        var ordered = allMigrations.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();

        if (subCommand is "migrate" or "up")
        {
            if (args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase))
                subCommand = "dry-run";
            else if (args.Contains("--check", StringComparer.OrdinalIgnoreCase))
                subCommand = "check";
        }

        switch (subCommand)
        {
            case "check":
            {
                var applied = await GetAppliedMigrationIdsAsync(db);
                var pending = ordered.Where(m => !applied.Contains(m.Id)).ToList();

                Console.WriteLine(
                    pending.Count == 0
                        ? "Database is up to date."
                        : $"{pending.Count} pending migration(s):");

                foreach (var migration in pending)
                    Console.WriteLine($"  [ ] {migration.Id} ({migration.Name})");

                return pending.Count == 0 ? 0 : 1;
            }

            case "dry-run":
            {
                var applied = await GetAppliedMigrationIdsAsync(db);
                var pending = ordered.Where(m => !applied.Contains(m.Id)).ToList();

                if (pending.Count == 0)
                {
                    Console.WriteLine("Database is up to date.");
                    return 0;
                }

                foreach (var migration in pending)
                {
                    Console.WriteLine($"-- {migration.Id} ({migration.Name})");

                    if (migration is SqlMigration sqlMigration)
                        Console.WriteLine(sqlMigration.UpSql);

                    Console.WriteLine();
                }

                return 0;
            }

            case "status":
            {
                var applied = await GetAppliedMigrationIdsAsync(db);
                Console.WriteLine($"Migrations status ({applied.Count} applied, {ordered.Count(m => !applied.Contains(m.Id))} pending):");
                if (ordered.Count == 0 && applied.Count == 0)
                {
                    Console.WriteLine("  (no migrations found)");
                    return 0;
                }

                foreach (var m in ordered)
                {
                    var state = applied.Contains(m.Id) ? "[x] Applied" : "[ ] Pending";
                    Console.WriteLine($"  {state}  {m.Id} ({m.Name})");
                }

                foreach (var id in applied.Where(id => !allMigrations.ContainsKey(id)).OrderBy(x => x, StringComparer.Ordinal))
                {
                    Console.WriteLine($"  [x] Applied  {id} (database only)");
                }

                return 0;
            }

            case "migrate":
            case "up":
            {
                var results = await MigrateAsync(db, ordered);
                if (results.Count == 0)
                {
                    Console.WriteLine("Database is up to date (0 pending migrations).");
                    return 0;
                }

                foreach (var r in results)
                {
                    Console.WriteLine($"  ✓ Applied {r.Id} ({r.Duration.TotalMilliseconds:0.0}ms)");
                }
                Console.WriteLine($"Successfully applied {results.Count} migration(s).");
                return 0;
            }

            case "rollback":
            case "down":
            {
                var stepsIdx = Array.FindIndex(args, a => string.Equals(a, "--steps", StringComparison.OrdinalIgnoreCase));
                var steps = stepsIdx >= 0 && stepsIdx + 1 < args.Length && int.TryParse(args[stepsIdx + 1], out var parsedSteps)
                    ? parsedSteps
                    : 1;

                var results = await RollbackAsync(db, ordered, steps);
                if (results.Count == 0)
                {
                    Console.WriteLine("No migrations to roll back.");
                    return 0;
                }

                foreach (var r in results)
                {
                    Console.WriteLine($"  ✓ Rolled back {r.Id} ({r.Duration.TotalMilliseconds:0.0}ms)");
                }
                return 0;
            }

            default:
                Console.Error.WriteLine($"Unknown db command '{subCommand}'. Available: status, check, dry-run, migrate, rollback");
                return 1;
        }
    }
}

public static class MigrationAppExtensions
{
    private static readonly List<IMigration> _registeredMigrations = new();

    public static IReadOnlyList<IMigration> RegisteredMigrations => _registeredMigrations;

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

    public static Task<int> RunMigrationCliAsync(this ReptarApp app, string[] args, string? defaultMigrationsDir = null)
    {
        return MigrationRunner.RunCliAsync(app.Db, args, _registeredMigrations, defaultMigrationsDir);
    }
}

using Reptar;

namespace Reptar.Tests;

public class MigrationTests
{
    [Fact]
    public async Task MigrateAsync_executes_pending_migrations_and_records_history()
    {
        using var db = new SqliteDbClient(":memory:");

        var migration1 = new SqlMigration(
            "001_create_posts",
            "CREATE TABLE posts (id TEXT PRIMARY KEY, title TEXT NOT NULL);",
            "DROP TABLE posts;");

        var migration2 = new SqlMigration(
            "002_add_content",
            "ALTER TABLE posts ADD COLUMN content TEXT;",
            null);

        // First run: applies 001 and 002
        var results = await MigrationRunner.MigrateAsync(db, new[] { migration2, migration1 });
        Assert.Equal(2, results.Count);
        Assert.Equal("001_create_posts", results[0].Id);
        Assert.Equal("002_add_content", results[1].Id);
        Assert.True(results[0].Applied);
        Assert.True(results[1].Applied);

        // Verify table and column exist by inserting
        await db.ExecuteAsync("INSERT INTO posts (id, title, content) VALUES ('p1', 'Post 1', 'Content 1');");

        // Second run: no pending migrations
        var secondRun = await MigrationRunner.MigrateAsync(db, new[] { migration1, migration2 });
        Assert.Empty(secondRun);

        // Rollback step 1: should rollback 002
        var rollback1 = await MigrationRunner.RollbackAsync(db, new[] { migration1, migration2 }, steps: 1);
        Assert.Single(rollback1);
        Assert.Equal("002_add_content", rollback1[0].Id);

        var applied = await MigrationRunner.GetAppliedMigrationIdsAsync(db);
        Assert.Contains("001_create_posts", applied);
        Assert.DoesNotContain("002_add_content", applied);

        // Rollback step 2: should rollback 001
        var rollback2 = await MigrationRunner.RollbackAsync(db, new[] { migration1, migration2 }, steps: 1);
        Assert.Single(rollback2);
        Assert.Equal("001_create_posts", rollback2[0].Id);

        var appliedAfter = await MigrationRunner.GetAppliedMigrationIdsAsync(db);
        Assert.Empty(appliedAfter);
    }

    [Fact]
    public async Task App_MigrateAsync_and_RollbackMigrationAsync_flow()
    {
        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = new SqliteDbClient(":memory:")
        });

        app.AddMigration("20261024_01", "CREATE TABLE tags (id TEXT PRIMARY KEY, name TEXT);", "DROP TABLE tags;");

        var results = await app.MigrateAsync();
        Assert.Single(results);
        Assert.Equal("20261024_01", results[0].Id);

        var rollback = await app.RollbackMigrationAsync(1);
        Assert.Single(rollback);
        Assert.Equal("20261024_01", rollback[0].Id);
    }

    [Fact]
    public async Task RunCliAsync_loads_directory_migrations_and_executes_status_migrate_rollback()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"reptar_mig_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "0001_create_items.sql"),
                """
                -- +reptar Up
                CREATE TABLE items (id TEXT PRIMARY KEY, label TEXT NOT NULL);
                -- +reptar Down
                DROP TABLE items;
                """);

            using var db = new SqliteDbClient(":memory:");

            var statusExit = await MigrationRunner.RunCliAsync(db, ["db", "status", "--dir", tempDir]);
            Assert.Equal(0, statusExit);

            var migrateExit = await MigrationRunner.RunCliAsync(db, ["db", "migrate", "--dir", tempDir]);
            Assert.Equal(0, migrateExit);

            var applied = await MigrationRunner.GetAppliedMigrationIdsAsync(db);
            Assert.Contains("0001_create_items", applied);

            var rollbackExit = await MigrationRunner.RunCliAsync(db, ["db", "rollback", "--dir", tempDir]);
            Assert.Equal(0, rollbackExit);

            var appliedAfter = await MigrationRunner.GetAppliedMigrationIdsAsync(db);
            Assert.Empty(appliedAfter);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}

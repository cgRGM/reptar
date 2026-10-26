namespace Reptar.Tests;

public sealed class MigrationChecksumTests
{
    [Fact]
    public async Task Applied_migration_checksum_mismatch_fails_loudly()
    {
        using var db = new SqliteDbClient(":memory:");

        var original = new SqlMigration(
            "0001_checksum",
            "CREATE TABLE checksum_probe (id TEXT PRIMARY KEY)");

        await MigrationRunner.MigrateAsync(db, [original]);

        var lockCount = await db.QuerySingleOrDefaultAsync<DbCount>(
            "SELECT COUNT(*) AS count FROM \"__reptar_migrations_lock\"");
        Assert.Equal(0, lockCount?.Count ?? -1);

        var edited = new SqlMigration(
            "0001_checksum",
            "CREATE TABLE checksum_probe (id TEXT PRIMARY KEY, extra TEXT)");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MigrationRunner.MigrateAsync(db, [edited]));
    }

    [Fact]
    public async Task Migration_check_reports_pending_and_then_clean()
    {
        using var db = new SqliteDbClient(":memory:");
        var migration = new SqlMigration(
            "0002_check",
            "CREATE TABLE check_probe (id TEXT PRIMARY KEY)");

        var pendingExit = await MigrationRunner.RunCliAsync(
            db,
            ["db", "check"],
            [migration]);

        Assert.Equal(1, pendingExit);

        await MigrationRunner.MigrateAsync(db, [migration]);

        var cleanExit = await MigrationRunner.RunCliAsync(
            db,
            ["db", "check"],
            [migration]);

        Assert.Equal(0, cleanExit);
    }
}

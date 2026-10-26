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

        var edited = new SqlMigration(
            "0001_checksum",
            "CREATE TABLE checksum_probe (id TEXT PRIMARY KEY, extra TEXT)");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MigrationRunner.MigrateAsync(db, [edited]));
    }
}

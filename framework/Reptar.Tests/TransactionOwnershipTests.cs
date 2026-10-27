using Reptar;

namespace Reptar.Tests;

public sealed class TransactionOwnershipTests
{
    [Fact]
    public async Task An_ambient_transaction_cannot_attach_to_another_clients_connection()
    {
        using var first = new SqliteDbClient(":memory:");
        using var second = new SqliteDbClient(":memory:");
        await first.ExecuteAsync("CREATE TABLE records (id TEXT PRIMARY KEY)");
        await second.ExecuteAsync("CREATE TABLE records (id TEXT PRIMARY KEY)");
        await Assert.ThrowsAsync<ApplicationException>(() => first.TransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("INSERT INTO records VALUES ('rolled-back')");
            await second.ExecuteAsync("INSERT INTO records VALUES ('independently-committed')");
            throw new ApplicationException("roll back only the first database");
        }));
        Assert.Equal(0L, await first.QuerySingleOrDefaultAsync<long>("SELECT COUNT(*) FROM records"));
        Assert.Equal(1L, await second.QuerySingleOrDefaultAsync<long>("SELECT COUNT(*) FROM records"));
    }

    [Fact]
    public async Task Independent_transactions_can_be_nested_across_clients_without_losing_outer_scope()
    {
        using var first = new SqliteDbClient(":memory:");
        using var second = new SqliteDbClient(":memory:");
        await first.ExecuteAsync("CREATE TABLE records (id TEXT PRIMARY KEY)");
        await second.ExecuteAsync("CREATE TABLE records (id TEXT PRIMARY KEY)");
        await first.TransactionAsync(async outer =>
        {
            await outer.ExecuteAsync("INSERT INTO records VALUES ('first')");
            await second.TransactionAsync(async inner =>
            {
                await inner.ExecuteAsync("INSERT INTO records VALUES ('second')");
            });
            await outer.ExecuteAsync("INSERT INTO records VALUES ('third')");
            Assert.Equal(2L, await outer.QuerySingleOrDefaultAsync<long>("SELECT COUNT(*) FROM records"));
        });
        Assert.Equal(2L, await first.QuerySingleOrDefaultAsync<long>("SELECT COUNT(*) FROM records"));
        Assert.Equal(1L, await second.QuerySingleOrDefaultAsync<long>("SELECT COUNT(*) FROM records"));
    }
}

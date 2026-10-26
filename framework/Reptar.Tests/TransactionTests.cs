using static Reptar.ReptarApi;

namespace Reptar.Tests;

[ReptarEntity("tx_widgets")]
public sealed partial record TxWidget(string Id, string Name);

public sealed class TransactionTests
{
    private static readonly TableDef Widgets = TableDef.Define("tx_widgets",
        Col.Text("id").PrimaryKey(),
        Col.Text("name").NotNull());

    [Fact]
    public async Task Buffered_transaction_commits_writes()
    {
        using var db = new SqliteDbClient(":memory:");
        await db.EnsureCreatedAsync(Widgets);

        await db.TransactionAsync(async tx =>
        {
            await tx.ExecuteAsync(
                "INSERT INTO \"tx_widgets\" (\"id\", \"name\") VALUES ('w1', 'Bolt')");
        });

        var row = await db.GetAsync<TxWidget>(Widgets, "w1");
        Assert.NotNull(row);
        Assert.Equal("Bolt", row!.Name);
    }

    [Fact]
    public async Task Buffered_transaction_rolls_back_on_exception()
    {
        using var db = new SqliteDbClient(":memory:");
        await db.EnsureCreatedAsync(Widgets);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await db.TransactionAsync(async tx =>
            {
                await tx.ExecuteAsync(
                    "INSERT INTO \"tx_widgets\" (\"id\", \"name\") VALUES ('w2', 'Nut')");
                throw new InvalidOperationException("rollback");
            });
        });

        var row = await db.GetAsync<TxWidget>(Widgets, "w2");
        Assert.Null(row);
    }

    [Fact]
    public async Task Buffered_transaction_rejects_reads()
    {
        using var db = new SqliteDbClient(":memory:");
        await db.EnsureCreatedAsync(Widgets);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await db.TransactionAsync(async tx =>
            {
                await tx.QueryAsync<TxWidget>("SELECT * FROM \"tx_widgets\"");
            });
        });
    }
}

using System.Data;
using Microsoft.Data.Sqlite;
using Reptar;

namespace Reptar.Tests;

public enum OrderState
{
    Pending = 0,
    Active = 1,
    Completed = 2,
    Cancelled = 3
}

[ReptarEntity]
public partial record OrderEntity(
    string Id,
    long CustomerId,
    decimal TotalAmount,
    OrderState State,
    DateTime CreatedAt,
    Guid TrackingNumber,
    string? Notes,
    int? DiscountPercent);

[ReptarEntity]
public partial class ProductClass
{
    public string Sku { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public double Price { get; set; }
    public bool InStock { get; set; }
    public int? CategoryId { get; set; }
}

[ReptarEntity]
public partial record struct MetricStruct(string MetricKey, double MetricValue, long TimestampUnix);

public class ReptarEntityTests
{
    [Fact]
    public void SourceGenerator_registers_factories_for_all_entity_shapes()
    {
        Assert.NotNull(EntityMapper<OrderEntity>.Factory);
        Assert.NotNull(EntityMapper<ProductClass>.Factory);
        Assert.NotNull(EntityMapper<MetricStruct>.Factory);
    }

    [Fact]
    public async Task SqliteDbClient_maps_complex_positional_record()
    {
        using var db = new SqliteDbClient(":memory:");
        await db.ExecuteAsync(@"
            CREATE TABLE orders (
                id TEXT PRIMARY KEY,
                customer_id INTEGER NOT NULL,
                total_amount NUMERIC NOT NULL,
                state INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                tracking_number TEXT NOT NULL,
                notes TEXT,
                discount_percent INTEGER
            );
        ");

        var testGuid = Guid.NewGuid();
        var now = new DateTime(2026, 10, 24, 18, 0, 0, DateTimeKind.Utc);

        await db.ExecuteAsync(@"
            INSERT INTO orders (id, customer_id, total_amount, state, created_at, tracking_number, notes, discount_percent)
            VALUES (@id, @customer_id, @total_amount, @state, @created_at, @tracking_number, @notes, @discount_percent)
        ", new Dictionary<string, object?>
        {
            ["id"] = "ord_100",
            ["customer_id"] = 42L,
            ["total_amount"] = 129.99m,
            ["state"] = (int)OrderState.Active,
            ["created_at"] = now.ToString("o"),
            ["tracking_number"] = testGuid.ToString(),
            ["notes"] = null,
            ["discount_percent"] = 15
        });

        var orders = await db.QueryAsync<OrderEntity>("SELECT * FROM orders WHERE id = @id", new { id = "ord_100" });
        Assert.Single(orders);

        var order = orders[0];
        Assert.Equal("ord_100", order.Id);
        Assert.Equal(42L, order.CustomerId);
        Assert.Equal(129.99m, order.TotalAmount);
        Assert.Equal(OrderState.Active, order.State);
        Assert.Equal(testGuid, order.TrackingNumber);
        Assert.Null(order.Notes);
        Assert.Equal(15, order.DiscountPercent);
    }

    [Fact]
    public async Task SqliteDbClient_maps_class_with_get_set_properties()
    {
        using var db = new SqliteDbClient(":memory:");
        await db.ExecuteAsync(@"
            CREATE TABLE products (
                sku TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                price REAL NOT NULL,
                in_stock INTEGER NOT NULL,
                category_id INTEGER
            );
        ");

        await db.ExecuteAsync(@"
            INSERT INTO products (sku, title, price, in_stock, category_id)
            VALUES ('SKU-99', 'Mechanical Keyboard', 149.50, 1, NULL)
        ");

        var products = await db.QueryAsync<ProductClass>("SELECT * FROM products WHERE sku = 'SKU-99'");
        Assert.Single(products);

        var product = products[0];
        Assert.Equal("SKU-99", product.Sku);
        Assert.Equal("Mechanical Keyboard", product.Title);
        Assert.Equal(149.50, product.Price);
        Assert.True(product.InStock);
        Assert.Null(product.CategoryId);
    }

    [Fact]
    public async Task SqliteDbClient_maps_record_struct()
    {
        using var db = new SqliteDbClient(":memory:");
        await db.ExecuteAsync(@"
            CREATE TABLE metrics (
                metric_key TEXT,
                metric_value REAL,
                timestamp_unix INTEGER
            );
        ");

        await db.ExecuteAsync(@"
            INSERT INTO metrics (metric_key, metric_value, timestamp_unix)
            VALUES ('cpu_load', 0.42, 1729800000)
        ");

        var metrics = await db.QueryAsync<MetricStruct>("SELECT * FROM metrics");
        Assert.Single(metrics);

        var m = metrics[0];
        Assert.Equal("cpu_load", m.MetricKey);
        Assert.Equal(0.42, m.MetricValue);
        Assert.Equal(1729800000L, m.TimestampUnix);
    }

    [Fact]
    public void Entity_implements_IDataRecordMapper_directly()
    {
        Assert.True(typeof(IDataRecordMapper<OrderEntity>).IsAssignableFrom(typeof(OrderEntity)));
        Assert.True(typeof(IDataRecordMapper<ProductClass>).IsAssignableFrom(typeof(ProductClass)));
        Assert.True(typeof(IDataRecordMapper<MetricStruct>).IsAssignableFrom(typeof(MetricStruct)));
    }
}

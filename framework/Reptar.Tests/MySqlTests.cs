using MySqlConnector;
using Reptar;

namespace Reptar.Tests;

public class MySqlTests
{
    [Fact]
    public void MySqlDbClient_converts_mysql_and_mariadb_uris_correctly()
    {
        // 1. Full URI with credentials, port, and database
        var conn1 = MySqlDbClient.ConvertUrlToConnectionString(
            "mysql://appuser:secret%40pass@db.example.internal:3307/reptar_prod?sslmode=required");
        Assert.Contains("Server=db.example.internal", conn1);
        Assert.Contains("Port=3307", conn1);
        Assert.Contains("Database=reptar_prod", conn1);
        Assert.Contains("User ID=appuser", conn1);
        Assert.Contains("Password=secret@pass", conn1);
        Assert.Contains("Required", conn1);

        // 2. MariaDB URI with default port (3306) and no password
        var conn2 = MySqlDbClient.ConvertUrlToConnectionString(
            "mariadb://root@localhost/reptar_dev");
        Assert.Contains("Server=localhost", conn2);
        Assert.Contains("Port=3306", conn2);
        Assert.Contains("Database=reptar_dev", conn2);
        Assert.Contains("User ID=root", conn2);

        // 3. Raw ADO.NET connection string passed through untouched
        var raw = "Server=127.0.0.1;Uid=myuser;Pwd=mypass;Database=testdb;";
        var conn3 = MySqlDbClient.ConvertUrlToConnectionString(raw);
        Assert.Equal(raw, conn3);
    }

    [Fact]
    public void SqlDialects_MySql_generates_valid_backtick_ddl_and_indexes()
    {
        var schema = ReptarApi.DefineTable("customers", new
        {
            Id = ReptarApi.Col.Text("id").PrimaryKey(),
            Email = ReptarApi.Col.Text("email").NotNull().Unique(),
            Score = ReptarApi.Col.Int("score").Default(100),
            Balance = ReptarApi.Col.Real("balance").Default(0.0),
            Avatar = ReptarApi.Col.Blob("avatar").Indexed(),
            OrgId = ReptarApi.Col.Text("org_id").References("organizations", "id")
        });

        var ddl = SqlDialects.MySql.CreateTable(schema);
        Assert.Contains("CREATE TABLE IF NOT EXISTS `customers`", ddl);
        Assert.Contains("`id` VARCHAR(255)", ddl);
        Assert.Contains("PRIMARY KEY", ddl);
        Assert.Contains("`email` VARCHAR(255) NOT NULL", ddl);
        Assert.Contains("`score` INT DEFAULT 100", ddl);
        Assert.Contains("`balance` DOUBLE DEFAULT 0", ddl);
        Assert.Contains("`avatar` BLOB", ddl);
        Assert.Contains("REFERENCES `organizations`(`id`)", ddl);

        var indexes = SqlDialects.MySql.CreateIndexes(schema);
        Assert.NotEmpty(indexes);
        Assert.Contains(indexes, idx => idx.Contains("UNIQUE INDEX") && idx.Contains("`customers_email_uq`"));
        Assert.Contains(indexes, idx => idx.Contains("INDEX") && idx.Contains("`customers`") && idx.Contains("`avatar`"));
    }

    [Fact]
    public void SqlDialects_MySql_formats_parameters_and_identifiers()
    {
        Assert.Equal("`accounts`", SqlDialects.MySql.QuoteIdentifier("accounts"));
        Assert.Equal("@tenant_id", SqlDialects.MySql.FormatParameter("tenant_id", 1));
        Assert.Equal(" LIMIT 10 OFFSET 20", SqlDialects.MySql.LimitOffset(10, 20));
        Assert.Equal(" LIMIT 15", SqlDialects.MySql.LimitOffset(15, 0));
    }

    [Fact]
    public async Task MySqlDbClient_handles_crud_and_batch_operations_with_backticks()
    {
        using var client = new MySqlDbClient("mock:");

        // 1. Insert with entity
        var insertCount = await client.InsertAsync(TestSchema.Widgets, new Widget("w_mysql_1", "SpeedWidget", 45));
        Assert.Equal(1, insertCount);
        Assert.NotNull(client.LastExecutedSql);
        Assert.StartsWith("INSERT INTO `widgets`", client.LastExecutedSql);
        Assert.Contains("`id`", client.LastExecutedSql);
        Assert.Contains("`name`", client.LastExecutedSql);
        Assert.Contains("`count`", client.LastExecutedSql);
        Assert.Contains("@Id", client.LastExecutedSql);

        // 2. Insert with dictionary
        await client.InsertAsync(TestSchema.Widgets, new Dictionary<string, object?>
        {
            ["id"] = "w_mysql_2",
            ["name"] = "DictWidget",
            ["count"] = 50
        });
        Assert.Contains("INSERT INTO `widgets`", client.LastExecutedSql);
        Assert.Contains("@id", client.LastExecutedSql);

        // 3. Update with dictionary
        await client.UpdateAsync(TestSchema.Widgets, new Dictionary<string, object?> { ["count"] = 99 }, "w_mysql_1");
        Assert.Equal("UPDATE `widgets` SET `count` = @count WHERE `id` = @__id", client.LastExecutedSql);

        // 4. Update with object
        await client.UpdateAsync(TestSchema.Widgets, new { Name = "RenamedWidget" }, "w_mysql_1");
        Assert.Equal("UPDATE `widgets` SET `name` = @Name WHERE `id` = @__id", client.LastExecutedSql);

        // 5. GetAsync
        await client.GetAsync<Widget>(TestSchema.Widgets, "w_mysql_1");
        Assert.Equal("SELECT * FROM `widgets` WHERE `id` = @id LIMIT 1", client.LastExecutedSql);

        // 6. ListAsync
        await client.ListAsync<Widget>(TestSchema.Widgets, "WHERE count > 20", limit: 25, offset: 5);
        Assert.Equal("SELECT * FROM `widgets` WHERE count > 20 LIMIT @__limit OFFSET @__offset", client.LastExecutedSql);

        // 7. ExistsAsync
        await client.ExistsAsync(TestSchema.Widgets, "`name` = @name", new { name = "SpeedWidget" });
        Assert.Equal("SELECT EXISTS(SELECT 1 FROM `widgets` WHERE `name` = @name)", client.LastExecutedSql);

        // 8. CountAsync
        await client.CountAsync(TestSchema.Widgets, "`count` > 0");
        Assert.Equal("SELECT COUNT(*) AS count FROM `widgets` WHERE `count` > 0", client.LastExecutedSql);

        // 9. DeleteAsync
        await client.DeleteAsync(TestSchema.Widgets, "w_mysql_1");
        Assert.Equal("DELETE FROM `widgets` WHERE `id` = @id", client.LastExecutedSql);

        // 10. DeleteWhereAsync
        await client.DeleteWhereAsync(TestSchema.Widgets, "`count` < @min", new { min = 5 });
        Assert.Equal("DELETE FROM `widgets` WHERE `count` < @min", client.LastExecutedSql);

        // 11. BatchAsync
        var batchCount = await client.BatchAsync(new[]
        {
            new DbStatement("INSERT INTO `widgets` (`id`, `name`) VALUES (@id, @name)", new { id = "b1", name = "B1" }),
            new DbStatement("INSERT INTO `widgets` (`id`, `name`) VALUES (@id, @name)", new { id = "b2", name = "B2" })
        });
        Assert.Equal(2, batchCount);
    }

    [Fact]
    public async Task MySqlDbClient_live_integration_test_when_mysql_is_available()
    {
        var requireMySql = Environment.GetEnvironmentVariable("MYSQL_TEST") == "1";
        var connStr = Environment.GetEnvironmentVariable("MYSQL_URL")
            ?? "Server=localhost;Port=3306;Database=reptar_test;User ID=root;Password=;";

        MySqlDbClient client;
        try
        {
            client = new MySqlDbClient(connStr);
            var testConn = await client.QuerySingleOrDefaultAsync<ScalarVal>("SELECT 1 as val;");
            if (testConn == null && requireMySql)
            {
                Assert.Fail("MySQL connection succeeded but returned null test result.");
            }
        }
        catch
        {
            if (requireMySql) throw;
            // MySQL/MariaDB not running in current local environment; skip live portion gracefully
            return;
        }

        // Live MySQL instance is available - run full end-to-end integration test
        var testTable = ReptarApi.DefineTable("mysql_integration_widgets", new
        {
            Id = ReptarApi.Col.Text("id").PrimaryKey(),
            Name = ReptarApi.Col.Text("name").NotNull(),
            Count = ReptarApi.Col.Int("count").Default(0)
        });

        await client.EnsureCreatedAsync(testTable);

        // Insert
        await client.InsertAsync(testTable, new Widget("live_1", "LiveWidget", 42));

        // Query with source generated mapper
        var items = await client.QueryAsync<Widget>("SELECT * FROM `mysql_integration_widgets` WHERE `id` = 'live_1'");
        Assert.Single(items);
        Assert.Equal("LiveWidget", items[0].Name);
        Assert.Equal(42, items[0].Count);

        // Update
        await client.UpdateAsync(testTable, new Dictionary<string, object?> { ["count"] = 100 }, "live_1");
        var updated = await client.GetAsync<Widget>(testTable, "live_1");
        Assert.NotNull(updated);
        Assert.Equal(100, updated.Count);

        // Delete
        await client.DeleteAsync(testTable, "live_1");
        var afterDelete = await client.GetAsync<Widget>(testTable, "live_1");
        Assert.Null(afterDelete);

        // Cleanup
        await client.ExecuteAsync("DROP TABLE IF EXISTS `mysql_integration_widgets`");
    }

    private sealed record ScalarVal(int Val);
}

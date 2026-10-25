using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Reptar.Tests;

public static partial class GeneratedUsers
{
    public static readonly TableDef Table = ReptarApi.DefineTable("gen_users",
        ReptarApi.Col.Text("id").PrimaryKey(),
        ReptarApi.Col.Text("email").NotNull().Unique(),
        ReptarApi.Col.Int("active").NotNull().Default(1),
        ReptarApi.Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"));
}

public sealed class ColumnRefTests
{
    private static readonly TableDef TestUsers = ReptarApi.DefineTable("test_users",
        ReptarApi.Col.Text("id").PrimaryKey(),
        ReptarApi.Col.Text("email").NotNull().Unique(),
        ReptarApi.Col.Int("age"),
        ReptarApi.Col.Text("status").NotNull());

    private static readonly ColumnRef<string> UserId = TestUsers.Col<string>("id");
    private static readonly ColumnRef<string> UserEmail = TestUsers.Col<string>("email");
    private static readonly ColumnRef<int> UserAge = TestUsers.Col<int>("age");
    private static readonly ColumnRef<string> UserStatus = TestUsers.Col<string>("status");

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
    private sealed record TestUserDto(string Id, string Email, int Age, string Status);

    [Fact]
    public void ColumnRef_Generates_Expected_Condition_And_Parameters()
    {
        var eqFilter = UserEmail.Eq("alice@example.com");
        var (eqClause, eqParams) = eqFilter.ToWhereClause();
        Assert.Equal("WHERE \"test_users\".\"email\" = @p0", eqClause);
        Assert.Equal("alice@example.com", eqParams["p0"]);

        var compoundFilter = (UserAge.Gte(21) & UserStatus.Eq("active")) | UserEmail.Like("%@admin.org");
        var (compoundClause, compoundParams) = compoundFilter.ToWhereClause();
        Assert.Equal(
            "WHERE ((\"test_users\".\"age\" >= @p0 AND \"test_users\".\"status\" = @p1) OR \"test_users\".\"email\" LIKE @p2)",
            compoundClause);
        Assert.Equal(21, compoundParams["p0"]);
        Assert.Equal("active", compoundParams["p1"]);
        Assert.Equal("%@admin.org", compoundParams["p2"]);
    }

    [Fact]
    public void ColumnRef_In_And_NotIn_Handle_Empty_And_Multiple_Values()
    {
        var emptyIn = UserStatus.In(Array.Empty<string>());
        var (emptyClause, emptyParams) = emptyIn.ToWhereClause();
        Assert.Equal("WHERE 1 = 0", emptyClause);
        Assert.Empty(emptyParams);

        var emptyNotIn = UserStatus.NotIn(Array.Empty<string>());
        var (notInClause, notInParams) = emptyNotIn.ToWhereClause();
        Assert.Equal("WHERE 1 = 1", notInClause);
        Assert.Empty(notInParams);

        var multiIn = UserStatus.In("pending", "approved", "active");
        var (multiClause, multiParams) = multiIn.ToWhereClause();
        Assert.Equal("WHERE \"test_users\".\"status\" IN (@p0, @p1, @p2)", multiClause);
        Assert.Equal("pending", multiParams["p0"]);
        Assert.Equal("approved", multiParams["p1"]);
        Assert.Equal("active", multiParams["p2"]);
    }

    [Fact]
    public async Task ColumnRef_And_TableQuery_Execute_Against_Sqlite()
    {
        using var db = new SqliteDbClient(":memory:");
        await db.EnsureCreatedAsync(TestUsers);

        await db.InsertAsync(TestUsers, new { Id = "u1", Email = "alice@example.com", Age = 30, Status = "active" });
        await db.InsertAsync(TestUsers, new { Id = "u2", Email = "bob@example.com", Age = 19, Status = "pending" });
        await db.InsertAsync(TestUsers, new { Id = "u3", Email = "carol@example.com", Age = 25, Status = "active" });

        // 1. ListAsync with SqlFilter
        var activeUsers = await db.ListAsync<TestUserDto>(
            TestUsers,
            UserStatus.Eq("active"),
            orderBy: UserAge.Desc());

        Assert.Equal(2, activeUsers.Count);
        Assert.Equal("alice@example.com", activeUsers[0].Email);
        Assert.Equal("carol@example.com", activeUsers[1].Email);

        // 2. Fluent TableQuery
        var adults = await db.From<TestUserDto>(TestUsers)
            .Where(UserAge.Gte(20))
            .OrderBy(UserEmail.Asc())
            .ToListAsync();

        Assert.Equal(2, adults.Count);
        Assert.Equal("alice@example.com", adults[0].Email);
        Assert.Equal("carol@example.com", adults[1].Email);

        // 3. FirstOrDefaultAsync
        var bob = await db.From<TestUserDto>(TestUsers)
            .Where(UserEmail.Eq("bob@example.com"))
            .FirstOrDefaultAsync();

        Assert.NotNull(bob);
        Assert.Equal("u2", bob.Id);
        Assert.Equal(19, bob.Age);

        // 4. CountAsync and ExistsAsync
        var activeCount = await db.CountAsync(TestUsers, UserStatus.Eq("active"));
        Assert.Equal(2, activeCount);

        var hasBob = await db.From<TestUserDto>(TestUsers)
            .Where(UserEmail.Eq("bob@example.com"))
            .ExistsAsync();
        Assert.True(hasBob);

        var hasDave = await db.From<TestUserDto>(TestUsers)
            .Where(UserEmail.Eq("dave@example.com"))
            .ExistsAsync();
        Assert.False(hasDave);

        // 5. DeleteWhereAsync via TableQuery
        var deleted = await db.From<TestUserDto>(TestUsers)
            .Where(UserStatus.Eq("pending"))
            .DeleteAsync();

        Assert.Equal(1, deleted);
        var remainingCount = await db.CountAsync(TestUsers);
        Assert.Equal(2, remainingCount);
    }

    [Fact]
    public void Roslyn_Generates_ColumnRef_Tokens_And_Exports_SchemaJson()
    {
        // 1. Generated static ColumnRef<T> tokens on partial table class (GeneratedUsers.Email, GeneratedUsers.CreatedAt)
        var filter = GeneratedUsers.Email.Eq("dev@reptar.dev") & GeneratedUsers.Active.Eq(1);
        var (whereSql, parameters) = filter.ToWhereClause();
        Assert.Equal("WHERE (\"gen_users\".\"email\" = @p0 AND \"gen_users\".\"active\" = @p1)", whereSql);
        Assert.Equal("dev@reptar.dev", parameters["p0"]);
        Assert.Equal(1, parameters["p1"]);
        Assert.Equal("\"gen_users\".\"created_at\" DESC", GeneratedUsers.CreatedAt.Desc().Sql);

        // 2. Generated static ColumnRef<T> tokens on [ReptarEntity] type (OrderEntity.Cols.CustomerId)
        var entityFilter = OrderEntity.Cols.CustomerId.Eq(42L);
        var (entityWhere, entityParams) = entityFilter.ToWhereClause();
        Assert.Equal("WHERE \"order_entity\".\"customer_id\" = @p0", entityWhere);
        Assert.Equal(42L, entityParams["p0"]);

        // 3. SchemaRegistry.All & app.ExportSchemaJson()
        var app = ReptarApp.Create();
        var schemaJson = app.ExportSchemaJson();
        Assert.Contains("\"name\": \"gen_users\"", schemaJson);
        Assert.Contains("\"name\": \"test_users\"", schemaJson);
        Assert.Contains("\"clrName\": \"Email\"", schemaJson);
    }
}

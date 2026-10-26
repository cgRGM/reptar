using Reptar;

namespace Reptar.Tests;

public sealed class AuthAotMappingTests
{
    [Fact]
    public void Built_in_auth_rows_expose_explicit_database_parameters()
    {
        IReptarEntity[] rows = [
            new Auth.UserRow("u1", "operator@example.test", "Operator", "hash", 0, "user", "now"),
            new Auth.SessionRow("s1", "u1", "token", "later", "now"),
            new Auth.OAuthAccountRow("o1", "u1", "provider", "remote-user", "now"),
            new Auth.ApiKeyRow("k1", "u1", "CI", "hash", "prefix", null, "now")];
        var expected = new[] { 9, 5, 5, 7 };
        for (var index = 0; index < rows.Length; index++)
        {
            var parameters = rows[index].ToDbParameters();
            Assert.Equal(expected[index], parameters.Length);
            Assert.Equal(parameters.Length, parameters.Select(p => p.Name).Distinct().Count());
            Assert.Contains(parameters, parameter => parameter.Name == "id");
        }
    }

    [Fact]
    public async Task Auth_rows_roundtrip_using_explicit_mappers_and_parameters()
    {
        using var db = new SqliteDbClient(":memory:");
        await db.EnsureCreatedAsync(Auth.Users, Auth.Sessions, Auth.OAuthAccounts, Auth.ApiKeys);
        var user = new Auth.UserRow("u1", "operator@example.test", "Operator", "hash", 0, "user", "now", "avatar", 1);
        await db.InsertAsync(Auth.Users, user);
        var session = new Auth.SessionRow("s1", "u1", "token", "later", "now");
        await db.InsertAsync(Auth.Sessions, session);
        Assert.NotNull(EntityMapper<Auth.UserRow>.Factory);
        Assert.NotNull(EntityMapper<SessionUser>.Factory);
        var found = await db.QuerySingleOrDefaultAsync<Auth.UserRow>(
            "SELECT * FROM users WHERE id=@id", ("id", "u1"));
        Assert.Equal(user, found);
        var current = await db.QuerySingleOrDefaultAsync<SessionUser>(
            "SELECT u.id, u.email, u.name, u.role, u.image, u.email_verified FROM users u JOIN sessions s ON s.user_id=u.id WHERE s.token_hash=@hash",
            ("hash", "token"));
        Assert.Equal(new SessionUser("u1", user.Email, user.Name, "user", "avatar", 1), current);
    }
}

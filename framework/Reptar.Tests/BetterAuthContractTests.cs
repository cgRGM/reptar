using System.Text.Json;
using Reptar;

namespace Reptar.Tests;

public sealed class BetterAuthContractTests
{
    [Theory]
    [InlineData("/api/auth/sign-up/email", "201", "201,400,409")]
    [InlineData("/api/auth/sign-in/email", "200", "200,400,401,429")]
    public void Mounted_email_auth_declares_actual_success_and_error_contracts(
        string path, string successStatus, string expectedStatuses)
    {
        var app = ReptarApp.Create();
        app.Route("/api/auth", BetterAuthProtocol.Routes());
        using var document = JsonDocument.Parse(app.GetOpenApiJson());
        var responses = document.RootElement.GetProperty("paths")
            .GetProperty(path).GetProperty("post").GetProperty("responses");
        Assert.Equal(expectedStatuses.Split(','),
            responses.EnumerateObject().Select(response => response.Name).OrderBy(status => status));
        var success = responses.GetProperty(successStatus).GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        Assert.Equal("object", success.GetProperty("type").GetString());
        Assert.True(success.GetProperty("properties").TryGetProperty("user", out _));
        Assert.Equal("string", success.GetProperty("properties").GetProperty("token").GetProperty("type").GetString());
        foreach (var response in responses.EnumerateObject())
        {
            Assert.True(response.Value.TryGetProperty("content", out _));
        }
    }
}

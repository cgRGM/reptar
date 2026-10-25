using System.Text;
using System.Text.Json;
using Reptar;

namespace Reptar.Tests;

public sealed record CreateArticleDto(string Title, string Body, string[] Tags);
public sealed record ArticleDto(string Slug, string Title, string Body, DateTime CreatedAt);

public class OpenApiTests
{
    [Fact]
    public void OpenApi_generates_valid_v3_1_specification_with_parameters_and_types()
    {
        var app = ReptarApp.Create();

        app.Get("/api/articles", ctx => Task.FromResult(ctx.Json("[]")))
            .WithSummary("List all articles")
            .WithDescription("Returns a paginated list of articles")
            .WithTags("articles", "blog")
            .WithName("listArticles")
            .Produces<List<ArticleDto>>(200);

        app.Get("/api/articles/:slug", ctx => Task.FromResult(ctx.Json("{}")))
            .WithSummary("Get article by slug")
            .WithTag("articles")
            .Produces<ArticleDto>(200)
            .Produces(404);

        app.Post<CreateArticleDto>("/api/articles", (ctx, input) => Task.FromResult(ctx.Json(input, 201)))
            .WithSummary("Create article")
            .WithTag("articles")
            .Produces<ArticleDto>(201)
            .Produces(422);

        var json = app.GetOpenApiJson(new OpenApiOptions
        {
            Title = "RealWorld API",
            Version = "2.0.0"
        });

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("3.1.0", root.GetProperty("openapi").GetString());
        Assert.Equal("RealWorld API", root.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("2.0.0", root.GetProperty("info").GetProperty("version").GetString());

        var paths = root.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/articles", out var articlesPath));
        Assert.True(paths.TryGetProperty("/api/articles/{slug}", out var articleSlugPath));

        // Verify GET /api/articles
        var getArticles = articlesPath.GetProperty("get");
        Assert.Equal("List all articles", getArticles.GetProperty("summary").GetString());
        Assert.Equal("listArticles", getArticles.GetProperty("operationId").GetString());
        Assert.Equal("articles", getArticles.GetProperty("tags")[0].GetString());
        Assert.True(getArticles.GetProperty("responses").TryGetProperty("200", out _));

        // Verify GET /api/articles/{slug} path parameter
        var getSlug = articleSlugPath.GetProperty("get");
        var parameters = getSlug.GetProperty("parameters");
        Assert.Equal("slug", parameters[0].GetProperty("name").GetString());
        Assert.Equal("path", parameters[0].GetProperty("in").GetString());
        Assert.True(parameters[0].GetProperty("required").GetBoolean());
    }

    [Fact]
    public void OpenApi_generates_schema_from_typed_validators()
    {
        var app = ReptarApp.Create();

        var validator = ReptarApi.Rule<CreateArticleDto>()
            .Prop(x => x.Title, p => p.Min(5).Max(100))
            .Prop(x => x.Body, p => p.Min(10));

        app.Post("/api/articles", validator, (ctx, input) => Task.FromResult(ctx.Json(input, 201)));

        var json = app.GetOpenApiJson();
        using var doc = JsonDocument.Parse(json);
        var schema = doc.RootElement
            .GetProperty("paths")
            .GetProperty("/api/articles")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");

        Assert.Equal("object", schema.GetProperty("type").GetString());
        var props = schema.GetProperty("properties");
        Assert.True(props.TryGetProperty("title", out var titleProp));
        Assert.Equal("string", titleProp.GetProperty("type").GetString());
        Assert.Equal(5, titleProp.GetProperty("minLength").GetInt32());
        Assert.Equal(100, titleProp.GetProperty("maxLength").GetInt32());

        Assert.True(props.TryGetProperty("body", out var bodyProp));
        Assert.Equal("string", bodyProp.GetProperty("type").GetString());
        Assert.Equal(10, bodyProp.GetProperty("minLength").GetInt32());

        var required = schema.GetProperty("required");
        Assert.Equal(2, required.GetArrayLength());
    }

    [Fact]
    public void SourceGenJsonCodec_resolves_app_and_internal_error_types()
    {
        var codec = new SourceGenJsonCodec();
        var errBytes = codec.SerializeUtf8(new ErrorBody("not_found", "Missing", "req_1"));
        var roundTrip = codec.Deserialize<ErrorBody>(errBytes.AsSpan());
        Assert.NotNull(roundTrip);
        Assert.Equal("not_found", roundTrip.Code);
        Assert.Equal("Missing", roundTrip.Message);
    }

    [Fact]
    public async Task UseScalar_and_UseSwaggerUi_serve_interactive_ui_pages()
    {
        var app = ReptarApp.Create();
        app.UseScalar();
        app.UseSwaggerUi();

        app.Get("/api/ping", ctx => Task.FromResult(ctx.Text("pong")))
            .WithSummary("Ping");

        // 1. Check OpenAPI JSON
        var jsonRes = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/openapi.json"
        });
        Assert.Equal(200, jsonRes.Status);
        Assert.Contains("application/json", jsonRes.ContentType);
        var jsonText = Encoding.UTF8.GetString(jsonRes.Body!);
        Assert.Contains("\"openapi\": \"3.1.0\"", jsonText);

        // 2. Check Scalar Reference UI
        var scalarRes = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/reference"
        });
        Assert.Equal(200, scalarRes.Status);
        Assert.Contains("text/html", scalarRes.ContentType);
        var scalarHtml = Encoding.UTF8.GetString(scalarRes.Body!);
        Assert.Contains("@scalar/api-reference", scalarHtml);
        Assert.Contains("data-url=\"/openapi.json\"", scalarHtml);

        // 3. Check Swagger UI
        var swaggerRes = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/swagger"
        });
        Assert.Equal(200, swaggerRes.Status);
        Assert.Contains("text/html", swaggerRes.ContentType);
        var swaggerHtml = Encoding.UTF8.GetString(swaggerRes.Body!);
        Assert.Contains("swagger-ui", swaggerHtml);
    }
}

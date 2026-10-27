using System.Text.Json;
using Reptar;

namespace Reptar.Tests;

public sealed record ProbeQuery(string Subject, int? Limit = null);
public sealed record ProbeResult(string Id, ProbeDetail Detail, string[] Tags, string? Note);
public sealed record ProbeDetail(bool Enabled);
public sealed record UnsupportedQuery(string[] Subjects);

public sealed class QueryCodegenTests
{
    [Fact]
    public void Query_metadata_survives_mounting_and_emits_client_and_both_OpenApi_surfaces()
    {
        var app = ReptarApp.Create();
        var routes = ReptarApp.Create();
        routes.Get("/:key", ctx => ctx.Text("ok")).WithQuery<ProbeQuery>().Produces<List<ProbeResult>>().Produces(304);
        app.Route("/flags", routes);
        Assert.Equal(typeof(ProbeQuery), Assert.Single(app.RouteMetadata).QueryType);
        var output = Path.Combine(Path.GetTempPath(), "reptar-query-" + Guid.NewGuid());
        try
        {
            RpcGenerator.Generate(app, output);
            var client = File.ReadAllText(Path.Combine(output, "app-type.d.ts"));
            Assert.Contains("query: ProbeQuery", client);
            Assert.Contains("param: { key: string }", client);
            Assert.Contains("200 | 304", client);
            Assert.Contains("200: ProbeResult[]; 304: never", client);
            foreach (var json in new[] { File.ReadAllText(Path.Combine(output, "openapi.json")), app.GetOpenApiJson() })
            {
                using var document = JsonDocument.Parse(json);
                var operation = document.RootElement.GetProperty("paths").GetProperty("/flags/{key}").GetProperty("get");
                var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
                var subject = Assert.Single(parameters, p => p.GetProperty("name").GetString() == "subject");
                Assert.Equal("query", subject.GetProperty("in").GetString());
                Assert.True(subject.GetProperty("required").GetBoolean());
                var limit = Assert.Single(parameters, p => p.GetProperty("name").GetString() == "limit");
                Assert.False(limit.GetProperty("required").GetBoolean());
                Assert.Equal("integer", limit.GetProperty("schema").GetProperty("type").GetString());
                Assert.False(operation.TryGetProperty("requestBody", out _));
            }
        }
        finally { Directory.Delete(output, true); }
    }

    [Fact]
    public void Generated_OpenApi_has_array_items_nested_refs_required_fields_and_bodyless_304()
    {
        var app = ReptarApp.Create();
        app.Get("/flags", ctx => ctx.Text("ok")).Produces<List<ProbeResult>>().Produces(304);
        var output = Path.Combine(Path.GetTempPath(), "reptar-schema-" + Guid.NewGuid());
        try
        {
            RpcGenerator.Generate(app, output);
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "openapi.json")));
            var responses = document.RootElement.GetProperty("paths").GetProperty("/flags").GetProperty("get").GetProperty("responses");
            var schema = responses.GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
            Assert.Equal("array", schema.GetProperty("type").GetString());
            Assert.Equal("#/components/schemas/ProbeResult", schema.GetProperty("items").GetProperty("$ref").GetString());
            Assert.False(responses.GetProperty("304").TryGetProperty("content", out _));
            var result = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("ProbeResult");
            var properties = result.GetProperty("properties");
            Assert.Equal("#/components/schemas/ProbeDetail", properties.GetProperty("detail").GetProperty("$ref").GetString());
            Assert.Equal("string", properties.GetProperty("tags").GetProperty("items").GetProperty("type").GetString());
            Assert.Contains(result.GetProperty("required").EnumerateArray(), value => value.GetString() == "id");
            Assert.DoesNotContain(result.GetProperty("required").EnumerateArray(), value => value.GetString() == "note");
            Assert.Equal("null", properties.GetProperty("note").GetProperty("anyOf")[1].GetProperty("type").GetString());
        }
        finally { Directory.Delete(output, true); }
    }

    [Fact]
    public void Unsupported_query_collections_fail_codegen_with_an_actionable_error()
    {
        var app = ReptarApp.Create();
        app.Get("/flags", ctx => ctx.Text("ok")).WithQuery<UnsupportedQuery>();
        var output = Path.Combine(Path.GetTempPath(), "reptar-query-invalid-" + Guid.NewGuid());
        try
        {
            var error = Assert.Throws<NotSupportedException>(() => RpcGenerator.Generate(app, output));
            Assert.Contains("UnsupportedQuery.Subjects", error.Message);
            Assert.Contains("scalar", error.Message);
        }
        finally { Directory.Delete(output, true); }
    }
}

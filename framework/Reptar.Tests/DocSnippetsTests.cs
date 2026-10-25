using Xunit;

namespace Reptar.Tests;

public sealed class DocSnippetsTests
{
    private sealed record DocTaskItem(string Id, string Title, string Status);

    [Fact]
    public async Task Documented_Apis_QueryValue_Group_AiRunAsync_BatchAsync_And_DbD1_CompileAndWork()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"reptar_docs_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new SqliteDbClient(dbPath);
            var app = ReptarApp.Create(new ReptarConfig
            {
                Database = db,
                AI = new StubAiClient(),
            });

            await app.Db.ExecuteAsync(
                "CREATE TABLE tasks (id TEXT PRIMARY KEY, title TEXT NOT NULL, status TEXT NOT NULL)");

            // 1. BatchAsync with tuple statements (from apps/docs/src/content/docs/data/database.md)
            var affected = await app.Db.BatchAsync(
                ("INSERT INTO tasks (id, title, status) VALUES (@id, @title, @status)",
                    new { id = "t_1", title = "First task", status = "open" }),
                ("INSERT INTO tasks (id, title, status) VALUES (@id, @title, @status)",
                    new { id = "t_2", title = "Second task", status = "done" })
            );
            Assert.Equal(2, affected);

            // 2. TransactionAsync (from apps/docs/src/content/docs/data/database.md)
            await app.Db.TransactionAsync(async tx =>
            {
                await tx.ExecuteAsync(
                    "UPDATE tasks SET status = @status WHERE id = @id",
                    ("status", "in_progress"),
                    ("id", "t_1"));
            });

            // 3. app.Group("/api/v1") + ctx.Req.QueryValue("comments", defaultValue: "false") + ctx.AI.RunAsync(...)
            var api = app.Group("/api/v1");
            api.Get("/tasks/:id", async ctx =>
            {
                var id = ctx.Req.Param("id");
                var includeComments = ctx.Req.QueryValue("comments", defaultValue: "false");
                var aiSummary = await ctx.AI.RunAsync("@cf/meta/llama-3-8b-instruct", prompt: $"Summarize {id}");
                return ctx.Json(new
                {
                    id,
                    includeComments,
                    summary = aiSummary.Text,
                });
            });

            var res = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/api/v1/tasks/t_1",
            });
            Assert.Equal(200, res.Status);
            var body = System.Text.Encoding.UTF8.GetString(res.Body!);
            Assert.Contains("\"includeComments\":\"false\"", body);
            Assert.Contains("summary:Summarize t_1", body);

            // 4. Db.D1() exists on Db static class (throws PlatformNotSupportedException outside Wasm)
            Assert.Throws<PlatformNotSupportedException>(() => Db.D1());
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    if (File.Exists(dbPath + suffix))
                        File.Delete(dbPath + suffix);
                }
                catch { }
            }
        }
    }

    private sealed class StubAiClient : IAiClient
    {
        public Task<AiResult> GenerateAsync(AiRequest request) =>
            Task.FromResult(new AiResult(request.Model, $"summary:{request.Prompt}", 4, 8));
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Reptar;
using Reptar.AI;

public static class Chat
{
    // POST /api/chat — SSE endpoint that runs the streaming agent loop and
    // emits typed JSON events: text deltas, tool calls, and a final done frame.
    public static void Configure(ReptarApp app, AgentSessionStore store, string workspaceRoot)
    {
        app.Post("/api/chat", ctx =>
        {
            var root = ParseBody(ctx);
            var message = root?["message"]?.GetValue<string>() ?? "";
            if (message.Trim().Length == 0)
                return ctx.BadRequest("Body field 'message' is required.");

            var sessionId = root?["sessionId"]?.GetValue<string>();
            var (session, history) = LoadOrCreateSession(store, sessionId, message);

            var provider = root?["provider"]?.GetValue<string>();
            var model = root?["model"]?.GetValue<string>();
            var client = LlmClientFactory.Create(LlmConfig.FromEnvironment(provider, model));

            var events = RunStreaming(app, client, store, session, history, message, workspaceRoot);
            return ctx.StreamLlmAsync(events);
        }).WithSummary("Stream an agent reply over SSE");

        app.Get("/api/sessions", async ctx =>
        {
            var sessions = await store.ListSessionsAsync(50);
            var array = new JsonArray();
            var list = (IList<JsonNode?>)array;
            foreach (var s in sessions)
            {
                list.Add(new JsonObject
                {
                    ["id"] = s.Id,
                    ["sessionId"] = s.Id,
                    ["title"] = s.Title,
                    ["createdAt"] = s.CreatedAt
                });
            }
            return ctx.JsonRaw(array.ToJsonString());
        }).WithSummary("List saved agent sessions");

        app.Get("/api/sessions/:id/messages", async ctx =>
        {
            var id = ctx.Req.Param("id") ?? "";
            var found = await store.FindSessionAsync(id);
            if (found is null)
                return ctx.NotFound($"Session '{id}' not found.");

            var messages = await store.LoadMessagesAsync(id);
            var array = new JsonArray();
            var list = (IList<JsonNode?>)array;
            foreach (var m in messages)
            {
                var obj = new JsonObject
                {
                    ["role"] = m.Role,
                    ["content"] = m.Content
                };
                if (m.ToolCalls is { Count: > 0 })
                {
                    var calls = new JsonArray();
                    var callsList = (IList<JsonNode?>)calls;
                    foreach (var c in m.ToolCalls)
                        callsList.Add(new JsonObject { ["id"] = c.Id, ["name"] = c.Name });
                    obj["toolCalls"] = calls;
                }
                list.Add(obj);
            }
            return ctx.JsonRaw(array.ToJsonString());
        }).WithSummary("Return a session's saved messages");

        app.Get("/api/tools", ctx =>
        {
            var array = new JsonArray();
            var list = (IList<JsonNode?>)array;
            foreach (var t in app.GetAiTools(onlyExplicit: true))
            {
                list.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["method"] = t.Method
                });
            }
            return ctx.JsonRaw(array.ToJsonString());
        }).WithSummary("List mounted AI tools");
    }

    private static JsonNode? ParseBody(Ctx ctx)
    {
        var body = ctx.Req.BodyText;
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonNode.Parse(body);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    private static (AgentSessionRow Session, List<ChatMessage> History) LoadOrCreateSession(
        AgentSessionStore store, string? sessionId, string firstMessage)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var found = store.FindSessionAsync(sessionId).GetAwaiter().GetResult();
            if (found is not null)
                return (found, store.LoadMessagesAsync(found.Id).GetAwaiter().GetResult());
        }

        var title = firstMessage.Length > 70 ? firstMessage[..70] : firstMessage;
        var created = store.CreateSessionAsync(title).GetAwaiter().GetResult();
        return (created, []);
    }

    private static async IAsyncEnumerable<string> RunStreaming(
        ReptarApp app,
        ILlmClient client,
        AgentSessionStore store,
        AgentSessionRow session,
        List<ChatMessage> history,
        string message,
        string workspaceRoot)
    {
        history.Add(new ChatMessage("user", message));
        await store.AppendMessageAsync(session.Id, history[^1]);

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        var runTask = Task.Run(async () =>
        {
            var step = 0;
            try
            {
                var summary = await StreamingAgentHarness.RunAsync(
                    app,
                    client,
                    history,
                    systemPrompt: SystemPrompt(workspaceRoot),
                    maxSteps: 24,
                    onEvent: evt =>
                    {
                        switch (evt)
                        {
                            case AgentEvent.TurnStarted turn:
                                step = turn.Step;
                                break;
                            case AgentEvent.TextDelta delta:
                                channel.Writer.TryWrite(JsonText("text", delta.Text, step));
                                break;
                            case AgentEvent.ToolCallStarted call:
                                channel.Writer.TryWrite(Json(new JsonObject
                                {
                                    ["type"] = "tool_start",
                                    ["step"] = step,
                                    ["id"] = call.Id,
                                    ["name"] = call.Name,
                                    ["args"] = call.ArgumentsJson
                                }));
                                break;
                            case AgentEvent.ToolCallFinished call:
                                channel.Writer.TryWrite(Json(new JsonObject
                                {
                                    ["type"] = "tool_end",
                                    ["step"] = step,
                                    ["id"] = call.Id,
                                    ["name"] = call.Name,
                                    ["success"] = call.Success,
                                    ["elapsedMs"] = Math.Round(call.ElapsedMs, 1)
                                }));
                                break;
                        }
                    });

                var last = history.LastOrDefault(m => m.Role == "assistant");
                if (last is not null)
                    await store.AppendMessageAsync(session.Id, last);

                channel.Writer.TryWrite(Json(new JsonObject
                {
                    ["type"] = "done",
                    ["sessionId"] = session.Id,
                    ["stopReason"] = summary.StopReason.ToString().ToLowerInvariant(),
                    ["steps"] = summary.StepsTaken,
                    ["toolCalls"] = summary.ToolCallsExecuted,
                    ["elapsedMs"] = Math.Round(summary.ElapsedMs, 1)
                }));
            }
            catch (Exception ex)
            {
                channel.Writer.TryWrite(Json(new JsonObject
                {
                    ["type"] = "error",
                    ["message"] = ex.Message
                }));
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        });

        while (await channel.Reader.WaitToReadAsync())
        {
            while (channel.Reader.TryRead(out var evt))
                yield return evt;
        }

        await runTask;
    }

    private static string JsonText(string type, string text, int step) =>
        Json(new JsonObject
        {
            ["type"] = type,
            ["text"] = text,
            ["step"] = step
        });

    private static string Json(JsonObject obj) => obj.ToJsonString();

    private static string SystemPrompt(string workspaceRoot) => $$"""
        You are the Reptar web agent, an interactive coding assistant embedded in the Reptar web framework.

        Working directory: {{workspaceRoot}}

        Rules:
        - Use the workspace tools (read_file, write_file, edit_file, list_dir, run_command) to inspect and change files.
        - Read a file before editing it. Prefer edit_file with a unique target string over rewriting whole files.
        - Answer concisely in Markdown.
        """;
}

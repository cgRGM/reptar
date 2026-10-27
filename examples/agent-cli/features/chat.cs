using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
#if !NETWASM
using System.Threading.Channels;
#endif
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

            AgentRunRegistry.Begin(session.Id, out var runToken);
            var events = RunStreaming(
                app, client, store, session, history, message, workspaceRoot, runToken);
            return ctx.StreamLlmAsync(events);
        }).WithSummary("Stream an agent reply over SSE");

        // Steer a run that is already streaming. This is what makes the web client feel like the
        // terminal: you can redirect the agent without waiting for it to stop.
        app.Post("/api/chat/steer", ctx =>
        {
            var root = ParseBody(ctx);
            var sessionId = root?["sessionId"]?.GetValue<string>() ?? "";
            var message = root?["message"]?.GetValue<string>() ?? "";

            if (message.Trim().Length == 0)
                return ctx.BadRequest("Body field 'message' is required.");

            if (!AgentRunRegistry.Steer(sessionId, message))
                return ctx.Error(409, "no_active_run", "No run is streaming for that session.");

            return ctx.JsonRaw("{\"ok\":true}");
        }).WithSummary("Queue a steering message for a streaming run");

        app.Post("/api/chat/abort", ctx =>
        {
            var root = ParseBody(ctx);
            var sessionId = root?["sessionId"]?.GetValue<string>() ?? "";

            if (!AgentRunRegistry.Abort(sessionId))
                return ctx.Error(409, "no_active_run", "No run is streaming for that session.");

            return ctx.JsonRaw("{\"ok\":true}");
        }).WithSummary("Abort a streaming run");

        app.Get("/api/chat/status/:sessionId", ctx =>
        {
            var sessionId = ctx.Param("sessionId");
            var (running, elapsed) = AgentRunRegistry.Status(sessionId);

            return ctx.JsonRaw(new JsonObject
            {
                ["running"] = running,
                ["elapsedMs"] = Math.Round(elapsed, 1)
            }.ToJsonString());
        }).WithSummary("Whether a session is currently streaming");

        // Compact on demand: the same checkpoint the terminal's /compact produces.
        app.Post("/api/chat/compact", async ctx =>
        {
            var root = ParseBody(ctx);
            var sessionId = root?["sessionId"]?.GetValue<string>() ?? "";
            var instructions = root?["customInstructions"]?.GetValue<string>();

            var session = sessionId.Length > 0
                ? await store.FindSessionAsync(sessionId)
                : await store.LatestSessionAsync();

            if (session is null)
                return ctx.Error(404, "not_found", "No session to compact.");

            var history = await store.LoadMessagesAsync(session.Id);
            var provider = root?["provider"]?.GetValue<string>();
            var model = root?["model"]?.GetValue<string>();
            var client = LlmClientFactory.Create(LlmConfig.FromEnvironment(provider, model));

            var outcome = await ContextCompactor.CompactAsync(
                history, client, CompactionSettings.Default, CompactionReason.Manual,
                customInstructions: instructions);

            if (outcome is null)
                return ctx.Error(409, "nothing_to_compact", "Nothing worth compacting yet.");

            var (result, projection) = outcome.Value;

            return ctx.JsonRaw(new JsonObject
            {
                ["summary"] = result.Summary,
                ["tokensBefore"] = result.TokensBefore,
                ["estimatedTokensAfter"] = result.EstimatedTokensAfter,
                ["projectedMessages"] = projection.Count
            }.ToJsonString());
        }).WithSummary("Compact the conversation into a checkpoint");


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

        app.Post("/api/sessions", async ctx =>
        {
            var root = ParseBody(ctx);
            var title = root?["title"]?.GetValue<string>() ?? "New chat";
            var session = await store.CreateSessionAsync(title);
            return ctx.JsonRaw(new JsonObject
            {
                ["id"] = session.Id,
                ["sessionId"] = session.Id,
                ["title"] = session.Title,
                ["createdAt"] = session.CreatedAt
            }.ToJsonString());
        }).WithSummary("Create a new agent session");

        app.Delete("/api/sessions/:id", async ctx =>
        {
            var id = ctx.Req.Param("id") ?? "";
            var deleted = await store.DeleteSessionAsync(id);
            return ctx.JsonRaw(new JsonObject { ["ok"] = deleted }.ToJsonString());
        }).WithSummary("Delete an agent session");

        app.Patch("/api/sessions/:id", async ctx =>
        {
            var id = ctx.Req.Param("id") ?? "";
            var root = ParseBody(ctx);
            var title = root?["title"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(title))
                return ctx.BadRequest("Title is required.");
            await store.RenameSessionAsync(id, title);
            return ctx.JsonRaw(new JsonObject { ["ok"] = true, ["title"] = title }.ToJsonString());
        }).WithSummary("Rename an agent session");

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
                if (!string.IsNullOrEmpty(m.Name))
                    obj["name"] = m.Name;
                if (!string.IsNullOrEmpty(m.ToolCallId))
                    obj["toolCallId"] = m.ToolCallId;
                if (m.ToolCalls is { Count: > 0 })
                {
                    var calls = new JsonArray();
                    var callsList = (IList<JsonNode?>)calls;
                    foreach (var c in m.ToolCalls)
                    {
                        callsList.Add(new JsonObject
                        {
                            ["id"] = c.Id,
                            ["name"] = c.Name,
                            ["arguments"] = c.Arguments
                        });
                    }
                    obj["toolCalls"] = calls;
                }
                list.Add(obj);
            }
            return ctx.JsonRaw(array.ToJsonString());
        }).WithSummary("Return a session's saved messages");

        app.Get("/api/status", ctx =>
        {
            var auth = LlmAuthFile.LoadActive();
            var activeConfig = LlmConfig.FromEnvironment();
            var gitBranch = ReadGitBranch(workspaceRoot);
            var tools = app.GetAiTools(onlyExplicit: true);

            var providersArray = new JsonArray();
            var pList = (IList<JsonNode?>)providersArray;
            foreach (var p in LlmProviders.Known)
            {
                var (source, detail) = LlmProviders.CredentialSource(p.Key, auth);
                pList.Add(new JsonObject
                {
                    ["key"] = p.Key,
                    ["name"] = p.DisplayName,
                    ["source"] = source,
                    ["detail"] = detail,
                    ["status"] = LlmProviders.FormatStatus(p.Key, auth),
                    ["defaultModel"] = p.DefaultModel,
                    ["defaultBaseUrl"] = p.DefaultBaseUrl,
                    ["requiresApiKey"] = p.RequiresApiKey,
                    ["requiresBaseUrl"] = p.RequiresBaseUrl,
                    ["keyUrl"] = p.KeyUrl,
                    ["note"] = p.Note
                });
            }

            var res = new JsonObject
            {
                ["ok"] = true,
                ["workspaceRoot"] = workspaceRoot,
                ["workspaceName"] = Path.GetFileName(workspaceRoot),
                ["gitBranch"] = gitBranch,
                ["provider"] = activeConfig.ProviderKey,
                ["model"] = activeConfig.Model,
                ["displayName"] = activeConfig.DisplayName,
                ["isOffline"] = activeConfig.Provider == LlmProvider.Echo,
                ["authPath"] = LlmAuthFile.ActivePath,
                ["providers"] = providersArray,
                ["toolsCount"] = tools.Count
            };
            return ctx.JsonRaw(res.ToJsonString());
        }).WithSummary("Get agent workspace status and providers");

        app.Get("/api/workspace/files", ctx =>
        {
            var files = ScanWorkspaceFiles(workspaceRoot, limit: 300);
            var array = new JsonArray();
            var list = (IList<JsonNode?>)array;
            foreach (var f in files)
            {
                list.Add(new JsonObject
                {
                    ["path"] = f,
                    ["name"] = Path.GetFileName(f)
                });
            }
            return ctx.JsonRaw(array.ToJsonString());
        }).WithSummary("List workspace files for context autocomplete");

        app.Get("/api/models", async ctx =>
        {
            var provider = ctx.Req.QueryValue("provider");
            if (string.IsNullOrWhiteSpace(provider))
            {
                var cfg = LlmConfig.FromEnvironment();
                provider = cfg.ProviderKey;
            }

            var config = LlmConfig.FromEnvironment(provider);
            var live = await LlmCatalog.ListAsync(config);
            var curated = await LlmCatalog.ListCuratedAsync(provider);

            var array = new JsonArray();
            var list = (IList<JsonNode?>)array;
            if (live.Ok && live.Models.Count > 0)
            {
                foreach (var m in live.Models)
                {
                    var cur = curated.FirstOrDefault(c => string.Equals(c.Id, m, StringComparison.OrdinalIgnoreCase));
                    list.Add(new JsonObject
                    {
                        ["id"] = m,
                        ["name"] = cur?.Name ?? m,
                        ["context"] = cur?.ContextTokens ?? 0,
                        ["inputCost"] = cur?.InputCostPerMillion ?? 0,
                        ["outputCost"] = cur?.OutputCostPerMillion ?? 0,
                        ["source"] = "live"
                    });
                }
            }
            else if (curated.Count > 0)
            {
                foreach (var c in curated)
                {
                    list.Add(new JsonObject
                    {
                        ["id"] = c.Id,
                        ["name"] = c.Name,
                        ["context"] = c.ContextTokens,
                        ["inputCost"] = c.InputCostPerMillion,
                        ["outputCost"] = c.OutputCostPerMillion,
                        ["source"] = "curated"
                    });
                }
            }

            var res = new JsonObject
            {
                ["provider"] = provider,
                ["liveOk"] = live.Ok,
                ["liveError"] = live.Error,
                ["models"] = array
            };
            return ctx.JsonRaw(res.ToJsonString());
        }).WithSummary("List models for a provider");

        app.Post("/api/providers/connect", async ctx =>
        {
            var root = ParseBody(ctx);
            var provider = root?["provider"]?.GetValue<string>() ?? "";
            var apiKey = root?["apiKey"]?.GetValue<string>();
            var baseUrl = root?["baseUrl"]?.GetValue<string>();
            var model = root?["model"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(provider))
                return ctx.BadRequest("Provider is required.");

            var auth = LlmAuthFile.LoadActive();
            var cred = new ProviderCredential(
                string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(),
                string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim(),
                string.IsNullOrWhiteSpace(model) ? null : model.Trim());

            var testConfig = LlmConfig.FromEnvironment(provider, cred.DefaultModel, cred.BaseUrl);
            if (!string.IsNullOrWhiteSpace(cred.ApiKey))
                testConfig = testConfig with { ApiKey = cred.ApiKey };

            var testResult = await LlmCatalog.ListAsync(testConfig);
            auth.Set(provider, cred, makeDefault: true);

            var res = new JsonObject
            {
                ["ok"] = true,
                ["tested"] = testResult.Ok,
                ["testError"] = testResult.Error,
                ["modelsCount"] = testResult.Models.Count,
                ["provider"] = provider,
                ["model"] = cred.DefaultModel ?? testConfig.Model
            };
            return ctx.JsonRaw(res.ToJsonString());
        }).WithSummary("Save and test credentials for a provider");

        app.Post("/api/providers/disconnect", ctx =>
        {
            var root = ParseBody(ctx);
            var provider = root?["provider"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(provider))
                return ctx.BadRequest("Provider is required.");

            var auth = LlmAuthFile.LoadActive();
            var cleared = auth.Clear(provider);
            return ctx.JsonRaw(new JsonObject { ["ok"] = cleared, ["provider"] = provider }.ToJsonString());
        }).WithSummary("Clear credentials for a provider");

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

#if !NETWASM
    private static async IAsyncEnumerable<string> RunStreaming(
        ReptarApp app,
        ILlmClient client,
        AgentSessionStore store,
        AgentSessionRow session,
        List<ChatMessage> history,
        string message,
        string workspaceRoot,
        CancellationToken runToken)
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
                    configure: config => config with
                    {
                        GetSteeringMessages = () => AgentRunRegistry.DrainSteering(session.Id)
                    },
                    cancellationToken: runToken,
                    onEvent: evt =>
                    {
                        // Event names follow pi's JSON/RPC stream so a single client
                        // implementation can consume the terminal, SSE, and JSONL modes.
                        switch (evt)
                        {
                            case AgentEvent.AgentStart:
                                channel.Writer.TryWrite(Json(new JsonObject { ["type"] = "agent_start" }));
                                break;
                            case AgentEvent.TurnStart turn:
                                step = turn.Step;
                                channel.Writer.TryWrite(Json(new JsonObject
                                {
                                    ["type"] = "turn_start",
                                    ["step"] = step
                                }));
                                break;
                            case AgentEvent.MessageUpdate { Kind: AgentContentKind.Text } delta:
                                channel.Writer.TryWrite(JsonText("text", delta.Delta, step));
                                break;
                            case AgentEvent.MessageUpdate { Kind: AgentContentKind.Thinking } delta:
                                channel.Writer.TryWrite(JsonText("thinking", delta.Delta, step));
                                break;
                            case AgentEvent.MessageEnd end:
                                channel.Writer.TryWrite(Json(new JsonObject
                                {
                                    ["type"] = "message_end",
                                    ["step"] = step,
                                    ["role"] = end.Message.Role,
                                    ["content"] = end.Message.Content,
                                    ["toolCallId"] = end.Message.ToolCallId
                                }));
                                break;
                            case AgentEvent.ToolExecutionStart call:
                                channel.Writer.TryWrite(Json(new JsonObject
                                {
                                    ["type"] = "tool_execution_start",
                                    ["step"] = step,
                                    ["id"] = call.ToolCallId,
                                    ["name"] = call.ToolName,
                                    ["args"] = call.ArgumentsJson
                                }));
                                break;
                            case AgentEvent.ToolExecutionUpdate update:
                                channel.Writer.TryWrite(Json(new JsonObject
                                {
                                    ["type"] = "tool_execution_update",
                                    ["step"] = step,
                                    ["id"] = update.ToolCallId,
                                    ["name"] = update.ToolName,
                                    ["partialResult"] = update.PartialResult
                                }));
                                break;
                            case AgentEvent.ToolExecutionEnd call:
                                channel.Writer.TryWrite(Json(new JsonObject
                                {
                                    ["type"] = "tool_execution_end",
                                    ["step"] = step,
                                    ["id"] = call.ToolCallId,
                                    ["name"] = call.ToolName,
                                    ["isError"] = call.IsError,
                                    ["result"] = call.Result.Length > 2000 ? call.Result[..2000] + "\n...(truncated)" : call.Result,
                                    ["durationMs"] = Math.Round(call.DurationMs, 1)
                                }));
                                break;
                            case AgentEvent.UsageReported usage:
                                channel.Writer.TryWrite(Json(new JsonObject
                                {
                                    ["type"] = "usage",
                                    ["step"] = usage.Step,
                                    ["inputTokens"] = usage.InputTokens,
                                    ["outputTokens"] = usage.OutputTokens
                                }));
                                break;
                            case AgentEvent.AgentError failed:
                                channel.Writer.TryWrite(Json(new JsonObject
                                {
                                    ["type"] = "error",
                                    ["message"] = failed.Message
                                }));
                                break;
                            case AgentEvent.AgentSettled settled:
                                channel.Writer.TryWrite(Json(new JsonObject
                                {
                                    ["type"] = "agent_settled",
                                    ["aborted"] = settled.Aborted
                                }));
                                break;
                        }
                    });

                if (summary.StopReason == AgentStopReason.Error)
                {
                    channel.Writer.TryWrite(Json(new JsonObject
                    {
                        ["type"] = "error",
                        ["message"] = summary.FinalContent
                    }));
                }

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
                AgentRunRegistry.End(session.Id);
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
#else
    private static async IAsyncEnumerable<string> RunStreaming(
        ReptarApp app,
        ILlmClient client,
        AgentSessionStore store,
        AgentSessionRow session,
        List<ChatMessage> history,
        string message,
        string workspaceRoot)
    {
        yield return Json(new JsonObject
        {
            ["type"] = "error",
            ["message"] = "Agent streaming is not supported under NetWasm"
        });
        await Task.CompletedTask;
    }
#endif

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

    private static string ReadGitBranch(string workspaceRoot)
    {
#if !NETWASM
        try
        {
            var headPath = Path.Combine(workspaceRoot, ".git", "HEAD");
            if (!File.Exists(headPath))
                return "";

            var head = File.ReadAllText(headPath).Trim();
            return head.StartsWith("ref: refs/heads/", StringComparison.Ordinal)
                ? head["ref: refs/heads/".Length..]
                : head.Length > 8
                    ? head[..8]
                    : head;
        }
        catch
        {
            return "";
        }
#else
        return "";
#endif
    }

    private static List<string> ScanWorkspaceFiles(string root, int limit = 300)
    {
        var results = new List<string>();
#if !NETWASM
        try
        {
            var ignoreDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", "bin", "obj", "node_modules", ".reptar", "dist", ".next", ".alchemy", ".cache"
            };

            void Scan(string current)
            {
                if (results.Count >= limit) return;
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(current))
                    {
                        var dirName = Path.GetFileName(dir);
                        if (dirName.StartsWith('.') || ignoreDirs.Contains(dirName))
                            continue;
                        Scan(dir);
                        if (results.Count >= limit) return;
                    }

                    foreach (var file in Directory.EnumerateFiles(current))
                    {
                        var rel = Path.GetRelativePath(root, file);
                        results.Add(rel);
                        if (results.Count >= limit) return;
                    }
                }
                catch
                {
                }
            }

            Scan(root);
        }
        catch
        {
        }
#endif
        return results;
    }
}

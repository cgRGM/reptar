using System.Net;
using System.Text;
using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for the Responses API client, driven against a local server that speaks the
/// real event stream. A ChatGPT subscription token cannot be exercised in CI, so the wire shape
/// and the stream translation are pinned here instead.
/// </summary>
public sealed class OpenAiResponsesTests
{
    /// <summary>Stands in for <c>api.openai.com/v1</c> and records what was sent.</summary>
    private sealed class StubServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public StubServer(string sseBody)
        {
            // HttpListener refuses port 0, so a free port has to be chosen and then bound — a
            // window where another test can take it. Retrying closes that window without the
            // flakiness of picking once and hoping.
            var random = new Random();
            for (var attempt = 1; ; attempt++)
            {
                var candidate = random.Next(30_000, 60_000);
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");

                try
                {
                    _listener.Start();
                    Port = candidate;
                    break;
                }
                catch (HttpListenerException) when (attempt < 25)
                {
                    // Taken by something else; try another.
                }
            }

            BaseUrl = $"http://127.0.0.1:{Port}/v1";

            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                    {
                        return;
                    }

                    using (var reader = new StreamReader(context.Request.InputStream))
                        RequestBody = await reader.ReadToEndAsync();
                    RequestPath = context.Request.Url?.AbsolutePath;
                    Authorization = context.Request.Headers["authorization"];

                    var bytes = Encoding.UTF8.GetBytes(sseBody);
                    context.Response.ContentType = "text/event-stream";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }

        public int Port { get; }
        public string BaseUrl { get; }
        public string? RequestBody { get; private set; }
        public string? RequestPath { get; private set; }
        public string? Authorization { get; private set; }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // Shutdown races are expected.
            }
        }
    }

    private static string Frame(string json) => $"event: x\ndata: {json}\n\n";

    private static async Task<List<LlmStreamEvent>> CollectAsync(
        string sse, List<ChatMessage> messages, IReadOnlyList<AiToolDefinition>? tools = null)
    {
        using var server = new StubServer(sse);
        var client = new OpenAiResponsesLlmClient(new LlmConfig
        {
            Provider = LlmProvider.OpenAiResponses,
            ProviderKey = "openai-chatgpt",
            Model = "gpt-5.1-codex",
            ApiKey = "test-token",
            BaseUrl = server.BaseUrl
        });

        var events = new List<LlmStreamEvent>();
        await foreach (var evt in client.StreamAsync(messages, tools ?? [], CancellationToken.None))
            events.Add(evt);

        // Exposed so a test can inspect what the request looked like.
        CollectedBody = server.RequestBody;
        CollectedPath = server.RequestPath;
        CollectedAuth = server.Authorization;
        return events;
    }

    private static string? CollectedBody { get; set; }
    private static string? CollectedPath { get; set; }
    private static string? CollectedAuth { get; set; }

    // ------------------------------------------------------------------
    // Request shape
    // ------------------------------------------------------------------

    [Fact]
    public async Task a_system_message_becomes_instructions_and_a_user_message_becomes_an_item()
    {
        await CollectAsync(
            Frame("""{"type":"response.completed","response":{"status":"completed"}}"""),
            [
                new ChatMessage("system", "you are terse"),
                new ChatMessage("user", "hi")
            ]);

        Assert.Equal("/v1/responses", CollectedPath);
        Assert.Equal("Bearer test-token", CollectedAuth);

        var body = System.Text.Json.Nodes.JsonNode.Parse(CollectedBody!)!;

        // The system prompt is not an input item on this endpoint.
        Assert.Equal("you are terse", body["instructions"]!.GetValue<string>());
        Assert.False(body["store"]!.GetValue<bool>());

        var input = body["input"]!.AsArray();
        Assert.Single(input);
        Assert.Equal("user", input[0]!["role"]!.GetValue<string>());
        Assert.Equal("input_text", input[0]!["content"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task a_tool_call_is_a_sibling_item_not_a_field_on_the_assistant_message()
    {
        await CollectAsync(
            Frame("""{"type":"response.completed","response":{"status":"completed"}}"""),
            [
                new ChatMessage("user", "list files"),
                new ChatMessage("assistant", "checking", ToolCalls: [new AiToolCall("call_1", "list_files", """{"path":"."}""")]),
                new ChatMessage("tool", "a.txt") { ToolCallId = "call_1" }
            ]);

        var input = System.Text.Json.Nodes.JsonNode.Parse(CollectedBody!)!["input"]!.AsArray();

        var functionCall = input.Single(item => item!["type"]?.GetValue<string>() == "function_call");
        Assert.Equal("call_1", functionCall!["call_id"]!.GetValue<string>());
        Assert.Equal("list_files", functionCall["name"]!.GetValue<string>());

        var output = input.Single(item => item!["type"]?.GetValue<string>() == "function_call_output");
        Assert.Equal("call_1", output!["call_id"]!.GetValue<string>());
        Assert.Equal("a.txt", output["output"]!.GetValue<string>());
    }

    [Fact]
    public async Task tool_declarations_use_the_responses_function_shape()
    {
        await CollectAsync(
            Frame("""{"type":"response.completed","response":{"status":"completed"}}"""),
            [new ChatMessage("user", "hi")],
            [
                new AiToolDefinition
                {
                    Name = "read_file",
                    Description = "Reads a file",
                    Parameters = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["path"] = new Dictionary<string, object?> { ["type"] = "string" }
                        }
                    },
                    Method = "GET",
                    Path = "/files"
                }
            ]);

        var tools = System.Text.Json.Nodes.JsonNode.Parse(CollectedBody!)!["tools"]!.AsArray();

        Assert.Single(tools);
        Assert.Equal("function", tools[0]!["type"]!.GetValue<string>());
        Assert.Equal("read_file", tools[0]!["name"]!.GetValue<string>());
        Assert.Equal("object", tools[0]!["parameters"]!["type"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    // Stream translation
    // ------------------------------------------------------------------

    [Fact]
    public async Task text_deltas_become_text_events_in_order()
    {
        var events = await CollectAsync(
            Frame("""{"type":"response.output_text.delta","delta":"Hel"}""")
            + Frame("""{"type":"response.output_text.delta","delta":"lo"}""")
            + Frame("""{"type":"response.completed","response":{"status":"completed"}}"""),
            [new ChatMessage("user", "hi")]);

        var text = events.OfType<LlmStreamEvent.TextDelta>().Select(delta => delta.Text).ToList();

        Assert.Equal(["Hel", "lo"], text);
        Assert.Equal("stop", events.OfType<LlmStreamEvent.Completed>().Last().FinishReason);
    }

    [Fact]
    public async Task a_function_call_arrives_with_its_full_arguments()
    {
        var events = await CollectAsync(
            Frame("""{"type":"response.output_item.added","item":{"type":"function_call","id":"fc_1","call_id":"call_9","name":"read_file"}}""")
            + Frame("""{"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":"{\"path\":"}""")
            + Frame("""{"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":"\"a.txt\"}"}""")
            + Frame("""{"type":"response.output_item.done","item":{"type":"function_call","call_id":"call_9","name":"read_file","arguments":"{\"path\":\"a.txt\"}"}}""")
            + Frame("""{"type":"response.completed","response":{"status":"completed"}}"""),
            [new ChatMessage("user", "read a.txt")]);

        var call = events.OfType<LlmStreamEvent.ToolCall>().Single();

        Assert.Equal("call_9", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("""{"path":"a.txt"}""", call.ArgumentsJson);

        // A turn that called a tool reports tool_use, not stop, so the loop continues.
        Assert.Equal("tool_use", events.OfType<LlmStreamEvent.Completed>().Last().FinishReason);
    }

    [Fact]
    public async Task usage_includes_cached_input_tokens()
    {
        var events = await CollectAsync(
            Frame("""{"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":120,"output_tokens":8,"input_tokens_details":{"cached_tokens":100}}}}"""),
            [new ChatMessage("user", "hi")]);

        var usage = events.OfType<LlmStreamEvent.Usage>().Single();

        Assert.Equal(120, usage.InputTokens);
        Assert.Equal(8, usage.OutputTokens);
        Assert.Equal(100, usage.CacheReadTokens);
    }

    [Fact]
    public async Task an_incomplete_response_reports_length_so_the_loop_can_guard()
    {
        var events = await CollectAsync(
            Frame("""{"type":"response.incomplete","response":{"status":"incomplete"}}"""),
            [new ChatMessage("user", "write a lot")]);

        Assert.Equal("length", events.OfType<LlmStreamEvent.Completed>().Last().FinishReason);
    }

    [Fact]
    public async Task a_failed_response_surfaces_the_provider_message()
    {
        var events = await CollectAsync(
            Frame("""{"type":"response.failed","response":{"error":{"message":"quota exceeded"}}}"""),
            [new ChatMessage("user", "hi")]);

        Assert.Equal("quota exceeded", events.OfType<LlmStreamEvent.Failed>().Single().Message);
    }

    [Fact]
    public async Task reasoning_deltas_are_kept_separate_from_text()
    {
        var events = await CollectAsync(
            Frame("""{"type":"response.reasoning_summary_text.delta","delta":"thinking"}""")
            + Frame("""{"type":"response.output_text.delta","delta":"answer"}""")
            + Frame("""{"type":"response.completed","response":{"status":"completed"}}"""),
            [new ChatMessage("user", "hi")]);

        Assert.Equal("thinking", events.OfType<LlmStreamEvent.ReasoningDelta>().Single().Text);
        Assert.Equal("answer", events.OfType<LlmStreamEvent.TextDelta>().Single().Text);
    }

    [Fact]
    public async Task malformed_frames_are_skipped_rather_than_ending_the_stream()
    {
        var events = await CollectAsync(
            "event: x\ndata: {not json\n\n"
            + Frame("""{"type":"response.output_text.delta","delta":"still here"}""")
            + Frame("""{"type":"response.completed","response":{"status":"completed"}}"""),
            [new ChatMessage("user", "hi")]);

        Assert.Equal("still here", events.OfType<LlmStreamEvent.TextDelta>().Single().Text);
    }
}

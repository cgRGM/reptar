using System.Text;
using System.Text.Json;
using Reptar;

namespace Reptar.Tests;

public sealed record AiTaskInput(string Title, int Priority, string? Description);
public sealed record AiTaskOutput(string Id, string Title, int Priority, string Status);

public class AiToolTests
{
    private static readonly Validator<AiTaskInput> TaskValidator = ReptarApi.Rule<AiTaskInput>()
        .Prop(x => x.Title, r => r.Min(3).Max(50))
        .Prop(x => x.Priority, r => r.Min(1).Max(5));

    [Fact]
    public void GetAiTools_defaults_to_explicit_routes_only()
    {
        var app = ReptarApp.Create();

        app.Get("/api/public", ctx => Task.FromResult(ctx.JsonRaw("""{"ok":true}""")));
        app.Get("/api/tool", ctx => Task.FromResult(ctx.JsonRaw("""{"ok":true}""")))
            .AsAiTool("explicit_tool", "An explicitly exposed tool");

        var defaults = app.GetAiTools();
        Assert.Single(defaults);
        Assert.Equal("explicit_tool", defaults[0].Name);

        var all = app.GetAiTools(onlyExplicit: false);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void GetAiTools_generates_valid_tool_definitions_for_openai_claude_and_gemini()
    {
        var app = ReptarApp.Create();

        app.Post("/api/projects/:projectId/tasks", TaskValidator, (ctx, input) =>
            Task.FromResult(ctx.Json(new AiTaskOutput("t_1", input.Title, input.Priority, "open"), 201)))
            .WithSummary("Create a new task")
            .WithDescription("Creates a task within a specified project.")
            .AsAiTool("create_task", "Creates a new task in a project");

        app.Get("/api/projects/:projectId/tasks/:id", ctx =>
            Task.FromResult(ctx.Json(new AiTaskOutput(ctx.Req.Param("id")!, "Test Task", 1, "open"))))
            .AsAiTool("get_task", "Fetch a single task by ID");

        // 1. OpenAI Format
        var openAiTools = app.GetAiTools(AiToolFormat.OpenAi);
        Assert.Equal(2, openAiTools.Count);

        var createTaskTool = openAiTools.First(t => t.Name == "create_task");
        Assert.Equal("Creates a new task in a project", createTaskTool.Description);
        Assert.Equal("POST", createTaskTool.Method);
        Assert.Equal("/api/projects/:projectId/tasks", createTaskTool.Path);

        var openAiDict = createTaskTool.ToDictionary(AiToolFormat.OpenAi);
        Assert.Equal("function", openAiDict["type"]);
        var fn = (Dictionary<string, object?>)openAiDict["function"]!;
        Assert.Equal("create_task", fn["name"]);
        var parameters = (Dictionary<string, object?>)fn["parameters"]!;
        Assert.Equal("object", parameters["type"]);

        var properties = (Dictionary<string, object?>)parameters["properties"]!;
        Assert.True(properties.ContainsKey("projectId"));
        Assert.True(properties.ContainsKey("title"));
        Assert.True(properties.ContainsKey("priority"));

        var required = (List<string>)parameters["required"]!;
        Assert.Contains("projectId", required);
        Assert.Contains("title", required);
        Assert.Contains("priority", required);

        // 2. Anthropic Claude Format
        var claudeDict = createTaskTool.ToDictionary(AiToolFormat.Anthropic);
        Assert.Equal("create_task", claudeDict["name"]);
        Assert.True(claudeDict.ContainsKey("input_schema"));

        // 3. Google Gemini Format
        var geminiDict = createTaskTool.ToDictionary(AiToolFormat.Gemini);
        Assert.Equal("create_task", geminiDict["name"]);
        Assert.True(geminiDict.ContainsKey("parameters"));
    }

    [Fact]
    public async Task ExecuteAiToolAsync_dispatches_in_process_with_parameter_substitution_and_validation()
    {
        var app = ReptarApp.Create();

        app.Post("/api/projects/:projectId/tasks", TaskValidator, (ctx, input) =>
        {
            var projectId = ctx.Req.Param("projectId");
            return Task.FromResult(ctx.Json(new
            {
                id = "task_99",
                projectId = projectId,
                title = input.Title,
                priority = input.Priority
            }, 201));
        }).AsAiTool("create_task");

        // Success case: valid arguments with route parameter and body
        var validArgs = JsonSerializer.Serialize(new
        {
            projectId = "proj_alpha",
            title = "Write Unit Tests",
            priority = 2
        });

        var result = await app.ExecuteAiToolAsync("create_task", validArgs);
        Assert.True(result.Success);
        Assert.Equal(201, result.StatusCode);
        Assert.NotNull(result.Content);
        Assert.Contains("task_99", result.Content);
        Assert.Contains("proj_alpha", result.Content);
        Assert.Contains("Write Unit Tests", result.Content);

        // Failure case: invalid arguments failing RuleFor constraints
        var invalidArgs = JsonSerializer.Serialize(new
        {
            projectId = "proj_alpha",
            title = "X", // too short (min 3)
            priority = 99 // out of range (max 5)
        });

        var failResult = await app.ExecuteAiToolAsync("create_task", invalidArgs);
        Assert.False(failResult.Success);
        Assert.Equal(400, failResult.StatusCode);
        Assert.NotNull(failResult.ValidationErrors);
        Assert.Equal(2, failResult.ValidationErrors.Count);

        // Verify ToLlmResponse() formats actionable field errors for agent self-correction
        var llmResponse = failResult.ToLlmResponse();
        Assert.Contains("Error", llmResponse);
        Assert.Contains("title", llmResponse);
        Assert.Contains("priority", llmResponse);
    }

    [Fact]
    public async Task AgentHarness_RunAsync_executes_autonomous_react_loop_until_completion()
    {
        var app = ReptarApp.Create();

        app.Post("/api/projects/:projectId/tasks", TaskValidator, (ctx, input) =>
            Task.FromResult(ctx.Json(new AiTaskOutput("tsk_42", input.Title, input.Priority, "created"), 201)))
            .AsAiTool("create_task");

        var conversation = new List<ChatMessage>
        {
            new("user", "Please create a high-priority task titled 'Ship Reptar v1' in project 'proj_1'.")
        };

        var simulatedTurn = 0;

        // Mock LLM model function simulating a 2-step ReAct agent:
        // Turn 1: LLM outputs tool call to create_task
        // Turn 2: LLM receives tool output and gives final conversational answer
        Task<AgentStep> MockModel(IReadOnlyList<ChatMessage> history, CancellationToken ct)
        {
            simulatedTurn++;
            if (simulatedTurn == 1)
            {
                var toolCalls = new List<AiToolCall>
                {
                    new(
                        Id: "call_123",
                        Name: "create_task",
                        Arguments: JsonSerializer.Serialize(new
                        {
                            projectId = "proj_1",
                            title = "Ship Reptar v1",
                            priority = 1
                        }))
                };
                return Task.FromResult(new AgentStep(Content: null, ToolCalls: toolCalls));
            }

            return Task.FromResult(new AgentStep(
                Content: "I've successfully created the task 'Ship Reptar v1' (ID: tsk_42) in project 'proj_1'.",
                IsComplete: true));
        }

        var runResult = await AgentHarness.RunAsync(
            model: MockModel,
            history: conversation,
            toolExecutor: (name, args) => app.ExecuteAiToolAsync(name, args),
            maxSteps: 5);

        Assert.True(runResult.Success);
        Assert.Equal(2, runResult.StepsTaken);
        Assert.Contains("Ship Reptar v1", runResult.FinalContent);

        // Check complete history:
        // 1. user prompt
        // 2. assistant tool_call
        // 3. tool execution response
        // 4. assistant final completion
        Assert.Equal(4, runResult.History.Count);
        Assert.Equal("user", runResult.History[0].Role);
        Assert.Equal("assistant", runResult.History[1].Role);
        Assert.Equal("tool", runResult.History[2].Role);
        Assert.Equal("call_123", runResult.History[2].ToolCallId);
        Assert.Contains("tsk_42", runResult.History[2].Content);
        Assert.Equal("assistant", runResult.History[3].Role);
    }

    [Fact]
    public async Task StreamLlmAsync_streams_tokens_with_done_sentinel()
    {
        var app = ReptarApp.Create();

        static async IAsyncEnumerable<string> MockTokenGenerator()
        {
            yield return "Hello";
            yield return " ";
            yield return "world";
            yield return "!";
        }

        app.Get("/api/ai/stream", ctx =>
            Task.FromResult(ctx.StreamLlmAsync(MockTokenGenerator())));

        var req = new ReptarRequest
        {
            Method = "GET",
            Path = "/api/ai/stream"
        };

        var response = await app.HandleAsync(req);
        Assert.Equal(200, response.Status);
        Assert.Equal("text/event-stream; charset=utf-8", response.ContentType);
        Assert.NotNull(response.BodyStream);

        var chunks = new List<string>();
        await foreach (var chunk in response.BodyStream(CancellationToken.None))
        {
            chunks.Add(Encoding.UTF8.GetString(chunk));
        }

        var fullStream = string.Join("", chunks);
        Assert.Contains("data: Hello\n\n", fullStream);
        Assert.Contains("data: world\n\n", fullStream);
        Assert.Contains("data: [DONE]\n\n", fullStream);
    }
}

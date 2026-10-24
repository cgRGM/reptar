using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace Reptar;

public sealed class AiClient : IAiClient
{
    [JSImport("aiRun", "app.host")]
    private static extern Task<string> RunJs(string model, string prompt);

    public async Task<AiResult> GenerateAsync(AiRequest request)
    {
        var rawJson = await RunJs(request.Model, request.Prompt);
        using var doc = JsonDocument.Parse(rawJson);

        var text = "";
        if (doc.RootElement.TryGetProperty("response", out var r))
        {
            text = r.GetString() ?? "";
        }
        else if (doc.RootElement.TryGetProperty("text", out var t))
        {
            text = t.GetString() ?? "";
        }
        else
        {
            text = rawJson;
        }

        return new AiResult(request.Model, text, 0, 0);
    }
}

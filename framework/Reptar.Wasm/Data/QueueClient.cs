using System.Runtime.InteropServices.JavaScript;
using System.Text.Json.Nodes;

namespace Reptar;

public sealed class QueueClient : IQueueClient
{
    [JSImport("queueSend", "app.host")]
    private static extern Task SendJs(string queueName, string messageJson);

    [JSImport("queueSendWithOptions", "app.host")]
    private static extern Task SendWithOptionsJs(
        string queueName,
        string messageJson,
        int delaySeconds);

    [JSImport("queueSendBatch", "app.host")]
    private static extern Task SendBatchJs(string queueName, string messagesJson);

    public Task SendAsync(
        string queueName,
        string messageJson,
        QueueSendOptions? options = null)
    {
        if (options?.DelaySeconds is > 0)
            return SendWithOptionsJs(queueName, messageJson, options.DelaySeconds.Value);

        // Cloudflare Queues has no native dedup; AttemptsHint is consumer
        // policy (max_retries in wrangler.jsonc). Only the delay maps.
        return SendJs(queueName, messageJson);
    }

    public async Task SendBatchAsync(
        string queueName,
        IReadOnlyList<string> messagesJson,
        QueueSendOptions? options = null)
    {
        var array = new JsonArray();
        foreach (var message in messagesJson)
        {
            array.Add(message);
        }

        await SendBatchJs(queueName, array.ToJsonString());
    }
}

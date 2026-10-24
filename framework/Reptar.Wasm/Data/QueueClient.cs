using System.Runtime.InteropServices.JavaScript;

namespace Reptar;

public sealed class QueueClient : IQueueClient
{
    [JSImport("queueSend", "app.host")]
    private static extern Task SendJs(string queueName, string messageJson);

    public Task SendAsync(string queueName, string messageJson) =>
        SendJs(queueName, messageJson);
}

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;

public sealed record ProbeQuery(string Subject, bool? Enabled = null);
public sealed record QueryResult(string Key, string Subject, string Enabled);
public sealed record StreamStats(int Active, int Completed, int Disconnected, int Failed);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(QueryResult))]
[JsonSerializable(typeof(StreamStats))]
internal partial class ProbeJson : JsonSerializerContext;

internal static class ProbeState
{
    private static int _active;
    private static int _completed;
    private static int _disconnected;
    private static int _failed;

    public static StreamStats Snapshot() => new(
        Volatile.Read(ref _active), Volatile.Read(ref _completed),
        Volatile.Read(ref _disconnected), Volatile.Read(ref _failed));

    public static async IAsyncEnumerable<byte[]> Stream(
        string mode, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _active);
        try
        {
            await Task.Yield();
            if (mode == "before")
            {
                Interlocked.Increment(ref _failed);
                throw new IOException("injected before first byte");
            }
            yield return Encoding.UTF8.GetBytes("first\n");
            if (mode == "after")
            {
                Interlocked.Increment(ref _failed);
                throw new IOException("injected after first byte");
            }
            if (mode == "disconnect")
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _disconnected);
                    throw;
                }
            }
            yield return Encoding.UTF8.GetBytes(new string('y', 4096));
            Interlocked.Increment(ref _completed);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public static async IAsyncEnumerable<string> Events(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(1, cancellationToken);
        yield return "one\ntwo";
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reptar;

/// <summary>
/// The request envelope shared by every NetWasm Worker entrypoint. Keeping it
/// in the framework means apps only provide <see cref="WorkerEntrypoint"/> glue
/// and their own JSON context; they do not hand-write host plumbing.
/// </summary>
public sealed record HostRequest(
    string Method,
    string Path,
    string Query,
    Dictionary<string, string> Headers,
    string Body,
    string RequestId);

public sealed record HostResponse(
    int Status,
    Dictionary<string, string> Headers,
    string Body,
    string[] SetCookies);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HostRequest))]
[JsonSerializable(typeof(HostResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string[]))]
internal partial class WorkerJsonContext : JsonSerializerContext
{
}

public static class WorkerJson
{
    public static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = WorkerJsonContext.Default,
        };
}

/// <summary>
/// Safe, source-generated bridge from the Worker host into a <see cref="ReptarApp"/>.
/// The host calls the exported <c>handle(string)</c>; this helper guarantees
/// that exactly one response is sent, malformed envelopes become 400s, and
/// unhandled exceptions become 500s instead of a permanently pending Promise.
/// </summary>
public static class WorkerEntrypoint
{
    private const string InternalErrorBody =
        "{\"code\":\"internal_error\",\"message\":\"Something went wrong.\"}";

    public static async Task HandleAsync(
        ReptarApp app,
        string requestJson,
        Action<string, string> respond)
    {
        var responded = false;

        void RespondOnce(string requestId, HostResponse response)
        {
            if (responded)
                return;

            responded = true;
            respond(requestId, JsonSerializer.Serialize(response, WorkerJson.Options));
        }

        HostRequest? request = null;

        try
        {
            request = JsonSerializer.Deserialize<HostRequest>(
                requestJson,
                WorkerJson.Options);
        }
        catch (JsonException)
        {
            // Fall through to the 400 response below.
        }

        if (request is null)
        {
            RespondOnce(
                ExtractRequestId(requestJson),
                new HostResponse(400, [], EncodeBody("{}"), []));
            return;
        }

        try
        {
            var result = await app.HandleWithBackgroundAsync(new ReptarRequest
            {
                Method = request.Method,
                Path = request.Path,
                Query = request.Query.TrimStart('?'),
                Headers = request.Headers,
                Cookies = ParseCookies(request.Headers),
                Body = string.IsNullOrEmpty(request.Body)
                    ? []
                    : Convert.FromBase64String(request.Body),
            });

            RespondOnce(
                request.RequestId,
                ToHostResponse(result.Response));

            try
            {
                await result.Background;
            }
            catch (Exception exception)
            {
                // The response is already on the wire. Surface the failure in
                // logs; the host's waitUntil sees the rejected export.
                Console.Error.WriteLine(exception);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            RespondOnce(
                request.RequestId,
                new HostResponse(500, [], EncodeBody(InternalErrorBody), []));
        }
    }

    private static HostResponse ToHostResponse(ReptarResponse response)
    {
        var headers = new Dictionary<string, string>(response.Headers);

        if (response.ContentType is not null)
            headers["content-type"] = response.ContentType;

        return new HostResponse(
            response.Status,
            headers,
            response.Body is null ? "" : Convert.ToBase64String(response.Body),
            response.SetCookies.ToArray());
    }

    private static string EncodeBody(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    private static string ExtractRequestId(string requestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);

            return document.RootElement.TryGetProperty(
                "requestId",
                out var requestId)
                ? requestId.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static Dictionary<string, string> ParseCookies(
        Dictionary<string, string> headers)
    {
        var cookies = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        if (!headers.TryGetValue("cookie", out var header))
            return cookies;

        foreach (var pair in header.Split(';'))
        {
            var separator = pair.IndexOf('=');

            if (separator <= 0)
                continue;

            var name = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();

            if (name.Length > 0)
                cookies[name] = value;
        }

        return cookies;
    }
}

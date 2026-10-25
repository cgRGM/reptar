using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.Hosting.Lambda;

public static class LambdaHost
{
    private static readonly HttpClient HttpClient = new();

    public static async Task RunLambdaAsync(
        this ReptarApp app,
        CancellationToken cancellationToken = default)
    {
        var runtimeApi = Environment.GetEnvironmentVariable("AWS_LAMBDA_RUNTIME_API");
        if (string.IsNullOrEmpty(runtimeApi))
        {
            throw new InvalidOperationException("AWS_LAMBDA_RUNTIME_API environment variable is not set.");
        }

        var nextInvocationUrl = $"http://{runtimeApi}/2018-06-01/runtime/invocation/next";

        while (!cancellationToken.IsCancellationRequested)
        {
            HttpResponseMessage nextResponse;
            try
            {
                nextResponse = await HttpClient.GetAsync(nextInvocationUrl, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            using (nextResponse)
            {
                nextResponse.EnsureSuccessStatusCode();

                if (!nextResponse.Headers.TryGetValues("Lambda-Runtime-Aws-Request-Id", out var idValues))
                {
                    continue;
                }

                var requestId = idValues.First();
                var responseUrl = $"http://{runtimeApi}/2018-06-01/runtime/invocation/{requestId}/response";
                var errorUrl = $"http://{runtimeApi}/2018-06-01/runtime/invocation/{requestId}/error";

                try
                {
                    var eventJson = await nextResponse.Content.ReadAsStringAsync(cancellationToken);
                    var reptarRequest = ParseApiGatewayV2Event(eventJson);

                    var reptarResponse = await app.HandleAsync(reptarRequest);
                    var responsePayload = FormatApiGatewayV2Response(reptarResponse);

                    using var responseContent = new StringContent(responsePayload, Encoding.UTF8, "application/json");
                    await HttpClient.PostAsync(responseUrl, responseContent, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    var errorPayload = JsonSerializer.Serialize(new
                    {
                        errorMessage = ex.Message,
                        errorType = ex.GetType().FullName,
                        stackTrace = ex.StackTrace,
                    });

                    using var errorContent = new StringContent(errorPayload, Encoding.UTF8, "application/json");
                    await HttpClient.PostAsync(errorUrl, errorContent, cancellationToken);
                }
            }
        }
    }

    public static ReptarRequest ParseApiGatewayV2Event(string eventJson)
    {
        var doc = JsonNode.Parse(eventJson);
        if (doc is null)
        {
            return new ReptarRequest { Method = "GET", Path = "/" };
        }

        var method = doc["requestContext"]?["http"]?["method"]?.GetValue<string>()
            ?? doc["httpMethod"]?.GetValue<string>()
            ?? "GET";

        var path = doc["rawPath"]?.GetValue<string>()
            ?? doc["path"]?.GetValue<string>()
            ?? "/";

        var query = doc["rawQueryString"]?.GetValue<string>() ?? "";
        var remoteIp = doc["requestContext"]?["http"]?["sourceIp"]?.GetValue<string>()
            ?? doc["requestContext"]?["identity"]?["sourceIp"]?.GetValue<string>();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc["headers"] is JsonObject headersObj)
        {
            foreach (var (key, node) in headersObj)
            {
                if (node is not null)
                {
                    headers[key] = node.ToString();
                }
            }
        }

        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc["cookies"] is JsonArray cookiesArr)
        {
            foreach (var cookieNode in cookiesArr)
            {
                var cookieStr = cookieNode?.ToString();
                if (string.IsNullOrEmpty(cookieStr))
                {
                    continue;
                }

                var idx = cookieStr.IndexOf('=');
                if (idx > 0)
                {
                    cookies[cookieStr[..idx].Trim()] = cookieStr[(idx + 1)..].Trim();
                }
            }
        }
        else if (headers.TryGetValue("cookie", out var cookieHeader))
        {
            foreach (var part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var idx = part.IndexOf('=');
                if (idx > 0)
                {
                    cookies[part[..idx]] = part[(idx + 1)..];
                }
            }
        }

        byte[]? body = null;
        var bodyNode = doc["body"];
        if (bodyNode is not null)
        {
            var rawBody = bodyNode.GetValue<string>();
            var isBase64 = doc["isBase64Encoded"]?.GetValue<bool>() ?? false;

            body = isBase64
                ? Convert.FromBase64String(rawBody)
                : Encoding.UTF8.GetBytes(rawBody);
        }

        return new ReptarRequest
        {
            Method = method,
            Path = path,
            Query = query,
            RemoteIp = remoteIp,
            Headers = headers,
            Cookies = cookies,
            Body = body,
        };
    }

    public static string FormatApiGatewayV2Response(ReptarResponse response)
    {
        var responseObj = new JsonObject
        {
            ["statusCode"] = response.Status,
            ["isBase64Encoded"] = true,
        };

        var headersObj = new JsonObject();
        foreach (var (key, value) in response.Headers)
        {
            headersObj[key] = value;
        }

        if (response.ContentType is not null)
        {
            headersObj["content-type"] = response.ContentType;
        }

        responseObj["headers"] = headersObj;

        if (response.SetCookies.Count > 0)
        {
            var cookiesArr = new JsonArray();
            foreach (var cookie in response.SetCookies)
            {
                cookiesArr.Add(cookie);
            }
            responseObj["cookies"] = cookiesArr;
        }

        if (response.Body is { Length: > 0 } bytes)
        {
            responseObj["body"] = Convert.ToBase64String(bytes);
        }
        else
        {
            responseObj["body"] = "";
            responseObj["isBase64Encoded"] = false;
        }

        return responseObj.ToJsonString();
    }
}

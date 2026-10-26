#if !NETWASM
using System.Text;

namespace Reptar;

/// <summary>
/// Portable SHA-256/HMAC used by the queue adapters that sign requests
/// (AWS SigV4, Azure Storage SharedKey). Native targets only: NetWasm apps
/// use the Cloudflare Queues binding instead.
/// </summary>
public static class QueueCrypto
{
    public static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    public static byte[] Sha256(byte[] message) => System.Security.Cryptography.SHA256.HashData(message);

    public static byte[] Hmac(byte[] key, byte[] message) =>
        System.Security.Cryptography.HMACSHA256.HashData(key, message);

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static string HexSha256(byte[] message) => Hex(Sha256(message));

    public static string Base64(byte[] bytes) => Convert.ToBase64String(bytes);

    public static byte[] Base64Decode(string value) => Convert.FromBase64String(value);
}

/// <summary>
/// AWS Signature Version 4 request signing implemented over raw HTTP —
/// zero SDK dependency, Native AOT safe. Validated against the published
/// AWS walkthrough vector (see Reptar.Tests AwsSigV4Tests).
/// </summary>
public static class AwsSigV4
{
    /// <summary>
    /// Signs a request and returns the Authorization header value. Adds
    /// x-amz-date (and x-amz-security-token when supplied) to
    /// <paramref name="headers"/>. The x-amz-content-sha256 header is S3-only;
    /// the hashed payload is always part of the canonical request.
    /// </summary>
    public static string Sign(
        string method,
        string service,
        string region,
        string host,
        string canonicalUri,
        string canonicalQuery,
        Dictionary<string, string> headers,
        byte[] payload,
        string accessKey,
        string secretKey,
        string? sessionToken,
        DateTimeOffset now)
    {
        var dateStamp = $"{now.Year:0000}{now.Month:00}{now.Day:00}";
        var amzDate = $"{dateStamp}T{now.Hour:00}{now.Minute:00}{now.Second:00}Z";
        var payloadHash = QueueCrypto.HexSha256(payload);

        // SigV4 signs lowercase header names in byte order.
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in headers)
            normalized[key.ToLowerInvariant()] = value.Trim();
        normalized["host"] = host;
        normalized["x-amz-date"] = amzDate;
        if (sessionToken is { Length: > 0 })
            normalized["x-amz-security-token"] = sessionToken;

        var signedHeaders = string.Join(';', normalized.Keys.OrderBy(name => name, StringComparer.Ordinal));

        var canonicalHeaders = new StringBuilder();
        foreach (var name in normalized.Keys.OrderBy(name => name, StringComparer.Ordinal))
            canonicalHeaders.Append(name).Append(':').Append(normalized[name]).Append('\n');

        var canonicalRequest =
            $"{method}\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

        var scope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign =
            $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{QueueCrypto.HexSha256(QueueCrypto.Utf8(canonicalRequest))}";

        var signature = Signature(secretKey, dateStamp, region, service, stringToSign);

        headers["x-amz-date"] = amzDate;
        if (sessionToken is { Length: > 0 })
            headers["x-amz-security-token"] = sessionToken;

        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, " +
               $"SignedHeaders={signedHeaders}, " +
               $"Signature={signature}";
    }

    /// <summary>The derived signing key as hex — published AWS example value:
    /// c4afb1cc5771d871763a393e44b703571b55cc28424d1a5e86da6ed3c154a4b9.</summary>
    public static byte[] SigningKey(string secretKey, string dateStamp, string region, string service)
    {
        var dateKey = QueueCrypto.Hmac(QueueCrypto.Utf8("AWS4" + secretKey), QueueCrypto.Utf8(dateStamp));
        var regionKey = QueueCrypto.Hmac(dateKey, QueueCrypto.Utf8(region));
        var serviceKey = QueueCrypto.Hmac(regionKey, QueueCrypto.Utf8(service));
        return QueueCrypto.Hmac(serviceKey, QueueCrypto.Utf8("aws4_request"));
    }

    /// <summary>Final lowercase hex signature — published AWS example value:
    /// 5d672d79c15b13162d9279b0855cfba6789a8edb4c82c400e06b5924a6f2b5d7.</summary>
    public static string Signature(
        string secretKey, string dateStamp, string region, string service, string stringToSign) =>
        QueueCrypto.Hex(QueueCrypto.Hmac(SigningKey(secretKey, dateStamp, region, service), QueueCrypto.Utf8(stringToSign)));

    /// <summary>AWS UriEncode: everything except A-Z a-z 0-9 - . _ ~ becomes
    /// %XX with uppercase hex; space is %20, never '+'.</summary>
    public static string UriEncode(string value)
    {
        var builder = new StringBuilder();
        foreach (var bite in Encoding.UTF8.GetBytes(value))
        {
            if (bite is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or
                >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
            {
                builder.Append((char)bite);
            }
            else
            {
                builder.Append('%').Append(bite.ToString("X2"));
            }
        }
        return builder.ToString();
    }
}

/// <summary>
/// String-slicing XML extraction for the few tags the queue adapters read
/// (SQS and Azure Storage responses). Deliberately dependency-free: no
/// System.Xml under trimming, identical behavior on every target.
/// </summary>
public static class MiniXml
{
    /// <summary>Returns the inner text of the first <paramref name="tag"/> occurrence, or null.</summary>
    public static string? Tag(string xml, string tag)
    {
        var open = "<" + tag + ">";
        var close = "</" + tag + ">";
        var start = xml.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
            return null;

        var from = start + open.Length;
        var end = xml.IndexOf(close, from, StringComparison.Ordinal);
        return end < 0 ? null : xml[from..end];
    }

    /// <summary>Returns every <paramref name="tag"/>...&lt;/tag&gt; block, outer tags included.</summary>
    public static List<string> Blocks(string xml, string tag)
    {
        var open = "<" + tag + ">";
        var close = "</" + tag + ">";
        var blocks = new List<string>();
        var cursor = 0;

        while (cursor < xml.Length)
        {
            var start = xml.IndexOf(open, cursor, StringComparison.Ordinal);
            if (start < 0)
                break;

            var end = xml.IndexOf(close, start, StringComparison.Ordinal);
            if (end < 0)
                break;

            blocks.Add(xml[start..(end + close.Length)]);
            cursor = end + close.Length;
        }

        return blocks;
    }

    /// <summary>
    /// Extracts Name/Value pairs from SQS attribute lists:
    /// &lt;Attribute&gt;&lt;Name&gt;..&lt;/Name&gt;&lt;Value&gt;..&lt;/Value&gt;&lt;/Attribute&gt;.
    /// </summary>
    public static Dictionary<string, string> Attributes(string xml, string container)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var block in Blocks(xml, container))
        {
            var name = Tag(block, "Name");
            var value = Tag(block, "Value");
            if (name is not null && value is not null)
                result[name] = value;
        }
        return result;
    }
}
#endif

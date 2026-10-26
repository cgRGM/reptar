namespace Reptar;

/// <summary>
/// Outbound HTTP URL policy for native hosts, mirroring the Workers shim guard
/// (<c>packages/host/src/host.ts</c>): loopback, link-local, private, and
/// metadata networks are unreachable by default, and an explicit allowlist
/// overrides the blocklist for deployments with legitimate internal
/// destinations.
///
/// Unlike the Workers guard (which is https-only), the native policy accepts
/// any scheme so self-hosted object storage on plain HTTP keeps working; the
/// private-network block is the security boundary that closes the SSRF
/// surface (including credential-bearing requests such as LLM provider
/// calls and OAuth token exchanges).
///
/// Residual gap, shared with the Workers guard: policy runs before DNS
/// resolution, so hostnames that only resolve to private addresses via
/// unusual encodings (decimal/hex IP forms, DNS rebinding) are not blocked.
/// </summary>
public static class HttpUrlPolicy
{
    /// <summary>
    /// True when <paramref name="url"/> may be fetched: a valid absolute URL
    /// whose host matches the allowlist when one is configured, or is not a
    /// private/metadata address otherwise.
    /// </summary>
    public static bool IsAllowed(string url, IReadOnlyList<string>? allowedHosts = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return false;

        // Unlike the Workers guard (https-only), plain http is accepted so
        // self-hosted object storage keeps working; any other scheme is a
        // misconfiguration.
        if (parsed.Scheme is not ("http" or "https"))
            return false;

        var hostname = parsed.Host.ToLowerInvariant();

        // .NET keeps IPv6 brackets in Uri.Host; the Workers shim strips them
        // before the private-range checks — mirror that here.
        if (hostname.StartsWith('[') && hostname.EndsWith(']'))
            hostname = hostname[1..^1];

        if (allowedHosts is { Count: > 0 })
        {
            for (var i = 0; i < allowedHosts.Count; i++)
            {
                var host = allowedHosts[i].ToLowerInvariant().TrimEnd('.');

                if (hostname == host ||
                    hostname.EndsWith($".{host}", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        return !IsPrivateHostname(hostname);
    }

    /// <summary>
    /// Throws when <paramref name="url"/> targets a blocked host, before any
    /// connection is attempted.
    /// </summary>
    public static void EnsureAllowed(string url, IReadOnlyList<string>? allowedHosts = null)
    {
        if (!IsAllowed(url, allowedHosts))
            throw new InvalidOperationException($"Outbound HTTP host is not allowed: {url}");
    }

    private static bool IsPrivateHostname(string host) =>
        IsPrivateName(host) ||
        IsPrivateIpv6(host) ||
        IsPrivateIpv4(host);

    private static bool IsPrivateName(string host) =>
        host == "localhost" ||
        host.EndsWith(".localhost", StringComparison.Ordinal) ||
        host.EndsWith(".local", StringComparison.Ordinal) ||
        host == "metadata" ||
        host == "metadata.google.internal";

    private static bool IsPrivateIpv6(string host) =>
        host == "::1" ||
        host.StartsWith("fc", StringComparison.Ordinal) ||
        host.StartsWith("fd", StringComparison.Ordinal) ||
        host.StartsWith("fe80", StringComparison.Ordinal) ||
        host.StartsWith("::ffff:127.", StringComparison.Ordinal);

    private static bool IsPrivateIpv4(string host)
    {
        var parts = host.Split('.');

        if (parts.Length != 4)
            return false;

        Span<byte> octets = stackalloc byte[4];

        for (var i = 0; i < 4; i++)
        {
            if (!byte.TryParse(parts[i], out var octet))
                return false;

            octets[i] = octet;
        }

        var a = octets[0];
        var b = octets[1];

        return a == 0 ||
            a == 10 ||
            a == 127 ||
            (a == 169 && b == 254) ||
            (a == 172 && b >= 16 && b <= 31) ||
            (a == 192 && b == 168) ||
            (a == 100 && b >= 64 && b <= 127) ||
            a >= 224;
    }
}

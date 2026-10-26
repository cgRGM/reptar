#if !NETWASM
using System.Globalization;

namespace Reptar;

public sealed class StaticFileOptions
{
    public string IndexFile { get; set; } = "index.html";
    public bool SpaFallback { get; set; } = true;
    public string CacheControl { get; set; } = "public, max-age=3600";
    public string? ImmutableCacheControl { get; set; } = "public, max-age=31536000, immutable";
    public string[] ApiPathPrefixes { get; set; } = ["/api/", "/health/", "/openapi.json", "/scalar", "/swagger"];
}

[ReptarEntity]
public sealed partial record HealthProbeRow(int Ok);

public static class ProductionExtensions
{
    private static readonly byte[] LivePayload =
        "{\"status\":\"healthy\"}"u8.ToArray();

    private static readonly byte[] ReadyPayload =
        "{\"status\":\"ready\",\"database\":\"connected\"}"u8.ToArray();

    private static readonly byte[] UnavailablePayload =
        "{\"status\":\"unavailable\",\"database\":\"disconnected\"}"u8.ToArray();

    public static ReptarApp MapHealth(
        this ReptarApp app,
        string livePath = "/health/live",
        string readyPath = "/health/ready")
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Get(livePath, _ => Task.FromResult(ReptarResponse.Json(LivePayload, 200)))
            .WithSummary("Liveness probe")
            .WithTag("Health");

        app.Get(readyPath, async ctx =>
        {
            try
            {
                await ctx.Db.QuerySingleOrDefaultAsync<HealthProbeRow>("SELECT 1 AS ok");
                return ReptarResponse.Json(ReadyPayload, 200);
            }
            catch
            {
                return ReptarResponse.Json(UnavailablePayload, 503);
            }
        })
        .WithSummary("Readiness probe")
        .WithTag("Health");

        return app;
    }

    public static ReptarApp UseSecurityHeaders(
        this ReptarApp app,
        SecurityHeadersOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(SecurityHeadersMiddleware(options));
    }

    public static ReptarApp UseSecurityHeaders(
        this ReptarApp app,
        Action<SecurityHeadersOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SecurityHeadersOptions();
        configure(options);
        return app.Use(SecurityHeadersMiddleware(options));
    }

    public static ReptarMiddleware SecurityHeadersMiddleware(SecurityHeadersOptions? options = null) =>
        Middlewares.SecurityHeaders(options);

    public static ReptarApp UseStaticFiles(
        this ReptarApp app,
        string rootDirectory = "wwwroot",
        StaticFileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(StaticFilesMiddleware(rootDirectory, options));
    }

    public static ReptarMiddleware StaticFilesMiddleware(
        string rootDirectory = "wwwroot",
        StaticFileOptions? options = null)
    {
        var opts = options ?? new StaticFileOptions();
        var fullRoot = Path.GetFullPath(rootDirectory);
        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return async (ctx, next) =>
        {
            var response = await next();
            if (response.Status != 404)
                return response;

            var method = ctx.Req.Method;
            var isGet = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
            var isHead = !isGet && string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);

            if (!isGet && !isHead)
                return response;

            var rawPath = ctx.Req.Path;
            if (string.IsNullOrEmpty(rawPath) || rawPath.Contains('\0'))
                return response;

            var decodedPath = Uri.UnescapeDataString(rawPath);
            if (decodedPath.Contains("..", StringComparison.Ordinal) || decodedPath.Contains('\0'))
                return response;

            var relativePath = decodedPath.TrimStart('/', '\\');
            if (string.IsNullOrEmpty(relativePath))
                relativePath = opts.IndexFile;

            var candidatePath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
            if (!candidatePath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
                return response;

            if (Directory.Exists(candidatePath))
            {
                candidatePath = Path.GetFullPath(Path.Combine(candidatePath, opts.IndexFile));
                if (!candidatePath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
                    return response;
            }

            if (!File.Exists(candidatePath))
            {
                if (!opts.SpaFallback || HasFileExtension(relativePath) || IsApiPath(decodedPath, opts.ApiPathPrefixes))
                    return response;

                var spaIndexPath = Path.GetFullPath(Path.Combine(fullRoot, opts.IndexFile));
                if (!spaIndexPath.StartsWith(rootWithSeparator, StringComparison.Ordinal) || !File.Exists(spaIndexPath))
                    return response;

                candidatePath = spaIndexPath;
            }

            var fileInfo = new FileInfo(candidatePath);
            var etag = string.Create(
                CultureInfo.InvariantCulture,
                $"\"{fileInfo.Length:x}-{fileInfo.LastWriteTimeUtc.Ticks:x}\"");

            var ifNoneMatch = ctx.Req.Header("If-None-Match");
            if (!string.IsNullOrEmpty(ifNoneMatch) && MatchesEtag(ifNoneMatch, etag))
            {
                var notModified = new ReptarResponse { Status = 304 };
                notModified.Headers["ETag"] = etag;
                notModified.Headers["Cache-Control"] = ResolveCacheControl(candidatePath, opts);
                return notModified;
            }

            var contentType = ResolveMimeType(candidatePath);
            var bytes = isHead ? [] : await File.ReadAllBytesAsync(candidatePath);

            var fileResponse = new ReptarResponse
            {
                Status = 200,
                ContentType = contentType,
                Body = bytes,
            };
            fileResponse.Headers["ETag"] = etag;
            fileResponse.Headers["Cache-Control"] = ResolveCacheControl(candidatePath, opts);
            if (isHead)
            {
                fileResponse.Headers["Content-Length"] = fileInfo.Length.ToString(CultureInfo.InvariantCulture);
            }

            return fileResponse;
        };
    }

    private static bool MatchesEtag(string headerValue, string etag)
    {
        if (headerValue == "*")
            return true;

        var trimmed = headerValue.AsSpan().Trim();
        if (trimmed.StartsWith("W/", StringComparison.Ordinal))
            trimmed = trimmed[2..].Trim();

        return trimmed.Equals(etag.AsSpan(), StringComparison.Ordinal);
    }

    private static bool HasFileExtension(string relativePath)
    {
        var lastSlash = relativePath.LastIndexOf('/');
        var fileName = lastSlash >= 0 ? relativePath[(lastSlash + 1)..] : relativePath;
        return fileName.Contains('.');
    }

    private static bool IsApiPath(string path, string[] prefixes)
    {
        for (var i = 0; i < prefixes.Length; i++)
        {
            if (path.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ResolveCacheControl(string filePath, StaticFileOptions opts)
    {
        if (opts.ImmutableCacheControl is not null &&
            (filePath.Contains("/assets/", StringComparison.OrdinalIgnoreCase) ||
             filePath.Contains("\\assets\\", StringComparison.OrdinalIgnoreCase) ||
             filePath.Contains("/_astro/", StringComparison.OrdinalIgnoreCase) ||
             filePath.Contains("\\_astro\\", StringComparison.OrdinalIgnoreCase)))
        {
            return opts.ImmutableCacheControl;
        }

        return opts.CacheControl;
    }

    public static string ResolveMimeType(string filePath) =>
        Files.ContentTypeFor(filePath);
}
#endif

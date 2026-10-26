using System.Globalization;

namespace Reptar;

public sealed record StorageFileOptions
{
    public static readonly StorageFileOptions Default = new();

    /// <summary>Overrides the stored content type for this response.</summary>
    public string? ContentType { get; init; }

    /// <summary>Sets Content-Disposition. Also implies an attachment by default.</summary>
    public string? FileName { get; init; }

    /// <summary>Serve as an attachment using the key's file name when no FileName is set.</summary>
    public bool Download { get; init; }

    /// <summary>Use inline disposition when a FileName is set.</summary>
    public bool Inline { get; init; } = true;

    public string? CacheControl { get; init; }

    /// <summary>Shorthand for <c>public, max-age=31536000, immutable</c>.</summary>
    public bool Immutable { get; init; }

    public bool EnableRange { get; init; } = true;

    public bool EnableConditional { get; init; } = true;

    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

public static class StorageFile
{
    public static async Task<ReptarResponse> SendAsync(
        Ctx ctx,
        string key,
        StorageFileOptions? options = null)
    {
        options ??= StorageFileOptions.Default;

        string normalized;
        try
        {
            normalized = StorageKey.Normalize(key);
        }
        catch (StorageException exception)
        {
            return MapError(ctx, exception);
        }

        StorageObject? info;
        try
        {
            info = await ctx.Storage.HeadAsync(normalized);
        }
        catch (StorageException exception) when (exception.Code != StorageErrorCode.Unsupported)
        {
            return MapError(ctx, exception);
        }
        catch (StorageException)
        {
            info = null;
        }
        catch (NotSupportedException)
        {
            info = null;
        }

        byte[]? body = null;
        if (info is null)
        {
            try
            {
                body = await ctx.Storage.GetAsync(normalized);
            }
            catch (StorageException exception)
            {
                return MapError(ctx, exception);
            }

            if (body is null)
                return ctx.NotFound();

            info = new StorageObject(normalized, body.Length);
        }

        var contentType = ResolveContentType(normalized, info, options);
        var cacheControl = ResolveCacheControl(info, options);
        var etag = info.HttpETag;
        var lastModified = info.LastModified;
        var isHead = IsHead(ctx.Req.Method);

        if (options.EnableConditional)
        {
            var ifNoneMatch = ctx.Req.Header("If-None-Match");
            if (!string.IsNullOrEmpty(ifNoneMatch))
            {
                if (etag.Length > 0 && MatchesEtag(ifNoneMatch, etag))
                    return NotModified(etag, cacheControl, lastModified);
            }
            else if (lastModified is { } modified)
            {
                var ifModifiedSince = ctx.Req.Header("If-Modified-Since");
                if (!string.IsNullOrEmpty(ifModifiedSince) &&
                    StorageKey.TryParseHttpDate(ifModifiedSince, out var since) &&
                    modified <= since.AddSeconds(1))
                {
                    return NotModified(etag, cacheControl, lastModified);
                }
            }
        }

        var rangeHeader = options.EnableRange && !isHead
            ? ctx.Req.Header("Range")
            : null;

        if (!string.IsNullOrEmpty(rangeHeader))
        {
            var ifRange = ctx.Req.Header("If-Range");
            if (!string.IsNullOrEmpty(ifRange) &&
                !IfRangeMatches(ifRange, etag, lastModified))
            {
                // The validator changed, so the client must receive the full
                // representation rather than a range from a stale copy.
                rangeHeader = null;
            }
        }

        if (!string.IsNullOrEmpty(rangeHeader))
        {
            var range = ParseRange(rangeHeader, info.Size);
            if (range.Kind == RangeKind.Unsatisfiable)
                return RangeNotSatisfiable(ctx, info.Size, etag, cacheControl, lastModified);

            if (range.Kind == RangeKind.Valid)
            {
                StorageRange? slice = null;
                try
                {
                    if (ctx.Storage.Capabilities.Has(StorageFeatures.RangeReads))
                    {
                        slice = await ctx.Storage.GetRangeAsync(
                            normalized,
                            range.Start,
                            range.End - range.Start + 1);
                    }
                }
                catch (StorageException exception) when (exception.Code == StorageErrorCode.RangeNotSatisfiable)
                {
                    return RangeNotSatisfiable(ctx, info.Size, etag, cacheControl, lastModified);
                }
                catch (StorageException exception) when (exception.Code == StorageErrorCode.Unsupported)
                {
                    // Provider advertised range reads but does not implement
                    // them; fall through to the in-memory slice path.
                    slice = null;
                }
                catch (StorageException exception)
                {
                    return MapError(ctx, exception);
                }

                if (slice is null)
                {
                    body ??= await ctx.Storage.GetAsync(normalized);
                    if (body is null)
                        return ctx.NotFound();

                    var start = (int)Math.Min(range.Start, body.Length);
                    var end = (int)Math.Min(range.End, body.Length - 1);
                    if (start > end)
                        return RangeNotSatisfiable(ctx, info.Size, etag, cacheControl, lastModified);

                    var bytes = new byte[end - start + 1];
                    Array.Copy(body, start, bytes, 0, bytes.Length);
                    slice = new StorageRange(
                        bytes,
                        start,
                        body.Length,
                        contentType,
                        info.ETag,
                        info.LastModified);
                }

                var partial = new ReptarResponse
                {
                    Status = 206,
                    ContentType = contentType,
                    Body = slice.Data,
                };

                ApplyHeaders(
                    partial,
                    normalized,
                    etag,
                    cacheControl,
                    lastModified,
                    options);
                partial.Headers["Content-Range"] =
                    $"bytes {slice.Offset.ToString(CultureInfo.InvariantCulture)}-{slice.End.ToString(CultureInfo.InvariantCulture)}/{slice.TotalSize.ToString(CultureInfo.InvariantCulture)}";
                partial.Headers["Content-Length"] =
                    slice.Length.ToString(CultureInfo.InvariantCulture);

                return partial;
            }
        }

        if (isHead)
        {
            var head = new ReptarResponse
            {
                Status = 200,
                ContentType = contentType,
            };

            ApplyHeaders(head, normalized, etag, cacheControl, lastModified, options);
            head.Headers["Content-Length"] =
                info.Size.ToString(CultureInfo.InvariantCulture);
            return head;
        }

        body ??= await ctx.Storage.GetAsync(normalized);
        if (body is null)
            return ctx.NotFound();

        var full = new ReptarResponse
        {
            Status = 200,
            ContentType = contentType,
            Body = body,
        };

        ApplyHeaders(full, normalized, etag, cacheControl, lastModified, options);
        full.Headers["Content-Length"] =
            body.Length.ToString(CultureInfo.InvariantCulture);
        return full;
    }

    private static void ApplyHeaders(
        ReptarResponse response,
        string key,
        string etag,
        string cacheControl,
        DateTimeOffset? lastModified,
        StorageFileOptions options)
    {
        if (etag.Length > 0)
            response.Headers["ETag"] = etag;

        if (lastModified is { } modified)
        {
            response.Headers["Last-Modified"] =
                modified.ToString("R", CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(cacheControl))
            response.Headers["Cache-Control"] = cacheControl;

        if (options.EnableRange)
            response.Headers["Accept-Ranges"] = "bytes";

        response.Headers["X-Content-Type-Options"] = "nosniff";

        if (!string.IsNullOrWhiteSpace(options.FileName))
        {
            response.Headers["Content-Disposition"] = StorageKey.ContentDisposition(
                options.FileName,
                options.Inline && !options.Download);
        }
        else if (options.Download)
        {
            response.Headers["Content-Disposition"] = StorageKey.ContentDisposition(
                StorageKey.FileName(key));
        }

        if (options.Headers is { Count: > 0 })
        {
            foreach (var (name, value) in options.Headers)
                response.Headers[name] = value;
        }
    }

    private static string ResolveContentType(
        string key,
        StorageObject info,
        StorageFileOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ContentType))
            return options.ContentType;

        if (!string.IsNullOrWhiteSpace(info.ContentType) &&
            !string.Equals(
                info.ContentType,
                "application/octet-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            return info.ContentType;
        }

        return Files.ContentTypeFor(key);
    }

    private static string ResolveCacheControl(
        StorageObject info,
        StorageFileOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CacheControl))
            return options.CacheControl;

        if (options.Immutable)
            return "public, max-age=31536000, immutable";

        if (info.Metadata is not null)
        {
            foreach (var (name, value) in info.Metadata)
            {
                if (string.Equals(name, "cache-control", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        // Safe default: browser caches may revalidate, shared caches must not
        // store private objects. Public assets opt into Immutable or an
        // explicit CacheControl.
        return "private, max-age=0, must-revalidate";
    }

    private static ReptarResponse NotModified(
        string etag,
        string cacheControl,
        DateTimeOffset? lastModified)
    {
        var response = new ReptarResponse { Status = 304 };
        if (etag.Length > 0)
            response.Headers["ETag"] = etag;
        if (lastModified is { } modified)
        {
            response.Headers["Last-Modified"] =
                modified.ToString("R", CultureInfo.InvariantCulture);
        }
        if (!string.IsNullOrWhiteSpace(cacheControl))
            response.Headers["Cache-Control"] = cacheControl;

        return response;
    }

    private static ReptarResponse RangeNotSatisfiable(
        Ctx ctx,
        long size,
        string etag,
        string cacheControl,
        DateTimeOffset? lastModified)
    {
        var response = ctx.Error(
            416,
            "range_not_satisfiable",
            "Requested range is not satisfiable.");

        response.Headers["Content-Range"] =
            $"bytes */{size.ToString(CultureInfo.InvariantCulture)}";
        if (etag.Length > 0)
            response.Headers["ETag"] = etag;
        if (lastModified is { } modified)
        {
            response.Headers["Last-Modified"] =
                modified.ToString("R", CultureInfo.InvariantCulture);
        }
        if (!string.IsNullOrWhiteSpace(cacheControl))
            response.Headers["Cache-Control"] = cacheControl;

        return response;
    }

    private static bool MatchesEtag(string header, string etag)
    {
        var normalized = etag.Trim().Trim('"');
        foreach (var raw in header.Split(','))
        {
            var candidate = raw.Trim();
            if (candidate == "*")
                return true;

            if (candidate.StartsWith("W/", StringComparison.Ordinal))
                candidate = candidate[2..].Trim();

            candidate = candidate.Trim('"');
            if (string.Equals(candidate, normalized, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IfRangeMatches(
        string ifRange,
        string etag,
        DateTimeOffset? lastModified)
    {
        var value = ifRange.Trim();

        // Weak validators cannot be used for If-Range; force a full response.
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            return false;

        // A quoted value is an entity-tag; an unquoted value is an HTTP date.
        if (value.StartsWith('"'))
            return etag.Length > 0 && MatchesEtag(value, etag);

        return lastModified is { } modified &&
               StorageKey.TryParseHttpDate(value, out var since) &&
               modified <= since.AddSeconds(1);
    }

    private static bool IsHead(string method) =>
        method.Length == 4 &&
        string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);

    private static ParsedRange ParseRange(string header, long size)
    {
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return ParsedRange.None;

        var spec = header["bytes=".Length..].Trim();
        if (spec.Contains(',', StringComparison.Ordinal))
            return ParsedRange.None;

        var dash = spec.IndexOf('-');
        if (dash < 0)
            return ParsedRange.None;

        var startText = spec[..dash].Trim();
        var endText = spec[(dash + 1)..].Trim();

        if (startText.Length == 0)
        {
            if (!long.TryParse(
                    endText,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var suffix) ||
                suffix <= 0)
            {
                return ParsedRange.None;
            }

            if (size == 0)
                return ParsedRange.Unsatisfiable;

            return new ParsedRange(
                RangeKind.Valid,
                Math.Max(0, size - suffix),
                size - 1);
        }

        if (!long.TryParse(
                startText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var start) ||
            start < 0)
        {
            return ParsedRange.None;
        }

        if (size == 0 || start >= size)
            return ParsedRange.Unsatisfiable;

        long end;
        if (endText.Length == 0)
        {
            end = size - 1;
        }
        else if (!long.TryParse(
                     endText,
                     NumberStyles.Integer,
                     CultureInfo.InvariantCulture,
                     out end) ||
                 end < start)
        {
            return ParsedRange.Unsatisfiable;
        }

        if (end >= size)
            end = size - 1;

        return new ParsedRange(RangeKind.Valid, start, end);
    }

    private static ReptarResponse MapError(Ctx ctx, StorageException exception) =>
        exception.Code switch
        {
            StorageErrorCode.NotFound => ctx.NotFound(exception.Message),
            StorageErrorCode.InvalidKey => ctx.Error(
                400,
                "invalid_storage_key",
                exception.Message),
            StorageErrorCode.Unauthorized or StorageErrorCode.Forbidden => ctx.Error(
                403,
                "storage_forbidden",
                exception.Message),
            StorageErrorCode.PreconditionFailed => ctx.Error(
                412,
                "precondition_failed",
                exception.Message),
            StorageErrorCode.Conflict => ctx.Conflict(exception.Message),
            StorageErrorCode.TooLarge => ctx.Error(
                413,
                "payload_too_large",
                exception.Message),
            StorageErrorCode.RangeNotSatisfiable => ctx.Error(
                416,
                "range_not_satisfiable",
                exception.Message),
            StorageErrorCode.Unsupported => ctx.Error(
                501,
                "storage_unsupported",
                exception.Message),
            StorageErrorCode.Unavailable => ctx.Error(
                503,
                "storage_unavailable",
                exception.Message),
            _ => ctx.Error(500, "storage_error", exception.Message),
        };

    private enum RangeKind
    {
        None,
        Valid,
        Unsatisfiable,
    }

    private readonly record struct ParsedRange(
        RangeKind Kind,
        long Start,
        long End)
    {
        public static ParsedRange None => new(RangeKind.None, 0, 0);

        public static ParsedRange Unsatisfiable => new(RangeKind.Unsatisfiable, 0, 0);
    }
}

public sealed partial class Ctx
{
    /// <summary>
    /// Serves a stored object with metadata, conditional ETag handling, and byte
    /// ranges. Works with any <see cref="IStorage"/> provider.
    /// </summary>
    public Task<ReptarResponse> File(
        string key,
        StorageFileOptions? options = null) =>
        StorageFile.SendAsync(this, key, options);

    /// <summary>Serves a stored object as a download.</summary>
    public Task<ReptarResponse> Attachment(
        string key,
        string? fileName = null,
        StorageFileOptions? options = null)
    {
        var resolved = options ?? StorageFileOptions.Default;
        if (fileName is not null)
            resolved = resolved with { FileName = fileName };

        return StorageFile.SendAsync(
            this,
            key,
            resolved with
            {
                Download = true,
                Inline = false,
            });
    }
}

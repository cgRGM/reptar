using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;

namespace Reptar.Hosting;

/// <summary>
/// Retains ASP.NET Core's encoding negotiation and compression policy while
/// avoiding compressor setup for small, known-length responses. Unknown-length
/// streams retain the standard policy; SSE is excluded by the host's MIME list.
/// </summary>
public sealed class ReptarResponseCompressionProvider(
    IServiceProvider services,
    IOptions<ResponseCompressionOptions> options)
    : ResponseCompressionProvider(services, options)
{
    public const int MinimumBodyBytes = 1024;

    public override bool ShouldCompressResponse(HttpContext context)
    {
        if (context.Response.ContentLength is { } length && length < MinimumBodyBytes)
            return false;

        return base.ShouldCompressResponse(context);
    }
}

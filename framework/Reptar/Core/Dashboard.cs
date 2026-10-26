using System.Text;

namespace Reptar;

public sealed class DashboardOptions
{
    public string Path { get; set; } = "/_reptar";

    /// <summary>
    /// Include the Prometheus metrics text. The dashboard does not mount the
    /// metrics middleware itself.
    /// </summary>
    public bool IncludeMetrics { get; set; } = true;

    /// <summary>
    /// Allow non-loopback requests. Keep this false in development and add
    /// authentication middleware before enabling it in production.
    /// </summary>
    public bool AllowRemote { get; set; }

    /// <summary>Optional middleware (for example an admin-role guard).</summary>
    public ReptarMiddleware? Middleware { get; set; }
}

/// <summary>
/// A dependency-free developer dashboard: routes, registered tables, cache
/// state, and Prometheus metrics. It is local-only by default.
/// </summary>
public static class DashboardExtensions
{
    public static ReptarApp MapReptarDashboard(
        this ReptarApp app,
        DashboardOptions? options = null)
    {
        var opts = options ?? new DashboardOptions();

        Func<Ctx, Task<ReptarResponse>> handler = ctx =>
        {
            if (!opts.AllowRemote && !IsLocalRequest(ctx))
                return Task.FromResult(ctx.NotFound("Dashboard is local-only."));

            return Task.FromResult(ctx.Text(
                Render(app, opts),
                200,
                "text/html; charset=utf-8"));
        };

        if (opts.Middleware is null)
            app.Get(opts.Path, handler);
        else
            app.Get(opts.Path, opts.Middleware, handler);

        return app;
    }

    private static bool IsLocalRequest(Ctx ctx)
    {
        if (!string.IsNullOrEmpty(ctx.Req.Header("CF-Connecting-IP")))
            return false;

        var host = ctx.Req.Header("Host");

        if (string.IsNullOrWhiteSpace(host))
            return true;

        var hostname = host.Trim();

        if (hostname.StartsWith('['))
        {
            var end = hostname.IndexOf(']');
            if (end > 0)
                hostname = hostname[1..end];
        }
        else
        {
            var colon = hostname.LastIndexOf(':');
            if (colon > 0 && hostname.IndexOf(':') == colon)
                hostname = hostname[..colon];
        }

        hostname = hostname.ToLowerInvariant();

        return hostname is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0" ||
            hostname.EndsWith(".localhost", StringComparison.Ordinal);
    }

    private static string Render(ReptarApp app, DashboardOptions options)
    {
        var builder = new StringBuilder(8_192);
        builder.Append("""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Reptar</title>
              <style>
                :root { color-scheme: light dark; --muted: #6b7280; --line: #d1d5db; }
                body { font: 15px/1.5 ui-sans-serif, system-ui, sans-serif; margin: 0; padding: 2rem; }
                main { max-width: 1100px; margin: 0 auto; }
                h1 { margin: 0 0 .25rem; font-size: 1.6rem; }
                h2 { margin: 2rem 0 .75rem; font-size: 1.1rem; }
                p.sub { color: var(--muted); margin: 0 0 1.5rem; }
                table { border-collapse: collapse; width: 100%; font-size: .9rem; }
                th, td { text-align: left; padding: .45rem .6rem; border-bottom: 1px solid var(--line); vertical-align: top; }
                th { font-weight: 600; color: var(--muted); }
                code, pre { font: .85rem ui-monospace, SFMono-Regular, Menlo, monospace; }
                pre { padding: 1rem; overflow: auto; border: 1px solid var(--line); border-radius: .5rem; }
                .pill { display: inline-block; padding: .05rem .4rem; border: 1px solid var(--line); border-radius: 999px; font-size: .75rem; }
              </style>
            </head>
            <body><main>
            <h1>Reptar</h1>
            <p class="sub">Developer dashboard · local-only by default</p>
            """);

        builder.Append("<p><span class=\"pill\">")
            .Append(Escape(RuntimeLabel()))
            .Append("</span> <span class=\"pill\">uptime ")
            .Append(FormatUptime())
            .Append("</span> <span class=\"pill\">")
            .Append(app.RouteMetadata.Count)
            .Append(" routes</span> <span class=\"pill\">")
            .Append(app.Tables.Count)
            .Append(" tables</span> <span class=\"pill\">")
            .Append(ResponseCache.Count)
            .Append(" cached</span></p>");

        RenderRoutes(builder, app);
        RenderTables(builder, app);

        if (options.IncludeMetrics)
        {
            builder.Append("<h2>Metrics</h2><pre>")
                .Append(Escape(app.Metrics.Render()))
                .Append("</pre>");
        }

        builder.Append("</main></body></html>");
        return builder.ToString();
    }

    private static void RenderRoutes(StringBuilder builder, ReptarApp app)
    {
        builder.Append("<h2>Routes</h2><table><thead><tr>")
            .Append("<th>Method</th><th>Path</th><th>Input</th><th>Produces</th><th>Notes</th>")
            .Append("</tr></thead><tbody>");

        foreach (var route in app.RouteMetadata.OrderBy(
            route => route.Path,
            StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("<tr><td><code>")
                .Append(Escape(route.Method))
                .Append("</code></td><td><code>")
                .Append(Escape(route.Path))
                .Append("</code></td><td>")
                .Append(Escape(route.InputType?.Name ?? "—"))
                .Append("</td><td>")
                .Append(Escape(string.Join(
                    ", ",
                    route.Produces.Select(p => p.Type?.Name ?? p.Status.ToString()))))
                .Append("</td><td>");

            var notes = new List<string>();
            if (!string.IsNullOrEmpty(route.Summary))
                notes.Add(route.Summary);
            if (route.IsAiTool)
                notes.Add($"AI tool: {route.AiToolName ?? route.Name ?? route.Path}");
            if (route.Tags.Count > 0)
                notes.Add(string.Join(", ", route.Tags));

            builder.Append(Escape(string.Join(" · ", notes)))
                .Append("</td></tr>");
        }

        builder.Append("</tbody></table>");
    }

    private static void RenderTables(StringBuilder builder, ReptarApp app)
    {
        builder.Append("<h2>Tables</h2>");

        if (app.Tables.Count == 0)
        {
            builder.Append("<p>No tables registered.</p>");
            return;
        }

        foreach (var table in app.Tables.OrderBy(
            table => table.Name,
            StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("<h3><code>")
                .Append(Escape(table.Name))
                .Append("</code></h3><table><thead><tr>")
                .Append("<th>Column</th><th>Type</th><th>Flags</th><th>Default</th>")
                .Append("</tr></thead><tbody>");

            foreach (var column in table.Columns)
            {
                var flags = new List<string>();
                if (column.IsPrimaryKey)
                    flags.Add("primary key");
                if (column.IsNotNull)
                    flags.Add("not null");
                if (column.IsUnique)
                    flags.Add("unique");
                if (column.IsIndexed)
                    flags.Add("indexed");
                if (!string.IsNullOrEmpty(column.ReferencesTable))
                    flags.Add($"→ {column.ReferencesTable}.{column.ReferencesColumn}");

                builder.Append("<tr><td><code>")
                    .Append(Escape(column.SqlName))
                    .Append("</code></td><td><code>")
                    .Append(Escape(column.SqlType))
                    .Append("</code></td><td>")
                    .Append(Escape(string.Join(", ", flags)))
                    .Append("</td><td>")
                    .Append(Escape(column.DefaultSql ?? "—"))
                    .Append("</td></tr>");
            }

            builder.Append("</tbody></table>");
        }
    }

    private static string RuntimeLabel()
    {
#if NETWASM
        return "wasm";
#else
        return Environment.Version.ToString();
#endif
    }

    private static string FormatUptime()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return uptime.TotalDays >= 1
            ? $"{(int)uptime.TotalDays}d {uptime.Hours}h"
            : uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
                : $"{uptime.Minutes}m {uptime.Seconds}s";
    }

    private static string Escape(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}

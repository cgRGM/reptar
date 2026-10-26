// Semgrep-only regression fixture. Not compiled or shipped as application code.
class SecurityRuleFixtures
{
    void Check(Ctx ctx, Query query, Input input, ReptarApp app)
    {
        // ruleid: reptar-sqlfilter-raw-usage
        var raw = SqlFilter.Raw(input.Filter);
        // ok: reptar-sqlfilter-raw-usage
        var typed = Posts.Title.Eq("safe");

        // ruleid: reptar-string-orderby
        query.OrderBy(ctx.Req.QueryValue("sort"));
        // ruleid: reptar-string-orderby
        query.OrderBy("title " + input.Direction);
        // ok: reptar-string-orderby
        query.OrderBy(Posts.Title.Asc());

        // ruleid: reptar-redirect-from-request-data
        ctx.Redirect(ctx.Req.QueryValue("next"));
        // ok: reptar-redirect-from-request-data
        ctx.Redirect("/dashboard");

        // ruleid: reptar-cookie-value-from-request-data
        ctx.Cookies.Set("identity", ctx.Req.Header("X-Identity"));
        // ok: reptar-cookie-value-from-request-data
        ctx.Cookies.Set("preference", "compact");

        // ruleid: reptar-raw-html-from-request-data
        ctx.Html(input.Html);
        // ok: reptar-raw-html-from-request-data
        ctx.Html("<h1>Trusted constant</h1>");

        // ruleid: reptar-workspace-tools-mounted
        app.UseWorkspaceTools();
    }
}

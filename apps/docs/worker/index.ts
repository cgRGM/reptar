// Edge worker for the docs site, deployed in front of the static assets
// (wired from packages/infra/alchemy.run.ts — the docs StaticSite's `main`).
//
// Adds agent-friendly Markdown content negotiation:
//   - `Accept: text/markdown` at any page URL returns the page's `.md` twin
//     (emitted at build time by scripts/emit-markdown.ts) with Vary: Accept.
//   - Unknown paths requested as Markdown get a Markdown 404 body that
//     links the llms.txt index and sitemap.
//   - Every response carries Vary: Accept so HTML and Markdown stay in
//     separate cache entries.

interface AssetsFetcher {
  fetch: (input: Request | string, init?: RequestInit) => Promise<Response>;
}

interface Env {
  ASSETS: AssetsFetcher;
}

const SITE_ORIGIN = "https://reptar-docs.rocktown-labs.workers.dev";

const MARKDOWN_HEADERS: HeadersInit = {
  "content-type": "text/markdown; charset=utf-8",
  vary: "Accept",
};

const MARKDOWN_404 = `# 404 — page not found

The requested page does not exist in the Reptar documentation.

- Documentation index: ${SITE_ORIGIN}/llms.txt
- Full docs bundle: ${SITE_ORIGIN}/llms-full.txt
- Sitemap: ${SITE_ORIGIN}/sitemap-index.xml
`;

const acceptsMarkdown = (request: Request): boolean =>
  (request.headers.get("accept") ?? "").toLowerCase().includes("text/markdown");

const markdownHrefFor = (pathname: string, origin: string): string =>
  new URL(
    pathname === "/" ? "/index.md" : `${pathname.replace(/\/+$/u, "")}.md`,
    origin
  ).href;

export default {
  fetch: async (request: Request, env: Env): Promise<Response> => {
    if (acceptsMarkdown(request)) {
      const { origin, pathname } = new URL(request.url);
      const markdownResponse = await env.ASSETS.fetch(
        markdownHrefFor(pathname, origin)
      );

      if (markdownResponse.ok) {
        return new Response(await markdownResponse.text(), {
          headers: MARKDOWN_HEADERS,
          status: 200,
        });
      }

      return new Response(MARKDOWN_404, {
        headers: MARKDOWN_HEADERS,
        status: 404,
      });
    }

    const response = await env.ASSETS.fetch(request);
    const headers = new Headers(response.headers);
    headers.append("vary", "Accept");
    return new Response(response.body, {
      headers,
      status: response.status,
    });
  },
};

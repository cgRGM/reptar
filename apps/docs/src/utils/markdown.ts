// Shared Markdown extraction for the per-page "Copy/View as Markdown" actions.
// Works against the rendered DOM, so MDX components (Tabs, Cards, Code) and
// `?raw`-imported living-contract artifacts are fully captured.
// Also imported by scripts/verify-markdown.ts, which locks conversion quality
// in CI — keep both consumers in sync by editing only this file.
import type TurndownService from "turndown";

const STRIP_SELECTORS = [".copy-button-container", "a.sl-anchor-link"];

const textOf = (element: Element | null): string =>
  element?.textContent?.replaceAll(/\s+/gu, " ").trim() ?? "";

const createTurndownService = async (): Promise<TurndownService> => {
  const [{ default: TurndownService }, { gfm }] = await Promise.all([
    import("turndown"),
    import("turndown-plugin-gfm"),
  ]);

  const service = new TurndownService({
    bulletListMarker: "-",
    codeBlockStyle: "fenced",
    headingStyle: "atx",
    hr: "---",
  });
  service.use(gfm);

  // Expressive Code renders <pre data-language="…"> with one <div class="ec-line">
  // per source line; join them back into real newlines. Overrides the default
  // fencedCodeBlock rule.
  service.addRule("fencedCodeBlock", {
    filter: (node) => node.nodeName === "PRE",
    replacement: (_content, node) => {
      const language = node.dataset.language ?? "";
      const ecLines = node.querySelectorAll(".ec-line");
      const code =
        ecLines.length > 0
          ? [...ecLines]
              .map((line) => (line.textContent ?? "").replace(/\s+$/u, ""))
              .join("\n")
          : (node.textContent ?? "").replace(/\n+$/u, "");
      return `\n\n\`\`\`${language}\n${code}\n\`\`\`\n\n`;
    },
  });

  // Starlight asides become blockquotes: > **Note:** …
  service.addRule("starlightAside", {
    filter: (node) =>
      node.nodeName === "ASIDE" && node.classList.contains("starlight-aside"),
    replacement: (_content, node) => {
      const variantClass = [...node.classList].find((className) =>
        className.startsWith("starlight-aside--")
      );
      const variant = variantClass?.split("--")[1] ?? "note";
      const label = `${variant.charAt(0).toUpperCase()}${variant.slice(1)}`;
      const body = [...node.children]
        .filter((child) => !child.classList.contains("starlight-aside__title"))
        .map((child) => textOf(child))
        .filter(Boolean)
        .join(" ");
      return `\n\n> **${label}:** ${body}\n\n`;
    },
  });

  // Link cards become list entries: - [**Title**](href) — description
  service.addRule("starlightLinkCard", {
    filter: (node) =>
      node.nodeName === "A" && node.classList.contains("sl-link-card"),
    replacement: (_content, node) => {
      const title = textOf(node.querySelector(":scope > .title"));
      const body = [...node.children]
        .filter((child) => !child.classList.contains("title"))
        .map((child) => textOf(child))
        .filter(Boolean)
        .join(" ");
      const href = node.getAttribute("href") ?? "";
      return `\n\n- [**${title}**](${href})${body ? ` — ${body}` : ""}\n\n`;
    },
  });

  // Cards (CardGrid children) become bold list entries.
  service.addRule("starlightCard", {
    filter: (node) =>
      node.nodeName === "ARTICLE" && node.classList.contains("card"),
    replacement: (_content, node) => {
      const title = textOf(node.querySelector(":scope > .title"));
      const body = [...node.children]
        .filter((child) => !child.classList.contains("title"))
        .map((child) => textOf(child))
        .filter(Boolean)
        .join(" ");
      return `\n\n- **${title}**${body ? ` — ${body}` : ""}\n\n`;
    },
  });

  return service;
};

let servicePromise: Promise<TurndownService> | undefined;

// Memoized so the turndown chunk is loaded at most once per page view.
export const buildTurndownService = (): Promise<TurndownService> => {
  servicePromise ??= createTurndownService();
  return servicePromise;
};

export const extractMarkdown = async (source: HTMLElement): Promise<string> => {
  const service = await buildTurndownService();

  const clone = source.cloneNode(true) as HTMLElement;
  for (const selector of STRIP_SELECTORS) {
    for (const element of clone.querySelectorAll(selector)) {
      element.remove();
    }
  }

  const body = service.turndown(clone);
  const { title } = source.ownerDocument;
  return `# ${title}\n\n${body.trim()}\n`;
};

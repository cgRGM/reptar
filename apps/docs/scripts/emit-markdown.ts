// Post-build step: converts every built docs page into a sibling `.md` asset
// (dist/<path>.md) using the same extraction pipeline as the in-page
// Copy/View as Markdown buttons. The edge worker (apps/docs/worker/index.ts)
// serves these via `Accept: text/markdown` content negotiation.
// Runs automatically as part of `bun run build`.
import { readdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import path from "node:path";

import { Window } from "happy-dom";

import { extractMarkdown } from "../src/utils/markdown.ts";

const DIST_DIR = path.join(import.meta.dir, "../dist");

// Collects every <dir>/index.html under dist (each Starlight page).
const findPageFiles = (dir: string): string[] => {
  const pages: string[] = [];
  for (const entry of readdirSync(dir)) {
    const fullPath = path.join(dir, entry);
    if (statSync(fullPath).isDirectory()) {
      pages.push(...findPageFiles(fullPath));
      continue;
    }
    if (entry === "index.html") {
      pages.push(fullPath);
    }
  }
  return pages;
};

// dist/client/codegen/index.html → dist/client/codegen.md
// dist/index.html                → dist/index.md
const markdownPathFor = (pagePath: string): string => {
  const pageDir = path.join(pagePath, "..");
  return pageDir === DIST_DIR
    ? path.join(DIST_DIR, "index.md")
    : `${pageDir}.md`;
};

const pages = findPageFiles(DIST_DIR);
if (pages.length === 0) {
  console.error(
    `No built pages found under ${DIST_DIR} — run \`astro build\` first.`
  );
  process.exit(1);
}

const emitPage = async (pagePath: string): Promise<boolean> => {
  const window = new Window();
  window.document.write(readFileSync(pagePath, "utf-8"));
  const source = window.document.querySelector("main .sl-markdown-content");
  if (!source) {
    return false;
  }

  const markdown = await extractMarkdown(source);
  if (!markdown) {
    return false;
  }

  writeFileSync(markdownPathFor(pagePath), markdown, "utf-8");
  return true;
};

const results = await Promise.all(pages.map((page) => emitPage(page)));
const emitted = results.filter(Boolean).length;

console.log(
  `✓ Emitted ${emitted} markdown twins into ${path.relative(import.meta.dir, DIST_DIR)}/ (of ${pages.length} built pages)`
);

// CI gate for the per-page Markdown actions (Copy/View as Markdown).
// Converts built HTML pages through the same extraction pipeline the browser
// buttons use (src/utils/markdown.ts) and asserts quality markers, so a
// Starlight markup change that breaks conversion fails the build instead of
// silently shipping degraded output.
import { existsSync, readFileSync } from "node:fs";

import { Window } from "happy-dom";

import { extractMarkdown } from "../src/utils/markdown.ts";

interface Expectation {
  /** Path to a built HTML page, relative to apps/docs. */
  file: string;
  /** Fragments that must appear in the converted Markdown. */
  mustInclude: string[];
  /** Fragments that must not appear in the converted Markdown. */
  mustExclude: string[];
  /** What this page proves about the pipeline, used in failure messages. */
  proves: string;
}

const ANCHOR_NOISE = "Section titled";

const EXPECTATIONS: Expectation[] = [
  {
    file: "dist/client/codegen/index.html",
    mustExclude: [ANCHOR_NOISE],
    mustInclude: [
      "export type AppType = {",
      "$post: Endpoint<undefined, SessionUser | ErrorBody, 201 | 404>;",
      'app.UseScalar(title: "Reptar API", version: "1.0.0");',
    ],
    proves: "living-contract artifacts survive (?raw imports inside <Code>)",
  },
  {
    file: "dist/getting-started/quickstart/index.html",
    mustExclude: [ANCHOR_NOISE],
    mustInclude: [
      "> **Note:**",
      "git clone https://github.com/your-org/reptar.git   # or your fork\ncd reptar",
    ],
    proves: "asides become blockquotes and code keeps its newlines",
  },
  {
    file: "dist/getting-started/cli/index.html",
    mustExclude: [ANCHOR_NOISE],
    mustInclude: ["| `codegen` | `bun reptar codegen` |"],
    proves: "GFM tables survive",
  },
];

const convert = async (file: string): Promise<string> => {
  if (!existsSync(file)) {
    throw new Error(
      `Built page not found: ${file} — run \`bun run build\` first.`
    );
  }

  const window = new Window();
  window.document.write(readFileSync(file, "utf-8"));
  const source = window.document.querySelector("main .sl-markdown-content");
  if (!source) {
    throw new Error(`No markdown content root found in ${file}.`);
  }

  return await extractMarkdown(source);
};

const results = await Promise.all(
  EXPECTATIONS.map(async (expectation) => {
    const markdown = await convert(expectation.file);
    const failures: string[] = [];

    for (const fragment of expectation.mustInclude) {
      if (!markdown.includes(fragment)) {
        failures.push(
          `${expectation.file} (${expectation.proves}): missing ${JSON.stringify(fragment)}`
        );
      }
    }
    for (const fragment of expectation.mustExclude) {
      if (markdown.includes(fragment)) {
        failures.push(
          `${expectation.file} (${expectation.proves}): leaked ${JSON.stringify(fragment)}`
        );
      }
    }

    return failures;
  })
);

const failures = results.flat();

if (failures.length > 0) {
  console.error("Markdown conversion quality check failed:");
  for (const failure of failures) {
    console.error(`  - ${failure}`);
  }
  process.exit(1);
}

console.log(
  `Verified Markdown conversion across ${EXPECTATIONS.length} representative pages.`
);

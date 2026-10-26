// One-off generator for the docs site og:image (1200x630).
// Run: bun scripts/make-og-image.ts
// The output (public/og.png) is committed — regenerate only when branding changes.
import path from "node:path";

import sharp from "sharp";

const OUT_PATH = path.join(import.meta.dir, "../public/og.png");

const SANS = "Inter, -apple-system, Helvetica Neue, Arial, sans-serif";
const MONO = "JetBrains Mono, SF Mono, Menlo, monospace";

const CODE_LINES: [string, string][] = [
  ["var app = ", "ReptarApp"],
  ["  app.MapGet(", '"/hello"'],
  ["    ctx => ", "ctx.Json"],
  ["    new { hi = true }));", ""],
  ["app.", "UseScalar"],
];

const codeLinesSvg = CODE_LINES.map(([plain, accent], index) => {
  const y = 235 + index * 34;
  const escaped = plain.replaceAll("&", "&amp;").replaceAll("<", "&lt;");
  const accentSvg = accent
    ? `<tspan fill="#34d399">${accent
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")}</tspan>`
    : "";
  return `<text x="752" y="${y}" font-family="${MONO}" font-size="24" fill="#a1a1aa">${escaped}${accentSvg}</text>`;
}).join("\n  ");

const svg = `<svg width="1200" height="630" xmlns="http://www.w3.org/2000/svg">
  <defs>
    <linearGradient id="accent" x1="0" y1="0" x2="1" y2="0">
      <stop offset="0" stop-color="#10b981"/>
      <stop offset="1" stop-color="#34d399"/>
    </linearGradient>
  </defs>
  <rect width="1200" height="630" fill="#0a0b0e"/>
  <g stroke="#13161c" stroke-width="1">
    <line x1="0" y1="105" x2="1200" y2="105"/>
    <line x1="0" y1="210" x2="1200" y2="210"/>
    <line x1="0" y1="315" x2="1200" y2="315"/>
    <line x1="0" y1="420" x2="1200" y2="420"/>
    <line x1="0" y1="525" x2="1200" y2="525"/>
    <line x1="150" y1="0" x2="150" y2="630"/>
    <line x1="300" y1="0" x2="300" y2="630"/>
    <line x1="450" y1="0" x2="450" y2="630"/>
    <line x1="600" y1="0" x2="600" y2="630"/>
    <line x1="750" y1="0" x2="750" y2="630"/>
    <line x1="900" y1="0" x2="900" y2="630"/>
    <line x1="1050" y1="0" x2="1050" y2="630"/>
  </g>
  <text x="84" y="248" font-family="${SANS}" font-size="132" font-weight="800" fill="#f4f4f5">Reptar</text>
  <rect x="88" y="274" width="420" height="10" rx="5" fill="url(#accent)"/>
  <text x="86" y="345" font-family="${SANS}" font-size="36" fill="#a1a1aa">Single-binary C# 13 for the edge.</text>
  <text x="86" y="398" font-family="${SANS}" font-size="36" fill="#a1a1aa">Cloudflare Workers · AWS Lambda</text>
  <text x="86" y="568" font-family="${MONO}" font-size="26" fill="#71717a">github.com/RocktownLabs/reptar</text>
  <rect x="722" y="140" width="400" height="320" rx="14" fill="#0f1116" stroke="#222630" stroke-width="1.5"/>
  <circle cx="752" cy="172" r="7" fill="#222630"/>
  <circle cx="776" cy="172" r="7" fill="#222630"/>
  <circle cx="800" cy="172" r="7" fill="#222630"/>
  <line x1="722" y1="192" x2="1122" y2="192" stroke="#1b1f28" stroke-width="1"/>
  ${codeLinesSvg}
</svg>`;

await sharp(Buffer.from(svg)).png().toFile(OUT_PATH);
console.log(`✓ Generated ${OUT_PATH}`);

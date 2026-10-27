// @ts-check
import starlight from "@astrojs/starlight";
import { defineConfig } from "astro/config";
import starlightLlmsTxt from "starlight-llms-txt";

// Machine-readable identity for AI agents and social previews.
// Facts only — no fabricated contact or license claims.
const OG_IMAGE_URL = "https://reptar-docs.rocktownlabs.app/og.png";
const JSON_LD = JSON.stringify({
  "@context": "https://schema.org",
  "@type": "SoftwareApplication",
  applicationCategory: "DeveloperApplication",
  author: {
    "@type": "Organization",
    name: "Rocktown Labs",
    url: "https://github.com/RocktownLabs",
  },
  description:
    "Single-binary C# web framework targeting Native AOT and Cloudflare Workers (NetWasm), with source-generated JSON, typed RPC codegen, and built-in AI tooling.",
  isAccessibleForFree: true,
  name: "Reptar",
  operatingSystem: "Cloudflare Workers, AWS Lambda, Docker, Linux, macOS",
  programmingLanguage: "C#",
  sameAs: ["https://github.com/cgRGM/reptar"],
  softwareVersion: "0.1",
  url: "https://reptar-docs.rocktownlabs.app/",
});

// https://astro.build/config
export default defineConfig({
  integrations: [
    starlight({
      components: {
        PageTitle: "./src/components/page-title.astro",
      },
      customCss: ["./src/styles/custom.css"],
      description:
        "Reptar is a high-performance, single-binary C# 13 web framework for Native AOT and Cloudflare Workers (NetWasm), with zero runtime reflection, typed RPC codegen, and built-in AI tooling.",
      head: [
        {
          attrs: { content: OG_IMAGE_URL, property: "og:image" },
          tag: "meta",
        },
        {
          attrs: { type: "application/ld+json" },
          content: JSON_LD,
          tag: "script",
        },
      ],
      plugins: [
        starlightLlmsTxt({
          details:
            "Reach for Reptar when you need a single C# backend that deploys unchanged to Cloudflare Workers (WebAssembly), AWS Lambda (Native AOT), containers, or a VPS — with compile-time validated routes, a typed TypeScript RPC client generated from the C# route tree, and built-in auth, realtime rooms, and AI agent tooling. When working in a Reptar codebase, run `bun reptar inspect --json` for machine-readable project state, `bun reptar mcp` for a stdio MCP server (inspect, schema, routes, db plan, doctor), and read the generated contract in `packages/rpc/openapi.json`. To scaffold or deploy a Reptar app, start with the Quick Start and Build with an AI Agent pages.",
          optionalLinks: [
            {
              description:
                "Source repository for the framework, examples, and benchmarks.",
              label: "GitHub repository",
              url: "https://github.com/cgRGM/reptar",
            },
            {
              description:
                "Installable agent skill covering routes, TableDef schemas, migrations, and RPC codegen.",
              label: "Reptar agent skill (SKILL.md)",
              url: "https://github.com/cgRGM/reptar/blob/master/packages/rpc/skills/reptar/SKILL.md",
            },
          ],
        }),
      ],
      sidebar: [
        {
          items: [
            { label: "Documentation Home", slug: "index" },
            { label: "Why Reptar", slug: "overview/why-reptar" },
            { label: "The One Rule", slug: "overview/the-one-rule" },
            { label: "System Architecture", slug: "overview/architecture" },
            {
              label: "Deployment Targets",
              slug: "overview/deployment-targets",
            },
            { label: "RealWorld & Benchmarks", slug: "overview/realworld" },
            {
              label: "Verified Release Benchmarks",
              slug: "overview/release-benchmarks",
            },
            { label: "ReptarBase (BaaS Engine)", slug: "overview/baas" },
          ],
          label: "Overview",
        },
        {
          items: [
            { label: "Quick Start", slug: "getting-started/quickstart" },
            {
              label: "Build with an AI Agent",
              slug: "getting-started/agents",
            },
            {
              label: "Project Layout",
              slug: "getting-started/project-structure",
            },
            { label: "CLI & Tooling", slug: "getting-started/cli" },
          ],
          label: "Getting Started",
        },
        {
          items: [
            { label: "Routing, SSE & Slices", slug: "core/routing" },
            { label: "Built-in Middleware", slug: "core/middleware" },
            { label: "Context Surface (Ctx)", slug: "core/context" },
            { label: "Validation & Rule<T>", slug: "core/validation" },
            { label: "Testing & TestClient", slug: "core/testing" },
            {
              label: "Realtime Rooms & WebSockets",
              slug: "core/realtime-rooms",
            },
          ],
          label: "Core Concepts",
        },
        {
          items: [
            { label: "Schema Definition", slug: "data/schema" },
            { label: "Database Client (c.Db)", slug: "data/database" },
            { label: "Migrations Engine", slug: "data/migrations" },
          ],
          label: "Data & Storage",
        },
        {
          items: [
            { label: "Built-in C# Auth", slug: "auth/builtin" },
            { label: "Better Auth & Adapters", slug: "auth/adapters" },
          ],
          label: "Authentication",
        },
        {
          items: [
            { label: "D1, R2, AI & Bindings", slug: "cloudflare/bindings" },
            {
              label: "Queues & Cron Triggers",
              slug: "cloudflare/queues-and-cron",
            },
          ],
          label: "Cloudflare Bindings",
        },
        {
          items: [{ label: "Payments (Stripe)", slug: "features/stripe" }],
          label: "Features & Add-ons",
        },
        {
          items: [
            {
              label: "Fullstack React SPA with Reptar",
              slug: "cookbook/fullstack-react-spa",
            },
            {
              label: "AI Workspace Agent & In-Process Tools",
              slug: "cookbook/ai-agent-tools",
            },
            {
              label: "Multiplayer Rooms (Cloudflare DO)",
              slug: "cookbook/multiplayer-rooms",
            },
            {
              label: "Stripe Webhooks & Idempotency",
              slug: "cookbook/stripe-webhooks",
            },
            {
              label: "D1 Persistence & Migrations",
              slug: "cookbook/d1-migrations",
            },
            {
              label: "Background Tasks (WaitUntil)",
              slug: "cookbook/background-tasks",
            },
            {
              label: "File Uploads & R2 Storage",
              slug: "cookbook/file-uploads",
            },
            {
              label: "Resource Policies & Authorization",
              slug: "cookbook/authorization",
            },
            { label: "Realtime SSE Streams", slug: "cookbook/realtime-sse" },
            {
              label: "Keyset Pagination (ColumnRef)",
              slug: "cookbook/keyset-pagination",
            },
            {
              label: "Structured LLM Outputs (Rule<T>)",
              slug: "cookbook/structured-llm-outputs",
            },
            {
              label: "Human-in-the-Loop Agents",
              slug: "cookbook/human-in-the-loop",
            },
            {
              label: "Autonomous Agent Loops",
              slug: "cookbook/autonomous-agent-loop",
            },
            {
              label: "Agent & Prompt Evals",
              slug: "cookbook/agent-evals",
            },
            {
              label: "Tenant Isolation & Safe History",
              slug: "cookbook/tenant-isolation",
            },
          ],
          label: "Cookbook",
        },
        {
          items: [
            { label: "AI Tools & Agent Harness", slug: "ai/agent-harness" },
            { label: "Agent Runtime (loop, sessions, SDK)", slug: "ai/agent-runtime" },
            { label: "AI Gateway (Proxy & Failover)", slug: "ai/ai-gateway" },
          ],
          label: "AI & Agents",
        },
        {
          items: [
            { label: "Typed Client & OpenAPI 3.1", slug: "client/codegen" },
          ],
          label: "Client & OpenAPI",
        },
        {
          items: [{ autogenerate: { directory: "api" } }],
          label: "API Reference",
        },
        {
          items: [
            { label: "Telemetry & Logs", slug: "observability/telemetry" },
          ],
          label: "Observability",
        },
        {
          items: [
            { label: "About", slug: "about" },
            { label: "Contact", slug: "contact" },
            { label: "Privacy", slug: "privacy" },
          ],
          label: "Project",
        },
      ],
      social: [
        {
          href: "https://github.com/cgRGM/reptar",
          icon: "github",
          label: "GitHub",
        },
      ],
      title: "reptar docs",
    }),
  ],
  server: {
    port: 4322,
  },
  site: "https://reptar-docs.rocktownlabs.app",
  vite: {
    // Keep native module loading relative to satteri's own optional packages.
    // Bundling its loader into prerender chunks breaks Linux binding lookup.
    ssr: { external: ["satteri"] },
  },
});

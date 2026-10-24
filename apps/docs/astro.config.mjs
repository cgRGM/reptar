// @ts-check
import starlight from "@astrojs/starlight";
import { defineConfig } from "astro/config";

// https://astro.build/config
export default defineConfig({
  integrations: [
    starlight({
      customCss: ["./src/styles/custom.css"],
      sidebar: [
        {
          items: [
            { label: "Documentation Home", slug: "index" },
            { label: "The One Rule", slug: "overview/the-one-rule" },
            { label: "System Architecture", slug: "overview/architecture" },
            {
              label: "Deployment Targets",
              slug: "overview/deployment-targets",
            },
          ],
          label: "Overview",
        },
        {
          items: [
            { label: "Quick Start", slug: "getting-started/quickstart" },
            {
              label: "Project Layout",
              slug: "getting-started/project-structure",
            },
          ],
          label: "Getting Started",
        },
        {
          items: [
            { label: "Routing & Handlers", slug: "core/routing" },
            { label: "Context Surface (Ctx)", slug: "core/context" },
            { label: "Validation & Rule<T>", slug: "core/validation" },
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
            { label: "Adapters", slug: "auth/adapters" },
          ],
          label: "Authentication",
        },
        {
          items: [
            { label: "D1, R2, AI & Limits", slug: "cloudflare/bindings" },
          ],
          label: "Cloudflare Bindings",
        },
        {
          items: [{ label: "Payments (Stripe)", slug: "features/stripe" }],
          label: "Features & Add-ons",
        },
        {
          items: [
            { label: "Typed Client & Contracts", slug: "client/codegen" },
          ],
          label: "Client Codegen",
        },
        {
          items: [
            { label: "Telemetry & Logs", slug: "observability/telemetry" },
          ],
          label: "Observability",
        },
      ],
      social: [
        {
          href: "https://github.com/RocktownLabs/reptar",
          icon: "github",
          label: "GitHub",
        },
      ],
      title: "Reptar Documentation",
    }),
  ],
  server: {
    port: 4322,
  },
});

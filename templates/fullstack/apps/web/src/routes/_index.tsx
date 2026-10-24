import { Button } from "@{{PROJECT_NAME}}/ui";
import { ArrowRight, Code2, Cpu, ShieldCheck, Zap } from "lucide-react";
import type * as React from "react";
import { useNavigate } from "react-router";

import { useSession } from "../lib/auth-client";

export const IndexRoute = (): React.JSX.Element => {
  const { data: session } = useSession();
  const navigate = useNavigate();

  return (
    <div className="relative isolate px-6 pt-14 lg:px-8">
      <div className="mx-auto max-w-3xl py-20 text-center sm:py-28">
        <div className="mb-8 flex justify-center">
          <div className="text-muted-foreground ring-border hover:ring-foreground/20 relative rounded-full px-3 py-1 text-sm leading-6 ring-1">
            Powered by Reptar C# &amp; Better Auth{" "}
            <span className="text-primary font-semibold">v0.1.0</span>
          </div>
        </div>
        <h1 className="text-foreground text-4xl font-bold tracking-tight sm:text-6xl">
          High-performance C# backend. Modern React frontend.
        </h1>
        <p className="text-muted-foreground mt-6 text-lg leading-8">
          A fullstack starter built for speed: Native AOT C# backend running on
          Cloudflare Workers or bare metal, end-to-end typed RPC, and complete
          Better Auth authentication.
        </p>
        <div className="mt-10 flex items-center justify-center gap-x-6">
          <Button
            size="lg"
            onClick={() => navigate(session?.user ? "/dashboard" : "/login")}
          >
            {session?.user ? "Go to Dashboard" : "Get Started"}
            <ArrowRight className="ml-2 size-4" />
          </Button>
          <a
            href="https://github.com/RocktownLabs/reptar"
            target="_blank"
            rel="noopener noreferrer"
            className="text-foreground hover:text-primary text-sm leading-6 font-semibold transition-colors"
          >
            Learn more <span aria-hidden="true">→</span>
          </a>
        </div>
      </div>

      <div className="mx-auto max-w-5xl py-12">
        <div className="grid grid-cols-1 gap-6 sm:grid-cols-2 lg:grid-cols-4">
          <div className="border-border bg-card rounded-xl border p-6 shadow-sm">
            <Cpu className="text-primary mb-3 size-8" />
            <h3 className="text-lg font-semibold">Native AOT C#</h3>
            <p className="text-muted-foreground mt-2 text-sm">
              Sub-millisecond cold starts and ultra-lean &lt; 30 MB RSS memory
              footprints.
            </p>
          </div>
          <div className="border-border bg-card rounded-xl border p-6 shadow-sm">
            <ShieldCheck className="text-primary mb-3 size-8" />
            <h3 className="text-lg font-semibold">Better Auth</h3>
            <p className="text-muted-foreground mt-2 text-sm">
              Standard wire protocol with credentials and pure C# Google &amp;
              GitHub OAuth.
            </p>
          </div>
          <div className="border-border bg-card rounded-xl border p-6 shadow-sm">
            <Code2 className="text-primary mb-3 size-8" />
            <h3 className="text-lg font-semibold">End-to-End Typed</h3>
            <p className="text-muted-foreground mt-2 text-sm">
              Compile-time C# route trees automatically generate type-safe
              TypeScript clients.
            </p>
          </div>
          <div className="border-border bg-card rounded-xl border p-6 shadow-sm">
            <Zap className="text-primary mb-3 size-8" />
            <h3 className="text-lg font-semibold">Edge Ready</h3>
            <p className="text-muted-foreground mt-2 text-sm">
              Deploy to Cloudflare Workers with D1 or run in ultra-lean Docker
              chiseled containers.
            </p>
          </div>
        </div>
      </div>
    </div>
  );
};

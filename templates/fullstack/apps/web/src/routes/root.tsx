import type * as React from "react";
import { Outlet } from "react-router";

import { Header } from "../components/header";

export const RootLayout = (): React.JSX.Element => (
  <div className="bg-background text-foreground flex min-h-screen flex-col">
    <Header />
    <main className="flex-1">
      <Outlet />
    </main>
    <footer className="border-border text-muted-foreground border-t py-6 text-center text-xs">
      Powered by <span className="text-foreground font-semibold">Reptar</span>{" "}
      &amp; <span className="text-foreground font-semibold">Better Auth</span>
    </footer>
  </div>
);

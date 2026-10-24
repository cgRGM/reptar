import type * as React from "react";
import { Link } from "react-router";

import { UserMenu } from "./user-menu";

export const Header = (): React.JSX.Element => (
  <header className="border-border bg-background/95 supports-[backdrop-filter]:bg-background/60 sticky top-0 z-40 border-b backdrop-blur">
    <div className="mx-auto flex h-14 max-w-7xl items-center justify-between px-4 sm:px-6">
      <div className="flex items-center gap-6">
        <Link
          to="/"
          className="flex items-center gap-2 font-bold tracking-tight"
        >
          <span className="text-xl">🦖</span>
          <span>{{ PROJECT_TITLE }}</span>
        </Link>
        <nav className="text-muted-foreground flex items-center gap-4 text-sm font-medium">
          <Link to="/" className="hover:text-foreground transition-colors">
            Home
          </Link>
          <Link
            to="/dashboard"
            className="hover:text-foreground transition-colors"
          >
            Dashboard
          </Link>
        </nav>
      </div>
      <div className="flex items-center gap-2">
        <UserMenu />
      </div>
    </div>
  </header>
);

import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@{{PROJECT_NAME}}/ui";
import { LogOut, User as UserIcon } from "lucide-react";
import type * as React from "react";
import { useNavigate } from "react-router";

import { authClient, useSession } from "../lib/auth-client";

export const UserMenu = (): React.JSX.Element => {
  const { data: session } = useSession();
  const navigate = useNavigate();

  if (!session?.user) {
    return (
      <Button variant="default" size="sm" onClick={() => navigate("/login")}>
        Sign In
      </Button>
    );
  }

  const { user } = session;
  const initial = user.name ? user.name.charAt(0).toUpperCase() : "U";

  const handleSignOut = async () => {
    await authClient.signOut();
    navigate("/");
  };

  return (
    <DropdownMenu>
      <DropdownMenuTrigger className="border-border hover:bg-accent flex items-center gap-2 rounded-full border p-1 focus:outline-none">
        {user.image ? (
          <img
            src={user.image}
            alt={user.name ?? "User"}
            className="size-7 rounded-full object-cover"
          />
        ) : (
          <div className="bg-primary text-primary-foreground flex size-7 items-center justify-center rounded-full text-xs font-semibold">
            {initial}
          </div>
        )}
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-56">
        <div className="flex flex-col space-y-1 p-2">
          <p className="text-sm leading-none font-medium">{user.name}</p>
          <p className="text-muted-foreground text-xs leading-none">
            {user.email}
          </p>
        </div>
        <DropdownMenuSeparator />
        <DropdownMenuItem onClick={() => navigate("/dashboard")}>
          <UserIcon className="mr-2 size-4" />
          <span>Dashboard</span>
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuItem onClick={handleSignOut} className="text-destructive">
          <LogOut className="mr-2 size-4" />
          <span>Sign Out</span>
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
};

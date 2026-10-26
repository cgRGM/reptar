import type { Item } from "@{{PROJECT_NAME}}/client";
import {
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  Input,
  Label,
  Skeleton,
} from "@{{PROJECT_NAME}}/ui";
import { Plus, Trash2 } from "lucide-react";
import type * as React from "react";
import { useEffect, useState } from "react";
import { useNavigate } from "react-router";

import { useSession } from "../lib/auth-client";
import { client } from "../lib/client";

export const DashboardRoute = (): React.JSX.Element => {
  const { data: session, isPending } = useSession();
  const navigate = useNavigate();

  const [items, setItems] = useState<Item[]>([]);
  const [loadingItems, setLoadingItems] = useState(true);
  const [title, setTitle] = useState("");
  const [content, setContent] = useState("");
  const [creating, setCreating] = useState(false);

  useEffect(() => {
    if (!isPending && !session?.user) {
      navigate("/login");
    }
  }, [session, isPending, navigate]);

  const loadItems = async () => {
    try {
      setLoadingItems(true);
      const res = await client.api.items.$get();
      if (res.ok) {
        const data = await res.json();
        setItems(data);
      }
    } catch {
      // Fallback
    } finally {
      setLoadingItems(false);
    }
  };

  useEffect(() => {
    if (session?.user) {
      loadItems();
    }
  }, [session?.user]);

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!title || !content) {
      return;
    }

    setCreating(true);
    try {
      const res = await client.api.items.$post({
        json: { content, title },
      });
      if (res.ok) {
        setTitle("");
        setContent("");
        await loadItems();
      }
    } finally {
      setCreating(false);
    }
  };

  const handleDelete = async (id: string) => {
    try {
      const res = await fetch(`/api/items/${id}`, {
        credentials: "include",
        method: "DELETE",
      });
      if (res.ok) {
        setItems((prev) => prev.filter((item) => item.id !== id));
      }
    } catch {
      // Error handling
    }
  };

  if (isPending) {
    return (
      <div className="mx-auto max-w-4xl space-y-4 p-8">
        <Skeleton className="h-10 w-48" />
        <Skeleton className="h-32 w-full" />
      </div>
    );
  }

  const renderItemsList = (): React.JSX.Element => {
    if (loadingItems) {
      return (
        <div className="space-y-2">
          <Skeleton className="h-16 w-full" />
          <Skeleton className="h-16 w-full" />
        </div>
      );
    }

    if (items.length === 0) {
      return (
        <p className="text-muted-foreground text-sm">
          No items created yet. Create one above!
        </p>
      );
    }

    return (
      <div className="grid gap-3">
        {items.map((item) => (
          <div
            key={item.id}
            className="border-border bg-card flex items-center justify-between rounded-lg border p-4 shadow-sm"
          >
            <div>
              <h3 className="text-foreground font-semibold">{item.title}</h3>
              <p className="text-muted-foreground text-sm">{item.content}</p>
              <p className="text-muted-foreground/60 mt-1 text-xs">
                {new Date(item.createdAt).toLocaleString()}
              </p>
            </div>
            {item.userId === session?.user.id ? (
              <Button
                variant="ghost"
                size="icon"
                onClick={() => handleDelete(item.id)}
                className="text-destructive hover:text-destructive"
              >
                <Trash2 className="size-4" />
              </Button>
            ) : null}
          </div>
        ))}
      </div>
    );
  };

  return (
    <div className="mx-auto max-w-4xl space-y-8 p-6 sm:p-8">
      <div className="flex flex-col gap-2">
        <h1 className="text-3xl font-bold tracking-tight">Dashboard</h1>
        <p className="text-muted-foreground">
          Welcome back,{" "}
          <span className="text-foreground font-semibold">
            {session?.user.name}
          </span>{" "}
          ({session?.user.email}).
        </p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Create New Item</CardTitle>
          <CardDescription>
            Add an item to the database using the type-safe C# API.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleCreate} className="space-y-4">
            <div className="space-y-2">
              <Label htmlFor="item-title">Title</Label>
              <Input
                id="item-title"
                placeholder="Item title"
                value={title}
                onChange={(e) => setTitle(e.target.value)}
                required
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="item-content">Content</Label>
              <Input
                id="item-content"
                placeholder="Description or notes"
                value={content}
                onChange={(e) => setContent(e.target.value)}
                required
              />
            </div>
            <Button
              type="submit"
              disabled={creating}
              className="flex items-center gap-2"
            >
              <Plus className="size-4" />
              {creating ? "Creating..." : "Create Item"}
            </Button>
          </form>
        </CardContent>
      </Card>

      <div className="space-y-4">
        <h2 className="text-xl font-bold tracking-tight">Your Items</h2>
        {renderItemsList()}
      </div>
    </div>
  );
};

import { createClient } from "@reptar/client";

import type { AppType } from "./app-type";

const api = createClient<AppType>("http://localhost:8787");

const main = async () => {
  const posts = await api.posts.$get();
  if (posts.ok) {
    const rows = await posts.json();
    console.log(rows[0]?.title);
  }

  const post = await api.posts[":id"].$get({ param: { id: "abc" } });
  if (post.status === 404) {
    const error = await post.json();
    if ("message" in error) {
      console.log(error.message);
    }
  }

  const created = await api.posts.$post({
    json: { content: "World", title: "Hello" },
  });
  if (created.ok) {
    const row = await created.json();
    console.log(row.slug);
  }

  const session = await api.auth.session.$get();
  if (session.ok) {
    const { user } = await session.json();
    console.log(user?.email);
  }

  // @ts-expect-error - title is required
  await api.posts.$post({ json: { content: "missing title" } });
};

void main();

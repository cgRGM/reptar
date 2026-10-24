const baseUrl = process.env.REALWORLD_URL ?? "http://127.0.0.1:8787";
const count = Number(process.env.REALWORLD_SEED_COUNT ?? "300");
const uid = Date.now().toString(36);
const password = "bench-password-123";

interface UserResponse {
  user: { token: string; username: string };
}

interface ArticleResponse {
  article: { slug: string };
}

const postJson = async <T>(
  path: string,
  body: unknown,
  token?: string
): Promise<T> => {
  const response = await fetch(`${baseUrl}${path}`, {
    body: JSON.stringify(body),
    headers: {
      "content-type": "application/json",
      ...(token ? { authorization: `Token ${token}` } : {}),
    },
    method: "POST",
  });

  if (!response.ok) {
    throw new Error(`${path} -> ${response.status}: ${await response.text()}`);
  }

  return (await response.json()) as T;
};

const author = await postJson<UserResponse>("/api/users", {
  user: {
    email: `bench_${uid}@test.com`,
    password,
    username: `bench_${uid}`,
  },
});

const follower = await postJson<UserResponse>("/api/users", {
  user: {
    email: `feed_${uid}@test.com`,
    password,
    username: `feed_${uid}`,
  },
});

let firstSlug = "";

const seedArticle = async (index: number): Promise<void> => {
  const created = await postJson<ArticleResponse>(
    "/api/articles",
    {
      article: {
        body: `Benchmark body ${index}. ${"lorem ipsum ".repeat(20)}`,
        description: `Benchmark article ${index}`,
        tagList: ["bench", `bucket-${index % 10}`],
        title: `Benchmark article ${index} ${uid}`,
      },
    },
    author.user.token
  );

  if (index === 0) {
    firstSlug = created.article.slug;
  }
};

for (let start = 0; start < count; start += 20) {
  const batch = Array.from(
    { length: Math.min(20, count - start) },
    (_, offset) => start + offset
  );

  // eslint-disable-next-line no-await-in-loop
  await Promise.all(batch.map(seedArticle));
}

await postJson(
  `/api/profiles/${author.user.username}/follow`,
  {},
  follower.user.token
);

const result = {
  authorToken: author.user.token,
  authorUsername: author.user.username,
  firstSlug,
  followerToken: follower.user.token,
  followerUsername: follower.user.username,
  password,
  tag: "bench",
};

await Bun.write(
  process.env.REALWORLD_SEED_OUT ?? "/tmp/realworld-bench.json",
  JSON.stringify(result, null, 2)
);

console.log(JSON.stringify(result, null, 2));

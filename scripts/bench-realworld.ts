import { spawnSync } from "node:child_process";

interface Seed {
  authorToken: string;
  firstSlug: string;
  followerToken: string;
  tag: string;
}

const baseUrl = process.env.REALWORLD_URL ?? "http://127.0.0.1:8787";
const seedPath = process.env.REALWORLD_SEED ?? "/tmp/realworld-bench.json";
const connections = process.env.REALWORLD_BENCH_CONNECTIONS ?? "100";
const duration = process.env.REALWORLD_BENCH_DURATION ?? "5";
const seed = (await Bun.file(seedPath).json()) as Seed;

interface Scenario {
  body?: string;
  headers?: string[];
  method?: string;
  name: string;
  path: string;
}

const scenarios: Scenario[] = [
  { name: "tags", path: "/api/tags" },
  { name: "articles", path: "/api/articles?limit=20" },
  {
    name: "articles-by-tag",
    path: `/api/articles?tag=${encodeURIComponent(seed.tag)}&limit=20`,
  },
  { name: "single-article", path: `/api/articles/${seed.firstSlug}` },
  {
    headers: [`Authorization: Token ${seed.followerToken}`],
    name: "feed",
    path: "/api/articles/feed?limit=20",
  },
];

const run = (scenario: Scenario): void => {
  console.log(`\n=== ${scenario.name} ===`);
  const args = ["x", "autocannon", "-c", connections, "-d", duration];

  if (scenario.method) {
    args.push("-m", scenario.method);
  }
  if (scenario.body) {
    args.push("-b", scenario.body);
  }
  for (const header of scenario.headers ?? []) {
    args.push("-H", header);
  }
  args.push(`${baseUrl}${scenario.path}`);

  const result = spawnSync("bun", args, { stdio: "inherit" });
  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
};

for (const scenario of scenarios) {
  run(scenario);
}

const writeBody = JSON.stringify({
  article: {
    body: "benchmark body",
    description: "benchmark",
    tagList: [seed.tag],
    title: "Benchmark write",
  },
});

run({
  body: writeBody,
  headers: [
    "content-type: application/json",
    `Authorization: Token ${seed.authorToken}`,
  ],
  method: "POST",
  name: "create-article",
  path: "/api/articles",
});

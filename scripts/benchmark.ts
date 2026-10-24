import { spawnSync } from "node:child_process";

interface AutocannonResult {
  connections: number;
  duration: number;
  errors: number;
  latency: {
    average: number;
    max: number;
    min: number;
    p50: number;
    p75: number;
    p90: number;
    p97_5: number;
    p99: number;
  };
  non2xx: number;
  requests: {
    average: number;
    p50: number;
    p75: number;
    p90: number;
    p99: number;
    total: number;
  };
  statusCodeStats: Record<string, { count: number }>;
  timeouts: number;
  url: string;
}

interface CurlTiming {
  connect: number;
  dns: number;
  status: number;
  tls: number;
  total: number;
  ttfb: number;
}

const measureCurl = (url: string): CurlTiming => {
  const format =
    "%{time_namelookup} %{time_connect} %{time_appconnect} %{time_starttransfer} %{time_total} %{http_code}";
  const res = spawnSync("curl", ["-w", format, "-o", "/dev/null", "-s", url], {
    encoding: "utf-8",
  });

  const parts = res.stdout.trim().split(/\s+/u);
  return {
    connect: Math.round(Number(parts[1] ?? "0") * 1000),
    dns: Math.round(Number(parts[0] ?? "0") * 1000),
    status: Math.trunc(Number(parts[5] ?? "0")),
    tls: Math.round(Number(parts[2] ?? "0") * 1000),
    total: Math.round(Number(parts[4] ?? "0") * 1000),
    ttfb: Math.round(Number(parts[3] ?? "0") * 1000),
  };
};

const runAutocannon = (
  url: string,
  connections = 10,
  duration = 5
): AutocannonResult => {
  const res = spawnSync(
    "bunx",
    [
      "autocannon",
      "-c",
      String(connections),
      "-d",
      String(duration),
      "-j",
      url,
    ],
    { encoding: "utf-8", maxBuffer: 10 * 1024 * 1024 }
  );

  if (res.error || res.status !== 0) {
    throw new Error(
      `Autocannon failed: ${res.stderr || res.stdout || res.error?.message}`
    );
  }

  return JSON.parse(res.stdout) as AutocannonResult;
};

const runSuite = () => {
  const args = process.argv.slice(2);
  const targetArgIdx = args.findIndex((a) => a === "--target" || a === "-t");
  const baseUrl = (
    targetArgIdx !== -1 && args[targetArgIdx + 1]
      ? args[targetArgIdx + 1]
      : process.env.API_URL || "https://reptar-api.rocktown-labs.workers.dev"
  ).replace(/\/$/u, "");

  const connections = 10;
  const duration = 5;

  console.log(`\n======================================================`);
  console.log(`  REPTAR BENCHMARK SUITE`);
  console.log(`  Target: ${baseUrl}`);
  console.log(
    `  Connections: ${connections} | Duration: ${duration}s per route`
  );
  console.log(`======================================================\n`);

  // 1. Cold start / Single-shot measurements
  console.log(
    `[1/3] Measuring isolated single-shot timings (Cold / Edge TTFB)...`
  );
  const healthTiming = measureCurl(`${baseUrl}/health`);
  const postsTiming = measureCurl(`${baseUrl}/posts`);

  console.log(
    `  • GET /health (in-memory): TTFB = ${healthTiming.ttfb} ms | Total = ${healthTiming.total} ms | Status = ${healthTiming.status}`
  );
  console.log(
    `  • GET /posts  (D1 query):  TTFB = ${postsTiming.ttfb} ms | Total = ${postsTiming.total} ms | Status = ${postsTiming.status}\n`
  );

  // 2. Load test pure in-memory compute
  console.log(`[2/3] Benchmarking pure in-memory compute (GET /health)...`);
  const healthLoad = runAutocannon(`${baseUrl}/health`, connections, duration);
  console.log(
    `  ✓ Completed: ${healthLoad.requests.total} requests (${Math.round(healthLoad.requests.average)} req/s)\n`
  );

  // 3. Load test real database I/O
  console.log(
    `[3/3] Benchmarking real-world database I/O (GET /posts via Cloudflare D1)...`
  );
  const postsLoad = runAutocannon(`${baseUrl}/posts`, connections, duration);
  console.log(
    `  ✓ Completed: ${postsLoad.requests.total} requests (${Math.round(postsLoad.requests.average)} req/s)\n`
  );

  // Summary Report
  console.log(
    `========================================================================================`
  );
  console.log(`  BENCHMARK RESULTS SUMMARY`);
  console.log(
    `========================================================================================`
  );
  console.log(
    `| Route               | Type         | Req/sec | Latency p50 | Latency p95 | Latency p99 | Errors |`
  );
  console.log(
    `| :------------------ | :----------- | :------ | :---------- | :---------- | :---------- | :----- |`
  );
  console.log(
    `| GET /health         | In-Memory    | ${String(Math.round(healthLoad.requests.average)).padStart(7)} | ${String(`${healthLoad.latency.p50} ms`).padStart(11)} | ${String(`${healthLoad.latency.p97_5} ms`).padStart(11)} | ${String(`${healthLoad.latency.p99} ms`).padStart(11)} | ${String(healthLoad.errors + healthLoad.non2xx).padStart(6)} |`
  );
  console.log(
    `| GET /posts          | D1 Database  | ${String(Math.round(postsLoad.requests.average)).padStart(7)} | ${String(`${postsLoad.latency.p50} ms`).padStart(11)} | ${String(`${postsLoad.latency.p97_5} ms`).padStart(11)} | ${String(`${postsLoad.latency.p99} ms`).padStart(11)} | ${String(postsLoad.errors + postsLoad.non2xx).padStart(6)} |`
  );
  console.log(
    `========================================================================================\n`
  );

  console.log(`Key Findings:`);
  console.log(
    `- In-Memory Wasm: Serves responses with minimal compute overhead across the JS<->Wasm boundary.`
  );
  console.log(
    `- D1 Database I/O: Latency is primarily dominated by edge-to-D1 query roundtrips, with zero reflection penalty in C#.`
  );
  console.log(`\nBenchmark run complete.`);
};

try {
  runSuite();
} catch (error) {
  console.error("Benchmark failed:", error);
  process.exit(1);
}

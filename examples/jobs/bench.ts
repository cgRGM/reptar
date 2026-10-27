/**
 * Queue throughput bench against a running jobs app (start it first — see
 * examples/jobs/README.md). Enqueues N jobs, waits for the consumer pipeline
 * to drain them, and reports enqueue and drain rates.
 *
 *   REPTAR_JOBS_URL=http://127.0.0.1:8787 bun examples/jobs/bench.ts 2000
 */
const base = process.env.REPTAR_JOBS_URL ?? "http://127.0.0.1:8787";
const count = Number(process.argv[2] ?? "1000");

console.log(`reptar queue bench: ${count} jobs against ${base}`);

const stats = async (): Promise<{ processed: number; mode: string }> => {
  const response = await fetch(`${base}/stats`, {
    signal: AbortSignal.timeout(10_000),
  });
  if (!response.ok) {
    throw new Error(`stats failed: HTTP ${response.status}`);
  }
  return (await response.json()) as { processed: number; mode: string };
};

const enqueue = async (n: number): Promise<number> => {
  const response = await fetch(`${base}/enqueue?n=${n}`, {
    method: "POST",
    signal: AbortSignal.timeout(120_000),
  });
  if (!response.ok) {
    throw new Error(`enqueue failed: HTTP ${response.status}`);
  }
  const { queued } = (await response.json()) as { queued: number };
  return queued;
};

const { mode } = await stats();

// Fresh baseline.
const { processed: baseline } = await stats();
await enqueue(1);
await Bun.sleep(mode === "sync" ? 100 : 4000);

const enqueuedAt = performance.now();
const queued = await enqueue(count);
const enqueueMs = performance.now() - enqueuedAt;

const drainStartedAt = performance.now();
let processed = 0;
for (let attempt = 0; attempt < 600; attempt += 1) {
  await Bun.sleep(500); // eslint-disable-line no-await-in-loop -- polling loop
  const current = await stats(); // eslint-disable-line no-await-in-loop
  processed = current.processed - baseline - 1;
  if (processed >= queued) {
    break;
  }
}
const drainMs = performance.now() - drainStartedAt;

console.log(
  `enqueue: ${queued} in ${enqueueMs.toFixed(0)}ms ` +
    `(${Math.round((queued / Math.max(enqueueMs, 1)) * 1000)}/s)`
);
console.log(
  `drain:    ${processed} in ${drainMs.toFixed(0)}ms ` +
    `(${Math.round((processed / Math.max(drainMs, 1)) * 1000)}/s)`
);
if (processed < queued) {
  console.log(`warning: only ${processed} processed of ${queued}`);
  process.exit(1);
}

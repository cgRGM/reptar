/**
 * Queue throughput bench: enqueues N jobs through the configured adapter
 * (REPTAR_QUEUE=sync|db|sqs|azure-storage|pubsub), then waits for the
 * consumer pipeline to drain them, reporting enqueue and drain rates.
 *
 *   bun examples/jobs/bench.ts 1000          # default sync adapter
 *   REPTAR_QUEUE=db bun examples/jobs/bench.ts 5000
 */
import { $ } from "bun";

const count = Number(process.argv[2] ?? "1000");
const mode = process.env.REPTAR_QUEUE ?? "sync";
const port = process.env.PORT ?? "8791";
const base = `http://localhost:${port}`;

console.log(`reptar queue bench: ${count} jobs, adapter=${mode}`);

const proc = Bun.spawn(["dotnet", "run", "app.cs", "--no-launch-profile"], {
  cwd: import.meta.dir,
  env: { ...process.env, PORT: port, REPTAR_QUEUE: mode },
  stdout: "ignore",
  stderr: "ignore",
});

const cleanup = (): void => {
  proc.kill();
  process.exit(0);
};
process.on("SIGINT", cleanup);

try {
  // Wait for the app to come up.
  let up = false;
  for (let attempt = 0; attempt < 90 && !up; attempt++) {
    await Bun.sleep(1000);
    try {
      const response = await fetch(`${base}/stats`);
      up = response.ok;
    } catch {
      up = false;
    }
  }
  if (!up) {
    throw new Error("app did not start");
  }

  // Fresh baseline.
  await fetch(`${base}/enqueue?n=1`);
  await Bun.sleep(mode === "sync" ? 100 : 4000);

  const enqueuedAt = performance.now();
  const response = await fetch(`${base}/enqueue?n=${count}`);
  const { queued } = (await response.json()) as { queued: number };
  const enqueueMs = performance.now() - enqueuedAt;

  const drainStartedAt = performance.now();
  let processed = 0;
  for (let attempt = 0; attempt < 600; attempt++) {
    await Bun.sleep(500);
    const stats = (await (await fetch(`${base}/stats`)).json()) as {
      processed: number;
    };
    processed = stats.processed;
    if (processed >= queued) {
      break;
    }
  }
  const drainMs = performance.now() - drainStartedAt;

  const drainCount = Math.max(processed - 1, 0);
  console.log(
    `enqueue: ${queued} in ${enqueueMs.toFixed(0)}ms ` +
      `(${Math.round((queued / enqueueMs) * 1000)}/s)`
  );
  console.log(
    `drain:    ${drainCount} in ${drainMs.toFixed(0)}ms ` +
      `(${Math.round((drainCount / Math.max(drainMs, 1)) * 1000)}/s)`
  );
  if (drainCount < queued) {
    console.log(`warning: only ${processed} processed of ${queued}`);
  }
} finally {
  proc.kill();
}

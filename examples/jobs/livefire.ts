/**
 * Live-fire harness: runs a full enqueue → retry → process cycle for every
 * cloud queue adapter whose credentials are present, against real
 * infrastructure. No infrastructure is created — each provider expects an
 * existing queue/topic/subscription (see the setup notes per provider).
 *
 *   bun examples/jobs/livefire.ts
 *   bun examples/jobs/livefire.ts sqs            # one provider
 *
 * Credentials are read from the environment (the same variables the
 * adapters use). Providers without credentials are reported MISSING with
 * the exact variables to set — never silently skipped.
 *
 * SQS:          AWS_REGION, AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY,
 *               [AWS_SESSION_TOKEN], SQS_QUEUE_URL
 *               (create: aws sqs create-queue --queue-name reptar-livefire)
 * Azure:        AZURE_STORAGE_ACCOUNT, AZURE_STORAGE_KEY
 *               (create queue "reptarlivefire" in the storage account)
 * Pub/Sub:      GOOGLE_CLOUD_PROJECT, PUBSUB_TOPIC, PUBSUB_SUBSCRIPTION,
 *               GOOGLE_OAUTH_ACCESS_TOKEN (gcloud auth print-access-token)
 *               (create: gcloud pubsub topics create T &&
 *                        gcloud pubsub subscriptions create S --topic=T)
 */
const providers = process.argv.slice(2);
const selected = providers.length > 0 ? providers : ["sqs", "azure", "pubsub"];
const jobs = 3;
const port = process.env.PORT ?? "8841";
const base = `http://127.0.0.1:${port}`;

interface ProviderResult {
  name: string;
  status: "PASS" | "FAIL" | "MISSING";
  detail: string;
}

const { env } = process;

const checkEnv = (names: string[]): string | null => {
  for (const name of names) {
    if (!env[name]) {
      return name;
    }
  }
  return null;
};

const runProvider = async (
  mode: string,
  timeoutSeconds: number
): Promise<ProviderResult> => {
  const app = Bun.spawn(["dotnet", "run", "app.cs", "--no-launch-profile"], {
    cwd: import.meta.dir,
    env: {
      ...env,
      HOST: "127.0.0.1",
      PORT: port,
      REPTAR_JOBS_FAIL_FIRST: "1",
      REPTAR_QUEUE: mode,
      REPTAR_QUEUE_POLL_SECS: "1",
    },
    stderr: "inherit",
    stdout: "inherit",
  });

  const killApp = (): void => {
    app.kill();
    const listeners = Bun.spawnSync(["lsof", "-ti", `tcp:${port}`])
      .stdout.toString()
      .trim();
    for (const pid of listeners.split("\n")) {
      const parsed = Number.parseInt(pid, 10);
      if (!Number.isNaN(parsed)) {
        try {
          process.kill(parsed, "SIGKILL");
        } catch {
          // Already gone.
        }
      }
    }
  };

  try {
    // Wait for the app.
    let up = false;
    for (let attempt = 0; attempt < 60 && !up; attempt += 1) {
      await Bun.sleep(1000); // eslint-disable-line no-await-in-loop -- polling loop
      try {
        const response = await fetch(`${base}/stats`, { // eslint-disable-line no-await-in-loop -- polling loop
          signal: AbortSignal.timeout(3000),
        });
        up = response.ok;
      } catch {
        up = false;
      }
    }
    if (!up) {
      return {
        detail: "app did not start (check credentials and queue config)",
        name: mode,
        status: "FAIL",
      };
    }

    const enqueuedAt = Date.now();
    await fetch(`${base}/enqueue?n=${jobs}`, {
      method: "POST",
      signal: AbortSignal.timeout(60_000),
    });

    // Poll until every job survives the first-delivery failure and lands
    // as processed. RetryIn(2) + provider backoff can stretch the cycle.
    let processed = 0;
    for (let attempt = 0; attempt < timeoutSeconds; attempt += 1) {
      await Bun.sleep(1000); // eslint-disable-line no-await-in-loop -- polling loop
      const response = await fetch(`${base}/stats`, { // eslint-disable-line no-await-in-loop -- polling loop
        signal: AbortSignal.timeout(5000),
      });
      const { processed: nowProcessed } = (await response.json()) as { // eslint-disable-line no-await-in-loop
        processed: number;
      };
      processed = nowProcessed;
      if (processed >= jobs) {
        break;
      }
    }

    const seconds = (Date.now() - enqueuedAt) / 1000;
    return processed >= jobs
      ? {
          detail: `${jobs} jobs enqueued, retried once, and processed in ${seconds.toFixed(0)}s`,
          name: mode,
          status: "PASS",
        }
      : {
          detail: `only ${processed}/${jobs} processed after ${timeoutSeconds}s — check provider credentials, queue policy, and consumer config`,
          name: mode,
          status: "FAIL",
        };
  } finally {
    killApp();
  }
};

const results: ProviderResult[] = [];

for (const provider of selected) {
  if (provider === "sqs") {
    const missing = checkEnv([
      "AWS_REGION",
      "AWS_ACCESS_KEY_ID",
      "AWS_SECRET_ACCESS_KEY",
      "SQS_QUEUE_URL",
    ]);
    results.push(
      // eslint-disable-line no-await-in-loop -- sequential providers by design
      missing
        ? {
            detail: `set ${missing} (aws sso login or static keys, then aws sqs create-queue --queue-name reptar-livefire and export SQS_QUEUE_URL)`,
            name: "sqs",
            status: "MISSING",
          }
        : await runProvider("sqs", 120) // eslint-disable-line no-await-in-loop -- sequential by design
    );
  } else if (provider === "azure") {
    const missing = checkEnv(["AZURE_STORAGE_ACCOUNT", "AZURE_STORAGE_KEY"]);
    results.push(
      // eslint-disable-line no-await-in-loop -- sequential providers by design
      missing
        ? {
            detail: `set ${missing} (az login + az storage account keys list), and create a queue named "reptarlivefire"`,
            name: "azure",
            status: "MISSING",
          }
        : await runProvider("azure-storage", 120) // eslint-disable-line no-await-in-loop -- sequential by design
    );
  } else if (provider === "pubsub") {
    const missing = checkEnv([
      "GOOGLE_CLOUD_PROJECT",
      "PUBSUB_TOPIC",
      "PUBSUB_SUBSCRIPTION",
      "GOOGLE_OAUTH_ACCESS_TOKEN",
    ]);
    results.push(
      // eslint-disable-line no-await-in-loop -- sequential providers by design
      missing
        ? {
            detail: `set ${missing} (gcloud auth login, then gcloud auth print-access-token)`,
            name: "pubsub",
            status: "MISSING",
          }
        : await runProvider("pubsub", 120) // eslint-disable-line no-await-in-loop -- sequential by design
    );
  } else {
    results.push({
      detail: "unknown provider; expected sqs | azure | pubsub",
      name: provider,
      status: "MISSING",
    });
  }
}

console.log("\nreptar queue live-fire results");
console.log("──────────────────────────────────────────");
for (const result of results) {
  let icon = "❌";
  if (result.status === "PASS") {
    icon = "✅";
  } else if (result.status === "MISSING") {
    icon = "⏭️ ";
  }
  console.log(
    `${icon} ${result.status.padEnd(7)} ${result.name}: ${result.detail}`
  );
}

const failed = results.some((r) => r.status === "FAIL");
process.exit(failed ? 1 : 0);

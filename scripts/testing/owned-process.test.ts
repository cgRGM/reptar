import { expect, test } from "bun:test";
import { once } from "node:events";

import { runRealtimeSmoke } from "../test-realtime";
import { assertPortAvailable, spawnOwnedProcess } from "./owned-process";

test("occupied-port preflight leaves an unrelated listener serving", async () => {
  const sentinel = Bun.serve({
    fetch: () => new Response("unrelated-service"),
    hostname: "127.0.0.1",
    port: 0,
  });
  try {
    await expect(assertPortAvailable(sentinel.port)).rejects.toThrow(
      "refusing to disturb its owner"
    );
    const response = await fetch(sentinel.url);
    expect(await response.text()).toBe("unrelated-service");
  } finally {
    sentinel.stop(true);
  }
});

test("realtime runner rejects an occupied port before starting hosts or deleting state", async () => {
  const sentinel = Bun.serve({
    fetch: () => new Response("not-reptar"),
    hostname: "127.0.0.1",
    port: 0,
  });
  const previous = process.env.REPTAR_REALTIME_KESTREL_PORT;
  process.env.REPTAR_REALTIME_KESTREL_PORT = String(sentinel.port);
  try {
    await expect(runRealtimeSmoke({ kestrelOnly: true })).rejects.toThrow(
      "unavailable"
    );
    const response = await fetch(sentinel.url);
    expect(await response.text()).toBe("not-reptar");
  } finally {
    if (previous === undefined) {
      delete process.env.REPTAR_REALTIME_KESTREL_PORT;
    } else {
      process.env.REPTAR_REALTIME_KESTREL_PORT = previous;
    }
    sentinel.stop(true);
  }
});

test("owned cleanup is idempotent and leaves an unrelated listener intact", async () => {
  const sentinel = Bun.serve({
    fetch: () => new Response("still-running"),
    hostname: "127.0.0.1",
    port: 0,
  });
  const owned = await spawnOwnedProcess(
    process.execPath,
    ["-e", "setInterval(() => {}, 1000)"],
    { stdio: "ignore" }
  );
  try {
    await Promise.all([owned.stop(), owned.stop()]);
    expect(
      owned.child.exitCode !== null || owned.child.signalCode !== null
    ).toBe(true);
    const response = await fetch(sentinel.url);
    expect(await response.text()).toBe("still-running");
  } finally {
    await owned.stop();
    sentinel.stop(true);
  }
});

test("cleanup handles a child that already exited", async () => {
  const owned = await spawnOwnedProcess(
    process.execPath,
    ["-e", "setTimeout(() => process.exit(0), 50)"],
    { stdio: "ignore" }
  );
  await once(owned.child, "close");
  await owned.stop();
  expect(owned.child.exitCode).toBe(0);
});

test("owned cleanup escalates when its child ignores graceful termination", async () => {
  const owned = await spawnOwnedProcess(
    process.execPath,
    [
      "-e",
      'process.on("SIGTERM", () => {}); console.log("ready"); setInterval(() => {}, 1000)',
    ],
    { stdio: ["ignore", "pipe", "ignore"] }
  );
  try {
    const output = owned.child.stdout;
    if (!output) {
      throw new Error("Test child did not expose its readiness stream.");
    }
    await once(output, "data");
    await owned.stop();
    expect(owned.child.signalCode).toBe("SIGKILL");
  } finally {
    await owned.stop();
  }
}, 10_000);

test("failed executable startup reports the failure", async () => {
  await expect(
    spawnOwnedProcess("/reptar-nonexistent-test-binary", [], {
      stdio: "ignore",
    })
  ).rejects.toThrow();
});

test("invalid ports and conflicting host filters are rejected", async () => {
  await expect(assertPortAvailable(0)).rejects.toThrow("Invalid test port");
  await expect(assertPortAvailable(65_536)).rejects.toThrow(
    "Invalid test port"
  );
  await expect(
    runRealtimeSmoke({ kestrelOnly: true, workersOnly: true })
  ).rejects.toThrow("at most one");
});

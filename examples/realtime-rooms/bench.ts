#!/usr/bin/env bun
/**
 * Reptar Rooms — WebSocket concurrency benchmark.
 *
 * Simulates thousands of concurrent clients across N rooms and measures:
 *   1. Connection ramp-up time (and connections/second)
 *   2. Memory per socket (server RSS delta + client RSS)
 *   3. Broadcast fanout latency (one sender -> 49 peers, p50/p95/p99)
 *   4. Presence storm throughput (ephemeral cursor traffic)
 *
 * Usage:
 *   bun bench.ts                                # 5,000 clients / 100 rooms
 *   bun bench.ts --clients 20000                # scale up
 *   bun bench.ts --clients 10000 --rooms 100 --peers 50
 *
 * Needs a high file-descriptor limit for large runs:
 *   ulimit -n 65536
 */

type Frame =
  | { type: "connected"; connectionId: string; room: string }
  | { type: "joined"; room: string; connectionId: string; peers: unknown[] }
  | { type: "peer_joined"; peer: unknown }
  | { type: "peer_left"; connectionId: string; userId: string }
  | {
      type: "presence";
      connectionId: string;
      userId: string;
      x: number;
      y: number;
      typing: boolean;
    }
  | {
      type: "message";
      id: string;
      room: string;
      connectionId?: string;
      senderId: string;
      sentAt: string;
      payload: unknown;
    }
  | { type: "bye"; connectionId: string }
  | { type: "pong"; t: number }
  | { type: "error"; error: string; message: string };

interface Client {
  socket: WebSocket;
  room: string;
  id: number;
  joined: boolean;
  connectionId?: string;
  /** messageId -> send timestamp (fanout receipts) */
  pending: Map<string, number>;
  /** fanout latencies (ms) received by this client */
  receipts: number[];
  closed: boolean;
}

// ---------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------

const args = process.argv.slice(2);

const argValue = (name: string, fallback: number): number => {
  const index = args.indexOf(`--${name}`);
  if (index === -1) {
    return fallback;
  }
  return Math.trunc(Number(args[index + 1] ?? "")) || fallback;
};

const totalClients = argValue("clients", 5000);
const roomCount = argValue("rooms", 100);
const fanoutPeers = Math.max(2, argValue("peers", 50));
const host = process.env.BENCH_HOST ?? "http://127.0.0.1:8787";
const wsUrl = process.env.BENCH_WS_URL ?? "ws://127.0.0.1:8787";
const rampBatchSize = argValue("ramp", 500);

const roomOf = (index: number): string =>
  `bench-room-${(index % roomCount).toString().padStart(4, "0")}`;

const percentile = (sortedValues: number[], p: number): number => {
  if (sortedValues.length === 0) {
    return Number.NaN;
  }
  const rank = (p / 100) * (sortedValues.length - 1);
  const low = Math.floor(rank);
  const high = Math.ceil(rank);
  const lower = sortedValues[low] ?? 0;
  const upper = sortedValues[high] ?? 0;
  if (low === high) {
    return lower;
  }
  return lower + (upper - lower) * (rank - low);
};

const fmt = (value: number, digits = 1): string =>
  Number.isFinite(value) ? value.toFixed(digits) : "n/a";

// ---------------------------------------------------------------------------
// Phase 1 — connect + join ramp
// ---------------------------------------------------------------------------

const connectOne = (index: number, clients: Client[]): Promise<void> =>
  // Resolves from either the "joined" frame or a socket error — a promise
  // bridge is the idiomatic way to unify two callback sources.
  // eslint-disable-next-line promise/avoid-new
  new Promise((resolve) => {
    const room = roomOf(index);
    const socket = new WebSocket(`${wsUrl}/rooms/${room}`);

    const client: Client = {
      closed: false,
      id: index,
      joined: false,
      pending: new Map(),
      receipts: [],
      room,
      socket,
    };
    clients[index] = client;

    socket.addEventListener("message", (event: MessageEvent) => {
      const frame = JSON.parse(String(event.data)) as Frame;

      switch (frame.type) {
        case "connected": {
          client.connectionId = frame.connectionId;
          socket.send(
            JSON.stringify({
              name: `Bench ${index}`,
              type: "join",
              userId: `bench-user-${index}`,
            })
          );
          break;
        }
        case "joined": {
          client.joined = true;
          resolve();
          break;
        }
        case "message": {
          // The bench's probe id travels inside the payload; the server
          // assigns its own top-level message id.
          const probeId = (frame.payload as { id?: string } | undefined)?.id;
          const sentAt =
            probeId === undefined ? undefined : client.pending.get(probeId);
          if (probeId !== undefined && sentAt !== undefined) {
            client.receipts.push(performance.now() - sentAt);
            client.pending.delete(probeId);
          }
          break;
        }
        case "error": {
          console.error(`  [client ${index}] error frame: ${frame.error}`);
          break;
        }
        default: {
          break;
        }
      }
    });

    socket.addEventListener("close", () => {
      client.closed = true;
    });

    socket.addEventListener("error", () => {
      resolve();
    });
  });

const connectRamp = async (
  clients: Client[],
  onProgress: (connected: number) => void
): Promise<{ rampMs: number; connected: number }> => {
  const rampStartedAt = performance.now();
  let launched = 0;

  const launchBatch = (batchSize: number) => {
    for (let i = 0; i < batchSize && launched < clients.length; i += 1) {
      void connectOne(launched, clients);
      launched += 1;
      onProgress(launched);
    }
  };

  let settled = 0;
  const settleWaiter = async (deadline: number): Promise<number> => {
    while (performance.now() < deadline) {
      settled = clients.filter((c) => c && c.joined).length;
      if (settled >= clients.length) {
        return settled;
      }
      // Polling joins; the sleep paces the poll.
      // eslint-disable-next-line no-await-in-loop
      await Bun.sleep(100);
    }
    return clients.filter((c) => c && c.joined).length;
  };

  const joinDeadline = performance.now() + 120_000;
  const settlePromise = settleWaiter(joinDeadline);

  while (launched < clients.length) {
    launchBatch(rampBatchSize);
    // Give the server room to breathe between batches so the connect path
    // (not the SYN backlog) is what we measure. Sequential pacing is the
    // point here, hence the deliberate await-in-loop.
    // eslint-disable-next-line no-await-in-loop
    await Bun.sleep(50);
  }

  const connected = await settlePromise;

  return { connected, rampMs: performance.now() - rampStartedAt };
};

// ---------------------------------------------------------------------------
// Phase 2 — broadcast fanout latency
// ---------------------------------------------------------------------------

interface FanoutResult {
  measured: boolean;
  latencies: number[];
  messages: number;
  droppedReceipts: number;
}

const measureFanout = async (
  clients: Client[],
  roomsPerSlice: number
): Promise<FanoutResult> => {
  const latencies: number[] = [];
  let messages = 0;
  let droppedReceipts = 0;

  // Pick one sender per sampled room (up to 20 rooms); recipients are the
  // fanoutPeers peers that joined the same room.
  const senders: Client[] = [];
  const roomStride = Math.max(1, Math.floor(roomsPerSlice / 20));
  for (
    let found = 0, room = 0;
    room < roomCount && found < 20;
    room += roomStride
  ) {
    const sender = clients.find(
      (c) =>
        c &&
        c.room === roomOf(room) &&
        c.joined &&
        c.socket.readyState === WebSocket.OPEN
    );
    if (sender) {
      senders.push(sender);
      found += 1;
    }
  }

  if (senders.length === 0) {
    return { droppedReceipts, latencies, measured: false, messages };
  }

  // Register every joined client to expect receipts of messages sent by the
  // sender in its room (server fans out to ALL peers including the sender).
  const markPending = (messageId: string, room: string) => {
    for (const client of clients) {
      if (client && client.room === room && client.joined) {
        client.pending.set(messageId, performance.now());
      }
    }
  };

  for (const sender of senders) {
    for (let burst = 0; burst < 5; burst += 1) {
      const messageId = `bench-fanout-${sender.id}-${burst}`;
      markPending(messageId, sender.room);
      sender.socket.send(
        JSON.stringify({
          payload: { burst, id: messageId, text: "fanout latency probe" },
          type: "broadcast",
        })
      );
      messages += 1;
      // Space bursts out so each fanout wave is measured independently.
      // eslint-disable-next-line no-await-in-loop
      await Bun.sleep(10);
    }
  }

  // Wait for receipts to drain (or a 15s cap).
  const drainDeadline = performance.now() + 15_000;
  while (performance.now() < drainDeadline) {
    const outstanding = clients.reduce(
      (sum, c) => sum + (c ? c.pending.size : 0),
      0
    );
    if (outstanding === 0) {
      break;
    }
    // Polling is inherent to drain detection; the sleep paces the loop.
    // eslint-disable-next-line no-await-in-loop
    await Bun.sleep(100);
  }

  for (const client of clients) {
    latencies.push(...client.receipts);
    droppedReceipts += client.pending.size;
    client.receipts.length = 0;
    client.pending.clear();
  }

  return { droppedReceipts, latencies, measured: true, messages };
};

// ---------------------------------------------------------------------------
// Phase 3 — presence storm throughput
// ---------------------------------------------------------------------------

const presenceStorm = async (clients: Client[], durationMs: number) => {
  const stormStartedAt = performance.now();
  let sent = 0;

  const openClients = clients.filter(
    (c) => c && c.joined && c.socket.readyState === WebSocket.OPEN
  );

  const stopAt = stormStartedAt + durationMs;
  while (performance.now() < stopAt) {
    for (const client of openClients) {
      client.socket.send(
        JSON.stringify({
          type: "presence",
          typing: Math.random() > 0.7,
          x: Math.random() * 1920,
          y: Math.random() * 1080,
        })
      );
      sent += 1;
    }
    // ~20 presence updates/sec/client; pacing is the point of the loop.
    // eslint-disable-next-line no-await-in-loop
    await Bun.sleep(50);
  }

  // Let the last frames land before snapshotting server counters.
  await Bun.sleep(500);
  const stormMs = performance.now() - stormStartedAt;
  return { perSecond: sent / (stormMs / 1000), sent };
};

// ---------------------------------------------------------------------------
// Server snapshots
// ---------------------------------------------------------------------------

interface ServerStats {
  rooms?: number;
  activeConnections?: number;
  messagesRelayed?: number;
  presenceUpdatesRelayed?: number;
  messagesPersisted?: number;
  persisterQueueDepth?: number;
  joins?: number;
  leaves?: number;
  process?: {
    peakWorkingSetMb?: number;
    workingSetMb?: number;
    threads?: number;
  } | null;
}

const serverStats = async (): Promise<ServerStats | null> => {
  try {
    const response = await fetch(`${host}/stats`);
    return (await response.json()) as ServerStats;
  } catch {
    return null;
  }
};

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

console.log("Reptar Rooms — WebSocket concurrency benchmark");
console.log(
  `  target:        ${wsUrl}/rooms/{roomId} (${totalClients} clients / ${roomCount} rooms / ~${fanoutPeers} peers per room)`
);

try {
  const fdCheck = await Bun.spawnSync(["sh", "-c", "ulimit -n -H"]);
  const fdHardLimit = fdCheck.text().trim();
  console.log(`  fd hard limit: ${fdHardLimit}`);
} catch {
  // Non-POSIX platform; the limit is advisory anyway.
}

const initialStats = await serverStats();
const initialRss = initialStats?.process?.peakWorkingSetMb ?? null;

const clients: Client[] = [];
// Pre-size the sparse array so connectRamp's length checks see every slot.
clients.length = totalClients;
const { rampMs, connected } = await connectRamp(clients, (connectedSoFar) => {
  if (connectedSoFar % 1000 === 0) {
    process.stdout.write(`  connected ${connectedSoFar}/${totalClients}\r`);
  }
});

console.log();

console.log("\n[1] Connection ramp-up");
console.log(
  `  ramp time:            ${fmt(rampMs / 1000, 2)}s (${fmt(connected / (rampMs / 1000), 0)} sockets/s)`
);
console.log(`  joined:               ${connected}/${totalClients}`);

const clientRssBefore = process.memoryUsage.rss() / (1024 * 1024);

console.log("\n[2] Broadcast fanout latency (1 sender -> N peers per room)");
const fanout = await measureFanout(clients, roomCount);
const sortedLatencies = fanout.latencies.toSorted((a, b) => a - b);
if (fanout.measured) {
  console.log(
    `  receipts:             ${fanout.latencies.length} across ${fanout.messages} messages`
  );
  console.log(
    `  p50:                  ${fmt(percentile(sortedLatencies, 50), 2)} ms`
  );
  console.log(
    `  p95:                  ${fmt(percentile(sortedLatencies, 95), 2)} ms`
  );
  console.log(
    `  p99:                  ${fmt(percentile(sortedLatencies, 99), 2)} ms`
  );
  console.log(
    `  max:                  ${fmt(sortedLatencies.at(-1) ?? Number.NaN, 2)} ms`
  );
  console.log(`  dropped receipts:    ${fanout.droppedReceipts}`);
} else {
  console.log("  no sender available — skipped");
}

console.log("\n[3] Presence storm (ephemeral cursor traffic)");
const storm = await presenceStorm(clients, 5000);
console.log(
  `  sent:                 ${storm.sent} presence frames (${fmt(storm.perSecond, 0)}/s)`
);

console.log("\n[4] Memory & persistence");
const finalStats = await serverStats();
const peakRss = finalStats?.process?.peakWorkingSetMb ?? null;
const rssNow = finalStats?.process?.workingSetMb ?? null;
const deltaRss =
  initialRss !== null && rssNow !== null ? rssNow - initialRss : null;

console.log(`  server peak RSS:      ${fmt(peakRss, 1)} MB`);
console.log(
  `  server RSS delta:     ${fmt(deltaRss, 1)} MB (${fmt((deltaRss ?? 0) / connected, 2)} MB/socket over ${connected} sockets)`
);
console.log(`  client (Bun) RSS:     ${fmt(clientRssBefore, 1)} MB`);

// Let the write-behind persister drain so the persisted counter is meaningful.
await Bun.sleep(2000);
const persistedStats = await serverStats();
console.log(
  `  messages persisted:   ${persistedStats?.messagesPersisted ?? 0} (queue depth ${persistedStats?.persisterQueueDepth ?? 0})`
);
console.log(`  messages relayed:     ${persistedStats?.messagesRelayed ?? 0}`);
console.log(
  `  presence relayed:     ${persistedStats?.presenceUpdatesRelayed ?? 0}`
);

console.log("\n[5] Teardown");
const closeStartedAt = performance.now();
for (const client of clients) {
  if (client && client.socket.readyState === WebSocket.OPEN) {
    client.socket.send(JSON.stringify({ type: "leave" }));
  }
}
await Bun.sleep(1000);
const closedCount = clients.filter((c) => c && c.closed).length;
for (const client of clients) {
  client?.socket.close();
}
await Bun.sleep(300);
console.log(
  `  closed:               ${closedCount}/${totalClients} in ${fmt(performance.now() - closeStartedAt, 0)} ms`
);

// Final report as JSON for CI capture.
console.log("\n--- bench-result ---");
console.log(
  JSON.stringify(
    {
      clients: totalClients,
      connected,
      fanout: {
        dropped: fanout.droppedReceipts,
        measured: fanout.measured,
        p50Ms: Number(percentile(sortedLatencies, 50).toFixed(2)),
        p95Ms: Number(percentile(sortedLatencies, 95).toFixed(2)),
        p99Ms: Number(percentile(sortedLatencies, 99).toFixed(2)),
        receipts: fanout.latencies.length,
      },
      presence: {
        perSecond: Number(storm.perSecond.toFixed(0)),
        sent: storm.sent,
      },
      rampMs: Number(rampMs.toFixed(0)),
      rooms: roomCount,
      server: {
        messagesPersisted: persistedStats?.messagesPersisted ?? 0,
        peakRssMb: peakRss,
        rssDeltaMb: deltaRss === null ? null : Number(deltaRss.toFixed(1)),
        threads: finalStats?.process?.threads ?? null,
      },
      socketsPerSecond: Number((connected / (rampMs / 1000)).toFixed(0)),
    },
    null,
    2
  )
);
process.exit(0);

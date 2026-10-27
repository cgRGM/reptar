#!/usr/bin/env bun
/**
 * Protocol smoke test for Reptar Rooms: two clients join the same room,
 * exchange presence + broadcast frames, then leave. Exits non-zero on any
 * protocol violation. Not a load test — see bench.ts for that.
 */

const wsUrl = process.env.BENCH_WS_URL ?? "ws://127.0.0.1:8787";
const host = process.env.BENCH_HOST ?? "http://127.0.0.1:8787";
const room = `smoke-${Math.random().toString(36).slice(2, 8)}`;

type Frame = Record<string, unknown>;

const clients: {
  socket: WebSocket;
  inbox: Frame[];
  waiters: ((frame: Frame) => void)[];
}[] = [];

const connect = (name: string) =>
  // Promise wrapper bridges open/error callbacks; awaited by the caller.
  // eslint-disable-next-line promise/avoid-new
  new Promise<number>((resolve, reject) => {
    const socket = new WebSocket(`${wsUrl}/rooms/${room}`);
    const index = clients.length;
    clients.push({ inbox: [], socket, waiters: [] });

    socket.addEventListener("message", (event: MessageEvent) => {
      const frame = JSON.parse(String(event.data)) as Frame;
      const client = clients[index];
      if (client === undefined) {
        return;
      }
      const waiter = client.waiters.shift();
      if (waiter) {
        waiter(frame);
      } else {
        client.inbox.push(frame);
      }
    });

    socket.addEventListener("error", () => {
      reject(new Error(`socket error for ${name}`));
    });

    // If the server never completes the upgrade we'd otherwise hang forever.
    setTimeout(() => {
      reject(new Error(`connect timeout for ${name} (${wsUrl})`));
    }, 8000);

    socket.addEventListener("open", () => {
      resolve(index);
    });
  });

const send = (index: number, frame: unknown) => {
  const client = clients[index];
  if (!client) {
    throw new Error(`no client ${index}`);
  }
  client.socket.send(JSON.stringify(frame));
};

const nextFrame = (
  index: number,
  type: string,
  timeoutMs = 5000
): Promise<Frame> => {
  const client = clients[index];
  if (!client) {
    throw new Error(`no client ${index}`);
  }
  const queuedIndex = client.inbox.findIndex((frame) => frame.type === type);
  if (queuedIndex !== -1) {
    const [queuedFrame] = client.inbox.splice(queuedIndex, 1);
    if (queuedFrame !== undefined) {
      return Promise.resolve(queuedFrame);
    }
  }
  // Promise wrapper bridges two callback sources (matching frame / timeout)
  // so the async call site can simply await it.
  // eslint-disable-next-line promise/avoid-new
  return new Promise<Frame>((resolve, reject) => {
    const timer = setTimeout(() => {
      reject(new Error(`timeout waiting for "${type}"`));
    }, timeoutMs);
    const wait = (frame: Frame) => {
      if (frame.type === type) {
        clearTimeout(timer);
        resolve(frame);
        return;
      }
      // Wrong type: park it and keep waiting for the next frame.
      client.inbox.push(frame);
      client.waiters.push(wait);
    };
    client.waiters.push(wait);
  });
};

const assert = (condition: boolean, message: string) => {
  if (!condition) {
    throw new Error(`assertion failed: ${message}`);
  }
};

const waitForPersistence = async (expected = 1): Promise<Frame> => {
  const deadline = Date.now() + 5000;
  while (true) {
    // eslint-disable-next-line no-await-in-loop -- Native fanout precedes the queued SQLite commit.
    const response = await fetch(`${host}/stats`);
    if (!response.ok) {
      throw new Error(`stats returned HTTP ${response.status}`);
    }
    // eslint-disable-next-line no-await-in-loop -- Inspect each persistence attempt before polling again.
    const snapshot = (await response.json()) as Frame;
    if (
      Number(snapshot.messagesPersistFailed) > 0 ||
      Number(snapshot.messagesPersistDropped) > 0
    ) {
      throw new Error(
        `message persistence failed: ${JSON.stringify(snapshot)}`
      );
    }
    if (Number(snapshot.messagesPersisted) >= expected) {
      return snapshot;
    }
    if (Date.now() >= deadline) {
      throw new Error(
        `persistence did not complete within 5s: ${JSON.stringify(snapshot)}`
      );
    }
    // eslint-disable-next-line no-await-in-loop -- This is a bounded completion poll, not a load test.
    await Bun.sleep(25);
  }
};

try {
  const alice = await connect("alice");
  const connected = await nextFrame(alice, "connected");
  assert(connected.room === room, "connected frame carries room id");

  send(alice, {
    avatar: "👻",
    name: "Alice",
    type: "join",
    userId: "alice-1",
  });
  const joined = await nextFrame(alice, "joined");
  assert(joined.peers.length === 1, "alice sees herself in the roster");
  assert(joined.peers[0].userId === "alice-1", "roster carries metadata");

  const bob = await connect("bob");
  await nextFrame(bob, "connected");
  send(bob, { name: "Bob", type: "join", userId: "bob-1" });
  const bobJoined = await nextFrame(bob, "joined");
  assert(bobJoined.peers.length === 2, "bob sees alice + himself");

  const peerJoined = await nextFrame(alice, "peer_joined");
  assert(peerJoined.peer.userId === "bob-1", "alice is told bob joined");

  // Presence: bob moves his cursor; alice should see the ephemeral frame.
  send(bob, { type: "presence", typing: true, x: 128.5, y: 256.25 });
  const presence = await nextFrame(alice, "presence");
  assert(presence.connectionId, "presence identifies the sender");
  assert(presence.x === 128.5 && presence.y === 256.25, "cursor round-trips");
  assert(presence.typing === true, "typing flag round-trips");

  // Broadcast: alice sends a payload; both peers receive the fanned frame.
  send(alice, { payload: { kind: "note", text: "hi" }, type: "broadcast" });
  const mine = await nextFrame(alice, "message");
  const bobs = await nextFrame(bob, "message");
  assert(mine.payload.text === "hi", "sender receives its broadcast");
  assert(bobs.senderId === "alice-1", "receiver sees the sender id");
  assert(
    typeof bobs.id === "string" && bobs.id.length > 0,
    "message has an id"
  );

  // Leave: bob leaves; alice is notified.
  send(bob, { type: "leave" });
  await nextFrame(bob, "bye");
  const peerLeft = await nextFrame(alice, "peer_left");
  assert(peerLeft.userId === "bob-1", "alice is told bob left");

  // REST surface
  // Native persistence is deliberately queued after broadcast; receipt of a
  // frame is not a durable-write acknowledgement. Verify eventual completion.
  const stats = await waitForPersistence();
  assert(stats.messagesRelayed >= 1, "stats counts relayed messages");
  assert(stats.messagesPersisted >= 1, "stats counts persisted messages");

  const historyResponse = await fetch(
    `${host}/rooms/${room}/messages?limit=10`
  );
  const history = (await historyResponse.json()) as Frame[];
  assert(history.length === 1, "one persisted message in history");
  assert(
    history[0]?.id === mine.id,
    "history preserves the relayed message id"
  );
  // payload is stored as TEXT (raw JSON), so it round-trips as a string.
  const persisted = JSON.parse(String(history[0]?.payload)) as Frame;
  assert(persisted.text === "hi", "persisted payload matches");
  assert(typeof history[0]?.createdAt === "string", "created_at was defaulted");

  const serverResponse = await fetch(`${host}/rooms/${room}/broadcast`, {
    body: JSON.stringify({
      payload: { text: "server-note" },
      senderId: "server",
    }),
    headers: { "Content-Type": "application/json" },
    method: "POST",
  });
  assert(
    serverResponse.status === 202,
    "server fanout is accepted, not a durability ack"
  );
  const serverFrame = await nextFrame(alice, "message");
  assert(
    serverFrame.payload.text === "server-note",
    "REST fanout reaches the live peer"
  );
  await waitForPersistence(2);
  const serverHistoryResponse = await fetch(
    `${host}/rooms/${room}/messages?limit=10`
  );
  const serverHistory = (await serverHistoryResponse.json()) as Frame[];
  assert(serverHistory.length === 2, "WebSocket and REST snapshots persist");
  assert(
    serverHistory.some((message) => message.id === serverFrame.id),
    "REST frame id survives persistence"
  );

  const peersResponse = await fetch(`${host}/rooms/${room}/peers`);
  const peers = (await peersResponse.json()) as Frame[];
  assert(peers.length === 1, "only alice remains in the roster");

  const roomsResponse = await fetch(`${host}/rooms`);
  const roomsList = (await roomsResponse.json()) as Frame[];
  const entry = roomsList.find((r) => r.roomId === room);
  assert(entry && entry.peers === 1, "/rooms lists the room with 1 peer");

  console.log("✓ smoke test passed");
  for (const client of clients) {
    client.socket.close();
  }
  process.exit(0);
} catch (error) {
  console.error(
    `✗ smoke test failed: ${error instanceof Error ? error.message : error}`
  );
  process.exit(1);
}

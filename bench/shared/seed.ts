/* oxlint-disable eslint/no-bitwise, eslint/operator-assignment, unicorn/prefer-math-trunc, typescript/no-non-null-assertion */
/**
 * Deterministic seed for the Reptar Bench issue-tracker database.
 *
 * Every implementation reads the same SQLite file, so the data (and therefore
 * the response bytes) is identical across Reptar, .NET Minimal API, and Hono.
 *
 * Usage:
 *   bun bench/shared/seed.ts [outPath]
 *
 * Defaults to bench/out/bench.db. Writes fixtures.json next to it.
 */

import { Database } from "bun:sqlite";
import { createHmac, pbkdf2Sync } from "node:crypto";
import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import path from "node:path";

const outPath = path.resolve(
  process.argv[2] ?? path.join(import.meta.dir, "..", "out", "bench.db")
);
const fixturesPath = path.join(path.dirname(outPath), "fixtures.json");

mkdirSync(path.dirname(outPath), { recursive: true });

const USER_COUNT = 1000;
const ORG_COUNT = 10;
const PROJECTS_PER_ORG = 10;
const TASKS_PER_PROJECT = 500;
const COMMENTS_PER_TASK = 5;

const STATUSES = [
  "open",
  "open",
  "open",
  "in_progress",
  "in_progress",
  "done",
] as const;

const VERBS = [
  "Fix",
  "Add",
  "Refactor",
  "Document",
  "Investigate",
  "Optimize",
  "Remove",
  "Harden",
  "Instrument",
  "Simplify",
];

const NOUNS = [
  "login timeout",
  "pagination cursor",
  "webhook retry",
  "session refresh",
  "search ranking",
  "billing export",
  "audit log",
  "rate limiter",
  "upload pipeline",
  "notification digest",
  "permission check",
  "cache invalidation",
];

const AREAS = [
  "auth",
  "billing",
  "search",
  "uploads",
  "notifications",
  "permissions",
  "reporting",
  "realtime",
];

const SENTENCES = [
  "Reproduced on the staging cluster with a fresh seed.",
  "This blocks the weekly release and needs a decision today.",
  "Add a regression test before shipping the fix.",
  "The current workaround is documented in the runbook.",
  "We should measure the impact before changing the schema.",
  "Customer reported this on the enterprise plan.",
  "Related to the previous incident but a different code path.",
  "Keep the API response shape stable for existing clients.",
];

const pad = (value: number, width: number): string =>
  String(value).padStart(width, "0");

const iso = (ms: number): string => new Date(ms).toISOString();

/** Small deterministic PRNG so every run produces the same rows. */
const mulberry32 = (seed: number): (() => number) => {
  let state = seed;
  return () => {
    state |= 0;
    state = (state + 0x6d_2b_79_f5) | 0;
    let t = Math.imul(state ^ (state >>> 15), 1 | state);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4_294_967_296;
  };
};

const random = mulberry32(0x5e_ed_12_34);

const pick = <T>(items: readonly T[]): T =>
  items[Math.floor(random() * items.length)]!;

const baseTime = Date.UTC(2025, 0, 1, 0, 0, 0);

const passwordSalt = Buffer.from("000102030405060708090a0b0c0d0e0f", "hex");
const passwordHash = pbkdf2Sync(
  "benchmark-password",
  passwordSalt,
  100_000,
  32,
  "sha256"
);
const passwordRecord = `pbkdf2-sha256$100000$${passwordSalt.toString("base64")}$${passwordHash.toString("base64")}`;

const jwtSecret = "bench-jwt-secret";
const jwtIssuedAt = Math.floor(Date.UTC(2026, 0, 1) / 1000);
const jwtExpiresAt = Math.floor(Date.UTC(2036, 0, 1) / 1000);

const base64Url = (value: Buffer | string): string =>
  Buffer.from(value).toString("base64url");

const jwtHeader = base64Url(JSON.stringify({ alg: "HS256", typ: "JWT" }));
const jwtPayload = base64Url(
  JSON.stringify({
    aud: "reptar-bench",
    email: "user1@example.com",
    exp: jwtExpiresAt,
    iat: jwtIssuedAt,
    iss: "reptar-bench",
    name: "User 1",
    role: "user",
    sub: "usr_000001",
  })
);
const jwtSignature = createHmac("sha256", jwtSecret)
  .update(`${jwtHeader}.${jwtPayload}`)
  .digest("base64url");
const jwt = `${jwtHeader}.${jwtPayload}.${jwtSignature}`;

if (await Bun.file(outPath).exists()) {
  rmSync(outPath, { force: true });
}

rmSync(`${outPath}-wal`, { force: true });
rmSync(`${outPath}-shm`, { force: true });

const db = new Database(outPath, { create: true });
db.exec("PRAGMA journal_mode = WAL");
db.exec("PRAGMA synchronous = NORMAL");
db.exec(await Bun.file(path.join(import.meta.dir, "schema.sql")).text());

const insertUser = db.prepare(
  "INSERT INTO users (id, email, name, password_hash, created_at) VALUES (?, ?, ?, ?, ?)"
);
const insertOrg = db.prepare(
  "INSERT INTO orgs (id, name, slug, created_at) VALUES (?, ?, ?, ?)"
);
const insertProject = db.prepare(
  "INSERT INTO projects (id, org_id, name, key, created_at) VALUES (?, ?, ?, ?, ?)"
);
const insertTask = db.prepare(
  `INSERT INTO tasks
    (id, project_id, author_id, assignee_id, title, description, status, priority, created_at, updated_at)
   VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
);
const insertComment = db.prepare(
  "INSERT INTO comments (id, task_id, author_id, body, created_at) VALUES (?, ?, ?, ?, ?)"
);

const userIds: string[] = [];
const userEmails: string[] = [];
const orgIds: string[] = [];
const projectIds: string[] = [];
const taskIds: string[] = [];

console.log(`Seeding ${outPath} ...`);

db.exec("BEGIN");

for (let index = 0; index < USER_COUNT; index += 1) {
  const id = `usr_${pad(index + 1, 6)}`;
  const email = `user${index + 1}@example.com`;
  const name = `User ${index + 1}`;
  userIds.push(id);
  userEmails.push(email);
  insertUser.run(
    id,
    email,
    name,
    index === 0 ? passwordRecord : "!",
    iso(baseTime + index * 60_000)
  );
}

for (let index = 0; index < ORG_COUNT; index += 1) {
  const id = `org_${pad(index + 1, 4)}`;
  orgIds.push(id);
  insertOrg.run(
    id,
    `Org ${index + 1}`,
    `org-${index + 1}`,
    iso(baseTime + index * 86_400_000)
  );
}

for (let index = 0; index < ORG_COUNT * PROJECTS_PER_ORG; index += 1) {
  const id = `prj_${pad(index + 1, 4)}`;
  projectIds.push(id);
  insertProject.run(
    id,
    orgIds[Math.floor(index / PROJECTS_PER_ORG)]!,
    `Project ${index + 1}`,
    `PRJ${index + 1}`,
    iso(baseTime + index * 3_600_000)
  );
}

const taskRows: {
  id: string;
  projectId: string;
  createdAt: string;
}[] = [];

for (let index = 0; index < projectIds.length * TASKS_PER_PROJECT; index += 1) {
  const id = `tsk_${pad(index + 1, 6)}`;
  const projectId = projectIds[Math.floor(index / TASKS_PER_PROJECT)]!;
  const authorId = userIds[Math.floor(random() * userIds.length)]!;
  const assigneeId =
    index % 10 === 9 ? null : userIds[Math.floor(random() * userIds.length)]!;
  const status = index === 0 ? "open" : pick(STATUSES);
  const priority = Math.floor(random() * 4);
  const createdAt =
    index === 0
      ? baseTime
      : baseTime + Math.floor(random() * 365 * 24 * 3_600_000);
  const updatedAt = createdAt + Math.floor(random() * 72 * 3_600_000);
  const title = `${pick(VERBS)} ${pick(NOUNS)} #${index + 1}`;
  const description =
    `${pick(SENTENCES)} Area: ${pick(AREAS)}. ` +
    `Task ${index + 1} in project ${projectId}. ` +
    `Owner: ${authorId}. Expected impact: ${pick(["low", "medium", "high"])}.`;

  taskRows.push({ createdAt: iso(createdAt), id, projectId });
  taskIds.push(id);

  insertTask.run(
    id,
    projectId,
    index === 0 ? userIds[0]! : authorId,
    index === 0 ? userIds[1]! : assigneeId,
    title,
    description,
    status,
    priority,
    iso(createdAt),
    iso(updatedAt)
  );
}

let commentIndex = 0;

for (const task of taskRows) {
  for (let index = 0; index < COMMENTS_PER_TASK; index += 1) {
    commentIndex += 1;
    const id = `cmt_${pad(commentIndex, 6)}`;
    const authorId = userIds[Math.floor(random() * userIds.length)]!;
    const createdAt = Date.parse(task.createdAt) + (index + 1) * 3_600_000;
    const body = `Comment ${index + 1} on ${task.id}: ${pick(SENTENCES)}`;

    insertComment.run(id, task.id, authorId, body, iso(createdAt));
  }
}

db.exec("COMMIT");
db.exec("PRAGMA wal_checkpoint(TRUNCATE)");

const fixtures = {
  counts: {
    comments: commentIndex,
    orgs: ORG_COUNT,
    projects: projectIds.length,
    tasks: taskIds.length,
    users: USER_COUNT,
  },
  generatedAt: new Date().toISOString(),
  login: {
    email: "user1@example.com",
    jwt,
    password: "benchmark-password",
  },
  orgIds,
  projectIds,
  sample: {
    assigneeId: "usr_000002",
    orgId: "org_0001",
    projectId: "prj_0001",
    secondTaskId: "tsk_000002",
    status: "open",
    taskId: "tsk_000001",
    userId: "usr_000001",
  },
  taskIds: taskIds.slice(0, 200),
};

writeFileSync(fixturesPath, `${JSON.stringify(fixtures, null, 2)}\n`);

const stats = db
  .query(
    "SELECT (SELECT count(*) FROM users) AS users, (SELECT count(*) FROM tasks) AS tasks, (SELECT count(*) FROM comments) AS comments"
  )
  .get() as { users: number; tasks: number; comments: number };

db.close();

console.log(
  `Seeded ${stats.users} users, ${stats.tasks} tasks, ${stats.comments} comments.`
);
console.log(`Fixtures: ${fixturesPath}`);

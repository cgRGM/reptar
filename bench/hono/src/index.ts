import { Database } from "bun:sqlite";
import { timingSafeEqual } from "node:crypto";

import { zValidator } from "@hono/zod-validator";
import { Hono } from "hono";
import type { Context, MiddlewareHandler } from "hono";
import { sign, verify } from "hono/jwt";
import { z } from "zod";

const databasePath = process.env.BENCH_DB ?? "bench.db";
const port = Number(process.env.PORT ?? 9103);
const jwtSecret = process.env.BENCH_JWT_SECRET ?? "bench-jwt-secret";
const jwtIssuer = "reptar-bench";
const jwtAudience = "reptar-bench";

const db = new Database(databasePath);
db.exec("PRAGMA busy_timeout = 5000");
db.exec("PRAGMA synchronous = NORMAL");

const getUserById = db.prepare(
  "select id, email, name, created_at from users where id = ?"
);
const getUserByEmail = db.prepare(
  "select id, email, name, password_hash, created_at from users where email = ?"
);
const getTask = db.prepare(
  `select t.id, t.project_id, t.title, t.description, t.status, t.priority,
          t.author_id, au.name as author_name,
          t.assignee_id, su.name as assignee_name,
          t.created_at, t.updated_at
   from tasks t
   join users au on au.id = t.author_id
   left join users su on su.id = t.assignee_id
   where t.id = ?`
);
const listTasks = db.prepare(
  `select id, project_id, title, description, status, priority,
          assignee_id, created_at, updated_at
   from tasks
   where project_id = ? and status = ?
   order by created_at desc
   limit ? offset ?`
);
const listComments = db.prepare(
  `select c.id, c.task_id, c.body, c.created_at,
          u.id as author_id, u.name as author_name
   from comments c
   join users u on u.id = c.author_id
   where c.task_id = ?
   order by c.created_at asc
   limit ? offset ?`
);
const insertTask = db.prepare(
  `insert into tasks
     (id, project_id, author_id, assignee_id, title, description, status, priority, created_at, updated_at)
   values (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
);
const updateTask = db.prepare(
  `update tasks
   set title = ?, description = ?, status = ?, priority = ?, assignee_id = ?, updated_at = ?
   where id = ?`
);

interface AuthUserRow {
  id: string;
  email: string;
  name: string;
  created_at: string;
}

interface AuthUserWithPasswordRow extends AuthUserRow {
  password_hash: string;
}

interface TaskDetailRow {
  id: string;
  project_id: string;
  title: string;
  description: string;
  status: string;
  priority: number;
  author_id: string;
  author_name: string;
  assignee_id: string | null;
  assignee_name: string | null;
  created_at: string;
  updated_at: string;
}

interface TaskSummaryRow {
  id: string;
  project_id: string;
  title: string;
  description: string;
  status: string;
  priority: number;
  assignee_id: string | null;
  created_at: string;
  updated_at: string;
}

interface CommentRow {
  id: string;
  task_id: string;
  body: string;
  created_at: string;
  author_id: string;
  author_name: string;
}

interface AppVariables {
  user: AuthUserRow;
}

const app = new Hono<{ Variables: AppVariables }>();

let taskSequence = 0;

const isoNow = (): string => new Date().toISOString();

const parseLimit = (value: string | undefined, fallback: number): number => {
  const parsed = Math.trunc(Number(value));
  return Number.isFinite(parsed)
    ? Math.min(Math.max(parsed, 1), 100)
    : fallback;
};

const parseOffset = (value: string | undefined): number => {
  const parsed = Math.trunc(Number(value));
  return Number.isFinite(parsed) ? Math.min(Math.max(parsed, 0), 1_000_000) : 0;
};

const errorBody = (code: string, message: string) => ({
  error: { code, message },
});

interface ValidationFailure {
  success: false;
  error: z.ZodError;
}

const isValidationFailure = (value: unknown): value is ValidationFailure =>
  typeof value === "object" &&
  value !== null &&
  "success" in value &&
  (value as { success?: unknown }).success === false;

const validationHook = (result: unknown, c: Context) => {
  if (!isValidationFailure(result)) {
    return;
  }

  return c.json(
    {
      error: { code: "validation_error", message: "Validation failed." },
      issues: result.error.issues.map((issue) => ({
        message: issue.message,
        path: issue.path.join("."),
      })),
    },
    422
  );
};

const taskSchema = z.object({
  assigneeId: z.string().nullable().optional(),
  description: z.string().max(2000),
  priority: z.number().int().min(0).max(3),
  status: z.enum(["open", "in_progress", "done"]),
  title: z.string().min(1).max(200),
});

const updateTaskSchema = taskSchema.partial();

const loginSchema = z.object({
  email: z.string().min(1),
  password: z.string().min(1),
});

const base64ToBytes = (value: string): Uint8Array =>
  Uint8Array.from(Buffer.from(value, "base64"));

const verifyPassword = async (
  password: string,
  stored: string
): Promise<boolean> => {
  const parts = stored.split("$");

  if (parts.length !== 4 || parts[0] !== "pbkdf2-sha256") {
    return false;
  }

  const iterations = Number(parts[1]);
  const salt = base64ToBytes(parts[2] ?? "");
  const expected = base64ToBytes(parts[3] ?? "");

  if (!(Number.isFinite(iterations) && expected.length > 0)) {
    return false;
  }

  const key = await crypto.subtle.importKey(
    "raw",
    new TextEncoder().encode(password),
    "PBKDF2",
    false,
    ["deriveBits"]
  );
  const derived = new Uint8Array(
    await crypto.subtle.deriveBits(
      {
        hash: "SHA-256",
        iterations,
        name: "PBKDF2",
        salt,
      },
      key,
      expected.length * 8
    )
  );

  return (
    derived.length === expected.length && timingSafeEqual(derived, expected)
  );
};

const requireAuth: MiddlewareHandler<{ Variables: AppVariables }> = async (
  c,
  next
) => {
  const header = c.req.header("authorization") ?? "";

  if (!header.toLowerCase().startsWith("bearer ")) {
    return c.json(errorBody("unauthorized", "Missing bearer token."), 401);
  }

  const token = header.slice(7).trim();

  try {
    const payload = (await verify(token, jwtSecret, "HS256")) as {
      sub?: string;
      iss?: string;
      aud?: string;
    };

    if (payload.iss !== jwtIssuer || payload.aud !== jwtAudience) {
      return c.json(errorBody("unauthorized", "Invalid bearer token."), 401);
    }

    const user = getUserById.get(payload.sub ?? "") as AuthUserRow | null;

    if (!user) {
      return c.json(errorBody("unauthorized", "User not found."), 401);
    }

    c.set("user", user);
    return await next();
  } catch {
    return c.json(errorBody("unauthorized", "Invalid bearer token."), 401);
  }
};

const toDetail = (row: TaskDetailRow) => ({
  assignee: row.assignee_id
    ? { id: row.assignee_id, name: row.assignee_name }
    : null,
  author: { id: row.author_id, name: row.author_name },
  createdAt: row.created_at,
  description: row.description,
  id: row.id,
  priority: row.priority,
  projectId: row.project_id,
  status: row.status,
  title: row.title,
  updatedAt: row.updated_at,
});

const toSummary = (row: TaskSummaryRow) => ({
  assigneeId: row.assignee_id,
  createdAt: row.created_at,
  description: row.description,
  id: row.id,
  priority: row.priority,
  projectId: row.project_id,
  status: row.status,
  title: row.title,
  updatedAt: row.updated_at,
});

const toComment = (row: CommentRow) => ({
  author: { id: row.author_id, name: row.author_name },
  body: row.body,
  createdAt: row.created_at,
  id: row.id,
  taskId: row.task_id,
});

const resolveAssignee = (assigneeId: string | null | undefined) => {
  if (!assigneeId) {
    return null;
  }

  const row = getUserById.get(assigneeId) as AuthUserRow | null;
  return row ? { id: row.id, name: row.name } : null;
};

// ---------------------------------------------------------------------------
// Calibration tiers
// ---------------------------------------------------------------------------

app.get("/__bench/plaintext", (c) => c.text("Hello, World!"));

app.get("/__bench/json", (c) =>
  c.json({
    message: "Hello, World!",
    nested: { a: 1, b: 2 },
    ok: true,
    tags: ["bench", "reptar", "http"],
    version: 1,
  })
);

app.get("/__bench/router/:orgId/:projectId/:taskId", (c) =>
  c.json({
    orgId: c.req.param("orgId"),
    projectId: c.req.param("projectId"),
    taskId: c.req.param("taskId"),
  })
);

app.post(
  "/__bench/validate",
  zValidator("json", taskSchema, validationHook),
  (c) => {
    const input = c.req.valid("json");
    return c.json(
      {
        ok: true,
        priority: input.priority,
        status: input.status,
        title: input.title,
      },
      201
    );
  }
);

// ---------------------------------------------------------------------------
// Realistic API
// ---------------------------------------------------------------------------

app.get("/api/v1/tasks/:id", (c) => {
  const row = getTask.get(c.req.param("id")) as TaskDetailRow | null;

  if (!row) {
    return c.json(errorBody("not_found", "Task not found."), 404);
  }

  return c.json(toDetail(row));
});

app.get("/api/v1/projects/:projectId/tasks", (c) => {
  const projectId = c.req.param("projectId");
  const status = c.req.query("status") ?? "open";
  const limit = parseLimit(c.req.query("limit"), 20);
  const offset = parseOffset(c.req.query("cursor"));
  const rows = listTasks.all(
    projectId,
    status,
    limit,
    offset
  ) as TaskSummaryRow[];

  return c.json({
    data: rows.map(toSummary),
    nextCursor: rows.length === limit ? String(offset + limit) : null,
  });
});

app.get("/api/v1/tasks/:id/comments", (c) => {
  const taskId = c.req.param("id");
  const limit = parseLimit(c.req.query("limit"), 20);
  const offset = parseOffset(c.req.query("cursor"));
  const rows = listComments.all(taskId, limit, offset) as CommentRow[];

  return c.json({
    data: rows.map(toComment),
    nextCursor: rows.length === limit ? String(offset + limit) : null,
  });
});

app.post(
  "/api/v1/projects/:projectId/tasks",
  requireAuth,
  zValidator("json", taskSchema, validationHook),
  (c) => {
    const user = c.get("user");
    const projectId = c.req.param("projectId");
    const input = c.req.valid("json");
    const now = isoNow();
    taskSequence += 1;
    const id = `tsk_${Date.now()}_${taskSequence}`;

    insertTask.run(
      id,
      projectId,
      user.id,
      input.assigneeId ?? null,
      input.title,
      input.description,
      input.status,
      input.priority,
      now,
      now
    );

    return c.json(
      {
        assignee: resolveAssignee(input.assigneeId),
        author: { id: user.id, name: user.name },
        createdAt: now,
        description: input.description,
        id,
        priority: input.priority,
        projectId,
        status: input.status,
        title: input.title,
        updatedAt: now,
      },
      201
    );
  }
);

app.put(
  "/api/v1/tasks/:id",
  requireAuth,
  zValidator("json", updateTaskSchema, validationHook),
  (c) => {
    const id = c.req.param("id");
    const input = c.req.valid("json");
    const existing = getTask.get(id) as TaskDetailRow | null;

    if (!existing) {
      return c.json(errorBody("not_found", "Task not found."), 404);
    }

    const title = input.title ?? existing.title;
    const description = input.description ?? existing.description;
    const status = input.status ?? existing.status;
    const priority = input.priority ?? existing.priority;
    const assigneeId = input.assigneeId ?? existing.assignee_id;
    const now = isoNow();

    updateTask.run(title, description, status, priority, assigneeId, now, id);

    return c.json({
      assignee: resolveAssignee(assigneeId),
      author: { id: existing.author_id, name: existing.author_name },
      createdAt: existing.created_at,
      description,
      id: existing.id,
      priority,
      projectId: existing.project_id,
      status,
      title,
      updatedAt: now,
    });
  }
);

app.get("/api/v1/me", requireAuth, (c) => {
  const user = c.get("user");

  return c.json({
    createdAt: user.created_at,
    email: user.email,
    id: user.id,
    name: user.name,
  });
});

app.post(
  "/api/v1/auth/login",
  zValidator("json", loginSchema, validationHook),
  async (c) => {
    const input = c.req.valid("json");
    const row = getUserByEmail.get(
      input.email
    ) as AuthUserWithPasswordRow | null;

    if (!row || !(await verifyPassword(input.password, row.password_hash))) {
      return c.json(
        errorBody("unauthorized", "Invalid email or password."),
        401
      );
    }

    const issuedAt = Math.floor(Date.now() / 1000);
    const expiresAt = issuedAt + 60 * 60 * 24 * 3650;
    const token = await sign(
      {
        aud: jwtAudience,
        email: row.email,
        exp: expiresAt,
        iat: issuedAt,
        iss: jwtIssuer,
        name: row.name,
        role: "user",
        sub: row.id,
      },
      jwtSecret,
      "HS256"
    );

    return c.json({ expiresAt, token, tokenType: "Bearer" });
  }
);

Bun.serve({ fetch: app.fetch, port });

console.log(`reptar-bench-hono listening on ${port}`);

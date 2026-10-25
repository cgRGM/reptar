-- Reptar Bench shared schema.
-- Every implementation uses this exact schema and the exact same seeded rows.

PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;

CREATE TABLE IF NOT EXISTS users (
  id            TEXT PRIMARY KEY,
  email         TEXT NOT NULL UNIQUE,
  name          TEXT NOT NULL,
  password_hash TEXT NOT NULL,
  created_at    TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS orgs (
  id         TEXT PRIMARY KEY,
  name       TEXT NOT NULL,
  slug       TEXT NOT NULL UNIQUE,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS projects (
  id         TEXT PRIMARY KEY,
  org_id     TEXT NOT NULL REFERENCES orgs(id),
  name       TEXT NOT NULL,
  key        TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS tasks (
  id          TEXT PRIMARY KEY,
  project_id  TEXT NOT NULL REFERENCES projects(id),
  author_id   TEXT NOT NULL REFERENCES users(id),
  assignee_id TEXT REFERENCES users(id),
  title       TEXT NOT NULL,
  description TEXT NOT NULL,
  status      TEXT NOT NULL,
  priority    INTEGER NOT NULL,
  created_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS comments (
  id         TEXT PRIMARY KEY,
  task_id    TEXT NOT NULL REFERENCES tasks(id),
  author_id  TEXT NOT NULL REFERENCES users(id),
  body       TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS tasks_project_status_created
  ON tasks(project_id, status, created_at DESC);

CREATE INDEX IF NOT EXISTS tasks_assignee_status
  ON tasks(assignee_id, status);

CREATE INDEX IF NOT EXISTS comments_task_created
  ON comments(task_id, created_at);

CREATE INDEX IF NOT EXISTS projects_org
  ON projects(org_id);

CREATE INDEX IF NOT EXISTS users_email
  ON users(email);

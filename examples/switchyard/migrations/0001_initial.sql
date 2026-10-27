-- Migration: 0001_initial.sql
-- Created: 2026-10-26T10:27:17.517Z
-- Changes: 12 operation(s) planned from TableDef

CREATE TABLE IF NOT EXISTS "users" (
  "id" TEXT PRIMARY KEY,
  "email" TEXT NOT NULL UNIQUE,
  "name" TEXT NOT NULL,
  "password_hash" TEXT,
  "image" TEXT,
  "email_verified" INTEGER NOT NULL DEFAULT 0,
  "is_anonymous" INTEGER NOT NULL DEFAULT 0,
  "role" TEXT NOT NULL DEFAULT 'user',
  "created_at" TEXT DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS "users_email_uq" ON "users"("email");

CREATE TABLE IF NOT EXISTS "sessions" (
  "id" TEXT PRIMARY KEY,
  "user_id" TEXT NOT NULL REFERENCES "users"("id") ON DELETE CASCADE,
  "token_hash" TEXT NOT NULL UNIQUE,
  "expires_at" TEXT NOT NULL,
  "created_at" TEXT DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS "sessions_token_hash_uq" ON "sessions"("token_hash");

CREATE TABLE IF NOT EXISTS "oauth_accounts" (
  "id" TEXT PRIMARY KEY,
  "user_id" TEXT NOT NULL REFERENCES "users"("id") ON DELETE CASCADE,
  "provider" TEXT NOT NULL,
  "provider_user_id" TEXT NOT NULL,
  "created_at" TEXT DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS "api_keys" (
  "id" TEXT PRIMARY KEY,
  "user_id" TEXT NOT NULL REFERENCES "users"("id") ON DELETE CASCADE,
  "name" TEXT NOT NULL,
  "key_hash" TEXT NOT NULL UNIQUE,
  "prefix" TEXT NOT NULL,
  "expires_at" TEXT,
  "created_at" TEXT DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS "api_keys_key_hash_uq" ON "api_keys"("key_hash");

CREATE TABLE IF NOT EXISTS "sy_projects" (
  "id" TEXT PRIMARY KEY,
  "owner_id" TEXT NOT NULL,
  "name" TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS "sy_projects_owner_id_idx" ON "sy_projects"("owner_id");

CREATE TABLE IF NOT EXISTS "sy_flags" (
  "project_id" TEXT NOT NULL REFERENCES "sy_projects"("id"),
  "environment" TEXT NOT NULL,
  "key" TEXT NOT NULL,
  "enabled" INTEGER NOT NULL,
  "rollout" INTEGER NOT NULL,
  "targets" TEXT NOT NULL,
  "revision" INTEGER NOT NULL,
  "version" INTEGER NOT NULL,
  PRIMARY KEY ("project_id", "environment", "key")
);

CREATE TABLE IF NOT EXISTS "sy_versions" (
  "project_id" TEXT NOT NULL REFERENCES "sy_projects"("id"),
  "environment" TEXT NOT NULL,
  "key" TEXT NOT NULL,
  "version" INTEGER NOT NULL,
  "enabled" INTEGER NOT NULL,
  "rollout" INTEGER NOT NULL,
  "targets" TEXT NOT NULL,
  "actor_id" TEXT NOT NULL,
  "created_at" TEXT NOT NULL,
  PRIMARY KEY ("project_id", "environment", "key", "version")
);

CREATE TABLE IF NOT EXISTS "sy_audit" (
  "id" TEXT PRIMARY KEY,
  "project_id" TEXT NOT NULL REFERENCES "sy_projects"("id"),
  "environment" TEXT NOT NULL,
  "key" TEXT NOT NULL,
  "version" INTEGER NOT NULL,
  "actor_id" TEXT NOT NULL,
  "created_at" TEXT NOT NULL
);

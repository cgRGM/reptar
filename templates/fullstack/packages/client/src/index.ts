export { createClient } from "./client";
export type {
  Client,
  ClientOptions,
  ClientResponse,
  Endpoint,
  RouteTree,
} from "./client";

// Codegen owns types.ts, schemas.ts, and app-type.d.ts. Re-export them
// wholesale so adding or renaming a DTO never requires hand-editing this file.
export * from "./types";
export * from "./schemas";
export type { AppType } from "./app-type";

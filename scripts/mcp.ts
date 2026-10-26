/* oxlint-disable no-explicit-any -- JSON-RPC envelopes are schema-free */
/* oxlint-disable complexity -- one dispatch table for MCP methods */
import readline from "node:readline";

import { planMigrations } from "./diff";
import { doctorProject, inspectProject } from "./inspect";

export interface JsonRpcRequest {
  jsonrpc: "2.0";
  id?: number | string | null;
  method: string;
  params?: any;
}

export interface JsonRpcResponse {
  jsonrpc: "2.0";
  id?: number | string | null;
  result?: any;
  error?: {
    code: number;
    message: string;
    data?: any;
  };
}

const MCP_TOOLS = [
  {
    description:
      "Inspect complete Reptar project state: database dialect, active connection, tables, columns, indexes, registered HTTP routes, AI tool endpoints, and migrations in structured JSON.",
    inputSchema: {
      properties: {
        dbPath: {
          description: "Optional explicit path to SQLite database file",
          type: "string",
        },
      },
      type: "object",
    },
    name: "reptar_inspect",
  },
  {
    description:
      "Query database tables, columns, types, primary keys, nullability, and indexes. Allows inspecting a single table or the whole schema.",
    inputSchema: {
      properties: {
        table: {
          description:
            "Optional table name to filter by (e.g. 'posts', 'users')",
          type: "string",
        },
      },
      type: "object",
    },
    name: "reptar_schema",
  },
  {
    description:
      "List all registered HTTP routes, methods, and AI tool endpoints (x-ai-tool) in the application.",
    inputSchema: {
      properties: {
        aiOnly: {
          description: "Filter to only routes exposed as AI tools",
          type: "boolean",
        },
      },
      type: "object",
    },
    name: "reptar_routes",
  },
  {
    description:
      "Check database connection, migration directory, applied migrations, and pending migrations.",
    inputSchema: {
      properties: {},
      type: "object",
    },
    name: "reptar_db_status",
  },
  {
    description:
      "Compare C# TableDef schema definitions against the active database and return planned CREATE TABLE, ALTER TABLE, and CREATE INDEX statements.",
    inputSchema: {
      properties: {},
      type: "object",
    },
    name: "reptar_db_plan",
  },
  {
    description:
      "Run static project verification: ensures migrations are applied, RPC contracts are up-to-date, and schemas are aligned.",
    inputSchema: {
      properties: {},
      type: "object",
    },
    name: "reptar_check",
  },
  {
    description:
      "Diagnose toolchain environment: verify .NET 10 SDK, Bun runtime, database connectivity, and RPC codegen status.",
    inputSchema: {
      properties: {},
      type: "object",
    },
    name: "reptar_doctor",
  },
];

const sendResponse = (res: JsonRpcResponse) => {
  process.stdout.write(`${JSON.stringify(res)}\n`);
};

export const runMcpServer = (
  repoRoot: string,
  cwd: string = process.cwd()
): void => {
  const rl = readline.createInterface({
    input: process.stdin,
    output: process.stdout,
    terminal: false,
  });

  rl.on("close", () => {
    process.exit(0);
  });

  rl.on("line", (line) => {
    const trimmed = line.trim();
    if (!trimmed) {
      return;
    }

    let request: JsonRpcRequest;
    try {
      request = JSON.parse(trimmed);
    } catch {
      sendResponse({
        error: { code: -32_700, message: "Parse error: invalid JSON" },
        id: null,
        jsonrpc: "2.0",
      });
      return;
    }

    const { id, method, params } = request;

    switch (method) {
      case "initialize": {
        sendResponse({
          id,
          jsonrpc: "2.0",
          result: {
            capabilities: {
              tools: {},
            },
            protocolVersion: "2024-11-05",
            serverInfo: {
              name: "reptar-mcp",
              version: "0.1.0",
            },
          },
        });
        break;
      }

      case "notifications/initialized": {
        // Notification, no response
        break;
      }

      case "ping": {
        sendResponse({
          id,
          jsonrpc: "2.0",
          result: {},
        });
        break;
      }

      case "tools/list": {
        sendResponse({
          id,
          jsonrpc: "2.0",
          result: {
            tools: MCP_TOOLS,
          },
        });
        break;
      }

      case "tools/call": {
        const toolName = params?.name;
        const toolArgs = params?.arguments ?? {};

        try {
          let output: any;

          switch (toolName) {
            case "reptar_inspect": {
              output = inspectProject(repoRoot, cwd, {
                dbPath: toolArgs.dbPath,
              });
              break;
            }

            case "reptar_schema": {
              const inspection = inspectProject(repoRoot, cwd);
              if (toolArgs.table) {
                const target = toolArgs.table.toLowerCase();
                const matched = inspection.tables.find(
                  (t) => t.name.toLowerCase() === target
                );
                output = matched ?? {
                  error: `Table '${toolArgs.table}' not found in database.`,
                };
              } else {
                output = { tables: inspection.tables };
              }
              break;
            }

            case "reptar_routes": {
              const inspection = inspectProject(repoRoot, cwd);
              let { routes } = inspection;
              if (toolArgs.aiOnly) {
                routes = routes.filter((r) => r.isAiTool);
              }
              output = { routes };
              break;
            }

            case "reptar_db_status": {
              const inspection = inspectProject(repoRoot, cwd);
              output = {
                database: inspection.database,
                migrations: inspection.migrations,
              };
              break;
            }

            case "reptar_db_plan": {
              output = planMigrations(repoRoot, cwd);
              break;
            }

            case "reptar_check": {
              const inspection = inspectProject(repoRoot, cwd);
              const plan = planMigrations(repoRoot, cwd);
              const issues: string[] = [];

              if (inspection.migrations.pending.length > 0) {
                issues.push(
                  `${inspection.migrations.pending.length} pending migration(s): ${inspection.migrations.pending.join(", ")}`
                );
              }

              if (plan.status === "drift_detected") {
                issues.push(
                  `Schema drift detected (${plan.changes.length} change(s) needed): run 'bun reptar db plan' or 'bun reptar db generate'`
                );
              }

              if (inspection.generated.status !== "up-to-date") {
                issues.push(
                  "RPC contracts are stale or missing: run 'bun reptar codegen'"
                );
              }

              output = {
                issues,
                passed: issues.length === 0,
                summary:
                  issues.length === 0
                    ? "✓ Reptar project is healthy with no schema drift or pending migrations."
                    : `Found ${issues.length} issue(s) needing attention.`,
              };
              break;
            }

            case "reptar_doctor": {
              output = doctorProject(repoRoot, cwd);
              break;
            }

            default: {
              sendResponse({
                error: {
                  code: -32_601,
                  message: `Method '${toolName}' not found`,
                },
                id,
                jsonrpc: "2.0",
              });
              return;
            }
          }

          sendResponse({
            id,
            jsonrpc: "2.0",
            result: {
              content: [
                {
                  text:
                    typeof output === "string"
                      ? output
                      : JSON.stringify(output, null, 2),
                  type: "text",
                },
              ],
            },
          });
        } catch (error: any) {
          sendResponse({
            id,
            jsonrpc: "2.0",
            result: {
              content: [
                {
                  text: `Error executing ${toolName}: ${error?.message ?? String(error)}`,
                  type: "text",
                },
              ],
              isError: true,
            },
          });
        }
        break;
      }

      default: {
        sendResponse({
          error: {
            code: -32_601,
            message: `Method '${method}' not found`,
          },
          id,
          jsonrpc: "2.0",
        });
        break;
      }
    }
  });
};

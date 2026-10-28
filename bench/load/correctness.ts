import { sha256 } from "./response-witness";
import type { Fixtures, Scenario } from "./scenarios";

const requireValue = (condition: boolean, message: string): void => {
  if (!condition) {
    throw new Error(`Benchmark correctness failure: ${message}`);
  }
};

const object = (value: unknown): Record<string, unknown> => {
  requireValue(
    typeof value === "object" && value !== null && !Array.isArray(value),
    "expected JSON object"
  );
  return value as Record<string, unknown>;
};

const stable = (value: unknown, depth = 0): unknown => {
  requireValue(depth <= 64, "response nesting budget");
  if (Array.isArray(value)) {
    return value.map((item) => stable(item, depth + 1));
  }
  if (value !== null && typeof value === "object") {
    return Object.fromEntries(
      Object.entries(value)
        .toSorted(([left], [right]) => left.localeCompare(right))
        .map(([key, item]) => [key, stable(item, depth + 1)])
    );
  }
  return value;
};

const signature = (value: unknown): string =>
  sha256(JSON.stringify(stable(value)));
const omit = (
  value: Record<string, unknown>,
  keys: string[]
): Record<string, unknown> =>
  Object.fromEntries(
    Object.entries(value).filter(([key]) => !keys.includes(key))
  );

const task = (value: unknown, projectId: string): Record<string, unknown> => {
  const result = object(value);
  requireValue(
    typeof result.id === "string" && result.id.length > 0,
    "task identity"
  );
  requireValue(result.projectId === projectId, "task project");
  requireValue(
    typeof result.title === "string" &&
      result.title.length > 0 &&
      typeof result.priority === "number",
    "task shape"
  );
  return result;
};

type Validator = (
  value: unknown,
  fixtures: Fixtures,
  scenario: Scenario
) => unknown;

const validateInput: Validator = (value, _fixtures, scenario) => {
  const result = object(value);
  const input = object(JSON.parse(scenario.body ?? "null"));
  requireValue(
    result.ok === true &&
      result.title === input.title &&
      result.priority === input.priority &&
      result.status === input.status,
    "validation result"
  );
  return result;
};

const validateWrite: Validator = (value, fixtures, scenario) => {
  const result = task(value, fixtures.sample.projectId);
  const input = object(JSON.parse(scenario.body ?? "null"));
  requireValue(
    result.title === input.title && result.priority === input.priority,
    "persisted write fields"
  );
  if (scenario.name === "update-task") {
    requireValue(result.id === fixtures.sample.taskId, "updated task identity");
    return omit(result, ["updatedAt"]);
  }
  return omit(result, ["id", "createdAt", "updatedAt"]);
};

const validateList: Validator = (value, fixtures, scenario) => {
  const result = object(value);
  requireValue(Array.isArray(result.data), "list data array");
  const rows = result.data as unknown[];
  requireValue(
    rows.length <= 20 && "nextCursor" in result,
    "bounded list shape"
  );
  for (const row of rows) {
    if (scenario.name === "task-list") {
      const item = task(row, fixtures.sample.projectId);
      requireValue(
        item.status === fixtures.sample.status,
        "task-list status filter"
      );
    } else {
      const item = object(row);
      requireValue(
        item.taskId === fixtures.sample.taskId && typeof item.body === "string",
        "comment identity/body"
      );
    }
  }
  return result;
};

const validateLogin: Validator = (value, fixtures) => {
  const result = object(value);
  requireValue(
    result.tokenType === "Bearer" && typeof result.token === "string",
    "login token shape"
  );
  requireValue(
    typeof result.expiresAt === "number" &&
      result.expiresAt > Date.now() / 1000,
    "login expiry"
  );
  const parts = String(result.token).split(".");
  const expected = fixtures.login.jwt.split(".");
  requireValue(
    parts.length === 3 && parts.every(Boolean) && expected.length === 3,
    "JWT segments"
  );
  const claims = object(
    JSON.parse(Buffer.from(parts[1] ?? "", "base64url").toString())
  );
  const expectedClaims = object(
    JSON.parse(Buffer.from(expected[1] ?? "", "base64url").toString())
  );
  const normalized = omit(claims, ["exp", "iat", "nbf"]);
  requireValue(
    signature(normalized) ===
      signature(omit(expectedClaims, ["exp", "iat", "nbf"])),
    "login claims"
  );
  // Never retain the bearer token or password in result artifacts.
  return normalized;
};

const validators: Record<string, Validator> = {
  "create-task": validateWrite,
  json: (value) => {
    requireValue(
      signature(value) ===
        signature({
          message: "Hello, World!",
          nested: { a: 1, b: 2 },
          ok: true,
          tags: ["bench", "reptar", "http"],
          version: 1,
        }),
      "fixed JSON payload"
    );
    return value;
  },
  login: validateLogin,
  me: (value, fixtures) => {
    const result = object(value);
    requireValue(
      result.email === fixtures.login.email &&
        typeof result.id === "string" &&
        typeof result.name === "string",
      "authenticated user"
    );
    return result;
  },
  "router-params": (value, fixtures) => {
    const result = object(value);
    const { sample } = fixtures;
    requireValue(
      result.orgId === sample.orgId &&
        result.projectId === sample.projectId &&
        result.taskId === sample.taskId,
      "decoded route parameters"
    );
    return result;
  },
  "task-comments": validateList,
  "task-detail": (value, fixtures) => {
    const result = task(value, fixtures.sample.projectId);
    requireValue(
      result.id === fixtures.sample.taskId,
      "selected task identity"
    );
    return result;
  },
  "task-list": validateList,
  "update-task": validateWrite,
  validate: validateInput,
};

/** Sampled semantics only: not validation of every load-generator response or JWT signatures. */
export const validateResponse = (
  scenario: Scenario,
  fixtures: Fixtures,
  status: number,
  decoded: Buffer
): string => {
  const expectedStatus = ["validate", "create-task"].includes(scenario.name)
    ? 201
    : 200;
  requireValue(
    status === expectedStatus,
    `${scenario.name} status ${status}, expected ${expectedStatus}`
  );
  if (scenario.name === "plaintext") {
    requireValue(decoded.toString() === "Hello, World!", "plaintext payload");
    return sha256(decoded);
  }
  const validator = validators[scenario.name];
  if (!validator) {
    throw new Error(`No correctness contract for scenario ${scenario.name}`);
  }
  return signature(
    validator(JSON.parse(decoded.toString()), fixtures, scenario)
  );
};

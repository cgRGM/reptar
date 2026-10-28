import { expect, test } from "bun:test";

import { validateResponse } from "./correctness";
import type { Fixtures, Scenario } from "./scenarios";

const fixtures: Fixtures = {
  login: { email: "synthetic@example.test", jwt: "unused", password: "unused" },
  orgIds: ["org"],
  projectIds: ["project"],
  sample: {
    assigneeId: "assignee",
    orgId: "org",
    projectId: "project",
    secondTaskId: "second",
    status: "open",
    taskId: "task",
    userId: "user",
  },
  taskIds: ["task", "second"],
};
const scenario = (name: string): Scenario => ({
  method: "GET",
  name,
  path: "/",
  tier: "test",
});

test("HTTP 200 with an empty, incomplete or wrong fixed JSON body is not correctness", () => {
  const item = scenario("json");
  expect(() =>
    validateResponse(item, fixtures, 200, Buffer.from("{}"))
  ).toThrow("fixed JSON payload");
  expect(() =>
    validateResponse(item, fixtures, 200, Buffer.from("null"))
  ).toThrow();
  expect(() =>
    validateResponse(item, fixtures, 500, Buffer.from("{}"))
  ).toThrow("status");
  const valid = {
    message: "Hello, World!",
    nested: { a: 1, b: 2 },
    ok: true,
    tags: ["bench", "reptar", "http"],
    version: 1,
  };
  expect(
    validateResponse(item, fixtures, 200, Buffer.from(JSON.stringify(valid)))
  ).toHaveLength(64);
});

test("wrong route/project/record identities cannot pass sampled correctness", () => {
  expect(() =>
    validateResponse(
      scenario("router-params"),
      fixtures,
      200,
      Buffer.from(
        JSON.stringify({ orgId: "wrong", projectId: "project", taskId: "task" })
      )
    )
  ).toThrow("route parameters");
  expect(() =>
    validateResponse(
      scenario("task-detail"),
      fixtures,
      200,
      Buffer.from(
        JSON.stringify({
          id: "wrong",
          priority: 1,
          projectId: "project",
          title: "not the selected task",
        })
      )
    )
  ).toThrow("selected task");
});

test("missing validators are explicit failures, not unchecked performance rows", () => {
  expect(() =>
    validateResponse(scenario("unknown"), fixtures, 200, Buffer.from("{}"))
  ).toThrow("No correctness contract");
});

test("a create response may be correct but variable byte work remains a comparison concern", () => {
  const item = {
    ...scenario("create-task"),
    body: JSON.stringify({ priority: 1, title: "written" }),
  };
  const first = {
    createdAt: "first",
    id: "generated-1",
    priority: 1,
    projectId: "project",
    title: "written",
    updatedAt: "first",
  };
  const second = { ...first, id: "generated-100000", updatedAt: "later" };
  expect(
    validateResponse(item, fixtures, 201, Buffer.from(JSON.stringify(first)))
  ).toBe(
    validateResponse(item, fixtures, 201, Buffer.from(JSON.stringify(second)))
  );
});

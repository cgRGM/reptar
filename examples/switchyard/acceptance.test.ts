import { beforeAll, expect, test } from "bun:test";

import { createClient } from "@reptar/client";

import { bucket } from "./bucketing-reference";
/* eslint-disable unicorn/no-await-expression-member, no-await-in-loop -- Sequential HTTP assertions keep protocol scenarios explicit. */
import { required, sessionCookie } from "./required";
import type { AppType } from "./rpc/app-type";
import type {
  AuditRow,
  Evaluation,
  FlagRow,
  ProjectRow,
  VersionRow,
} from "./rpc/types";

const base = process.env.SWITCHYARD_URL ?? "http://127.0.0.1:8795";
let cookie = "";
let outsider = "";
let project: ProjectRow;
const suffix = crypto.randomUUID();
const path = () => `/api/projects/${project.id}/flags/production/checkout`;
const evaluation = (subject = "customer-42") =>
  `/api/evaluate/${project.id}/production/checkout?subject=${encodeURIComponent(subject)}`;

const request = async (
  url: string,
  method = "GET",
  body?: unknown,
  session = cookie,
  headers: Record<string, string> = {}
) => {
  const init: RequestInit = {
    body: body === undefined ? undefined : JSON.stringify(body),
    headers: {
      "content-type": "application/json",
      cookie: session,
      ...headers,
    },
    method,
    signal: AbortSignal.timeout(10_000),
  };
  return await fetch(new Request(base + url, init));
};
const signup = async (label: string) => {
  const response = await request(
    "/api/auth/signup",
    "POST",
    {
      email: `${label}-${suffix}@example.test`,
      name: label,
      password: "Switchyard-test-2026!",
    },
    ""
  );
  expect(response.status).toBe(201);
  expect((await response.json()).email).toContain(label);
  return sessionCookie(response);
};

beforeAll(async () => {
  cookie = await signup("operator");
  outsider = await signup("outsider");
  const client = createClient<AppType>(base, { headers: { cookie } });
  const response = await client.api.projects.$post({
    json: { name: "Switchyard acceptance" },
  });
  expect(response.status).toBe(201);
  project = await response.json();
  expect(project.id).toStartWith("prj_");
  const { flags } = client.api.projects[":projectId"];
  const created = await flags.$post({
    json: { environment: "production", key: "checkout" },
    param: { projectId: project.id },
  });
  expect(created.status).toBe(201);
  expect(await created.json()).toMatchObject({ revision: 0, version: 0 });
});

test("native auth, tenant isolation, duplicate constraints, and validation", async () => {
  expect((await request("/api/projects", "GET", undefined, "")).status).toBe(
    401
  );
  for (const url of [
    `${path()}/versions`,
    `/api/projects/${project.id}/flags`,
    `/api/projects/${project.id}/audit`,
    evaluation(),
  ]) {
    expect((await request(url, "GET", undefined, outsider)).status).toBe(404);
  }
  expect(
    (
      await request(
        `${path()}/draft`,
        "PUT",
        { enabled: true, expectedRevision: 0, rollout: 50, targets: [] },
        outsider
      )
    ).status
  ).toBe(404);
  expect(
    (
      await request(
        `${path()}/publish`,
        "POST",
        { expectedRevision: 0, expectedVersion: 0 },
        outsider
      )
    ).status
  ).toBe(404);
  expect(
    (
      await request(`/api/projects/${project.id}/flags`, "POST", {
        environment: "production",
        key: "checkout",
      })
    ).status
  ).toBe(409);
  expect(
    (
      await request(`/api/projects/${project.id}/flags`, "POST", {
        environment: "production",
        key: "BAD KEY",
      })
    ).status
  ).toBe(400);
  expect(
    (
      await request(`${path()}/draft`, "PUT", {
        enabled: true,
        expectedRevision: 0,
        rollout: 101,
        targets: [],
      })
    ).status
  ).toBe(400);
  expect(
    (
      await request(`${path()}/draft`, "PUT", {
        enabled: true,
        expectedRevision: 0,
        rollout: 50,
        targets: null,
      })
    ).status
  ).toBe(400);
  expect((await request(evaluation())).status).toBe(404);
});

test("concurrent publish has one winner, immutable versions and matching audit", async () => {
  const draft = await request(`${path()}/draft`, "PUT", {
    enabled: true,
    expectedRevision: 0,
    rollout: 37,
    targets: ["vip-α"],
  });
  expect(draft.status).toBe(200);
  expect(await draft.json()).toMatchObject({ revision: 1, rollout: 37 });
  const responses = await Promise.all(
    Array.from(
      { length: 24 },
      async () =>
        await request(`${path()}/publish`, "POST", {
          expectedRevision: 1,
          expectedVersion: 0,
        })
    )
  );
  expect(responses.filter((r) => r.status === 201)).toHaveLength(1);
  expect(responses.filter((r) => r.status === 409)).toHaveLength(23);
  const versions: VersionRow[] = await (
    await request(`${path()}/versions`)
  ).json();
  const audit: AuditRow[] = await (
    await request(`/api/projects/${project.id}/audit`)
  ).json();
  expect(versions).toHaveLength(1);
  expect(audit).toHaveLength(1);
  expect(versions[0]).toMatchObject({ rollout: 37, version: 1 });
  const published = required(versions[0], "published version");
  expect(JSON.parse(published.targets)).toEqual(["vip-α"]);
  expect(audit[0]).toMatchObject({ actorId: published.actorId, version: 1 });
  const client = createClient<AppType>(base, { headers: { cookie } });
  const response = await client.api.evaluate[":projectId"][":environment"][
    ":key"
  ].$post({
    json: { subject: "vip-α" },
    param: {
      environment: "production",
      key: "checkout",
      projectId: project.id,
    },
  });
  expect(await response.json()).toMatchObject({
    enabled: true,
    reason: "targeted",
    version: 1,
  });
});

test("deterministic rollout, stable buckets, conditional GET and publish invalidation", async () => {
  const first = await request(evaluation());
  const original: Evaluation = await first.json();
  expect(original.enabled).toBe(original.bucket < 37);
  const tag = required(first.headers.get("etag"), "evaluation ETag");
  expect(first.headers.get("cache-control")).toBe("private, no-cache");
  for (const header of [tag, `W/${tag}`, `"other", ${tag}`, "*"]) {
    const cached = await request(evaluation(), "GET", undefined, cookie, {
      "if-none-match": header,
    });
    expect(cached.status).toBe(304);
    expect(await cached.text()).toBe("");
  }
  for (let round = 0; round < 5; round += 1) {
    expect(await (await request(evaluation())).json()).toEqual(original);
  }
  expect(
    (
      await request(`${path()}/draft`, "PUT", {
        enabled: true,
        expectedRevision: 1,
        rollout: 100,
        targets: [],
      })
    ).status
  ).toBe(200);
  // Draft changes must not leak into the live configuration.
  expect(await (await request(evaluation())).json()).toEqual(original);
  expect(
    (
      await request(`${path()}/publish`, "POST", {
        expectedRevision: 2,
        expectedVersion: 1,
      })
    ).status
  ).toBe(201);
  const fresh = await request(evaluation(), "GET", undefined, cookie, {
    "if-none-match": tag,
  });
  expect(fresh.status).toBe(200);
  expect(await fresh.json()).toMatchObject({
    bucket: original.bucket,
    enabled: true,
    version: 2,
  });
  expect(
    (
      await request(`${path()}/draft`, "PUT", {
        enabled: false,
        expectedRevision: 1,
        rollout: 100,
        targets: ["vip-α"],
      })
    ).status
  ).toBe(409);
  const versions: VersionRow[] = await (
    await request(`${path()}/versions`)
  ).json();
  expect(versions.map((v) => v.version)).toEqual([2, 1]);
  expect(required(versions[1], "original version").rollout).toBe(37);
});

test("disabled wins over allowlist and rollout; independent environments", async () => {
  expect(
    (
      await request(`${path()}/draft`, "PUT", {
        enabled: false,
        expectedRevision: 2,
        rollout: 100,
        targets: ["vip-α"],
      })
    ).status
  ).toBe(200);
  expect(
    (
      await request(`${path()}/publish`, "POST", {
        expectedRevision: 3,
        expectedVersion: 2,
      })
    ).status
  ).toBe(201);
  expect(await (await request(evaluation("vip-α"))).json()).toMatchObject({
    enabled: false,
    reason: "disabled",
    version: 3,
  });
  expect(
    (
      await request(`/api/projects/${project.id}/flags`, "POST", {
        environment: "staging",
        key: "checkout",
      })
    ).status
  ).toBe(201);
  expect(
    (await request(`/api/evaluate/${project.id}/staging/checkout?subject=test`))
      .status
  ).toBe(404);
  const flags: FlagRow[] = await (
    await request(`/api/projects/${project.id}/flags`)
  ).json();
  expect(flags).toHaveLength(2);
});

test("runtime buckets match an independent BigInt implementation, including Unicode", async () => {
  expect(
    ["customer-42", "vip-α", "a:b/c", "🚂", ""].map((subject) =>
      bucket(["project", "production", "checkout", subject])
    )
  ).toEqual([50, 67, 24, 95, 82]);
  await Promise.all(
    [
      "customer-42",
      "vip-α",
      "a:b/c",
      "🚂",
      ...Array.from({ length: 100 }, (_, index) => `subject-${index}`),
    ].map(async (subject) => {
      const response = await request(evaluation(subject));
      expect(response.status).toBe(200);
      const result: Evaluation = await response.json();
      expect(result.bucket).toBe(
        bucket([project.id, "production", "checkout", subject])
      );
    })
  );
});

test("signin, nested session JSON, duplicate signup, and signout", async () => {
  const credentials = {
    email: `operator-${suffix}@example.test`,
    password: "Switchyard-test-2026!",
  };
  const signin = await request("/api/auth/signin", "POST", credentials, "");
  expect(signin.status).toBe(200);
  const session = sessionCookie(signin);
  const current = await request("/api/auth/session", "GET", undefined, session);
  expect(await current.json()).toMatchObject({
    user: { email: credentials.email, name: "operator", role: "user" },
  });
  expect(
    (
      await request(
        "/api/auth/signup",
        "POST",
        { ...credentials, name: "duplicate" },
        ""
      )
    ).status
  ).toBe(409);
  expect(
    (await request("/api/auth/signout", "POST", undefined, session)).status
  ).toBe(204);
  expect(
    await (await request("/api/auth/session", "GET", undefined, session)).json()
  ).toEqual({ user: null });
});

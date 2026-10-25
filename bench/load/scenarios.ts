export interface Scenario {
  /** Stable identifier used in result files. */
  name: string;
  /** Calibration tier or realistic endpoint. */
  tier: string;
  method: "GET" | "POST" | "PUT";
  path: string;
  body?: string;
  headers?: Record<string, string>;
  /** Per-scenario connection override (e.g. CPU-heavy login). */
  connections?: number;
  /** Optional POST path to reset a benchmark collector before warmup. */
  resetPath?: string;
  /** Optional GET path to fetch a breakdown after the reps. */
  breakdownPath?: string;
  /** Skip the warmup for destructive/write scenarios if needed. */
  warmup?: boolean;
}

export interface Fixtures {
  login: {
    email: string;
    password: string;
    jwt: string;
  };
  sample: {
    orgId: string;
    projectId: string;
    taskId: string;
    secondTaskId: string;
    status: string;
    userId: string;
    assigneeId: string;
  };
  taskIds: string[];
  projectIds: string[];
  orgIds: string[];
}

const jsonHeaders = { "content-type": "application/json" };

export const buildScenarios = (fixtures: Fixtures): Scenario[] => {
  const { sample, login } = fixtures;
  const bearer = { authorization: `Bearer ${login.jwt}` };

  return [
    {
      method: "GET",
      name: "plaintext",
      path: "/__bench/plaintext",
      tier: "T0",
    },
    {
      method: "GET",
      name: "json",
      path: "/__bench/json",
      tier: "T1",
    },
    {
      method: "GET",
      name: "router-params",
      path: `/__bench/router/${sample.orgId}/${sample.projectId}/${sample.taskId}`,
      tier: "T2",
    },
    {
      body: JSON.stringify({
        assigneeId: sample.assigneeId,
        description:
          "A valid description for the validation tier. It is long enough to exercise the parser but short enough to stay realistic.",
        priority: 2,
        status: "open",
        title: "A valid benchmark task title",
      }),
      headers: jsonHeaders,
      method: "POST",
      name: "validate",
      path: "/__bench/validate",
      tier: "T3",
    },
    {
      method: "GET",
      name: "task-detail",
      path: `/api/v1/tasks/${sample.taskId}`,
      tier: "T4",
    },
    {
      method: "GET",
      name: "task-list",
      path: `/api/v1/projects/${sample.projectId}/tasks?status=${sample.status}&limit=20`,
      tier: "T5",
    },
    {
      method: "GET",
      name: "task-comments",
      path: `/api/v1/tasks/${sample.taskId}/comments?limit=20`,
      tier: "T6",
    },
    {
      body: JSON.stringify({
        assigneeId: sample.assigneeId,
        description: "Created by the benchmark write tier.",
        priority: 2,
        status: "open",
        title: "Benchmark write task",
      }),
      breakdownPath: "/__bench/breakdown",
      headers: { ...jsonHeaders, ...bearer },
      method: "POST",
      name: "create-task",
      path: `/api/v1/projects/${sample.projectId}/tasks`,
      resetPath: "/__bench/breakdown/reset",
      tier: "T7",
    },
    {
      headers: bearer,
      method: "GET",
      name: "me",
      path: "/api/v1/me",
      tier: "T8",
    },
    {
      body: JSON.stringify({
        email: login.email,
        password: login.password,
      }),
      connections: 10,
      headers: jsonHeaders,
      method: "POST",
      name: "login",
      path: "/api/v1/auth/login",
      tier: "T9",
    },
    {
      body: JSON.stringify({
        priority: 3,
        title: "Updated by the benchmark",
      }),
      headers: { ...jsonHeaders, ...bearer },
      method: "PUT",
      name: "update-task",
      path: `/api/v1/tasks/${sample.taskId}`,
      tier: "T10",
    },
  ];
};

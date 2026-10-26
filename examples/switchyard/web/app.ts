import { createClient } from "@reptar/client";

import { required } from "../required";
import type { AppType } from "../rpc/app-type";
import type { FlagRow, ProjectRow } from "../rpc/types";

const api = createClient<AppType>(location.origin);
const element = <T extends HTMLElement>(id: string) => {
  const node = document.querySelector<T>(`#${id}`);
  if (!node) {
    throw new Error(`Missing element: ${id}`);
  }
  return node;
};
const input = (id: string) => element<HTMLInputElement>(id);
const select = (id: string) => element<HTMLSelectElement>(id);
let projects: ProjectRow[] = [];
let flags: FlagRow[] = [];
let selected: FlagRow | undefined;
const projectRoutes = () => api.api.projects[":projectId"];
const params = () => {
  if (!selected) {
    throw new Error("Select a flag first.");
  }
  return {
    environment: selected.environment,
    key: selected.key,
    projectId: selected.projectId,
  };
};
const message = (text: string) => {
  element("message").textContent = text;
};
const read = async <T>(
  response:
    | (Response & { ok: true; json: () => Promise<T> })
    | (Response & { ok: false; json: () => Promise<unknown> })
): Promise<T> => {
  if (!response.ok) {
    const body: unknown = await response.json();
    throw new Error(`HTTP ${response.status}: ${JSON.stringify(body)}`);
  }
  return await response.json();
};
const act = (work: () => Promise<void>) => async (event: Event) => {
  event.preventDefault();
  message("");
  try {
    await work();
  } catch (error) {
    message(error instanceof Error ? error.message : String(error));
  }
};
const options = (
  id: string,
  values: { value: string; label: string }[],
  preferred?: string
) => {
  const control = select(id);
  control.replaceChildren(
    ...values.map((value) => new Option(value.label, value.value))
  );
  if (preferred && values.some((value) => value.value === preferred)) {
    control.value = preferred;
  }
};
const paintBuckets = () => {
  const value = Number(input("rollout").value);
  element("rollout-value").textContent = `${value}%`;
  element("buckets").replaceChildren(
    ...Array.from({ length: 100 }, (_, index) => {
      const lane = document.createElement("i");
      lane.className = index < value ? "on" : "";
      return lane;
    })
  );
};
const chooseFlag = () => {
  selected = flags.find(
    (flag) => `${flag.environment}/${flag.key}` === select("flag").value
  );
  input("enabled").checked = selected?.enabled === 1;
  input("rollout").value = String(selected?.rollout ?? 0);
  element<HTMLTextAreaElement>("targets").value = selected
    ? (JSON.parse(selected.targets) as string[]).join("\n")
    : "";
  element("flag-meta").textContent = selected
    ? `DRAFT r${selected.revision} / LIVE v${selected.version} / ${selected.environment.toUpperCase()}`
    : "No signal selected";
  paintBuckets();
};
const loadLedger = async () => {
  if (!selected) {
    element("ledger").replaceChildren();
    return;
  }
  const rows = await read(
    await projectRoutes().flags[":environment"][":key"].versions.$get({
      param: params(),
    })
  );
  element("ledger").replaceChildren(
    ...rows.map((row) => {
      const item = document.createElement("li");
      const version = document.createElement("strong");
      version.textContent = `v${row.version} / ${row.enabled ? "ON" : "OFF"} / ${row.rollout}%`;
      const detail = document.createElement("div");
      detail.textContent = `${new Date(row.createdAt).toLocaleString()} · ${row.actorId}`;
      item.append(version, detail);
      return item;
    })
  );
};
const loadFlags = async (preferred = select("flag").value) => {
  const projectId = select("project").value;
  flags = projectId
    ? await read(await projectRoutes().flags.$get({ param: { projectId } }))
    : [];
  options(
    "flag",
    flags.map((flag) => ({
      label: `${flag.key} / ${flag.environment}`,
      value: `${flag.environment}/${flag.key}`,
    })),
    preferred
  );
  chooseFlag();
  await loadLedger();
};
const loadProjects = async (preferred = select("project").value) => {
  projects = await read(await api.api.projects.$get());
  options(
    "project",
    projects.map((project) => ({ label: project.name, value: project.id })),
    preferred
  );
  await loadFlags();
};
const session = async () => {
  const current = await read(await api.api.auth.session.$get());
  element("auth-panel").hidden = Boolean(current.user);
  element("workspace").hidden = !current.user;
  if (current.user) {
    element("operator").textContent = `${current.user.name}'s switchyard`;
    await loadProjects();
  }
};
element("auth-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const action = (event as SubmitEvent).submitter?.getAttribute("value");
  await act(async () => {
    const json = {
      email: input("email").value,
      name: input("name").value,
      password: input("password").value,
    };
    const response =
      action === "signup"
        ? await api.api.auth.signup.$post({ json })
        : await api.api.auth.signin.$post({ json });
    await read(response);
    input("password").value = "";
    await session();
  })(event);
});
element("signout").addEventListener(
  "click",
  act(async () => {
    const response = await api.api.auth.signout.$post();
    if (!response.ok) {
      throw new Error("Sign out failed.");
    }
    await session();
  })
);
element("project-form").addEventListener(
  "submit",
  act(async () => {
    const project = await read(
      await api.api.projects.$post({
        json: { name: input("project-name").value },
      })
    );
    await loadProjects(project.id);
  })
);
element("project").addEventListener(
  "change",
  act(async () => {
    await loadFlags();
  })
);
element("flag-form").addEventListener(
  "submit",
  act(async () => {
    const response = await read(
      await projectRoutes().flags.$post({
        json: {
          environment: select("environment").value,
          key: input("flag-key").value,
        },
        param: { projectId: select("project").value },
      })
    );
    await loadFlags(`${response.environment}/${response.key}`);
  })
);
element("flag").addEventListener(
  "change",
  act(async () => {
    chooseFlag();
    await loadLedger();
  })
);
input("rollout").addEventListener("input", paintBuckets);
element("draft-form").addEventListener(
  "submit",
  act(async () => {
    const param = params();
    await read(
      await projectRoutes().flags[":environment"][":key"].draft.$put({
        json: {
          enabled: input("enabled").checked,
          expectedRevision: required(selected, "selected flag").revision,
          rollout: Number(input("rollout").value),
          targets: element<HTMLTextAreaElement>("targets")
            .value.split("\n")
            .map((value) => value.trim())
            .filter(Boolean),
        },
        param,
      })
    );
    await loadFlags();
    message("Draft saved. Live evaluation is unchanged until you publish.");
  })
);
element("publish").addEventListener(
  "click",
  act(async () => {
    const param = params();
    await read(
      await projectRoutes().flags[":environment"][":key"].publish.$post({
        json: {
          expectedRevision: required(selected, "selected flag").revision,
          expectedVersion: required(selected, "selected flag").version,
        },
        param,
      })
    );
    await loadFlags();
    message("Immutable version published with its audit record.");
  })
);
element("evaluate-form").addEventListener(
  "submit",
  act(async () => {
    const result = await read(
      await api.api.evaluate[":projectId"][":environment"][":key"].$get({
        param: params(),
        query: { subject: input("subject").value },
      })
    );
    element("decision").textContent = result.enabled
      ? "SIGNAL ON ↗"
      : "SIGNAL OFF —";
    element("decision-meta").textContent =
      `BUCKET ${String(result.bucket).padStart(2, "0")} / VERSION ${result.version} / ${result.reason.toUpperCase()}`;
  })
);
element("refresh").addEventListener(
  "click",
  act(async () => {
    await loadFlags();
  })
);
try {
  await read(await api.health.$get());
  element("connection").textContent = "NATIVE API ONLINE";
  await session();
} catch (error) {
  element("connection").textContent = "CONNECTION FAILED";
  message(error instanceof Error ? error.message : String(error));
}
paintBuckets();

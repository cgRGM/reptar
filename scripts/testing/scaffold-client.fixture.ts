// Copied into the external scaffold's client package before typechecking.
import { createClient } from "./index";
import type { AppType, Client } from "./index";

type ScaffoldClient = Client<AppType>;

const authenticate = async (
  client: ScaffoldClient,
  baseUrl: string
): Promise<ScaffoldClient> => {
  const email = `scaffold-${Date.now()}@example.com`;
  const signup = await client.api.auth["sign-up"].email.$post({
    json: { email, name: "Scaffold User", password: "password123" },
  });
  if (signup.status !== 201 || !signup.ok) {
    throw new Error(
      `Signup should be 201, got ${signup.status}: ${await signup.text()}`
    );
  }
  const user = await signup.json();
  if (user.user.email !== email || !user.token) {
    throw new Error("Signup body does not match its generated contract.");
  }
  const setCookie = signup.headers.get("set-cookie") ?? "";
  const [cookie] = setCookie.split(";");
  if (
    !cookie?.startsWith("better-auth.session_token=") ||
    !setCookie.toLowerCase().includes("secure")
  ) {
    throw new Error(
      "Published Release signup must set a secure session cookie."
    );
  }
  // Explicit cookie forwarding models an HTTP client, not browser cookie/TLS policy.
  const signedIn = createClient<AppType>(baseUrl, { headers: { cookie } });
  const me = await signedIn.api.me.$get();
  const profile = await me.json();
  if (
    me.status !== 200 ||
    !profile.authenticated ||
    profile.user?.email !== email
  ) {
    throw new Error(
      "Published session did not survive a subsequent generated-client request."
    );
  }
  return signedIn;
};

const exerciseItems = async (signedIn: ScaffoldClient): Promise<void> => {
  const invalid = await signedIn.api.items.$post({
    json: { content: "", title: "" },
  });
  if (invalid.status !== 400) {
    throw new Error(`Validation should return 400, got ${invalid.status}`);
  }
  const issues = await invalid.json();
  if (issues.issues.length === 0) {
    throw new Error(
      "Validation response does not match its generated contract."
    );
  }
  const created = await signedIn.api.items.$post({
    json: {
      content: "Created through the generated client",
      title: "External scaffold item",
    },
  });
  if (created.status !== 201) {
    throw new Error(`Create should be 201, got ${created.status}`);
  }
  const item = await created.json();
  if (!item.id || item.title !== "External scaffold item") {
    throw new Error("Created item does not match its generated contract.");
  }
  const list = await signedIn.api.items.$get();
  const entries = await list.json();
  if (list.status !== 200 || !entries.some((entry) => entry.id === item.id)) {
    throw new Error("Created item is missing from the generated-client list.");
  }
  const deleted = await signedIn.api.items[":id"].$delete({
    param: { id: item.id },
  });
  if (deleted.status !== 200) {
    throw new Error(`Delete should return 200, got ${deleted.status}`);
  }
  const acknowledgement = await deleted.json();
  if (!acknowledgement.success) {
    throw new Error("Delete did not return the declared success response.");
  }
  const missing = await signedIn.api.items[":id"].$delete({
    param: { id: item.id },
  });
  if (missing.status !== 404) {
    throw new Error(`Repeated delete should return 404, got ${missing.status}`);
  }
  const missingBody = await missing.json();
  if (missingBody.code !== "not_found") {
    throw new Error("Repeated delete must return its declared 404 error.");
  }
  const finalList = await signedIn.api.items.$get();
  const remaining = await finalList.json();
  if (remaining.some((entry) => entry.id === item.id)) {
    throw new Error("Deleted item remained visible.");
  }
};

export const runScaffoldContractSmoke = async (
  baseUrl: string
): Promise<void> => {
  const client = createClient<AppType>(baseUrl);
  const denied = await client.api.items.$post({
    json: { content: "Unauthorized", title: "Unauthorized" },
  });
  if (denied.status !== 401 || denied.ok) {
    throw new Error(`Anonymous create should be 401, got ${denied.status}`);
  }
  const deniedBody = await denied.json();
  if (deniedBody.code !== "unauthorized") {
    throw new Error("Unauthorized body does not match its generated contract.");
  }
  const signedIn = await authenticate(client, baseUrl);
  await exerciseItems(signedIn);
  console.log(
    "  ✓ published AOT generated-client auth, validation, CRUD, and error contracts"
  );
};

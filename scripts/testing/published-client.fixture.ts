import type { AppType } from "./app-type";
// Copied beside contracts emitted by the published Native AOT probe itself.
import { createClient } from "./client";

export const runPublishedContractProbe = async (
  baseUrl: string
): Promise<void> => {
  const client = createClient<AppType>(baseUrl);
  const subject = "space + &= snowman ☃ / : *";
  const key = "key +&: snowman ☃";
  const response = await client.query[":key"].$get({
    param: { key },
    query: { enabled: false, subject },
  });
  if (response.status !== 200) {
    throw new Error(`Generated query request returned ${response.status}`);
  }
  const body = await response.json();
  if (
    body.key !== key ||
    body.subject !== subject ||
    body.enabled !== "false"
  ) {
    throw new Error(
      `Generated query request changed meaning: ${JSON.stringify(body)}`
    );
  }
  const empty = await client.empty["204"].$get();
  const reset = await client.empty["205"].$get();
  const cached = await client.empty["304"].$get();
  if (empty.status !== 204 || reset.status !== 205 || cached.status !== 304) {
    throw new Error("Generated bodyless response statuses changed.");
  }
  for (const result of [empty, reset, cached]) {
    // eslint-disable-next-line no-await-in-loop -- exercise each real body stream.
    const text = await result.text();
    if (text !== "") {
      throw new Error("Generated bodyless response unexpectedly had a body.");
    }
  }
};

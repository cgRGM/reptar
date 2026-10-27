import { chromium } from "playwright";

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { height: 1200, width: 1440 } });
const failures: string[] = [];
page.on("pageerror", (error) => failures.push(error.message));
try {
  await page.goto(process.env.SWITCHYARD_WEB_URL ?? "http://127.0.0.1:8796/");
  await page.getByText("NATIVE API ONLINE").waitFor();
  await page
    .getByLabel("Email", { exact: true })
    .fill(`browser-${crypto.randomUUID()}@example.test`);
  await page
    .getByLabel("Password", { exact: true })
    .fill("Switchyard-browser-2026!");
  await page.getByRole("button", { name: "Create account" }).click();
  await page
    .getByLabel("New project", { exact: true })
    .fill("Browser field test");
  await page.getByRole("button", { exact: true, name: "Create" }).click();
  await page.locator("#project option").first().waitFor({ state: "attached" });
  await page.getByLabel("Flag key", { exact: true }).fill("new-checkout");
  await page.getByRole("button", { name: "Add signal +" }).click();
  await page.getByText("DRAFT r0 / LIVE v0 / DEVELOPMENT").waitFor();
  await page.getByLabel("Enable this flag").check();
  await page.getByLabel("Percentage rollout").fill("100");
  await page.getByRole("button", { name: "Save draft" }).click();
  await page
    .getByText("Draft saved. Live evaluation is unchanged until you publish.")
    .waitFor();
  await page.getByRole("button", { name: "Publish version ↗" }).click();
  await page
    .getByText("Immutable version published with its audit record.")
    .waitFor();
  const evaluation = page.waitForRequest((request) =>
    new URL(request.url()).pathname.startsWith("/api/evaluate/")
  );
  await page.getByRole("button", { exact: true, name: "Evaluate" }).click();
  const evaluationRequest = await evaluation;
  if (
    evaluationRequest.method() !== "GET" ||
    !new URL(evaluationRequest.url()).searchParams.has("subject")
  ) {
    throw new Error("Evaluation did not use the generated GET query contract");
  }
  await page.getByText("SIGNAL ON ↗", { exact: true }).waitFor();
  await page.screenshot({ fullPage: true, path: "browser-desktop.png" });
  await page.setViewportSize({ height: 844, width: 390 });
  await page.screenshot({ fullPage: true, path: "browser-mobile.png" });
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth > innerWidth
  );
  if (overflow) {
    throw new Error("Mobile viewport has horizontal overflow");
  }
  if (failures.length) {
    throw new AggregateError(failures, "Browser runtime errors");
  }
  process.stdout.write(
    "Browser signup → project → flag → draft → publish → evaluate: PASS (desktop + mobile)\n"
  );
} catch (error) {
  await page.screenshot({ fullPage: true, path: "browser-failure.png" });
  process.stderr.write(
    `${await page.locator("body").textContent()}\n${failures.join("\n")}\n`
  );
  throw error;
} finally {
  await browser.close();
}

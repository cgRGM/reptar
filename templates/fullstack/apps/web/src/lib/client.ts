import { createClient } from "@{{PROJECT_NAME}}/client";
import type { AppType } from "@{{PROJECT_NAME}}/client";

export const client = createClient<AppType>(import.meta.env.VITE_API_URL ?? "");

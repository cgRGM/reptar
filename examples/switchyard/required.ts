export const required = <T>(value: T | null | undefined, label: string): T => {
  if (value === null || value === undefined) {
    throw new Error(`Missing ${label}`);
  }
  return value;
};

export const sessionCookie = (response: Response): string =>
  required(
    required(response.headers.get("set-cookie"), "session cookie").split(
      ";"
    )[0],
    "cookie pair"
  );

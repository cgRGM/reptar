const encoder = new TextEncoder();

const bytesToBase64 = (bytes: Uint8Array): string => {
  let binary = "";

  for (const byte of bytes) {
    binary += String.fromCodePoint(byte);
  }

  return btoa(binary);
};

const base64ToBytes = (value: string): Uint8Array => {
  const binary = atob(value);
  const bytes = new Uint8Array(binary.length);

  for (let index = 0; index < binary.length; index += 1) {
    bytes[index] = binary.codePointAt(index) ?? 0;
  }

  return bytes;
};

const bytesToHex = (bytes: Uint8Array): string => {
  let hex = "";

  for (const byte of bytes) {
    hex += byte.toString(16).padStart(2, "0");
  }

  return hex;
};

const toBase64Url = (bytes: Uint8Array): string =>
  bytesToBase64(bytes)
    .replaceAll("+", "-")
    .replaceAll("/", "_")
    .replace(/[=]+$/u, "");

const derivePassword = async (
  password: string,
  salt: Uint8Array,
  iterations: number
): Promise<Uint8Array> => {
  const key = await crypto.subtle.importKey(
    "raw",
    encoder.encode(password),
    "PBKDF2",
    false,
    ["deriveBits"]
  );
  const bits = await crypto.subtle.deriveBits(
    {
      hash: "SHA-256",
      iterations,
      name: "PBKDF2",
      salt: salt as unknown as BufferSource,
    },
    key,
    256
  );

  return new Uint8Array(bits);
};

export const hashPassword = async (password: string): Promise<string> => {
  // workerd caps PBKDF2 at 100,000 iterations (higher counts throw
  // NotSupportedError in production even though local dev may allow them).
  const iterations = 100_000;
  const salt = crypto.getRandomValues(new Uint8Array(16));
  const hash = await derivePassword(password, salt, iterations);

  return `pbkdf2-sha256$${iterations}$${bytesToBase64(salt)}$${bytesToBase64(hash)}`;
};

export const verifyPassword = async (
  password: string,
  stored: string
): Promise<boolean> => {
  const [scheme, iterationsText, saltText, hashText] = stored.split("$");

  if (scheme !== "pbkdf2-sha256" || !iterationsText || !saltText || !hashText) {
    return false;
  }

  const iterations = Math.trunc(Number(iterationsText));

  if (!Number.isFinite(iterations) || iterations <= 0) {
    return false;
  }

  const actual = await derivePassword(
    password,
    base64ToBytes(saltText),
    iterations
  );
  const expected = base64ToBytes(hashText);
  const length = Math.max(actual.length, expected.length);
  let difference = actual.length === expected.length ? 0 : 1;

  for (let index = 0; index < length; index += 1) {
    difference += Math.abs((actual[index] ?? 0) - (expected[index] ?? 0));
  }

  return difference === 0;
};

export const sha256 = async (input: string): Promise<string> => {
  const digest = await crypto.subtle.digest("SHA-256", encoder.encode(input));
  return bytesToHex(new Uint8Array(digest));
};

export const randomToken = (bytes = 32): string =>
  toBase64Url(crypto.getRandomValues(new Uint8Array(bytes)));

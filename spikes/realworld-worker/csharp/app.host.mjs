export const query = (sql, paramsJson) =>
  globalThis.__REPTAR_DB_QUERY(sql, paramsJson);

export const execute = (sql, paramsJson) =>
  globalThis.__REPTAR_DB_EXECUTE(sql, paramsJson);

export const batch = (batchJson) => globalThis.__REPTAR_DB_BATCH(batchJson);

export const hashPassword = (password) =>
  globalThis.__REPTAR_CRYPTO_HASH_PASSWORD(password);

export const verifyPassword = (password, hash) =>
  globalThis.__REPTAR_CRYPTO_VERIFY_PASSWORD(password, hash);

export const sha256 = (input) => globalThis.__REPTAR_CRYPTO_SHA256(input);

export const hmacSha256 = (key, message) =>
  globalThis.__REPTAR_CRYPTO_HMAC_SHA256(key, message);

export const randomToken = (bytes) =>
  globalThis.__REPTAR_CRYPTO_RANDOM_TOKEN(bytes);

export const envGet = (name) => globalThis.__REPTAR_ENV_GET(name);

export const respond = (requestId, json) =>
  globalThis.__REPTAR_RESPOND(requestId, json);

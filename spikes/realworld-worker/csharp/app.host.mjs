export const query = (sql, paramsJson) =>
  globalThis.__REPTAR_DB_QUERY(sql, paramsJson);

export const execute = (sql, paramsJson) =>
  globalThis.__REPTAR_DB_EXECUTE(sql, paramsJson);

export const batch = (batchJson) => globalThis.__REPTAR_DB_BATCH(batchJson);

export const r2put = (key, bytes, contentType) =>
  globalThis.__REPTAR_R2_PUT(key, bytes, contentType);

export const r2putoptions = (
  key,
  bytes,
  contentType,
  cacheControl,
  metadataJson,
  onlyIfAbsent
) =>
  globalThis.__REPTAR_R2_PUT_OPTIONS(
    key,
    bytes,
    contentType,
    cacheControl,
    metadataJson,
    onlyIfAbsent
  );

export const r2get = (key) => globalThis.__REPTAR_R2_GET(key);

export const r2getrange = (key, offset, length) =>
  globalThis.__REPTAR_R2_GET_RANGE(key, offset, length);

export const r2head = (key) => globalThis.__REPTAR_R2_HEAD(key);

export const r2list = (prefix, cursor, limit) =>
  globalThis.__REPTAR_R2_LIST(prefix, cursor, limit);

export const r2exists = (key) => globalThis.__REPTAR_R2_EXISTS(key);

export const r2delete = (key) => globalThis.__REPTAR_R2_DELETE(key);

export const httpFetch = (url, method, headersJson, body) =>
  globalThis.__REPTAR_HTTP_FETCH(url, method, headersJson, body);

export const httpFetchBytes = (
  url,
  method,
  headersJson,
  body,
  contentType
) =>
  globalThis.__REPTAR_HTTP_FETCH_BYTES(
    url,
    method,
    headersJson,
    body,
    contentType
  );

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

export const telemetry = (json) =>
  globalThis.__REPTAR_TELEMETRY(json);

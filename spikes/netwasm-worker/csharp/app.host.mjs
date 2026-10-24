export const query = (sql, paramsJson) =>
  globalThis.__REPTAR_DB_QUERY(sql, paramsJson);

export const execute = (sql, paramsJson) =>
  globalThis.__REPTAR_DB_EXECUTE(sql, paramsJson);

export const r2put = (key, bytes, contentType) =>
  globalThis.__REPTAR_R2_PUT(key, bytes, contentType);

export const r2get = (key) => globalThis.__REPTAR_R2_GET(key);

export const r2exists = (key) => globalThis.__REPTAR_R2_EXISTS(key);

export const r2delete = (key) => globalThis.__REPTAR_R2_DELETE(key);

export const kvGet = (key) => globalThis.__REPTAR_KV_GET(key);

export const kvPut = (key, value, ttl) =>
  globalThis.__REPTAR_KV_PUT(key, value, ttl);

export const kvDelete = (key) => globalThis.__REPTAR_KV_DELETE(key);

export const queueSend = (queueName, messageJson) =>
  globalThis.__REPTAR_QUEUE_SEND(queueName, messageJson);

export const aiRun = (model, prompt) =>
  globalThis.__REPTAR_AI_RUN(model, prompt);

export const durableFetch = (namespace, id, path, method, body) =>
  globalThis.__REPTAR_DURABLE_FETCH(namespace, id, path, method, body);

export const httpFetch = (url, method, headersJson, body) =>
  globalThis.__REPTAR_HTTP_FETCH(url, method, headersJson, body);

export const hashPassword = (password) =>
  globalThis.__REPTAR_CRYPTO_HASH_PASSWORD(password);

export const verifyPassword = (password, hash) =>
  globalThis.__REPTAR_CRYPTO_VERIFY_PASSWORD(password, hash);

export const sha256 = (input) => globalThis.__REPTAR_CRYPTO_SHA256(input);

export const hmacSha256 = (key, message) =>
  globalThis.__REPTAR_CRYPTO_HMAC_SHA256(key, message);

export const randomToken = (bytes) =>
  globalThis.__REPTAR_CRYPTO_RANDOM_TOKEN(bytes);

export const respond = (json) => globalThis.__REPTAR_RESPOND(json);

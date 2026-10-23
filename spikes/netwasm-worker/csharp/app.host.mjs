export const query = (sql, paramsJson) =>
  globalThis.__REPTAR_DB_QUERY(sql, paramsJson);

export const execute = (sql, paramsJson) =>
  globalThis.__REPTAR_DB_EXECUTE(sql, paramsJson);

export const r2put = (key, bytes, contentType) =>
  globalThis.__REPTAR_R2_PUT(key, bytes, contentType);

export const hashPassword = (password) =>
  globalThis.__REPTAR_CRYPTO_HASH_PASSWORD(password);

export const verifyPassword = (password, hash) =>
  globalThis.__REPTAR_CRYPTO_VERIFY_PASSWORD(password, hash);

export const sha256 = (input) => globalThis.__REPTAR_CRYPTO_SHA256(input);

export const randomToken = (bytes) =>
  globalThis.__REPTAR_CRYPTO_RANDOM_TOKEN(bytes);

export const respond = (json) => globalThis.__REPTAR_RESPOND(json);

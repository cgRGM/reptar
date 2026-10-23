export const query = (sql, paramsJson) =>
  globalThis.__REPTAR_DB_QUERY(sql, paramsJson);

export const r2put = (key, bytes, contentType) =>
  globalThis.__REPTAR_R2_PUT(key, bytes, contentType);

export const resolve = (result) => globalThis.__REPTAR_RESOLVE(result);

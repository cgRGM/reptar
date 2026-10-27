export const buildRequestHeaders = (
  overrides: Record<string, string> | undefined,
  acceptEncoding: string
): Headers => {
  const headers = new Headers(overrides);
  if (!headers.has("Accept-Encoding")) {
    headers.set("Accept-Encoding", acceptEncoding);
  }
  return headers;
};

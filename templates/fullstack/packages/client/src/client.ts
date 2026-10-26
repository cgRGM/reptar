export interface Endpoint<I, O, S extends number, R = unknown> {
  input: I;
  output: O;
  status: S;
  responses?: R;
}

export interface RouteTree {
  [key: string]: Endpoint<unknown, unknown, number> | RouteTree;
}

type SuccessStatus = 200 | 201 | 202 | 203 | 204 | 205 | 206;

type ResponseBody<O, S extends number, R> = S extends 204 | 205 | 304
  ? never
  : R extends Record<S, infer Body>
    ? Body
    : O;

export type ClientResponse<
  O = unknown,
  S extends number = number,
  R = unknown,
> = Omit<Response, "json" | "ok" | "status"> &
  (number extends S
    ? { json: () => Promise<O>; ok: boolean; status: S }
    : S extends SuccessStatus
      ? { json: () => Promise<ResponseBody<O, S, R>>; ok: true; status: S }
      : { json: () => Promise<ResponseBody<O, S, R>>; ok: false; status: S });

type HasKeys<T> = [T] extends [undefined | null | Record<string, never>]
  ? false
  : [keyof T] extends [never]
    ? false
    : true;

export type Client<T> = {
  [K in keyof T]: T[K] extends Endpoint<infer I, infer O, infer S, infer R>
    ? HasKeys<I> extends true
      ? (args: I) => Promise<ClientResponse<O, S, R>>
      : (args?: I) => Promise<ClientResponse<O, S, R>>
    : T[K] extends object
      ? Client<T[K]>
      : never;
};

export interface ClientOptions {
  fetch?: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
  credentials?: RequestCredentials;
  headers?: Record<string, string>;
  init?: RequestInit;
}

interface RequestArgs {
  json?: unknown;
  param?: Record<string, string>;
  query?: Record<string, string | number | boolean | null | undefined>;
}

const buildUrl = (
  baseUrl: string,
  segments: string[],
  args?: RequestArgs
): string => {
  let path = segments.join("/");

  if (args?.param) {
    // Replace each placeholder once so values containing ':' or '*' cannot be
    // re-interpreted by a later replacement. Catch-all params preserve path
    // separators while still encoding each segment.
    path = path.replaceAll(
      /(?<prefix>^|\/)(?<marker>[:*])(?<key>[A-Za-z0-9_]+)/gu,
      (match: string, prefix: string, marker: string, key: string) => {
        const value = args.param?.[key];
        if (value === undefined) {
          return match;
        }

        const encoded =
          marker === "*"
            ? value.split("/").map(encodeURIComponent).join("/")
            : encodeURIComponent(value);

        return `${prefix}${encoded}`;
      }
    );
  }

  let url = `${baseUrl.replace(/\/+$/u, "")}/${path.replace(/^\/+/u, "")}`;

  if (args?.query) {
    const search = new URLSearchParams();

    for (const [key, value] of Object.entries(args.query)) {
      if (value !== undefined && value !== null) {
        search.set(key, String(value));
      }
    }

    const query = search.toString();
    if (query) {
      url += `?${query}`;
    }
  }

  return url;
};

export const createClient = <T extends object = RouteTree>(
  baseUrl: string,
  options: ClientOptions = {}
): Client<T> => {
  const call = async (
    segments: string[],
    method: string,
    args?: RequestArgs
  ): Promise<ClientResponse> => {
    const headers = new Headers(options.headers);
    const init: RequestInit = {
      credentials: options.credentials ?? "include",
      headers,
      method: method.toUpperCase(),
      ...options.init,
    };

    if (args?.json !== undefined) {
      headers.set("content-type", "application/json");
      init.body = JSON.stringify(args.json);
    }

    const response = await (options.fetch ?? fetch)(
      buildUrl(baseUrl, segments, args),
      init
    );
    return response as ClientResponse;
  };

  const createProxy = (segments: string[]): unknown =>
    new Proxy(() => null, {
      apply(_target, _thisArg, argumentsList: unknown[]) {
        const [args] = argumentsList as [RequestArgs?];
        const last = segments.at(-1);

        if (last?.startsWith("$")) {
          return call(segments.slice(0, -1), last.slice(1), args);
        }

        return call(segments, "get", args);
      },
      get(_target, key) {
        if (typeof key !== "string" || key === "then") {
          return;
        }

        return createProxy([...segments, key]);
      },
    });

  return createProxy([]) as Client<T>;
};

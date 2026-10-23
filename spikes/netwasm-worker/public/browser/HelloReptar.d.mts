export interface NetWasmPagePlatformHooks {
  now?: () => number;
  random?: (bytes: Uint8Array) => void;
}
export interface NetWasmPageOpenOptions {
  environment?: Record<string, string>;
  modules?: Record<string, object>;
  onNotification?: (value: { operation: string; arguments: unknown[] }) => void | Promise<void>;
  platform?: NetWasmPagePlatformHooks;
  stderr?: (line: string) => void;
  stdout?: (line: string) => void;
}
export interface NetWasmPageModule {
  "createPost"(title: string, content: string, imageKey: string): void;
  "listPosts"(): void;
  "uploadImage"(key: string, contentType: string, bytes: Uint8Array): void;
  close(): Promise<void>;
  readonly failure: Promise<unknown>;
}
export declare function open(options?: NetWasmPageOpenOptions): Promise<NetWasmPageModule>;

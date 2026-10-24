import type { Endpoint } from "./client";
import type { CreateItemInput, Item, User } from "./types";

export interface AppType {
  api: {
    items: {
      get: Endpoint<undefined, Item[], 200>;
      post: Endpoint<{ json: CreateItemInput }, Item, 201>;
      [key: string]:
        | {
            delete: Endpoint<
              { param: { id: string } },
              { success: boolean },
              200
            >;
          }
        | Endpoint<undefined, Item[], 200>
        | Endpoint<{ json: CreateItemInput }, Item, 201>;
    };
    me: {
      get: Endpoint<undefined, User | null, 200>;
    };
  };
}

export interface D1PreparedStatementLike {
  all: () => Promise<{ results: unknown[] }>;
}

export interface D1DatabaseLike {
  prepare: (sql: string) => D1PreparedStatementLike;
}

export interface ReptarBindings {
  db?: D1DatabaseLike;
}

export const d1Query = async (
  db: D1DatabaseLike,
  sql: string
): Promise<string> => {
  const result = await db.prepare(sql).all();
  return JSON.stringify(result.results);
};

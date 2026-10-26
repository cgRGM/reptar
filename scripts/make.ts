#!/usr/bin/env bun
/**
 * make:model / make:feature — small, static scaffolding for file-based apps.
 *
 * Generates a TableDef + [ReptarEntity] DTO, optionally a CRUD feature slice,
 * wires the #:include lines into app.cs, updates the JSON context when it can
 * find one, and optionally runs `reptar db generate`.
 */

import { spawnSync } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";

import { ensureJsonContextInclude, generateJsonContext } from "./json-context";

export interface MakeOptions {
  appRoot: string;
  name: string;
  feature: boolean;
  migration: boolean;
  force: boolean;
  dryRun: boolean;
}

const pascal = (value: string): string =>
  value
    .split(/[-_\s]+/u)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join("");

const snake = (value: string): string =>
  value
    .replaceAll(/(?<lower>[a-z0-9])(?<upper>[A-Z])/gu, "$<lower>_$<upper>")
    .replaceAll(/[-\s]+/gu, "_")
    .toLowerCase();

const pluralize = (value: string): string => {
  const lower = value.toLowerCase();
  if (lower.endsWith("y") && !/[aeiou]y$/u.test(lower)) {
    return `${value.slice(0, -1)}ies`;
  }
  if (/(?:s|x|z|ch|sh)$/u.test(lower)) {
    return `${value}es`;
  }
  return `${value}s`;
};

const argValue = (args: string[], name: string): string | undefined => {
  const index = args.indexOf(name);
  return index !== -1 && index + 1 < args.length ? args[index + 1] : undefined;
};

export const parseMakeArgs = (
  args: string[],
  defaultAppRoot: string
): MakeOptions => {
  const positional = args.find((arg) => !arg.startsWith("-"));
  if (!positional) {
    throw new Error(
      "Usage: bun reptar make:model <Name> [--feature] [--migration] [--force] [--dry-run]"
    );
  }

  return {
    appRoot: path.resolve(defaultAppRoot, argValue(args, "--app") ?? "."),
    dryRun: args.includes("--dry-run"),
    feature: args.includes("--feature") || args.includes("-f"),
    force: args.includes("--force"),
    migration: args.includes("--migration") || args.includes("-m"),
    name: positional,
  };
};

const readIf = (file: string): string | null =>
  existsSync(file) ? readFileSync(file, "utf-8") : null;

const writeGenerated = (
  file: string,
  content: string,
  options: MakeOptions
): void => {
  if (existsSync(file) && !options.force) {
    console.log(`skip   ${file} (exists; use --force)`);
    return;
  }

  if (options.dryRun) {
    console.log(`create ${file} (dry-run)`);
    return;
  }

  writeFileSync(file, content, "utf-8");
  console.log(`create ${file}`);
};

const insertInclude = (
  appCs: string,
  includePath: string
): { content: string; changed: boolean } => {
  if (appCs.includes(`#:include ${includePath}`)) {
    return { changed: false, content: appCs };
  }

  const lines = appCs.split("\n");
  const includeIndexes = lines
    .map((line, index) =>
      line.trimStart().startsWith("#:include ") ? index : -1
    )
    .filter((index) => index >= 0);

  const lastInclude = includeIndexes.at(-1) ?? -1;
  lines.splice(lastInclude + 1, 0, `#:include ${includePath}`);
  return { changed: true, content: lines.join("\n") };
};

const insertConfigureCall = (
  appCs: string,
  featureClass: string
): { content: string; changed: boolean } => {
  if (appCs.includes(`${featureClass}.Configure(app)`)) {
    return { changed: false, content: appCs };
  }

  const marker = /^(?<indent>\s*)await app\.RunAsync\(/mu;
  if (!marker.test(appCs)) {
    return { changed: false, content: appCs };
  }

  const content = appCs.replace(
    marker,
    `$<indent>${featureClass}.Configure(app);\n\n$<indent>await app.RunAsync(`
  );
  return { changed: true, content };
};

const updateSchemaIndex = (
  appRoot: string,
  tableField: string,
  options: MakeOptions
): void => {
  const file = path.join(appRoot, "schema", "index.cs");
  const content = readIf(file);
  if (!content) {
    return;
  }

  if (
    content.includes(`${tableField},`) ||
    content.includes(`${tableField} ]`)
  ) {
    return;
  }

  const updated = content.replace(
    /(?<start>TableDef\[\]\s+All\s*=\s*\[)(?<body>[\s\S]*?)(?<end>\])/u,
    (_match, start: string, body: string, end: string) => {
      const trimmed = body.trim();
      const separator =
        trimmed.length === 0 || trimmed.endsWith(",")
          ? "\n        "
          : ",\n        ";
      return `${start}${trimmed}${separator}${tableField}\n    ${end}`;
    }
  );

  if (updated === content) {
    return;
  }

  if (options.dryRun) {
    console.log(`update ${file} (dry-run)`);
  } else {
    writeFileSync(file, updated, "utf-8");
    console.log(`update ${file}`);
  }
};

export const runMake = (
  command: "make:model" | "make:feature",
  args: string[],
  repoRoot: string
): void => {
  const options = parseMakeArgs(args, process.cwd());
  const entity = pascal(options.name);
  const tableField = pluralize(entity);
  const tableName = snake(tableField);
  const dtoType = entity;
  const featureClass = `${tableField}Feature`;
  const schemaFile = path.join(
    options.appRoot,
    "schema",
    `${snake(tableField)}.cs`
  );
  const featureFile = path.join(
    options.appRoot,
    "features",
    `${snake(tableField)}.cs`
  );
  const appCsPath = path.join(options.appRoot, "app.cs");

  if (command === "make:model" || !existsSync(featureFile)) {
    const schema = `using Reptar;
using static Reptar.ReptarApi;

public static partial class Schema
{
    public static readonly TableDef ${tableField} = DefineTable("${tableName}",
        Col.Text("id").PrimaryKey(),
        Col.Text("title").NotNull(),
        Col.Text("created_at").NotNull().DefaultSql("CURRENT_TIMESTAMP")
    );
}

[ReptarEntity("${tableName}")]
public partial record ${dtoType}(
    string Id,
    string Title,
    string CreatedAt);
`;

    writeGenerated(schemaFile, schema, options);
    updateSchemaIndex(options.appRoot, tableField, options);
  }

  if (options.feature) {
    const feature = `using Reptar;
using static Reptar.ReptarApi;

public static class ${featureClass}
{
    public static void Configure(ReptarApp app)
    {
        app.Get("/api/${tableName}", async ctx =>
        {
            var rows = await ctx.Db.From<${dtoType}>(Schema.${tableField})
                .OrderBy(Schema.${tableField}.Col<string>("created_at").Desc())
                .Limit(50)
                .ToListAsync();

            return ctx.Json(rows.ToArray());
        }).Produces<${dtoType}[]>(200);

        app.Get("/api/${tableName}/:id", async ctx =>
        {
            var row = await ctx.Db.GetAsync<${dtoType}>(
                Schema.${tableField},
                ctx.Param("id")!);

            return row is null
                ? ctx.NotFound("${entity} not found.")
                : ctx.Json(row);
        }).Produces<${dtoType}>(200);
    }
}
`;

    writeGenerated(featureFile, feature, options);
  }

  const jsonContext = generateJsonContext(options.appRoot, {
    dryRun: options.dryRun,
  });

  if (jsonContext.types.length > 0) {
    const includeChanged = ensureJsonContextInclude(options.appRoot, {
      dryRun: options.dryRun,
    });

    console.log(
      `${options.dryRun ? "would update" : "update"} ${jsonContext.file} ` +
        `(${jsonContext.types.length} JSON type(s))`
    );

    if (includeChanged) {
      console.log(
        `${options.dryRun ? "would update" : "update"} ${appCsPath} ` +
          `(#:include ${path.basename(jsonContext.file)})`
      );
    }
  }

  if (existsSync(appCsPath)) {
    let appCs = readFileSync(appCsPath, "utf-8");
    let changed = false;

    const schemaInclude = insertInclude(
      appCs,
      `schema/${snake(tableField)}.cs`
    );
    appCs = schemaInclude.content;
    changed ||= schemaInclude.changed;

    if (options.feature) {
      const featureInclude = insertInclude(
        appCs,
        `features/${snake(tableField)}.cs`
      );
      appCs = featureInclude.content;
      changed ||= featureInclude.changed;

      const configure = insertConfigureCall(appCs, featureClass);
      appCs = configure.content;
      changed ||= configure.changed;
    }

    if (changed) {
      if (options.dryRun) {
        console.log(`update ${appCsPath} (dry-run)`);
      } else {
        writeFileSync(appCsPath, appCs, "utf-8");
        console.log(`update ${appCsPath}`);
      }
    }
  } else {
    console.log(
      `note   ${appCsPath} not found; add the generated files to your project manually`
    );
  }

  if (options.migration) {
    const migrationName = `create_${tableName}`;
    if (options.dryRun) {
      console.log(
        `run    reptar db generate --name ${migrationName} (dry-run)`
      );
    } else {
      const result = spawnSync(
        process.execPath,
        [
          path.join(repoRoot, "scripts", "reptar.ts"),
          "db",
          "generate",
          "--name",
          migrationName,
          "--app",
          options.appRoot,
        ],
        { cwd: options.appRoot, stdio: "inherit" }
      );

      if (result.status !== 0) {
        console.log(
          `note   db generate failed; run it manually after wiring the schema`
        );
      }
    }
  }

  console.log(
    `\nNext: bun reptar codegen${options.feature ? "  # refresh the typed client" : ""}`
  );
};

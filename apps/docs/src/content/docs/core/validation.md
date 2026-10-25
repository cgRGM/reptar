---
title: Validation & Rule<T>
description: Built-in AOT-safe validation, auto-derived OpenAPI 3.1 schemas, and Standard Schema generation.
---

## Three-Layer Validation Architecture

Reptar validates incoming payloads without relying on third-party validation libraries or runtime expression compilation:

1. **Source-Generated JSON Binding (`SourceGenJsonCodec`):** Missing required properties, type mismatches, and malformed JSON are rejected at deserialization time with zero reflection.
2. **AOT-Safe `Rule<T>()` Constraints:** Declarative rules for string lengths, numeric ranges, email formatting, regex patterns, and cross-field refinements that execute without `Expression.Compile()` overhead.
3. **Auto-Derived OpenAPI 3.1 Schemas:** Every rule registered on a `Validator<T>` automatically populates the route's OpenAPI 3.1 JSON Schema (`required`, `minLength`, `maxLength`, `minimum`, `maximum`, `format`) in `/openapi.json`, Scalar UI (`/reference`), and Swagger UI (`/swagger`).

## Defining Validation Rules

```csharp
public record UpdateUserInput(string DisplayName, string Email, int Age);

public static readonly Validator<UpdateUserInput> UpdateSchema = Rule<UpdateUserInput>()
    .Prop(x => x.DisplayName, r => r.NotEmpty().Min(2).Max(50))
    .Prop(x => x.Email, r => r.NotEmpty().Email().Max(255))
    .Prop(x => x.Age, r => r.Min(18).Max(120))
    .Refine(
        x => !x.DisplayName.Equals("admin", StringComparison.OrdinalIgnoreCase),
        message: "DisplayName cannot be 'admin'",
        path: "displayName"
    );
```

For maximum throughput in strict Native AOT builds, you can also pass an explicit property name and direct getter delegate to bypass expression tree inspection completely:

```csharp
public static readonly Validator<CreateTaskInput> CreateTaskSchema = Rule<CreateTaskInput>()
    .Prop("title", static x => x.Title, r => r.NotEmpty().Min(3).Max(200))
    .Prop("priority", static x => x.Priority, r => r.Min(1).Max(5));
```

## Native AOT JSON Serialization (`SourceGenJsonCodec`)

To eliminate reflection in Native AOT deployments, provide your `JsonSerializerContext` to `SourceGenJsonCodec`. Reptar automatically chains its internal `ReptarInternalJsonContext` so framework error envelopes (`ErrorBody`, `ValidationError`) are always AOT-safe out of the box:

```csharp
[JsonSerializable(typeof(UpdateUserInput))]
[JsonSerializable(typeof(UserDto))]
internal partial class AppJsonContext : JsonSerializerContext;

var app = ReptarApp.Create(new ReptarConfig
{
    Database = Db.Sqlite("app.db"),
    JsonCodec = new SourceGenJsonCodec(AppJsonContext.Default),
});
```

## Structured Error Envelope

When input validation fails, Reptar returns a standardized HTTP 400 response:

```json
{
  "code": "validation_error",
  "message": "Validation failed.",
  "requestId": "req_01J8F...",
  "issues": [
    {
      "path": ["displayName"],
      "message": "Must be at least 2 characters"
    }
  ]
}
```

## Standard Schema (`~standard`) Interoperability

During `bun run api:codegen`, `Rule<T>` definitions emit zero-dependency TypeScript validators implementing the [Standard Schema specification](https://standardschema.dev) (`~standard` v1, vendor `"reptar"`), integrating directly with TanStack Form and React Hook Form.

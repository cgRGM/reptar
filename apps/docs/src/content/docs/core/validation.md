---
title: Validation & Rule<T>
description: Built-in three-layer validation and Standard Schema generation.
---

## Three-Layer Architecture

Reptar provides native validation without relying on Zod or third-party validation libraries on the backend:

1. **Records are the Schema:** JSON binding is source-generated at build time. Missing required properties, type mismatches, and unrecognized fields result in immediate validation errors. Non-nullable properties are strictly required.
2. **`Rule<T>` for Field Constraints:** Declarative rules for string lengths, ranges, regex patterns, email formatting, and cross-field refinements.
3. **Schema-Derived Base Rules:** In routes mapping directly to a `TableDef`, column constraints (such as `NotNull`, max lengths, and unique keys) are automatically applied as base rules.

---

## Defining Validation Rules

```csharp
public record UpdateUserInput(string DisplayName, string Email, int Age);

public static readonly Validator<UpdateUserInput> UpdateSchema = Rule<UpdateUserInput>()
    .Prop(x => x.DisplayName, r => r.Min(2).Max(50))
    .Prop(x => x.Email, r => r.Email())
    .Prop(x => x.Age, r => r.Min(18).Max(120))
    .Refine(
        x => !x.DisplayName.Equals("admin", StringComparison.OrdinalIgnoreCase),
        message: "DisplayName cannot be 'admin'",
        path: "displayName"
    );
```

---

## Error Envelope

When input validation fails, Reptar returns a structured HTTP 400 response:

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

---

## Standard Schema (`~standard`) Interoperability

During `bun reptar codegen`, every `Rule<T>` emits a zero-dependency TypeScript validator implementing the [Standard Schema specification](https://standardschema.dev) (`~standard` version 1, vendor `"reptar"`).

This means frontend frameworks like **TanStack Form** or form libraries can validate forms client-side using the exact rules written in C# — without installing Zod or duplicating schema definitions.

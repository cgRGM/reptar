using System.Collections.Generic;
using System.Text.Json.Serialization;
using {{PROJECT_PASCAL}}.Features;
using {{PROJECT_PASCAL}}.Schema;

namespace {{PROJECT_PASCAL}};

/// <summary>
/// Source-generated JSON metadata for every DTO this app returns or binds.
/// Registering this context (see ReptarConfig.Json in app.cs) keeps
/// serialization reflection-free under Native AOT and NetWasm.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(AuthMeResponse))]
[JsonSerializable(typeof(UserProfile))]
[JsonSerializable(typeof(ActionSuccessResponse))]
[JsonSerializable(typeof(Item))]
[JsonSerializable(typeof(List<Item>))]
[JsonSerializable(typeof(CreateItemInput))]
public sealed partial class AppJsonContext : JsonSerializerContext;

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Reptar;

public static class RpcGenerator
{
    public static void Generate(ReptarApp app, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var routes = app.RouteMetadata;
        var index = new TsTypeIndex();

        foreach (var route in routes)
        {
            if (route.InputType is not null)
                index.Add(route.InputType);

            foreach (var (_, type) in route.Produces)
            {
                if (type is not null)
                    index.Add(type);
            }
        }

        File.WriteAllText(
            Path.Combine(outputDirectory, "types.ts"),
            TsEmitter.EmitTypes(index));

        File.WriteAllText(
            Path.Combine(outputDirectory, "app-type.d.ts"),
            TsEmitter.EmitAppType(routes, index));

        File.WriteAllText(
            Path.Combine(outputDirectory, "openapi.json"),
            OpenApiEmitter.Emit(routes, index));
    }
}

internal sealed class TsTypeIndex
{
    private readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal);

    public IEnumerable<Type> All => _types.Values;

    public void Add(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target.IsArray)
        {
            Add(target.GetElementType()!);
            return;
        }

        if (target.IsGenericType)
        {
            foreach (var argument in target.GetGenericArguments())
                Add(argument);

            return;
        }

        if (TsMapper.IsPrimitive(target) || target.IsEnum)
            return;

        if (target.Name.StartsWith("<>", StringComparison.Ordinal))
            return;

        if (_types.ContainsKey(target.Name))
            return;

        _types[target.Name] = target;

        foreach (var property in target.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Add(property.PropertyType);
    }
}

internal static class TsMapper
{
    public static bool IsPrimitive(Type type) =>
        type == typeof(string) || type == typeof(Guid) || type == typeof(DateTime) ||
        type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(char) ||
        type == typeof(int) || type == typeof(long) || type == typeof(short) ||
        type == typeof(byte) || type == typeof(double) || type == typeof(float) ||
        type == typeof(decimal) || type == typeof(bool) || type == typeof(object);

    public static string Type(Type type, TsTypeIndex index)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target == typeof(string) || target == typeof(Guid) ||
            target == typeof(DateTime) || target == typeof(DateTimeOffset) ||
            target == typeof(DateOnly) || target == typeof(char))
            return "string";

        if (target == typeof(int) || target == typeof(long) || target == typeof(short) ||
            target == typeof(byte) || target == typeof(double) || target == typeof(float) ||
            target == typeof(decimal))
            return "number";

        if (target == typeof(bool))
            return "boolean";

        if (target == typeof(object))
            return "unknown";

        if (target.IsArray)
            return $"{Type(target.GetElementType()!, index)}[]";

        if (target.IsGenericType)
        {
            var definition = target.GetGenericTypeDefinition();
            var arguments = target.GetGenericArguments();

            if (definition == typeof(List<>) ||
                definition == typeof(IReadOnlyList<>) ||
                definition == typeof(IList<>) ||
                definition == typeof(IEnumerable<>))
                return $"{Type(arguments[0], index)}[]";

            if (definition == typeof(Dictionary<,>) && arguments[0] == typeof(string))
                return $"Record<string, {Type(arguments[1], index)}>";

            return "unknown";
        }

        if (target.IsEnum)
            return string.Join(" | ", Enum.GetNames(target).Select(name => $"\"{name}\""));

        index.Add(target);
        return CleanName(target.Name);
    }

    public static string CleanName(string name)
    {
        var tick = name.IndexOf('`');
        return tick >= 0 ? name[..tick] : name;
    }

    public static bool IsNullable(PropertyInfo property)
    {
        if (Nullable.GetUnderlyingType(property.PropertyType) is not null)
            return true;

        var context = new NullabilityInfoContext();
        return context.Create(property).ReadState == NullabilityState.Nullable;
    }

    public static bool IsOptional(PropertyInfo property)
    {
        var constructor = property.DeclaringType?
            .GetConstructors()
            .OrderByDescending(candidate => candidate.GetParameters().Length)
            .FirstOrDefault();

        var parameter = constructor?
            .GetParameters()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name, property.Name, StringComparison.OrdinalIgnoreCase));

        return parameter?.HasDefaultValue == true;
    }

    public static string Camel(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}

internal static class TsEmitter
{
    public static string EmitTypes(TsTypeIndex index)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// Generated by reptar codegen. Do not edit.");
        builder.AppendLine();

        foreach (var type in index.All.OrderBy(type => type.Name, StringComparer.Ordinal))
        {
            builder.AppendLine($"export interface {TsMapper.CleanName(type.Name)} {{");

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var nullable = TsMapper.IsNullable(property);
                var optional = TsMapper.IsOptional(property) ? "?" : "";
                var typeName = TsMapper.Type(property.PropertyType, index);
                var suffix = nullable ? " | null" : "";
                builder.AppendLine(
                    $"  {TsMapper.Camel(property.Name)}{optional}: {typeName}{suffix};");
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static string EmitAppType(IReadOnlyList<RouteMeta> routes, TsTypeIndex index)
    {
        var root = new RouteNode();

        foreach (var route in routes)
        {
            var segments = route.Path == "/" ? [] : route.Path[1..].Split('/');
            var node = root;

            foreach (var segment in segments)
            {
                if (!node.Children.TryGetValue(segment, out var child))
                {
                    child = new RouteNode();
                    node.Children[segment] = child;
                }

                node = child;
            }

            node.Endpoints[route.Method.ToLowerInvariant()] = BuildEndpoint(route, index);
        }

        var builder = new StringBuilder();
        builder.AppendLine("// Generated by reptar codegen. Do not edit.");
        builder.AppendLine("import type { Endpoint } from \"@reptar/client\";");
        builder.AppendLine(
            "import type { " +
            string.Join(
                ", ",
                index.All
                    .Select(type => TsMapper.CleanName(type.Name))
                    .OrderBy(name => name, StringComparer.Ordinal)) +
            " } from \"./types\";");
        builder.AppendLine();
        builder.AppendLine("export type AppType = {");
        EmitNode(builder, root, "  ");
        builder.AppendLine("};");

        return builder.ToString();
    }

    private static string BuildEndpoint(RouteMeta route, TsTypeIndex index)
    {
        var inputParts = new List<string>();

        if (route.InputType is not null)
            inputParts.Add($"json: {TsMapper.Type(route.InputType, index)}");

        var parameters = route.Path
            .Split('/')
            .Where(segment => segment.StartsWith(':'))
            .Select(segment => segment[1..])
            .ToArray();

        if (parameters.Length > 0)
        {
            inputParts.Add("param: { " +
                string.Join(", ", parameters.Select(parameter => $"{parameter}: string")) + " }");
        }

        var input = inputParts.Count == 0
            ? "{}"
            : "{ " + string.Join("; ", inputParts) + " }";

        var outputs = route.Produces
            .Where(produced => produced.Type is not null)
            .Select(produced => TsMapper.Type(produced.Type!, index))
            .Distinct()
            .ToArray();

        var statuses = route.Produces
            .Select(produced => produced.Status)
            .Distinct()
            .OrderBy(status => status)
            .ToArray();

        var output = outputs.Length > 0
            ? string.Join(" | ", outputs)
            : statuses.Contains(204) ? "void" : "unknown";

        var status = statuses.Length == 0 ? "200" : string.Join(" | ", statuses);

        return $"Endpoint<{input}, {output}, {status}>";
    }

    private static void EmitNode(StringBuilder builder, RouteNode node, string indent)
    {
        foreach (var (method, endpoint) in node.Endpoints.OrderBy(
            pair => pair.Key,
            StringComparer.Ordinal))
        {
            builder.AppendLine($"{indent}${method}: {endpoint};");
        }

        foreach (var (segment, child) in node.Children.OrderBy(
            pair => pair.Key,
            StringComparer.Ordinal))
        {
            var key = Regex.IsMatch(segment, "^[A-Za-z_$][A-Za-z0-9_$]*$")
                ? segment
                : $"\"{segment}\"";

            builder.AppendLine($"{indent}{key}: {{");
            EmitNode(builder, child, indent + "  ");
            builder.AppendLine($"{indent}}};");
        }
    }

    private sealed class RouteNode
    {
        public Dictionary<string, RouteNode> Children { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Endpoints { get; } = new(StringComparer.Ordinal);
    }
}

internal static class OpenApiEmitter
{
    public static string Emit(IReadOnlyList<RouteMeta> routes, TsTypeIndex index)
    {
        var document = new Dictionary<string, object?>
        {
            ["openapi"] = "3.1.0",
            ["info"] = new Dictionary<string, object?>
            {
                ["title"] = "Reptar API",
                ["version"] = "1.0.0",
            },
            ["paths"] = BuildPaths(routes),
            ["components"] = new Dictionary<string, object?>
            {
                ["schemas"] = BuildSchemas(index),
            },
        };

        return JsonSerializer.Serialize(
            document,
            new JsonSerializerOptions(ReptarJson.Options) { WriteIndented = true });
    }

    private static Dictionary<string, object?> BuildPaths(IReadOnlyList<RouteMeta> routes)
    {
        var paths = new Dictionary<string, object?>();

        foreach (var route in routes)
        {
            var path = Regex.Replace(route.Path, ":([A-Za-z0-9_]+)", "{$1}");

            if (!paths.TryGetValue(path, out var existing))
            {
                existing = new Dictionary<string, object?>();
                paths[path] = existing;
            }

            var operations = (Dictionary<string, object?>)existing!;
            var responses = new Dictionary<string, object?>();

            foreach (var (status, type) in route.Produces)
            {
                responses[status.ToString()] = new Dictionary<string, object?>
                {
                    ["description"] = status switch
                    {
                        200 => "OK",
                        201 => "Created",
                        204 => "No Content",
                        400 => "Bad Request",
                        401 => "Unauthorized",
                        403 => "Forbidden",
                        404 => "Not Found",
                        409 => "Conflict",
                        _ => "Response",
                    },
                    ["content"] = type is null
                        ? null
                        : new Dictionary<string, object?>
                        {
                            ["application/json"] = new Dictionary<string, object?>
                            {
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["$ref"] = $"#/components/schemas/{TsMapper.CleanName(type.Name)}",
                                },
                            },
                        },
                };
            }

            if (responses.Count == 0)
                responses["200"] = new Dictionary<string, object?> { ["description"] = "OK" };

            var operation = new Dictionary<string, object?>
            {
                ["operationId"] = route.Name ??
                    $"{route.Method.ToLowerInvariant()}{route.Path.Replace("/", "_").Replace(":", "")}",
                ["responses"] = responses,
            };

            if (route.InputType is not null)
            {
                operation["requestBody"] = new Dictionary<string, object?>
                {
                    ["required"] = true,
                    ["content"] = new Dictionary<string, object?>
                    {
                        ["application/json"] = new Dictionary<string, object?>
                        {
                            ["schema"] = new Dictionary<string, object?>
                            {
                                ["$ref"] = $"#/components/schemas/{TsMapper.CleanName(route.InputType.Name)}",
                            },
                        },
                    },
                };
            }

            operations[route.Method.ToLowerInvariant()] = operation;
        }

        return paths;
    }

    private static Dictionary<string, object?> BuildSchemas(TsTypeIndex index)
    {
        var schemas = new Dictionary<string, object?>();

        foreach (var type in index.All)
        {
            var properties = new Dictionary<string, object?>();

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                properties[TsMapper.Camel(property.Name)] = new Dictionary<string, object?>
                {
                    ["type"] = JsonType(property.PropertyType),
                };
            }

            schemas[TsMapper.CleanName(type.Name)] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = properties,
            };
        }

        return schemas;
    }

    private static string JsonType(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target == typeof(string) || target == typeof(Guid) ||
            target == typeof(DateTime) || target == typeof(DateTimeOffset))
            return "string";

        if (target == typeof(int) || target == typeof(long) || target == typeof(short) ||
            target == typeof(double) || target == typeof(float) || target == typeof(decimal))
            return "number";

        if (target == typeof(bool))
            return "boolean";

        if (target.IsArray ||
            (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(List<>)))
            return "array";

        return "object";
    }
}

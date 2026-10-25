#if !NETWASM
using System.Text;
using System.Text.Json;

namespace Reptar;

public sealed class OpenApiOptions
{
    public string Title { get; set; } = "Reptar API";
    public string Version { get; set; } = "1.0.0";
    public string? Description { get; set; }
    public string DocumentPath { get; set; } = "/openapi.json";
    public string ReferencePath { get; set; } = "/reference";
    public string SwaggerPath { get; set; } = "/swagger";
    public string? Theme { get; set; } = "default";
}

public static class OpenApiExtensions
{
    public static ReptarApp UseScalar(this ReptarApp app, Action<OpenApiOptions>? configure = null)
    {
        var options = new OpenApiOptions();
        configure?.Invoke(options);

        // Serve the OpenAPI JSON document
        app.Get(options.DocumentPath, ctx =>
        {
            var json = app.GetOpenApiJson(options);
            return Task.FromResult(ctx.JsonRaw(json));
        });

        // Serve the Scalar UI HTML page
        app.Get(options.ReferencePath, ctx =>
        {
            var html = RenderScalarHtml(options);
            return Task.FromResult(ctx.Html(html));
        });

        return app;
    }

    public static ReptarApp UseSwaggerUi(this ReptarApp app, Action<OpenApiOptions>? configure = null)
    {
        var options = new OpenApiOptions();
        configure?.Invoke(options);

        // Serve the OpenAPI JSON document
        app.Get(options.DocumentPath, ctx =>
        {
            var json = app.GetOpenApiJson(options);
            return Task.FromResult(ctx.JsonRaw(json));
        });

        // Serve the Swagger UI HTML page
        app.Get(options.SwaggerPath, ctx =>
        {
            var html = RenderSwaggerHtml(options);
            return Task.FromResult(ctx.Html(html));
        });

        return app;
    }

    public static string GetOpenApiJson(this ReptarApp app, OpenApiOptions? options = null)
    {
        var opt = options ?? new OpenApiOptions();
        return OpenApiGenerator.GenerateJson(app.RouteMetadata, opt);
    }

    public static string RenderScalarHtml(OpenApiOptions options)
    {
        var title = System.Net.WebUtility.HtmlEncode(options.Title);
        var specUrl = options.DocumentPath;

        return $$"""
        <!doctype html>
        <html>
          <head>
            <title>{{title}}</title>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <style>
              body { margin: 0; padding: 0; }
            </style>
          </head>
          <body>
            <script
              id="api-reference"
              data-url="{{specUrl}}">
            </script>
            <script src="https://cdn.jsdelivr.net/npm/@scalar/api-reference"></script>
          </body>
        </html>
        """;
    }

    public static string RenderSwaggerHtml(OpenApiOptions options)
    {
        var title = System.Net.WebUtility.HtmlEncode(options.Title);
        var specUrl = options.DocumentPath;

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
          <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>{{title}}</title>
            <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5/swagger-ui.css" />
            <style>
              body { margin: 0; padding: 0; }
            </style>
          </head>
          <body>
            <div id="swagger-ui"></div>
            <script src="https://unpkg.com/swagger-ui-dist@5/swagger-ui-bundle.js" crossorigin></script>
            <script>
              window.onload = () => {
                window.ui = SwaggerUIBundle({
                  url: '{{specUrl}}',
                  dom_id: '#swagger-ui',
                });
              };
            </script>
          </body>
        </html>
        """;
    }
}

internal static class OpenApiGenerator
{
    public static string GenerateJson(IReadOnlyList<RouteMeta> routes, OpenApiOptions options)
    {
        var paths = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);

        foreach (var route in routes)
        {
            // Skip the OpenAPI and reference endpoints themselves
            if (string.Equals(route.Path, options.DocumentPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(route.Path, options.ReferencePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(route.Path, options.SwaggerPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (openApiPath, pathParams) = TranslatePath(route.Path);
            if (!paths.TryGetValue(openApiPath, out var operations))
            {
                operations = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                paths[openApiPath] = operations;
            }

            var method = route.Method.ToLowerInvariant();
            var operation = new Dictionary<string, object?>();

            if (!string.IsNullOrWhiteSpace(route.Summary))
                operation["summary"] = route.Summary;

            if (!string.IsNullOrWhiteSpace(route.Description))
                operation["description"] = route.Description;

            if (route.Tags.Count > 0)
                operation["tags"] = route.Tags;

            if (!string.IsNullOrWhiteSpace(route.Name))
                operation["operationId"] = route.Name;

            // Parameters
            var parameters = new List<Dictionary<string, object?>>();
            foreach (var p in pathParams)
            {
                parameters.Add(new Dictionary<string, object?>
                {
                    ["name"] = p,
                    ["in"] = "path",
                    ["required"] = true,
                    ["schema"] = new Dictionary<string, object?> { ["type"] = "string" }
                });
            }

            if (parameters.Count > 0)
                operation["parameters"] = parameters;

            // Request body
            if (!string.IsNullOrEmpty(route.FileFieldName))
            {
                var fileProp = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["format"] = "binary",
                };
                if (!string.IsNullOrWhiteSpace(route.FileDescription))
                    fileProp["description"] = route.FileDescription;

                var multipartSchema = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        [route.FileFieldName!] = fileProp,
                    },
                };
                if (route.FileRequired)
                    multipartSchema["required"] = new[] { route.FileFieldName! };

                operation["requestBody"] = new Dictionary<string, object?>
                {
                    ["required"] = route.FileRequired,
                    ["content"] = new Dictionary<string, object?>
                    {
                        ["multipart/form-data"] = new Dictionary<string, object?>
                        {
                            ["schema"] = multipartSchema,
                        },
                    },
                };
            }
            else if (route.Validator is IValidatorMeta validatorMeta && route.InputType is not null)
            {
                operation["requestBody"] = new Dictionary<string, object?>
                {
                    ["required"] = true,
                    ["content"] = new Dictionary<string, object?>
                    {
                        ["application/json"] = new Dictionary<string, object?>
                        {
                            ["schema"] = GenerateValidatorSchema(validatorMeta, route.InputType)
                        }
                    }
                };
            }
            else if (route.InputType is not null)
            {
                operation["requestBody"] = new Dictionary<string, object?>
                {
                    ["required"] = true,
                    ["content"] = new Dictionary<string, object?>
                    {
                        ["application/json"] = new Dictionary<string, object?>
                        {
                            ["schema"] = GenerateTypeSchema(route.InputType)
                        }
                    }
                };
            }

            // Responses
            var responses = new Dictionary<string, object?>();
            if (route.Produces.Count > 0)
            {
                foreach (var (status, type) in route.Produces)
                {
                    var responseObj = new Dictionary<string, object?>
                    {
                        ["description"] = GetStatusDescription(status)
                    };

                    if (type is not null)
                    {
                        responseObj["content"] = new Dictionary<string, object?>
                        {
                            ["application/json"] = new Dictionary<string, object?>
                            {
                                ["schema"] = GenerateTypeSchema(type)
                            }
                        };
                    }

                    responses[status.ToString()] = responseObj;
                }
            }
            else
            {
                var defaultStatus = method == "post" ? "201" : "200";
                responses[defaultStatus] = new Dictionary<string, object?>
                {
                    ["description"] = GetStatusDescription(int.Parse(defaultStatus))
                };
            }

            operation["responses"] = responses;
            operations[method] = operation;
        }

        var doc = new Dictionary<string, object?>
        {
            ["openapi"] = "3.1.0",
            ["info"] = new Dictionary<string, object?>
            {
                ["title"] = options.Title,
                ["version"] = options.Version,
                ["description"] = options.Description ?? $"{options.Title} documentation powered by Reptar."
            },
            ["paths"] = paths
        };

        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    internal static (string Path, List<string> Params) TranslatePath(string reptarPath)
    {
        var segments = reptarPath.Split('/');
        var translated = new StringBuilder();
        var paramNames = new List<string>();

        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (i > 0) translated.Append('/');

            if (seg.StartsWith(':') && seg.Length > 1)
            {
                var paramName = seg.Substring(1);
                paramNames.Add(paramName);
                translated.Append('{').Append(paramName).Append('}');
            }
            else
            {
                translated.Append(seg);
            }
        }

        return (translated.ToString(), paramNames);
    }

    internal static Dictionary<string, object?> GenerateValidatorSchema(IValidatorMeta validatorMeta, Type inputType)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object"
        };

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<string>();

        foreach (var prop in validatorMeta.Properties)
        {
            var propSchema = GenerateTypeSchema(prop.PropertyType);

            if (prop.MinLength.HasValue)
                propSchema["minLength"] = prop.MinLength.Value;

            if (prop.MaxLength.HasValue)
                propSchema["maxLength"] = prop.MaxLength.Value;

            if (prop.ExactLength.HasValue)
            {
                propSchema["minLength"] = prop.ExactLength.Value;
                propSchema["maxLength"] = prop.ExactLength.Value;
            }

            if (prop.MinValue.HasValue)
                propSchema["minimum"] = prop.MinValue.Value;

            if (prop.MaxValue.HasValue)
                propSchema["maximum"] = prop.MaxValue.Value;

            if (prop.IsEmail)
                propSchema["format"] = "email";

            if (prop.IsUrl)
                propSchema["format"] = "uri";

            if (!string.IsNullOrEmpty(prop.Pattern))
                propSchema["pattern"] = prop.Pattern;

            properties[prop.Name] = propSchema;

            if (!prop.IsOptional)
                required.Add(prop.Name);
        }

        schema["properties"] = properties;
        if (required.Count > 0)
            schema["required"] = required;

        return schema;
    }

    internal static Dictionary<string, object?> GenerateTypeSchema(Type type)
    {
        var schema = new Dictionary<string, object?>();
        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;

        if (unwrapped == typeof(string))
        {
            schema["type"] = "string";
            return schema;
        }

        if (unwrapped == typeof(int) || unwrapped == typeof(long) || unwrapped == typeof(short) || unwrapped == typeof(byte))
        {
            schema["type"] = "integer";
            return schema;
        }

        if (unwrapped == typeof(float) || unwrapped == typeof(double) || unwrapped == typeof(decimal))
        {
            schema["type"] = "number";
            return schema;
        }

        if (unwrapped == typeof(bool))
        {
            schema["type"] = "boolean";
            return schema;
        }

        if (unwrapped == typeof(Guid))
        {
            schema["type"] = "string";
            schema["format"] = "uuid";
            return schema;
        }

        if (unwrapped == typeof(DateTime) || unwrapped == typeof(DateTimeOffset))
        {
            schema["type"] = "string";
            schema["format"] = "date-time";
            return schema;
        }

        if (unwrapped == typeof(FormFile))
        {
            schema["type"] = "string";
            schema["format"] = "binary";
            return schema;
        }

        if (unwrapped.IsEnum)
        {
            schema["type"] = "string";
            schema["enum"] = Enum.GetNames(unwrapped);
            return schema;
        }

        // Object properties
        schema["type"] = "object";
        var properties = new Dictionary<string, object?>();

        foreach (var prop in unwrapped.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            var propName = char.ToLowerInvariant(prop.Name[0]) + prop.Name.Substring(1);
            properties[propName] = GenerateTypeSchema(prop.PropertyType);
        }

        if (properties.Count > 0)
        {
            schema["properties"] = properties;
        }

        return schema;
    }

    private static string GetStatusDescription(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        202 => "Accepted",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        422 => "Unprocessable Entity",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        _ => "Response"
    };
}
#endif

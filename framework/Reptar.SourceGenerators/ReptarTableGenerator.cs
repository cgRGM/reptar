using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Reptar.SourceGenerators;

[Generator]
public sealed class ReptarTableGenerator : IIncrementalGenerator
{
    private sealed class TableFieldInfo
    {
        public string? Namespace { get; set; }
        public string ContainingTypeName { get; set; } = "";
        public string FullContainingTypeName { get; set; } = "";
        public bool IsStaticClass { get; set; }
        public bool IsPartialContainingType { get; set; }
        public bool CanAutoInit { get; set; }
        public string FieldName { get; set; } = "";
        public string TableName { get; set; } = "";
        public List<(string SqlName, string PascalName, string ClrType)> Columns { get; } = new();
        public HashSet<string> ExistingMemberNames { get; } = new(StringComparer.Ordinal);
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsCandidate(node),
                transform: static (ctx, _) => GetTableFieldInfo(ctx))
            .Where(static m => m is not null);

        context.RegisterSourceOutput(candidates.Collect(),
            static (spc, items) => Execute(items!, spc));
    }

    private static bool IsCandidate(SyntaxNode node)
    {
        if (node is not VariableDeclaratorSyntax declarator || declarator.Initializer is null)
            return false;

        var text = declarator.Initializer.Value.ToString();
        return text.IndexOf("DefineTable(", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("TableDef.Define(", StringComparison.Ordinal) >= 0;
    }

    private static TableFieldInfo? GetTableFieldInfo(GeneratorSyntaxContext context)
    {
        var declarator = (VariableDeclaratorSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declarator) is not IFieldSymbol fieldSymbol)
            return null;

        if (!fieldSymbol.IsStatic || fieldSymbol.Type.Name != "TableDef")
            return null;

        var containingType = fieldSymbol.ContainingType;
        if (containingType is null || containingType.ContainingType is not null)
            return null;

        var invocation = declarator.Initializer?.Value
            .DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(inv =>
            {
                var expr = inv.Expression.ToString();
                return expr.EndsWith("DefineTable", StringComparison.Ordinal) ||
                       expr.EndsWith("TableDef.Define", StringComparison.Ordinal);
            });

        if (invocation is null || invocation.ArgumentList.Arguments.Count == 0)
            return null;

        var firstArg = invocation.ArgumentList.Arguments[0].Expression;
        if (firstArg is not LiteralExpressionSyntax tableLiteral ||
            !tableLiteral.IsKind(SyntaxKind.StringLiteralExpression))
            return null;

        var tableName = tableLiteral.Token.ValueText;
        if (string.IsNullOrWhiteSpace(tableName))
            return null;

        var isPartial = containingType.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<TypeDeclarationSyntax>()
            .Any(t => t.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));

        var isStaticClass = containingType.IsStatic;
        var canAutoInit =
            (fieldSymbol.DeclaredAccessibility == Accessibility.Public ||
             fieldSymbol.DeclaredAccessibility == Accessibility.Internal) &&
            (containingType.DeclaredAccessibility == Accessibility.Public ||
             containingType.DeclaredAccessibility == Accessibility.Internal);

        var info = new TableFieldInfo
        {
            Namespace = containingType.ContainingNamespace.IsGlobalNamespace
                ? null
                : containingType.ContainingNamespace.ToDisplayString(),
            ContainingTypeName = containingType.Name,
            FullContainingTypeName = containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            IsStaticClass = isStaticClass,
            IsPartialContainingType = isPartial,
            CanAutoInit = canAutoInit,
            FieldName = fieldSymbol.Name,
            TableName = tableName
        };

        foreach (var member in containingType.GetMembers())
        {
            info.ExistingMemberNames.Add(member.Name);
        }

        var seenCols = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < invocation.ArgumentList.Arguments.Count; i++)
        {
            var argExpr = invocation.ArgumentList.Arguments[i].Expression;
            if (TryExtractColumn(argExpr, out var sqlName, out var clrType))
            {
                var pascal = ToPascalCase(sqlName);
                if (!string.IsNullOrEmpty(pascal) && seenCols.Add(pascal))
                {
                    info.Columns.Add((sqlName, pascal, clrType));
                }
            }
        }

        return info;
    }

    private static bool TryExtractColumn(ExpressionSyntax expr, out string sqlName, out string clrType)
    {
        sqlName = "";
        clrType = "string";

        foreach (var inv in expr.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var methodName = memberAccess.Name.Identifier.Text;
                if (methodName is "Text" or "Int" or "Real" or "Blob" &&
                    inv.ArgumentList.Arguments.Count > 0 &&
                    inv.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax colLit &&
                    colLit.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    sqlName = colLit.Token.ValueText;
                    clrType = methodName switch
                    {
                        "Int" => "int",
                        "Real" => "double",
                        "Blob" => "byte[]",
                        _ => "string"
                    };
                    return !string.IsNullOrWhiteSpace(sqlName);
                }
            }
        }

        return false;
    }

    private static void Execute(ImmutableArray<TableFieldInfo> items, SourceProductionContext context)
    {
        if (items.IsDefaultOrEmpty)
            return;

        var index = 0;
        foreach (var item in items)
        {
            if (item is null)
                continue;

            index++;
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();

            if (item.Namespace is not null)
            {
                sb.AppendLine($"namespace {item.Namespace};");
                sb.AppendLine();
            }

            if (item.IsPartialContainingType && item.Columns.Count > 0)
            {
                var staticMod = item.IsStaticClass ? "static " : "";
                sb.AppendLine($"{staticMod}partial class {item.ContainingTypeName}");
                sb.AppendLine("{");

                if (string.Equals(item.FieldName, "Table", StringComparison.Ordinal))
                {
                    foreach (var (sqlName, pascalName, clrType) in item.Columns)
                    {
                        if (!item.ExistingMemberNames.Contains(pascalName))
                        {
                            sb.AppendLine($"    public static readonly global::Reptar.ColumnRef<{clrType}> {pascalName} = new(\"{item.TableName}\", \"{sqlName}\");");
                        }
                    }
                    sb.AppendLine();
                }

                var colsClassName = $"{item.FieldName}Cols";
                if (!item.ExistingMemberNames.Contains(colsClassName))
                {
                    sb.AppendLine($"    public static class {colsClassName}");
                    sb.AppendLine("    {");
                    foreach (var (sqlName, pascalName, clrType) in item.Columns)
                    {
                        sb.AppendLine($"        public static readonly global::Reptar.ColumnRef<{clrType}> {pascalName} = new(\"{item.TableName}\", \"{sqlName}\");");
                    }
                    sb.AppendLine("    }");
                }

                sb.AppendLine("}");
                sb.AppendLine();
            }

            if (item.CanAutoInit)
            {
                var initClass = $"__RegisterTable_{item.ContainingTypeName}_{item.FieldName}_{index}";
                sb.AppendLine($"file static class {initClass}");
                sb.AppendLine("{");
                sb.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
                sb.AppendLine("    internal static void Init()");
                sb.AppendLine("    {");
                sb.AppendLine($"        global::Reptar.SchemaRegistry.Register({item.FullContainingTypeName}.{item.FieldName});");
                sb.AppendLine("    }");
                sb.AppendLine("}");
            }

            var nsPrefix = item.Namespace is null ? "Global" : item.Namespace.Replace('.', '_');
            var fileName = $"{nsPrefix}_{item.ContainingTypeName}_{item.FieldName}_TableCols.g.cs";
            context.AddSource(fileName, SourceText.From(sb.ToString(), Encoding.UTF8));
        }
    }

    private static string ToPascalCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var parts = value.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            sb.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
                sb.Append(part.Substring(1));
        }
        return sb.ToString();
    }
}

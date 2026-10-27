using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Reptar.SourceGenerators;

/// <summary>
/// Compile-time workflow graph validation. Mirrors the Microsoft Agent
/// Framework's ExecutorRouteGenerator philosophy: the message flow between
/// steps is statically analyzed instead of wired at runtime.
///
/// For every type containing methods annotated with
/// <c>[WorkflowStep(id, Produces = [...], Consumes = [...])]</c>, this
/// generator emits a <c>&lt;TypeName&gt;Graph</c> static class in the same
/// namespace holding the step list, the resolved message edges, and the
/// incomplete-graph message sets — and reports REPTARWF001/REPTARWF002
/// warnings whenever a produced message has no consumer or a consumed
/// message has no producer, so a broken graph fails build review instead
/// of a live demo.
/// </summary>
[Generator]
public sealed class ReptarWorkflowGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor UnhandledMessageRule = new(
        "REPTARWF001",
        "Workflow message has no consumer",
        "Workflow '{0}': message '{1}' produced by step '{2}' is not consumed by any step. Add a consuming step, or mark the producer with IsOutput = true.",
        "Reptar.Agents",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Every produced workflow message must flow to a consuming step or be a declared workflow output.");

    private static readonly DiagnosticDescriptor OrphanConsumerRule = new(
        "REPTARWF002",
        "Workflow step consumes an unproduced message",
        "Workflow '{0}': step '{1}' consumes message '{2}' but no step produces it",
        "Reptar.Agents",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Every consumed workflow message must be produced by some step.");

    private sealed class StepInfo
    {
        public string Namespace { get; set; } = "";
        public string TypeName { get; set; } = "";
        public string Id { get; set; } = "";
        public string[] Produces { get; set; } = [];
        public string[] Consumes { get; set; } = [];
        public bool IsOutput { get; set; }
        public Location Location { get; set; } = Location.None;
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var steps = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "Reptar.WorkflowStepAttribute",
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, _) => GetStepInfo(ctx))
            .Where(static step => step is not null);

        context.RegisterSourceOutput(
            steps.Collect(),
            static (spc, collected) => Execute(collected!, spc));
    }

    private static StepInfo? GetStepInfo(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not IMethodSymbol method ||
            method.ContainingType is null ||
            method.ContainingType.ContainingType is not null)
        {
            return null;
        }

        foreach (var attribute in context.Attributes)
        {
            if (attribute.AttributeClass is null ||
                attribute.AttributeClass.ToDisplayString() is not
                    ("Reptar.WorkflowStepAttribute" or "Reptar.WorkflowStep"))
            {
                continue;
            }

            if (attribute.ConstructorArguments.IsEmpty ||
                attribute.ConstructorArguments[0].Value is not string id ||
                string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return new StepInfo
            {
                Namespace = method.ContainingType.ContainingNamespace.IsGlobalNamespace
                    ? ""
                    : method.ContainingType.ContainingNamespace.ToDisplayString(),
                TypeName = method.ContainingType.Name,
                Id = id,
                Produces = ReadMessageNames(attribute, "Produces"),
                Consumes = ReadMessageNames(attribute, "Consumes"),
                IsOutput = ReadBool(attribute, "IsOutput"),
                Location = method.Locations.FirstOrDefault() ?? Location.None
            };
        }

        return null;
    }

    private static string[] ReadMessageNames(AttributeData attribute, string propertyName)
    {
        foreach (var named in attribute.NamedArguments)
        {
            if (string.Equals(named.Key, propertyName, StringComparison.Ordinal) &&
                named.Value.Kind == TypedConstantKind.Array)
            {
                return named.Value.Values
                    .Select(value => value.Value as string)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToArray();
            }
        }

        return [];
    }

    private static bool ReadBool(AttributeData attribute, string propertyName)
    {
        foreach (var named in attribute.NamedArguments)
        {
            if (string.Equals(named.Key, propertyName, StringComparison.Ordinal) &&
                named.Value.Value is bool flag)
            {
                return flag;
            }
        }

        return false;
    }

    private static void Execute(ImmutableArray<StepInfo> steps, SourceProductionContext context)
    {
        var grouped = steps
            .GroupBy(step => (step.Namespace, step.TypeName))
            .ToList();

        foreach (var group in grouped)
        {
            EmitWorkflow(group.ToList(), context);
        }
    }

    private static void EmitWorkflow(List<StepInfo> steps, SourceProductionContext context)
    {
        var first = steps[0];
        var namespaceName = first.Namespace;
        var typeName = first.TypeName;
        var graphClassName = $"{typeName}Graph";

        var produces = steps
            .SelectMany(step => step.Produces.Select(message => (Step: step, Message: message)))
            .ToList();
        var consumes = steps
            .SelectMany(step => step.Consumes.Select(message => (Step: step, Message: message)))
            .ToList();

        var producedMessages = new HashSet<string>(
            produces.Select(pair => pair.Message), StringComparer.Ordinal);
        var consumedMessages = new HashSet<string>(
            consumes.Select(pair => pair.Message), StringComparer.Ordinal);

        var unhandled = new List<string>();
        foreach (var (step, message) in produces)
        {
            if (!consumedMessages.Contains(message) && !step.IsOutput)
            {
                unhandled.Add(message);
                context.ReportDiagnostic(Diagnostic.Create(
                    UnhandledMessageRule, step.Location, typeName, message, step.Id));
            }
        }

        var orphans = new List<string>();
        foreach (var (step, message) in consumes)
        {
            if (!producedMessages.Contains(message))
            {
                orphans.Add(message);
                context.ReportDiagnostic(Diagnostic.Create(
                    OrphanConsumerRule, step.Location, typeName, step.Id, message));
            }
        }

        var edges = new List<string>();
        foreach (var (step, message) in consumes)
        {
            var producer = produces.FirstOrDefault(pair =>
                pair.Message == message &&
                !string.Equals(pair.Step.Id, step.Id, StringComparison.Ordinal));
            if (producer.Step is not null)
            {
                edges.Add($"new Reptar.WorkflowEdge(\"{Escape(producer.Step.Id)}\", \"{Escape(message)}\", \"{Escape(step.Id)}\")");
            }
        }

        var source = BuildSource(namespaceName, graphClassName, typeName, steps, produces, consumes, edges, unhandled, orphans);
        context.AddSource($"{graphClassName}.g.cs", SourceText.From(source, Encoding.UTF8));
    }

    private static string BuildSource(
        string namespaceName,
        string graphClassName,
        string typeName,
        List<StepInfo> steps,
        List<(StepInfo Step, string Message)> produces,
        List<(StepInfo Step, string Message)> consumes,
        List<string> edges,
        List<string> unhandled,
        List<string> orphans)
    {
        var builder = new StringBuilder();

        builder.AppendLine("// <auto-generated/>");
        if (!string.IsNullOrEmpty(namespaceName))
        {
            builder.AppendLine($"namespace {namespaceName}");
        }
        else
        {
            builder.AppendLine("namespace global");
        }

        builder.AppendLine("{");
        builder.AppendLine($"    /// <summary>Compile-time graph for the '{typeName}' workflow (generated by ReptarWorkflowGenerator).</summary>");
        builder.AppendLine($"    public static class {graphClassName}");
        builder.AppendLine("    {");
        builder.AppendLine($"        public const string WorkflowName = \"{Escape(typeName)}\";");
        builder.AppendLine();

        builder.Append("        public static readonly string[] Steps = new string[] { ");
        builder.Append(string.Join(", ", steps.Select(step => $"\"{Escape(step.Id)}\"")));
        builder.AppendLine(" };");
        builder.AppendLine();

        builder.Append("        public static readonly string[] ProducedMessages = new string[] { ");
        builder.Append(string.Join(", ", produces.Select(pair => $"\"{Escape(pair.Message)}\"").Distinct()));
        builder.AppendLine(" };");
        builder.AppendLine();

        builder.Append("        public static readonly string[] ConsumedMessages = new string[] { ");
        builder.Append(string.Join(", ", consumes.Select(pair => $"\"{Escape(pair.Message)}\"").Distinct()));
        builder.AppendLine(" };");
        builder.AppendLine();

        builder.Append("        public static readonly Reptar.WorkflowEdge[] Edges = new Reptar.WorkflowEdge[] { ");
        builder.Append(string.Join(", ", edges));
        builder.AppendLine(" };");
        builder.AppendLine();

        builder.Append("        public static readonly string[] UnhandledMessages = new string[] { ");
        builder.Append(string.Join(", ", unhandled.Select(message => $"\"{Escape(message)}\"")));
        builder.AppendLine(" };");
        builder.AppendLine();

        builder.Append("        public static readonly string[] OrphanConsumers = new string[] { ");
        builder.Append(string.Join(", ", orphans.Select(message => $"\"{Escape(message)}\"")));
        builder.AppendLine(" };");
        builder.AppendLine();

        builder.AppendLine("        public static bool IsComplete => UnhandledMessages.Length == 0 && OrphanConsumers.Length == 0;");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static string Escape(string value) => value.Replace("\"", "\\\"");
}

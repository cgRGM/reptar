using System;

namespace Reptar;

/// <summary>
/// G16 — Declares one step of a compile-time-validated agent workflow graph.
/// Apply to a static method; the Reptar workflow source generator turns the
/// annotated methods of a type into a <c>&lt;TypeName&gt;Graph</c> static class
/// with the step list, message edges, and the unhandled/orphan message sets —
/// and reports a build warning whenever a produced message has no consumer.
///
/// Semantics:
///  - <see cref="Produces"/> names the messages this step emits.
///  - <see cref="Consumes"/> names the messages this step waits on.
///  - Every produced message must be consumed by another step — unless the
///    producing step declares <see cref="IsOutput"/>, marking its messages
///    as terminal workflow outputs (the MSFT YieldsOutput intent).
///
/// These attribute and edge types are platform-neutral metadata on purpose:
/// they are referenced from generated code, so they must exist on every
/// compile target including NetWasm.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WorkflowStepAttribute(string id) : Attribute
{
    /// <summary>The unique step id inside the containing workflow type.</summary>
    public string Id { get; } = id;

    /// <summary>Messages this step produces.</summary>
    public string[] Produces { get; set; } = [];

    /// <summary>Messages this step consumes.</summary>
    public string[] Consumes { get; set; } = [];

    /// <summary>
    /// True when this step's produced messages are workflow outputs allowed
    /// to leave the graph (no downstream consumer required).
    /// </summary>
    public bool IsOutput { get; set; }
}

/// <summary>
/// One edge of a generated workflow graph: the producing step, the message
/// name, and the consuming step.
/// </summary>
public sealed record WorkflowEdge(string From, string Message, string To);

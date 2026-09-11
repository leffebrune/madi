namespace Madi.Core;

/// <summary>A snapshot of the environment used by the agent's tools.</summary>
public sealed record AgentRuntimeContext(
    DateOnly CurrentDate,
    string WorkingDirectory,
    string OperatingSystem,
    string Shell);

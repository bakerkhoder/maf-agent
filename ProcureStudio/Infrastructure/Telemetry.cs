using System.Diagnostics;

namespace ProcureStudio.Infrastructure;

/// <summary>
/// ONE telemetry name used everywhere (chat client, agents, our own spans, and AddSource in Program.cs).
/// If these names differ, OpenTelemetry silently records nothing: no error, just silence.
/// </summary>
public static class Tel
{
    public const string Name = "ProcureStudio";
    public static readonly ActivitySource Source = new($"{Name}.Tools");   // our custom spans (matched by AddSource("ProcureStudio*"))
}

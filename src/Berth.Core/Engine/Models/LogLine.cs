namespace Berth.Engine.Models;

/// <summary>Which of a container's output streams a log line came from.</summary>
public enum LogStream
{
    Stdout,
    Stderr,
}

/// <summary>
/// One line of a container's log. <see cref="Text"/> has no trailing newline and no ANSI escape codes;
/// <see cref="Timestamp"/> is null when the line did not start with one.
/// </summary>
public sealed record LogLine(LogStream Stream, DateTimeOffset? Timestamp, string Text);

/// <summary>Which log lines to read: <see cref="Tail"/> lines from the end (-1 for all), then new ones as they come when <see cref="Follow"/>.</summary>
public sealed record LogRequest(int Tail, bool Follow);

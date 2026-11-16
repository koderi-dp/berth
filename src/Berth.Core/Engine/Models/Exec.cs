namespace Berth.Engine.Models;

/// <summary>A terminal's size in character cells.</summary>
public sealed record TerminalSize(int Columns, int Rows)
{
    /// <summary>Whether the size is one Berth sends: 2 to 1000 columns and 1 to 500 rows.</summary>
    public bool IsValid => Columns is >= 2 and <= 1000 && Rows is >= 1 and <= 500;
}

/// <summary>An exec as the engine reports it. <see cref="ExitCode"/> is null while it runs or when the engine gave none.</summary>
public sealed record ExecInspection(bool Running, int? ExitCode);

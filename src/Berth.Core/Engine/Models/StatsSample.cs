namespace Berth.Engine.Models;

/// <summary>
/// One reading of a running container's resource use. <see cref="CpuPercent"/> is of one CPU, so a container busy on two CPUs
/// reads 200. <see cref="MemoryBytes"/> leaves out page cache the kernel can reclaim. Network and block I/O figures are totals
/// since the container started, summed over every interface and device. <see cref="Time"/> is
/// <see cref="DateTimeOffset.MinValue"/> when the engine sent none. No figure is ever negative or NaN.
/// </summary>
public sealed record StatsSample(
    DateTimeOffset Time,
    double CpuPercent,
    long MemoryBytes,
    long MemoryLimitBytes,
    long NetworkReceivedBytes,
    long NetworkSentBytes,
    long BlockReadBytes,
    long BlockWrittenBytes,
    int Pids);

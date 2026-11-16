using System.Diagnostics;
using Berth.Processes;

namespace Berth;

/// <summary>Asks <c>wsl.exe -l -q</c> for the WSL distributions (read only), for the connection editor's "Use the WSL default".</summary>
internal static class WslList
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>The distributions, the default first; empty when WSL is not installed, has none, or did not answer in time. Never throws.</summary>
    public static async Task<IReadOnlyList<string>> LoadAsync()
    {
        try
        {
            var start = new ProcessStartInfo("wsl.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-l");
            start.ArgumentList.Add("-q");

            using var process = Process.Start(start);
            if (process is null)
                return [];
            using var cancel = new CancellationTokenSource(Limit);
            using var bytes = new MemoryStream();
            var errors = process.StandardError.ReadToEndAsync(cancel.Token);
            try
            {
                await process.StandardOutput.BaseStream.CopyToAsync(bytes, cancel.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(cancel.Token).ConfigureAwait(false);
                await errors.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                AppLog.Write("listing the WSL distributions took too long");
                return [];
            }

            return process.ExitCode == 0 ? WslDistros.Parse(bytes.ToArray()) : [];
        }
        catch (Exception ex)
        {
            AppLog.Error("listing the WSL distributions failed", ex);
            return [];
        }
    }
}

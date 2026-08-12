using System.Diagnostics;
using System.Text;

namespace PortableOCR.WinUI3.Services;

internal sealed class ProcessRunner
{
    private readonly HashSet<Process> _children = [];
    private readonly object _gate = new();

    public async Task<(string StdOut, string StdErr)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct, string? workingDirectory = null, IDictionary<string, string?>? environment = null)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workingDirectory ?? System.IO.Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var pair in environment) psi.Environment[pair.Key] = pair.Value;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        lock (_gate) _children.Add(process);
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start {System.IO.Path.GetFileName(exe)}.");
            using var reg = ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{System.IO.Path.GetFileName(exe)} exited with code {process.ExitCode}{(string.IsNullOrWhiteSpace(stderr) ? string.Empty : $": {stderr.Trim()}")}");
            return (stdout, stderr);
        }
        finally
        {
            lock (_gate) _children.Remove(process);
        }
    }

    public void CancelAll()
    {
        Process[] list;
        lock (_gate) list = [.. _children];
        foreach (var process in list)
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}

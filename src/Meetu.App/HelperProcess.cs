using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Meetu.App;

internal readonly record struct HelperResult(int ExitCode, bool Cancelled, string? Error);

// One run at a time of the helper executable. Only content-free status lines cross the process boundary.
internal sealed class HelperProcess
{
    private const string ErrorPrefix = "error ";
    private readonly Lock gate = new();
    private Process? current;
    private bool cancelled;

    public bool IsRunning { get { lock (gate) return current is not null; } }

    public async Task<HelperResult> RunAsync(IEnumerable<string> arguments, Action<string> onLine)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "meetus.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the helper process.");
        lock (gate)
        {
            current = process;
            cancelled = false;
        }
        string? error = null;
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.StartsWith(ErrorPrefix, StringComparison.Ordinal)) error = line[ErrorPrefix.Length..];
                else onLine(line);
            }
            await process.WaitForExitAsync();
        }
        finally
        {
            lock (gate) current = null;
        }
        return new HelperResult(process.ExitCode, cancelled, error);
    }

    public void Cancel()
    {
        lock (gate)
        {
            if (current is null) return;
            cancelled = true;
            // It may have finished on its own at this very moment, which is just as good.
            try { current.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
    }

    internal static bool TryParseProgress(string line, out long completed, out long total)
    {
        completed = total = 0;
        var parts = line.Split(' ');
        return parts.Length == 3 && parts[0] == "progress" &&
            long.TryParse(parts[1], CultureInfo.InvariantCulture, out completed) &&
            long.TryParse(parts[2], CultureInfo.InvariantCulture, out total);
    }
}

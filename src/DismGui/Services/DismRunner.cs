using System.Diagnostics;
using System.Text;

namespace DismGui.Services;

public sealed record DismResult(int ExitCode, string Output);
public interface IDismRunner
{
    Task<DismResult> RunAsync(DismCommand command);
}

public sealed class DismRunner(SessionLog log) : IDismRunner
{
    public async Task<DismResult> RunAsync(DismCommand command)
    {
        log.Write("COMMAND", command.ToString());
        var nativeSystem = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), nativeSystem, "dism.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Default, StandardErrorEncoding = Encoding.Default
        };
        foreach (var arg in command.Arguments) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        var timer = Stopwatch.StartNew();
        process.Start();
        var output = new StringBuilder();
        Exception? logFailure = null;
        var gate = new object();
        async Task Pump(StreamReader reader, string level)
        {
            var buffer = new char[1024];
            var line = new StringBuilder();
            void Emit()
            {
                if (line.Length == 0) return;
                var text = line.ToString();
                line.Clear();
                lock (gate)
                {
                    output.AppendLine(text);
                    // Drain both pipes even if logging fails, so DISM is never left blocked on output.
                    try { log.Write(level, text); } catch (Exception ex) { logFailure ??= ex; }
                }
            }
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
                for (var i = 0; i < count; i++)
                    if (buffer[i] is '\r' or '\n') Emit(); else line.Append(buffer[i]);
            Emit();
        }
        await Task.WhenAll(Pump(process.StandardOutput, "DISM"), Pump(process.StandardError, "STDERR"), process.WaitForExitAsync());
        if (logFailure != null) throw new IOException($"Session logging failed while DISM was running (exit {process.ExitCode}). Inspect mounted images before continuing.", logFailure);
        log.Write("RESULT", $"Exit code {process.ExitCode}; elapsed {timer.Elapsed.TotalSeconds:F1} seconds.");
        if (process.ExitCode is not (0 or 3010)) throw new InvalidOperationException($"DISM failed with exit code {process.ExitCode}. See the session log for details.");
        if (process.ExitCode == 3010) log.Write("WARN", "DISM reports that a restart is required.");
        return new(process.ExitCode, output.ToString());
    }
}

public sealed class ImageServicing(IDismRunner runner, Action<string> report)
{
    public async Task InjectAsync(InjectionPlan plan, Func<bool> stopAfterEdition)
    {
        DismCommands.Validate(plan);
        var mounts = DismCommands.ParseMounted((await runner.RunAsync(DismCommands.Mounted())).Output);
        if (mounts.Any(m => DismCommands.Overlaps(m.MountPath, plan.MountPath) ||
            m.ImagePath.Equals(plan.ImagePath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This image or mount folder is already in use. Inspect Mounted images first.");
        Directory.CreateDirectory(plan.MountPath);
        var completed = new List<int>();
        foreach (var index in plan.Indexes)
        {
            if (stopAfterEdition()) break;
            var mountAttempted = false;
            try
            {
                report($"Mounting image index {index}…");
                // Even a failed mount may leave a registered mount that needs recovery.
                mountAttempted = true;
                await runner.RunAsync(DismCommands.Mount(plan, index));
                foreach (var folder in plan.DriverFolders)
                {
                    report($"Adding drivers to index {index}: {folder}");
                    await runner.RunAsync(DismCommands.AddDrivers(plan, folder));
                }
                report($"Saving and unmounting index {index}…");
                await runner.RunAsync(DismCommands.Unmount(plan.MountPath, true));
                completed.Add(index);
                report($"Saved index {index} ({completed.Count}/{plan.Indexes.Length}).");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Stopped at index {index}. Saved indexes: {(completed.Count == 0 ? "none" : string.Join(", ", completed))}. " +
                    (mountAttempted ? $"Inspect Mounted images for {plan.MountPath}; use remount, save, or discard to recover. " : "") + ex.Message, ex);
            }
        }
        report(completed.Count == plan.Indexes.Length ? "Completed. All selected editions were saved." : $"Stopped safely. Saved {completed.Count}/{plan.Indexes.Length} editions.");
    }
}

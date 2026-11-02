using System.Text;

namespace DismGui.Services;

public sealed class SessionLog : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter writer;
    public string DirectoryPath { get; }
    public string FilePath { get; }
    public event Action<string>? LineWritten;

    public SessionLog(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DismStudio", "Logs");
        Directory.CreateDirectory(DirectoryPath);
        FilePath = Path.Combine(DirectoryPath, $"session-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        Write("INFO", $"DISM Studio session started. App={typeof(SessionLog).Assembly.GetName().Version}; OS={Environment.OSVersion}; 64bit={Environment.Is64BitProcess}");
    }

    public void Write(string level, string message)
    {
        lock (gate)
        {
            foreach (var part in message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (part.Length == 0) continue;
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {part}";
                writer.WriteLine(line);
                LineWritten?.Invoke(line);
            }
        }
    }

    public string ReadAll()
    {
        lock (gate)
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }

    public void Dispose()
    {
        lock (gate) { Write("INFO", "Session ended."); writer.Dispose(); }
    }
}

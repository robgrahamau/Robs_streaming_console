using System.Collections.Concurrent;
using System.Text;

namespace Steaming.Core.Services;

public static class DebugLogFile
{
    private static readonly object _configLock = new();
    private static string? _configuredPath;

    // Queue-based async writer — callers enqueue and return immediately.
    // A dedicated background thread drains the queue, keeping disk I/O
    // completely off the EventBus / pipe read-loop threads.
    private static readonly ConcurrentQueue<string> _queue = new();
    private static readonly SemaphoreSlim _signal = new(0);

    static DebugLogFile()
    {
        var t = new Thread(DrainLoop) { IsBackground = true, Name = "DebugLogDrain" };
        t.Start();
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Steaming", "debug.log");

    public static void Configure(string? path)
    {
        lock (_configLock)
            _configuredPath = string.IsNullOrWhiteSpace(path) ? DefaultPath : path.Trim();
    }

    public static string GetPath()
    {
        lock (_configLock)
            return string.IsNullOrWhiteSpace(_configuredPath) ? DefaultPath : _configuredPath;
    }

    // Enqueues immediately — never blocks the caller.
    public static void Append(string message)
    {
        _queue.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
        try { _signal.Release(); } catch { }
    }

    public static void Clear()
    {
        // Flush pending lines, then overwrite with empty file.
        _queue.Enqueue("\x01CLEAR\x01");
        try { _signal.Release(); } catch { }
    }

    public static string ReadAll()
    {
        var path = GetPath();
        try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty; }
        catch { return string.Empty; }
    }

    private static void DrainLoop()
    {
        while (true)
        {
            try { _signal.Wait(); } catch { }

            var path = GetPath();
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                using var sw = new StreamWriter(path, append: true, Encoding.UTF8);
                while (_queue.TryDequeue(out var line))
                {
                    if (line == "\x01CLEAR\x01")
                    {
                        sw.Close();
                        File.WriteAllText(path, string.Empty, Encoding.UTF8);
                        // Reopen for any remaining lines after the clear
                        using var sw2 = new StreamWriter(path, append: true, Encoding.UTF8);
                        while (_queue.TryDequeue(out var after))
                            sw2.WriteLine(after);
                        break;
                    }
                    sw.WriteLine(line);
                }
            }
            catch { /* never crash the background drain */ }
        }
    }
}

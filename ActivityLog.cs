namespace PaneWeaver;

internal sealed class ActivityLog
{
    private readonly object gate = new();
    private readonly Queue<string> lines = new();

    internal event Action<string>? Added;

    internal void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        lock (gate)
        {
            lines.Enqueue(line);
            while (lines.Count > 120)
            {
                lines.Dequeue();
            }
        }

        Added?.Invoke(line);
    }

    internal string Snapshot()
    {
        lock (gate)
        {
            return string.Join(Environment.NewLine, lines);
        }
    }
}

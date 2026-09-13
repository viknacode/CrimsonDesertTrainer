namespace CrimsonTrainer.ViewModels;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record LogEntry(DateTime Time, string Message, LogLevel Level)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

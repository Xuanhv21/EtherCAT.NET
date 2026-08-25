namespace EtherCAT.NET;

/// <summary>
/// One line in the UI's scrolling log panel: a formatted timestamp plus the message text, fed from
/// <see cref="Engine.ProcessData.CyclicExchangeService.LogEmitted"/> and from
/// <see cref="MainWindowViewModel"/>'s own bring-up/shutdown narration.
/// </summary>
/// <param name="Timestamp">Wall-clock time the entry was created, pre-formatted as <c>HH:mm:ss.fff</c>.</param>
/// <param name="Message">The human-readable log message.</param>
public sealed record LogEntry(string Timestamp, string Message)
{
    /// <summary>Creates a <see cref="LogEntry"/> stamped with the current local time.</summary>
    public static LogEntry Now(string message) => new(DateTime.Now.ToString("HH:mm:ss.fff"), message);

    /// <inheritdoc />
    public override string ToString() => $"[{Timestamp}] {Message}";
}

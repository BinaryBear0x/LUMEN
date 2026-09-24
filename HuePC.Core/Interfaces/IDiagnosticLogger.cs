namespace HuePC.Core.Interfaces;

public interface IDiagnosticLogger
{
    string LogDirectory { get; }
    Task WriteAsync(string level, string message, object? data = null, CancellationToken cancellationToken = default);
}

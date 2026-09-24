using System.Text.Json;
using HuePC.Core.Interfaces;

namespace HuePC.Infrastructure.Logging;

public sealed class LocalDiagnosticLogger : IDiagnosticLogger, IAsyncDisposable
{
    private const long MaximumFileSize = 5 * 1024 * 1024;
    private const int RetainedRotations = 3;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public LocalDiagnosticLogger()
    {
        var configuredDirectory = Environment.GetEnvironmentVariable("HUEPC_LOG_DIRECTORY");
        LogDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HuePC", "Logs")
            : Path.GetFullPath(configuredDirectory);
    }

    public string LogDirectory { get; }

    public async Task WriteAsync(string level, string message, object? data = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var entry = new DiagnosticLogEntry(DateTimeOffset.UtcNow, level, message, data);
            var json = JsonSerializer.Serialize(entry, JsonOptions);
            var text = $"{entry.TimestampUtc:O} [{level.ToUpperInvariant()}] {message}" +
                (data is null ? string.Empty : $" | {JsonSerializer.Serialize(data, JsonOptions)}");

            await AppendWithRotationAsync(Path.Combine(LogDirectory, "ble-events.jsonl"), json, cancellationToken);
            await AppendWithRotationAsync(Path.Combine(LogDirectory, "ble-events.log"), text, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task AppendWithRotationAsync(string path, string line, CancellationToken cancellationToken)
    {
        var currentSize = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (currentSize + line.Length + Environment.NewLine.Length > MaximumFileSize)
        {
            for (var index = RetainedRotations; index >= 1; index--)
            {
                var source = index == 1 ? path : $"{path}.{index - 1}";
                var destination = $"{path}.{index}";
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                if (File.Exists(source))
                {
                    File.Move(source, destination);
                }
            }
        }

        await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record DiagnosticLogEntry(DateTimeOffset TimestampUtc, string Level, string Message, object? Data);
}

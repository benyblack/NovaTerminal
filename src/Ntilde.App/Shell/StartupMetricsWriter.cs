using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NovaTerminal.Shell;

public sealed class StartupMetricsWriter : IDisposable
{
    private const string EnabledEnvVar = "NOVATERM_STARTUP_METRICS";
    private const string OutputEnvVar = "NOVATERM_STARTUP_METRICS_OUT";
    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly ArrayBufferWriter<byte> _buffer = new(512);
    private bool _disabled;

    private StartupMetricsWriter(FileStream stream)
    {
        _stream = stream;
    }

    public static StartupMetricsWriter? CreateFromEnvironment()
    {
        if (!IsEnabled())
        {
            return null;
        }

        string? configuredPath = Environment.GetEnvironmentVariable(OutputEnvVar);
        string outputPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(AppContext.BaseDirectory, "startup_metrics.jsonl")
            : configuredPath;

        return Create(outputPath);
    }

    public static StartupMetricsWriter? Create(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(outputPath);
            if (Directory.Exists(fullPath))
            {
                return null;
            }

            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new FileStream(
                fullPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                options: FileOptions.SequentialScan);

            return new StartupMetricsWriter(stream);
        }
        catch
        {
            return null;
        }
    }

    public bool TryWriteSnapshot(StartupMetricsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_disabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (_disabled)
            {
                return false;
            }

            try
            {
                _buffer.Clear();
                using (var writer = new Utf8JsonWriter(_buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString(nameof(StartupMetricsSnapshot.LaunchId), snapshot.LaunchId);
                    writer.WriteString(nameof(StartupMetricsSnapshot.StartedAtUtc), snapshot.StartedAtUtc);
                    WriteNullableInt64(writer, nameof(StartupMetricsSnapshot.MainWindowConstructedMs), snapshot.MainWindowConstructedMs);
                    WriteNullableInt64(writer, nameof(StartupMetricsSnapshot.WindowOpenedMs), snapshot.WindowOpenedMs);
                    WriteNullableInt64(writer, nameof(StartupMetricsSnapshot.FirstTerminalReadyMs), snapshot.FirstTerminalReadyMs);
                    WriteNullableInt64(writer, nameof(StartupMetricsSnapshot.SessionRestoreCompleteMs), snapshot.SessionRestoreCompleteMs);
                    WriteNullableInt64(writer, nameof(StartupMetricsSnapshot.DeferredWorkCompleteMs), snapshot.DeferredWorkCompleteMs);
                    WriteNullableInt64(writer, nameof(StartupMetricsSnapshot.BackgroundRestoreCompleteMs), snapshot.BackgroundRestoreCompleteMs);
                    WriteCheckpoints(writer, snapshot.Checkpoints);
                    writer.WriteEndObject();
                    writer.Flush();
                }

                _stream.Write(_buffer.WrittenSpan);
                _stream.WriteByte((byte)'\n');
                _stream.Flush();
                return true;
            }
            catch
            {
                DisableUnsafe();
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DisableUnsafe();
        }
    }

    private static bool IsEnabled()
    {
        string? raw = Environment.GetEnvironmentVariable(EnabledEnvVar);
        return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteNullableInt64(Utf8JsonWriter writer, string propertyName, long? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
            return;
        }

        writer.WriteNull(propertyName);
    }

    private static void WriteCheckpoints(Utf8JsonWriter writer, IReadOnlyDictionary<string, long>? checkpoints)
    {
        if (checkpoints == null || checkpoints.Count == 0)
        {
            writer.WriteNull(nameof(StartupMetricsSnapshot.Checkpoints));
            return;
        }

        writer.WritePropertyName(nameof(StartupMetricsSnapshot.Checkpoints));
        writer.WriteStartObject();

        foreach ((string key, long value) in checkpoints.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteNumber(key, value);
        }

        writer.WriteEndObject();
    }

    private void DisableUnsafe()
    {
        if (_disabled)
        {
            return;
        }

        _disabled = true;
        try
        {
            _stream.Dispose();
        }
        catch
        {
            // Keep failures non-fatal.
        }
    }
}

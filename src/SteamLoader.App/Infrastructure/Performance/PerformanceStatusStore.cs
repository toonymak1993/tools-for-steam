using System.Text.Json;

namespace SteamLoader.App.Infrastructure.Performance;

public sealed class PerformanceStatusStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _statusPath;
    private readonly object _gate = new();

    public PerformanceStatusStore(string statusPath)
    {
        _statusPath = statusPath;
    }

    internal PerformanceRuntimeStatus Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_statusPath))
                {
                    return new PerformanceRuntimeStatus();
                }

                var json = File.ReadAllText(_statusPath);
                return JsonSerializer.Deserialize<PerformanceRuntimeStatus>(json, JsonOptions) ?? new PerformanceRuntimeStatus();
            }
            catch
            {
                return new PerformanceRuntimeStatus();
            }
        }
    }

    internal void Save(PerformanceRuntimeStatus status)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statusPath)!);
            File.WriteAllText(_statusPath, JsonSerializer.Serialize(status, JsonOptions));
        }
    }
}

internal sealed class PerformanceRuntimeStatus
{
    public bool OverlayVisible { get; set; }

    public bool Elevated { get; set; }

    public int HelperProcessId { get; set; }

    public int TargetProcessId { get; set; }

    public string TargetProcessName { get; set; } = string.Empty;

    public string TargetWindowTitle { get; set; } = string.Empty;

    public double FramesPerSecond { get; set; }

    public double FrameTimeMs { get; set; }

    public double OnePercentLowFps { get; set; }

    public double FramePacingMs { get; set; }

    public double TargetCpuPercent { get; set; }

    public long TargetMemoryMb { get; set; }

    public string DetailText { get; set; } = string.Empty;

    public string ErrorText { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.MinValue;
}


using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 本机启动档案：记录最近几次启动各阶段的实际耗时和界面语言。
/// 启动页在 Host 构建前就要用它，所以只做同步的小文件读写，任何异常都回落默认值，绝不挡住收银启动。
/// </summary>
public sealed class StartupTimingProfile
{
    public const int MaxSamplesPerPhase = 5;

    // 首次启动没有本机记录时使用的预期耗时，只影响这一次的进度节奏，之后按本机实测校准。
    public static readonly IReadOnlyDictionary<StartupPhase, TimeSpan> DefaultExpectedDurations =
        new Dictionary<StartupPhase, TimeSpan>
        {
            [StartupPhase.Services] = TimeSpan.FromMilliseconds(800),
            [StartupPhase.Interface] = TimeSpan.FromMilliseconds(1200),
            [StartupPhase.Update] = TimeSpan.FromMilliseconds(700),
            [StartupPhase.Device] = TimeSpan.FromMilliseconds(600),
            [StartupPhase.Catalog] = TimeSpan.FromMilliseconds(1200),
            [StartupPhase.Display] = TimeSpan.FromMilliseconds(600)
        };

    // 单次样本上限：网络超时之类的极端值只保留到这里，再由中位数过滤掉偶发异常。
    private static readonly TimeSpan MaxSample = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly object _gate = new();
    private ProfileDocument _document;

    private StartupTimingProfile(string filePath, ProfileDocument document)
    {
        _filePath = filePath;
        _document = document;
    }

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hbpos.Client",
        "startup-profile.json");

    public string? CultureName
    {
        get
        {
            lock (_gate)
            {
                return _document.Culture;
            }
        }
    }

    public string? StoreLabel
    {
        get
        {
            lock (_gate)
            {
                return _document.StoreLabel;
            }
        }
    }

    public static StartupTimingProfile Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var document = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(filePath), JsonOptions);
                if (document is not null)
                {
                    return new StartupTimingProfile(filePath, document);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // 档案损坏只影响进度节奏，按首次启动处理，下次成功启动会整份重写。
        }

        return new StartupTimingProfile(filePath, new ProfileDocument());
    }

    /// <summary>各阶段预期耗时：取最近几次样本的中位数，没有样本的阶段用默认值。</summary>
    public IReadOnlyDictionary<StartupPhase, TimeSpan> GetExpectedDurations()
    {
        lock (_gate)
        {
            var result = new Dictionary<StartupPhase, TimeSpan>();
            foreach (var phase in StartupProgressTracker.Phases)
            {
                result[phase] = _document.Phases is not null &&
                    _document.Phases.TryGetValue(phase.ToString(), out var samples) &&
                    samples is { Count: > 0 }
                        ? TimeSpan.FromMilliseconds(Median(samples))
                        : DefaultExpectedDurations[phase];
            }

            return result;
        }
    }

    public void RecordRun(IReadOnlyDictionary<StartupPhase, TimeSpan> measured)
    {
        ArgumentNullException.ThrowIfNull(measured);
        lock (_gate)
        {
            var phases = _document.Phases ?? new Dictionary<string, List<long>>(StringComparer.Ordinal);
            foreach (var (phase, duration) in measured)
            {
                // 至少记 1ms：0 会被当成"没有记录"而回落默认值，反而放大极快阶段的权重。
                var sample = (long)Math.Round(Math.Clamp(duration.TotalMilliseconds, 1, MaxSample.TotalMilliseconds));
                if (!phases.TryGetValue(phase.ToString(), out var samples) || samples is null)
                {
                    samples = [];
                    phases[phase.ToString()] = samples;
                }

                samples.Add(sample);
                if (samples.Count > MaxSamplesPerPhase)
                {
                    samples.RemoveRange(0, samples.Count - MaxSamplesPerPhase);
                }
            }

            _document = _document with { Phases = phases };
        }
    }

    public void RememberCulture(string? cultureName)
    {
        lock (_gate)
        {
            _document = _document with { Culture = string.IsNullOrWhiteSpace(cultureName) ? null : cultureName.Trim() };
        }
    }

    /// <summary>启动页副标题：门店名 · 设备号；没有门店名时返回 null，由启动页显示通用副标题。</summary>
    public static string? FormatStoreLabel(string? storeName, string? deviceCode)
    {
        if (string.IsNullOrWhiteSpace(storeName))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(deviceCode)
            ? storeName.Trim()
            : $"{storeName.Trim()} · {deviceCode.Trim()}";
    }

    public void RememberStoreLabel(string? storeLabel)
    {
        lock (_gate)
        {
            _document = _document with { StoreLabel = string.IsNullOrWhiteSpace(storeLabel) ? null : storeLabel.Trim() };
        }
    }

    public bool TrySave()
    {
        string json;
        lock (_gate)
        {
            json = JsonSerializer.Serialize(_document with { Version = ProfileDocument.CurrentVersion }, JsonOptions);
        }

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 先写临时文件再替换，断电或强杀时不会留下半截 JSON。
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static double Median(List<long> samples)
    {
        var ordered = samples.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2d;
    }

    internal sealed record ProfileDocument
    {
        public const int CurrentVersion = 1;

        public int Version { get; init; } = CurrentVersion;

        public string? Culture { get; init; }

        public string? StoreLabel { get; init; }

        public Dictionary<string, List<long>>? Phases { get; init; }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZellerProbe;

/// <summary>
/// 追加写入的 JSONL 记录：每次调用 SDK 之前先写一条 before-call，结果回来再写 result。
/// </summary>
/// <remarks>
/// 中文注释：这正是正式接入要用的「先落库再扣款」做法。进程被杀、断电之后，
/// 没有 result 的 before-call 就是「结果未知」的交易，可以在重启后逐条按 reference 查询。
/// 记录里不会出现完整卡号：SDK 只返回屏蔽后的卡号（PanMasked）。
/// </remarks>
internal sealed class ProbeJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _gate = new();

    public ProbeJournal(string directory)
    {
        Directory.CreateDirectory(directory);
        JournalPath = Path.Combine(directory, "zeller-probe-journal.jsonl");
        LogPath = Path.Combine(directory, "zeller-probe-log.jsonl");
    }

    public string JournalPath { get; }

    public string LogPath { get; }

    public void WriteJournal(JournalEntry entry) => Append(JournalPath, entry);

    public void WriteLog(object entry) => Append(LogPath, entry);

    public static string Serialize(object? value) =>
        value is null ? "null" : JsonSerializer.Serialize(value, value.GetType(), JsonOptions);

    /// <summary>
    /// 找出只有 before-call、没有 result 的调用（进程中途退出留下的「结果未知」交易）。
    /// </summary>
    public IReadOnlyList<JournalEntry> FindUnfinished()
    {
        if (!File.Exists(JournalPath))
        {
            return [];
        }

        var entries = new List<JournalEntry>();
        foreach (var line in File.ReadLines(JournalPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<JournalEntry>(line, JsonOptions);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // 中文注释：断电可能留下半行，跳过即可，不影响其他记录。
            }
        }

        var finished = entries
            .Where(entry => entry.Phase == "result")
            .Select(entry => entry.CallId)
            .ToHashSet(StringComparer.Ordinal);
        return entries
            .Where(entry => entry.Phase == "before-call" && !finished.Contains(entry.CallId))
            .ToList();
    }

    private void Append(string path, object entry)
    {
        var line = Serialize(entry);
        lock (_gate)
        {
            // 中文注释：每条单独打开、写入、刷盘，进程随时被杀也最多丢最后半行。
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(line);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
    }
}

internal sealed record JournalEntry(
    string CallId,
    string Phase,
    string Operation,
    string? Reference,
    int? AmountCents,
    string? OriginalPurchase,
    DateTimeOffset At,
    string? ResultType = null,
    string? ResultJson = null,
    long? ElapsedMs = null);

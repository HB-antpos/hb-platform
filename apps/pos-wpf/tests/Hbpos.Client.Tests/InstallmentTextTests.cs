using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>
/// 分期服务层提示的中英文切换：格式化规则，以及源码里用到的每个资源键在中英文资源里都存在、占位符一致。
/// </summary>
public sealed class InstallmentTextTests
{
    private static readonly string[] SourceFiles =
    [
        "Services/InstallmentOperationService.cs",
        "Services/InstallmentOrderService.cs",
        "ViewModels/InstallmentCenterViewModel.cs"
    ];

    [Fact]
    public void Without_a_localization_service_the_source_text_is_returned()
    {
        Assert.Equal("补款已记录。", InstallmentText.Format(null, "installment.text.any", "补款已记录。", []));
        Assert.Equal("已创建分期单 INS-1。", InstallmentText.Format(null, "installment.text.any", "已创建分期单 {0}。", ["INS-1"]));
    }

    [Fact]
    public void The_current_language_text_is_used_and_missing_keys_fall_back_to_the_source_text()
    {
        var localization = new LocalizationService();
        localization.SetCulture(LocalizationService.DefaultCultureName);
        var key = FindKey("已创建分期单 {0}。");

        Assert.Equal("Lay-by INS-1 created.", InstallmentText.Format(localization, key, "已创建分期单 {0}。", ["INS-1"]));
        Assert.Equal("补款已记录。", InstallmentText.Format(localization, "installment.text.missing", "补款已记录。", []));

        localization.SetCulture(LocalizationService.ChineseCultureName);
        Assert.Equal("已创建分期单 INS-1。", InstallmentText.Format(localization, key, "已创建分期单 {0}。", ["INS-1"]));
    }

    [Fact]
    public void Every_installment_text_key_exists_in_both_languages_with_matching_placeholders()
    {
        var english = LoadResource("Strings.resx");
        var chinese = LoadResource("Strings.zh-CN.resx");
        var calls = ReadCalls().ToList();

        Assert.True(calls.Count > 200, $"expected the installment messages to go through InstallmentText, found {calls.Count}");
        foreach (var (key, fallback) in calls)
        {
            Assert.True(english.TryGetValue(key, out var en), $"{key} is missing from Strings.resx ({fallback})");
            Assert.True(chinese.TryGetValue(key, out var zh), $"{key} is missing from Strings.zh-CN.resx ({fallback})");
            Assert.False(Regex.IsMatch(en!, "[一-鿿]"), $"{key} English text still contains Chinese: {en}");
            Assert.Equal(Placeholders(fallback), Placeholders(en!));
            Assert.Equal(Placeholders(fallback), Placeholders(zh!));
        }
    }

    [Fact]
    public void Installment_messages_shown_to_the_cashier_are_not_left_as_untranslated_literals()
    {
        // 留着不翻译的中文字面量只有两类：发给服务端的默认原因（数据），和被别处按文字比较的状态名。
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "取消分期并退代金券", "取消分期并退款", "取消分期退款", "作废分期单",
            "待补款", "待提货", "已提货", "已作废", "已取消"
        };
        foreach (var file in SourceFiles)
        {
            foreach (var line in File.ReadLines(SourcePath(file)))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                // 去掉已经走 InstallmentText 的调用和行尾注释，剩下的字面量里不应再有中文。
                var remaining = Regex.Replace(line, "InstallmentText\\.L\\(\"[^\"]+\", \"(?:[^\"\\\\]|\\\\.)*\"", string.Empty);
                foreach (Match literal in Regex.Matches(remaining, "\"((?:[^\"\\\\]|\\\\.)*)\""))
                {
                    var text = literal.Groups[1].Value;
                    if (Regex.IsMatch(text, "[一-鿿]"))
                    {
                        Assert.True(allowed.Contains(text), $"{file}: untranslated literal \"{text}\"");
                    }
                }
            }
        }
    }

    private static string FindKey(string fallback) =>
        ReadCalls().First(call => call.Fallback == fallback).Key;

    private static IEnumerable<(string Key, string Fallback)> ReadCalls()
    {
        var pattern = new Regex("InstallmentText\\.L\\(\"([^\"]+)\", \"((?:[^\"\\\\]|\\\\.)*)\"");
        foreach (var file in SourceFiles)
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(SourcePath(file))))
            {
                yield return (match.Groups[1].Value, match.Groups[2].Value);
            }
        }
    }

    private static string Placeholders(string template) =>
        string.Join(",", Regex.Matches(template, "\\{(\\d+)(?::[^}]*)?\\}").Select(match => match.Groups[1].Value).Distinct().Order());

    private static Dictionary<string, string> LoadResource(string fileName) =>
        XDocument.Load(SourcePath($"Resources/{fileName}"))
            .Root!
            .Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);

    private static string SourcePath(string relative) =>
        Path.Combine(FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", relative.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "apps", "pos-wpf", "src", "Hbpos.Client.Wpf")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to find repository root.");
    }
}

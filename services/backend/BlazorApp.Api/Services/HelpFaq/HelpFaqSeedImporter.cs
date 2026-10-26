using System.Text.Json;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.HelpFaq;

/// <summary>种子解析结果：待写入的分组行与问答行（已按种子顺序编好 SortOrder 与外键）。</summary>
public sealed record HelpFaqSeedPlan(
    IReadOnlyList<HelpFaqGroup> Groups,
    IReadOnlyList<HelpFaqQuestion> Questions
);

/// <summary>
/// 把 help-faq-seed.json（嵌入资源）解析成可直接入库的行对象。
/// 纯函数、无 I/O（嵌入资源读取除外），写库由迁移运行时完成，便于单测。
/// 种子结构：{version:1, tabs:{pos:[Group], mobile:[Group]}}，与公开接口 tabs 完全同形。
/// </summary>
public static class HelpFaqSeedImporter
{
    /// <summary>嵌入资源逻辑名，与 BlazorApp.Api.csproj 的 LogicalName 保持一致。</summary>
    public const string SeedResourceName = "BlazorApp.Api.Data.SeedData.help-faq-seed.json";

    /// <summary>首次导入写入 CreatedBy / UpdatedBy 的操作人标识。</summary>
    public const string SeedActor = "system-seed";

    private const int SupportedSeedVersion = 1;
    private const int GroupCodeMaxLength = 64;
    private const int QuestionCodeMaxLength = 96;

    public static string ReadEmbeddedSeedJson()
    {
        var assembly = typeof(HelpFaqSeedImporter).Assembly;
        using var stream =
            assembly.GetManifestResourceStream(SeedResourceName)
            ?? throw new InvalidOperationException(
                $"缺少嵌入资源 {SeedResourceName}，请检查 BlazorApp.Api.csproj 的 EmbeddedResource。"
            );
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 解析种子 JSON。已发布态：Status=Published、PublishedAt=nowUtc、Version=1，
    /// 分组与问题的 SortOrder = 数组下标 * 10（预留插入空位），审计人 system-seed。
    /// 种子内容不合法（重复 Code、超长、缺步骤等）直接抛异常，让迁移失败而不是写入半截内容。
    /// </summary>
    public static HelpFaqSeedPlan Parse(string json, DateTime nowUtc)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (
            !root.TryGetProperty("version", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || versionElement.GetInt32() != SupportedSeedVersion
        )
        {
            throw new InvalidOperationException($"FAQ 种子 version 必须为 {SupportedSeedVersion}。");
        }

        if (!root.TryGetProperty("tabs", out var tabsElement))
        {
            throw new InvalidOperationException("FAQ 种子缺少 tabs。");
        }

        var tabs =
            JsonSerializer.Deserialize<HelpFaqTabsDto>(
                tabsElement.GetRawText(),
                HelpFaqRowMapper.JsonOptions
            ) ?? throw new InvalidOperationException("FAQ 种子 tabs 无法解析。");

        var groups = new List<HelpFaqGroup>();
        var questions = new List<HelpFaqQuestion>();
        var questionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AppendTab(HelpFaqTabs.Pos, tabs.Pos, nowUtc, groups, questions, questionCodes);
        AppendTab(HelpFaqTabs.Mobile, tabs.Mobile, nowUtc, groups, questions, questionCodes);

        return new HelpFaqSeedPlan(groups, questions);
    }

    private static void AppendTab(
        string tab,
        List<HelpFaqGroupDto> groupDtos,
        DateTime nowUtc,
        List<HelpFaqGroup> groups,
        List<HelpFaqQuestion> questions,
        HashSet<string> questionCodes
    )
    {
        var groupCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var groupIndex = 0; groupIndex < groupDtos.Count; groupIndex++)
        {
            var groupDto = groupDtos[groupIndex];
            RequireCode(groupDto.Id, GroupCodeMaxLength, $"{tab} 分组");
            if (!groupCodes.Add(groupDto.Id))
            {
                throw new InvalidOperationException($"FAQ 种子 {tab} 内分组 id 重复：{groupDto.Id}。");
            }

            var group = new HelpFaqGroup
            {
                Tab = tab,
                Code = groupDto.Id,
                TitleZh = groupDto.Title.Zh,
                TitleEn = groupDto.Title.En,
                SortOrder = groupIndex * 10,
                CreatedAt = nowUtc,
                CreatedBy = SeedActor,
                UpdatedAt = nowUtc,
                UpdatedBy = SeedActor,
                IsDeleted = false,
            };
            groups.Add(group);

            for (var questionIndex = 0; questionIndex < groupDto.Questions.Count; questionIndex++)
            {
                var questionDto = groupDto.Questions[questionIndex];
                RequireCode(questionDto.Id, QuestionCodeMaxLength, $"{tab}/{groupDto.Id} 问题");
                if (!questionCodes.Add(questionDto.Id))
                {
                    // Code 即 #faq-<code> 锚点，必须跨 Tab 全局唯一。
                    throw new InvalidOperationException($"FAQ 种子问题 id 重复：{questionDto.Id}。");
                }

                if (
                    questionDto.Steps.Zh.Count == 0
                    || questionDto.Steps.Zh.Count != questionDto.Steps.En.Count
                )
                {
                    throw new InvalidOperationException(
                        $"FAQ 种子问题 {questionDto.Id} 的中英文步骤数必须一致且非空。"
                    );
                }

                var question = new HelpFaqQuestion
                {
                    GroupGUID = group.GroupGUID,
                    SortOrder = questionIndex * 10,
                    Status = HelpFaqStatuses.Published,
                    PublishedAt = nowUtc,
                    Version = 1,
                    CreatedAt = nowUtc,
                    CreatedBy = SeedActor,
                    UpdatedAt = nowUtc,
                    UpdatedBy = SeedActor,
                    IsDeleted = false,
                };
                HelpFaqRowMapper.ApplyContent(question, questionDto);
                questions.Add(question);
            }
        }
    }

    private static void RequireCode(string? code, int maxLength, string what)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException($"FAQ 种子{what} id 不能为空。");
        }

        if (code.Length > maxLength)
        {
            throw new InvalidOperationException($"FAQ 种子{what} id 超过 {maxLength} 个字符：{code}。");
        }
    }
}

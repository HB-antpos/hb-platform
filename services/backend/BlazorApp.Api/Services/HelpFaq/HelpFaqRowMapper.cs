using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.HelpFaq;

/// <summary>
/// FAQ 行（表中的 JSON 文本列）与公开 DTO 之间的互转。
/// 种子导入、公开读取（P1）以及后续后台编辑 / 草稿快照（P2）共用同一套序列化，避免各写一份造成漂移。
/// </summary>
public static class HelpFaqRowMapper
{
    /// <summary>
    /// 库内 JSON 与草稿快照使用的序列化选项：
    /// 放宽转义（中文、&amp;、引号内的 &lt;&gt; 保持可读，便于直接在库里排查），null 字段省略。
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string SerializeStrings(IReadOnlyList<string> items) =>
        JsonSerializer.Serialize(items, JsonOptions);

    public static string SerializeRelated(IReadOnlyList<HelpFaqRelatedItemDto> items) =>
        JsonSerializer.Serialize(items, JsonOptions);

    /// <summary>解析 JSON 字符串数组；空值按空列表处理，格式非法抛 <see cref="JsonException"/>。</summary>
    public static List<string> ParseStrings(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>();

    public static List<HelpFaqRelatedItemDto> ParseRelated(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new List<HelpFaqRelatedItemDto>()
            : JsonSerializer.Deserialize<List<HelpFaqRelatedItemDto>>(json, JsonOptions)
                ?? new List<HelpFaqRelatedItemDto>();

    /// <summary>把 DTO 的内容字段写入行（不动主键、分组、排序、状态、版本与审计列）。</summary>
    public static void ApplyContent(HelpFaqQuestion row, HelpFaqQuestionDto dto)
    {
        row.Code = dto.Id;
        row.TitleZh = dto.Title.Zh;
        row.TitleEn = dto.Title.En;
        row.WhereZh = dto.Where.Zh;
        row.WhereEn = dto.Where.En;
        row.StepsZh = SerializeStrings(dto.Steps.Zh);
        row.StepsEn = SerializeStrings(dto.Steps.En);

        // 可选块：缺省存 NULL，存在则中英两列都写（即使其中一侧为空数组），读取时才能原样还原。
        row.TipsZh = dto.Tips is null ? null : SerializeStrings(dto.Tips.Zh);
        row.TipsEn = dto.Tips is null ? null : SerializeStrings(dto.Tips.En);
        row.RulesZh = dto.Rules is null ? null : SerializeStrings(dto.Rules.Zh);
        row.RulesEn = dto.Rules is null ? null : SerializeStrings(dto.Rules.En);
        row.RelatedZh = dto.Related is null ? null : SerializeRelated(dto.Related.Zh);
        row.RelatedEn = dto.Related is null ? null : SerializeRelated(dto.Related.En);
        row.Permissions = dto.Permissions is null ? null : SerializeStrings(dto.Permissions);
    }

    /// <summary>
    /// 行 → 公开 DTO。只读取内容列；<see cref="HelpFaqQuestion.DraftJson"/> 永远不参与，
    /// 所以公开接口不可能泄露未发布草稿。JSON 列损坏时抛 <see cref="JsonException"/>，由调用方决定跳过。
    /// </summary>
    public static HelpFaqQuestionDto ToDto(HelpFaqQuestion row)
    {
        var dto = new HelpFaqQuestionDto
        {
            Id = row.Code,
            Title = new HelpFaqTextDto { Zh = row.TitleZh, En = row.TitleEn },
            Where = new HelpFaqTextDto { Zh = row.WhereZh, En = row.WhereEn },
            Steps = new HelpFaqListDto
            {
                Zh = ParseStrings(row.StepsZh),
                En = ParseStrings(row.StepsEn),
            },
        };

        if (row.TipsZh is not null || row.TipsEn is not null)
        {
            dto.Tips = new HelpFaqListDto
            {
                Zh = ParseStrings(row.TipsZh),
                En = ParseStrings(row.TipsEn),
            };
        }

        if (row.RulesZh is not null || row.RulesEn is not null)
        {
            dto.Rules = new HelpFaqListDto
            {
                Zh = ParseStrings(row.RulesZh),
                En = ParseStrings(row.RulesEn),
            };
        }

        if (row.RelatedZh is not null || row.RelatedEn is not null)
        {
            dto.Related = new HelpFaqRelatedDto
            {
                Zh = ParseRelated(row.RelatedZh),
                En = ParseRelated(row.RelatedEn),
            };
        }

        if (row.Permissions is not null)
        {
            dto.Permissions = ParseStrings(row.Permissions);
        }

        return dto;
    }
}

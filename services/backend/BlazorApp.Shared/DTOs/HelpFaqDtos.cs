using System.Text.Json.Serialization;

namespace BlazorApp.Shared.DTOs;

// 公开 FAQ 接口 GET /api/support/faq 的响应形状。
// 必须与前端 StoreHelpFaqGroup / StoreHelpFaqQuestion（apps/web/src/pages/StoreHelpFaq）以及种子文件
// help-faq-seed.json 的 tabs 结构逐字段一致：字段名别改；可选字段为 null 时整体省略（不输出 null）。
// 这里显式写 JsonPropertyName / JsonIgnore，保证即便全局序列化选项变化，线上形状也不漂移。

/// <summary>公开 FAQ 完整树：version 是已发布内容的稳定摘要（也是 ETag 来源）。</summary>
public sealed class HelpFaqPublicDto
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("tabs")]
    public HelpFaqTabsDto Tabs { get; set; } = new();
}

public sealed class HelpFaqTabsDto
{
    [JsonPropertyName("pos")]
    public List<HelpFaqGroupDto> Pos { get; set; } = new();

    [JsonPropertyName("mobile")]
    public List<HelpFaqGroupDto> Mobile { get; set; } = new();
}

public sealed class HelpFaqGroupDto
{
    /// <summary>分组 Code（前端的 group.id）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public HelpFaqTextDto Title { get; set; } = new();

    [JsonPropertyName("questions")]
    public List<HelpFaqQuestionDto> Questions { get; set; } = new();
}

public sealed class HelpFaqQuestionDto
{
    /// <summary>问题 Code（前端的 question.id，同时是 #faq-&lt;id&gt; 锚点）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public HelpFaqTextDto Title { get; set; } = new();

    [JsonPropertyName("where")]
    public HelpFaqTextDto Where { get; set; } = new();

    [JsonPropertyName("steps")]
    public HelpFaqListDto Steps { get; set; } = new();

    [JsonPropertyName("tips")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HelpFaqListDto? Tips { get; set; }

    [JsonPropertyName("rules")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HelpFaqListDto? Rules { get; set; }

    [JsonPropertyName("permissions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Permissions { get; set; }

    [JsonPropertyName("related")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HelpFaqRelatedDto? Related { get; set; }
}

/// <summary>中英双语单行文本。</summary>
public sealed class HelpFaqTextDto
{
    [JsonPropertyName("zh")]
    public string Zh { get; set; } = string.Empty;

    [JsonPropertyName("en")]
    public string En { get; set; } = string.Empty;
}

/// <summary>中英双语有序列表（步骤 / 提示 / 规则）。</summary>
public sealed class HelpFaqListDto
{
    [JsonPropertyName("zh")]
    public List<string> Zh { get; set; } = new();

    [JsonPropertyName("en")]
    public List<string> En { get; set; } = new();
}

public sealed class HelpFaqRelatedDto
{
    [JsonPropertyName("zh")]
    public List<HelpFaqRelatedItemDto> Zh { get; set; } = new();

    [JsonPropertyName("en")]
    public List<HelpFaqRelatedItemDto> En { get; set; } = new();
}

public sealed class HelpFaqRelatedItemDto
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;
}

namespace BlazorApp.Api.Services;

/// <summary>
/// 一封邮件的主题与正文（纯文本 + HTML）。
/// 各发送器把正文拼装抽成返回它的纯函数，SMTP 发送只负责投递：这样测试可以直接断言正文内容，
/// 例如「所有账号 / 监护人邮件只有英文」，不必真的连 SMTP。
/// </summary>
internal readonly record struct EmailContent(string Subject, string Text, string Html);

namespace BlazorApp.Api.Features.PosReceipts;

/// <summary>
/// 在线小票的一行纸面内容。服务端把整张小票按收银机的 42 列规则排好版，Web / 移动端 / 客人公开页只负责按行绘制，
/// 三端排版天然一致，不再各自复制一份排版代码。
/// Kind：text（文本行）、separator（整行 42 个减号）、blank（空行）、barcode（Code128，Text 为条码内容）、qr（二维码，Text 为内容）。
/// Align：left / center。
/// </summary>
public sealed record PosReceiptLineDto(string Kind, string Text, string Align = "left", bool Bold = false);

public sealed record PosReceiptDocumentDto(
    string OrderGuid,
    // ShortCode：订单 GUID 去横杠后末 8 位大写，小票条码与可读单号都用它。
    string ShortCode,
    // StatusKind：paid / refund，只用于纸外状态标签着色，纸面文字保持黑白。
    string StatusKind,
    IReadOnlyList<PosReceiptLineDto> Lines
);

/// <summary>员工端看到的分享链接状态；令牌明文只在 <see cref="Url"/> 里出现。</summary>
public sealed record PosReceiptShareLinkDto(string Url, DateTime ExpiresAtUtc, DateTime CreatedAtUtc, int OpenCount);

/// <summary>员工端小票详情：含银行回单原文的完整纸面 + 当前有效的分享链接（没有则为空）。</summary>
public sealed record PosReceiptStaffViewDto(PosReceiptDocumentDto Document, PosReceiptShareLinkDto? Link);

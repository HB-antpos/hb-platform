using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Wpf.Services;

internal static class LinklyLocalTxnRef
{
    // 中文注释：本地 GetLastTransaction 已被 EFT-Client 正常回答（Success=true），但回包里的 TxnRef 不是被查的这笔
    // （生产实测：老交易查询回 JH / NOT FOUND、TxnRef 为 16 个空格）。结果仍是 ResultUnknown（恢复流程不能据此结案），
    // 只是与“刷卡机没回答（离线/超时/断线）”区分开：主管结案的实时核验把它视为“无法证明也无法否定”，凭银行证据结案。
    internal const string LastTransactionNotMatchedStatusKey = "linkly.local.lastTransactionNotMatched";

    // 派生算法与 Hbpos.Api 共用同一份实现：后端异步链路由 API 按同一 attempt 身份算出同一个引用。
    internal static string Create(char transactionType, string stableIdentity)
    {
        return LinklyAttemptTxnRef.Create(transactionType, stableIdentity);
    }

    internal static bool TryNormalizeHistoricalReference(string? reference, out string normalized)
    {
        normalized = string.Empty;
        if (reference is null)
        {
            return false;
        }

        // 中文注释：协议允许首尾空格作为填充，但所有控制字符和非 ASCII 字符必须在接触 SDK 前拒绝。
        foreach (var character in reference)
        {
            if (character is < '\x20' or > '\x7E')
            {
                return false;
            }
        }

        var trimmed = reference.Trim(' ');
        normalized = trimmed.StartsWith("ANZ:", StringComparison.OrdinalIgnoreCase)
            ? trimmed[4..].Trim(' ')
            : trimmed;
        return normalized.Length is >= 1 and <= 16;
    }

    internal static string? TrimProtocolPadding(string? reference)
    {
        return reference?.Trim(' ');
    }
}

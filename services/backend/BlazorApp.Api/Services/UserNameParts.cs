using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services;

/// <summary>
/// 请求里携带的姓名相关字段（创建 / 更新 / 导入共用）。null 与空白在更新语义里不同，调用方原样传入，不要自行归一。
/// </summary>
internal readonly record struct UserNameInput(
    string? FullName,
    string? GivenName,
    string? MiddleName,
    string? FamilyName,
    string? Nickname
);

/// <summary>
/// 用户「姓 / 名 / 中间名 / 昵称」的统一写入规则（管理员路径 UserService 与店长路径 StoreUserReactService 共用，
/// 避免两份重复实现漂移）。FullName 仍是全站显示与搜索的主口径，由三段通过 <see cref="PersonNameFormatter.Compose"/> 合成。
/// 所有方法只改传入的 <see cref="User"/> 实体、不落库；返回 null 表示成功，否则是面向用户的中文校验错误。
/// </summary>
internal static class UserNameParts
{
    internal const int PartMaxLength = 50;
    internal const int FullNameMaxLength = 100;

    /// <summary>
    /// 创建时写入：四个新字段任一非空（Trim 后）→ 保存各段并由三段合成 FullName（合成为空时退回传入的 FullName）；
    /// 都没传 → FullName 原样沿用（调用方按各自既有口径决定是否预先 Trim），拆分字段保持为空。
    /// </summary>
    public static string? ApplyOnCreate(User user, UserNameInput input)
    {
        var given = PersonNameFormatter.Normalize(input.GivenName);
        var middle = PersonNameFormatter.Normalize(input.MiddleName);
        var family = PersonNameFormatter.Normalize(input.FamilyName);
        var nickname = PersonNameFormatter.Normalize(input.Nickname);

        if (given is null && middle is null && family is null && nickname is null)
        {
            user.FullName = input.FullName;
            return null;
        }

        var partError = ValidatePartLengths(given, middle, family, nickname);
        if (partError is not null)
        {
            return partError;
        }

        var fullName = PersonNameFormatter.Compose(given, middle, family)
            ?? NullIfBlank(input.FullName?.Trim());
        if (fullName is { Length: > FullNameMaxLength })
        {
            return FullNameTooLongMessage;
        }

        user.GivenName = given;
        user.MiddleName = middle;
        user.FamilyName = family;
        user.Nickname = nickname;
        user.FullName = fullName;
        return null;
    }

    /// <summary>
    /// 更新时写入（<paramref name="user"/> 必须是刚从库里读出的完整实体，保证未涉及的列原样写回）。
    /// 新字段语义：null = 不改；空串 / 空白 = 清空；非空 = 设置。
    /// <list type="bullet">
    /// <item>任一「名 / 中间名 / 姓」非 null：用「请求值覆盖现有值后的有效三段」重算 FullName，合成为空则保留原 FullName。</item>
    /// <item>仅昵称非 null 且没传 FullName：只改昵称，FullName 不动。</item>
    /// <item>没有任何三段字段（旧客户端 / 旧 Web，或新客户端同时带了 FullName）：按旧行为设置 FullName；
    /// 若新 FullName 与「现有三段合成值」不一致，清空三段，避免拆分字段与 FullName 漂移；昵称不动。</item>
    /// </list>
    /// </summary>
    public static string? ApplyOnUpdate(User user, UserNameInput input)
    {
        var hasNameParts = input.GivenName is not null
            || input.MiddleName is not null
            || input.FamilyName is not null;
        var hasAnyNewField = hasNameParts || input.Nickname is not null;

        // 先算出昵称的目标值并校验，保证失败时不留下任何半改的状态。
        var nickname = ResolvePart(input.Nickname, user.Nickname);

        if (hasNameParts)
        {
            var given = ResolvePart(input.GivenName, user.GivenName);
            var middle = ResolvePart(input.MiddleName, user.MiddleName);
            var family = ResolvePart(input.FamilyName, user.FamilyName);

            var partError = ValidatePartLengths(given, middle, family, nickname);
            if (partError is not null)
            {
                return partError;
            }

            // 合成为空（三段都被清空）时保留原 FullName，不把账号显示名清成空。
            var fullName = PersonNameFormatter.Compose(given, middle, family) ?? user.FullName;
            if (fullName is { Length: > FullNameMaxLength })
            {
                return FullNameTooLongMessage;
            }

            user.GivenName = given;
            user.MiddleName = middle;
            user.FamilyName = family;
            user.Nickname = nickname;
            user.FullName = fullName;
            return null;
        }

        var nicknameError = ValidatePartLengths(null, null, null, nickname);
        if (nicknameError is not null)
        {
            return nicknameError;
        }

        if (hasAnyNewField && input.FullName is null)
        {
            // 新客户端只改了昵称：全名与三段都不动。
            user.Nickname = nickname;
            return null;
        }

        // 旧客户端（四个新字段都没带）会在每次编辑时重发 FullName；这里保持旧行为：直接设置 FullName。
        var currentComposed = PersonNameFormatter.Compose(user.GivenName, user.MiddleName, user.FamilyName);
        user.FullName = input.FullName;
        if (currentComposed is not null
            && !string.Equals(currentComposed, PersonNameFormatter.Normalize(input.FullName), StringComparison.Ordinal))
        {
            // 关键逻辑：FullName 被旧客户端改成与三段合成值不一致的内容，三段已不可信，必须清空，
            // 否则之后按三段合成 / 展示会让两边长期不一致。昵称与全名无关，保持不动。
            user.GivenName = null;
            user.MiddleName = null;
            user.FamilyName = null;
        }

        if (input.Nickname is not null)
        {
            user.Nickname = nickname;
        }

        return null;
    }

    private const string FullNameTooLongMessage = "姓名合成后超过100个字符，请缩短姓、名或中间名";

    /// <summary>null = 不改（沿用现值）；空白 = 清空；其余取规范化后的值。</summary>
    private static string? ResolvePart(string? requested, string? current)
    {
        return requested is null ? current : PersonNameFormatter.Normalize(requested);
    }

    private static string? ValidatePartLengths(string? given, string? middle, string? family, string? nickname)
    {
        if (given is { Length: > PartMaxLength })
        {
            return $"名长度不能超过{PartMaxLength}个字符";
        }
        if (middle is { Length: > PartMaxLength })
        {
            return $"中间名长度不能超过{PartMaxLength}个字符";
        }
        if (family is { Length: > PartMaxLength })
        {
            return $"姓长度不能超过{PartMaxLength}个字符";
        }
        if (nickname is { Length: > PartMaxLength })
        {
            return $"昵称长度不能超过{PartMaxLength}个字符";
        }

        return null;
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

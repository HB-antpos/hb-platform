using System.Text.RegularExpressions;
using BlazorApp.Shared.Constants;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 「修改分期商品列表」权限的登记：种子行、可下发给收银机的业务码，以及手工执行的幂等迁移脚本。
/// </summary>
public sealed class InstallmentAmendLinesPermissionTests
{
    private const string Code = "Permissions.PosTerminal.Installments.AmendLines";

    [Fact]
    public void Permission_is_seeded_as_pos_business_code_and_not_granted_to_any_role_template()
    {
        Assert.Equal(Code, Permissions.PosTerminal.Installments.AmendLines);
        var seed = Assert.Single(PermissionSeedData.AllPermissions, item => item.Code == Code);
        Assert.Equal("修改分期商品列表", seed.Name);
        Assert.Equal("POS 分期", seed.Category);
        Assert.Contains(Code, PermissionSeedData.PosTerminalBusinessPermissionCodes);
        // 新权限默认不授予任何角色，由管理员按需授权。
        Assert.All(
            PermissionSeedData.RolePermissionTemplates,
            template => Assert.DoesNotContain(Code, template.PermissionCodes));
    }

    [Fact]
    public void MigrationScript_RegistersOnlyThisPermission_WithoutGrantingAnyRole_AndIsIdempotent()
    {
        var script = File.ReadAllText(ResolveMigrationPath());

        Assert.Contains($"N'{Code}'", script, StringComparison.Ordinal);
        Assert.Contains("N'POS 分期'", script, StringComparison.Ordinal);
        Assert.Contains("@Actor nvarchar(100) = N'Migration_20261008_InstallmentAmendLines'", script, StringComparison.Ordinal);
        Assert.Contains("DB_NAME() <> N'HBweb'", script, StringComparison.Ordinal);
        Assert.Contains("BEGIN TRANSACTION", script, StringComparison.Ordinal);
        Assert.Contains("SET XACT_ABORT ON", script, StringComparison.Ordinal);
        // 幂等：只在权限码不存在时插入。
        Assert.Contains("WHERE NOT EXISTS (SELECT 1 FROM dbo.HbwebSysPermissions p WHERE p.Code = expected.Code)", script, StringComparison.Ordinal);
        // 只写权限表：不授予角色、不碰用户直授权限。
        Assert.DoesNotContain("HBwebSysRolePermissions", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HbwebSysUserPermissions", script, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Regex.Matches(script, @"INSERT INTO dbo\.HbwebSysPermissions\b"));
    }

    private static string ResolveMigrationPath([System.Runtime.CompilerServices.CallerFilePath] string testFilePath = "")
    {
        var testDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("无法解析测试文件目录");
        return Path.GetFullPath(
            Path.Combine(
                testDirectory,
                "..",
                "BlazorApp.Api",
                "Data",
                "Migrations",
                "20261008_AddInstallmentAmendLinesPermission.sql"));
    }
}

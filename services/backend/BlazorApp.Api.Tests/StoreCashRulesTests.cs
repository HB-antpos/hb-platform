using System.Reflection;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Services.StoreCash;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 分店现金管理的纯规则：T2 可见窗口、补录范围、作废时限、日结存档默认与手选、图片规则，以及控制器的权限码门禁。
/// </summary>
public sealed class StoreCashRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 8);

    private static CashAccess Manager(bool canVoid = false, bool canCreate = true) =>
        new("u-1", "店长", false, new HashSet<string> { "S001" }, true, canCreate, canCreate, canVoid);

    private static CashAccess Finance() =>
        new("u-fin", "财务", true, new HashSet<string>(), true, true, true, true);

    // ───────────────────────── 可见性与录入范围 ─────────────────────────

    [Fact]
    public void T2窗口含当天共十四天_其他类别不受限_全部分店权限不受限()
    {
        Assert.Equal(new DateOnly(2026, 9, 25), CashVisibilityRules.T2VisibleFrom(Today));
        Assert.True(CashVisibilityRules.CanSeeExpense(Manager(), "T2", new DateOnly(2026, 9, 25), Today));
        Assert.False(CashVisibilityRules.CanSeeExpense(Manager(), "T2", new DateOnly(2026, 9, 24), Today));
        Assert.True(CashVisibilityRules.CanSeeExpense(Manager(), "Salary", new DateOnly(2025, 1, 1), Today));
        Assert.True(CashVisibilityRules.CanSeeExpense(Finance(), "T2", new DateOnly(2025, 1, 1), Today));
    }

    [Fact]
    public void 录入日期_店长最多回溯七天且不能晚于今天_全部分店权限只限不晚于今天()
    {
        Assert.True(CashVisibilityRules.IsEntryDateAllowed(Manager(), Today, Today));
        Assert.True(CashVisibilityRules.IsEntryDateAllowed(Manager(), Today.AddDays(-7), Today));
        Assert.False(CashVisibilityRules.IsEntryDateAllowed(Manager(), Today.AddDays(-8), Today));
        Assert.False(CashVisibilityRules.IsEntryDateAllowed(Manager(), Today.AddDays(1), Today));
        Assert.True(CashVisibilityRules.IsEntryDateAllowed(Finance(), Today.AddDays(-400), Today));
        Assert.False(CashVisibilityRules.IsEntryDateAllowed(Finance(), Today.AddDays(1), Today));
    }

    [Fact]
    public void 作废_本人在时限内且仍有录入权限才可_作废权限不受限()
    {
        var now = new DateTimeOffset(2026, 10, 8, 2, 0, 0, TimeSpan.Zero);
        var createdAt = now.UtcDateTime.AddHours(-23);

        Assert.True(CashVisibilityRules.CanVoid(Manager(), true, "u-1", createdAt, now));
        Assert.False(CashVisibilityRules.CanVoid(Manager(), true, "u-1", now.UtcDateTime.AddHours(-25), now));
        Assert.False(CashVisibilityRules.CanVoid(Manager(), true, "u-2", createdAt, now));
        Assert.False(CashVisibilityRules.CanVoid(Manager(canCreate: false), false, "u-1", createdAt, now));
        Assert.True(CashVisibilityRules.CanVoid(Manager(canVoid: true), false, "u-2", now.UtcDateTime.AddDays(-30), now));
    }

    [Fact]
    public void 支出类别只有四个且不出现其他叫法()
    {
        Assert.Equal(new[] { "Salary", "Purchase", "T2", "Other" }, StoreCashConstants.ExpenseCategory.All);
        Assert.False(StoreCashConstants.ExpenseCategory.IsValid("Dividend"));
        Assert.False(StoreCashConstants.ExpenseCategory.IsValid("t2"));
    }

    // ───────────────────────── 日结存档默认与手选 ─────────────────────────

    private static CashCloseArchive Archive(string id, int savedHour, int fromHour = 0, int toHour = 24, decimal counted = 100m)
    {
        var midnight = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
        return new CashCloseArchive(
            "S001",
            new DateOnly(2026, 10, 7),
            "POS_1",
            id,
            midnight.AddHours(fromHour),
            midnight.AddHours(toHour),
            midnight.AddHours(savedHour),
            counted,
            counted - 1m
        );
    }

    [Fact]
    public void 默认取保存时间最新的一份_同一时间按编号取大者()
    {
        var archives = new[] { Archive("a", 20), Archive("c", 22), Archive("b", 22) };

        var resolved = CashCloseSelectionResolver.Resolve(archives, null);

        Assert.Equal(StoreCashConstants.SelectionMode.Default, resolved.Mode);
        Assert.Equal(new[] { "c" }, resolved.Included.Select(item => item.CloseId));
        Assert.Equal(1m, archives[0].Variance);
    }

    [Fact]
    public void 手选的存档全部消失时退回最新一份并标记需要确认()
    {
        var current = new StoreCashCloseSelection
        {
            Mode = StoreCashConstants.SelectionMode.Manual,
            CloseIdsJson = CashCloseSelectionResolver.SerializeCloseIds(new[] { "gone" }),
            LatestSavedAtUtcAtSelection = new DateTime(2026, 10, 7, 22, 0, 0),
            Reason = "原因",
        };

        var resolved = CashCloseSelectionResolver.Resolve(new[] { Archive("a", 21) }, current);

        Assert.True(resolved.Stale);
        Assert.Equal(new[] { "a" }, resolved.Included.Select(item => item.CloseId));
    }

    [Fact]
    public void 区间首尾相接不算重叠_交叉才算()
    {
        Assert.False(CashCloseSelectionResolver.HasOverlap(new[] { Archive("a", 15, 0, 15), Archive("b", 23, 15, 24) }));
        Assert.True(CashCloseSelectionResolver.HasOverlap(new[] { Archive("a", 15, 0, 15), Archive("b", 23, 0, 24) }));
        Assert.Empty(CashCloseSelectionResolver.ParseCloseIds("not json"));
    }

    // ───────────────────────── 图片规则 ─────────────────────────

    [Fact]
    public void 图片只收三种格式且不超过五兆_待确认对象键必须在本店目录下()
    {
        Assert.True(StoreCashImageRules.IsValid("image/jpeg", 1));
        Assert.True(StoreCashImageRules.IsValid("image/webp", StoreCashImageRules.MaximumFileSize));
        Assert.False(StoreCashImageRules.IsValid("image/gif", 1));
        Assert.False(StoreCashImageRules.IsValid("image/png", StoreCashImageRules.MaximumFileSize + 1));
        Assert.False(StoreCashImageRules.IsValid("image/png", 0));

        var key = StoreCashImageRules.BuildPendingObjectKey("S001", "abc", "image/png");
        Assert.Equal("cash/pending/S001/abc.png", key);
        Assert.True(StoreCashImageRules.OwnsPendingObjectKey(key, "S001"));
        Assert.False(StoreCashImageRules.OwnsPendingObjectKey(key, "S00"));
        Assert.Equal(
            "cash/S001/202610/abc.jpg",
            StoreCashImageRules.BuildFinalObjectKey("S001", new DateTime(2026, 10, 8), "abc", "image/jpeg")
        );
        // 伪造的图片内容（只有文件头）不能通过内容校验。
        Assert.False(StoreCashImageRules.MatchesImageContent(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 }, "image/jpeg"));
    }

    // ───────────────────────── 控制器门禁 ─────────────────────────

    [Theory]
    [InlineData(nameof(StoreCashController.GetContext), Permissions.Cash.OverviewView)]
    [InlineData(nameof(StoreCashController.GetSummary), Permissions.Cash.OverviewView)]
    [InlineData(nameof(StoreCashController.GetOverview), Permissions.Cash.OverviewView)]
    [InlineData(nameof(StoreCashController.GetDaily), Permissions.Cash.OverviewView)]
    [InlineData(nameof(StoreCashController.ListDeposits), Permissions.Cash.OverviewView)]
    [InlineData(nameof(StoreCashController.GetDeposit), Permissions.Cash.OverviewView)]
    [InlineData(nameof(StoreCashController.ListExpenses), Permissions.Cash.OverviewView)]
    [InlineData(nameof(StoreCashController.GetExpense), Permissions.Cash.OverviewView)]
    [InlineData(nameof(StoreCashController.ListEntries), Permissions.Cash.OverviewView)]
    [InlineData(nameof(StoreCashController.SetCloseSelection), Permissions.Cash.DepositCreate)]
    [InlineData(nameof(StoreCashController.CreateDeposit), Permissions.Cash.DepositCreate)]
    [InlineData(nameof(StoreCashController.SetOpening), Permissions.Cash.DepositCreate)]
    [InlineData(nameof(StoreCashController.CreateCount), Permissions.Cash.DepositCreate)]
    [InlineData(nameof(StoreCashController.CreateExpense), Permissions.Cash.ExpenseCreate)]
    [InlineData(nameof(StoreCashController.ReviewExpense), Permissions.Cash.Void)]
    public void 控制器动作带对应的权限码(string actionName, string expectedPolicy)
    {
        var method = typeof(StoreCashController).GetMethod(actionName)!;
        var policies = method.GetCustomAttributes<AuthorizeAttribute>(inherit: false).Select(item => item.Policy).ToList();

        Assert.Equal(new[] { expectedPolicy }, policies);
    }

    [Fact]
    public void 控制器整体要求登录_所有动作都显式声明了HTTP方法()
    {
        Assert.NotEmpty(typeof(StoreCashController).GetCustomAttributes<AuthorizeAttribute>(inherit: false));
        var actions = typeof(StoreCashController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.Equal(19, actions.Length);
        Assert.All(actions, action => Assert.NotEmpty(action.GetCustomAttributes<HttpMethodAttribute>(inherit: false)));
    }

    [Theory]
    [InlineData(StoreCashConstants.ErrorCodes.StoreForbidden, StatusCodes.Status403Forbidden)]
    [InlineData(StoreCashConstants.ErrorCodes.VoidNotAllowed, StatusCodes.Status403Forbidden)]
    [InlineData(StoreCashConstants.ErrorCodes.StoreNotFound, StatusCodes.Status404NotFound)]
    [InlineData(StoreCashConstants.ErrorCodes.RecordNotFound, StatusCodes.Status404NotFound)]
    [InlineData(StoreCashConstants.ErrorCodes.CloseNotFound, StatusCodes.Status404NotFound)]
    [InlineData(StoreCashConstants.ErrorCodes.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(StoreCashConstants.ErrorCodes.OpeningExists, StatusCodes.Status409Conflict)]
    [InlineData(StoreCashConstants.ErrorCodes.OverrideReasonRequired, StatusCodes.Status400BadRequest)]
    [InlineData(null, StatusCodes.Status400BadRequest)]
    public void 错误码映射HTTP状态码(string? errorCode, int expected)
    {
        Assert.Equal(expected, StoreCashController.MapStatusCode(errorCode));
    }
}

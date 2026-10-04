using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class MinorEmploymentReminderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"minor-reminders-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    public MinorEmploymentReminderTests()
    {
        _db = new(new ConnectionConfig { ConnectionString = $"Data Source={_path}", DbType = DbType.Sqlite, IsAutoCloseConnection = true, InitKeyType = InitKeyType.Attribute });
        _db.CodeFirst.InitTables(typeof(EmployeeMinorReminder), typeof(EmployeeMinorReminderEvent), typeof(User));
    }

    [Fact]
    public async Task RepeatedCalculationKeepsAcknowledgement_AndActualWorkCreatesSeparateFinding()
    {
        var findings = Findings(300);
        await MinorEmploymentReminderService.RecordAsync(_db, "child", "01", "shift", "schedule_created", findings, "manager");
        var row = (await Service().ListAsync(null, "open", 1, 30)).Data!.Items.Single();
        Assert.True((await Service().ActAsync(row.Id, new() { ExpectedRevision = row.Revision, Action = "acknowledge" })).Success);
        await MinorEmploymentReminderService.RecordAsync(_db, "child", "01", "shift", "schedule_published", Findings(360), "manager");
        var noted = (await Service().ListAsync(null, "acknowledged", 1, 30)).Data!.Items.Single();
        Assert.Equal(360, noted.ActualMinutes);
        Assert.True(noted.CanContinuePublishing);
        Assert.Equal(2, noted.Revision);
        await MinorEmploymentReminderService.RecordAsync(_db, "child", "01", "shift", "punch_recorded", findings, "manager");
        Assert.Equal(2, (await Service().ListAsync(null, null, 1, 30)).Data!.Total);
        Assert.Equal(3, await _db.Queryable<EmployeeMinorReminderEvent>().CountAsync());
    }

    [Fact]
    public async Task CorrectedShiftResolvesOnlyItsOwnPriorFinding()
    {
        await MinorEmploymentReminderService.RecordAsync(_db, "child", "01", "shift-a", "schedule_created", Findings(300), "manager");
        await MinorEmploymentReminderService.RecordAsync(_db, "child", "01", "shift-b", "schedule_created", Findings(300), "manager");
        await MinorEmploymentReminderService.RecordAsync(_db, "child", "01", "shift-a", "schedule_updated", new(), "manager");
        Assert.Equal("shift-b", (await Service().ListAsync(null, "open", 1, 30)).Data!.Items.Single().ScheduleGuid);
        Assert.Equal("shift-a", (await Service().ListAsync(null, "resolved", 1, 30)).Data!.Items.Single().ScheduleGuid);
        await MinorEmploymentReminderService.RecordAsync(_db, "child", "01", "shift-a", "schedule_updated", Findings(300), "manager");
        Assert.Equal(2, (await Service().ListAsync(null, "open", 1, 30)).Data!.Total);
    }

    [Fact]
    public async Task CalculationCannotOverwriteManagerActionCommittedAfterItsRead()
    {
        await MinorEmploymentReminderService.RecordAsync(_db, "child", "01", "shift", "schedule_created", Findings(300), "manager");
        var row = await _db.Queryable<EmployeeMinorReminder>().FirstAsync();
        var interleaved = false;
        using var otherConnection = new SqlSugarClient(new ConnectionConfig { ConnectionString = $"Data Source={_path}", DbType = DbType.Sqlite, IsAutoCloseConnection = true, InitKeyType = InitKeyType.Attribute });
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (interleaved || !sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)) return;
            interleaved = true;
            // 在计算读出旧行之后、写入新事实之前提交真实经理动作。
            otherConnection.Updateable<EmployeeMinorReminder>()
                .SetColumns(x => new EmployeeMinorReminder { Status = "escalated", Revision = 2, ActionActor = "senior-manager" })
                .Where(x => x.Id == row.Id).ExecuteCommand();
        };
        await MinorEmploymentReminderService.RecordAsync(_db, "child", "01", "shift", "schedule_updated", Findings(360), "manager");
        _db.Aop.OnLogExecuting = null;
        var current = await _db.Queryable<EmployeeMinorReminder>().InSingleAsync(row.Id);
        Assert.True(interleaved);
        Assert.Equal("escalated", current.Status);
        Assert.Equal(2, current.Revision);
        Assert.Equal("senior-manager", current.ActionActor);
        Assert.Equal(360, current.ActualMinutes);
    }

    [Fact]
    public async Task StoreScopeAndRevisionPreventUnauthorizedOrStaleActions()
    {
        await MinorEmploymentReminderService.RecordAsync(_db, "child", "99", "shift", "schedule_created", Findings(300), "manager");
        var row = await _db.Queryable<EmployeeMinorReminder>().FirstAsync();
        Assert.Empty((await Service().ListAsync(null, null, 1, 30)).Data!.Items);
        Assert.False((await Service().ListAsync("99", null, 1, 30)).Success);
        Assert.False((await Service().ActAsync(row.Id, new() { ExpectedRevision = 1, Action = "escalate" })).Success);
        Assert.True((await Service("99").ActAsync(row.Id, new() { ExpectedRevision = 1, Action = "escalate", Comment = "Please review" })).Success);
        Assert.False((await Service("99").ActAsync(row.Id, new() { ExpectedRevision = 1, Action = "acknowledge" })).Success);
        Assert.Equal("escalated", (await _db.Queryable<EmployeeMinorReminder>().InSingleAsync(row.Id)).Status);
    }

    private static MinorEmploymentComplianceEvaluationDto Findings(int minutes) => new() { Findings = [new() { RuleId = "QLD_SCHOOL_DAY", Severity = "warning", RuleCategory = "qld_law", Message = "School-day hours exceed the reference limit", WorkDate = new DateTime(2026, 9, 14), ActualMinutes = minutes, LimitMinutes = 240 }] };
    private MinorEmploymentReminderService Service(string store = "01")
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, _db);
        var user = Mock.Of<ICurrentUserService>(x => x.GetCurrentUsername() == "manager");
        var scope = new Mock<ICurrentUserManageableStoreScopeService>();
        scope.Setup(x => x.GetScopeAsync()).ReturnsAsync(new CurrentUserManageableStoreScope { IsAllowed = true, IsAuthenticated = true, StoreCodes = [store] });
        return new(context, user, scope.Object);
    }
    public void Dispose() { _db.Dispose(); File.Delete(_path); }
}

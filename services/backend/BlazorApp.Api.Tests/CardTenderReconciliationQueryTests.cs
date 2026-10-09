using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Models.Linkly;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class CardTenderReconciliationQueryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly CardTenderReconciliationQueryService _service;

    public CardTenderReconciliationQueryServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.Ado.ExecuteCommand("""
            CREATE TABLE POSM_CardTenderReconciliationIssue (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DedupKey TEXT NOT NULL,
                IssueType TEXT NOT NULL,
                Severity TEXT NOT NULL,
                Source TEXT NOT NULL,
                Status TEXT NOT NULL,
                StoreCode TEXT NOT NULL,
                DeviceCode TEXT NULL,
                Environment TEXT NULL,
                SessionId TEXT NULL,
                TxnRef TEXT NULL,
                OrderGuid TEXT NULL,
                PaymentGuid TEXT NULL,
                Amount NUMERIC NULL,
                Detail TEXT NULL,
                OccurrenceCount INTEGER NOT NULL,
                FirstDetectedAt TEXT NOT NULL,
                LastDetectedAt TEXT NOT NULL,
                ResolvedAt TEXT NULL
            );
            """);
        var context = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(POSMSqlSugarContext));
        typeof(POSMSqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, _db);
        _service = new CardTenderReconciliationQueryService(context);
    }

    [Fact]
    public async Task GetListAsync_defaults_to_open_issues_newest_first()
    {
        await InsertAsync("a", "Open", "S01", lastDetected: new DateTime(2026, 10, 1, 8, 0, 0));
        await InsertAsync("b", "Open", "S01", lastDetected: new DateTime(2026, 10, 2, 8, 0, 0));
        await InsertAsync("c", "Resolved", "S01", lastDetected: new DateTime(2026, 10, 3, 8, 0, 0));

        var result = await _service.GetListAsync(new CardTenderReconciliationQueryDto());

        Assert.Equal(2, result.Total);
        Assert.Equal(["b", "a"], result.Items.Select(item => item.SessionId));
        Assert.All(result.Items, item => Assert.Equal("Open", item.Status));
        Assert.Equal(DateTimeKind.Utc, result.Items[0].LastDetectedAtUtc.Kind);
    }

    [Fact]
    public async Task GetListAsync_filters_by_status_type_store_session_and_order()
    {
        var order = Guid.NewGuid().ToString("D");
        await InsertAsync("a", "Open", "S01", issueType: "SessionNotApproved", orderGuid: order);
        await InsertAsync("b", "Resolved", "S01", issueType: "ApprovedSessionWithoutOrder");
        await InsertAsync("c", "Open", "S02", issueType: "SessionNotApproved");

        Assert.Equal(3, (await _service.GetListAsync(new CardTenderReconciliationQueryDto { Status = "all" })).Total);
        Assert.Equal(["b"], (await _service.GetListAsync(new CardTenderReconciliationQueryDto { Status = "resolved" })).Items.Select(item => item.SessionId));
        Assert.Equal(["c"], (await _service.GetListAsync(new CardTenderReconciliationQueryDto { StoreCode = "S02" })).Items.Select(item => item.SessionId));
        Assert.Equal(["a"], (await _service.GetListAsync(new CardTenderReconciliationQueryDto { OrderGuid = order })).Items.Select(item => item.SessionId));
        Assert.Equal(["c", "a"], (await _service.GetListAsync(new CardTenderReconciliationQueryDto { IssueType = "SessionNotApproved" })).Items.Select(item => item.SessionId).OrderByDescending(id => id));
        Assert.Equal(["b"], (await _service.GetListAsync(new CardTenderReconciliationQueryDto { Status = "All", SessionId = "b" })).Items.Select(item => item.SessionId));
    }

    [Fact]
    public async Task GetListAsync_paginates_and_clamps_page_size()
    {
        for (var i = 0; i < 5; i++)
            await InsertAsync($"s{i}", "Open", "S01", lastDetected: new DateTime(2026, 10, 1, 8, i, 0));

        var second = await _service.GetListAsync(new CardTenderReconciliationQueryDto { PageNumber = 2, PageSize = 2 });
        var oversized = await _service.GetListAsync(new CardTenderReconciliationQueryDto { PageSize = 100_000 });

        Assert.Equal(5, second.Total);
        Assert.Equal(["s2", "s1"], second.Items.Select(item => item.SessionId));
        Assert.Equal(100, oversized.PageSize);
    }

    [Fact]
    public async Task GetListAsync_rejects_unknown_status()
    {
        var exception = await Assert.ThrowsAsync<CardTenderReconciliationRequestException>(
            () => _service.GetListAsync(new CardTenderReconciliationQueryDto { Status = "Bogus" }));

        Assert.Equal("INVALID_STATUS", exception.Code);
    }

    [Fact]
    public async Task Controller_returns_bad_request_for_invalid_status_and_ok_otherwise()
    {
        var controller = new CardTenderReconciliationController(_service);

        var bad = await controller.GetIssues(new CardTenderReconciliationQueryDto { Status = "Bogus" }, CancellationToken.None);
        var ok = await controller.GetIssues(new CardTenderReconciliationQueryDto(), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(bad.Result);
        Assert.IsType<OkObjectResult>(ok.Result);
    }

    [Fact]
    public void Controller_is_admin_only()
    {
        var authorize = typeof(CardTenderReconciliationController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Contains("Admin", authorize.Roles);
    }

    [Fact]
    public async Task Program_registers_the_query_service()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "services/backend/BlazorApp.Api/Program.cs")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var program = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "services/backend/BlazorApp.Api/Program.cs"));

        Assert.Contains("AddScoped<ICardTenderReconciliationQueryService, CardTenderReconciliationQueryService>()", program);
    }

    private async Task InsertAsync(
        string sessionId,
        string status,
        string storeCode,
        string issueType = "SessionNotApproved",
        string? orderGuid = null,
        DateTime? lastDetected = null)
    {
        var detected = lastDetected ?? new DateTime(2026, 10, 1, 8, 0, 0);
        await _db.Insertable(new PosmCardTenderReconciliationIssue
        {
            DedupKey = $"{issueType}|{orderGuid}||{sessionId}",
            IssueType = issueType,
            Severity = "Error",
            Source = "OrderSync",
            Status = status,
            StoreCode = storeCode,
            SessionId = sessionId,
            OrderGuid = orderGuid,
            OccurrenceCount = 1,
            FirstDetectedAt = detected,
            LastDetectedAt = detected,
        }).ExecuteCommandAsync();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}

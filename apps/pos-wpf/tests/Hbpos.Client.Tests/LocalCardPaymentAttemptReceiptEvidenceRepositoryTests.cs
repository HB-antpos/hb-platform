using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>
/// 小票批准证据落库：只写一次、不改 Status/UpdatedAt（付款工作流随后还要按原 UpdatedAt 做 CAS），
/// 并能随 attempt 读回。
/// </summary>
public sealed class LocalCardPaymentAttemptReceiptEvidenceRepositoryTests
{
    [Fact]
    public async Task Receipt_evidence_is_written_once_without_touching_status_or_updated_at()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-receipt-evidence-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalCardPaymentAttemptRepository(store);
            var updatedAt = DateTimeOffset.Parse("2026-10-22T08:50:00.9247629+00:00");
            var attempt = new LocalCardPaymentAttempt(
                Guid.Parse("3b54add1-613a-4b54-8ea1-355134d5ff9c"),
                null,
                LinklyLocalReceiptApprovalEvidenceTests.TxnRef,
                "Linkly",
                "Production",
                nameof(LinklyConnectionMode.LocalIp),
                "P",
                13.99m,
                LocalCardPaymentAttemptStatus.Pending,
                "{}",
                "1002",
                "POS_1002_2111",
                "C001",
                null,
                null,
                null,
                updatedAt,
                updatedAt,
                null,
                null);
            await repository.CreateAsync(attempt);

            Assert.True(await repository.TryRecordReceiptApprovalEvidenceAsync(attempt.AttemptGuid, "{\"first\":true}"));
            Assert.False(await repository.TryRecordReceiptApprovalEvidenceAsync(attempt.AttemptGuid, "{\"second\":true}"));
            Assert.False(await repository.TryRecordReceiptApprovalEvidenceAsync(Guid.NewGuid(), "{\"other\":true}"));

            var stored = await repository.GetAttemptAsync(attempt.AttemptGuid);
            Assert.NotNull(stored);
            Assert.Equal("{\"first\":true}", stored.ReceiptApprovalEvidence);
            Assert.Equal(LocalCardPaymentAttemptStatus.Pending, stored.Status);
            Assert.Equal(updatedAt, stored.UpdatedAt);

            var open = Assert.Single(await repository.GetOpenAttemptsAsync("1002", "POS_1002_2111", "Production"));
            Assert.Equal("{\"first\":true}", open.ReceiptApprovalEvidence);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }
}

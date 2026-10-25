using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 一次性 ProductSetCode 类型修复执行器。
///
/// 此类刻意不注册 DI、不暴露 HTTP API，也不触发 HQ 同步；只能由受控的本地运维入口
/// 显式构造并以 <see cref="ProductSetCodeTypeRepairOptions.Apply"/> 执行。执行前先将扫描
/// 结果与已批准的基线逐项比对，任何漂移都拒绝写入。
/// </summary>
public sealed class ProductSetCodeTypeRepairRunner
{
    public static readonly ProductSetCodeTypeRepairBaseline ApprovedBaseline = new(
        MismatchParentCount: 1525,
        TypeOneMismatchParentCount: 1493,
        IsolatedNormalParentCount: 32,
        IsolatedTypeOneParentCount: 38,
        EligibleParentCount: 1455,
        EligibleActiveParentCount: 1439,
        EligibleInactiveParentCount: 16,
        ChildTypeUpdateCount: 7281,
        MissingStoreRetailPriceCount: 8835,
        ZeroStoreRetailPurchasePriceCount: 51,
        MissingStoreProjectionCount: 35771,
        ActiveStoreCount: 33
    );

    private readonly ISqlSugarClient _db;

    public ProductSetCodeTypeRepairRunner(ISqlSugarClient db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// 扫描、生成不可变快照和清单；dry-run 绝不调用 BeginTran 或任何写入方法。
    /// </summary>
    public async Task<ProductSetCodeTypeRepairRunReport> RunAsync(
        ProductSetCodeTypeRepairOptions options,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            throw new ArgumentException("必须指定修复材料输出目录", nameof(options));
        }
        if (options.Apply)
        {
            throw new InvalidOperationException(
                "直接 Apply 已禁用；请先生成并审核快照，再调用 ApplyPreparedAsync 绑定快照 SHA-256"
            );
        }

        var runId = string.IsNullOrWhiteSpace(options.RunId)
            ? $"product-set-code-type-repair-{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}"
            : options.RunId.Trim();
        ValidateRunId(runId);
        var snapshot = await ScanAsync(runId, cancellationToken);
        var baseline = options.ExpectedBaseline ?? ApprovedBaseline;
        var differences = baseline.Diff(snapshot.Baseline);
        if (differences.Count != 0)
        {
            throw new ProductSetCodeTypeRepairBaselineMismatchException(differences);
        }

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var snapshotPath = BuildOutputPath(outputDirectory, $"{runId}.snapshot.json");
        var manifestPath = BuildOutputPath(outputDirectory, $"{runId}.manifest.json");
        if (File.Exists(snapshotPath) || File.Exists(manifestPath))
        {
            throw new InvalidOperationException("同运行编号的快照或清单已存在，禁止覆盖");
        }
        var snapshotJson = SerializeCanonical(snapshot);
        var snapshotHash = ComputeSha256(snapshotJson);
        var manifest = new ProductSetCodeTypeRepairManifest
        {
            RunId = runId,
            CreatedAtUtc = DateTime.UtcNow,
            SnapshotFileName = Path.GetFileName(snapshotPath),
            SnapshotSha256 = snapshotHash,
            Baseline = snapshot.Baseline,
            EligibleProductCodes = snapshot.Eligible.Select(x => x.Product.ProductCode!).ToList(),
            IsolatedProducts = BuildIsolationSummaries(snapshot.Isolated),
            DryRun = true,
        };
        await WriteAtomicallyAsync(snapshotPath, snapshotJson, cancellationToken);
        await WriteAtomicallyAsync(manifestPath, SerializeCanonical(manifest), cancellationToken);

        var report = new ProductSetCodeTypeRepairRunReport
        {
            RunId = runId,
            ManifestPath = manifestPath,
            SnapshotPath = snapshotPath,
            SnapshotSha256 = snapshotHash,
            Baseline = snapshot.Baseline,
            DryRun = true,
            IsolatedProducts = BuildIsolationSummaries(snapshot.Isolated),
        };
        return report;
    }

    /// <summary>
    /// 只允许应用已经由 prepare 生成并人工核对过 SHA-256 的不可变快照。
    /// </summary>
    public async Task<ProductSetCodeTypeRepairRunReport> ApplyPreparedAsync(
        string snapshotPath,
        string expectedSnapshotSha256,
        string actorName,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(snapshotPath) || !File.Exists(snapshotPath))
        {
            throw new FileNotFoundException("已审核的修复快照不存在", snapshotPath);
        }
        if (
            string.IsNullOrWhiteSpace(expectedSnapshotSha256)
            || expectedSnapshotSha256.Length != 64
            || expectedSnapshotSha256.Any(x => !Uri.IsHexDigit(x))
        )
        {
            throw new ArgumentException("必须提供64位十六进制快照 SHA-256", nameof(expectedSnapshotSha256));
        }

        var normalizedSnapshotPath = Path.GetFullPath(snapshotPath);
        var snapshotJson = await File.ReadAllTextAsync(normalizedSnapshotPath, cancellationToken);
        var actualSnapshotSha256 = ComputeSha256(snapshotJson);
        if (!string.Equals(actualSnapshotSha256, expectedSnapshotSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("已审核快照 SHA-256 不匹配，拒绝执行");
        }
        var snapshot = JsonSerializer.Deserialize<ProductSetCodeTypeRepairSnapshot>(snapshotJson)
            ?? throw new InvalidOperationException("已审核快照无法读取");
        ValidateRunId(snapshot.RunId);
        var baselineDifferences = ApprovedBaseline.Diff(snapshot.Baseline);
        if (baselineDifferences.Count != 0)
        {
            throw new ProductSetCodeTypeRepairBaselineMismatchException(baselineDifferences);
        }
        if (!string.Equals(
            Path.GetFileName(normalizedSnapshotPath),
            $"{snapshot.RunId}.snapshot.json",
            StringComparison.Ordinal
        ))
        {
            throw new InvalidOperationException("快照文件名与运行编号不一致");
        }

        var outputDirectory = Path.GetDirectoryName(normalizedSnapshotPath)
            ?? throw new InvalidOperationException("快照输出目录无效");
        var manifestPath = BuildOutputPath(outputDirectory, $"{snapshot.RunId}.manifest.json");
        var manifest = JsonSerializer.Deserialize<ProductSetCodeTypeRepairManifest>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken)
        ) ?? throw new InvalidOperationException("已审核清单无法读取");
        if (
            !manifest.DryRun
            || !string.Equals(manifest.RunId, snapshot.RunId, StringComparison.Ordinal)
            || !string.Equals(manifest.SnapshotSha256, actualSnapshotSha256, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidOperationException("已审核清单与快照不一致");
        }

        var journalPath = BuildOutputPath(outputDirectory, $"{snapshot.RunId}.journal.json");
        var verificationPath = BuildOutputPath(outputDirectory, $"{snapshot.RunId}.verification.json");
        if (File.Exists(journalPath) || File.Exists(verificationPath))
        {
            throw new InvalidOperationException("该运行编号已存在执行或验收材料，拒绝重复执行");
        }

        // apply 前实时重扫：既检查批准基线，也逐商品比较完整快照指纹。
        var applySnapshot = await ScanAsync(snapshot.RunId, cancellationToken);
        var liveBaselineDifferences = ApprovedBaseline.Diff(applySnapshot.Baseline);
        if (liveBaselineDifferences.Count != 0)
        {
            throw new ProductSetCodeTypeRepairBaselineMismatchException(liveBaselineDifferences);
        }
        if (!string.Equals(ComputePlanHash(snapshot), ComputePlanHash(applySnapshot), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("已审核快照与实时修复清单不一致，已停止且未写入数据库");
        }

        var report = new ProductSetCodeTypeRepairRunReport
        {
            RunId = snapshot.RunId,
            ManifestPath = manifestPath,
            SnapshotPath = normalizedSnapshotPath,
            SnapshotSha256 = actualSnapshotSha256,
            Baseline = snapshot.Baseline,
            DryRun = false,
            IsolatedProducts = BuildIsolationSummaries(snapshot.Isolated),
            JournalPath = journalPath,
        };
        report.JournalPath = journalPath;
        await WriteAtomicallyAsync(journalPath, SerializeCanonical(report), cancellationToken);

        foreach (var target in snapshot.Eligible.OrderBy(x => x.Product.ProductCode, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await ApplyOneAsync(
                    target,
                    string.IsNullOrWhiteSpace(actorName) ? "System" : actorName.Trim(),
                    snapshot.RunId,
                    cancellationToken
                );
                report.Succeeded.Add(result);
                await WriteAtomicallyAsync(journalPath, SerializeCanonical(report), cancellationToken);
            }
            catch (Exception ex)
            {
                // 单商品事务已回滚；保留失败原因，不做推测性重试。
                report.Failed.Add(new ProductSetCodeTypeRepairFailure
                {
                    ProductCode = target.Product.ProductCode ?? string.Empty,
                    Reason = ex.Message,
                    IsBusinessLockConflict = SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _),
                });
                await WriteAtomicallyAsync(journalPath, SerializeCanonical(report), cancellationToken);
            }
        }
        report = await MergeCommittedAuditIntoJournalAsync(
            snapshot,
            report,
            journalPath,
            cancellationToken
        );
        var verification = await VerifyAsync(
            normalizedSnapshotPath,
            journalPath,
            cancellationToken
        );
        report.VerificationPath = verificationPath;
        report.Verification = verification;
        await WriteAtomicallyAsync(
            verificationPath,
            SerializeCanonical(verification),
            cancellationToken
        );
        await WriteAtomicallyAsync(journalPath, SerializeCanonical(report), cancellationToken);
        return report;
    }

    /// <summary>
    /// 只允许回退本执行器创建的行。回退前须验证各商品当前 after 指纹，避免覆盖后续人工修改。
    /// </summary>
    public async Task<ProductSetCodeTypeRepairRollbackReport> RollbackAsync(
        string snapshotPath,
        string journalPath,
        string actorName,
        CancellationToken cancellationToken = default
    )
    {
        var snapshotJson = await File.ReadAllTextAsync(snapshotPath, cancellationToken);
        var snapshot = JsonSerializer.Deserialize<ProductSetCodeTypeRepairSnapshot>(snapshotJson)
            ?? throw new InvalidOperationException("回滚快照无法读取");
        var journal = JsonSerializer.Deserialize<ProductSetCodeTypeRepairRunReport>(
            await File.ReadAllTextAsync(journalPath, cancellationToken)
        ) ?? throw new InvalidOperationException("回滚执行日志无法读取");
        if (!string.Equals(ComputeSha256(snapshotJson), journal.SnapshotSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("回滚快照哈希不匹配，拒绝回滚");
        }
        journal = await MergeCommittedAuditIntoJournalAsync(snapshot, journal, journalPath, cancellationToken);

        var report = new ProductSetCodeTypeRepairRollbackReport { RunId = snapshot.RunId };
        var targetByCode = snapshot.Eligible.ToDictionary(x => x.Product.ProductCode!, StringComparer.Ordinal);
        foreach (var applied in journal.Succeeded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!targetByCode.TryGetValue(applied.ProductCode, out var before))
            {
                throw new InvalidOperationException($"回滚日志包含未知商品 {applied.ProductCode}");
            }
            await _db.Ado.BeginTranAsync();
            try
            {
                var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                    _db,
                    new[] { applied.ProductCode }
                );
                _ = lockScope;
                var current = await ReadTargetAsync(applied.ProductCode, before.ActiveStoreCodes);
                if (!string.Equals(BuildFingerprint(current), applied.AfterFingerprint, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("回滚前 after 指纹不一致，可能已有后续修改");
                }
                await RestoreRowsAsync(before, applied, actorName);
                var restored = await ReadTargetAsync(applied.ProductCode, before.ActiveStoreCodes);
                var restoredOriginalRowsFingerprint = BuildRollbackComparableFingerprint(
                    restored,
                    applied
                );
                if (!string.Equals(restoredOriginalRowsFingerprint, before.BeforeFingerprint, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("回滚后前态指纹不一致，事务已回滚");
                }
                await WriteAuditAsync(
                    applied.ProductCode,
                    applied.AfterFingerprint,
                    BuildFingerprint(restored),
                    actorName,
                    "ProductSetCodeTypeRepairRollback",
                    snapshot.RunId,
                    applied.InsertedStoreRetailPriceIds,
                    applied.InsertedStoreProjectionIds
                );
                await _db.Ado.CommitTranAsync();
                report.RolledBackProductCodes.Add(applied.ProductCode);
            }
            catch (Exception ex)
            {
                await _db.Ado.RollbackTranAsync();
                report.Failures.Add(new ProductSetCodeTypeRepairFailure
                {
                    ProductCode = applied.ProductCode,
                    Reason = ex.Message,
                    IsBusinessLockConflict = SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _),
                });
            }
        }
        return report;
    }

    /// <summary>
    /// 数据库审计和文件 journal 互相校验。进程若在提交后、journal 落盘前中断，
    /// 以同事务已提交的审计补齐 journal；反向缺失或内容冲突则拒绝回滚。
    /// </summary>
    private async Task<ProductSetCodeTypeRepairRunReport> MergeCommittedAuditIntoJournalAsync(
        ProductSetCodeTypeRepairSnapshot snapshot,
        ProductSetCodeTypeRepairRunReport journal,
        string journalPath,
        CancellationToken cancellationToken
    )
    {
        var sourceReferencePrefix = snapshot.RunId + ":";
        var auditRows = await _db.Queryable<WarehouseProductChangeHistory>()
            .Where(x => x.Source == "ProductSetCodeTypeRepair" && x.SourceReference != null && x.SourceReference.StartsWith(sourceReferencePrefix))
            .ToListAsync();
        var auditApplied = auditRows.Select(ParseAppliedAudit).ToDictionary(x => x.ProductCode, StringComparer.Ordinal);
        var journalApplied = journal.Succeeded.ToDictionary(x => x.ProductCode, StringComparer.Ordinal);
        var changed = false;
        foreach (var (code, audit) in auditApplied)
        {
            if (!journalApplied.TryGetValue(code, out var fromJournal))
            {
                journal.Succeeded.Add(audit);
                changed = true;
                continue;
            }
            if (!AppliedEntriesEqual(audit, fromJournal))
            {
                throw new InvalidOperationException($"审计与 journal 冲突，拒绝回滚: {code}");
            }
        }
        foreach (var code in journalApplied.Keys)
        {
            if (!auditApplied.ContainsKey(code))
            {
                throw new InvalidOperationException($"journal 成功记录缺少数据库审计，拒绝回滚: {code}");
            }
        }
        if (changed)
        {
            await WriteAtomicallyAsync(journalPath, SerializeCanonical(journal), cancellationToken);
        }
        return journal;
    }

    private static ProductSetCodeTypeRepairAppliedProduct ParseAppliedAudit(WarehouseProductChangeHistory row)
    {
        using var document = JsonDocument.Parse(row.ChangesJson);
        var root = document.RootElement;
        static string GetRequiredString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new InvalidOperationException($"审计字段 {name} 为空")
            : throw new InvalidOperationException($"审计缺少字段 {name}");
        static List<string> GetStringList(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : throw new InvalidOperationException($"审计缺少数组字段 {name}");
        return new ProductSetCodeTypeRepairAppliedProduct
        {
            ProductCode = row.ProductCode,
            BeforeFingerprint = GetRequiredString(root, "beforeFingerprint"),
            AfterFingerprint = GetRequiredString(root, "afterFingerprint"),
            InsertedStoreRetailPriceIds = GetStringList(root, "insertedStoreRetailPriceIds"),
            InsertedStoreProjectionIds = GetStringList(root, "insertedStoreProjectionIds"),
        };
    }

    private static bool AppliedEntriesEqual(ProductSetCodeTypeRepairAppliedProduct left, ProductSetCodeTypeRepairAppliedProduct right) =>
        string.Equals(left.ProductCode, right.ProductCode, StringComparison.Ordinal)
        && string.Equals(left.BeforeFingerprint, right.BeforeFingerprint, StringComparison.Ordinal)
        && string.Equals(left.AfterFingerprint, right.AfterFingerprint, StringComparison.Ordinal)
        && left.InsertedStoreRetailPriceIds.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(right.InsertedStoreRetailPriceIds.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal)
        && left.InsertedStoreProjectionIds.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(right.InsertedStoreProjectionIds.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>
    /// 只读验收。所有目标表按本次 1,525 个商品编码批量读取；此方法不开始事务、不调用任何写入 API。
    /// </summary>
    public async Task<ProductSetCodeTypeRepairVerificationReport> VerifyAsync(
        string snapshotPath,
        string journalPath,
        CancellationToken cancellationToken = default
    )
    {
        var snapshotJson = await File.ReadAllTextAsync(snapshotPath, cancellationToken);
        var snapshot = JsonSerializer.Deserialize<ProductSetCodeTypeRepairSnapshot>(snapshotJson)
            ?? throw new InvalidOperationException("验收快照无法读取");
        var journal = JsonSerializer.Deserialize<ProductSetCodeTypeRepairRunReport>(
            await File.ReadAllTextAsync(journalPath, cancellationToken)
        ) ?? throw new InvalidOperationException("验收日志无法读取");
        var report = new ProductSetCodeTypeRepairVerificationReport
        {
            RunId = snapshot.RunId,
            SnapshotSha256 = ComputeSha256(snapshotJson),
            ExpectedEligibleCount = snapshot.Eligible.Count,
            ExpectedIsolatedCount = snapshot.Isolated.Count,
        };
        foreach (var violation in ValidateJournalCoverage(snapshot, journal, report.SnapshotSha256))
        {
            report.Violations.Add(violation);
        }

        var allCodes = snapshot.Eligible.Select(x => x.Product.ProductCode!)
            .Concat(snapshot.Isolated.Select(x => x.ProductCode))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        var currentByCode = await ReadTargetsBatchAsync(allCodes, snapshot.ActiveStoreCodes, cancellationToken);
        var activeStores = await _db.Queryable<Store>()
            .Where(x => x.IsActive && !x.IsDeleted && x.StoreCode != null)
            .Select(x => x.StoreCode!)
            .ToListAsync();
        if (!activeStores.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(snapshot.ActiveStoreCodes.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            report.Violations.Add(new ProductSetCodeTypeRepairVerificationViolation("ActiveStores", "活动分店清单与执行前快照不一致"));
        }

        var appliedByCode = journal.Succeeded
            .GroupBy(x => x.ProductCode, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        foreach (var before in snapshot.Eligible)
        {
            var code = before.Product.ProductCode!;
            if (!currentByCode.TryGetValue(code, out var current))
            {
                report.Violations.Add(new ProductSetCodeTypeRepairVerificationViolation(code, "商品或关联数据缺失"));
                continue;
            }
            if (
                appliedByCode.TryGetValue(code, out var applied)
                && !string.Equals(
                    BuildFingerprint(current),
                    applied.AfterFingerprint,
                    StringComparison.Ordinal
                )
            )
            {
                report.Violations.Add(new(code, "当前完整指纹与逐商品提交后的指纹不一致"));
            }
            try
            {
                VerifyEligibleTarget(before, current, report);
            }
            catch (Exception ex)
            {
                report.Violations.Add(new ProductSetCodeTypeRepairVerificationViolation(code, $"验收计算异常: {ex.Message}"));
            }
        }
        foreach (var isolated in snapshot.Isolated)
        {
            if (!currentByCode.TryGetValue(isolated.ProductCode, out var current))
            {
                report.Violations.Add(new ProductSetCodeTypeRepairVerificationViolation(isolated.ProductCode, "隔离商品缺失"));
                continue;
            }
            if (!string.Equals(BuildFingerprint(current), isolated.BeforeFingerprint, StringComparison.Ordinal))
            {
                report.Violations.Add(new ProductSetCodeTypeRepairVerificationViolation(isolated.ProductCode, "隔离商品完整指纹已变化"));
            }
        }
        report.VerifiedEligibleCount = snapshot.Eligible.Count;
        report.VerifiedIsolatedCount = snapshot.Isolated.Count;
        report.IsValid = report.Violations.Count == 0;
        return report;
    }

    /// <summary>纯逻辑闸门，便于不连接数据库的测试和运维前预检。</summary>
    public static IReadOnlyList<ProductSetCodeTypeRepairVerificationViolation> ValidateJournalCoverage(
        ProductSetCodeTypeRepairSnapshot snapshot,
        ProductSetCodeTypeRepairRunReport journal,
        string actualSnapshotSha256
    )
    {
        var violations = new List<ProductSetCodeTypeRepairVerificationViolation>();
        if (!string.Equals(actualSnapshotSha256, journal.SnapshotSha256, StringComparison.Ordinal))
        {
            violations.Add(new("Journal", "snapshot SHA-256 不匹配"));
        }
        if (!string.Equals(snapshot.RunId, journal.RunId, StringComparison.Ordinal))
        {
            violations.Add(new("Journal", "运行编号不匹配"));
        }
        if (journal.DryRun)
        {
            violations.Add(new("Journal", "journal 标记为 dry-run，不能作为执行验收依据"));
        }
        if (journal.Failed.Count != 0)
        {
            violations.Add(new("Journal", $"存在 {journal.Failed.Count} 个执行失败商品"));
        }
        var expected = snapshot.Eligible.Select(x => x.Product.ProductCode!).ToHashSet(StringComparer.Ordinal);
        var actual = journal.Succeeded.Select(x => x.ProductCode).ToList();
        if (actual.Count != actual.Distinct(StringComparer.Ordinal).Count())
        {
            violations.Add(new("Journal", "成功 journal 含重复商品编码"));
        }
        if (!expected.SetEquals(actual))
        {
            violations.Add(new("Journal", "成功 journal 未完整覆盖全部合格商品"));
        }
        return violations;
    }

    private async Task<Dictionary<string, ProductSetCodeTypeRepairTarget>> ReadTargetsBatchAsync(
        IReadOnlyCollection<string> productCodes,
        IReadOnlyCollection<string> activeStoreCodes,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var codes = productCodes.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
        var products = await _db.Queryable<Product>().Where(x => !x.IsDeleted && x.ProductCode != null && codes.Contains(x.ProductCode)).ToListAsync();
        var warehouses = await _db.Queryable<WarehouseProduct>().Where(x => !x.IsDeleted && codes.Contains(x.ProductCode)).ToListAsync();
        var domestic = await _db.Queryable<DomesticProduct>().Where(x => codes.Contains(x.ProductCode)).ToListAsync();
        var children = await _db.Queryable<ProductSetCode>().Where(x => codes.Contains(x.ProductCode)).ToListAsync();
        var prices = await _db.Queryable<StoreRetailPrice>().Where(x => x.ProductCode != null && codes.Contains(x.ProductCode)).ToListAsync();
        var projections = await _db.Queryable<StoreMultiCodeProduct>().Where(x => x.ProductCode != null && codes.Contains(x.ProductCode)).ToListAsync();
        return products.Where(x => !string.IsNullOrWhiteSpace(x.ProductCode)).ToDictionary(
            x => x.ProductCode!,
            product => new ProductSetCodeTypeRepairTarget
            {
                Product = product,
                WarehouseProduct = warehouses.SingleOrDefault(x => x.ProductCode == product.ProductCode),
                DomesticProduct = domestic.SingleOrDefault(x => x.ProductCode == product.ProductCode),
                Children = children.Where(x => x.ProductCode == product.ProductCode).OrderBy(x => x.SetCodeId, StringComparer.Ordinal).ToList(),
                StoreRetailPrices = prices.Where(x => x.ProductCode == product.ProductCode).OrderBy(x => x.UUID, StringComparer.Ordinal).ToList(),
                StoreProjections = projections.Where(x => x.ProductCode == product.ProductCode).OrderBy(x => x.UUID, StringComparer.Ordinal).ToList(),
                ActiveStoreCodes = activeStoreCodes.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            },
            StringComparer.Ordinal
        );
    }

    private static void VerifyEligibleTarget(
        ProductSetCodeTypeRepairTarget before,
        ProductSetCodeTypeRepairTarget current,
        ProductSetCodeTypeRepairVerificationReport report
    )
    {
        var code = before.Product.ProductCode!;
        var liveChildren = current.Children.Where(x => !x.IsDeleted).ToList();
        if (liveChildren.Any(x => x.SetType != 1))
        {
            report.Violations.Add(new(code, "存在未删除子项 SetType != 1"));
        }
        if (!before.Product.IsActive)
        {
            if (!string.Equals(BuildInactiveBusinessFingerprint(before), BuildInactiveBusinessFingerprint(current), StringComparison.Ordinal))
            {
                report.Violations.Add(new(code, "停用商品发生了 SetType 及审计字段之外的业务变更"));
            }
            return;
        }

        foreach (var storeCode in before.ActiveStoreCodes)
        {
            var storePrices = current.StoreRetailPrices.Where(x => !x.IsDeleted && x.StoreCode == storeCode).ToList();
            if (storePrices.Count != 1 || !storePrices[0].IsActive || (storePrices[0].PurchasePrice ?? 0m) <= 0m)
            {
                report.Violations.Add(new(code, $"门店 {storeCode} 主价格未满足唯一活动正成本"));
                continue;
            }
            var price = storePrices[0];
            var activeChildren = liveChildren.Where(x => x.IsActive).ToList();
            var requiredCodes = activeChildren
                .Select(x => x.SetProductCode.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var activeProjectionRows = current.StoreProjections
                .Where(x => !x.IsDeleted && x.IsActive && x.StoreCode == storeCode)
                .ToList();
            if (
                activeProjectionRows.Count != requiredCodes.Count
                || activeProjectionRows.Any(x =>
                    string.IsNullOrWhiteSpace(x.MultiCodeProductCode)
                    || !requiredCodes.Contains(x.MultiCodeProductCode.Trim())
                )
            )
            {
                report.Violations.Add(new(code, $"门店 {storeCode} 存在缺失、重复或额外的活动子项投影"));
            }
            foreach (var child in activeChildren)
            {
                var rows = current.StoreProjections.Where(x => !x.IsDeleted && x.StoreCode == storeCode && x.MultiCodeProductCode == child.SetProductCode).ToList();
                if (rows.Count != 1 || !rows[0].IsActive)
                {
                    report.Violations.Add(new(code, $"门店 {storeCode} 子项 {child.SetProductCode} 投影不唯一或非活动"));
                }
            }
            VerifyStoreAllocation(code, storeCode, price, activeChildren, current.StoreProjections, report);
        }
        VerifyGlobalAllocation(code, current, report);
        VerifyPreservedFields(before, current, report);
    }

    private static void VerifyGlobalAllocation(string code, ProductSetCodeTypeRepairTarget current, ProductSetCodeTypeRepairVerificationReport report)
    {
        var children = current.Children.Where(x => !x.IsDeleted && x.IsActive && x.SetType == 1).ToList();
        var parentCost = ResolveFallbackCost(current);
        var expected = SetChildPurchasePriceAllocator.AllocateByRetailRatio(children, parentCost, x => x.SetProductCode, x => x.SetRetailPrice);
        if (children.Any(x => !expected.TryGetValue(x.SetProductCode.Trim(), out var value) || x.SetPurchasePrice != value) || children.Sum(x => x.SetPurchasePrice ?? 0m) != Math.Round(parentCost, 2, MidpointRounding.AwayFromZero))
        {
            report.Violations.Add(new(code, "总部 Type1 子项成本分摊或合计不匹配"));
        }
    }

    private static void VerifyStoreAllocation(string code, string storeCode, StoreRetailPrice price, IReadOnlyCollection<ProductSetCode> activeChildren, IReadOnlyCollection<StoreMultiCodeProduct> projections, ProductSetCodeTypeRepairVerificationReport report)
    {
        var rows = projections.Where(x => !x.IsDeleted && x.IsActive && x.StoreCode == storeCode && x.MultiCodeProductCode != null).ToList();
        var childMap = activeChildren.ToDictionary(x => x.SetProductCode, StringComparer.OrdinalIgnoreCase);
        var items = rows.Where(x => childMap.ContainsKey(x.MultiCodeProductCode!)).Select(x => new
        {
            Row = x,
            Retail = x.MultiCodeRetailPrice is > 0m ? x.MultiCodeRetailPrice : childMap[x.MultiCodeProductCode!].SetRetailPrice,
        }).ToList();
        var expected = SetChildPurchasePriceAllocator.AllocateByRetailRatio(items, price.PurchasePrice, x => x.Row.MultiCodeProductCode, x => x.Retail);
        if (items.Count != activeChildren.Count || items.Any(x => !expected.TryGetValue(x.Row.MultiCodeProductCode!, out var value) || x.Row.PurchasePrice != value) || items.Sum(x => x.Row.PurchasePrice ?? 0m) != Math.Round(price.PurchasePrice!.Value, 2, MidpointRounding.AwayFromZero))
        {
            report.Violations.Add(new(code, $"门店 {storeCode} Type1 子项成本分摊或合计不匹配"));
        }
    }

    private static void VerifyPreservedFields(ProductSetCodeTypeRepairTarget before, ProductSetCodeTypeRepairTarget current, ProductSetCodeTypeRepairVerificationReport report)
    {
        var code = before.Product.ProductCode!;
        if (!SnapshotsEqual(
            new { before.Product, before.WarehouseProduct, before.DomesticProduct },
            new { current.Product, current.WarehouseProduct, current.DomesticProduct }
        ))
        {
            report.Violations.Add(new(code, "Product、WarehouseProduct 或 DomesticProduct 被修改"));
        }

        var currentChildren = current.Children.ToDictionary(x => x.SetCodeId, StringComparer.Ordinal);
        foreach (var original in before.Children)
        {
            if (!currentChildren.TryGetValue(original.SetCodeId, out var live))
            {
                report.Violations.Add(new(code, $"既有 ProductSetCode 缺失: {original.SetCodeId}"));
                continue;
            }
            if (original.IsDeleted)
            {
                if (!SnapshotsEqual(original, live))
                {
                    report.Violations.Add(new(code, $"软删除 ProductSetCode 被修改: {original.SetCodeId}"));
                }
                continue;
            }
            if (
                live.ProductCode != original.ProductCode
                || live.SetProductCode != original.SetProductCode
                || live.SetItemNumber != original.SetItemNumber
                || live.SetBarcode != original.SetBarcode
                || live.SetRetailPrice != original.SetRetailPrice
                || live.SetQuantity != original.SetQuantity
                || live.IsActive != original.IsActive
                || live.IsDeleted != original.IsDeleted
                || live.CreatedAt != original.CreatedAt
                || live.CreatedBy != original.CreatedBy
                || (!original.IsActive && live.SetPurchasePrice != original.SetPurchasePrice)
            )
            {
                report.Violations.Add(new(code, $"既有 ProductSetCode 非目标字段被覆盖: {original.SetCodeId}"));
            }
        }

        var currentPrices = current.StoreRetailPrices.ToDictionary(x => x.UUID, StringComparer.Ordinal);
        foreach (var original in before.StoreRetailPrices)
        {
            if (!currentPrices.TryGetValue(original.UUID, out var live))
            {
                report.Violations.Add(new(code, $"既有门店主价格缺失: {original.UUID}"));
                continue;
            }
            var isInRepairScope =
                !original.IsDeleted
                && before.ActiveStoreCodes.Contains(original.StoreCode ?? string.Empty, StringComparer.Ordinal);
            if (!isInRepairScope)
            {
                if (!SnapshotsEqual(original, live))
                {
                    report.Violations.Add(new(code, $"范围外或软删除门店主价格被修改: {original.UUID}"));
                }
                continue;
            }
            if (
                live.StoreCode != original.StoreCode
                || live.ProductCode != original.ProductCode
                || live.StoreProductCode != original.StoreProductCode
                || live.SupplierCode != original.SupplierCode
                || live.StoreRetailPriceValue != original.StoreRetailPriceValue
                || live.DiscountRate != original.DiscountRate
                || live.IsActive != original.IsActive
                || live.IsAutoPricing != original.IsAutoPricing
                || live.IsSpecialProduct != original.IsSpecialProduct
                || live.IsDeleted != original.IsDeleted
                || live.CreatedAt != original.CreatedAt
                || live.CreatedBy != original.CreatedBy
                || (original.PurchasePrice is > 0m && live.PurchasePrice != original.PurchasePrice)
            )
            {
                report.Violations.Add(new(code, $"既有门店主价格非目标字段被覆盖: {original.UUID}"));
            }
        }
        var currentProjections = current.StoreProjections.ToDictionary(x => x.UUID, StringComparer.Ordinal);
        var activeChildCodes = before.Children
            .Where(x => !x.IsDeleted && x.IsActive)
            .Select(x => x.SetProductCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var original in before.StoreProjections)
        {
            if (!currentProjections.TryGetValue(original.UUID, out var live))
            {
                report.Violations.Add(new(code, $"既有门店投影缺失: {original.UUID}"));
                continue;
            }
            var isInRepairScope =
                !original.IsDeleted
                && before.ActiveStoreCodes.Contains(original.StoreCode ?? string.Empty, StringComparer.Ordinal)
                && !string.IsNullOrWhiteSpace(original.MultiCodeProductCode)
                && activeChildCodes.Contains(original.MultiCodeProductCode);
            if (!isInRepairScope)
            {
                if (!SnapshotsEqual(original, live))
                {
                    report.Violations.Add(new(code, $"范围外或软删除门店投影被修改: {original.UUID}"));
                }
                continue;
            }
            if (
                live.StoreCode != original.StoreCode
                || live.ProductCode != original.ProductCode
                || live.MultiCodeProductCode != original.MultiCodeProductCode
                || live.DiscountRate != original.DiscountRate
                || live.IsAutoPricing != original.IsAutoPricing
                || live.IsSpecialProduct != original.IsSpecialProduct
                || live.IsDeleted != original.IsDeleted
                || live.CreatedAt != original.CreatedAt
                || live.CreatedBy != original.CreatedBy
            )
            {
                report.Violations.Add(new(code, $"既有门店投影非派生字段被覆盖: {original.UUID}"));
            }
        }
    }

    private static bool SnapshotsEqual<T>(T left, T right) =>
        string.Equals(SerializeCanonical(left), SerializeCanonical(right), StringComparison.Ordinal);

    public static string BuildInactiveBusinessFingerprint(ProductSetCodeTypeRepairTarget target) => ComputeSha256(SerializeCanonical(new
    {
        target.Product.UUID, target.Product.ProductCode, target.Product.ProductType, target.Product.PurchasePrice, target.Product.RetailPrice, target.Product.IsActive, target.Product.IsDeleted,
        warehouse = target.WarehouseProduct,
        domestic = target.DomesticProduct,
        children = target.Children.OrderBy(x => x.SetCodeId, StringComparer.Ordinal).Select(x => new { x.SetCodeId, x.ProductCode, x.SetProductCode, x.SetItemNumber, x.SetBarcode, x.SetPurchasePrice, x.SetRetailPrice, x.SetQuantity, x.IsActive, x.IsDeleted }),
        prices = target.StoreRetailPrices.OrderBy(x => x.UUID, StringComparer.Ordinal).Select(x => new { x.UUID, x.StoreCode, x.ProductCode, x.StoreProductCode, x.SupplierCode, x.PurchasePrice, x.StoreRetailPriceValue, x.DiscountRate, x.IsActive, x.IsAutoPricing, x.IsSpecialProduct, x.IsDeleted }),
        projections = target.StoreProjections.OrderBy(x => x.UUID, StringComparer.Ordinal).Select(x => new { x.UUID, x.StoreCode, x.ProductCode, x.MultiCodeProductCode, x.StoreMultiCodeProductCode, x.MultiBarcode, x.PurchasePrice, x.MultiCodeRetailPrice, x.DiscountRate, x.IsActive, x.IsAutoPricing, x.IsSpecialProduct, x.IsDeleted }),
    }));

    private async Task<ProductSetCodeTypeRepairSnapshot> ScanAsync(
        string runId,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var activeStoreCodes = await _db.Queryable<Store>()
            .Where(x => x.IsActive && !x.IsDeleted && x.StoreCode != null)
            .Select(x => x.StoreCode!)
            .ToListAsync();
        var products = await _db.Queryable<Product>()
            .Where(x => !x.IsDeleted && x.ProductCode != null)
            .ToListAsync();
        // ProductType=0/1 主档总量可能超过 SQL Server 2100 参数上限；先读取子项再在内存关联。
        var children = await _db.Queryable<ProductSetCode>().ToListAsync();
        var candidates = products
            .Where(x => children.Any(c =>
                !c.IsDeleted
                && c.ProductCode == x.ProductCode
                && (x.ProductType != 1 && x.ProductType != 2 || c.SetType != x.ProductType)
            ))
            .OrderBy(x => x.ProductCode, StringComparer.Ordinal)
            .ToList();
        var candidateCodes = candidates.Select(x => x.ProductCode!).ToList();
        var warehouse = candidateCodes.Count == 0 ? new List<WarehouseProduct>() : await _db.Queryable<WarehouseProduct>()
            .Where(x => candidateCodes.Contains(x.ProductCode) && !x.IsDeleted)
            .ToListAsync();
        var domestic = candidateCodes.Count == 0 ? new List<DomesticProduct>() : await _db.Queryable<DomesticProduct>()
            .Where(x => candidateCodes.Contains(x.ProductCode))
            .ToListAsync();
        // 软删除行不参与合格判定或写入，但必须进入快照，回滚时才有完整的前态证据。
        var prices = candidateCodes.Count == 0 ? new List<StoreRetailPrice>() : await _db.Queryable<StoreRetailPrice>()
            .Where(x => candidateCodes.Contains(x.ProductCode!))
            .ToListAsync();
        var projections = candidateCodes.Count == 0 ? new List<StoreMultiCodeProduct>() : await _db.Queryable<StoreMultiCodeProduct>()
            .Where(x => candidateCodes.Contains(x.ProductCode!))
            .ToListAsync();

        var snapshot = new ProductSetCodeTypeRepairSnapshot
        {
            RunId = runId,
            CapturedAtUtc = DateTime.UtcNow,
            ActiveStoreCodes = activeStoreCodes.OrderBy(x => x, StringComparer.Ordinal).ToList(),
        };
        foreach (var product in candidates)
        {
            var target = new ProductSetCodeTypeRepairTarget
            {
                Product = product,
                WarehouseProduct = warehouse.SingleOrDefault(x => x.ProductCode == product.ProductCode),
                DomesticProduct = domestic.SingleOrDefault(x => x.ProductCode == product.ProductCode),
                Children = children.Where(x => x.ProductCode == product.ProductCode).OrderBy(x => x.SetCodeId, StringComparer.Ordinal).ToList(),
                StoreRetailPrices = prices.Where(x => x.ProductCode == product.ProductCode).OrderBy(x => x.UUID, StringComparer.Ordinal).ToList(),
                StoreProjections = projections.Where(x => x.ProductCode == product.ProductCode).OrderBy(x => x.UUID, StringComparer.Ordinal).ToList(),
                ActiveStoreCodes = snapshot.ActiveStoreCodes,
            };
            target.BeforeFingerprint = BuildFingerprint(target);
            var reason = GetIsolationReason(target);
            if (reason != null)
            {
                snapshot.Isolated.Add(new ProductSetCodeTypeRepairIsolation
                {
                    ProductCode = product.ProductCode!,
                    ProductType = product.ProductType,
                    Reason = reason,
                    ChildCount = target.Children.Count(x => !x.IsDeleted),
                    Product = target.Product,
                    WarehouseProduct = target.WarehouseProduct,
                    DomesticProduct = target.DomesticProduct,
                    Children = target.Children,
                    StoreRetailPrices = target.StoreRetailPrices,
                    StoreProjections = target.StoreProjections,
                    BeforeFingerprint = target.BeforeFingerprint,
                });
            }
            else
            {
                snapshot.Eligible.Add(target);
            }
        }
        snapshot.Baseline = CalculateBaseline(snapshot, candidates.Count);
        return snapshot;
    }

    private static ProductSetCodeTypeRepairBaseline CalculateBaseline(
        ProductSetCodeTypeRepairSnapshot snapshot,
        int mismatchParentCount
    )
    {
        var eligibleActive = snapshot.Eligible.Where(x => x.Product.IsActive).ToList();
        var eligibleInactive = snapshot.Eligible.Where(x => !x.Product.IsActive).ToList();
        return new ProductSetCodeTypeRepairBaseline(
            mismatchParentCount,
            snapshot.Isolated.Count(x => x.ProductType == 1) + snapshot.Eligible.Count,
            snapshot.Isolated.Count(x => x.ProductType == 0),
            snapshot.Isolated.Count(x => x.ProductType == 1),
            snapshot.Eligible.Count,
            eligibleActive.Count,
            eligibleInactive.Count,
            snapshot.Eligible.Sum(x => x.Children.Count(c => !c.IsDeleted && c.SetType != 1)),
            eligibleActive.Sum(CountMissingStoreRetailPrices),
            eligibleActive.Sum(CountZeroStoreRetailPurchasePrices),
            eligibleActive.Sum(CountMissingStoreProjections),
            snapshot.ActiveStoreCodes.Count
        );
    }

    private static int CountMissingStoreRetailPrices(ProductSetCodeTypeRepairTarget target) => target.ActiveStoreCodes.Count(store =>
        !target.StoreRetailPrices.Any(x => x.StoreCode == store && !x.IsDeleted));

    private static int CountZeroStoreRetailPurchasePrices(ProductSetCodeTypeRepairTarget target) => target.StoreRetailPrices.Count(x =>
        !x.IsDeleted && target.ActiveStoreCodes.Contains(x.StoreCode ?? string.Empty, StringComparer.Ordinal)
        && (x.PurchasePrice is null or <= 0));

    private static int CountMissingStoreProjections(ProductSetCodeTypeRepairTarget target) => target.ActiveStoreCodes.Sum(store => target.Children.Count(child => child.IsActive &&
        !child.IsDeleted && !target.StoreProjections.Any(x => x.StoreCode == store && x.MultiCodeProductCode == child.SetProductCode && !x.IsDeleted)));

    private static string? GetIsolationReason(ProductSetCodeTypeRepairTarget target)
    {
        var children = target.Children.Where(x => !x.IsDeleted).ToList();
        if (target.Product.ProductType != 1)
        {
            // ProductType=0 不能写入非法 SetType=0，也不能根据错误子项类型反推父类型。
            return "父商品 ProductType 非法或无法唯一判定为套装，保持整组不变";
        }
        var reasons = new List<string>();
        if (
            children.Count == 0
            || children.Any(x =>
                string.IsNullOrWhiteSpace(x.SetProductCode)
                || string.IsNullOrWhiteSpace(x.SetBarcode)
            )
        )
        {
            reasons.Add("子项缺少业务键或条码");
        }
        if (
            children.All(x => !string.IsNullOrWhiteSpace(x.SetBarcode))
            && children.GroupBy(
                x => x.SetBarcode!.Trim(),
                StringComparer.OrdinalIgnoreCase
            ).Any(x => x.Count() > 1)
        )
        {
            reasons.Add("存在重复子项条码");
        }
        if (
            children.All(x => !string.IsNullOrWhiteSpace(x.SetProductCode))
            && children.GroupBy(
                x => x.SetProductCode.Trim(),
                StringComparer.OrdinalIgnoreCase
            ).Any(x => x.Count() > 1)
        )
        {
            reasons.Add("存在重复子项业务键");
        }
        if (children.Any(x => x.SetType is < 1 or > 2))
        {
            reasons.Add("存在 SetType=3 或非法子项类型");
        }
        if (children.Any(x => x.SetRetailPrice is null or <= 0))
        {
            reasons.Add("子项零售价为空或非正数");
        }
        if (
            (target.Product.PurchasePrice ?? 0m) <= 0m
            && (target.WarehouseProduct?.ImportPrice ?? 0m) <= 0m
        )
        {
            reasons.Add("总部主商品成本为空或非正数");
        }
        if (target.Product.IsActive)
        {
            if (
                target.StoreRetailPrices
                    .Where(x =>
                        !x.IsDeleted
                        && target.ActiveStoreCodes.Contains(
                            x.StoreCode ?? string.Empty,
                            StringComparer.Ordinal
                        )
                    )
                    .GroupBy(x => x.StoreCode, StringComparer.Ordinal)
                    .Any(x => x.Count() > 1)
            )
            {
                reasons.Add("活动分店存在重复主价格行");
            }
            if (
                target.StoreRetailPrices.Any(x =>
                    !x.IsDeleted
                    && target.ActiveStoreCodes.Contains(
                        x.StoreCode ?? string.Empty,
                        StringComparer.Ordinal
                    )
                    && !x.IsActive
                )
            )
            {
                reasons.Add("活动分店主价格行处于非活动状态");
            }
            if (
                target.StoreProjections
                    .Where(x =>
                        !x.IsDeleted
                        && target.ActiveStoreCodes.Contains(
                            x.StoreCode ?? string.Empty,
                            StringComparer.Ordinal
                        )
                    )
                    .GroupBy(
                        x => $"{x.StoreCode}\u0001{x.MultiCodeProductCode}",
                        StringComparer.Ordinal
                    )
                    .Any(x => x.Count() > 1)
            )
            {
                reasons.Add("活动分店存在重复子项投影");
            }
        }
        return reasons.Count == 0 ? null : string.Join("；", reasons);
    }

    private async Task<ProductSetCodeTypeRepairAppliedProduct> ApplyOneAsync(
        ProductSetCodeTypeRepairTarget before,
        string actorName,
        string runId,
        CancellationToken cancellationToken
    )
    {
        var productCode = before.Product.ProductCode!;
        await _db.Ado.BeginTranAsync();
        try
        {
            using var auditScope = SqlSugarAuditScope.PreserveExplicitAuditFields();
            var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(_db, new[] { productCode });
            var activeStoreCodes = await _db.Queryable<Store>()
                .Where(x => x.IsActive && !x.IsDeleted && x.StoreCode != null)
                .Select(x => x.StoreCode!)
                .ToListAsync();
            if (!activeStoreCodes.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(before.ActiveStoreCodes.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal))
            {
                throw new InvalidOperationException("活动分店清单已变化");
            }
            var current = await ReadTargetAsync(productCode, before.ActiveStoreCodes);
            if (!string.Equals(BuildFingerprint(current), before.BeforeFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("执行前商品快照已变化");
            }
            if (GetIsolationReason(current) != null)
            {
                throw new InvalidOperationException("执行前商品结构已不再合格");
            }

            var now = DateTime.UtcNow;
            var insertedPriceIds = new List<string>();
            var insertedProjectionIds = new List<string>();
            if (current.Product.IsActive)
            {
                var fallbackCost = ResolveFallbackCost(current);
                var priceInserts = new List<StoreRetailPrice>();
                var priceUpdates = new List<StoreRetailPrice>();
                foreach (var storeCode in current.ActiveStoreCodes)
                {
                    var existing = current.StoreRetailPrices.SingleOrDefault(x => !x.IsDeleted && x.StoreCode == storeCode);
                    if (existing == null)
                    {
                        var created = BuildStoreRetailPrice(current.Product, storeCode, fallbackCost, actorName, now);
                        priceInserts.Add(created);
                        insertedPriceIds.Add(created.UUID);
                    }
                    else if ((existing.PurchasePrice ?? 0m) <= 0m)
                    {
                        existing.PurchasePrice = fallbackCost;
                        existing.UpdatedAt = now;
                        existing.UpdatedBy = actorName;
                        priceUpdates.Add(existing);
                    }
                }
                if (priceInserts.Count > 0)
                {
                    await _db.Insertable(priceInserts).ExecuteCommandAsync();
                }
                if (priceUpdates.Count > 0)
                {
                    await _db.Updateable(priceUpdates).UpdateColumns(x => new { x.PurchasePrice, x.UpdatedAt, x.UpdatedBy }).ExecuteCommandAsync();
                }
            }

            var typeUpdates = current.Children.Where(x => !x.IsDeleted && x.SetType != 1).ToList();
            foreach (var child in typeUpdates)
            {
                child.SetType = 1;
                child.UpdatedAt = now;
                child.UpdatedBy = actorName;
            }
            if (typeUpdates.Count > 0)
            {
                await _db.Updateable(typeUpdates)
                    .UpdateColumns(x => new { x.SetType, x.UpdatedAt, x.UpdatedBy })
                    .ExecuteCommandAsync();
            }

            if (current.Product.IsActive)
            {
                var refreshedPrices = await _db.Queryable<StoreRetailPrice>()
                    .Where(x => !x.IsDeleted && x.ProductCode == productCode && x.StoreCode != null && current.ActiveStoreCodes.Contains(x.StoreCode))
                    .ToListAsync();
                var projectionInserts = new List<StoreMultiCodeProduct>();
                var projectionUpdates = new List<StoreMultiCodeProduct>();
                foreach (var storeCode in current.ActiveStoreCodes)
                {
                    var mainPrice = refreshedPrices.Single(x => x.StoreCode == storeCode);
                    foreach (var child in current.Children.Where(x => !x.IsDeleted && x.IsActive))
                    {
                        var existing = current.StoreProjections.SingleOrDefault(x => !x.IsDeleted && x.StoreCode == storeCode && x.MultiCodeProductCode == child.SetProductCode);
                        if (existing == null)
                        {
                            var created = BuildProjection(child, mainPrice, storeCode, actorName, now);
                            projectionInserts.Add(created);
                            insertedProjectionIds.Add(created.UUID);
                        }
                        else
                        {
                            // 只更新由总部套装定义的字段；门店自己的折扣、自动定价和特殊商品标记必须保留。
                            existing.StoreMultiCodeProductCode = storeCode + child.SetProductCode;
                            existing.MultiBarcode = child.SetBarcode;
                            existing.MultiCodeRetailPrice = child.SetRetailPrice;
                            existing.IsActive = child.IsActive;
                            existing.UpdatedAt = now;
                            existing.UpdatedBy = actorName;
                            projectionUpdates.Add(existing);
                        }
                    }
                }
                if (projectionInserts.Count > 0)
                {
                    await _db.Insertable(projectionInserts).ExecuteCommandAsync();
                }
                if (projectionUpdates.Count > 0)
                {
                    await _db.Updateable(projectionUpdates)
                        .UpdateColumns(x => new { x.StoreMultiCodeProductCode, x.MultiBarcode, x.MultiCodeRetailPrice, x.IsActive, x.UpdatedAt, x.UpdatedBy })
                        .ExecuteCommandAsync();
                }
                var result = await new SetChildPurchasePriceService(_db).RecalculateLockedAsync(
                    lockScope,
                    new[] { productCode },
                    current.ActiveStoreCodes,
                    actorName
                );
                if (result.ProductSetCode.SkippedGroupCount > 0 || result.StoreMultiCodeProduct.SkippedGroupCount > 0)
                {
                    throw new InvalidOperationException(result.Errors.FirstOrDefault()?.Reason ?? "套装成本重算不完整");
                }
            }

            var after = await ReadTargetAsync(productCode, before.ActiveStoreCodes);
            var afterFingerprint = BuildFingerprint(after);
            // 审计与商品写入使用同一事务，作为 journal 落盘窗口中的可恢复证据。
            await WriteAuditAsync(productCode, before.BeforeFingerprint, afterFingerprint, actorName, "ProductSetCodeTypeRepair", runId, insertedPriceIds, insertedProjectionIds);
            await _db.Ado.CommitTranAsync();
            return new ProductSetCodeTypeRepairAppliedProduct
            {
                ProductCode = productCode,
                BeforeFingerprint = before.BeforeFingerprint,
                AfterFingerprint = afterFingerprint,
                InsertedStoreRetailPriceIds = insertedPriceIds,
                InsertedStoreProjectionIds = insertedProjectionIds,
            };
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();
            throw;
        }
    }

    private async Task<ProductSetCodeTypeRepairTarget> ReadTargetAsync(string productCode, IReadOnlyCollection<string> activeStores)
    {
        var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == productCode && !x.IsDeleted);
        var children = await _db.Queryable<ProductSetCode>().Where(x => x.ProductCode == productCode).OrderBy(x => x.SetCodeId).ToListAsync();
        var prices = await _db.Queryable<StoreRetailPrice>().Where(x => x.ProductCode == productCode).OrderBy(x => x.UUID).ToListAsync();
        var projections = await _db.Queryable<StoreMultiCodeProduct>().Where(x => x.ProductCode == productCode).OrderBy(x => x.UUID).ToListAsync();
        return new ProductSetCodeTypeRepairTarget
        {
            Product = product,
            WarehouseProduct = await _db.Queryable<WarehouseProduct>().FirstAsync(x => x.ProductCode == productCode && !x.IsDeleted),
            DomesticProduct = await _db.Queryable<DomesticProduct>().FirstAsync(x => x.ProductCode == productCode),
            Children = children,
            StoreRetailPrices = prices,
            StoreProjections = projections,
            ActiveStoreCodes = activeStores.OrderBy(x => x, StringComparer.Ordinal).ToList(),
        };
    }

    private static decimal ResolveFallbackCost(ProductSetCodeTypeRepairTarget target) => target.Product.PurchasePrice is > 0m
        ? target.Product.PurchasePrice.Value
        : target.WarehouseProduct?.ImportPrice is > 0m
            ? target.WarehouseProduct.ImportPrice.Value
            : throw new InvalidOperationException("没有可用的总部成本回退值");

    private static StoreRetailPrice BuildStoreRetailPrice(Product product, string storeCode, decimal purchasePrice, string actor, DateTime now) => new()
    {
        UUID = UuidHelper.GenerateUuid7(), StoreCode = storeCode, ProductCode = product.ProductCode, StoreProductCode = product.ProductCode,
        SupplierCode = product.LocalSupplierCode, PurchasePrice = purchasePrice, StoreRetailPriceValue = product.RetailPrice,
        DiscountRate = 0m, IsActive = product.IsActive, IsAutoPricing = product.IsAutoPricing, IsSpecialProduct = product.IsSpecialProduct,
        IsDeleted = false, CreatedAt = now, UpdatedAt = now, CreatedBy = actor, UpdatedBy = actor,
    };

    private static StoreMultiCodeProduct BuildProjection(ProductSetCode child, StoreRetailPrice main, string storeCode, string actor, DateTime now) => new()
    {
        UUID = UuidHelper.GenerateUuid7(), StoreCode = storeCode, ProductCode = child.ProductCode, MultiCodeProductCode = child.SetProductCode,
        StoreMultiCodeProductCode = storeCode + child.SetProductCode, MultiBarcode = child.SetBarcode, PurchasePrice = null,
        MultiCodeRetailPrice = child.SetRetailPrice, DiscountRate = 0m, IsAutoPricing = false, IsSpecialProduct = false,
        IsActive = child.IsActive, IsDeleted = false, CreatedAt = now, UpdatedAt = now, CreatedBy = actor, UpdatedBy = actor,
    };

    private async Task RestoreRowsAsync(ProductSetCodeTypeRepairTarget before, ProductSetCodeTypeRepairAppliedProduct applied, string actor)
    {
        var now = DateTime.UtcNow;
        using var auditScope = SqlSugarAuditScope.PreserveExplicitAuditFields();
        await _db.Updateable(before.Children).ExecuteCommandAsync();
        await _db.Updateable(before.StoreRetailPrices).ExecuteCommandAsync();
        await _db.Updateable(before.StoreProjections).ExecuteCommandAsync();
        if (applied.InsertedStoreRetailPriceIds.Count > 0)
        {
            await _db.Updateable<StoreRetailPrice>().SetColumns(x => new StoreRetailPrice { IsDeleted = true, IsActive = false, UpdatedAt = now, UpdatedBy = actor })
                .Where(x => applied.InsertedStoreRetailPriceIds.Contains(x.UUID)).ExecuteCommandAsync();
        }
        if (applied.InsertedStoreProjectionIds.Count > 0)
        {
            await _db.Updateable<StoreMultiCodeProduct>().SetColumns(x => new StoreMultiCodeProduct { IsDeleted = true, IsActive = false, UpdatedAt = now, UpdatedBy = actor })
                .Where(x => applied.InsertedStoreProjectionIds.Contains(x.UUID)).ExecuteCommandAsync();
        }
    }

    private static ProductSetCodeTypeRepairTarget ExcludeInsertedRows(
        ProductSetCodeTypeRepairTarget restored,
        ProductSetCodeTypeRepairAppliedProduct applied
    )
    {
        var insertedPriceIds = applied.InsertedStoreRetailPriceIds.ToHashSet(StringComparer.Ordinal);
        var insertedProjectionIds = applied.InsertedStoreProjectionIds.ToHashSet(StringComparer.Ordinal);
        return new ProductSetCodeTypeRepairTarget
        {
            Product = restored.Product,
            WarehouseProduct = restored.WarehouseProduct,
            DomesticProduct = restored.DomesticProduct,
            Children = restored.Children,
            StoreRetailPrices = restored.StoreRetailPrices
                .Where(x => !insertedPriceIds.Contains(x.UUID))
                .ToList(),
            StoreProjections = restored.StoreProjections
                .Where(x => !insertedProjectionIds.Contains(x.UUID))
                .ToList(),
            ActiveStoreCodes = restored.ActiveStoreCodes,
        };
    }

    private static void EnsureInsertedRowsAreSoftDeleted(
        ProductSetCodeTypeRepairTarget restored,
        ProductSetCodeTypeRepairAppliedProduct applied
    )
    {
        var insertedPrices = restored.StoreRetailPrices
            .Where(x => applied.InsertedStoreRetailPriceIds.Contains(x.UUID, StringComparer.Ordinal))
            .ToList();
        var insertedProjections = restored.StoreProjections
            .Where(x => applied.InsertedStoreProjectionIds.Contains(x.UUID, StringComparer.Ordinal))
            .ToList();
        if (
            insertedPrices.Count != applied.InsertedStoreRetailPriceIds.Count
            || insertedPrices.Any(x => !x.IsDeleted || x.IsActive)
            || insertedProjections.Count != applied.InsertedStoreProjectionIds.Count
            || insertedProjections.Any(x => !x.IsDeleted || x.IsActive)
        )
        {
            throw new InvalidOperationException("回滚新增行未完整软删除，事务已回滚");
        }
    }

    /// <summary>
    /// 回滚后的新增行按计划保留为软删除墓碑；比较原始前态时排除这些已核验墓碑。
    /// </summary>
    public static string BuildRollbackComparableFingerprint(
        ProductSetCodeTypeRepairTarget restored,
        ProductSetCodeTypeRepairAppliedProduct applied
    )
    {
        EnsureInsertedRowsAreSoftDeleted(restored, applied);
        return BuildFingerprint(ExcludeInsertedRows(restored, applied));
    }

    private Task WriteAuditAsync(
        string productCode,
        string before,
        string after,
        string actor,
        string source,
        string runId,
        IReadOnlyCollection<string> insertedPriceIds,
        IReadOnlyCollection<string> insertedProjectionIds
    ) => _db.Insertable(new WarehouseProductChangeHistory
    {
        ProductCode = productCode, Action = "Update", Source = source, SourceReference = $"{runId}:{productCode}",
        ActorName = string.IsNullOrWhiteSpace(actor) ? "System" : actor, ActorType = "System", OccurredAtUtc = DateTime.UtcNow,
        ChangesJson = JsonSerializer.Serialize(new
        {
            runId,
            fieldKey = "productSetCodeTypeRepair",
            beforeFingerprint = before,
            afterFingerprint = after,
            insertedStoreRetailPriceIds = insertedPriceIds.OrderBy(x => x, StringComparer.Ordinal),
            insertedStoreProjectionIds = insertedProjectionIds.OrderBy(x => x, StringComparer.Ordinal),
        }),
    }).ExecuteCommandAsync();

    /// <summary>
    /// 指纹覆盖完整快照（包括审计字段和软删除行），确保执行或回滚不会覆盖并发修改。
    /// </summary>
    public static string BuildFingerprint(ProductSetCodeTypeRepairTarget target) => ComputeSha256(SerializeCanonical(new
    {
        product = target.Product,
        warehouseProduct = target.WarehouseProduct,
        domesticProduct = target.DomesticProduct,
        children = target.Children.OrderBy(x => x.SetCodeId, StringComparer.Ordinal),
        storeRetailPrices = target.StoreRetailPrices.OrderBy(x => x.UUID, StringComparer.Ordinal),
        storeProjections = target.StoreProjections.OrderBy(x => x.UUID, StringComparer.Ordinal),
        activeStores = target.ActiveStoreCodes.OrderBy(x => x, StringComparer.Ordinal),
    }));

    private static List<ProductSetCodeTypeRepairIsolation> BuildIsolationSummaries(
        IEnumerable<ProductSetCodeTypeRepairIsolation> isolated
    ) => isolated
        .OrderBy(x => x.ProductCode, StringComparer.Ordinal)
        .Select(x => new ProductSetCodeTypeRepairIsolation
        {
            ProductCode = x.ProductCode,
            ProductType = x.ProductType,
            Reason = x.Reason,
            ChildCount = x.ChildCount,
            BeforeFingerprint = x.BeforeFingerprint,
        })
        .ToList();

    public static void ValidateRunId(string runId)
    {
        if (
            string.IsNullOrWhiteSpace(runId)
            || runId.Length > 128
            || runId.Contains("..", StringComparison.Ordinal)
            || runId.Any(x => !(char.IsAsciiLetterOrDigit(x) || x is '.' or '_' or '-'))
        )
        {
            throw new ArgumentException(
                "运行编号只允许1-128位英文字母、数字、点、下划线和连字符，且不能包含连续点",
                nameof(runId)
            );
        }
    }

    private static string BuildOutputPath(string outputDirectory, string fileName)
    {
        var normalizedDirectory = Path.GetFullPath(outputDirectory);
        var path = Path.GetFullPath(Path.Combine(normalizedDirectory, fileName));
        var directoryPrefix = normalizedDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedDirectory
            : normalizedDirectory + Path.DirectorySeparatorChar;
        if (!path.StartsWith(directoryPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("修复材料路径越出输出目录");
        }
        return path;
    }

    private static string ComputePlanHash(ProductSetCodeTypeRepairSnapshot snapshot) => ComputeSha256(SerializeCanonical(new
    {
        snapshot.Baseline,
        activeStores = snapshot.ActiveStoreCodes.OrderBy(x => x, StringComparer.Ordinal),
        eligible = snapshot.Eligible.OrderBy(x => x.Product.ProductCode, StringComparer.Ordinal).Select(x => new { productCode = x.Product.ProductCode, x.BeforeFingerprint }),
        isolated = snapshot.Isolated.OrderBy(x => x.ProductCode, StringComparer.Ordinal),
    }));

    public static string ComputeSha256(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    public static string SerializeCanonical<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }
}

public sealed class ProductSetCodeTypeRepairOptions
{
    public string OutputDirectory { get; init; } = string.Empty;
    public string? RunId { get; init; }
    public bool Apply { get; init; }
    public string ActorName { get; init; } = "System";
    public ProductSetCodeTypeRepairBaseline? ExpectedBaseline { get; init; }
}

public sealed record ProductSetCodeTypeRepairBaseline(
    int MismatchParentCount, int TypeOneMismatchParentCount, int IsolatedNormalParentCount, int IsolatedTypeOneParentCount,
    int EligibleParentCount, int EligibleActiveParentCount, int EligibleInactiveParentCount, int ChildTypeUpdateCount,
    int MissingStoreRetailPriceCount, int ZeroStoreRetailPurchasePriceCount, int MissingStoreProjectionCount, int ActiveStoreCount)
{
    public IReadOnlyList<string> Diff(ProductSetCodeTypeRepairBaseline actual)
    {
        var expected = GetType().GetProperties().ToDictionary(x => x.Name, x => (int)x.GetValue(this)!);
        var found = actual.GetType().GetProperties().ToDictionary(x => x.Name, x => (int)x.GetValue(actual)!);
        return expected.Where(x => found[x.Key] != x.Value).Select(x => $"{x.Key}: expected {x.Value}, actual {found[x.Key]}").ToList();
    }
}

public sealed class ProductSetCodeTypeRepairBaselineMismatchException : InvalidOperationException
{
    public ProductSetCodeTypeRepairBaselineMismatchException(IReadOnlyList<string> differences)
        : base("修复基线不匹配：" + string.Join("；", differences)) => Differences = differences;
    public IReadOnlyList<string> Differences { get; }
}

public sealed class ProductSetCodeTypeRepairSnapshot
{
    public string RunId { get; init; } = string.Empty;
    public DateTime CapturedAtUtc { get; init; }
    public List<string> ActiveStoreCodes { get; init; } = new();
    public ProductSetCodeTypeRepairBaseline Baseline { get; set; } = ProductSetCodeTypeRepairRunner.ApprovedBaseline;
    public List<ProductSetCodeTypeRepairTarget> Eligible { get; init; } = new();
    public List<ProductSetCodeTypeRepairIsolation> Isolated { get; init; } = new();
}

public sealed class ProductSetCodeTypeRepairTarget
{
    public Product Product { get; init; } = new();
    public WarehouseProduct? WarehouseProduct { get; init; }
    public DomesticProduct? DomesticProduct { get; init; }
    public List<ProductSetCode> Children { get; init; } = new();
    public List<StoreRetailPrice> StoreRetailPrices { get; init; } = new();
    public List<StoreMultiCodeProduct> StoreProjections { get; init; } = new();
    public List<string> ActiveStoreCodes { get; init; } = new();
    public string BeforeFingerprint { get; set; } = string.Empty;
}

public sealed class ProductSetCodeTypeRepairIsolation
{
    public string ProductCode { get; init; } = string.Empty;
    public int? ProductType { get; init; }
    public string Reason { get; init; } = string.Empty;
    public int ChildCount { get; init; }
    public Product? Product { get; init; }
    public WarehouseProduct? WarehouseProduct { get; init; }
    public DomesticProduct? DomesticProduct { get; init; }
    public List<ProductSetCode> Children { get; init; } = new();
    public List<StoreRetailPrice> StoreRetailPrices { get; init; } = new();
    public List<StoreMultiCodeProduct> StoreProjections { get; init; } = new();
    public string BeforeFingerprint { get; init; } = string.Empty;
}

public sealed class ProductSetCodeTypeRepairManifest
{
    public string RunId { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public string SnapshotFileName { get; init; } = string.Empty;
    public string SnapshotSha256 { get; init; } = string.Empty;
    public ProductSetCodeTypeRepairBaseline Baseline { get; init; } = ProductSetCodeTypeRepairRunner.ApprovedBaseline;
    public List<string> EligibleProductCodes { get; init; } = new();
    public List<ProductSetCodeTypeRepairIsolation> IsolatedProducts { get; init; } = new();
    public bool DryRun { get; init; }
}

public sealed class ProductSetCodeTypeRepairRunReport
{
    public string RunId { get; init; } = string.Empty;
    public bool DryRun { get; init; }
    public string ManifestPath { get; init; } = string.Empty;
    public string SnapshotPath { get; init; } = string.Empty;
    public string? JournalPath { get; set; }
    public string SnapshotSha256 { get; init; } = string.Empty;
    public ProductSetCodeTypeRepairBaseline Baseline { get; init; } = ProductSetCodeTypeRepairRunner.ApprovedBaseline;
    public List<ProductSetCodeTypeRepairIsolation> IsolatedProducts { get; init; } = new();
    public List<ProductSetCodeTypeRepairAppliedProduct> Succeeded { get; init; } = new();
    public List<ProductSetCodeTypeRepairFailure> Failed { get; init; } = new();
    public string? VerificationPath { get; set; }
    public ProductSetCodeTypeRepairVerificationReport? Verification { get; set; }
}

public sealed class ProductSetCodeTypeRepairAppliedProduct
{
    public string ProductCode { get; init; } = string.Empty;
    public string BeforeFingerprint { get; init; } = string.Empty;
    public string AfterFingerprint { get; init; } = string.Empty;
    public List<string> InsertedStoreRetailPriceIds { get; init; } = new();
    public List<string> InsertedStoreProjectionIds { get; init; } = new();
}

public sealed class ProductSetCodeTypeRepairFailure
{
    public string ProductCode { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public bool IsBusinessLockConflict { get; init; }
}

/// <summary>只读验收的结构化结果；有任一 violation 即不可通过后续 HQ 操作。</summary>
public sealed class ProductSetCodeTypeRepairVerificationReport
{
    public string RunId { get; init; } = string.Empty;
    public string SnapshotSha256 { get; init; } = string.Empty;
    public int ExpectedEligibleCount { get; init; }
    public int ExpectedIsolatedCount { get; init; }
    public int VerifiedEligibleCount { get; set; }
    public int VerifiedIsolatedCount { get; set; }
    public bool IsValid { get; set; }
    public List<ProductSetCodeTypeRepairVerificationViolation> Violations { get; init; } = new();
}

public sealed record ProductSetCodeTypeRepairVerificationViolation(string Scope, string Reason);

public sealed class ProductSetCodeTypeRepairRollbackReport
{
    public string RunId { get; init; } = string.Empty;
    public List<string> RolledBackProductCodes { get; init; } = new();
    public List<ProductSetCodeTypeRepairFailure> Failures { get; init; } = new();
}

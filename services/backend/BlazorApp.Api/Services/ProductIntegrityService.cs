using System.Diagnostics;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.React;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 商品数据一致性检测与修复服务
    /// 负责检测和修复商品基础表(Product)与三张关联表之间的数据一致性问题：
    /// - StoreRetailPrice（分店零售价表）
    /// - StoreMultiCodeProduct（分店多码表）
    /// - ProductSetCode（套装编码表）
    /// </summary>
    public class ProductIntegrityService : IProductIntegrityService
    {
        private readonly SqlSugarContext _db;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ProductIntegrityService> _logger;

        /// <summary>
        /// SQL 命令超时时间（秒），用于大数据量操作
        /// </summary>
        private const int CommandTimeoutSeconds = 300;

        /// <summary>
        /// 检测报告返回的最大样本编码数量
        /// </summary>
        private const int MaxSampleCount = 100;

        public ProductIntegrityService(
            SqlSugarContext db,
            IConfiguration configuration,
            ILogger<ProductIntegrityService> logger
        )
        {
            _db = db;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// 判断当前数据库是否为 PostgreSQL
        /// </summary>
        private bool IsPostgreSQL => _db.Db.CurrentConnectionConfig.DbType == DbType.PostgreSQL;

        /// <summary>
        /// SQL 注入防护：转义单引号
        /// </summary>
        private static string EscapeSql(string value)
        {
            return value.Replace("'", "''");
        }

        private static void ApplyInvalidKeyReport(
            TableIntegrityReport report,
            int invalidKeyCount,
            string messageTemplate
        )
        {
            report.InvalidKeyCount = invalidKeyCount;
            if (invalidKeyCount <= 0)
            {
                return;
            }

            // 检查阶段也必须暴露脏关键字段；修复阶段仍保持只报告、不自动修复。
            report.Errors.Add(string.Format(messageTemplate, invalidKeyCount));
        }

        #region Check Methods

        public async Task<ApiResponse<ProductIntegrityCheckResultDto>> CheckIntegrityAsync(
            List<string>? storeCodes = null
        )
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                Console.WriteLine("[数据一致性检测] ===== 开始检测 =====");

                var stores = await GetActiveStoresAsync(storeCodes);
                Console.WriteLine($"[数据一致性检测] 活跃分店数量: {stores.Count}");

                var result = new ProductIntegrityCheckResultDto();

                result.ProductSetCodeReport = await CheckProductSetCodeAsync();

                var semaphore = new SemaphoreSlim(5);
                var storeTasks = stores.Select(async store =>
                {
                    await semaphore.WaitAsync();
                    ISqlSugarClient? storeDb = null;
                    try
                    {
                        storeDb = SqlSugarContext.CreateConcurrentConnection(_configuration);
                        storeDb.Ado.CommandTimeOut = CommandTimeoutSeconds;

                        var storeReport = new StoreIntegrityReport
                        {
                            StoreCode = store.StoreCode,
                            StoreName = store.StoreName ?? store.StoreCode,
                        };

                        var retailPriceReport = await CheckStoreRetailPriceForStoreAsync(
                            storeDb, store.StoreCode
                        );
                        storeReport.TableReports.Add(retailPriceReport);

                        var multiCodeReport = await CheckStoreMultiCodeProductForStoreAsync(
                            storeDb, store.StoreCode
                        );
                        storeReport.TableReports.Add(multiCodeReport);

                        Console.WriteLine(
                            $"  [分店 {store.StoreCode}] StoreRetailPrice - 孤立: {retailPriceReport.OrphanedCount}, 缺失: {retailPriceReport.MissingCount}"
                        );
                        Console.WriteLine(
                            $"  [分店 {store.StoreCode}] StoreMultiCodeProduct - 孤立: {multiCodeReport.OrphanedCount}, 缺失: {multiCodeReport.MissingCount}"
                        );

                        return storeReport;
                    }
                    finally
                    {
                        semaphore.Release();
                        storeDb?.Dispose();
                    }
                });

                var storeResults = await Task.WhenAll(storeTasks);
                result.StoreReports = storeResults.OrderBy(s => s.StoreCode).ToList();

                stopwatch.Stop();
                result.DurationSeconds = stopwatch.Elapsed.TotalSeconds;

                var totalOrphaned = result.StoreReports.Sum(s =>
                    s.TableReports.Sum(t => t.OrphanedCount));
                var totalMissing = result.StoreReports.Sum(s =>
                    s.TableReports.Sum(t => t.MissingCount));

                _logger.LogInformation(
                    "数据一致性检测完成，{StoreCount} 个分店，总孤立 {Orphaned}，总缺失 {Missing}，耗时 {Duration:F2}s",
                    result.StoreReports.Count, totalOrphaned, totalMissing, result.DurationSeconds
                );
                Console.WriteLine(
                    $"[数据一致性检测] ===== 检测完成，{result.StoreReports.Count} 个分店，总孤立 {totalOrphaned}，总缺失 {totalMissing}，耗时 {result.DurationSeconds:F2}s ====="
                );

                return ApiResponse<ProductIntegrityCheckResultDto>.OK(result, "数据一致性检测完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据一致性检测失败");
                Console.WriteLine($"[数据一致性检测] ❌ 检测失败: {ex.Message}");
                return ApiResponse<ProductIntegrityCheckResultDto>.Error(
                    "检测失败: " + ex.Message,
                    "DATABASE_ERROR"
                );
            }
        }

        private async Task<TableIntegrityReport> CheckStoreRetailPriceForStoreAsync(
            ISqlSugarClient db,
            string storeCode
        )
        {
            var report = new TableIntegrityReport { TableName = "StoreRetailPrice" };
            var escapedCode = EscapeSql(storeCode);

            var totalSql =
                $@"SELECT COUNT(DISTINCT t.ProductCode) FROM StoreRetailPrice t
                   WHERE t.IsDeleted = 0 AND NULLIF(LTRIM(RTRIM(t.ProductCode)), '') IS NOT NULL AND t.StoreCode = '{escapedCode}'";
            report.TotalChecked = await db.Ado.GetIntAsync(totalSql);

            var invalidKeySql =
                $@"SELECT COUNT(*) FROM StoreRetailPrice t
                   WHERE t.IsDeleted = 0 AND t.StoreCode = '{escapedCode}'
                   AND NULLIF(LTRIM(RTRIM(t.ProductCode)), '') IS NULL";
            ApplyInvalidKeyReport(
                report,
                await db.Ado.GetIntAsync(invalidKeySql),
                $"分店 {storeCode} StoreRetailPrice 存在 {{0}} 条缺少 ProductCode 的记录，未参与自动修复。"
            );

            var orphanedCountSql =
                $@"SELECT COUNT(DISTINCT t.ProductCode) FROM StoreRetailPrice t
                   WHERE t.IsDeleted = 0 AND NULLIF(LTRIM(RTRIM(t.ProductCode)), '') IS NOT NULL AND t.StoreCode = '{escapedCode}'
                   AND NOT EXISTS (SELECT 1 FROM Product p WHERE p.ProductCode = t.ProductCode AND p.IsDeleted = 0)";
            report.OrphanedCount = await db.Ado.GetIntAsync(orphanedCountSql);

            var missingCountSql =
                $@"SELECT COUNT(*) FROM Product p
                   WHERE p.IsDeleted = 0 AND p.ProductCode IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM StoreRetailPrice t
                       WHERE t.ProductCode = p.ProductCode AND t.IsDeleted = 0 AND t.StoreCode = '{escapedCode}'
                   )";
            report.MissingCount = await db.Ado.GetIntAsync(missingCountSql);

            if (report.OrphanedCount > 0)
            {
                var orphanedSampleSql = IsPostgreSQL
                    ? $@"SELECT DISTINCT t.ProductCode FROM StoreRetailPrice t
                        WHERE t.IsDeleted = 0 AND NULLIF(LTRIM(RTRIM(t.ProductCode)), '') IS NOT NULL AND t.StoreCode = '{escapedCode}'
                        AND NOT EXISTS (SELECT 1 FROM Product p WHERE p.ProductCode = t.ProductCode AND p.IsDeleted = 0)
                        LIMIT {MaxSampleCount}"
                    : $@"SELECT DISTINCT TOP {MaxSampleCount} t.ProductCode FROM StoreRetailPrice t
                        WHERE t.IsDeleted = 0 AND NULLIF(LTRIM(RTRIM(t.ProductCode)), '') IS NOT NULL AND t.StoreCode = '{escapedCode}'
                        AND NOT EXISTS (SELECT 1 FROM Product p WHERE p.ProductCode = t.ProductCode AND p.IsDeleted = 0)";
                report.OrphanedProductCodes = await db.Ado.SqlQueryAsync<string>(orphanedSampleSql);
            }

            if (report.MissingCount > 0)
            {
                var missingSampleSql = IsPostgreSQL
                    ? $@"SELECT p.ProductCode FROM Product p
                        WHERE p.IsDeleted = 0 AND p.ProductCode IS NOT NULL
                        AND NOT EXISTS (
                            SELECT 1 FROM StoreRetailPrice t
                            WHERE t.ProductCode = p.ProductCode AND t.IsDeleted = 0 AND t.StoreCode = '{escapedCode}'
                        )
                        LIMIT {MaxSampleCount}"
                    : $@"SELECT TOP {MaxSampleCount} p.ProductCode FROM Product p
                        WHERE p.IsDeleted = 0 AND p.ProductCode IS NOT NULL
                        AND NOT EXISTS (
                            SELECT 1 FROM StoreRetailPrice t
                            WHERE t.ProductCode = p.ProductCode AND t.IsDeleted = 0 AND t.StoreCode = '{escapedCode}'
                        )";
                report.MissingProductCodes = await db.Ado.SqlQueryAsync<string>(missingSampleSql);
            }

            return report;
        }

        private async Task<TableIntegrityReport> CheckStoreMultiCodeProductForStoreAsync(
            ISqlSugarClient db,
            string storeCode
        )
        {
            var report = new TableIntegrityReport { TableName = "StoreMultiCodeProduct" };
            var escapedCode = EscapeSql(storeCode);

            var totalSql =
                $@"SELECT COUNT(DISTINCT sm.ProductCode) FROM StoreMultiCodeProduct sm
                   WHERE sm.IsDeleted = 0 AND NULLIF(LTRIM(RTRIM(sm.ProductCode)), '') IS NOT NULL AND sm.StoreCode = '{escapedCode}'";
            report.TotalChecked = await db.Ado.GetIntAsync(totalSql);

            var invalidKeySql =
                $@"SELECT COUNT(*) FROM StoreMultiCodeProduct sm
                   WHERE sm.IsDeleted = 0 AND sm.StoreCode = '{escapedCode}'
                   AND (
                       NULLIF(LTRIM(RTRIM(sm.ProductCode)), '') IS NULL
                       OR NULLIF(LTRIM(RTRIM(sm.MultiCodeProductCode)), '') IS NULL
                   )";
            ApplyInvalidKeyReport(
                report,
                await db.Ado.GetIntAsync(invalidKeySql),
                $"分店 {storeCode} StoreMultiCodeProduct 存在 {{0}} 条缺少 ProductCode 或 MultiCodeProductCode 的记录，未参与自动修复。"
            );

            var orphanedCountSql =
                $@"SELECT COUNT(*) FROM StoreMultiCodeProduct sm
                   WHERE sm.IsDeleted = 0 AND sm.StoreCode = '{escapedCode}'
                   AND NULLIF(LTRIM(RTRIM(sm.ProductCode)), '') IS NOT NULL
                   AND NULLIF(LTRIM(RTRIM(sm.MultiCodeProductCode)), '') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM ProductSetCode psc
                       WHERE psc.ProductCode = sm.ProductCode
                         AND psc.SetProductCode = sm.MultiCodeProductCode
                         AND psc.IsDeleted = 0
                   )";
            report.OrphanedCount = await db.Ado.GetIntAsync(orphanedCountSql);

            var missingCountSql =
                $@"SELECT COUNT(*) FROM ProductSetCode psc
                   WHERE psc.IsDeleted = 0 AND psc.IsActive = 1
                   AND NULLIF(LTRIM(RTRIM(psc.ProductCode)), '') IS NOT NULL
                   AND NULLIF(LTRIM(RTRIM(psc.SetProductCode)), '') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM StoreMultiCodeProduct sm
                       WHERE sm.ProductCode = psc.ProductCode
                         AND sm.MultiCodeProductCode = psc.SetProductCode
                         AND sm.IsDeleted = 0
                         AND sm.StoreCode = '{escapedCode}'
                   )";
            report.MissingCount = await db.Ado.GetIntAsync(missingCountSql);

            if (report.OrphanedCount > 0)
            {
                var orphanedSampleSql = IsPostgreSQL
                    ? $@"SELECT DISTINCT sm.ProductCode FROM StoreMultiCodeProduct sm
                        WHERE sm.IsDeleted = 0 AND sm.StoreCode = '{escapedCode}'
                        AND NULLIF(LTRIM(RTRIM(sm.ProductCode)), '') IS NOT NULL
                        AND NULLIF(LTRIM(RTRIM(sm.MultiCodeProductCode)), '') IS NOT NULL
                        AND NOT EXISTS (
                            SELECT 1 FROM ProductSetCode psc
                            WHERE psc.ProductCode = sm.ProductCode
                              AND psc.SetProductCode = sm.MultiCodeProductCode
                              AND psc.IsDeleted = 0
                        )
                        LIMIT {MaxSampleCount}"
                    : $@"SELECT DISTINCT TOP {MaxSampleCount} sm.ProductCode FROM StoreMultiCodeProduct sm
                        WHERE sm.IsDeleted = 0 AND sm.StoreCode = '{escapedCode}'
                        AND NULLIF(LTRIM(RTRIM(sm.ProductCode)), '') IS NOT NULL
                        AND NULLIF(LTRIM(RTRIM(sm.MultiCodeProductCode)), '') IS NOT NULL
                        AND NOT EXISTS (
                            SELECT 1 FROM ProductSetCode psc
                            WHERE psc.ProductCode = sm.ProductCode
                              AND psc.SetProductCode = sm.MultiCodeProductCode
                              AND psc.IsDeleted = 0
                        )";
                report.OrphanedProductCodes = await db.Ado.SqlQueryAsync<string>(orphanedSampleSql);
            }

            if (report.MissingCount > 0)
            {
                var missingSampleSql = IsPostgreSQL
                    ? $@"SELECT psc.ProductCode FROM ProductSetCode psc
                        WHERE psc.IsDeleted = 0 AND psc.IsActive = 1
                        AND NULLIF(LTRIM(RTRIM(psc.ProductCode)), '') IS NOT NULL
                        AND NULLIF(LTRIM(RTRIM(psc.SetProductCode)), '') IS NOT NULL
                        AND NOT EXISTS (
                            SELECT 1 FROM StoreMultiCodeProduct sm
                            WHERE sm.ProductCode = psc.ProductCode
                              AND sm.MultiCodeProductCode = psc.SetProductCode
                              AND sm.IsDeleted = 0
                              AND sm.StoreCode = '{escapedCode}'
                        )
                        LIMIT {MaxSampleCount}"
                    : $@"SELECT TOP {MaxSampleCount} psc.ProductCode FROM ProductSetCode psc
                        WHERE psc.IsDeleted = 0 AND psc.IsActive = 1
                        AND NULLIF(LTRIM(RTRIM(psc.ProductCode)), '') IS NOT NULL
                        AND NULLIF(LTRIM(RTRIM(psc.SetProductCode)), '') IS NOT NULL
                        AND NOT EXISTS (
                            SELECT 1 FROM StoreMultiCodeProduct sm
                            WHERE sm.ProductCode = psc.ProductCode
                              AND sm.MultiCodeProductCode = psc.SetProductCode
                              AND sm.IsDeleted = 0
                              AND sm.StoreCode = '{escapedCode}'
                        )";
                report.MissingProductCodes = await db.Ado.SqlQueryAsync<string>(missingSampleSql);
            }

            return report;
        }

        private async Task<TableIntegrityReport> CheckProductSetCodeAsync()
        {
            var report = new TableIntegrityReport { TableName = "ProductSetCode" };
            var db = _db.Db;
            var originalTimeout = db.Ado.CommandTimeOut;
            db.Ado.CommandTimeOut = CommandTimeoutSeconds;
            try
            {
                var totalSql =
                    "SELECT COUNT(DISTINCT ProductCode) FROM ProductSetCode WHERE IsDeleted = 0 AND NULLIF(LTRIM(RTRIM(ProductCode)), '') IS NOT NULL";
                report.TotalChecked = await db.Ado.GetIntAsync(totalSql);
                Console.WriteLine($"  [ProductSetCode] 检测商品编码总数: {report.TotalChecked}");

                var invalidKeySql =
                    @"SELECT COUNT(*) FROM ProductSetCode
                      WHERE IsDeleted = 0
                      AND (
                          NULLIF(LTRIM(RTRIM(ProductCode)), '') IS NULL
                          OR NULLIF(LTRIM(RTRIM(SetProductCode)), '') IS NULL
                      )";
                ApplyInvalidKeyReport(
                    report,
                    await db.Ado.GetIntAsync(invalidKeySql),
                    "ProductSetCode 存在 {0} 条缺少 ProductCode 或 SetProductCode 的记录，未参与自动修复。"
                );

                var orphanedCountSql =
                    @"SELECT COUNT(DISTINCT t.ProductCode) FROM ProductSetCode t
                      WHERE t.IsDeleted = 0
                      AND NULLIF(LTRIM(RTRIM(t.ProductCode)), '') IS NOT NULL
                      AND NULLIF(LTRIM(RTRIM(t.SetProductCode)), '') IS NOT NULL
                      AND NOT EXISTS (SELECT 1 FROM Product p WHERE p.ProductCode = t.ProductCode AND p.IsDeleted = 0)";
                report.OrphanedCount = await db.Ado.GetIntAsync(orphanedCountSql);
                Console.WriteLine($"  [ProductSetCode] 孤立记录数（需删除）: {report.OrphanedCount}");

                if (report.OrphanedCount > 0)
                {
                    var orphanedSampleSql = IsPostgreSQL
                        ? $@"SELECT DISTINCT t.ProductCode FROM ProductSetCode t
                            WHERE t.IsDeleted = 0
                            AND NULLIF(LTRIM(RTRIM(t.ProductCode)), '') IS NOT NULL
                            AND NULLIF(LTRIM(RTRIM(t.SetProductCode)), '') IS NOT NULL
                            AND NOT EXISTS (SELECT 1 FROM Product p WHERE p.ProductCode = t.ProductCode AND p.IsDeleted = 0)
                            LIMIT {MaxSampleCount}"
                        : $@"SELECT DISTINCT TOP {MaxSampleCount} t.ProductCode FROM ProductSetCode t
                            WHERE t.IsDeleted = 0
                            AND NULLIF(LTRIM(RTRIM(t.ProductCode)), '') IS NOT NULL
                            AND NULLIF(LTRIM(RTRIM(t.SetProductCode)), '') IS NOT NULL
                            AND NOT EXISTS (SELECT 1 FROM Product p WHERE p.ProductCode = t.ProductCode AND p.IsDeleted = 0)";
                    report.OrphanedProductCodes = await db.Ado.SqlQueryAsync<string>(orphanedSampleSql);
                    Console.WriteLine(
                        $"  [ProductSetCode] 孤立编码样本: {string.Join(", ", report.OrphanedProductCodes.Take(10))}..."
                    );
                }

                report.MissingCount = 0;
            }
            finally
            {
                db.Ado.CommandTimeOut = originalTimeout;
            }

            return report;
        }

        #endregion

        #region Fix Methods

        /// <summary>
        /// 执行数据一致性修复
        /// 支持对三张表分别执行修复操作，支持 DryRun 模拟运行模式
        /// </summary>
        public async Task<ApiResponse<ProductIntegrityFixResultDto>> FixIntegrityAsync(
            ProductIntegrityFixRequestDto request
        )
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                Console.WriteLine(
                    $"[数据一致性修复] ===== 开始修复（DryRun={request.DryRun}）====="
                );

                var stores = await GetActiveStoresAsync(request.SelectedStoreCodes);
                var activeStoreCodes = stores.Select(s => s.StoreCode).ToList();
                Console.WriteLine($"[数据一致性修复] 活跃分店数量: {activeStoreCodes.Count}");

                var result = new ProductIntegrityFixResultDto { IsDryRun = request.DryRun };

                if (request.FixStoreRetailPrice)
                {
                    result.Reports.Add(
                        await FixStoreRetailPriceAsync(activeStoreCodes, request.DryRun)
                    );
                }

                if (request.FixStoreMultiCodeProduct)
                {
                    result.Reports.Add(
                        await FixStoreMultiCodeProductAsync(activeStoreCodes, request.DryRun)
                    );
                }

                if (request.FixProductSetCode)
                {
                    result.Reports.Add(await FixProductSetCodeAsync(request.DryRun));
                }

                stopwatch.Stop();
                result.DurationSeconds = stopwatch.Elapsed.TotalSeconds;

                _logger.LogInformation(
                    "数据一致性修复完成（DryRun={DryRun}），耗时 {Duration:F2}s",
                    request.DryRun,
                    result.DurationSeconds
                );
                Console.WriteLine(
                    $"[数据一致性修复] ===== 修复完成（DryRun={request.DryRun}），耗时 {result.DurationSeconds:F2}s ====="
                );

                if (ShouldReturnBusyFixResponse(result.Reports))
                {
                    return ApiResponse<ProductIntegrityFixResultDto>.Error(
                        "套装商品正在被其他操作修改，请稍后重试",
                        SetChildPurchasePriceMutationLock.BusyErrorCode,
                        result
                    );
                }

                return ApiResponse<ProductIntegrityFixResultDto>.OK(
                    result,
                    request.DryRun ? "模拟修复完成" : "修复完成"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据一致性修复失败");
                Console.WriteLine($"[数据一致性修复] ❌ 修复失败: {ex.Message}");
                return ApiResponse<ProductIntegrityFixResultDto>.Error(
                    "修复失败: " + ex.Message,
                    "DATABASE_ERROR"
                );
            }
        }

        public async Task<ApiResponse<SetChildPurchasePriceWritebackResultDto>> PreviewSetChildPurchasePricesAsync(
            SetChildPurchasePriceWritebackRequestDto request
        )
        {
            try
            {
                var service = new SetChildPurchasePriceService(_db.Db);
                var result = await service.PreviewAsync(request);
                return ApiResponse<SetChildPurchasePriceWritebackResultDto>.OK(
                    result,
                    "套装子项进货价预览完成"
                );
            }
            catch (Exception ex)
                when (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
            {
                _logger.LogWarning(ex, "套装子项进货价预览获取业务锁失败");
                return ApiResponse<SetChildPurchasePriceWritebackResultDto>.Error(
                    "套装子项成本正在被其他操作更新，请稍后重试",
                    SetChildPurchasePriceMutationLock.BusyErrorCode
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "套装子项进货价预览失败");
                return ApiResponse<SetChildPurchasePriceWritebackResultDto>.Error(
                    "套装子项进货价预览失败: " + ex.Message,
                    "DATABASE_ERROR"
                );
            }
        }

        public async Task<ApiResponse<SetChildPurchasePriceWritebackResultDto>> WritebackSetChildPurchasePricesAsync(
            SetChildPurchasePriceWritebackRequestDto request,
            string updatedBy
        )
        {
            try
            {
                var service = new SetChildPurchasePriceService(_db.Db);
                var result = await service.WritebackAsync(request, updatedBy);
                _logger.LogInformation(
                    "套装子项进货价回写完成，ProductSetCode={ProductSetUpdated}，StoreMultiCodeProduct={StoreUpdated}，跳过组={SkippedGroups}",
                    result.ProductSetCode.UpdatedCount,
                    result.StoreMultiCodeProduct.UpdatedCount,
                    result.ProductSetCode.SkippedGroupCount
                        + result.StoreMultiCodeProduct.SkippedGroupCount
                );
                return ApiResponse<SetChildPurchasePriceWritebackResultDto>.OK(
                    result,
                    "套装子项进货价回写完成"
                );
            }
            catch (Exception ex)
                when (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
            {
                _logger.LogWarning(ex, "套装子项进货价回写获取业务锁失败");
                return ApiResponse<SetChildPurchasePriceWritebackResultDto>.Error(
                    "套装子项成本正在被其他操作更新，请稍后重试",
                    SetChildPurchasePriceMutationLock.BusyErrorCode
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "套装子项进货价回写失败");
                return ApiResponse<SetChildPurchasePriceWritebackResultDto>.Error(
                    "套装子项进货价回写失败: " + ex.Message,
                    "DATABASE_ERROR"
                );
            }
        }

        /// <summary>
        /// 修复 StoreRetailPrice（分店零售价表）
        /// 1. 软删除孤立记录（商品编码不在基础表中）
        /// 2. 为每个分店补充缺失的商品零售价记录
        /// </summary>
        private async Task<TableFixReport> FixStoreRetailPriceAsync(
            List<string> activeStoreCodes,
            bool dryRun
        )
        {
            var report = new TableFixReport { TableName = "StoreRetailPrice" };

            try
            {
                var db = _db.Db;
                var originalTimeout = db.Ado.CommandTimeOut;
                db.Ado.CommandTimeOut = CommandTimeoutSeconds;
                try
                {
                    var productFields = await db.Queryable<Product>()
                        .Where(p => p.IsDeleted == false && p.ProductCode != null)
                        .Select(p => new
                        {
                            p.ProductCode,
                            p.PurchasePrice,
                            p.RetailPrice,
                            p.LocalSupplierCode,
                            p.IsActive,
                            p.IsAutoPricing,
                            p.IsSpecialProduct,
                        })
                        .ToListAsync();
                    var productDict = productFields
                        .Where(p => p.ProductCode != null)
                        .ToDictionary(p => p.ProductCode!);
                    Console.WriteLine($"  [StoreRetailPrice] 基础商品总数: {productDict.Count}");

                    var semaphore = new SemaphoreSlim(5);

                    // 阶段一：并发查询，收集变更计划
                    var queryTasks = activeStoreCodes.Select(async storeCode =>
                    {
                        await semaphore.WaitAsync();
                        ISqlSugarClient? storeDb = null;
                        try
                        {
                            storeDb = SqlSugarContext.CreateConcurrentConnection(_configuration);
                            storeDb.Ado.CommandTimeOut = CommandTimeoutSeconds;

                            var rawStoreRecords = await storeDb
                                .Queryable<StoreRetailPrice>()
                                .Where(sr => sr.StoreCode == storeCode && sr.IsDeleted == false)
                                .Select(sr => sr.ProductCode)
                                .ToListAsync();
                            var invalidProductCodeCount = rawStoreRecords.Count(string.IsNullOrWhiteSpace);
                            // 缺少商品编码的数据不能参与字典匹配，但必须进入报告，避免数据质量问题被静默吞掉。
                            var storeRecords = rawStoreRecords
                                .Where(productCode => !string.IsNullOrWhiteSpace(productCode))
                                .Select(productCode => productCode!)
                                .Distinct()
                                .ToList();

                            var storeProductSet = new HashSet<string>(storeRecords);

                            var orphanedProductCodes = storeRecords
                                .Where(pc => !productDict.ContainsKey(pc))
                                .ToList();

                            var missingProductCodes = productDict
                                .Keys.Where(pc => !storeProductSet.Contains(pc))
                                .ToList();

                            Console.WriteLine(
                                $"    [分店 {storeCode}] 现有: {storeRecords.Count}, 孤立: {orphanedProductCodes.Count}, 缺失: {missingProductCodes.Count}"
                            );

                            return new StoreRetailPriceChangePlan
                            {
                                StoreCode = storeCode,
                                OrphanedProductCodes = orphanedProductCodes,
                                OrphanedCount = orphanedProductCodes.Count,
                                MissingProductCodes = missingProductCodes,
                                InvalidKeyCount = invalidProductCodeCount,
                            };
                        }
                        finally
                        {
                            semaphore.Release();
                            storeDb?.Dispose();
                        }
                    });
                    var changePlans = (await Task.WhenAll(queryTasks)).ToList();
                    report.Errors.AddRange(
                        changePlans
                            .Where(plan => plan.InvalidKeyCount > 0)
                            .Select(plan =>
                                $"分店 {plan.StoreCode} StoreRetailPrice 存在 {plan.InvalidKeyCount} 条缺少 ProductCode 的记录，未参与自动修复。"
                            )
                    );
                    report.ErrorCount = report.Errors.Count;

                    // 阶段二：并发执行软删除（只有UPDATE，不会死锁）
                    var deleteTasks = changePlans
                        .Where(p => p.OrphanedCount > 0)
                        .Select(async plan =>
                        {
                            if (dryRun)
                                return (plan.StoreCode, Deleted: plan.OrphanedCount);

                            await semaphore.WaitAsync();
                            ISqlSugarClient? storeDb = null;
                            try
                            {
                                storeDb = SqlSugarContext.CreateConcurrentConnection(_configuration);
                                storeDb.Ado.CommandTimeOut = CommandTimeoutSeconds;

                                var deleted = await storeDb
                                    .Updateable<StoreRetailPrice>()
                                    .SetColumns(sr => sr.IsDeleted == true)
                                    .SetColumns(sr => sr.UpdatedAt == DateTime.UtcNow)
                                    .SetColumns(sr => sr.UpdatedBy == "IntegrityFix")
                                    .Where(sr =>
                                        sr.StoreCode == plan.StoreCode
                                        && sr.IsDeleted == false
                                        && sr.ProductCode != null
                                        && plan.OrphanedProductCodes.Contains(sr.ProductCode)
                                    )
                                    .ExecuteCommandAsync();

                                Console.WriteLine(
                                    $"    [分店 {plan.StoreCode}] 已删除 {deleted}"
                                );
                                return (plan.StoreCode, Deleted: deleted);
                            }
                            finally
                            {
                                semaphore.Release();
                                storeDb?.Dispose();
                            }
                    });
                    var deleteResults = await Task.WhenAll(deleteTasks);
                    report.DeletedCount = deleteResults.Sum(r => r.Deleted);

                    // 阶段三：并发执行插入（只有INSERT，不会死锁）
                    var insertTasks = changePlans
                        .Where(p => p.MissingProductCodes.Count > 0)
                        .Select(async plan =>
                        {
                            var newRecords = plan.MissingProductCodes
                                .Select(pc =>
                                {
                                    var p = productDict[pc];
                                    return new StoreRetailPrice
                                    {
                                        UUID = UuidHelper.GenerateUuid7(),
                                        StoreCode = plan.StoreCode,
                                        ProductCode = pc,
                                        StoreProductCode = pc,
                                        SupplierCode = p.LocalSupplierCode,
                                        PurchasePrice = p.PurchasePrice,
                                        StoreRetailPriceValue = p.RetailPrice,
                                        DiscountRate = 0m, // 0 = 无折扣；曾写 1（=减免 100%）造成整行免单
                                        IsActive = p.IsActive,
                                        IsAutoPricing = p.IsAutoPricing,
                                        IsSpecialProduct = p.IsSpecialProduct,
                                        IsDeleted = false,
                                        CreatedAt = DateTime.UtcNow,
                                        CreatedBy = "IntegrityFix",
                                    };
                                })
                                .ToList();

                            if (dryRun)
                            {
                                Console.WriteLine(
                                    $"    [分店 {plan.StoreCode}] DryRun 预计新增 {newRecords.Count}"
                                );
                                return (plan.StoreCode, Added: newRecords.Count);
                            }

                            await semaphore.WaitAsync();
                            ISqlSugarClient? storeDb = null;
                            try
                            {
                                storeDb = SqlSugarContext.CreateConcurrentConnection(_configuration);
                                storeDb.Ado.CommandTimeOut = CommandTimeoutSeconds;

                                await BatchOperationHelper.BatchInsertAsync(
                                    storeDb,
                                    newRecords,
                                    BatchOperationHelper.LARGE_BATCH_SIZE
                                );
                                Console.WriteLine(
                                    $"    [分店 {plan.StoreCode}] 已新增 {newRecords.Count}"
                                );
                                return (plan.StoreCode, Added: newRecords.Count);
                            }
                            finally
                            {
                                semaphore.Release();
                                storeDb?.Dispose();
                            }
                        });
                    var insertResults = await Task.WhenAll(insertTasks);
                    report.AddedCount = insertResults.Sum(r => r.Added);

                    Console.WriteLine(
                        $"  [StoreRetailPrice] 总计删除: {report.DeletedCount}，新增: {report.AddedCount}"
                    );
                }
                finally
                {
                    db.Ado.CommandTimeOut = originalTimeout;
                }

                _logger.LogInformation(
                    "StoreRetailPrice 修复：删除 {Deleted}，新增 {Added}（DryRun={DryRun}）",
                    report.DeletedCount,
                    report.AddedCount,
                    dryRun
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "修复 StoreRetailPrice 失败");
                Console.WriteLine($"  [StoreRetailPrice] ❌ 修复失败: {ex.Message}");
                report.Errors.Add(ex.Message);
                report.ErrorCount++;
            }

            return report;
        }

        /// <summary>
        /// 修复 StoreMultiCodeProduct（分店多码表）
        /// 和 ProductSetCode（套装编码表）对比：
        /// 1. 软删除孤立记录（分店多码表中存在但 ProductSetCode 中没有对应记录）
        /// 2. 为每个分店补充 ProductSetCode 中有但分店多码表中没有的记录
        /// </summary>
        private async Task<TableFixReport> FixStoreMultiCodeProductAsync(
            List<string> activeStoreCodes,
            bool dryRun
        )
        {
            var report = new TableFixReport { TableName = "StoreMultiCodeProduct" };

            try
            {
                var db = _db.Db;
                var originalTimeout = db.Ado.CommandTimeOut;
                db.Ado.CommandTimeOut = CommandTimeoutSeconds;
                try
                {
                    var rawProductSetCodes = await db.Queryable<ProductSetCode>()
                        .Where(p => p.IsDeleted == false && p.IsActive == true)
                        .ToListAsync();
                    var invalidSetCodeCount = rawProductSetCodes.Count(p =>
                        string.IsNullOrWhiteSpace(p.ProductCode)
                        || string.IsNullOrWhiteSpace(p.SetProductCode)
                    );
                    if (invalidSetCodeCount > 0)
                    {
                        // 套装编码缺关键字段时只能报告，不能进入 HashSet/字典匹配。
                        report.Errors.Add(
                            $"ProductSetCode 存在 {invalidSetCodeCount} 条缺少 ProductCode 或 SetProductCode 的记录，未参与自动修复。"
                        );
                    }

                    var productSetCodes = rawProductSetCodes
                        .Where(p =>
                            !string.IsNullOrWhiteSpace(p.ProductCode)
                            && !string.IsNullOrWhiteSpace(p.SetProductCode)
                        )
                        .ToList();
                    Console.WriteLine(
                        $"  [StoreMultiCodeProduct] 套装编码总数: {productSetCodes.Count}"
                    );

                    var setCodeDict = productSetCodes
                        .GroupBy(sc => sc.ProductCode!)
                        .ToDictionary(g => g.Key, g => g.ToList());

                    var setCodeKeySet = new HashSet<string>(
                        productSetCodes.Select(sc => $"{sc.ProductCode!}\u0001{sc.SetProductCode!}")
                    );

                    var semaphore = new SemaphoreSlim(5);

                    // 阶段一：并发查询，收集变更计划
                    var changePlans = new List<StoreMultiCodeChangePlan>();
                    var queryTasks = activeStoreCodes.Select(async storeCode =>
                    {
                        await semaphore.WaitAsync();
                        ISqlSugarClient? storeDb = null;
                        try
                        {
                            storeDb = SqlSugarContext.CreateConcurrentConnection(_configuration);
                            storeDb.Ado.CommandTimeOut = CommandTimeoutSeconds;

                            var rawStoreRecords = await storeDb
                                .Queryable<StoreMultiCodeProduct>()
                                .Where(sm => sm.StoreCode == storeCode && sm.IsDeleted == false)
                                .Select(sm => new { sm.ProductCode, sm.MultiCodeProductCode })
                                .ToListAsync();
                            var invalidStoreMultiCodeCount = rawStoreRecords.Count(r =>
                                string.IsNullOrWhiteSpace(r.ProductCode)
                                || string.IsNullOrWhiteSpace(r.MultiCodeProductCode)
                            );
                            // 分店多码缺关键字段时保留为报告错误，防止修复统计漏报坏数据。
                            var storeRecords = rawStoreRecords
                                .Where(r =>
                                    !string.IsNullOrWhiteSpace(r.ProductCode)
                                    && !string.IsNullOrWhiteSpace(r.MultiCodeProductCode)
                                )
                                .Select(r => new
                                {
                                    ProductCode = r.ProductCode!,
                                    MultiCodeProductCode = r.MultiCodeProductCode!,
                                })
                                .ToList();

                            var storeKeySet = new HashSet<string>(
                                storeRecords.Select(r =>
                                    $"{r.ProductCode}\u0001{r.MultiCodeProductCode}"
                                )
                            );

                            var orphanedRecords = storeRecords
                                .Where(r =>
                                    !setCodeDict.ContainsKey(r.ProductCode)
                                    || !setCodeDict[r.ProductCode]
                                        .Any(sc => sc.SetProductCode == r.MultiCodeProductCode)
                                )
                                .ToList();

                            var missingEntries = productSetCodes
                                .Where(sc =>
                                    !storeKeySet.Contains(
                                        $"{sc.ProductCode}\u0001{sc.SetProductCode}"
                                    )
                                )
                                .ToList();

                            Console.WriteLine(
                                $"    [分店 {storeCode}] 现有: {storeRecords.Count}, 孤立: {orphanedRecords.Count}, 缺失: {missingEntries.Count}"
                            );

                            return new StoreMultiCodeChangePlan
                            {
                                StoreCode = storeCode,
                                OrphanedKeys = orphanedRecords
                                    .Select(r =>
                                        new StoreMultiCodeKey(
                                            r.ProductCode,
                                            r.MultiCodeProductCode
                                        )
                                    )
                                    .Distinct()
                                    .ToList(),
                                OrphanedCount = orphanedRecords.Count,
                                MissingEntries = missingEntries,
                                InvalidKeyCount = invalidStoreMultiCodeCount,
                            };
                        }
                        finally
                        {
                            semaphore.Release();
                            storeDb?.Dispose();
                        }
                    });
                    changePlans = (await Task.WhenAll(queryTasks)).ToList();
                    report.Errors.AddRange(
                        changePlans
                            .Where(plan => plan.InvalidKeyCount > 0)
                            .Select(plan =>
                                $"分店 {plan.StoreCode} StoreMultiCodeProduct 存在 {plan.InvalidKeyCount} 条缺少 ProductCode 或 MultiCodeProductCode 的记录，未参与自动修复。"
                            )
                    );
                    report.ErrorCount = report.Errors.Count;

                    var managedSetParentCodes = productSetCodes
                        .Where(setCode => setCode.SetType == 1 || setCode.SetType == 2)
                        .Select(setCode => setCode.ProductCode!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // Type1/Type2 的门店子项成本都由统一服务派生，必须把补行、删孤儿和重算放在同一事务内。
                    // 每次只锁一个 (门店, 主商品) 组，某组异常时仅回滚该组，其他组仍可继续修复。
                    var managedSetResults = new List<StoreMultiCodeGroupFixResult>();
                    foreach (var plan in changePlans)
                    {
                        foreach (var parentCode in managedSetParentCodes)
                        {
                            var managedSetCodes = productSetCodes
                                .Where(setCode =>
                                    (setCode.SetType == 1 || setCode.SetType == 2)
                                    && string.Equals(
                                        setCode.ProductCode,
                                        parentCode,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                .ToList();
                            var allParentSetCodes = productSetCodes
                                .Where(setCode =>
                                    string.Equals(
                                        setCode.ProductCode,
                                        parentCode,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                .ToList();
                            managedSetResults.Add(
                                await FixType1StoreMultiCodeGroupAsync(
                                    plan.StoreCode,
                                    parentCode,
                                    managedSetCodes,
                                    allParentSetCodes,
                                    dryRun
                                )
                            );
                        }
                    }
                    report.DeletedCount = managedSetResults.Sum(result => result.DeletedCount);
                    report.AddedCount = managedSetResults.Sum(result => result.AddedCount);
                    report.SuccessfulGroupCount = managedSetResults.Count(result =>
                        string.IsNullOrWhiteSpace(result.Error)
                    );
                    report.Errors.AddRange(
                        managedSetResults
                            .Where(result => !string.IsNullOrWhiteSpace(result.Error))
                            .Select(result =>
                                $"分店 {result.StoreCode} 主商品 {result.ProductCode} Type1/Type2 修复已回滚: {result.Error}"
                            )
                    );
                    report.FailureDetails.AddRange(
                        managedSetResults
                            .Where(result => !string.IsNullOrWhiteSpace(result.Error))
                            .Select(result => new BatchOperationFailureDto
                            {
                                ItemKey = $"{result.StoreCode}|{result.ProductCode}",
                                Message = result.Error!,
                                ErrorCode = result.ErrorCode,
                            })
                    );
                    var allManagedSetGroupsBusy =
                        managedSetResults.Count > 0
                        && managedSetResults.All(result =>
                            result.ErrorCode == SetChildPurchasePriceMutationLock.BusyErrorCode
                        );
                    report.ErrorCount = report.Errors.Count;
                    var managedSetParentCodeSet = managedSetParentCodes.ToHashSet(
                        StringComparer.OrdinalIgnoreCase
                    );

                    // 阶段二：并发执行软删除（只有UPDATE，不会死锁）
                    var deleteTasks = changePlans
                        .Select(async plan =>
                        {
                            var nonManagedSetOrphanedKeys = plan.OrphanedKeys
                                .Where(key => !managedSetParentCodeSet.Contains(key.ProductCode))
                                .ToList();
                            if (nonManagedSetOrphanedKeys.Count == 0)
                            {
                                return (plan.StoreCode, Deleted: 0);
                            }
                            if (dryRun)
                                return (plan.StoreCode, Deleted: nonManagedSetOrphanedKeys.Count);

                            await semaphore.WaitAsync();
                            ISqlSugarClient? storeDb = null;
                            try
                            {
                                storeDb = SqlSugarContext.CreateConcurrentConnection(_configuration);
                                storeDb.Ado.CommandTimeOut = CommandTimeoutSeconds;

                                var deleted = await SoftDeleteStoreMultiCodeOrphansAsync(
                                    storeDb,
                                    new StoreMultiCodeChangePlan
                                    {
                                        StoreCode = plan.StoreCode,
                                        OrphanedKeys = nonManagedSetOrphanedKeys,
                                    }
                                );

                                Console.WriteLine(
                                    $"    [分店 {plan.StoreCode}] 已删除 {deleted}"
                                );
                                return (plan.StoreCode, Deleted: deleted);
                            }
                            finally
                            {
                                semaphore.Release();
                                storeDb?.Dispose();
                            }
                    });
                    var deleteResults = await Task.WhenAll(deleteTasks);
                    report.DeletedCount += deleteResults.Sum(r => r.Deleted);

                    // 阶段三：并发执行插入（只有INSERT，不会死锁）
                    var insertTasks = changePlans
                        .Where(p => p.MissingEntries.Count > 0)
                        .Select(async plan =>
                        {
                            var newRecords = plan.MissingEntries
                                .Where(sc => sc.SetType != 1 && sc.SetType != 2)
                                .Select(sc => new StoreMultiCodeProduct
                                {
                                    UUID = UuidHelper.GenerateUuid7(),
                                    StoreCode = plan.StoreCode,
                                    ProductCode = sc.ProductCode,
                                    MultiCodeProductCode = sc.SetProductCode,
                                    StoreMultiCodeProductCode = sc.SetProductCode,
                                    MultiBarcode = sc.SetBarcode,
                                    PurchasePrice = sc.SetPurchasePrice,
                                    MultiCodeRetailPrice = sc.SetRetailPrice,
                                    DiscountRate = 0m,
                                    IsAutoPricing = false,
                                    IsSpecialProduct = false,
                                    IsActive = true,
                                    IsDeleted = false,
                                    CreatedAt = DateTime.UtcNow,
                                    CreatedBy = "IntegrityFix",
                                })
                                .ToList();

                            if (dryRun)
                            {
                                Console.WriteLine(
                                    $"    [分店 {plan.StoreCode}] DryRun 预计新增 {newRecords.Count}"
                                );
                                return (plan.StoreCode, Added: newRecords.Count);
                            }

                            await semaphore.WaitAsync();
                            ISqlSugarClient? storeDb = null;
                            try
                            {
                                storeDb = SqlSugarContext.CreateConcurrentConnection(_configuration);
                                storeDb.Ado.CommandTimeOut = CommandTimeoutSeconds;

                                await BatchOperationHelper.BatchInsertAsync(
                                    storeDb,
                                    newRecords,
                                    BatchOperationHelper.LARGE_BATCH_SIZE
                                );
                                Console.WriteLine(
                                    $"    [分店 {plan.StoreCode}] 已新增 {newRecords.Count}"
                                );
                                return (plan.StoreCode, Added: newRecords.Count);
                            }
                            finally
                            {
                                semaphore.Release();
                                storeDb?.Dispose();
                            }
                    });
                    var insertResults = await Task.WhenAll(insertTasks);
                    report.AddedCount += insertResults.Sum(r => r.Added);
                    if (
                        allManagedSetGroupsBusy
                        && report.AddedCount == 0
                        && report.DeletedCount == 0
                    )
                    {
                        report.ErrorCode = SetChildPurchasePriceMutationLock.BusyErrorCode;
                    }

                    Console.WriteLine(
                        $"  [StoreMultiCodeProduct] 总计删除: {report.DeletedCount}，新增: {report.AddedCount}"
                    );
                }
                finally
                {
                    db.Ado.CommandTimeOut = originalTimeout;
                }

                _logger.LogInformation(
                    "StoreMultiCodeProduct 修复：删除 {Deleted}，新增 {Added}（DryRun={DryRun}）",
                    report.DeletedCount,
                    report.AddedCount,
                    dryRun
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "修复 StoreMultiCodeProduct 失败");
                Console.WriteLine($"  [StoreMultiCodeProduct] ❌ 修复失败: {ex.Message}");
                report.Errors.Add(ex.Message);
                report.ErrorCount++;
            }

            return report;
        }

        /// <summary>
        /// 修复 ProductSetCode（套装编码表）
        /// 只做软删除孤立记录（商品编码不在基础表中）
        /// 注意：ProductSetCode 是独立维护的数据，不存在"缺失需添加"的场景
        /// </summary>
        private async Task<TableFixReport> FixProductSetCodeAsync(bool dryRun)
        {
            var report = new TableFixReport { TableName = "ProductSetCode" };

            try
            {
                var db = _db.Db;
                var originalTimeout = db.Ado.CommandTimeOut;
                db.Ado.CommandTimeOut = CommandTimeoutSeconds;
                try
                {
                    Console.WriteLine($"  [ProductSetCode] 软删除孤立记录...");

                    var invalidKeyCount = await db.Queryable<ProductSetCode>()
                        .Where(p =>
                            p.IsDeleted == false
                            && (
                                p.ProductCode == null
                                || p.ProductCode.Trim() == string.Empty
                                || p.SetProductCode == null
                                || p.SetProductCode.Trim() == string.Empty
                            )
                        )
                        .CountAsync();
                    if (invalidKeyCount > 0)
                    {
                        // ProductSetCode 自身缺关键编码时只报告，不作为孤立记录自动删除。
                        report.Errors.Add(
                            $"ProductSetCode 存在 {invalidKeyCount} 条缺少 ProductCode 或 SetProductCode 的记录，未参与自动修复。"
                        );
                        report.ErrorCount = report.Errors.Count;
                    }

                    var deleteSql =
                        @"UPDATE ProductSetCode
                          SET IsDeleted = 1, UpdatedAt = @now, UpdatedBy = 'IntegrityFix'
                          WHERE IsDeleted = 0
                          AND NULLIF(LTRIM(RTRIM(ProductCode)), '') IS NOT NULL
                          AND NULLIF(LTRIM(RTRIM(SetProductCode)), '') IS NOT NULL
                          AND NOT EXISTS (
                              SELECT 1 FROM Product p WHERE p.ProductCode = ProductSetCode.ProductCode AND p.IsDeleted = 0
                          )";

                    if (dryRun)
                    {
                        var countSql =
                            @"SELECT COUNT(*) FROM ProductSetCode
                              WHERE IsDeleted = 0
                              AND NULLIF(LTRIM(RTRIM(ProductCode)), '') IS NOT NULL
                              AND NULLIF(LTRIM(RTRIM(SetProductCode)), '') IS NOT NULL
                              AND NOT EXISTS (
                                  SELECT 1 FROM Product p WHERE p.ProductCode = ProductSetCode.ProductCode AND p.IsDeleted = 0
                              )";
                        report.DeletedCount = await db.Ado.GetIntAsync(countSql);
                        Console.WriteLine(
                            $"  [ProductSetCode] DryRun 预计删除: {report.DeletedCount} 条"
                        );
                    }
                    else
                    {
                        report.DeletedCount = await db.Ado.ExecuteCommandAsync(
                            deleteSql,
                            new { now = DateTime.UtcNow }
                        );
                        Console.WriteLine($"  [ProductSetCode] 实际删除: {report.DeletedCount} 条");
                    }

                    _logger.LogInformation(
                        "ProductSetCode 修复：删除 {Deleted}（DryRun={DryRun}）",
                        report.DeletedCount,
                        dryRun
                    );
                }
                finally
                {
                    db.Ado.CommandTimeOut = originalTimeout;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "修复 ProductSetCode 失败");
                Console.WriteLine($"  [ProductSetCode] ❌ 修复失败: {ex.Message}");
                report.Errors.Add(ex.Message);
                report.ErrorCount++;
            }

            return report;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// 加载基础商品表字典（商品编码 → 商品实体）
        /// </summary>
        private async Task<Dictionary<string, Product>> LoadProductDictAsync()
        {
            var products = await _db
                .ProductDb.AsQueryable()
                .Where(p => p.IsDeleted == false && p.ProductCode != null)
                .ToListAsync();
            return products.Where(p => p.ProductCode != null).ToDictionary(p => p.ProductCode!);
        }

        /// <summary>
        /// 获取活跃分店列表
        /// 如果指定了 storeCodes 则筛选对应分店，否则返回所有活跃分店
        /// </summary>
        private async Task<List<Store>> GetActiveStoresAsync(List<string>? storeCodes)
        {
            var query = _db.StoreDb.AsQueryable().Where(s => s.IsDeleted == false && s.IsActive == true);

            if (storeCodes != null && storeCodes.Count > 0)
            {
                query = query.Where(s => storeCodes.Contains(s.StoreCode));
            }

            return await query.ToListAsync();
        }

        private static async Task<int> SoftDeleteStoreMultiCodeOrphansAsync(
            ISqlSugarClient db,
            StoreMultiCodeChangePlan plan
        )
        {
            var deleted = 0;
            foreach (var keyChunk in plan.OrphanedKeys.Chunk(200))
            {
                var parameters = new List<SugarParameter>
                {
                    new("@storeCode", plan.StoreCode),
                    new("@updatedAt", DateTime.UtcNow),
                };
                var predicates = new List<string>();
                var index = 0;

                foreach (var key in keyChunk)
                {
                    var productParam = $"@productCode{index}";
                    var multiCodeParam = $"@multiCode{index}";
                    predicates.Add(
                        $"(ProductCode = {productParam} AND MultiCodeProductCode = {multiCodeParam})"
                    );
                    parameters.Add(new SugarParameter(productParam, key.ProductCode));
                    parameters.Add(new SugarParameter(multiCodeParam, key.MultiCodeProductCode));
                    index++;
                }

                if (predicates.Count == 0)
                {
                    continue;
                }

                // 必须按复合键精确匹配，避免两个 IN 集合交叉后误删有效多码记录。
                var sql =
                    $@"UPDATE StoreMultiCodeProduct
                       SET IsDeleted = 1, UpdatedAt = @updatedAt, UpdatedBy = 'IntegrityFix'
                       WHERE StoreCode = @storeCode
                       AND IsDeleted = 0
                       AND ({string.Join(" OR ", predicates)})";
                deleted += await db.Ado.ExecuteCommandAsync(sql, parameters.ToArray());
            }

            return deleted;
        }

        private async Task<StoreMultiCodeGroupFixResult> FixType1StoreMultiCodeGroupAsync(
            string storeCode,
            string productCode,
            List<ProductSetCode> managedSetCodes,
            List<ProductSetCode> allParentSetCodes,
            bool dryRun
        )
        {
            ISqlSugarClient? storeDb = null;
            try
            {
                storeDb = SqlSugarContext.CreateConcurrentConnection(_configuration);
                storeDb.Ado.CommandTimeOut = CommandTimeoutSeconds;

                await storeDb.Ado.BeginTranAsync();
                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        storeDb,
                        new[] { productCode }
                    );
                    // 获取锁后重新读取关系和门店投影，禁止使用锁前套装快照决定插入或重算。
                    var lockedParentSetCodes = await storeDb.Queryable<ProductSetCode>()
                        .Where(setCode =>
                            setCode.ProductCode == productCode
                            && setCode.IsActive
                            && !setCode.IsDeleted
                        )
                        .ToListAsync();
                    var lockedManagedSetCodes = lockedParentSetCodes
                        .Where(setCode => setCode.SetType == 1 || setCode.SetType == 2)
                        .ToList();
                    var currentRows = await GetActiveStoreMultiCodeGroupAsync(
                        storeDb,
                        storeCode,
                        productCode
                    );
                    var plan = BuildType1StoreMultiCodeGroupPlan(
                        storeCode,
                        productCode,
                        lockedManagedSetCodes,
                        lockedParentSetCodes,
                        currentRows
                    );
                    var storeParentPrices = await storeDb.Queryable<StoreRetailPrice>()
                        .Where(price =>
                            price.StoreCode == storeCode
                            && price.ProductCode == productCode
                            && price.IsActive
                            && !price.IsDeleted
                        )
                        .ToListAsync();
                    if (storeParentPrices.Count != 1)
                    {
                        throw new InvalidOperationException("门店主商品成本记录缺失或重复");
                    }
                    if (storeParentPrices[0].PurchasePrice.GetValueOrDefault() <= 0)
                    {
                        throw new InvalidOperationException("门店主商品成本为空或0");
                    }
                    if (dryRun)
                    {
                        // 预览也必须使用与正式修复相同的锁内复读结果，但事务始终零写入。
                        await storeDb.Ado.RollbackTranAsync();
                        return plan;
                    }

                    if (plan.OrphanedKeys.Count > 0)
                    {
                        plan.DeletedCount = await SoftDeleteStoreMultiCodeOrphansAsync(
                            storeDb,
                            new StoreMultiCodeChangePlan
                            {
                                StoreCode = storeCode,
                                OrphanedKeys = plan.OrphanedKeys,
                            }
                        );
                    }

                    if (plan.MissingEntries.Count > 0)
                    {
                        // Type1/Type2 都不能复制总部或旧子项成本，统一服务随后按门店主成本回写。
                        var newRows = plan.MissingEntries
                            .Select(setCode => new StoreMultiCodeProduct
                            {
                                UUID = UuidHelper.GenerateUuid7(),
                                StoreCode = storeCode,
                                ProductCode = productCode,
                                MultiCodeProductCode = setCode.SetProductCode,
                                StoreMultiCodeProductCode = setCode.SetProductCode,
                                MultiBarcode = setCode.SetBarcode,
                                PurchasePrice = null,
                                MultiCodeRetailPrice = setCode.SetRetailPrice,
                                DiscountRate = 0m,
                                IsAutoPricing = false,
                                IsSpecialProduct = false,
                                IsActive = true,
                                IsDeleted = false,
                                CreatedAt = DateTime.UtcNow,
                                CreatedBy = "IntegrityFix",
                            })
                            .ToList();
                        plan.AddedCount = await storeDb.Insertable(newRows).ExecuteCommandAsync();
                    }

                    var recalculation = await new SetChildPurchasePriceService(storeDb)
                        .RecalculateStoresLockedAsync(
                            lockScope,
                            new[] { productCode },
                            new[] { storeCode },
                            "IntegrityFix"
                        );
                    if (recalculation.StoreMultiCodeProduct.SkippedGroupCount > 0)
                    {
                        var reason = recalculation.Errors.FirstOrDefault()?.Reason
                            ?? "目标门店套装子项成本重算不完整";
                        throw new InvalidOperationException(reason);
                    }
                    await storeDb.Ado.CommitTranAsync();
                    return plan;
                }
                catch (Exception ex)
                {
                    await storeDb.Ado.RollbackTranAsync();
                    return new StoreMultiCodeGroupFixResult
                    {
                        StoreCode = storeCode,
                        ProductCode = productCode,
                        Error = ex.Message,
                        ErrorCode = SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _)
                            ? SetChildPurchasePriceMutationLock.BusyErrorCode
                            : null,
                    };
                }
            }
            catch (Exception ex)
            {
                return new StoreMultiCodeGroupFixResult
                {
                    StoreCode = storeCode,
                    ProductCode = productCode,
                    Error = ex.Message,
                    ErrorCode = SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _)
                        ? SetChildPurchasePriceMutationLock.BusyErrorCode
                        : null,
                };
            }
            finally
            {
                storeDb?.Dispose();
            }
        }

        private static async Task<List<StoreMultiCodeProduct>> GetActiveStoreMultiCodeGroupAsync(
            ISqlSugarClient db,
            string storeCode,
            string productCode
        )
        {
            return await db.Queryable<StoreMultiCodeProduct>()
                .Where(row =>
                    row.StoreCode == storeCode
                    && row.ProductCode == productCode
                    && row.IsDeleted == false
                )
                .ToListAsync();
        }

        internal static bool ShouldReturnBusyFixResponse(
            IEnumerable<TableFixReport> reports
        )
        {
            var reportList = reports.ToList();
            var hasBusyFailure = reportList.Any(report =>
                report.ErrorCode == SetChildPurchasePriceMutationLock.BusyErrorCode
                || report.FailureDetails.Any(failure =>
                    failure.ErrorCode == SetChildPurchasePriceMutationLock.BusyErrorCode
                )
            );
            var successCount = reportList.Sum(report =>
                report.SuccessfulGroupCount + report.AddedCount + report.DeletedCount
            );
            return hasBusyFailure && successCount == 0;
        }

        private static StoreMultiCodeGroupFixResult BuildType1StoreMultiCodeGroupPlan(
            string storeCode,
            string productCode,
            List<ProductSetCode> managedSetCodes,
            List<ProductSetCode> allParentSetCodes,
            List<StoreMultiCodeProduct> currentRows
        )
        {
            var invalidManagedSetKey = managedSetCodes.FirstOrDefault(setCode =>
                string.IsNullOrWhiteSpace(setCode.SetProductCode)
            );
            if (invalidManagedSetKey != null)
            {
                throw new InvalidOperationException("套装子项业务键不能为空");
            }
            var crossTypeConflict = managedSetCodes
                .GroupBy(
                    setCode => setCode.SetProductCode!.Trim().ToUpperInvariant(),
                    StringComparer.Ordinal
                )
                .FirstOrDefault(group => group.Select(setCode => setCode.SetType).Distinct().Count() > 1);
            if (crossTypeConflict != null)
            {
                throw new InvalidOperationException(
                    $"同一子项存在活跃Type1/Type2冲突: {crossTypeConflict.Key}"
                );
            }
            var duplicateManagedSetKey = managedSetCodes
                .GroupBy(
                    setCode => setCode.SetProductCode!.Trim().ToUpperInvariant(),
                    StringComparer.Ordinal
                )
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateManagedSetKey != null)
            {
                throw new InvalidOperationException(
                    $"套装子项业务键重复: {duplicateManagedSetKey.Key}"
                );
            }
            var duplicateStoreKey = currentRows
                .Where(row => !string.IsNullOrWhiteSpace(row.MultiCodeProductCode))
                .GroupBy(
                    row => row.MultiCodeProductCode!.Trim().ToUpperInvariant(),
                    StringComparer.Ordinal
                )
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateStoreKey != null)
            {
                throw new InvalidOperationException(
                    $"门店套装子项业务键重复: {duplicateStoreKey.Key}"
                );
            }

            var expectedAllChildCodes = allParentSetCodes
                .Where(setCode => !string.IsNullOrWhiteSpace(setCode.SetProductCode))
                .Select(setCode => setCode.SetProductCode!.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existingChildCodes = currentRows
                .Where(row => !string.IsNullOrWhiteSpace(row.MultiCodeProductCode))
                .Select(row => row.MultiCodeProductCode!.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var orphanedKeys = currentRows
                .Where(row =>
                    !string.IsNullOrWhiteSpace(row.MultiCodeProductCode)
                    && !expectedAllChildCodes.Contains(row.MultiCodeProductCode!.Trim())
                )
                .Select(row => new StoreMultiCodeKey(productCode, row.MultiCodeProductCode!))
                .Distinct()
                .ToList();
            var missingEntries = managedSetCodes
                .Where(setCode => !existingChildCodes.Contains(setCode.SetProductCode!))
                .ToList();
            return new StoreMultiCodeGroupFixResult
            {
                StoreCode = storeCode,
                ProductCode = productCode,
                OrphanedKeys = orphanedKeys,
                MissingEntries = missingEntries,
                DeletedCount = orphanedKeys.Count,
                AddedCount = missingEntries.Count,
            };
        }

        private class StoreMultiCodeChangePlan
        {
            public string StoreCode { get; set; } = "";
            public List<StoreMultiCodeKey> OrphanedKeys { get; set; } = new();
            public int OrphanedCount { get; set; }
            public List<ProductSetCode> MissingEntries { get; set; } = new();
            public int InvalidKeyCount { get; set; }
        }

        private sealed record StoreMultiCodeKey(string ProductCode, string MultiCodeProductCode);

        private sealed class StoreMultiCodeGroupFixResult
        {
            public string StoreCode { get; init; } = "";
            public string ProductCode { get; init; } = "";
            public List<StoreMultiCodeKey> OrphanedKeys { get; init; } = new();
            public List<ProductSetCode> MissingEntries { get; init; } = new();
            public int DeletedCount { get; set; }
            public int AddedCount { get; set; }
            public string? Error { get; init; }
            public string? ErrorCode { get; init; }
        }

        private class StoreRetailPriceChangePlan
        {
            public string StoreCode { get; set; } = "";
            public List<string> OrphanedProductCodes { get; set; } = new();
            public int OrphanedCount { get; set; }
            public List<string> MissingProductCodes { get; set; } = new();
            public int InvalidKeyCount { get; set; }
        }

        #endregion
    }
}

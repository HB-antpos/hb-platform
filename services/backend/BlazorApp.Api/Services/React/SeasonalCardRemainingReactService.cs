using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    public class SeasonalCardRemainingReactService : ISeasonalCardRemainingReactService
    {
        private readonly ISqlSugarClient _db;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentUserManageableStoreScopeService _scopeService;
        private readonly ILogger<SeasonalCardRemainingReactService> _logger;

        public SeasonalCardRemainingReactService(
            SqlSugarContext context,
            ICurrentUserService currentUserService,
            ICurrentUserManageableStoreScopeService scopeService,
            ILogger<SeasonalCardRemainingReactService> logger
        )
        {
            _db = context.Db;
            _currentUserService = currentUserService;
            _scopeService = scopeService;
            _logger = logger;
        }

        public async Task<ApiResponse<List<SeasonalCardCatalogDto>>> GetCatalogAsync()
        {
            var rows = await _db.Queryable<SeasonalCardCatalog>()
                .Where(item => !item.IsDeleted && item.IsEnabled)
                .OrderBy(item => item.SortOrder)
                .ToListAsync();

            return ApiResponse<List<SeasonalCardCatalogDto>>.OK(
                rows.Select(ToCatalogDto).ToList()
            );
        }

        public async Task<ApiResponse<SeasonalCardRemainingSubmissionDto>> CreateSubmissionAsync(
            CreateSeasonalCardRemainingSubmissionDto request
        )
        {
            var validation = ValidateCreateRequest(request);
            if (!validation.Success)
            {
                return ApiResponse<SeasonalCardRemainingSubmissionDto>.Error(
                    validation.Message,
                    validation.ErrorCode
                );
            }

            var storeCode = request.StoreCode.Trim();
            var access = await ResolveManagedStoreAccessAsync(storeCode);
            if (!access.Success)
            {
                return ApiResponse<SeasonalCardRemainingSubmissionDto>.Error(
                    access.Message,
                    access.ErrorCode
                );
            }

            var store = await _db.Queryable<Store>()
                .FirstAsync(item => !item.IsDeleted && item.StoreCode == storeCode);
            if (store == null)
            {
                return ApiResponse<SeasonalCardRemainingSubmissionDto>.Error(
                    "分店不存在",
                    "STORE_NOT_FOUND"
                );
            }

            var catalog = await _db.Queryable<SeasonalCardCatalog>()
                .FirstAsync(item =>
                    !item.IsDeleted && item.CatalogGuid == request.CatalogGuid.Trim()
                );
            if (catalog == null)
            {
                return ApiResponse<SeasonalCardRemainingSubmissionDto>.Error(
                    "季节卡目录不存在",
                    "CATALOG_NOT_FOUND"
                );
            }

            if (!catalog.IsEnabled)
            {
                return ApiResponse<SeasonalCardRemainingSubmissionDto>.Error(
                    "季节卡目录未启用",
                    "CATALOG_DISABLED"
                );
            }

            var unitPriceResult = ResolveUnitPrice(catalog, request.CustomUnitPrice);
            if (!unitPriceResult.Success)
            {
                return ApiResponse<SeasonalCardRemainingSubmissionDto>.Error(
                    unitPriceResult.Message,
                    unitPriceResult.ErrorCode
                );
            }

            var now = DateTime.UtcNow;
            var submission = new SeasonalCardRemainingSubmission
            {
                SubmissionGuid = Guid.NewGuid().ToString(),
                StoreCode = storeCode,
                CatalogGuid = catalog.CatalogGuid,
                CatalogCode = catalog.CatalogCode,
                CardType = catalog.CardType,
                PriceOption = catalog.PriceOption,
                PriceLabel = catalog.PriceLabel,
                UnitPrice = unitPriceResult.UnitPrice,
                SeasonYear = request.SeasonYear,
                RemainingQuantity = request.RemainingQuantity,
                Remark = NormalizeRemark(request.Remark),
                SubmittedAt = now,
                SubmittedByUserGuid = _currentUserService.GetCurrentUserGuid(),
                SubmittedByName = _currentUserService.GetCurrentUsername(),
                CreatedAt = now,
                CreatedBy = _currentUserService.GetCurrentUsername(),
                UpdatedAt = now,
                UpdatedBy = _currentUserService.GetCurrentUsername(),
            };

            await _db.Insertable(submission).ExecuteCommandAsync();
            _logger.LogInformation(
                "季节卡剩余已提交: StoreCode={StoreCode}, CatalogGuid={CatalogGuid}, SeasonYear={SeasonYear}",
                submission.StoreCode,
                submission.CatalogGuid,
                submission.SeasonYear
            );

            return ApiResponse<SeasonalCardRemainingSubmissionDto>.OK(
                ToSubmissionDto(submission, store.StoreName),
                "季节卡剩余已提交"
            );
        }

        /// <summary>
        /// 整组提交一个 分店 + 年份 + 节日 + 供应商 的全部价格：必须覆盖该节日全部启用的价格目录项（空着按 0），
        /// 新批次整体取代旧批次（旧行保留为历史）。客户端须带上预填时看到的批次号，不一致说明已被他人更新。
        /// </summary>
        public async Task<ApiResponse<SeasonalCardBatchDto>> CreateBatchAsync(
            CreateSeasonalCardRemainingBatchDto request
        )
        {
            var validation = ValidateBatchRequest(request);
            if (!validation.Success)
            {
                return ApiResponse<SeasonalCardBatchDto>.Error(validation.Message, validation.ErrorCode);
            }

            var storeCode = request.StoreCode.Trim();
            var supplierCode = request.LocalSupplierCode.Trim();
            var access = await ResolveManagedStoreAccessAsync(storeCode);
            if (!access.Success)
            {
                return ApiResponse<SeasonalCardBatchDto>.Error(access.Message, access.ErrorCode);
            }

            var store = await _db.Queryable<Store>()
                .FirstAsync(item => !item.IsDeleted && item.StoreCode == storeCode);
            if (store == null)
            {
                return ApiResponse<SeasonalCardBatchDto>.Error("分店不存在", "STORE_NOT_FOUND");
            }

            // 名称快照以服务端查到的启用供应商为准，不信任客户端传入。
            var supplier = await _db.Queryable<HBLocalSupplier>()
                .FirstAsync(item =>
                    !item.IsDeleted && item.Status == 1 && item.LocalSupplierCode == supplierCode
                );
            if (supplier == null)
            {
                return ApiResponse<SeasonalCardBatchDto>.Error(
                    "供应商不存在或已停用",
                    "SUPPLIER_NOT_FOUND"
                );
            }

            var catalogs = await _db.Queryable<SeasonalCardCatalog>()
                .Where(item => !item.IsDeleted && item.IsEnabled && item.CardType == request.CardType)
                .OrderBy(item => item.SortOrder)
                .ToListAsync();
            var itemsByCatalog = request.Items
                .GroupBy(item => item.CatalogGuid.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var catalogGuids = catalogs
                .Select(item => item.CatalogGuid)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // 每批都必须是完整快照：恰好覆盖该节日全部启用目录项，不多不少不重复，否则统计会混入上一批的旧值。
            if (
                catalogs.Count == 0
                || itemsByCatalog.Count != request.Items.Count
                || itemsByCatalog.Count != catalogs.Count
                || itemsByCatalog.Any(group => !catalogGuids.Contains(group.Key))
            )
            {
                return ApiResponse<SeasonalCardBatchDto>.Error(
                    "提交的价格项必须恰好包含该节日全部启用的价格",
                    "BATCH_ITEMS_MISMATCH"
                );
            }

            var requestedByCatalog = request.Items.ToDictionary(
                item => item.CatalogGuid.Trim(),
                StringComparer.OrdinalIgnoreCase
            );
            var resolvedLines = new List<(SeasonalCardCatalog Catalog, int Quantity, decimal UnitPrice)>();
            foreach (var catalog in catalogs)
            {
                var item = requestedByCatalog[catalog.CatalogGuid];
                // 「其他价格」数量为 0 时允许不填单价，记为 0；有数量则必须给出大于 0 的实际单价。
                if (catalog.AllowsCustomUnitPrice && item.RemainingQuantity == 0 && !item.CustomUnitPrice.HasValue)
                {
                    resolvedLines.Add((catalog, 0, 0m));
                    continue;
                }

                var unitPriceResult = ResolveUnitPrice(catalog, item.CustomUnitPrice);
                if (!unitPriceResult.Success)
                {
                    return ApiResponse<SeasonalCardBatchDto>.Error(
                        unitPriceResult.Message,
                        unitPriceResult.ErrorCode
                    );
                }

                resolvedLines.Add((catalog, item.RemainingQuantity, unitPriceResult.UnitPrice));
            }

            var existingRows = await _db.Queryable<SeasonalCardRemainingSubmission>()
                .Where(item =>
                    !item.IsDeleted
                    && item.StoreCode == storeCode
                    && item.SeasonYear == request.SeasonYear
                    && item.CardType == request.CardType
                    && item.LocalSupplierCode == supplierCode
                )
                .ToListAsync();
            var currentBatch = SeasonalCardBatchResolver.FindLatestBatch(existingRows);
            var currentBatchGuid = currentBatch == null
                ? null
                : SeasonalCardBatchResolver.BatchKey(currentBatch[0]);
            var expectedBatchGuid = string.IsNullOrWhiteSpace(request.ExpectedPreviousBatchGuid)
                ? null
                : request.ExpectedPreviousBatchGuid.Trim();

            // 乐观并发：客户端预填后有人抢先提交，拒绝并把最新批次带回去，让客户端刷新「上次 → 本次」对比。
            if (!string.Equals(currentBatchGuid, expectedBatchGuid, StringComparison.OrdinalIgnoreCase))
            {
                return ApiResponse<SeasonalCardBatchDto>.Error(
                    "该节日的填报已被其他人更新，请刷新后再提交",
                    "SEASONAL_CARD_STALE",
                    currentBatch == null
                        ? null
                        : SeasonalCardBatchResolver.ToBatchDto(currentBatch, store.StoreName, true)
                );
            }

            // 与当前生效批次完全相同则不生成新记录，避免刷新「最后提交时间」造成重新盘点过的假象。
            if (currentBatch != null && IsSameAsBatch(currentBatch, resolvedLines))
            {
                return ApiResponse<SeasonalCardBatchDto>.Error(
                    "数量与上次填报相同，无需重复提交",
                    "SEASONAL_CARD_NO_CHANGES"
                );
            }

            var now = DateTime.UtcNow;
            var batchGuid = Guid.NewGuid().ToString();
            var userGuid = _currentUserService.GetCurrentUserGuid();
            var username = _currentUserService.GetCurrentUsername();
            var remark = NormalizeRemark(request.Remark);
            var rows = resolvedLines
                .Select(line => new SeasonalCardRemainingSubmission
                {
                    SubmissionGuid = Guid.NewGuid().ToString(),
                    StoreCode = storeCode,
                    CatalogGuid = line.Catalog.CatalogGuid,
                    CatalogCode = line.Catalog.CatalogCode,
                    CardType = line.Catalog.CardType,
                    PriceOption = line.Catalog.PriceOption,
                    PriceLabel = line.Catalog.PriceLabel,
                    UnitPrice = line.UnitPrice,
                    SeasonYear = request.SeasonYear,
                    RemainingQuantity = line.Quantity,
                    Remark = remark,
                    SubmittedAt = now,
                    SubmittedByUserGuid = userGuid,
                    SubmittedByName = username,
                    LocalSupplierCode = supplier.LocalSupplierCode,
                    SupplierName = supplier.Name,
                    BatchGuid = batchGuid,
                    CreatedAt = now,
                    CreatedBy = username,
                    UpdatedAt = now,
                    UpdatedBy = username,
                })
                .ToList();

            // 整批同一事务写入，不会出现只覆盖了部分价格的半批数据。
            await _db.Ado.BeginTranAsync();
            try
            {
                await _db.Insertable(rows).ExecuteCommandAsync();
                await _db.Ado.CommitTranAsync();
            }
            catch
            {
                await _db.Ado.RollbackTranAsync();
                throw;
            }

            _logger.LogInformation(
                "季节卡剩余已整组提交: StoreCode={StoreCode}, SeasonYear={SeasonYear}, CardType={CardType}, Supplier={Supplier}, BatchGuid={BatchGuid}, Replaced={Replaced}",
                storeCode,
                request.SeasonYear,
                request.CardType,
                supplier.LocalSupplierCode,
                batchGuid,
                currentBatchGuid
            );

            return ApiResponse<SeasonalCardBatchDto>.OK(
                SeasonalCardBatchResolver.ToBatchDto(rows, store.StoreName, true),
                currentBatch == null ? "季节卡剩余已提交" : "已覆盖上次填报"
            );
        }

        /// <summary>
        /// 填报页总览：某分店 + 年份 + 供应商下，五个节日各自当前生效的批次（节日网格的已填/待填与预填数量）。
        /// </summary>
        public async Task<ApiResponse<SeasonalCardOverviewDto>> GetOverviewAsync(
            SeasonalCardOverviewQueryDto query
        )
        {
            if (string.IsNullOrWhiteSpace(query.StoreCode))
            {
                return ApiResponse<SeasonalCardOverviewDto>.Error("分店代码不能为空", "STORE_CODE_REQUIRED");
            }

            if (query.SeasonYear <= 0)
            {
                return ApiResponse<SeasonalCardOverviewDto>.Error("季节年份必须大于 0", "INVALID_SEASON_YEAR");
            }

            if (string.IsNullOrWhiteSpace(query.LocalSupplierCode))
            {
                return ApiResponse<SeasonalCardOverviewDto>.Error("请选择供应商", "SUPPLIER_REQUIRED");
            }

            var storeCode = query.StoreCode.Trim();
            var supplierCode = query.LocalSupplierCode.Trim();
            var access = await ResolveManagedStoreAccessAsync(storeCode);
            if (!access.Success)
            {
                return ApiResponse<SeasonalCardOverviewDto>.Error(access.Message, access.ErrorCode);
            }

            var storeName = await _db.Queryable<Store>()
                .Where(item => !item.IsDeleted && item.StoreCode == storeCode)
                .Select(item => item.StoreName)
                .FirstAsync();
            var supplierName = await _db.Queryable<HBLocalSupplier>()
                .Where(item => !item.IsDeleted && item.LocalSupplierCode == supplierCode)
                .Select(item => item.Name)
                .FirstAsync();
            var rows = await _db.Queryable<SeasonalCardRemainingSubmission>()
                .Where(item =>
                    !item.IsDeleted
                    && item.StoreCode == storeCode
                    && item.SeasonYear == query.SeasonYear
                    && item.LocalSupplierCode == supplierCode
                )
                .ToListAsync();

            var rowsByType = rows.ToLookup(item => item.CardType);
            return ApiResponse<SeasonalCardOverviewDto>.OK(new SeasonalCardOverviewDto
            {
                StoreCode = storeCode,
                SeasonYear = query.SeasonYear,
                LocalSupplierCode = supplierCode,
                SupplierName = supplierName,
                Holidays = Enum.GetValues<SeasonalCardType>()
                    .Select(cardType =>
                    {
                        var latest = SeasonalCardBatchResolver.FindLatestBatch(rowsByType[cardType]);
                        return new SeasonalCardOverviewHolidayDto
                        {
                            CardType = cardType,
                            CardTypeName = SeasonalCardCatalogSeedData.GetCardTypeName(cardType),
                            CurrentBatch = latest == null
                                ? null
                                : SeasonalCardBatchResolver.ToBatchDto(latest, storeName, true),
                        };
                    })
                    .ToList(),
            });
        }

        public async Task<ApiResponse<PagedResult<SeasonalCardRemainingSubmissionDto>>> GetSubmissionsAsync(
            SeasonalCardRemainingSubmissionQueryDto query
        )
        {
            var access = await ResolveManagedStoreAccessAsync(query.StoreCode);
            if (!access.Success)
            {
                return ApiResponse<PagedResult<SeasonalCardRemainingSubmissionDto>>.Error(
                    access.Message,
                    access.ErrorCode
                );
            }

            var pageNumber = query.PageNumber <= 0 ? 1 : query.PageNumber;
            var pageSize = query.PageSize <= 0 ? 20 : Math.Min(query.PageSize, 200);
            var storeCode = query.StoreCode?.Trim();
            var supplierCode = query.LocalSupplierCode?.Trim();

            var submissions = _db.Queryable<SeasonalCardRemainingSubmission>()
                .Where(item => !item.IsDeleted)
                .WhereIF(!string.IsNullOrWhiteSpace(storeCode), item => item.StoreCode == storeCode!)
                .WhereIF(
                    string.IsNullOrWhiteSpace(storeCode) && access.StoreCodes.Count > 0,
                    item => access.StoreCodes.Contains(item.StoreCode)
                )
                .WhereIF(query.CardType.HasValue, item => item.CardType == query.CardType!.Value)
                .WhereIF(query.SeasonYear.HasValue, item => item.SeasonYear == query.SeasonYear!.Value)
                .WhereIF(!string.IsNullOrWhiteSpace(supplierCode), item => item.LocalSupplierCode == supplierCode!);

            var total = await submissions.CountAsync();
            var rows = await submissions
                .OrderByDescending(item => item.SubmittedAt)
                .ToPageListAsync(pageNumber, pageSize);

            var storeNameByCode = await GetStoreNameByCodeAsync(rows.Select(item => item.StoreCode));

            return ApiResponse<PagedResult<SeasonalCardRemainingSubmissionDto>>.OK(
                new PagedResult<SeasonalCardRemainingSubmissionDto>
                {
                    Items = rows
                        .Select(item =>
                            ToSubmissionDto(
                                item,
                                storeNameByCode.GetValueOrDefault(item.StoreCode)
                            )
                        )
                        .ToList(),
                    Total = total,
                    Page = pageNumber,
                    PageSize = pageSize,
                }
            );
        }

        public async Task<ApiResponse<SeasonalCardRemainingSubmissionDto>> GetSubmissionByGuidAsync(
            string submissionGuid
        )
        {
            if (string.IsNullOrWhiteSpace(submissionGuid))
            {
                return ApiResponse<SeasonalCardRemainingSubmissionDto>.Error(
                    "提交编号不能为空",
                    "SUBMISSION_GUID_REQUIRED"
                );
            }

            var submission = await _db.Queryable<SeasonalCardRemainingSubmission>()
                .FirstAsync(item =>
                    !item.IsDeleted && item.SubmissionGuid == submissionGuid.Trim()
                );
            if (submission == null)
            {
                return ApiResponse<SeasonalCardRemainingSubmissionDto>.Error(
                    "季节卡剩余提交不存在",
                    "NOT_FOUND"
                );
            }

            var access = await ResolveManagedStoreAccessAsync(submission.StoreCode);
            if (!access.Success)
            {
                return ApiResponse<SeasonalCardRemainingSubmissionDto>.Error(
                    access.Message,
                    access.ErrorCode
                );
            }

            var storeName = await _db.Queryable<Store>()
                .Where(item => !item.IsDeleted && item.StoreCode == submission.StoreCode)
                .Select(item => item.StoreName)
                .FirstAsync();

            return ApiResponse<SeasonalCardRemainingSubmissionDto>.OK(
                ToSubmissionDto(submission, storeName)
            );
        }

        private async Task<Dictionary<string, string>> GetStoreNameByCodeAsync(
            IEnumerable<string> storeCodes
        )
        {
            var codes = storeCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!codes.Any())
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var stores = await _db.Queryable<Store>()
                .Where(item => !item.IsDeleted && codes.Contains(item.StoreCode))
                .Select(item => new { item.StoreCode, item.StoreName })
                .ToListAsync();

            return stores.ToDictionary(
                item => item.StoreCode,
                item => item.StoreName,
                StringComparer.OrdinalIgnoreCase
            );
        }

        private static ValidationResult ValidateCreateRequest(
            CreateSeasonalCardRemainingSubmissionDto request
        )
        {
            if (string.IsNullOrWhiteSpace(request.StoreCode))
            {
                return ValidationResult.Error("分店代码不能为空", "STORE_CODE_REQUIRED");
            }

            if (string.IsNullOrWhiteSpace(request.CatalogGuid))
            {
                return ValidationResult.Error("目录编号不能为空", "CATALOG_GUID_REQUIRED");
            }

            if (request.SeasonYear <= 0)
            {
                return ValidationResult.Error("季节年份必须大于 0", "INVALID_SEASON_YEAR");
            }

            if (request.RemainingQuantity < 0)
            {
                return ValidationResult.Error("剩余数量不能为负数", "INVALID_QUANTITY");
            }

            return ValidationResult.OK();
        }

        private static UnitPriceResult ResolveUnitPrice(
            SeasonalCardCatalog catalog,
            decimal? customUnitPrice
        )
        {
            if (!catalog.AllowsCustomUnitPrice)
            {
                if (customUnitPrice.HasValue)
                {
                    return UnitPriceResult.Error(
                        "固定价格目录项不接受覆盖价格",
                        "FIXED_PRICE_OVERRIDE_NOT_ALLOWED"
                    );
                }

                if (!catalog.FixedUnitPrice.HasValue || catalog.FixedUnitPrice.Value <= 0)
                {
                    return UnitPriceResult.Error("固定目录价格无效", "INVALID_FIXED_PRICE");
                }

                return UnitPriceResult.OK(catalog.FixedUnitPrice.Value);
            }

            if (!customUnitPrice.HasValue)
            {
                return UnitPriceResult.Error(
                    "其他价格目录必须提交大于 0 的实际售价",
                    "CUSTOM_PRICE_REQUIRED"
                );
            }

            var roundedUnitPrice = decimal.Round(customUnitPrice.Value, 2);
            if (roundedUnitPrice <= 0)
            {
                return UnitPriceResult.Error(
                    "其他价格目录必须提交大于 0 的实际售价",
                    "CUSTOM_PRICE_REQUIRED"
                );
            }

            return UnitPriceResult.OK(roundedUnitPrice);
        }

        private async Task<StoreAccessResult> ResolveManagedStoreAccessAsync(string? requestedStoreCode)
        {
            var scope = await _scopeService.GetScopeAsync();
            if (!scope.IsAllowed)
            {
                return StoreAccessResult.Forbidden(scope.Message);
            }

            var requested = requestedStoreCode?.Trim();
            if (!string.IsNullOrWhiteSpace(requested) && !scope.CanAccessStoreCode(requested))
            {
                return StoreAccessResult.Forbidden("没有权限访问该分店", "FORBIDDEN_STORE");
            }

            return StoreAccessResult.Allowed(
                string.IsNullOrWhiteSpace(requested)
                    ? scope.StoreCodes.ToList()
                    : new List<string> { requested }
            );
        }

        private static string? NormalizeRemark(string? remark)
        {
            return string.IsNullOrWhiteSpace(remark) ? null : remark.Trim();
        }

        private static SeasonalCardCatalogDto ToCatalogDto(SeasonalCardCatalog item) => new()
        {
            CatalogGuid = item.CatalogGuid,
            CatalogCode = item.CatalogCode,
            CardType = item.CardType,
            CardTypeName = SeasonalCardCatalogSeedData.GetCardTypeName(item.CardType),
            PriceOption = item.PriceOption,
            PriceOptionName = SeasonalCardCatalogSeedData.GetPriceOptionName(item.PriceOption),
            PriceLabel = item.PriceLabel,
            FixedUnitPrice = item.FixedUnitPrice,
            AllowsCustomUnitPrice = item.AllowsCustomUnitPrice,
            IsEnabled = item.IsEnabled,
            SortOrder = item.SortOrder,
        };

        private static SeasonalCardRemainingSubmissionDto ToSubmissionDto(
            SeasonalCardRemainingSubmission item,
            string? storeName
        ) => new()
        {
            SubmissionGuid = item.SubmissionGuid,
            StoreCode = item.StoreCode,
            StoreName = storeName,
            CatalogGuid = item.CatalogGuid,
            CatalogCode = item.CatalogCode,
            CardType = item.CardType,
            CardTypeName = SeasonalCardCatalogSeedData.GetCardTypeName(item.CardType),
            PriceOption = item.PriceOption,
            PriceOptionName = SeasonalCardCatalogSeedData.GetPriceOptionName(item.PriceOption),
            PriceLabel = item.PriceLabel,
            SeasonYear = item.SeasonYear,
            RemainingQuantity = item.RemainingQuantity,
            UnitPrice = item.UnitPrice,
            Remark = item.Remark,
            SubmittedByUserGuid = item.SubmittedByUserGuid,
            SubmittedByName = item.SubmittedByName,
            SubmittedAt = item.SubmittedAt,
            LocalSupplierCode = item.LocalSupplierCode,
            SupplierName = item.SupplierName,
            BatchGuid = item.BatchGuid,
        };

        private static bool IsSameAsBatch(
            IReadOnlyCollection<SeasonalCardRemainingSubmission> currentBatch,
            IReadOnlyCollection<(SeasonalCardCatalog Catalog, int Quantity, decimal UnitPrice)> resolvedLines
        )
        {
            if (currentBatch.Count != resolvedLines.Count)
            {
                return false;
            }

            var current = currentBatch.ToDictionary(
                row => row.CatalogGuid,
                row => (row.RemainingQuantity, row.UnitPrice),
                StringComparer.OrdinalIgnoreCase
            );
            return resolvedLines.All(line =>
                current.TryGetValue(line.Catalog.CatalogGuid, out var previous)
                && previous.RemainingQuantity == line.Quantity
                && previous.UnitPrice == line.UnitPrice
            );
        }

        private static ValidationResult ValidateBatchRequest(CreateSeasonalCardRemainingBatchDto request)
        {
            if (string.IsNullOrWhiteSpace(request.StoreCode))
            {
                return ValidationResult.Error("分店代码不能为空", "STORE_CODE_REQUIRED");
            }

            if (request.SeasonYear <= 0)
            {
                return ValidationResult.Error("季节年份必须大于 0", "INVALID_SEASON_YEAR");
            }

            if (!Enum.IsDefined(request.CardType))
            {
                return ValidationResult.Error("节日无效", "INVALID_CARD_TYPE");
            }

            if (string.IsNullOrWhiteSpace(request.LocalSupplierCode))
            {
                return ValidationResult.Error("请选择供应商", "SUPPLIER_REQUIRED");
            }

            if (request.Items == null || request.Items.Count == 0)
            {
                return ValidationResult.Error("请填写各价格的剩余数量", "BATCH_ITEMS_REQUIRED");
            }

            if (request.Items.Any(item => string.IsNullOrWhiteSpace(item.CatalogGuid)))
            {
                return ValidationResult.Error("目录编号不能为空", "CATALOG_GUID_REQUIRED");
            }

            if (request.Items.Any(item => item.RemainingQuantity < 0))
            {
                return ValidationResult.Error("剩余数量不能为负数", "INVALID_QUANTITY");
            }

            return ValidationResult.OK();
        }

        private sealed class StoreAccessResult
        {
            public bool Success { get; private init; }
            public string Message { get; private init; } = string.Empty;
            public string? ErrorCode { get; private init; }
            public List<string> StoreCodes { get; private init; } = new();

            public static StoreAccessResult Allowed(List<string> storeCodes) => new()
            {
                Success = true,
                StoreCodes = storeCodes,
            };

            public static StoreAccessResult Forbidden(
                string message,
                string errorCode = "FORBIDDEN"
            ) => new()
            {
                Success = false,
                Message = message,
                ErrorCode = errorCode,
            };
        }

        private sealed class ValidationResult
        {
            public bool Success { get; private init; }
            public string Message { get; private init; } = string.Empty;
            public string? ErrorCode { get; private init; }

            public static ValidationResult OK() => new() { Success = true };

            public static ValidationResult Error(string message, string errorCode) => new()
            {
                Success = false,
                Message = message,
                ErrorCode = errorCode,
            };
        }

        private sealed class UnitPriceResult
        {
            public bool Success { get; private init; }
            public string Message { get; private init; } = string.Empty;
            public string? ErrorCode { get; private init; }
            public decimal UnitPrice { get; private init; }

            public static UnitPriceResult OK(decimal unitPrice) => new()
            {
                Success = true,
                UnitPrice = unitPrice,
            };

            public static UnitPriceResult Error(string message, string errorCode) => new()
            {
                Success = false,
                Message = message,
                ErrorCode = errorCode,
            };
        }
    }
}

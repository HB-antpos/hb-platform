using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.Logging;
using SetChildPurchasePriceMutationLock = BlazorApp.Api.Services.ProductCosts.ProductCostMutationLock;
using SetChildPurchasePriceService = BlazorApp.Api.Services.ProductCosts.ProductCostRecalculationService;

namespace BlazorApp.Api.Features.LocalSupplierInvoices
{
    /// <summary>唯一持有批量执行事务、业务锁、锁内复读和写命令的边界。</summary>
    internal sealed class LocalSupplierInvoicesProductExecutionCommandWriter
    {
        private readonly LocalSupplierInvoicesDependencies _dependencies;
        private readonly LocalSupplierInvoicesProductExecutionSource _source;
        private readonly LocalSupplierInvoicesProductExecutionRequestValidator _validator;
        private readonly LocalSupplierInvoicesProductExecutionStore _store;

        public LocalSupplierInvoicesProductExecutionCommandWriter(
            LocalSupplierInvoicesDependencies dependencies,
            LocalSupplierInvoicesProductExecutionSource source,
            LocalSupplierInvoicesProductExecutionRequestValidator validator
        )
        {
            _dependencies = dependencies;
            _source = source;
            _validator = validator;
            _store = new LocalSupplierInvoicesProductExecutionStore(dependencies);
        }

        public async Task<ProductExecutionCommandResult> ExecuteAsync(
            LocalSupplierInvoicesProductExecutionPlan plan
        )
        {
            var db = _dependencies.Context.Db;
            var accumulator = new LocalSupplierInvoicesProductExecutionResultAccumulator();
            await db.Ado.BeginTranAsync();
            try
            {
                // 新建商品必须走总闸；改货号会改变主档身份，使用身份互斥锁防止同一商品并发改码。
                // 纯价格和多码写入仍使用 Shared，避免无关成本操作互相阻塞。
                var requiresProductIdentityLock = plan.InitialData.Details.Any(detail =>
                    LocalSupplierInvoicesProductExecutionPlan.GetSavedAction(detail)
                    == DetailAction.UpdateItemNumber
                );
                var lockScope = plan.RequiresAllProductsLock
                    ? await SetChildPurchasePriceMutationLock.AcquireAllAsync(db)
                    : plan.InitialProductCodes.Count > 0
                        ? requiresProductIdentityLock
                            ? await SetChildPurchasePriceMutationLock.AcquireProductIdentitiesWithinBudgetAsync(
                                db,
                                plan.InitialProductCodes
                            )
                            : await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                                db,
                                plan.InitialProductCodes
                            )
                        : null;

                // 锁内重新读取所有执行身份和写入来源，禁止使用等待锁前的快照作决定。
                var lockedData = await _source.ReadLockedAsync(plan.Request);
                if (!plan.TryValidateLockedData(lockedData, out var snapshotError))
                {
                    await db.Ado.RollbackTranAsync();
                    return new ProductExecutionCommandResult(
                        accumulator.Result,
                        snapshotError,
                        "VALIDATION_ERROR"
                    );
                }

                var productCodes = LocalSupplierInvoicesProductExecutionPlan.NormalizeProductCodes(
                    lockedData.Details
                );
                lockScope?.EnsureCovers(db, productCodes);

                // 锁内识别「新建商品」里商品已建好的行：校验不报「已存在」，执行时跳过并标记已执行。
                var alreadyCreatedDetailGuids = await _source.FindAlreadyCreatedProductDetailGuidsAsync(
                    lockedData.Details,
                    lockedData.Header?.SupplierCode
                );
                var validationErrors = await _validator.ValidateLockedDetailsAsync(
                    lockedData,
                    plan.Request.ProductTypes,
                    alreadyCreatedDetailGuids
                );
                validationErrors.AddRange(plan.Request.ProductTypeSelectionErrors);
                if (validationErrors.Count > 0)
                {
                    accumulator.Result.Failed = validationErrors.Count;
                    accumulator.Result.Errors.AddRange(validationErrors);
                    await db.Ado.RollbackTranAsync();
                    return new ProductExecutionCommandResult(
                        accumulator.Result,
                        "批量执行校验失败",
                        "VALIDATION_ERROR"
                    );
                }

                // 历史审计与主档写入必须处于同一事务，保证任意失败整笔回滚。
                var beforeSnapshots = await _dependencies.ChangeHistoryService.CaptureSnapshotsAsync(productCodes);
                var groups = LocalSupplierInvoicesProductExecutionPlan.GroupBySavedAction(lockedData.Details);
                await ExecuteGroupsAsync(groups, lockedData, plan.Request, accumulator, alreadyCreatedDetailGuids);
                if (accumulator.Result.Failed > 0)
                {
                    await db.Ado.RollbackTranAsync();
                    return new ProductExecutionCommandResult(
                        accumulator.Result,
                        "批量执行失败，已回滚",
                        "BATCH_EXECUTE_ERROR"
                    );
                }

                if (groups.TryGetValue(DetailAction.None, out var none)) accumulator.AddSkipped(none.Count);
                if (groups.TryGetValue(DetailAction.WaitForOperation, out var waiting)) accumulator.AddSkipped(waiting.Count);
                if (accumulator.SuccessfulDetailGuids.Count > 0)
                    await _store.BatchUpdateDetailActivityTypeAsync(
                        accumulator.SuccessfulDetailGuids,
                        plan.Request.UserName
                    );

                var recalculationCodes = SetChildPurchasePriceMutationLock.NormalizeProductCodes(
                    accumulator.ChangedProductCodes
                );
                var repairedRelationCount = 0;
                if (lockScope != null && recalculationCodes.Count > 0)
                {
                    var costWriteback = new SetChildPurchasePriceService(db);
                    var repairFailures = new List<string>();
                    try
                    {
                        // 与「更新到分店」、HQ 同步一致：严格重算前先自动补齐门店缺失的套装/多码子项，
                        // 否则任一分店缺一条子项（如 1042 缺 G114109 的子项）都会让整单回滚。
                        // 补齐范围与下面重算的候选组同口径（活跃门店子项 ∪ 活跃分店价），只新增缺失行；
                        // 额外子项、停用墓碑等补不了的商品只记录原因，是否回滚仍由严格重算决定。
                        var parentPurchasePrices = await _source.LoadSetParentPurchasePricesAsync(
                            recalculationCodes
                        );
                        if (parentPurchasePrices.Count > 0)
                        {
                            var repair = await costWriteback.RepairMissingStoreRelationsLockedAsync(
                                lockScope,
                                parentPurchasePrices,
                                plan.Request.UserName
                            );
                            repairedRelationCount = repair.AutoRepairedRelationCount;
                            repairFailures.AddRange(
                                repair.Failures.Values
                                    .OrderBy(failure => failure.ProductCode, StringComparer.OrdinalIgnoreCase)
                                    .Select(failure =>
                                        $"{failure.ProductCode} / 分店 {failure.StoreCode ?? "全部"}: {failure.Message}"
                                    )
                            );
                        }

                        await costWriteback.RecalculateLockedAsync(
                            lockScope,
                            recalculationCodes,
                            storeCodes: null,
                            plan.Request.UserName
                        );
                    }
                    catch (InvalidOperationException ex)
                        when (!SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    {
                        // 套装数据补不齐时整单回滚，并把涉及的明细与原因返回前端，不再只显示「批量执行失败」。
                        await db.Ado.RollbackTranAsync();
                        _dependencies.Logger.LogWarning(
                            ex,
                            "批量执行套装成本重算失败 InvoiceGuid={InvoiceGuid}",
                            plan.Request.InvoiceGuid
                        );
                        var (message, failedCount, errors) = BuildSetCostFailure(
                            ex.Message,
                            repairFailures,
                            lockedData.Details
                        );
                        accumulator.MarkRolledBack(failedCount, errors);
                        return new ProductExecutionCommandResult(
                            accumulator.Result,
                            message,
                            "SET_CHILD_COST_RECALCULATION_INCOMPLETE"
                        );
                    }
                }

                if (accumulator.ChangedProductCodes.Count > 0)
                {
                    var afterSnapshots = await _dependencies.ChangeHistoryService.CaptureSnapshotsAsync(
                        accumulator.ChangedProductCodes
                    );
                    await _dependencies.ChangeHistoryService.RecordChangesAsync(
                        beforeSnapshots,
                        afterSnapshots,
                        new WarehouseProductChangeHistoryContextDto
                        {
                            Action = "BatchUpdate",
                            Source = "LocalSupplierInvoice",
                            SourceReference = plan.Request.InvoiceGuid,
                            BatchGuid = Guid.NewGuid(),
                            ActorName = plan.Request.UserName,
                            OccurredAtUtc = DateTime.UtcNow,
                        }
                    );
                }

                await db.Ado.CommitTranAsync();
                if (repairedRelationCount > 0)
                {
                    _dependencies.Logger.LogInformation(
                        "批量执行已提交门店套装子项补齐 InvoiceGuid={InvoiceGuid} RepairedRelationCount={RepairedRelationCount}",
                        plan.Request.InvoiceGuid,
                        repairedRelationCount
                    );
                }
                return new ProductExecutionCommandResult(accumulator.Result);
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                throw;
            }
        }

        /// <summary>
        /// 把套装重算失败原因映射回本单明细：标题列出涉及的货号，明细逐条给出原因，方便定位到具体行。
        /// 商品编码按完整词匹配（前后不是字母数字），避免短编码误命中长编码。
        /// </summary>
        private static (string Message, int FailedCount, List<string> Errors) BuildSetCostFailure(
            string reason,
            IReadOnlyCollection<string> repairFailures,
            IEnumerable<BlazorApp.Shared.Models.StoreLocalSupplierInvoiceDetails> details
        )
        {
            var reasonText = string.Join("\n", repairFailures.Prepend(reason));
            var involved = details
                .Where(detail => !string.IsNullOrWhiteSpace(detail.ProductCode))
                .Where(detail => System.Text.RegularExpressions.Regex.IsMatch(
                    reasonText,
                    $"(?<![A-Za-z0-9]){System.Text.RegularExpressions.Regex.Escape(detail.ProductCode!.Trim())}(?![A-Za-z0-9])",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                ))
                .GroupBy(detail => detail.ProductCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            var errors = involved
                .Select(detail =>
                    $"货号 {detail.ItemNumber ?? "--"} · {detail.ProductName ?? "--"}（{detail.ProductCode!.Trim()}）的套装子项无法自动补齐，可先把该行改为不操作再执行其余行"
                )
                .ToList();
            errors.Add(reason);
            errors.AddRange(repairFailures);

            var itemLabels = involved
                .Select(detail => $"{detail.ItemNumber ?? "--"}（{detail.ProductCode!.Trim()}）")
                .ToList();
            var message = itemLabels.Count == 0
                ? "套装成本重算失败，已整单回滚"
                : $"套装成本重算失败，已整单回滚：货号 {string.Join("、", itemLabels.Take(3))}"
                    + (itemLabels.Count > 3 ? $" 等 {itemLabels.Count} 个商品" : string.Empty);
            return (message, Math.Max(1, involved.Count), errors);
        }

        private async Task ExecuteGroupsAsync(
            IReadOnlyDictionary<DetailAction, List<BlazorApp.Shared.Models.StoreLocalSupplierInvoiceDetails>> groups,
            ProductExecutionSourceData data,
            ProductExecutionRequest request,
            LocalSupplierInvoicesProductExecutionResultAccumulator accumulator,
            IReadOnlySet<string> alreadyCreatedDetailGuids
        )
        {
            if (groups.TryGetValue(DetailAction.CreateProduct, out var createGroup))
            {
                accumulator.MarkAlreadyDone(createGroup
                    .Where(detail => alreadyCreatedDetailGuids.Contains(detail.DetailGUID))
                    .Select(detail => detail.DetailGUID));
                var create = createGroup.Where(detail => !alreadyCreatedDetailGuids.Contains(detail.DetailGUID)).ToList();
                if (create.Count > 0)
                    accumulator.Apply(
                        DetailAction.CreateProduct,
                        await _store.BatchCreateProductsAsync(
                            create,
                            data.Header!,
                            request.UserName,
                            request.ProductTypes
                        )
                    );
            }
            if (groups.TryGetValue(DetailAction.UpdatePurchasePrice, out var prices))
                accumulator.Apply(
                    DetailAction.UpdatePurchasePrice,
                    await _store.BatchUpdatePurchasePriceAsync(prices, request.UserName)
                );
            if (groups.TryGetValue(DetailAction.UpdateItemNumber, out var itemNumbers))
                accumulator.Apply(
                    DetailAction.UpdateItemNumber,
                    await _store.BatchUpdateItemNumberAsync(
                        itemNumbers,
                        data.ProductItemNumbers,
                        request.UserName
                    )
                );
            if (groups.TryGetValue(DetailAction.AddMultiCode, out var multiCodes))
                accumulator.Apply(
                    DetailAction.AddMultiCode,
                    await _store.BatchAddMultiCodesAsync(multiCodes, data.Header!, request.UserName)
                );
        }
    }
}

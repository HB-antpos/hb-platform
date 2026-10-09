using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Features.LocalSupplierInvoices
{
    /// <summary>请求契约、确认快照与锁内业务规则校验，不直接发出 SQL。</summary>
    internal sealed class LocalSupplierInvoicesProductExecutionRequestValidator
    {
        private readonly LocalSupplierInvoicesProductExecutionSource _source;

        public LocalSupplierInvoicesProductExecutionRequestValidator(
            LocalSupplierInvoicesProductExecutionSource source
        ) => _source = source;

        public bool TryCreateRequest(
            string invoiceGuid,
            IEnumerable<string>? detailGuids,
            string userName,
            IEnumerable<BatchExecuteNewProductProductTypeSelectionDto>? productTypeSelections,
            IReadOnlyCollection<BatchExecuteExpectedActionDto>? expectedActions,
            IReadOnlyCollection<StoreLocalSupplierInvoiceDetails>? confirmedDetails,
            out ProductExecutionRequest? request,
            out string? error
        )
        {
            var selectedDetailGuids = detailGuids?
                .Where(guid => !string.IsNullOrWhiteSpace(guid))
                .Distinct()
                .ToList() ?? new();
            if (selectedDetailGuids.Count == 0)
            {
                request = null;
                error = "请选择要执行的明细";
                return false;
            }

            if (!TryBuildActionSnapshot(expectedActions, out var confirmedActions, out error)
                || !MatchesSelection(selectedDetailGuids, confirmedActions, "批量执行确认已失效：确认动作与选中明细不一致，请刷新后重试", out error)
                || !TryBuildDetailSnapshot(confirmedDetails, out var confirmedDetailIdentities, out error)
                || !MatchesSelection(selectedDetailGuids, confirmedDetailIdentities, "批量执行确认已失效：确认明细与选中明细不一致，请刷新后重试", out error))
            {
                request = null;
                return false;
            }

            request = new ProductExecutionRequest(
                invoiceGuid,
                selectedDetailGuids,
                userName,
                BuildProductTypeSelectionMap(productTypeSelections),
                ValidateProductTypeSelectionContract(productTypeSelections),
                confirmedActions,
                confirmedDetailIdentities
            );
            error = null;
            return true;
        }

        public async Task<List<string>> ValidateLockedDetailsAsync(
            ProductExecutionSourceData data,
            IReadOnlyDictionary<string, int> productTypes,
            IReadOnlySet<string>? alreadyCreatedDetailGuids = null
        )
        {
            var errors = new List<string>();
            var createItemNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var createBarcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var multiCodeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var detail in data.Details)
            {
                if (!detail.ActivityType.HasValue)
                {
                    errors.Add($"明细 {detail.DetailGUID} 未设置操作类型，请先执行商品检测或手动设置操作类型");
                    continue;
                }
                var actionValue = detail.ActivityType.Value;
                if (actionValue == 99) continue;
                if (!Enum.IsDefined(typeof(DetailAction), actionValue))
                {
                    errors.Add($"明细 {detail.DetailGUID} 操作类型无效：{actionValue}");
                    continue;
                }

                switch ((DetailAction)actionValue)
                {
                    case DetailAction.None:
                    case DetailAction.WaitForOperation:
                        break;
                    case DetailAction.CreateProduct:
                        // 商品已建好的行视为已执行（写入时跳过），不再按「新建」校验货号条码是否已存在。
                        if (alreadyCreatedDetailGuids?.Contains(detail.DetailGUID) == true) break;
                        await ValidateCreateProductAsync(detail, data.Header!, productTypes, createItemNumbers, createBarcodes, errors);
                        break;
                    case DetailAction.UpdatePurchasePrice:
                        await ValidatePurchasePriceUpdateAsync(detail, errors);
                        break;
                    case DetailAction.UpdateItemNumber:
                        await ValidateItemNumberUpdateAsync(detail, errors);
                        break;
                    case DetailAction.AddMultiCode:
                        await ValidateMultiCodeAsync(detail, data.Header!, multiCodeKeys, errors);
                        break;
                }
            }
            return errors;
        }

        private async Task ValidateCreateProductAsync(
            StoreLocalSupplierInvoiceDetails detail,
            StoreLocalSupplierInvoice header,
            IReadOnlyDictionary<string, int> productTypes,
            HashSet<string> itemNumbers,
            HashSet<string> barcodes,
            List<string> errors
        )
        {
            if (string.IsNullOrWhiteSpace(detail.ItemNumber)) errors.Add($"明细 {detail.DetailGUID} 新建商品失败：货号不能为空");
            if (string.IsNullOrWhiteSpace(detail.Barcode)) errors.Add($"明细 {detail.DetailGUID} 新建商品失败：条码不能为空");
            if (detail.PurchasePrice == null || detail.PurchasePrice <= 0) errors.Add($"明细 {detail.DetailGUID} 新建商品失败：进货价必须大于0");
            if (!string.IsNullOrWhiteSpace(detail.ItemNumber) && !itemNumbers.Add(detail.ItemNumber.Trim())) errors.Add($"明细 {detail.DetailGUID} 新建商品失败：本次执行内货号重复");
            if (!string.IsNullOrWhiteSpace(detail.Barcode) && !barcodes.Add(detail.Barcode.Trim())) errors.Add($"明细 {detail.DetailGUID} 新建商品失败：本次执行内条码重复");

            var additionalBarcodes = LocalSupplierInvoicesBarcodeRules.DeserializeAdditionalBarcodes(detail.AdditionalBarcodesJson);
            if (additionalBarcodes.Count > 0)
            {
                if (!productTypes.TryGetValue(detail.DetailGUID, out var productType))
                    errors.Add($"明细 {detail.DetailGUID} 新建商品失败：有副码时必须选择商品类型");
                else if (productType is not 1 and not 2)
                    errors.Add($"明细 {detail.DetailGUID} 新建商品失败：商品类型只能是套装或多码");

                foreach (var barcode in additionalBarcodes)
                {
                    var normalized = NormalizeCaseInsensitive(barcode);
                    if (normalized == null) continue;
                    if (!barcodes.Add(barcode.Trim())) errors.Add($"明细 {detail.DetailGUID} 新建商品失败：本次执行内副码重复 {barcode}");
                    if (await _source.HasProductBarcodeAsync(normalized)) errors.Add($"明细 {detail.DetailGUID} 新建商品失败：副码已存在于商品主条码 {barcode}");
                    if (await _source.HasStoreMultiCodeBarcodeAsync(normalized)) errors.Add($"明细 {detail.DetailGUID} 新建商品失败：副码已存在于分店多码 {barcode}");
                    if (await _source.HasProductSetBarcodeAsync(normalized)) errors.Add($"明细 {detail.DetailGUID} 新建商品失败：副码已存在于商品多码关系 {barcode}");
                }
            }

            if (!string.IsNullOrWhiteSpace(detail.ItemNumber) || !string.IsNullOrWhiteSpace(detail.Barcode))
            {
                if (await _source.HasSupplierProductIdentityAsync(
                    header.SupplierCode,
                    NormalizeCaseInsensitive(detail.ItemNumber),
                    NormalizeCaseInsensitive(detail.Barcode)
                )) errors.Add($"明细 {detail.DetailGUID} 新建商品失败：货号或条码已存在");
            }
        }

        private async Task ValidatePurchasePriceUpdateAsync(StoreLocalSupplierInvoiceDetails detail, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(detail.ProductCode))
            {
                errors.Add($"明细 {detail.DetailGUID} 更新进货价失败：未找到商品编码");
                return;
            }
            if (!await _source.ProductExistsByCodeAsync(detail.ProductCode)) errors.Add($"明细 {detail.DetailGUID} 更新进货价失败：商品不存在");
            if (string.IsNullOrWhiteSpace(detail.StoreCode) || !await _source.StorePriceExistsAsync(detail.StoreCode, detail.ProductCode)) errors.Add($"明细 {detail.DetailGUID} 更新进货价失败：分店价格不存在");
        }

        private async Task ValidateItemNumberUpdateAsync(StoreLocalSupplierInvoiceDetails detail, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(detail.ItemNumber)) errors.Add($"明细 {detail.DetailGUID} 更新货号失败：新货号不能为空");
            if (string.IsNullOrWhiteSpace(detail.ProductCode))
            {
                // 走到这里说明锁内按条码也没能唯一解析出本供应商的商品（0 个或多个），需人工选定改哪个商品。
                errors.Add($"明细 {detail.DetailGUID}（货号 {detail.ItemNumber ?? "--"}）更新货号失败：未关联商品，且条码未唯一匹配到本供应商商品，请先「选用」要改货号的商品");
                return;
            }
            if (!await _source.ProductExistsByCodeAsync(detail.ProductCode)) errors.Add($"明细 {detail.DetailGUID} 更新货号失败：商品不存在");
        }

        private async Task ValidateMultiCodeAsync(
            StoreLocalSupplierInvoiceDetails detail,
            StoreLocalSupplierInvoice header,
            HashSet<string> multiCodeKeys,
            List<string> errors
        )
        {
            if (string.IsNullOrWhiteSpace(detail.ProductCode))
            {
                errors.Add($"明细 {detail.DetailGUID} 添加多码失败：未找到商品编码");
                return;
            }
            if (string.IsNullOrWhiteSpace(detail.Barcode)) errors.Add($"明细 {detail.DetailGUID} 添加多码失败：主条码不能为空");
            var barcodes = GetDetailBarcodes(detail);
            if (barcodes.Count == 0) errors.Add($"明细 {detail.DetailGUID} 添加多码失败：条码不能为空");
            var productExists = await _source.ProductExistsByCodeAsync(detail.ProductCode);
            if (!productExists) errors.Add($"明细 {detail.DetailGUID} 添加多码失败：商品不存在");
            if (LocalSupplierInvoicesBarcodeRules.DeserializeAdditionalBarcodes(detail.AdditionalBarcodesJson).Count > 0
                && productExists && !string.IsNullOrWhiteSpace(detail.Barcode)
                && !await _source.BarcodeBelongsToProductAsync(detail.Barcode, detail.ProductCode, LocalSupplierInvoicesRules.ResolveDetailStoreCode(detail.StoreCode, header.StoreCode)))
            {
                // 副条码只能挂在已确认归属的主条码商品上，避免条码交叉导致误写。
                errors.Add($"明细 {detail.DetailGUID} 添加多码失败：主条码未匹配当前商品");
            }
            foreach (var barcode in barcodes)
            {
                var normalized = NormalizeCaseInsensitive(barcode) ?? string.Empty;
                if (!multiCodeKeys.Add(normalized)) errors.Add($"明细 {detail.DetailGUID} 添加多码失败：本次执行内多码重复 {barcode}");
                // 关键位置：已挂在本行商品上的多码视为已添加（写入时跳过），只有被其他商品占用才是冲突。
                if (await _source.HasStoreMultiCodeBarcodeOnOtherProductAsync(normalized, detail.ProductCode!)) errors.Add($"明细 {detail.DetailGUID} 添加多码失败：分店多码已被其他商品使用 {barcode}");
                if (await _source.HasProductSetBarcodeConflictAsync(normalized, detail.ProductCode!)) errors.Add($"明细 {detail.DetailGUID} 添加多码失败：商品多码关系已被其他商品使用 {barcode}");
            }
        }

        private static bool TryBuildActionSnapshot(
            IReadOnlyCollection<BatchExecuteExpectedActionDto>? actions,
            out Dictionary<string, int>? snapshot,
            out string? error
        )
        {
            snapshot = null;
            error = null;
            if (actions == null || actions.Count == 0) return true;
            snapshot = new(StringComparer.OrdinalIgnoreCase);
            foreach (var action in actions)
            {
                if (string.IsNullOrWhiteSpace(action.DetailGuid)) { error = "确认动作快照包含空的明细标识，请刷新后重试"; return false; }
                var value = action.GetActionValue();
                if (value == null || !LocalSupplierInvoicesRules.IsClientSelectableDetailAction(value.Value)) { error = $"明细 {action.DetailGuid} 的确认动作无效，请刷新后重试"; return false; }
                var guid = action.DetailGuid.Trim();
                if (snapshot.TryGetValue(guid, out var existing) && existing != value.Value) { error = $"明细 {guid} 存在冲突的确认动作，请刷新后重试"; return false; }
                snapshot[guid] = value.Value;
            }
            return true;
        }

        private static bool TryBuildDetailSnapshot(
            IReadOnlyCollection<StoreLocalSupplierInvoiceDetails>? details,
            out Dictionary<string, string>? snapshot,
            out string? error
        )
        {
            snapshot = null;
            error = null;
            if (details == null || details.Count == 0) return true;
            snapshot = new(StringComparer.OrdinalIgnoreCase);
            foreach (var detail in details)
            {
                if (string.IsNullOrWhiteSpace(detail.DetailGUID)) { error = "确认明细快照包含空的明细标识，请刷新后重试"; return false; }
                var guid = detail.DetailGUID.Trim();
                var identity = LocalSupplierInvoicesProductExecutionPlan.BuildDetailIdentity(detail);
                if (snapshot.TryGetValue(guid, out var existing) && existing != identity) { error = $"明细 {guid} 存在冲突的确认快照，请刷新后重试"; return false; }
                snapshot[guid] = identity;
            }
            return true;
        }

        private static bool MatchesSelection<T>(List<string> selected, Dictionary<string, T>? snapshot, string mismatchError, out string? error)
        {
            error = null;
            if (snapshot == null) return true;
            if (snapshot.Count == selected.Count && selected.All(snapshot.ContainsKey)) return true;
            error = mismatchError;
            return false;
        }

        private static Dictionary<string, int> BuildProductTypeSelectionMap(IEnumerable<BatchExecuteNewProductProductTypeSelectionDto>? selections) =>
            (selections ?? Enumerable.Empty<BatchExecuteNewProductProductTypeSelectionDto>())
                .Where(selection => !string.IsNullOrWhiteSpace(selection.DetailGuid))
                .GroupBy(selection => selection.DetailGuid.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().ProductType, StringComparer.OrdinalIgnoreCase);

        private static List<string> ValidateProductTypeSelectionContract(IEnumerable<BatchExecuteNewProductProductTypeSelectionDto>? selections)
        {
            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var selection in selections ?? Enumerable.Empty<BatchExecuteNewProductProductTypeSelectionDto>())
            {
                if (string.IsNullOrWhiteSpace(selection.DetailGuid)) { errors.Add("新商品副码类型选择失败：明细GUID不能为空"); continue; }
                if (selection.ProductType is not 1 and not 2) errors.Add($"明细 {selection.DetailGuid} 新商品副码类型选择失败：商品类型只能是套装或多码");
                if (!seen.Add(selection.DetailGuid.Trim())) errors.Add($"明细 {selection.DetailGuid} 新商品副码类型选择失败：重复提交类型选择");
            }
            return errors;
        }

        private static List<string> GetDetailBarcodes(StoreLocalSupplierInvoiceDetails detail)
        {
            var additional = LocalSupplierInvoicesBarcodeRules.DeserializeAdditionalBarcodes(detail.AdditionalBarcodesJson);
            return additional.Count > 0
                ? additional
                : string.IsNullOrWhiteSpace(detail.Barcode) ? new() : new() { detail.Barcode.Trim() };
        }

        private static string? NormalizeCaseInsensitive(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }

    internal sealed record ProductExecutionRequest(
        string InvoiceGuid,
        List<string> SelectedDetailGuids,
        string UserName,
        Dictionary<string, int> ProductTypes,
        List<string> ProductTypeSelectionErrors,
        Dictionary<string, int>? ConfirmedActions,
        Dictionary<string, string>? ConfirmedDetailIdentities,
        // 用户已对「进货价较上次涨跌超过 40%」的行做二次确认；缺省为未确认，锁内校验据此拒绝。
        bool ConfirmedLargePriceChange = false,
        // 「更新进货价」额外同步的分店（已去重、已排除本单分店）；空表示只更新本单分店。不参与确认快照比对。
        IReadOnlyList<string>? ExtraStoreCodes = null
    )
    {
        /// <summary>规范化前端传入的目标分店：去空白、去重（忽略大小写）、排除本单分店。</summary>
        public static List<string> NormalizeExtraStoreCodes(
            IEnumerable<string>? targetStoreCodes,
            string? invoiceStoreCode
        ) => (targetStoreCodes ?? Enumerable.Empty<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Where(code => !string.Equals(code, invoiceStoreCode?.Trim(), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

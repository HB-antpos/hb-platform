using System.Text.Json;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Features.LocalSupplierInvoices
{
    /// <summary>锁前快照形成的纯领域执行计划；不访问数据库。</summary>
    internal sealed class LocalSupplierInvoicesProductExecutionPlan
    {
        private LocalSupplierInvoicesProductExecutionPlan(
            ProductExecutionRequest request,
            ProductExecutionSourceData initialData,
            IReadOnlyDictionary<string, string>? resolvedItemNumberProductCodes
        )
        {
            Request = request;
            InitialData = initialData;
            ResolvedItemNumberProductCodes = resolvedItemNumberProductCodes
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ExpectedHeaderIdentity = BuildHeaderIdentity(initialData.Header!);
            ExpectedDetailIdentities = request.ConfirmedDetailIdentities
                ?? initialData.Details.ToDictionary(
                    detail => detail.DetailGUID,
                    BuildDetailIdentity,
                    StringComparer.OrdinalIgnoreCase
                );
        }

        public ProductExecutionRequest Request { get; }
        public ProductExecutionSourceData InitialData { get; }
        public string ExpectedHeaderIdentity { get; }
        public Dictionary<string, string> ExpectedDetailIdentities { get; }

        /// <summary>锁前按条码解析出的「更新货号」商品编码（明细 GUID → 商品编码），锁内须解析出同样结果。</summary>
        public IReadOnlyDictionary<string, string> ResolvedItemNumberProductCodes { get; }

        public static LocalSupplierInvoicesProductExecutionPlan Create(
            ProductExecutionRequest request,
            ProductExecutionSourceData initialData,
            IReadOnlyDictionary<string, string>? resolvedItemNumberProductCodes = null
        ) => new(request, initialData, resolvedItemNumberProductCodes);

        public bool RequiresAllProductsLock => InitialData.Details.Any(detail =>
            GetSavedAction(detail) == DetailAction.CreateProduct
        );

        // 解析出的商品也要进身份锁：改货号会改变主档身份，不能只锁明细上已有编码的商品。
        public List<string> InitialProductCodes => NormalizeProductCodes(InitialData.Details)
            .Concat(ResolvedItemNumberProductCodes.Values)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        /// <summary>锁内解析结果必须与锁前一致，否则说明等待锁期间主档条码或供应商变了。</summary>
        public bool MatchesResolvedItemNumberProductCodes(IReadOnlyDictionary<string, string> lockedResolution) =>
            lockedResolution.Count == ResolvedItemNumberProductCodes.Count
            && lockedResolution.All(pair =>
                ResolvedItemNumberProductCodes.TryGetValue(pair.Key, out var code)
                && string.Equals(code, pair.Value, StringComparison.OrdinalIgnoreCase));

        /// <summary>把解析出的商品编码写回内存中的明细，后续校验、执行与明细回写都按已关联处理。</summary>
        public static void ApplyResolvedProductCodes(
            IEnumerable<StoreLocalSupplierInvoiceDetails> details,
            IReadOnlyDictionary<string, string> resolution)
        {
            foreach (var detail in details)
            {
                if (resolution.TryGetValue(detail.DetailGUID, out var productCode))
                    detail.ProductCode = productCode;
            }
        }

        public bool TryValidateLockedData(
            ProductExecutionSourceData lockedData,
            out string? validationError
        )
        {
            if (
                Request.ConfirmedActions != null
                && lockedData.Details.Any(detail =>
                    !Request.ConfirmedActions.TryGetValue(detail.DetailGUID, out var confirmedAction)
                    || (detail.ActivityType ?? (int)DetailAction.None) != confirmedAction
                )
            )
            {
                validationError = "批量执行确认已失效：明细动作已变化，请刷新后重试";
                return false;
            }

            if (
                Request.ConfirmedDetailIdentities != null
                && (
                    lockedData.Details.Count != Request.ConfirmedDetailIdentities.Count
                    || lockedData.Details.Any(detail =>
                        !Request.ConfirmedDetailIdentities.TryGetValue(
                            detail.DetailGUID,
                            out var confirmedIdentity
                        ) || BuildDetailIdentity(detail) != confirmedIdentity
                    )
                )
            )
            {
                validationError = "批量执行确认已失效：明细执行参数已变化，请刷新后重试";
                return false;
            }

            if (
                lockedData.Header == null
                || BuildHeaderIdentity(lockedData.Header) != ExpectedHeaderIdentity
                || lockedData.Details.Count != ExpectedDetailIdentities.Count
                || lockedData.Details.Any(detail =>
                    !ExpectedDetailIdentities.TryGetValue(detail.DetailGUID, out var expectedIdentity)
                    || BuildDetailIdentity(detail) != expectedIdentity
                )
            )
            {
                validationError = "等待商品锁期间进货单头或明细执行参数已变化，请重新读取并确认后重试";
                return false;
            }

            validationError = null;
            return true;
        }

        public static Dictionary<DetailAction, List<StoreLocalSupplierInvoiceDetails>> GroupBySavedAction(
            IEnumerable<StoreLocalSupplierInvoiceDetails> details
        ) => details.GroupBy(GetSavedAction).ToDictionary(group => group.Key, group => group.ToList());

        public static DetailAction GetSavedAction(StoreLocalSupplierInvoiceDetails detail) =>
            detail.ActivityType == 99 ? DetailAction.None : (DetailAction)detail.ActivityType!.Value;

        public static List<string> NormalizeProductCodes(
            IEnumerable<StoreLocalSupplierInvoiceDetails> details
        ) => details
            .Where(detail => !string.IsNullOrWhiteSpace(detail.ProductCode))
            .Select(detail => detail.ProductCode!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        public static string BuildDetailIdentity(StoreLocalSupplierInvoiceDetails detail) =>
            JsonSerializer.Serialize(new
            {
                detail.DetailGUID,
                detail.InvoiceGUID,
                detail.StoreCode,
                detail.SupplierCode,
                detail.ProductTagGUID,
                detail.ProductCategoryGUID,
                detail.StoreProductCode,
                detail.ProductCode,
                detail.ItemNumber,
                detail.Barcode,
                detail.AdditionalBarcodesJson,
                detail.ProductName,
                detail.Specification,
                detail.Unit,
                detail.Quantity,
                detail.LastPurchasePrice,
                detail.PurchasePrice,
                detail.RetailPrice,
                detail.Amount,
                detail.ExistingProductCount,
                detail.BarcodeStatus,
                detail.BarcodeMatchCount,
                detail.ActivityType,
                detail.DiscountRate,
                detail.AutoPricing,
                detail.PricingFloatRate,
                detail.NewAutoRetailPrice,
                detail.IsSpecialProduct,
                detail.OldStoreProductCode,
            });

        private static string BuildHeaderIdentity(StoreLocalSupplierInvoice header) =>
            JsonSerializer.Serialize(new { header.InvoiceGUID, header.StoreCode, header.SupplierCode });
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Models;

public enum CartLineKind
{
    Sale = 0,
    Return = 1,
    OpenItem = 2
}

public enum PosCartLineDiscountSource
{
    None = 0,
    Manual = 1,
    Promotion = 2,
    Catalog = 3
}

internal enum CartLineDiscountSource
{
    None = 0,
    Manual = 1,
    Promotion = 2,
    Catalog = 3
}

public sealed record ReturnCartLineRequest(
    string StoreCode,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string? ItemNumber,
    string? ProductImage,
    decimal Quantity,
    decimal UnitPrice,
    PriceSourceKind PriceSource,
    string PriceSourceLabel,
    string ReturnSourceKey,
    Guid? OriginalOrderGuid,
    Guid? OriginalOrderLineGuid,
    string? ReturnReason = null);

public sealed class CartLine : ObservableObject
{
    private string _storeCode = string.Empty;
    private string _productCode = string.Empty;
    private string? _itemNumber;
    private string? _referenceCode;
    private string? _productImage;
    private string _displayName = string.Empty;
    private string _lookupCode = string.Empty;
    private string _lookupCodeNormalized = string.Empty;
    private decimal _quantity;
    private decimal _unitPrice;
    private decimal _discountAmount;
    private decimal? _discountPercent;
    private int _catalogDiscountBasisPoints;
    private CartLineDiscountSource _discountSource = CartLineDiscountSource.None;
    private PriceSourceKind _priceSource;
    private string _priceSourceLabel = string.Empty;
    private CartLineKind _kind = CartLineKind.Sale;
    private string _returnSourceKey = string.Empty;
    private Guid? _originalOrderGuid;
    private Guid? _originalOrderLineGuid;
    private string? _returnReason;
    private bool _isManualPrice;

    public CartLine(SellableItemDto item)
        : this(item, CartLineKind.Sale, item.RetailPrice)
    {
    }

    public CartLine(SellableItemDto item, CartLineKind kind, decimal unitPrice)
    {
        if (kind == CartLineKind.Return)
        {
            throw new InvalidOperationException("Return cart lines must be created from a return request.");
        }

        if (!IsPositiveIntegerQuantity(item.QuantityFactor))
        {
            throw new InvalidOperationException("Cart line quantity must be a positive integer.");
        }

        Kind = kind;
        Quantity = item.QuantityFactor;
        UpdateFrom(item);
        UnitPrice = unitPrice;
    }

    public CartLine(ReturnCartLineRequest request)
    {
        if (!IsPositiveIntegerQuantity(request.Quantity))
        {
            throw new InvalidOperationException("Cart line quantity must be a positive integer.");
        }

        Kind = CartLineKind.Return;
        StoreCode = request.StoreCode;
        ProductCode = request.ProductCode;
        ItemNumber = request.ItemNumber;
        ReferenceCode = request.ReferenceCode;
        ProductImage = request.ProductImage;
        DisplayName = request.DisplayName;
        LookupCode = request.LookupCode;
        LookupCodeNormalized = NormalizeLookupCode(request.LookupCode);
        Quantity = request.Quantity;
        UnitPrice = request.UnitPrice;
        PriceSource = request.PriceSource;
        PriceSourceLabel = request.PriceSourceLabel;
        ReturnSourceKey = request.ReturnSourceKey;
        OriginalOrderGuid = request.OriginalOrderGuid;
        OriginalOrderLineGuid = request.OriginalOrderLineGuid;
        ReturnReason = request.ReturnReason;
    }

    public string StoreCode
    {
        get => _storeCode;
        private set => SetProperty(ref _storeCode, value);
    }

    public string ProductCode
    {
        get => _productCode;
        private set => SetProperty(ref _productCode, value);
    }

    public string? ItemNumber
    {
        get => _itemNumber;
        private set => SetProperty(ref _itemNumber, value);
    }

    public string? ReferenceCode
    {
        get => _referenceCode;
        private set => SetProperty(ref _referenceCode, value);
    }

    public string? ProductImage
    {
        get => _productImage;
        private set => SetProperty(ref _productImage, value);
    }

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string LookupCode
    {
        get => _lookupCode;
        private set => SetProperty(ref _lookupCode, value);
    }

    public string LookupCodeNormalized
    {
        get => _lookupCodeNormalized;
        private set => SetProperty(ref _lookupCodeNormalized, value);
    }

    public decimal Quantity
    {
        get => _quantity;
        private set
        {
            if (SetProperty(ref _quantity, value))
            {
                RefreshDiscountForGrossChange();
                OnAmountPropertiesChanged();
            }
        }
    }

    public decimal UnitPrice
    {
        get => _unitPrice;
        private set
        {
            if (SetProperty(ref _unitPrice, value))
            {
                RefreshDiscountForGrossChange();
                OnAmountPropertiesChanged();
                OnPropertyChanged(nameof(HasZeroUnitPrice));
            }
        }
    }

    public decimal DiscountAmount
    {
        get => _discountAmount;
        private set
        {
            if (SetProperty(ref _discountAmount, value))
            {
                OnAmountPropertiesChanged();
            }
        }
    }

    public decimal GrossAmount => SignedAmount(PositiveGrossAmount);

    public decimal ActualAmount => SignedAmount(PositiveActualAmount);

    public bool HasDiscount => DiscountAmount > 0m && PositiveGrossAmount > 0m;

    public bool IsAutomaticPromotionDiscount => _discountSource == CartLineDiscountSource.Promotion;

    public bool HasManualDiscount => HasDiscount && _discountSource == CartLineDiscountSource.Manual;

    public bool IsCatalogDiscount => _discountSource == CartLineDiscountSource.Catalog;

    public bool HasZeroUnitPrice => UnitPrice == 0m;

    public string DiscountRateText
    {
        get
        {
            if (!HasDiscount)
            {
                return string.Empty;
            }

            var rate = _discountPercent ?? DiscountAmount / PositiveGrossAmount * 100m;
            return $"-{rate:0.##}%";
        }
    }

    public decimal? DiscountPercent => _discountPercent;

    /// <summary>
    /// 目录折扣基线，以万分比保存（0..10000）；手工折扣覆盖时仍保留该基线。
    /// </summary>
    public int CatalogDiscountBasisPoints
    {
        get => _catalogDiscountBasisPoints;
        private set => SetProperty(ref _catalogDiscountBasisPoints, Math.Clamp(value, 0, 10_000));
    }

    internal CartLineDiscountSource DiscountSource => _discountSource;

    public PriceSourceKind PriceSource
    {
        get => _priceSource;
        private set => SetProperty(ref _priceSource, value);
    }

    public string PriceSourceLabel
    {
        get => _priceSourceLabel;
        private set => SetProperty(ref _priceSourceLabel, value);
    }

    /// <summary>
    /// base price provenance：true = 手工改价（SetLineUnitPrice），false = 目录/远端价格。
    /// 快照与挂单持久化必须随行保留；共享映射据此输出 canonical manual/catalog。
    /// </summary>
    public bool IsManualPrice
    {
        get => _isManualPrice;
        private set => SetProperty(ref _isManualPrice, value);
    }

    public CartLineKind Kind
    {
        get => _kind;
        private set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsReturnLine));
                OnPropertyChanged(nameof(IsOpenItem));
                OnPropertyChanged(nameof(IsLocked));
                OnAmountPropertiesChanged();
            }
        }
    }

    public bool IsReturnLine => Kind == CartLineKind.Return;

    public bool IsOpenItem => Kind == CartLineKind.OpenItem;

    public bool IsLocked => IsReturnLine;

    public decimal SignedQuantity => IsReturnLine ? -Quantity : Quantity;

    public string ReturnSourceKey
    {
        get => _returnSourceKey;
        private set => SetProperty(ref _returnSourceKey, value);
    }

    public Guid? OriginalOrderGuid
    {
        get => _originalOrderGuid;
        private set => SetProperty(ref _originalOrderGuid, value);
    }

    public Guid? OriginalOrderLineGuid
    {
        get => _originalOrderLineGuid;
        private set => SetProperty(ref _originalOrderLineGuid, value);
    }

    public string? ReturnReason
    {
        get => _returnReason;
        private set => SetProperty(ref _returnReason, value);
    }

    public void Increase(decimal quantity)
    {
        ThrowIfLocked();
        Quantity += Math.Max(1m, quantity);
    }

    public bool Decrease(decimal quantity)
    {
        ThrowIfLocked();
        var decreaseBy = Math.Max(1m, quantity);
        if (Quantity <= decreaseBy)
        {
            return false;
        }

        Quantity -= decreaseBy;
        return true;
    }

    public void IncreaseReturnQuantity(decimal quantity)
    {
        if (!IsReturnLine)
        {
            throw new InvalidOperationException("Only return lines can use return quantity merging.");
        }

        Quantity += Math.Max(1m, quantity);
    }

    public void SetQuantity(decimal quantity)
    {
        ThrowIfLocked();
        if (!IsPositiveIntegerQuantity(quantity))
        {
            throw new InvalidOperationException("Cart line quantity must be a positive integer.");
        }

        Quantity = quantity;
    }

    /// <summary>
    /// 共享 sale 快照恢复专用：允许正有限小数数量（跨 iPad→WPF canonical 是 decimal）。
    /// 仅由共享挂单恢复路径调用；普通 Add/SetQuantity/UI 编辑仍严格正整数。
    /// </summary>
    internal void SetSharedSaleQuantity(decimal quantity)
    {
        ThrowIfLocked();
        if (!IsPositiveFiniteQuantity(quantity))
        {
            throw new InvalidOperationException("Shared sale quantity must be a positive finite number.");
        }

        Quantity = quantity;
    }

    public void SetUnitPrice(decimal unitPrice)
    {
        ThrowIfLocked();
        UnitPrice = unitPrice;
    }

    /// <summary>快照/挂单恢复专用：按快照还原价格 provenance，不触发手工改价语义。</summary>
    internal void SetManualPrice(bool isManualPrice)
    {
        IsManualPrice = isManualPrice;
    }

    public void SetDiscountAmount(decimal discountAmount)
    {
        ThrowIfLocked();
        if (discountAmount <= 0m)
        {
            ClearManualDiscount();
            return;
        }

        ApplyDiscount(discountAmount, null, CartLineDiscountSource.Manual);
    }

    public void SetDiscountPercent(decimal discountPercent)
    {
        ThrowIfLocked();
        var normalizedDiscountPercent = Math.Clamp(discountPercent, 0m, 100m);
        if (normalizedDiscountPercent <= 0m)
        {
            ClearManualDiscount();
            return;
        }

        // 非零手工百分比即使当前金额舍入为 0，也必须保留来源和百分比，
        // 后续数量/单价变化才能按同一人工折扣重新计算。
        ApplyDiscount(
            CalculateDiscountAmount(normalizedDiscountPercent),
            normalizedDiscountPercent,
            CartLineDiscountSource.Manual,
            preserveManualSourceWhenZero: true);
    }

    /// <summary>
    /// 整单折扣分摊专用：Manual + 0 是合法状态，表示整单覆盖已明确排除该行，
    /// 不能因为该行分到 0 分而恢复 catalog 折扣。
    /// </summary>
    internal void SetOrderDiscountAmount(decimal discountAmount)
    {
        ThrowIfLocked();
        ApplyDiscount(
            discountAmount,
            null,
            CartLineDiscountSource.Manual,
            preserveManualSourceWhenZero: true);
    }

    internal void SetPromotionDiscountAmount(decimal discountAmount)
    {
        ThrowIfLocked();
        if (_discountSource == CartLineDiscountSource.Manual)
        {
            return;
        }

        if (HasCatalogDiscountBaseline)
        {
            ApplyCatalogDiscount();
            return;
        }

        ApplyDiscount(discountAmount, null, CartLineDiscountSource.Promotion);
    }

    internal void ClearPromotionDiscount()
    {
        if (_discountSource != CartLineDiscountSource.Promotion)
        {
            return;
        }

        RestoreCatalogDiscountOrClear();
    }

    public void SetAutomaticPromotionDiscountAmount(decimal discountAmount)
    {
        SetPromotionDiscountAmount(discountAmount);
    }

    public void ClearAutomaticPromotionDiscount()
    {
        ClearPromotionDiscount();
    }

    internal void ClearManualDiscount()
    {
        if (_discountSource == CartLineDiscountSource.Manual)
        {
            RestoreCatalogDiscountOrClear();
        }
    }

    internal void SetCatalogDiscountBasisPoints(int basisPoints)
    {
        if (basisPoints is < 0 or > 10_000)
        {
            // 本地挂单/崩溃恢复若读到损坏基线必须失败关闭，不能静默夹取成另一笔金额。
            throw new InvalidOperationException(
                "Catalog discount basis points must be between 0 and 10000.");
        }

        CatalogDiscountBasisPoints = basisPoints;
        if (Kind != CartLineKind.Sale || _discountSource == CartLineDiscountSource.Manual)
        {
            return;
        }

        if (HasCatalogDiscountBaseline)
        {
            // 目录折扣优先于旧的自动促销，但不能覆盖手工折扣。
            ApplyCatalogDiscount();
        }
        else if (_discountSource == CartLineDiscountSource.Catalog)
        {
            ApplyDiscount(0m, null, CartLineDiscountSource.Catalog);
        }
    }

    /// <summary>
    /// 目录复核/合并专用：同步安全商品元数据与目录折扣基线，
    /// 不触碰 UnitPrice、PriceSource、PriceSourceLabel 或手工改价 provenance。
    /// </summary>
    internal void UpdateCatalogMetadataAndDiscountFrom(SellableItemDto item)
    {
        if (Kind != CartLineKind.Sale)
        {
            return;
        }

        StoreCode = item.StoreCode;
        ProductCode = item.ProductCode;
        ItemNumber = item.ItemNumber;
        ReferenceCode = item.ReferenceCode;
        ProductImage = item.ProductImage;
        DisplayName = item.DisplayName;
        LookupCode = item.LookupCode;
        LookupCodeNormalized = NormalizeLookupCode(item.LookupCode);
        SetCatalogDiscountBasisPoints(CalculateCatalogDiscountBasisPoints(item.DiscountRate));
    }

    public void UpdateFrom(SellableItemDto item)
    {
        StoreCode = item.StoreCode;
        ProductCode = item.ProductCode;
        ItemNumber = item.ItemNumber;
        ReferenceCode = item.ReferenceCode;
        ProductImage = item.ProductImage;
        DisplayName = item.DisplayName;
        LookupCode = item.LookupCode;
        LookupCodeNormalized = NormalizeLookupCode(item.LookupCode);
        SetCatalogDiscountBasisPoints(Kind == CartLineKind.Sale
            ? CalculateCatalogDiscountBasisPoints(item.DiscountRate)
            : 0);
        UnitPrice = item.RetailPrice;
        PriceSource = item.PriceSource;
        PriceSourceLabel = item.PriceSourceLabel;
        // 远端/目录刷新恢复为 catalog 来源，清除手工改价 provenance。
        IsManualPrice = false;
    }

    public static string NormalizeLookupCode(string? lookupCode)
    {
        return (lookupCode ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static bool IsPositiveIntegerQuantity(decimal quantity)
    {
        return quantity > 0m && decimal.Truncate(quantity) == quantity;
    }

    /// <summary>正有限小数：0 &lt; quantity ≤ 共享 canonical 上限（decimal 本身无 NaN/Inf）。</summary>
    private static bool IsPositiveFiniteQuantity(decimal quantity)
    {
        return quantity > 0m
            && quantity <= SharedHeldOrderCanonicalConstants.MaxQuantity;
    }

    private void OnAmountPropertiesChanged()
    {
        OnPropertyChanged(nameof(SignedQuantity));
        OnPropertyChanged(nameof(GrossAmount));
        OnPropertyChanged(nameof(ActualAmount));
        OnPropertyChanged(nameof(HasDiscount));
        OnPropertyChanged(nameof(HasManualDiscount));
        OnPropertyChanged(nameof(DiscountRateText));
    }

    private void RefreshDiscountForGrossChange()
    {
        switch (_discountSource)
        {
            case CartLineDiscountSource.Manual:
                ApplyDiscount(
                    _discountPercent is decimal discountPercent
                        ? CalculateDiscountAmount(discountPercent)
                        : DiscountAmount,
                    _discountPercent,
                    CartLineDiscountSource.Manual,
                    preserveManualSourceWhenZero: true);
                break;
            case CartLineDiscountSource.Catalog:
                ApplyCatalogDiscount();
                break;
            case CartLineDiscountSource.Promotion when HasCatalogDiscountBaseline:
                ApplyCatalogDiscount();
                break;
            case CartLineDiscountSource.Promotion:
                ApplyDiscount(DiscountAmount, null, CartLineDiscountSource.Promotion);
                break;
            default:
                if (HasCatalogDiscountBaseline && Kind == CartLineKind.Sale)
                {
                    ApplyCatalogDiscount();
                }
                else
                {
                    ApplyDiscount(0m, null, CartLineDiscountSource.None);
                }

                break;
        }
    }

    private decimal CalculateDiscountAmount(decimal discountPercent)
    {
        return ClampDiscountAmount(decimal.Round(PositiveGrossAmount * discountPercent / 100m, 2, MidpointRounding.AwayFromZero));
    }

    private decimal CalculateCatalogDiscountAmount()
    {
        // 先按已舍入到分的总价计算，再 AwayFromZero 舍入到分，和整数分语义一致。
        var discount = decimal.Round(
            PositiveGrossAmount * CatalogDiscountBasisPoints / 10_000m,
            2,
            MidpointRounding.AwayFromZero);
        // 折扣保护：自动目录折扣至少留 1 分，金额很小时舍入也不能把一行折成 $0。
        var maxDiscount = Math.Max(0m, PositiveGrossAmount - 0.01m);
        return ClampDiscountAmount(Math.Min(discount, maxDiscount));
    }

    private decimal ClampDiscountAmount(decimal discountAmount)
    {
        return Math.Clamp(decimal.Round(discountAmount, 2, MidpointRounding.AwayFromZero), 0m, PositiveGrossAmount);
    }

    private void ApplyDiscount(
        decimal discountAmount,
        decimal? discountPercent,
        CartLineDiscountSource discountSource,
        bool preserveManualSourceWhenZero = false)
    {
        var previousDiscountSource = _discountSource;
        DiscountAmount = ClampDiscountAmount(discountAmount);
        var preservesZeroManualState = preserveManualSourceWhenZero &&
            discountSource == CartLineDiscountSource.Manual;
        _discountPercent = DiscountAmount > 0m || preservesZeroManualState
            ? discountPercent
            : null;
        _discountSource = DiscountAmount > 0m || preservesZeroManualState
            ? discountSource
            : CartLineDiscountSource.None;
        if (previousDiscountSource != _discountSource)
        {
            OnPropertyChanged(nameof(IsAutomaticPromotionDiscount));
            OnPropertyChanged(nameof(HasManualDiscount));
            OnPropertyChanged(nameof(IsCatalogDiscount));
        }

        OnPropertyChanged(nameof(DiscountPercent));
    }

    private void ApplyCatalogDiscount()
    {
        if (!HasCatalogDiscountBaseline || Kind != CartLineKind.Sale || PositiveGrossAmount <= 0m)
        {
            ApplyDiscount(0m, null, CartLineDiscountSource.Catalog);
            return;
        }

        ApplyDiscount(
            CalculateCatalogDiscountAmount(),
            CatalogDiscountBasisPoints / 100m,
            CartLineDiscountSource.Catalog);
    }

    private void RestoreCatalogDiscountOrClear()
    {
        if (HasCatalogDiscountBaseline && Kind == CartLineKind.Sale)
        {
            ApplyCatalogDiscount();
            return;
        }

        ApplyDiscount(0m, null, CartLineDiscountSource.None);
    }

    private bool HasCatalogDiscountBaseline => CatalogDiscountBasisPoints > 0;

    /// <summary>目录折扣是系统自动给的，永远不能是「全免」：基点上限 9999（99.99%）。</summary>
    internal const int MaxCatalogDiscountBasisPoints = 9_999;

    private static int CalculateCatalogDiscountBasisPoints(decimal? discountRate)
    {
        // 中文注释：折扣保护（1002 店 10-24/25：目录折扣率 = 1 被夹成 100%，整行变 $0）。
        // 目录折扣率是 0~1 的小数，≥ 1（100% 及以上）一定是脏数据（旧版本把 1、100 这类值写进了分店价），
        // 不能当成「全部折掉」，一律忽略、按原价卖；负数同样忽略。真正要打折的商品用 (0,1) 之间的小数。
        if (discountRate is not { } rate || rate <= 0m || rate >= 1m)
        {
            return 0;
        }

        var basisPoints = decimal.ToInt32(decimal.Round(rate * 10_000m, 0, MidpointRounding.AwayFromZero));
        return Math.Min(MaxCatalogDiscountBasisPoints, basisPoints);
    }

    private decimal PositiveGrossAmount => decimal.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);

    private decimal PositiveActualAmount => decimal.Round((Quantity * UnitPrice) - DiscountAmount, 2, MidpointRounding.AwayFromZero);

    private decimal SignedAmount(decimal amount)
    {
        return IsReturnLine ? -amount : amount;
    }

    private void ThrowIfLocked()
    {
        if (IsLocked)
        {
            throw new InvalidOperationException("Locked cart lines cannot be edited.");
        }
    }
}

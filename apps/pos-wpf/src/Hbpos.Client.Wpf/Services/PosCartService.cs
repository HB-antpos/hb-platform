using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public sealed class PosCartService
{
    private readonly List<CartLine> _lines = [];
    private readonly List<OrderReturnPaymentCapacityDto> _returnPaymentCapacities = [];
    private readonly ReaderWriterLockSlim _recoveryPublicationLock = new(LockRecursionPolicy.SupportsRecursion);
    private IReadOnlyList<CatalogPromotionRuleDto> _automaticPromotionRules = [];
    private Guid? _sharedHeldOrderClaimId;
    private Guid? _recoveryOwnerAttemptGuid;
    private CardRecoveryAttemptKey? _recoveryOwnerAttemptKey;
    private long _revision;
    private bool _preserveSharedPromotionDiscounts;
    private bool _preserveSharedSnapshotCatalogValues;

    public IReadOnlyList<CartLine> Lines => _lines;

    public IReadOnlyList<OrderReturnPaymentCapacityDto> ReturnPaymentCapacities => _returnPaymentCapacities;

    public decimal TotalAmount => decimal.Round(_lines.Sum(line => line.GrossAmount), 2, MidpointRounding.AwayFromZero);

    public decimal DiscountAmount => decimal.Round(_lines.Sum(line => line.DiscountAmount), 2, MidpointRounding.AwayFromZero);

    public decimal ActualAmount => decimal.Round(_lines.Sum(line => line.ActualAmount), 2, MidpointRounding.AwayFromZero);

    public bool IsEmpty => _lines.Count == 0;

    public bool HasZeroPriceLine => _lines.Any(line => line.HasZeroUnitPrice);

    public bool HasNonIntegerQuantity => _lines.Any(line =>
        _sharedHeldOrderClaimId is null
            ? !IsPositiveIntegerQuantity(line.Quantity)
            : !IsPositiveFiniteDecimalQuantity(line.Quantity));

    public bool HasReturnLine => _lines.Any(line => line.IsReturnLine);

    public long Revision => Interlocked.Read(ref _revision);

    public Guid? RecoveryOwnerAttemptGuid
    {
        get
        {
            _recoveryPublicationLock.EnterReadLock();
            try
            {
                return _recoveryOwnerAttemptGuid;
            }
            finally
            {
                _recoveryPublicationLock.ExitReadLock();
            }
        }
    }

    /// <summary>带 provider 的恢复 publication 所有权；旧 GUID 发布保留为 null 以兼容既有调用。</summary>
    internal CardRecoveryAttemptKey? RecoveryOwnerAttemptKey
    {
        get
        {
            _recoveryPublicationLock.EnterReadLock();
            try
            {
                return _recoveryOwnerAttemptKey;
            }
            finally
            {
                _recoveryPublicationLock.ExitReadLock();
            }
        }
    }

    public event EventHandler? CartChanged;

    public void SetAutomaticPromotionRules(IEnumerable<CatalogPromotionRuleDto> rules)
    {
        using var mutation = EnterMutationScope();
        ArgumentNullException.ThrowIfNull(rules);
        _automaticPromotionRules = rules
            .Where(rule => rule.Products.Count > 0)
            .ToArray();
        if (!_preserveSharedPromotionDiscounts && !_preserveSharedSnapshotCatalogValues)
        {
            RefreshAutomaticPromotionDiscounts();
        }

        OnCartChanged();
    }

    public CartLine AddItem(SellableItemDto item)
    {
        using var mutation = EnterMutationScope();
        if (!IsPositiveIntegerQuantity(item.QuantityFactor))
        {
            throw new InvalidOperationException("Cart item quantity must be a positive integer.");
        }

        var existing = FindSaleLineForProduct(item);

        if (existing is not null)
        {
            if (!IsPositiveIntegerQuantity(existing.Quantity))
            {
                throw new InvalidOperationException("Cart line quantity must be a positive integer.");
            }

            // 合并加购只能同步允许的目录折扣字段；手工价和共享快照冻结必须保持不变。
            UpdateCatalogMetadataAndDiscountIfAllowed(existing, item);
            existing.Increase(item.QuantityFactor);
            RefreshDiscountsAndNotify();
            return existing;
        }

        var line = new CartLine(item);
        _lines.Add(line);
        RefreshDiscountsAndNotify();
        return line;
    }

    public CartLine AddConsecutiveItem(SellableItemDto item)
    {
        using var mutation = EnterMutationScope();
        if (!IsPositiveIntegerQuantity(item.QuantityFactor))
        {
            throw new InvalidOperationException("Cart item quantity must be a positive integer.");
        }

        // 扫码自动加购只看最后一行，避免跨行回溯导致非连续重复商品被合并。
        var lastLine = _lines.LastOrDefault();
        var normalizedLookupCode = CartLine.NormalizeLookupCode(item.LookupCode);
        if (lastLine is not null &&
            !lastLine.IsReturnLine &&
            !lastLine.IsOpenItem &&
            string.Equals(lastLine.StoreCode, item.StoreCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(lastLine.LookupCodeNormalized, normalizedLookupCode, StringComparison.Ordinal) &&
            IsSameProduct(lastLine, item))
        {
            if (!IsPositiveIntegerQuantity(lastLine.Quantity))
            {
                throw new InvalidOperationException("Cart line quantity must be a positive integer.");
            }

            // 连续扫码合并只能同步允许的目录折扣字段，不能用新目录行重置手工价。
            UpdateCatalogMetadataAndDiscountIfAllowed(lastLine, item);
            lastLine.Increase(item.QuantityFactor);
            RefreshDiscountsAndNotify();
            return lastLine;
        }

        // 最后一行不匹配时必须新建购物车行，保留扫码顺序。
        var line = new CartLine(item);
        _lines.Add(line);
        RefreshDiscountsAndNotify();
        return line;
    }

    private void UpdateCatalogMetadataAndDiscountIfAllowed(CartLine line, SellableItemDto item)
    {
        if (_preserveSharedSnapshotCatalogValues ||
            line.IsReturnLine ||
            line.IsOpenItem)
        {
            return;
        }

        line.UpdateCatalogMetadataAndDiscountFrom(item);
    }

    public CartLine AddOpenItem(SellableItemDto item, decimal unitPrice)
    {
        using var mutation = EnterMutationScope();
        if (!IsPositiveIntegerQuantity(item.QuantityFactor))
        {
            throw new InvalidOperationException("Cart item quantity must be a positive integer.");
        }

        if (unitPrice < 0m)
        {
            throw new InvalidOperationException("Open item price must be zero or greater.");
        }

        var line = new CartLine(item, CartLineKind.OpenItem, unitPrice);
        _lines.Add(line);
        RefreshDiscountsAndNotify();
        return line;
    }

    public CartLine AddReturnLine(ReturnCartLineRequest request)
    {
        using var mutation = EnterMutationScope();
        if (!IsPositiveIntegerQuantity(request.Quantity))
        {
            throw new InvalidOperationException("Return cart line quantity must be a positive integer.");
        }

        var existing = FindReturnLineBySourceKey(request.ReturnSourceKey);
        if (existing is not null)
        {
            existing.IncreaseReturnQuantity(request.Quantity);
            RefreshDiscountsAndNotify();
            return existing;
        }

        var line = new CartLine(request);
        _lines.Add(line);
        RefreshDiscountsAndNotify();
        return line;
    }

    public void AddReturnPaymentCapacities(IEnumerable<OrderReturnPaymentCapacityDto> capacities)
    {
        using var mutation = EnterMutationScope();
        var capacityList = capacities
            .Where(capacity => capacity.RemainingAmount > 0m)
            .ToList();
        if (capacityList.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var capacity in capacityList)
        {
            var existingIndex = _returnPaymentCapacities.FindIndex(existing =>
                existing.Method == capacity.Method &&
                string.Equals(existing.Reference ?? string.Empty, capacity.Reference ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                existing.OriginalOrderGuid == capacity.OriginalOrderGuid);
            if (existingIndex >= 0)
            {
                _returnPaymentCapacities[existingIndex] = capacity;
            }
            else
            {
                _returnPaymentCapacities.Add(capacity);
            }

            changed = true;
        }

        if (changed)
        {
            OnCartChanged();
        }
    }

    public CartLine? FindLineByLookupCode(string storeCode, string lookupCode)
    {
        var normalizedLookupCode = CartLine.NormalizeLookupCode(lookupCode);
        return _lines.FirstOrDefault(line =>
            !line.IsReturnLine &&
            !line.IsOpenItem &&
            string.Equals(line.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase) &&
            line.LookupCodeNormalized == normalizedLookupCode);
    }

    private CartLine? FindSaleLineForProduct(SellableItemDto item)
    {
        var normalizedLookupCode = CartLine.NormalizeLookupCode(item.LookupCode);
        return _lines.FirstOrDefault(line =>
            !line.IsReturnLine &&
            !line.IsOpenItem &&
            string.Equals(line.StoreCode, item.StoreCode, StringComparison.OrdinalIgnoreCase) &&
            line.LookupCodeNormalized == normalizedLookupCode &&
            IsSameProduct(line, item));
    }

    // 一码多商品时同一查询码对应不同商品，合并行必须再比商品编码，否则后选商品会并进旧行按旧价收钱。
    // 口径与冲突合并器、LocalSellableItemIndex.Upsert 判定"不同商品"一致。
    private static bool IsSameProduct(CartLine line, SellableItemDto item)
    {
        return string.Equals(
            NormalizeProductCode(line.ProductCode),
            NormalizeProductCode(item.ProductCode),
            StringComparison.OrdinalIgnoreCase);
    }

    public CartLine? FindReturnLineBySourceKey(string returnSourceKey)
    {
        return _lines.FirstOrDefault(line =>
            line.IsReturnLine &&
            string.Equals(line.ReturnSourceKey, returnSourceKey, StringComparison.OrdinalIgnoreCase));
    }

    public bool UpdateLineFromRemote(SellableItemDto item)
    {
        return UpdateLineFromRemote(item.StoreCode, item.LookupCode, item);
    }

    public bool UpdateLineFromRemote(string storeCode, string lookupCode, SellableItemDto item)
    {
        using var mutation = EnterMutationScope();
        var line = FindLineByLookupCode(storeCode, lookupCode);

        if (line is null || _preserveSharedSnapshotCatalogValues)
        {
            return false;
        }

        // 手工改价行只接受安全目录元数据与折扣基线刷新，禁止覆盖人工单价/provenance。
        if (line.IsManualPrice)
        {
            UpdateCatalogMetadataAndDiscountIfAllowed(line, item);
        }
        else
        {
            line.UpdateFrom(item);
        }
        RefreshDiscountsAndNotify(preserveFrozenSharedPromotion: true);
        return true;
    }

    public bool UpdateLineFromRemote(CartLine line, SellableItemDto item)
    {
        using var mutation = EnterMutationScope();
        if (!_lines.Contains(line) || _preserveSharedSnapshotCatalogValues)
        {
            return false;
        }

        // 与按条码查找的远端刷新保持同一门禁：手工价只更新安全目录元数据与 catalog baseline。
        if (line.IsManualPrice)
        {
            UpdateCatalogMetadataAndDiscountIfAllowed(line, item);
        }
        else
        {
            line.UpdateFrom(item);
        }
        RefreshDiscountsAndNotify(preserveFrozenSharedPromotion: true);
        return true;
    }

    public bool RemoveLineByLookupCode(string storeCode, string lookupCode)
    {
        using var mutation = EnterMutationScope();
        var line = FindLineByLookupCode(storeCode, lookupCode);

        if (line is null)
        {
            return false;
        }

        // 绑定共享 claim 的购物车不能通过同步编辑清空：保持行+binding 并阻止操作，
        // 用户必须走已有异步 ClearCart owner-release，否则会遗留服务端 Active。
        if (_sharedHeldOrderClaimId is not null && _lines.Count == 1)
        {
            return false;
        }

        _lines.Remove(line);
        ClearSharedHeldOrderBindingIfCartEmpty();
        RefreshDiscountsAndNotify();
        return true;
    }

    public bool RemoveLine(CartLine line)
    {
        using var mutation = EnterMutationScope();
        if (!_lines.Contains(line))
        {
            return false;
        }

        // 绑定共享 claim 的购物车不能通过同步编辑清空：保持行+binding 并阻止操作。
        if (_sharedHeldOrderClaimId is not null && _lines.Count == 1)
        {
            return false;
        }

        _lines.Remove(line);
        if (line.IsReturnLine && !_lines.Any(existing => existing.IsReturnLine))
        {
            _returnPaymentCapacities.Clear();
        }

        ClearSharedHeldOrderBindingIfCartEmpty();
        RefreshDiscountsAndNotify();
        return true;
    }

    public bool IncreaseLine(CartLine? line)
    {
        using var mutation = EnterMutationScope();
        if (line is null || line.IsLocked || !_lines.Contains(line) || !IsPositiveIntegerQuantity(line.Quantity))
        {
            return false;
        }

        line.Increase(1m);
        RefreshDiscountsAndNotify();
        return true;
    }

    public bool DecreaseLine(CartLine? line)
    {
        using var mutation = EnterMutationScope();
        if (line is null || line.IsLocked || !_lines.Contains(line) || !IsPositiveIntegerQuantity(line.Quantity))
        {
            return false;
        }

        // 加减号只负责调整数量，数量下限为 1；删除整行必须走 RemoveLine。
        if (!line.Decrease(1m))
        {
            return false;
        }

        RefreshDiscountsAndNotify();
        return true;
    }

    public bool SetLineQuantity(CartLine? line, decimal quantity)
    {
        using var mutation = EnterMutationScope();
        // 数量必须是正有限整数，0/负数/小数一律拒绝：绑定共享 claim 的购物车
        // 永远不会通过 SetLineQuantity 被静默清空并遗留服务端 Active。
        if (line is null || line.IsLocked || !_lines.Contains(line) || !IsPositiveIntegerQuantity(quantity))
        {
            return false;
        }

        line.SetQuantity(quantity);
        RefreshDiscountsAndNotify();
        return true;
    }

    public bool SetLineUnitPrice(CartLine? line, decimal unitPrice)
    {
        using var mutation = EnterMutationScope();
        if (line is null || line.IsLocked || !_lines.Contains(line) || unitPrice < 0m)
        {
            return false;
        }

        line.SetUnitPrice(unitPrice);
        // 手工改价：base price provenance 标记为 manual，快照/挂单持久化保留。
        line.SetManualPrice(true);
        RefreshDiscountsAndNotify();
        return true;
    }

    public bool SetLineDiscountAmount(CartLine? line, decimal discountAmount)
    {
        using var mutation = EnterMutationScope();
        if (line is null || line.IsLocked || !_lines.Contains(line) || discountAmount < 0m || discountAmount > line.GrossAmount)
        {
            return false;
        }

        line.SetDiscountAmount(discountAmount);
        RefreshDiscountsAndNotify();
        return true;
    }

    public bool SetLineDiscountPercent(CartLine? line, decimal discountPercent)
    {
        using var mutation = EnterMutationScope();
        if (line is null || line.IsLocked || !_lines.Contains(line) || discountPercent < 0m || discountPercent > 100m)
        {
            return false;
        }

        line.SetDiscountPercent(discountPercent);
        RefreshDiscountsAndNotify();
        return true;
    }

    internal void ApplyPromotionDiscounts(IEnumerable<PromotionLineDiscount> discounts)
    {
        using var mutation = EnterMutationScope();
        var incomingDiscounts = discounts
            .GroupBy(discount => discount.Line)
            .ToDictionary(group => group.Key, group => group.Last().DiscountAmount);
        var changed = false;

        foreach (var line in _lines)
        {
            if (line.DiscountSource != CartLineDiscountSource.Promotion)
            {
                continue;
            }

            line.ClearPromotionDiscount();
            changed = true;
        }

        foreach (var entry in incomingDiscounts)
        {
            var line = entry.Key;
            if (!_lines.Contains(line) ||
                line.IsReturnLine ||
                line.IsOpenItem ||
                !IsPositiveIntegerQuantity(line.Quantity) ||
                line.GrossAmount <= 0m)
            {
                continue;
            }

            // 手工折扣必须优先保留，自动促销只能写入没有手工折扣来源的购物车行。
            if (line.DiscountSource == CartLineDiscountSource.Manual)
            {
                continue;
            }

            // Catalog 折扣已经是有效折扣，促销不得与其叠加；目录折扣移除后，
            // 下一次刷新才允许当前促销规则重新参与。
            if (line.DiscountSource == CartLineDiscountSource.Catalog)
            {
                continue;
            }

            var clampedDiscount = Math.Clamp(
                decimal.Round(entry.Value, 2, MidpointRounding.AwayFromZero),
                0m,
                line.GrossAmount);
            if (clampedDiscount <= 0m)
            {
                continue;
            }

            line.SetPromotionDiscountAmount(clampedDiscount);
            changed = true;
        }

        if (changed)
        {
            OnCartChanged();
        }
    }

    public bool SetOrderDiscountAmount(decimal discountAmount)
    {
        using var mutation = EnterMutationScope();
        if (_lines.Count == 0 || HasReturnLine || discountAmount < 0m || discountAmount > TotalAmount)
        {
            return false;
        }

        ApplyOrderDiscountAmount(discountAmount, preserveManualOverrideWhenZero: false);
        RefreshDiscountsAndNotify();
        return true;
    }

    public bool SetOrderDiscountPercent(decimal discountPercent)
    {
        using var mutation = EnterMutationScope();
        if (_lines.Count == 0 || HasReturnLine || discountPercent < 0m || discountPercent > 100m)
        {
            return false;
        }

        var discountAmount = decimal.Round(TotalAmount * discountPercent / 100m, 2, MidpointRounding.AwayFromZero);
        ApplyOrderDiscountAmount(
            discountAmount,
            preserveManualOverrideWhenZero: discountPercent > 0m);
        RefreshDiscountsAndNotify();
        return true;
    }

    public void Clear()
    {
        using var mutation = EnterMutationScope();
        if (_lines.Count == 0)
        {
            if (_returnPaymentCapacities.Count > 0 || _sharedHeldOrderClaimId is not null)
            {
                _returnPaymentCapacities.Clear();
                _sharedHeldOrderClaimId = null;
                _preserveSharedPromotionDiscounts = false;
                _preserveSharedSnapshotCatalogValues = false;
                OnCartChanged();
            }

            return;
        }

        _lines.Clear();
        _returnPaymentCapacities.Clear();
        _sharedHeldOrderClaimId = null;
        _preserveSharedPromotionDiscounts = false;
        _preserveSharedSnapshotCatalogValues = false;
        OnCartChanged();
    }

    public PosCartSnapshot CreateSnapshot()
    {
        return new PosCartSnapshot(_lines
            .Select(line => new PosCartLineSnapshot(
                line.StoreCode,
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
                line.ItemNumber,
                line.ProductImage,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.DiscountPercent,
                line.PriceSource,
                line.PriceSourceLabel,
                line.Kind,
                line.ReturnSourceKey,
                line.OriginalOrderGuid,
                line.OriginalOrderLineGuid,
                line.ReturnReason,
                MapSnapshotDiscountSource(line.DiscountSource),
                line.IsManualPrice,
                line.CatalogDiscountBasisPoints))
            .ToArray(),
            _sharedHeldOrderClaimId);
    }

    /// <summary>
    /// 以 attempt 所有权把已验证快照一次性发布到空购物车。revision 用于拒绝核验后发生的陈旧发布。
    /// </summary>
    public PosCartRecoveryPublicationResult TryPublishRecoverySnapshot(
        Guid attemptGuid,
        long expectedRevision,
        PosCartSnapshot snapshot)
    {
        return TryPublishRecoverySnapshotCore(
            attemptGuid,
            recoveryOwnerAttemptKey: null,
            expectedRevision,
            snapshot);
    }

    internal PosCartRecoveryPublicationResult TryPublishRecoverySnapshot(
        CardRecoveryAttemptKey attemptKey,
        long expectedRevision,
        PosCartSnapshot snapshot)
    {
        return TryPublishRecoverySnapshotCore(
            attemptKey.AttemptGuid,
            attemptKey,
            expectedRevision,
            snapshot);
    }

    private PosCartRecoveryPublicationResult TryPublishRecoverySnapshotCore(
        Guid attemptGuid,
        CardRecoveryAttemptKey? recoveryOwnerAttemptKey,
        long expectedRevision,
        PosCartSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // 先在隔离购物车中完整物化，任何语义错误都不能触碰真实购物车。
        var stagingCart = new PosCartService();
        stagingCart.RestoreSnapshot(snapshot);
        var validatedSnapshot = stagingCart.CreateSnapshot();

        _recoveryPublicationLock.EnterWriteLock();
        try
        {
            if (_recoveryOwnerAttemptGuid is not null ||
                _revision != expectedRevision ||
                !IsEmpty)
            {
                return new PosCartRecoveryPublicationResult(false, false, _revision);
            }

            _recoveryOwnerAttemptGuid = attemptGuid;
            _recoveryOwnerAttemptKey = recoveryOwnerAttemptKey;
            RestoreSnapshotCore(validatedSnapshot);
            var notificationWarning = OnCartChangedBestEffort();
            return new PosCartRecoveryPublicationResult(true, notificationWarning, _revision);
        }
        finally
        {
            _recoveryPublicationLock.ExitWriteLock();
        }
    }

    /// <summary>最终状态已经持久化后释放购物车所有权，保留已发布内容。</summary>
    public bool CompleteRecoveryPublication(Guid attemptGuid)
    {
        _recoveryPublicationLock.EnterWriteLock();
        try
        {
            if (_recoveryOwnerAttemptGuid != attemptGuid)
            {
                return false;
            }

            _recoveryOwnerAttemptGuid = null;
            _recoveryOwnerAttemptKey = null;
            return true;
        }
        finally
        {
            _recoveryPublicationLock.ExitWriteLock();
        }
    }

    /// <summary>仅在 provider 与 attempt 均精确匹配时释放购物车所有权。</summary>
    internal bool CompleteRecoveryPublication(CardRecoveryAttemptKey attemptKey)
    {
        _recoveryPublicationLock.EnterWriteLock();
        try
        {
            if (_recoveryOwnerAttemptKey != attemptKey ||
                _recoveryOwnerAttemptGuid != attemptKey.AttemptGuid)
            {
                return false;
            }

            _recoveryOwnerAttemptGuid = null;
            _recoveryOwnerAttemptKey = null;
            return true;
        }
        finally
        {
            _recoveryPublicationLock.ExitWriteLock();
        }
    }

    /// <summary>仅回滚指定 attempt 发布的购物车，绝不清理其他恢复或普通购物车。</summary>
    public PosCartRecoveryPublicationResult RollbackRecoveryPublication(Guid attemptGuid)
    {
        _recoveryPublicationLock.EnterWriteLock();
        try
        {
            if (_recoveryOwnerAttemptGuid != attemptGuid)
            {
                return new PosCartRecoveryPublicationResult(false, false, _revision);
            }

            _lines.Clear();
            _returnPaymentCapacities.Clear();
            _sharedHeldOrderClaimId = null;
            _preserveSharedPromotionDiscounts = false;
            _preserveSharedSnapshotCatalogValues = false;
            _recoveryOwnerAttemptGuid = null;
            _recoveryOwnerAttemptKey = null;
            var notificationWarning = OnCartChangedBestEffort();
            return new PosCartRecoveryPublicationResult(true, notificationWarning, _revision);
        }
        finally
        {
            _recoveryPublicationLock.ExitWriteLock();
        }
    }

    /// <summary>仅回滚 provider 与 attempt 均精确匹配的恢复购物车。</summary>
    internal PosCartRecoveryPublicationResult RollbackRecoveryPublication(CardRecoveryAttemptKey attemptKey)
    {
        _recoveryPublicationLock.EnterWriteLock();
        try
        {
            if (_recoveryOwnerAttemptKey != attemptKey ||
                _recoveryOwnerAttemptGuid != attemptKey.AttemptGuid)
            {
                return new PosCartRecoveryPublicationResult(false, false, _revision);
            }

            _lines.Clear();
            _returnPaymentCapacities.Clear();
            _sharedHeldOrderClaimId = null;
            _preserveSharedPromotionDiscounts = false;
            _preserveSharedSnapshotCatalogValues = false;
            _recoveryOwnerAttemptGuid = null;
            _recoveryOwnerAttemptKey = null;
            var notificationWarning = OnCartChangedBestEffort();
            return new PosCartRecoveryPublicationResult(true, notificationWarning, _revision);
        }
        finally
        {
            _recoveryPublicationLock.ExitWriteLock();
        }
    }

    public void RestoreSnapshot(PosCartSnapshot snapshot)
    {
        using var mutation = EnterMutationScope();
        RestoreSnapshotCore(snapshot);
        OnCartChanged();
    }

    private void RestoreSnapshotCore(PosCartSnapshot snapshot)
    {
        _lines.Clear();
        _returnPaymentCapacities.Clear();
        _sharedHeldOrderClaimId = null;
        _preserveSharedPromotionDiscounts = false;
        _preserveSharedSnapshotCatalogValues = false;
        foreach (var snapshotLine in snapshot.Lines)
        {
            if (!IsPositiveIntegerQuantity(snapshotLine.Quantity))
            {
                throw new InvalidOperationException("Cart line quantity must be a positive integer.");
            }

            CartLine line;
            if (snapshotLine.Kind == CartLineKind.Return)
            {
                line = new CartLine(new ReturnCartLineRequest(
                    snapshotLine.StoreCode,
                    snapshotLine.ProductCode,
                    snapshotLine.ReferenceCode,
                    snapshotLine.DisplayName,
                    snapshotLine.LookupCode,
                    snapshotLine.ItemNumber,
                    snapshotLine.ProductImage,
                    snapshotLine.Quantity,
                    snapshotLine.UnitPrice,
                    snapshotLine.PriceSource,
                    snapshotLine.PriceSourceLabel,
                    snapshotLine.ReturnSourceKey,
                    snapshotLine.OriginalOrderGuid,
                    snapshotLine.OriginalOrderLineGuid,
                    snapshotLine.ReturnReason));
            }
            else if (snapshotLine.Kind == CartLineKind.OpenItem)
            {
                var item = CreateSnapshotItem(snapshotLine);
                line = new CartLine(item, CartLineKind.OpenItem, snapshotLine.UnitPrice);
                line.SetQuantity(snapshotLine.Quantity);
                // 恢复快照时按来源还原折扣，避免促销折扣被误当手工折扣。
                RestoreSnapshotDiscount(line, snapshotLine);
            }
            else
            {
                var item = CreateSnapshotItem(snapshotLine);
                line = new CartLine(item);
                line.SetQuantity(snapshotLine.Quantity);
                line.SetUnitPrice(snapshotLine.UnitPrice);
                // 恢复快照时按来源还原折扣，避免促销折扣被误当手工折扣。
                RestoreSnapshotDiscount(line, snapshotLine);
            }

            // 恢复快照时按快照还原价格 provenance（手工改价/目录价格）。
            line.SetManualPrice(snapshotLine.IsManualPrice);
            _lines.Add(line);
        }

        _sharedHeldOrderClaimId = snapshot.SharedHeldOrderClaimId;
        // 快照用于挂单和支付恢复，必须保留当时金额；后续编辑或规则刷新再重新计算满减。
    }

    /// <summary>
    /// 共享 sale 快照恢复专用入口（跨 iPad→WPF 取单/离线 recall）：
    /// 只允许普通 sale 行与正有限小数数量；金额与折扣来源按快照精确恢复。
    /// 普通 RestoreSnapshot、Add/SetQuantity 与 UI 数量编辑仍严格正整数。
    /// </summary>
    public void RestoreSharedSaleSnapshot(PosCartSnapshot snapshot)
    {
        using var mutation = EnterMutationScope();
        _lines.Clear();
        _returnPaymentCapacities.Clear();
        _sharedHeldOrderClaimId = null;
        _preserveSharedPromotionDiscounts = false;
        _preserveSharedSnapshotCatalogValues = false;
        foreach (var snapshotLine in snapshot.Lines)
        {
            if (snapshotLine.Kind != CartLineKind.Sale)
            {
                throw new InvalidOperationException("Shared sale restore only supports sale lines.");
            }

            if (!IsPositiveFiniteDecimalQuantity(snapshotLine.Quantity))
            {
                throw new InvalidOperationException("Shared sale quantity must be a positive finite number.");
            }

            var item = CreateSnapshotItem(snapshotLine);
            var line = new CartLine(item);
            line.SetSharedSaleQuantity(snapshotLine.Quantity);
            line.SetUnitPrice(snapshotLine.UnitPrice);
            // 恢复快照时按来源还原折扣，避免促销折扣被误当手工折扣。
            RestoreSnapshotDiscount(line, snapshotLine);
            line.SetManualPrice(snapshotLine.IsManualPrice);
            _lines.Add(line);
        }

        _sharedHeldOrderClaimId = snapshot.SharedHeldOrderClaimId;
        _preserveSharedPromotionDiscounts = _lines.Any(line => line.IsAutomaticPromotionDiscount);
        _preserveSharedSnapshotCatalogValues = _lines.Count > 0;
        OnCartChanged();
    }

    private static SellableItemDto CreateSnapshotItem(PosCartLineSnapshot snapshotLine)
    {
        return new SellableItemDto(
            snapshotLine.StoreCode,
            snapshotLine.ProductCode,
            snapshotLine.ReferenceCode,
            snapshotLine.DisplayName,
            snapshotLine.LookupCode,
            snapshotLine.ItemNumber,
            snapshotLine.LookupCode,
            snapshotLine.UnitPrice,
            snapshotLine.PriceSource,
            snapshotLine.PriceSourceLabel,
            1m,
            null,
            snapshotLine.ProductImage,
            snapshotLine.Kind == CartLineKind.Sale
                ? snapshotLine.CatalogDiscountBasisPoints / 10_000m
                : null);
    }

    private void OnCartChanged()
    {
        Interlocked.Increment(ref _revision);
        CartChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool OnCartChangedBestEffort()
    {
        Interlocked.Increment(ref _revision);
        var handlers = CartChanged?.GetInvocationList();
        if (handlers is null)
        {
            return false;
        }

        var warning = false;
        foreach (var callback in handlers)
        {
            try
            {
                ((EventHandler)callback)(this, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                warning = true;
            }
        }

        return warning;
    }

    private IDisposable EnterMutationScope()
    {
        _recoveryPublicationLock.EnterReadLock();
        if (_recoveryOwnerAttemptGuid is not null)
        {
            _recoveryPublicationLock.ExitReadLock();
            throw new InvalidOperationException(
                "The cart is locked while a payment recovery is being finalized.");
        }

        return new RecoveryMutationScope(_recoveryPublicationLock);
    }

    private sealed class RecoveryMutationScope(ReaderWriterLockSlim publicationLock) : IDisposable
    {
        private ReaderWriterLockSlim? _publicationLock = publicationLock;

        public void Dispose()
        {
            Interlocked.Exchange(ref _publicationLock, null)?.ExitReadLock();
        }
    }

    private void ClearSharedHeldOrderBindingIfCartEmpty()
    {
        if (_lines.Count == 0)
        {
            _sharedHeldOrderClaimId = null;
            _preserveSharedPromotionDiscounts = false;
            _preserveSharedSnapshotCatalogValues = false;
        }
    }

    /// <summary>
    /// 主管强制释放后的本地 cart 清理：仅当购物车绑定到指定 claim（Active 取单购物车）
    /// 时整单清空并解绑；其他 claim 或普通购物车一律不动，Prepared 未恢复购物车时绝不误清。
    /// </summary>
    public bool ClearSharedHeldOrderClaim(Guid claimId)
    {
        using var mutation = EnterMutationScope();
        if (_sharedHeldOrderClaimId != claimId)
        {
            return false;
        }

        Clear();
        return true;
    }

    public static bool IsPositiveIntegerQuantity(decimal quantity)
    {
        return quantity > 0m && decimal.Truncate(quantity) == quantity;
    }

    public static bool IsPositiveFiniteDecimalQuantity(decimal quantity)
    {
        return quantity > 0m
            && quantity <= SharedHeldOrderCanonicalConstants.MaxQuantity;
    }

    /// <summary>
    /// 挂单时深拷贝当前自动促销规则：后续目录刷新/重启不得影响已冻结金额。
    /// 无规则时返回 null（旧挂单/无促销挂单语义）。
    /// </summary>
    public IReadOnlyList<CatalogPromotionRuleDto>? CreateFrozenAutomaticPromotionRules()
    {
        if (_automaticPromotionRules.Count == 0)
        {
            return null;
        }

        return _automaticPromotionRules
            .Select(rule => rule with
            {
                Products = rule.Products
                    .Select(product => product with { })
                    .ToArray()
            })
            .ToArray();
    }

    private static void RestoreSnapshotDiscount(CartLine line, PosCartLineSnapshot snapshotLine)
    {
        if (
            snapshotLine.CatalogDiscountBasisPoints > 0 &&
            snapshotLine.DiscountSource == PosCartLineDiscountSource.Promotion)
        {
            throw new InvalidOperationException(
                "Catalog discount cannot coexist with promotion discount state.");
        }

        // 先恢复 catalog 基线，再按有效来源恢复当前折扣；Catalog 不能落成 Manual。
        line.SetCatalogDiscountBasisPoints(snapshotLine.CatalogDiscountBasisPoints);
        switch (snapshotLine.DiscountSource)
        {
            case PosCartLineDiscountSource.Catalog:
                return;
            case PosCartLineDiscountSource.Promotion:
                line.SetPromotionDiscountAmount(snapshotLine.DiscountAmount);
                return;
            case PosCartLineDiscountSource.Manual:
                if (snapshotLine.DiscountPercent is decimal manualDiscountPercent && manualDiscountPercent > 0m)
                {
                    line.SetDiscountPercent(manualDiscountPercent);
                }
                else
                {
                    // Manual + 0 代表整单折扣明确覆盖但该行零分摊，取单时不能复活 catalog。
                    line.SetOrderDiscountAmount(snapshotLine.DiscountAmount);
                }

                return;
        }

        // 旧快照没有来源字段时，沿用原来的安全回退：有百分比/金额的折扣视为手工。
        if (snapshotLine.DiscountPercent is decimal discountPercent && discountPercent > 0m)
        {
            line.SetDiscountPercent(discountPercent);
            return;
        }

        if (snapshotLine.DiscountAmount <= 0m)
        {
            return;
        }

        line.SetDiscountAmount(snapshotLine.DiscountAmount);
    }

    private static PosCartLineDiscountSource MapSnapshotDiscountSource(CartLineDiscountSource source)
    {
        return source switch
        {
            CartLineDiscountSource.Manual => PosCartLineDiscountSource.Manual,
            CartLineDiscountSource.Catalog => PosCartLineDiscountSource.Catalog,
            CartLineDiscountSource.Promotion => PosCartLineDiscountSource.Promotion,
            _ => PosCartLineDiscountSource.None
        };
    }

    private void RefreshDiscountsAndNotify(bool preserveFrozenSharedPromotion = false)
    {
        if (!preserveFrozenSharedPromotion)
        {
            // 任一成功的购物车编辑都会结束共享快照保护；其后的远端目录刷新可按正常路径更新。
            _preserveSharedSnapshotCatalogValues = false;
        }

        var releasedFrozenSharedPromotion =
            !preserveFrozenSharedPromotion && _preserveSharedPromotionDiscounts;
        if (releasedFrozenSharedPromotion)
        {
            // 冻结促销只保护刚恢复的共享快照不受当前目录刷新影响；任何成功的
            // 用户编辑都会使原计算失效，必须切回当前规则（无规则时清掉旧促销）。
            _preserveSharedPromotionDiscounts = false;
        }

        if (releasedFrozenSharedPromotion
            || (_automaticPromotionRules.Count > 0 && !_preserveSharedPromotionDiscounts))
        {
            RefreshAutomaticPromotionDiscounts();
        }

        OnCartChanged();
    }

    private void RefreshAutomaticPromotionDiscounts()
    {
        foreach (var line in _lines)
        {
            line.ClearAutomaticPromotionDiscount();
        }

        if (_automaticPromotionRules.Count == 0 || _lines.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var candidates = _automaticPromotionRules
            .Where(rule =>
                rule.ApplyQuantity > 0 &&
                rule.FixedPrice >= 0m &&
                rule.EffectiveStart <= now &&
                rule.EffectiveEnd >= now &&
                rule.Products.Count > 0)
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        // 后端促销评估：存在互斥促销时，只取优先级最高的一条，保持收银端和后台一致。
        if (candidates.Any(rule => rule.IsExclusive))
        {
            candidates = candidates
                .Where(rule => rule.IsExclusive)
                .OrderByDescending(rule => rule.Priority)
                .Take(1)
                .ToList();
        }

        List<AutomaticPromotionPlan> plans;
        try
        {
            plans = [];
            foreach (var rule in candidates)
            {
                var productWeights = rule.Products
                    .GroupBy(product => NormalizeProductCode(product.ProductCode), StringComparer.OrdinalIgnoreCase)
                    .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                    .ToDictionary(
                        group => group.Key,
                        group => Math.Max(1, group.Last().UnitWeight),
                        StringComparer.OrdinalIgnoreCase);
                if (productWeights.Count == 0)
                {
                    continue;
                }

                var budget = PromotionComputationBudget.CalculateRule(
                    rule.PromotionId,
                    _lines
                        .Where(IsAutomaticPromotionLineEligible)
                        .Select(line => new PromotionBudgetLine(
                            NormalizeProductCode(line.ProductCode),
                            line.Quantity)),
                    productWeights,
                    rule.ApplyQuantity,
                    rule.MaxApplicationsPerOrder);
                plans.Add(new AutomaticPromotionPlan(rule, productWeights, budget));
            }

            PromotionComputationBudget.EnsureOrderLimit(plans.Select(plan => plan.Budget));
        }
        catch (PromotionComputationBudgetExceededException ex)
        {
            // 旧自动促销已在本方法入口清除；手工和目录折扣不受影响，购物车继续可售。
            ConsoleLog.Write(
                "Promotion",
                $"automatic promotion skipped budget-exceeded {ex.ToDiagnosticText()}");
            return;
        }

        var automaticDiscounts = new Dictionary<CartLine, decimal>();
        foreach (var plan in plans)
        {
            ApplyAutomaticPromotionRule(plan, automaticDiscounts);
        }

        foreach (var (line, discountAmount) in automaticDiscounts)
        {
            var amount = Math.Clamp(
                decimal.Round(discountAmount, 2, MidpointRounding.AwayFromZero),
                0m,
                line.GrossAmount);
            if (amount > 0m)
            {
                line.SetAutomaticPromotionDiscountAmount(amount);
            }
        }
    }

    private void ApplyAutomaticPromotionRule(
        AutomaticPromotionPlan plan,
        Dictionary<CartLine, decimal> automaticDiscounts)
    {
        if (plan.Budget.WorkUnits <= 0)
        {
            return;
        }

        // 每次只保留一个 bundle，避免按数量和权重构造整单展开列表。
        var group = new List<PromotionCartUnit>(
            Math.Min(plan.Rule.ApplyQuantity, checked((int)plan.Budget.WorkUnits)));
        foreach (var unit in EnumeratePromotionCartUnits(plan))
        {
            group.Add(unit);
            if (group.Count != plan.Rule.ApplyQuantity)
            {
                continue;
            }

            ApplyPromotionBundle(plan.Rule, group, automaticDiscounts);
            group.Clear();
        }
    }

    private IEnumerable<PromotionCartUnit> EnumeratePromotionCartUnits(AutomaticPromotionPlan plan)
    {
        long emittedUnits = 0;
        foreach (var line in _lines)
        {
            if (!IsAutomaticPromotionLineEligible(line) ||
                !plan.ProductWeights.TryGetValue(NormalizeProductCode(line.ProductCode), out var unitWeight))
            {
                continue;
            }

            var quantity = decimal.ToInt64(line.Quantity);
            for (long quantityIndex = 0; quantityIndex < quantity; quantityIndex++)
            {
                // 权重语义与 Web 端一致：按 qty * UnitWeight 展开同价单位，每个展开单位自身权重视为 1。
                // SortOrder 记录该单位属于本行第几件实物，供组内金额折叠使用。
                for (var weightIndex = 0; weightIndex < unitWeight; weightIndex++)
                {
                    yield return new PromotionCartUnit(line, line.UnitPrice, quantityIndex);
                    emittedUnits++;
                    if (emittedUnits >= plan.Budget.WorkUnits)
                    {
                        yield break;
                    }
                }
            }
        }
    }

    private static void ApplyPromotionBundle(
        CatalogPromotionRuleDto rule,
        IReadOnlyList<PromotionCartUnit> group,
        Dictionary<CartLine, decimal> automaticDiscounts)
    {
        // 关键逻辑：UnitWeight 只用于凑够 ApplyQuantity 的阈值计数，不参与组内金额累加。
        // 同一件实物按权重展开成多个单位，这里必须按 (行, 实物序号) 折叠回去让单价只计一次；
        // 否则权重大于 1 的商品单价会被重复累加，固定总价折扣随之放大，直接少收钱。
        // 该语义与 PromotionEvaluationService.AddGroupDiscount 的 (Line, SortOrder) 分组保持一致。
        var physicalUnits = group
            .GroupBy(unit => (unit.Line, unit.SortOrder))
            .Select(units => units.First())
            .ToList();

        var grossAmount = physicalUnits.Sum(unit => unit.UnitPrice);
        var bundleDiscount = decimal.Round(grossAmount - rule.FixedPrice, 2, MidpointRounding.AwayFromZero);
        if (bundleDiscount <= 0m)
        {
            return;
        }

        var remainingDiscount = bundleDiscount;
        for (var i = 0; i < physicalUnits.Count; i++)
        {
            var unit = physicalUnits[i];
            var unitDiscount = i == physicalUnits.Count - 1
                ? remainingDiscount
                : decimal.Round(bundleDiscount * unit.UnitPrice / grossAmount, 2, MidpointRounding.AwayFromZero);
            unitDiscount = Math.Clamp(unitDiscount, 0m, remainingDiscount);
            if (unitDiscount > 0m)
            {
                automaticDiscounts[unit.Line] = automaticDiscounts.GetValueOrDefault(unit.Line) + unitDiscount;
                remainingDiscount -= unitDiscount;
            }
        }
    }

    private static bool IsAutomaticPromotionLineEligible(CartLine line)
    {
        return
            !line.IsReturnLine &&
            !line.IsOpenItem &&
            line.DiscountSource != CartLineDiscountSource.Manual &&
            // catalog 基线可能舍入为 0，仍必须在固定总价分组前排除。
            line.CatalogDiscountBasisPoints <= 0 &&
            line.GrossAmount > 0m &&
            IsPositiveIntegerQuantity(line.Quantity);
    }

    private static string NormalizeProductCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private void ApplyOrderDiscountAmount(
        decimal discountAmount,
        bool preserveManualOverrideWhenZero)
    {
        var totalGrossAmount = TotalAmount;
        var remainingDiscount = Math.Clamp(
            decimal.Round(discountAmount, 2, MidpointRounding.AwayFromZero),
            0m,
            totalGrossAmount);
        var discountableLines = _lines.Where(line => line.GrossAmount > 0m).ToList();

        if (discountableLines.Count == 0)
        {
            return;
        }

        if (remainingDiscount <= 0m)
        {
            foreach (var line in discountableLines)
            {
                if (preserveManualOverrideWhenZero)
                {
                    // 非零整单百分比可能舍入为 0；仍须冻结 Manual + 0，
                    // 防止 catalog 折扣重新生效。只有用户输入 0 才清除。
                    line.SetOrderDiscountAmount(0m);
                }
                else
                {
                    line.ClearManualDiscount();
                }
            }

            return;
        }

        for (var i = 0; i < discountableLines.Count; i++)
        {
            var line = discountableLines[i];
            var lineDiscount = i == discountableLines.Count - 1
                ? remainingDiscount
                : decimal.Round(discountAmount * line.GrossAmount / totalGrossAmount, 2, MidpointRounding.AwayFromZero);

            lineDiscount = Math.Clamp(lineDiscount, 0m, Math.Min(line.GrossAmount, remainingDiscount));
            line.SetOrderDiscountAmount(lineDiscount);
            remainingDiscount -= lineDiscount;
        }
    }

    private sealed record AutomaticPromotionPlan(
        CatalogPromotionRuleDto Rule,
        IReadOnlyDictionary<string, int> ProductWeights,
        PromotionRuleBudget Budget);

    private sealed record PromotionCartUnit(CartLine Line, decimal UnitPrice, long SortOrder);
}

public sealed record PosCartSnapshot(
    IReadOnlyList<PosCartLineSnapshot> Lines,
    Guid? SharedHeldOrderClaimId = null);

public readonly record struct PosCartRecoveryPublicationResult(
    bool Succeeded,
    bool NotificationWarning,
    long Revision);

public sealed record PosCartLineSnapshot(
    string StoreCode,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string? ItemNumber,
    string? ProductImage,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal? DiscountPercent,
    PriceSourceKind PriceSource,
    string PriceSourceLabel,
    CartLineKind Kind = CartLineKind.Sale,
    string ReturnSourceKey = "",
    Guid? OriginalOrderGuid = null,
    Guid? OriginalOrderLineGuid = null,
    string? ReturnReason = null,
    PosCartLineDiscountSource DiscountSource = PosCartLineDiscountSource.None,
    bool IsManualPrice = false,
    int CatalogDiscountBasisPoints = 0);

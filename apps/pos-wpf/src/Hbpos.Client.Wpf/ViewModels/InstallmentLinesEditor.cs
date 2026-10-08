using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Installments;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>编辑器当前的校验结论；界面据此生成本地化文案（编辑器本身不依赖本地化服务）。</summary>
public enum InstallmentLinesEditorIssue
{
    None = 0,

    /// <summary>有行的数量 / 单价文本无法解析或不合法。</summary>
    InvalidInput,

    /// <summary>没有任何商品行。</summary>
    NoLines,

    /// <summary>行信息不完整（共享规则 ValidateLines 未通过）。</summary>
    InvalidLines,

    /// <summary>新总额低于已付金额。</summary>
    TotalBelowPaid,

    /// <summary>新总额低于分期订单总额下限。</summary>
    TotalBelowMinimum
}

/// <summary>
/// 分期单“修改商品”的编辑状态：不依赖任何 WPF 类型，规则全部来自 <see cref="InstallmentAmendRules"/>，
/// 与服务端落库校验同源，避免界面通过、服务端拒绝的口径漂移。
/// </summary>
public sealed class InstallmentLinesEditor : ObservableObject
{
    public const decimal MaxQuantity = 99999m;
    public const decimal MaxUnitPrice = 1_000_000m;

    private readonly int _originalLineCount;
    private decimal _newTotal;
    private decimal _newBalance;
    private decimal _delta;
    private bool _hasInputErrors;
    private bool _isDirty;
    private InstallmentLinesEditorIssue _issue;

    public InstallmentLinesEditor(
        IReadOnlyList<InstallmentLineDto> originalLines,
        decimal paidAmount,
        decimal originalTotal)
    {
        PaidAmount = RoundMoney(paidAmount);
        OriginalTotal = RoundMoney(originalTotal);
        _originalLineCount = originalLines.Count;
        foreach (var line in originalLines)
        {
            Rows.Add(new InstallmentLineEditRow(this, line, isNew: false));
        }

        Rows.CollectionChanged += OnRowsCollectionChanged;
        Recalculate();
    }

    public ObservableCollection<InstallmentLineEditRow> Rows { get; } = [];

    /// <summary>已付金额（编辑期间不变）。</summary>
    public decimal PaidAmount { get; }

    /// <summary>编辑开始时订单的总额，用于显示差额。</summary>
    public decimal OriginalTotal { get; }

    public decimal NewTotal
    {
        get => _newTotal;
        private set => SetProperty(ref _newTotal, value);
    }

    public decimal NewBalance
    {
        get => _newBalance;
        private set => SetProperty(ref _newBalance, value);
    }

    /// <summary>新总额相对原总额的变化（正 = 增加）。</summary>
    public decimal Delta
    {
        get => _delta;
        private set => SetProperty(ref _delta, value);
    }

    public bool HasInputErrors
    {
        get => _hasInputErrors;
        private set => SetProperty(ref _hasInputErrors, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public InstallmentLinesEditorIssue Issue
    {
        get => _issue;
        private set
        {
            if (SetProperty(ref _issue, value))
            {
                OnPropertyChanged(nameof(RequiredMinimum));
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    /// <summary>总额不达标时提示给收银员的最低总额：低于已付显示已付，低于下限显示下限。</summary>
    public decimal RequiredMinimum => Issue switch
    {
        InstallmentLinesEditorIssue.TotalBelowPaid => PaidAmount,
        InstallmentLinesEditorIssue.TotalBelowMinimum => Math.Max(PaidAmount, InstallmentAmendRules.MinimumTotalAmount),
        _ => 0m
    };

    /// <summary>校验通过且新余额为 0：保存后订单将变为已付清（待提货）。</summary>
    public bool WillBePaidOff => Issue == InstallmentLinesEditorIssue.None && NewBalance <= 0m;

    public bool CanSave => IsDirty && Issue == InstallmentLinesEditorIssue.None;

    /// <summary>
    /// 生成提交用的商品行（保持当前顺序）。存在无法解析的数量 / 单价时返回 null，调用方不得提交。
    /// </summary>
    public IReadOnlyList<InstallmentLineDto>? BuildLines()
    {
        if (Rows.Any(row => row.HasError))
        {
            return null;
        }

        return Rows.Select(row => row.ToLine()).ToList();
    }

    /// <summary>
    /// 加入商品。同一商品（商品编码 + 参考码一致）已在列表中时只增加数量，不重复建行。
    /// 数量按目录的 QuantityFactor 增加（整箱码一次加一箱的件数），无效时按 1 件处理，与购物车加购口径一致。
    /// </summary>
    public InstallmentLineEditRow AddProduct(SellableItemDto item)
    {
        var quantity = item.QuantityFactor > 0m && item.QuantityFactor == decimal.Truncate(item.QuantityFactor)
            ? item.QuantityFactor
            : 1m;
        var existing = Rows.FirstOrDefault(row => row.IsSameProduct(item));
        if (existing is not null)
        {
            existing.AddQuantity(quantity);
            return existing;
        }

        var unitPrice = RoundMoney(item.RetailPrice);
        var line = new InstallmentLineDto(
            Guid.NewGuid(),
            item.ProductCode,
            item.ReferenceCode,
            item.DisplayName,
            item.LookupCode,
            Math.Min(quantity, MaxQuantity),
            unitPrice,
            0m,
            InstallmentAmendRules.CalculateActualAmount(Math.Min(quantity, MaxQuantity), unitPrice, 0m),
            item.ItemNumber);
        var row = new InstallmentLineEditRow(this, line, isNew: true);
        Rows.Add(row);
        return row;
    }

    /// <summary>删除一行；订单至少保留一行商品，只剩一行时拒绝。</summary>
    public bool RemoveRow(InstallmentLineEditRow row)
    {
        if (Rows.Count <= 1 || !Rows.Remove(row))
        {
            return false;
        }

        return true;
    }

    internal bool CanRemoveRows => Rows.Count > 1;

    internal void OnRowChanged() => Recalculate();

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 行数变化会影响“删除”按钮可用性（最后一行不可删）。
        foreach (var row in Rows)
        {
            row.RemoveCommand.NotifyCanExecuteChanged();
        }

        Recalculate();
    }

    private void Recalculate()
    {
        // 汇总全部行的实收：输入有误的行沿用其最后一个合法值，仅供界面显示；有误时 CanSave 一定为 false。
        NewTotal = RoundMoney(Rows.Sum(row => row.ActualAmount));
        NewBalance = InstallmentAmendRules.CalculateBalance(NewTotal, PaidAmount);
        Delta = RoundMoney(NewTotal - OriginalTotal);
        HasInputErrors = Rows.Any(row => row.HasError);
        IsDirty = HasInputErrors ||
            Rows.Count != _originalLineCount ||
            Rows.Any(row => row.IsChangedFromOriginal);
        Issue = ResolveIssue();
        OnPropertyChanged(nameof(WillBePaidOff));
        OnPropertyChanged(nameof(CanSave));
    }

    private InstallmentLinesEditorIssue ResolveIssue()
    {
        if (HasInputErrors)
        {
            return InstallmentLinesEditorIssue.InvalidInput;
        }

        if (Rows.Count == 0)
        {
            return InstallmentLinesEditorIssue.NoLines;
        }

        var lines = Rows.Select(row => row.ToLine()).ToList();
        if (InstallmentAmendRules.ValidateLines(lines) != InstallmentAmendLinesValidation.Valid)
        {
            return InstallmentLinesEditorIssue.InvalidLines;
        }

        return InstallmentAmendRules.ValidateTotal(NewTotal, PaidAmount) switch
        {
            InstallmentAmendLinesValidation.TotalBelowPaid => InstallmentLinesEditorIssue.TotalBelowPaid,
            InstallmentAmendLinesValidation.TotalBelowMinimum => InstallmentLinesEditorIssue.TotalBelowMinimum,
            _ => InstallmentLinesEditorIssue.None
        };
    }

    internal static decimal RoundMoney(decimal amount) =>
        decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// 编辑器中的一行商品。数量 / 单价以文本形式绑定（边输入边校验，不回写文本），
/// 折扣按“每件折扣”随数量缩放，并保证折扣始终小于该行毛额（服务端同样要求）。
/// </summary>
public sealed class InstallmentLineEditRow : ObservableObject
{
    private readonly InstallmentLinesEditor _owner;
    private readonly decimal _originalQuantity;
    private readonly decimal _originalUnitPrice;
    private readonly decimal _originalDiscount;
    // 每件折扣：原折扣 / 原数量。用它而不是固定折扣额，数量变化时折扣才会同比缩放。
    private readonly decimal _perUnitDiscount;
    private string _quantityText;
    private string _unitPriceText;
    private decimal _quantity;
    private decimal _unitPrice;
    private decimal _discountAmount;
    private decimal _actualAmount;
    private bool _isQuantityInvalid;
    private bool _isUnitPriceInvalid;

    internal InstallmentLineEditRow(InstallmentLinesEditor owner, InstallmentLineDto source, bool isNew)
    {
        _owner = owner;
        IsNew = isNew;
        LineGuid = source.InstallmentLineGuid;
        ProductCode = source.ProductCode;
        ReferenceCode = source.ReferenceCode;
        DisplayName = source.DisplayName;
        LookupCode = source.LookupCode;
        ItemNumber = source.ItemNumber;
        _originalQuantity = source.Quantity;
        _originalUnitPrice = source.UnitPrice;
        _originalDiscount = source.DiscountAmount;
        _perUnitDiscount = source.Quantity > 0m ? source.DiscountAmount / source.Quantity : 0m;
        _quantity = source.Quantity;
        _unitPrice = source.UnitPrice;
        _quantityText = FormatQuantity(source.Quantity);
        _unitPriceText = FormatPrice(source.UnitPrice);
        // 目录价 <= 0 的新商品从一开始就是“单价不合法”，必须由收银员填价后才能保存。
        _isUnitPriceInvalid = source.UnitPrice <= 0m;
        IncrementCommand = new RelayCommand(Increment);
        DecrementCommand = new RelayCommand(Decrement, () => _quantity > 1m);
        RemoveCommand = new RelayCommand(() => _owner.RemoveRow(this), () => _owner.CanRemoveRows);
        _discountAmount = source.DiscountAmount;
        _actualAmount = source.ActualAmount;
        Recalculate();
    }

    public Guid LineGuid { get; }

    public string ProductCode { get; }

    public string? ReferenceCode { get; }

    public string DisplayName { get; }

    public string LookupCode { get; }

    public string? ItemNumber { get; }

    /// <summary>本次编辑中新加入的商品行。</summary>
    public bool IsNew { get; }

    public IRelayCommand IncrementCommand { get; }

    public IRelayCommand DecrementCommand { get; }

    public IRelayCommand RemoveCommand { get; }

    public string MetadataDisplay => string.IsNullOrWhiteSpace(ItemNumber)
        ? LookupCode
        : $"{ItemNumber.Trim()}  {LookupCode}";

    public string QuantityText
    {
        get => _quantityText;
        set
        {
            var text = value ?? string.Empty;
            if (!SetProperty(ref _quantityText, text))
            {
                return;
            }

            if (TryParseQuantity(text, out var parsed))
            {
                _quantity = parsed;
                IsQuantityInvalid = false;
            }
            else
            {
                IsQuantityInvalid = true;
            }

            AfterInputChanged();
        }
    }

    public string UnitPriceText
    {
        get => _unitPriceText;
        set
        {
            var text = value ?? string.Empty;
            if (!SetProperty(ref _unitPriceText, text))
            {
                return;
            }

            if (TryParseUnitPrice(text, out var parsed))
            {
                _unitPrice = parsed;
                IsUnitPriceInvalid = false;
            }
            else
            {
                IsUnitPriceInvalid = true;
            }

            AfterInputChanged();
        }
    }

    /// <summary>最后一个合法的数量（输入框内容非法时保持上一个合法值）。</summary>
    public decimal Quantity => _quantity;

    /// <summary>最后一个合法的单价。</summary>
    public decimal UnitPrice => _unitPrice;

    public decimal DiscountAmount
    {
        get => _discountAmount;
        private set
        {
            if (SetProperty(ref _discountAmount, value))
            {
                OnPropertyChanged(nameof(HasDiscount));
            }
        }
    }

    public bool HasDiscount => _discountAmount > 0m;

    public decimal ActualAmount
    {
        get => _actualAmount;
        private set => SetProperty(ref _actualAmount, value);
    }

    public bool IsQuantityInvalid
    {
        get => _isQuantityInvalid;
        private set
        {
            if (SetProperty(ref _isQuantityInvalid, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool IsUnitPriceInvalid
    {
        get => _isUnitPriceInvalid;
        private set
        {
            if (SetProperty(ref _isUnitPriceInvalid, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _isQuantityInvalid || _isUnitPriceInvalid;

    /// <summary>数量或单价与加载时不同（新行恒为已改动）；折扣是派生值，不参与比较。</summary>
    internal bool IsChangedFromOriginal =>
        IsNew || _quantity != _originalQuantity || _unitPrice != _originalUnitPrice;

    internal bool IsSameProduct(SellableItemDto item)
    {
        if (!string.IsNullOrWhiteSpace(ProductCode) && !string.IsNullOrWhiteSpace(item.ProductCode))
        {
            return string.Equals(ProductCode.Trim(), item.ProductCode.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ReferenceCode?.Trim() ?? string.Empty, item.ReferenceCode?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(LookupCode.Trim(), item.LookupCode?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    internal void AddQuantity(decimal amount)
    {
        SetQuantity(Math.Min(InstallmentLinesEditor.MaxQuantity, Math.Floor(_quantity) + amount));
    }

    internal InstallmentLineDto ToLine() => new(
        LineGuid,
        ProductCode,
        ReferenceCode,
        DisplayName,
        LookupCode,
        _quantity,
        _unitPrice,
        _discountAmount,
        _actualAmount,
        ItemNumber);

    private void Increment() => SetQuantity(Math.Min(InstallmentLinesEditor.MaxQuantity, Math.Floor(_quantity) + 1m));

    private void Decrement() => SetQuantity(Math.Max(1m, Math.Ceiling(_quantity) - 1m));

    private void SetQuantity(decimal quantity)
    {
        // 走文本属性，让“步进按钮”和“直接键入”共用同一条解析 / 重算路径。
        QuantityText = FormatQuantity(quantity);
    }

    private void AfterInputChanged()
    {
        Recalculate();
        DecrementCommand.NotifyCanExecuteChanged();
        _owner.OnRowChanged();
    }

    private void Recalculate()
    {
        var gross = InstallmentLinesEditor.RoundMoney(_quantity * _unitPrice);
        // 数量没变就沿用原折扣，保证未改动的行原样提交；数量变了才按每件折扣缩放。
        var discount = _quantity == _originalQuantity
            ? _originalDiscount
            : InstallmentLinesEditor.RoundMoney(_perUnitDiscount * _quantity);
        // 折扣必须严格小于毛额（服务端同样校验）：改小单价 / 数量时夹住折扣，至少留 0.01 的实收。
        discount = Math.Clamp(discount, 0m, Math.Max(0m, gross - 0.01m));
        DiscountAmount = discount;
        ActualAmount = InstallmentAmendRules.CalculateActualAmount(_quantity, _unitPrice, discount);
        OnPropertyChanged(nameof(Quantity));
        OnPropertyChanged(nameof(UnitPrice));
    }

    private bool TryParseQuantity(string text, out decimal quantity)
    {
        quantity = 0m;
        if (!decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        // 未改动的历史行允许保留小数数量（称重商品）；一旦改动，数量必须是 1 以上的整数。
        if (parsed == _originalQuantity && parsed > 0m)
        {
            quantity = parsed;
            return true;
        }

        if (parsed < 1m || parsed > InstallmentLinesEditor.MaxQuantity || parsed != decimal.Truncate(parsed))
        {
            return false;
        }

        quantity = parsed;
        return true;
    }

    private static bool TryParseUnitPrice(string text, out decimal unitPrice)
    {
        unitPrice = 0m;
        var normalized = text.Trim().TrimStart('$').Trim();
        if (!decimal.TryParse(
                normalized,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        var rounded = InstallmentLinesEditor.RoundMoney(parsed);
        if (rounded <= 0m || rounded > InstallmentLinesEditor.MaxUnitPrice)
        {
            return false;
        }

        unitPrice = rounded;
        return true;
    }

    private static string FormatQuantity(decimal quantity) =>
        quantity.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatPrice(decimal price) =>
        price.ToString("0.00", CultureInfo.InvariantCulture);
}

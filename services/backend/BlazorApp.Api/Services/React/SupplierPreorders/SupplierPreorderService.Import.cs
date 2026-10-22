using System.Text.Json;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

public sealed partial class SupplierPreorderService
{
    private const long MaxUploadBytes = 20 * 1024 * 1024;

    public async Task<SupplierPreorderParseResultDto> ParseFileAsync(
        Stream stream,
        string fileName,
        string contentType,
        long length,
        string? localSupplierCode
    )
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (extension == ".xls")
        {
            throw new SupplierPreorderException("暂不支持 .xls，请在 Excel 里另存为 .xlsx 后再上传。", "XLS_NOT_SUPPORTED");
        }

        if (extension is not ".xlsx" and not ".xlsm")
        {
            throw new SupplierPreorderException("只支持 .xlsx / .xlsm 文件。", "UNSUPPORTED_FILE");
        }

        if (length <= 0 || length > MaxUploadBytes)
        {
            throw new SupplierPreorderException("文件为空或超过 20MB。", "FILE_SIZE");
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        var bytes = buffer.ToArray();

        SupplierPreorderParseResultDto result;
        try
        {
            using var parseStream = new MemoryStream(bytes, writable: false);
            result = SupplierPreorderSheetParser.ParseWorkbook(parseStream, fileName!);
        }
        catch (SupplierPreorderSheetParser.ParseException ex)
        {
            throw new SupplierPreorderException(ex.Message, ex.ErrorCode);
        }

        await ApplyImportProfileAsync(result, localSupplierCode);

        // 原表暂存，建表时关联；顺手清理 3 天前没被用掉的暂存文件
        var file = new SupplierPreorderSourceFile
        {
            FileName = fileName!.Length > 260 ? fileName[..260] : fileName,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            SizeBytes = bytes.Length,
            Content = bytes,
            UploadedAtUtc = NowUtc,
            UploadedBy = CurrentUserName,
        };
        await _db.Insertable(file).ExecuteCommandAsync();
        var cutoff = NowUtc.AddDays(-3);
        await _db.Deleteable<SupplierPreorderSourceFile>()
            .Where(f => f.FormGuid == null && f.UploadedAtUtc < cutoff)
            .ExecuteCommandAsync();

        result.SourceFileGuid = file.FileGuid;
        return result;
    }

    public async Task<SupplierPreorderParseResultDto> ParsePastedAsync(SupplierPreorderParsePastedRequestDto request)
    {
        if (request.Rows is null || request.Rows.Count == 0)
        {
            throw new SupplierPreorderException("粘贴内容为空。", "EMPTY_PASTE");
        }

        SupplierPreorderParseResultDto result;
        try
        {
            result = SupplierPreorderSheetParser.ParsePasted(request.Rows);
        }
        catch (SupplierPreorderSheetParser.ParseException ex)
        {
            throw new SupplierPreorderException(ex.Message, ex.ErrorCode);
        }

        await ApplyImportProfileAsync(result, request.LocalSupplierCode);
        return result;
    }

    /// <summary>同一供应商、同样表头以前保存过列映射的，直接套用。</summary>
    private async Task ApplyImportProfileAsync(SupplierPreorderParseResultDto result, string? localSupplierCode)
    {
        if (string.IsNullOrWhiteSpace(localSupplierCode))
        {
            return;
        }

        var profile = await _db.Queryable<SupplierPreorderImportProfile>()
            .FirstAsync(p => p.LocalSupplierCode == localSupplierCode && p.HeaderSignature == result.HeaderSignature);
        if (profile is null)
        {
            return;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredProfile>(profile.MappingJson, JsonOptions);
            if (stored?.Mapping is null || !MappingFitsColumns(stored.Mapping, result.Columns.Count))
            {
                return;
            }

            result.SuggestedMapping = stored.Mapping;
            if (stored.ExportQtyUnit is "Cartons" or "Pieces")
            {
                result.SuggestedExportQtyUnit = stored.ExportQtyUnit;
            }

            result.MappingFromProfile = true;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "供应商预订单列映射配置损坏，忽略：{Supplier}", localSupplierCode);
        }
    }

    private sealed class StoredProfile
    {
        public SupplierPreorderColumnMappingDto? Mapping { get; set; }
        public string? ExportQtyUnit { get; set; }
    }

    private static bool MappingFitsColumns(SupplierPreorderColumnMappingDto mapping, int columnCount)
    {
        var values = new[]
        {
            mapping.ItemCode, mapping.Name, mapping.Price, mapping.CartonQty, mapping.PriceFallback, mapping.CartonQtyFallback,
            mapping.Barcode, mapping.Unit, mapping.CartonCbm,
            mapping.Image, mapping.Category, mapping.PrevYearItemCode, mapping.Notes, mapping.Seq, mapping.OrderQty,
        };
        return values.All(v => v is null || (v >= 0 && v < columnCount));
    }

    private static void ValidateRequiredMapping(SupplierPreorderColumnMappingDto mapping)
    {
        var missing = new List<string>();
        if (mapping.ItemCode is null) missing.Add("货号");
        if (mapping.Name is null) missing.Add("名称");
        if (mapping.Price is null) missing.Add("进价");
        if (mapping.CartonQty is null) missing.Add("箱规");
        if (missing.Count > 0)
        {
            throw new SupplierPreorderException($"列映射缺少必填列：{string.Join("、", missing)}", "MAPPING_REQUIRED");
        }
    }

    /// <summary>按条码、货号、去年货号批量取我方商品（分批 500 个，避开 SQL Server 2100 参数上限）。</summary>
    private async Task<List<SupplierPreorderProductCandidate>> LoadMatchCandidatesAsync(IReadOnlyList<SupplierPreorderNormalizedRow> rows)
    {
        var barcodes = rows.Select(r => r.Barcode).Where(b => !string.IsNullOrEmpty(b)).Distinct().Cast<string>().ToList();
        var codes = rows.SelectMany(r => new[] { r.SupplierItemCode, r.PrevYearItemCode })
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
        var result = new Dictionary<string, SupplierPreorderProductCandidate>(StringComparer.Ordinal);

        async Task LoadAsync(Func<List<string>, ISugarQueryable<Product>> build, List<string> keys)
        {
            foreach (var chunk in keys.Chunk(500))
            {
                var list = chunk.ToList();
                var products = await build(list)
                    .Select(p => new SupplierPreorderProductCandidate
                    {
                        ProductCode = p.ProductCode!,
                        ItemNumber = p.ItemNumber,
                        Barcode = p.Barcode,
                        ProductName = p.ProductName,
                        PurchasePrice = p.PurchasePrice,
                        ProductImage = p.ProductImage,
                        LocalSupplierCode = p.LocalSupplierCode,
                        IsActive = p.IsActive,
                    })
                    .ToListAsync();
                foreach (var product in products.Where(p => !string.IsNullOrEmpty(p.ProductCode)))
                {
                    result[product.ProductCode] = product;
                }
            }
        }

        await LoadAsync(list => _db.Queryable<Product>().Where(p => !p.IsDeleted && list.Contains(p.Barcode!)), barcodes);
        await LoadAsync(list => _db.Queryable<Product>().Where(p => !p.IsDeleted && list.Contains(p.ItemNumber!)), codes);
        return result.Values.ToList();
    }

    public async Task<SupplierPreorderPreviewResultDto> PreviewAsync(SupplierPreorderPreviewRequestDto request)
    {
        ValidateRequiredMapping(request.Mapping);
        var rows = SupplierPreorderItemRules.Normalize(request.Rows ?? new(), request.Mapping);
        var candidates = await LoadMatchCandidatesAsync(rows);
        var matches = SupplierPreorderItemRules.Match(rows, candidates, request.LocalSupplierCode);

        var counts = new SupplierPreorderPreviewCountsDto { Total = rows.Count };
        var items = new List<SupplierPreorderPreviewItemDto>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var match = matches[i];
            var product = match.Product;
            var rate = product is null ? null : SupplierPreorderItemRules.PriceChangeRate(row.Price, product.PurchasePrice);
            var status = row.Invalid ? "Invalid"
                : product is null ? "New"
                : match.Warnings.Count > 0 ? "Warning"
                : "Matched";
            switch (status)
            {
                case "Invalid": counts.Invalid++; break;
                case "New": counts.New++; break;
                case "Warning": counts.Warning++; break;
                default: counts.Matched++; break;
            }

            if (rate is decimal r && Math.Abs(r) >= 0.005m)
            {
                counts.PriceChanged++;
            }

            var productImage = product?.ProductImage;
            items.Add(new SupplierPreorderPreviewItemDto
            {
                RowNo = row.RowNo,
                Seq = row.Seq,
                SupplierItemCode = row.SupplierItemCode,
                Barcode = row.Barcode,
                Name = row.Name,
                Unit = row.Unit,
                CartonQty = row.CartonQty,
                CartonCbm = row.CartonCbm,
                Price = row.Price,
                Category = row.Category,
                PrevYearItemCode = row.PrevYearItemCode,
                ImageUrl = productImage is not null && productImage.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? productImage : row.ImageSourceUrl,
                Notes = row.Notes,
                MatchMethod = match.MatchMethod,
                MatchedProductCode = product?.ProductCode,
                MatchedItemNumber = product?.ItemNumber,
                MatchedProductName = product?.ProductName,
                LastPurchasePrice = product?.PurchasePrice,
                PriceChangeRate = rate,
                Status = status,
                Issues = row.Issues.Concat(match.Warnings).ToList(),
            });
        }

        return new SupplierPreorderPreviewResultDto { Items = items, Counts = counts };
    }

    public async Task<SupplierPreorderFormDetailDto> CreateFormAsync(SupplierPreorderCreateFormDto request)
    {
        ValidateRequiredMapping(request.Mapping);
        var header = ValidateHeader(request.Title, request.HolidayType, request.SeasonYear, request.ContainerCapacityCbm, request.ExportQtyUnit);
        var supplier = await _db.Queryable<HBLocalSupplier>()
            .FirstAsync(s => !s.IsDeleted && s.LocalSupplierCode == request.LocalSupplierCode);
        if (supplier is null)
        {
            throw new SupplierPreorderException("请选择供应商", "SUPPLIER_REQUIRED");
        }

        // 服务端重新规范化与匹配，不信任前端预览结果；不能导入的行直接跳过
        var rows = SupplierPreorderItemRules.Normalize(request.Rows ?? new(), request.Mapping);
        var validRows = rows.Where(r => !r.Invalid).ToList();
        if (validRows.Count == 0)
        {
            throw new SupplierPreorderException("没有可导入的商品行", "NO_VALID_ROWS");
        }

        var candidates = await LoadMatchCandidatesAsync(validRows);
        var matches = SupplierPreorderItemRules.Match(validRows, candidates, request.LocalSupplierCode);

        SupplierPreorderSourceFile? sourceFile = null;
        if (!string.IsNullOrWhiteSpace(request.SourceFileGuid))
        {
            sourceFile = await _db.Queryable<SupplierPreorderSourceFile>()
                .Where(f => f.FileGuid == request.SourceFileGuid && f.FormGuid == null)
                .Select(f => new SupplierPreorderSourceFile { FileGuid = f.FileGuid, FileName = f.FileName })
                .FirstAsync();
        }

        var now = NowUtc;
        var form = new SupplierPreorderForm
        {
            LocalSupplierCode = supplier.LocalSupplierCode,
            SupplierName = supplier.Name.Length > 200 ? supplier.Name[..200] : supplier.Name,
            HolidayType = header.HolidayType,
            SeasonYear = request.SeasonYear,
            Title = header.Title,
            QuoteNo = Trim(request.QuoteNo, 100),
            OfferDate = request.OfferDate?.Date,
            DeadlineAtUtc = request.DeadlineAtUtc is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null,
            ContainerType = Trim(request.ContainerType, 50),
            ContainerCapacityCbm = request.ContainerCapacityCbm,
            PriceTerms = Trim(request.PriceTerms, 200),
            HistoryFrom = request.HistoryFrom?.Date,
            HistoryTo = request.HistoryTo?.Date,
            Status = SupplierPreorderFormStatuses.Draft,
            ExportQtyUnit = header.ExportQtyUnit,
            ColumnMappingJson = SerializeMapping(new StoredMapping
            {
                SheetName = request.SheetName,
                HeaderRowNo = request.HeaderRowNo,
                HeaderSignature = request.HeaderSignature,
                Mapping = request.Mapping,
            }),
            SourceFileGuid = sourceFile?.FileGuid,
            ItemCount = validRows.Count,
            Notes = Trim(request.Notes, 1000),
            CreatedAtUtc = now,
            CreatedBy = CurrentUserName,
            UpdatedAtUtc = now,
            UpdatedBy = CurrentUserName,
        };

        var items = validRows.Select((row, index) =>
        {
            var match = matches[index];
            var notes = match.Warnings.Count > 0 ? string.Join("；", match.Warnings) : null;
            return new SupplierPreorderFormItem
            {
                FormGuid = form.FormGuid,
                Seq = row.Seq,
                // 只有上传的原表才有可靠行号（填回原表用）；粘贴导入记 0
                SourceRowNo = sourceFile is null ? 0 : row.RowNo,
                SortOrder = index + 1,
                SupplierItemCode = row.SupplierItemCode,
                Barcode = row.Barcode,
                Name = row.Name,
                Unit = row.Unit,
                CartonQty = row.CartonQty,
                CartonCbm = row.CartonCbm,
                Price = row.Price,
                Category = row.Category,
                PrevYearItemCode = row.PrevYearItemCode,
                ImageSourceUrl = row.ImageSourceUrl,
                Notes = row.Notes,
                MatchedProductCode = match.Product?.ProductCode,
                MatchMethod = match.MatchMethod,
                MatchNote = notes is { Length: > 300 } ? notes[..300] : notes,
                IsActive = true,
            };
        }).ToList();

        await RunInTransactionAsync(async () =>
        {
            await _db.Insertable(form).ExecuteCommandAsync();
            foreach (var chunk in items.Chunk(500))
            {
                await _db.Insertable(chunk.ToList()).ExecuteCommandAsync();
            }

            if (sourceFile is not null)
            {
                await _db.Updateable<SupplierPreorderSourceFile>()
                    .SetColumns(f => f.FormGuid == form.FormGuid)
                    .Where(f => f.FileGuid == sourceFile.FileGuid)
                    .ExecuteCommandAsync();
            }

            await SaveImportProfileAsync(form.LocalSupplierCode, request.HeaderSignature, request.Mapping, form.ExportQtyUnit);
        });

        _logger.LogInformation(
            "供应商预订单已导入：{FormGuid} {Title}，{Count} 款，跳过不能导入 {Invalid} 行",
            form.FormGuid, form.Title, items.Count, rows.Count - validRows.Count);
        return await GetFormAsync(form.FormGuid);
    }

    private async Task SaveImportProfileAsync(string supplierCode, string? headerSignature, SupplierPreorderColumnMappingDto mapping, string exportQtyUnit)
    {
        if (string.IsNullOrWhiteSpace(headerSignature) || headerSignature.Length > 64)
        {
            return;
        }

        var json = JsonSerializer.Serialize(new StoredProfile { Mapping = mapping, ExportQtyUnit = exportQtyUnit }, JsonOptions);
        var existing = await _db.Queryable<SupplierPreorderImportProfile>()
            .FirstAsync(p => p.LocalSupplierCode == supplierCode && p.HeaderSignature == headerSignature);
        if (existing is null)
        {
            await _db.Insertable(new SupplierPreorderImportProfile
            {
                LocalSupplierCode = supplierCode,
                HeaderSignature = headerSignature,
                MappingJson = json,
                UpdatedAtUtc = NowUtc,
                UpdatedBy = CurrentUserName,
            }).ExecuteCommandAsync();
        }
        else
        {
            existing.MappingJson = json;
            existing.UpdatedAtUtc = NowUtc;
            existing.UpdatedBy = CurrentUserName;
            await _db.Updateable(existing).ExecuteCommandAsync();
        }
    }

    private sealed record ValidHeader(string Title, string HolidayType, string ExportQtyUnit);

    private static ValidHeader ValidateHeader(string? title, string? holidayType, int seasonYear, decimal? capacity, string? exportQtyUnit)
    {
        var trimmedTitle = title?.Trim();
        if (string.IsNullOrEmpty(trimmedTitle))
        {
            throw new SupplierPreorderException("标题不能为空", "TITLE_REQUIRED");
        }

        if (!HolidayTypes.Contains(holidayType))
        {
            throw new SupplierPreorderException("节日类型无效", "HOLIDAY_INVALID");
        }

        if (seasonYear is < 2020 or > 2100)
        {
            throw new SupplierPreorderException("年份无效", "YEAR_INVALID");
        }

        if (capacity is < 0 or > 1000)
        {
            throw new SupplierPreorderException("柜容量无效", "CAPACITY_INVALID");
        }

        if (exportQtyUnit is not "Cartons" and not "Pieces")
        {
            throw new SupplierPreorderException("填回原表数量单位无效", "EXPORT_UNIT_INVALID");
        }

        return new ValidHeader(trimmedTitle.Length > 200 ? trimmedTitle[..200] : trimmedTitle, holidayType!, exportQtyUnit);
    }

    private static string? Trim(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    public async Task<List<SupplierPreorderSupplierOptionDto>> GetSuppliersAsync() =>
        (await _db.Queryable<HBLocalSupplier>()
            .Where(s => !s.IsDeleted && s.Status == 1)
            .OrderBy(s => s.LocalSupplierCode)
            .Select(s => new { s.LocalSupplierCode, s.Name })
            .ToListAsync())
        .Select(s => new SupplierPreorderSupplierOptionDto { Code = s.LocalSupplierCode, Name = s.Name })
        .ToList();

    public async Task<List<SupplierPreorderStoreRefDto>> GetActiveStoresAsync() =>
        (await LoadActiveStoresAsync()).Select(s => ToStoreRef(s.StoreGUID, s.StoreCode, s.StoreName)).ToList();
}

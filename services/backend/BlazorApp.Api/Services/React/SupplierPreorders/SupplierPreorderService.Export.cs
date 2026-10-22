using System.Text.RegularExpressions;
using BlazorApp.Shared.Models;
using ClosedXML.Excel;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>
/// 导出：
/// - 通用格式：只列有下单的商品，每家开启的分店一列箱数，另附分店合计页；
/// - 填回原表：以导入时暂存的原表为底，原表「下单数量」列写全部分店合计，表格右侧追加各分店一列与合计列，
///   数量单位按订单表的 ExportQtyUnit（Dats 原表各店列是件数 = 箱数 × Inner）。只有上传导入的订单表才有原表。
/// </summary>
public sealed partial class SupplierPreorderService
{
    public async Task<SupplierPreorderExportFile> ExportAsync(string formGuid, string format)
    {
        var form = await RequireFormAsync(formGuid);
        var snapshot = await LoadSnapshotAsync(form);
        return format switch
        {
            "original" => await ExportOriginalAsync(snapshot),
            "generic" => ExportGeneric(snapshot),
            _ => throw new SupplierPreorderException("导出格式无效", "EXPORT_FORMAT_INVALID"),
        };
    }

    private static SupplierPreorderExportFile ExportGeneric(FormSnapshot snapshot)
    {
        var counted = snapshot.CountedLines().ToList();
        var stores = snapshot.EnabledStores
            .Where(s => counted.Any(x => string.Equals(x.Order.StoreGuid, s.StoreGuid, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var byItem = counted.GroupBy(x => x.Item.ItemGuid).ToDictionary(g => g.Key, g => g.ToList());

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Summary");
        var headers = new List<string> { "Seq", "Item Code", "Barcode", "Description", "Unit", "Ctn Qty", "Unit Price" };
        headers.AddRange(stores.Select(s => $"{s.StoreName} (ctn)"));
        headers.AddRange(new[] { "Total Cartons", "Total Pieces", "Total Value", "Total CBM" });
        for (var c = 0; c < headers.Count; c++)
        {
            sheet.Cell(1, c + 1).Value = headers[c];
        }

        var row = 2;
        foreach (var item in snapshot.Items.Where(i => i.IsActive && byItem.ContainsKey(i.ItemGuid)))
        {
            var lines = byItem[item.ItemGuid];
            var cartons = lines.Sum(x => x.Line.Cartons);
            var col = 1;
            sheet.Cell(row, col++).Value = item.Seq;
            sheet.Cell(row, col++).Value = item.SupplierItemCode;
            sheet.Cell(row, col++).SetValue(item.Barcode ?? string.Empty);
            sheet.Cell(row, col++).Value = item.Name;
            sheet.Cell(row, col++).Value = item.Unit ?? string.Empty;
            sheet.Cell(row, col++).Value = item.CartonQty;
            sheet.Cell(row, col++).Value = item.Price;
            foreach (var store in stores)
            {
                var value = lines.Where(x => string.Equals(x.Order.StoreGuid, store.StoreGuid, StringComparison.OrdinalIgnoreCase)).Sum(x => x.Line.Cartons);
                if (value > 0)
                {
                    sheet.Cell(row, col).Value = value;
                }

                col++;
            }

            sheet.Cell(row, col++).Value = cartons;
            sheet.Cell(row, col++).Value = cartons * item.CartonQty;
            sheet.Cell(row, col++).Value = LineAmount(cartons, item);
            sheet.Cell(row, col).Value = Math.Round(LineCbm(cartons, item), 3);
            row++;
        }

        // 合计行
        sheet.Cell(row, 4).Value = "TOTAL";
        var totalCol = 8;
        foreach (var store in stores)
        {
            sheet.Cell(row, totalCol++).Value = counted.Where(x => string.Equals(x.Order.StoreGuid, store.StoreGuid, StringComparison.OrdinalIgnoreCase)).Sum(x => x.Line.Cartons);
        }

        sheet.Cell(row, totalCol++).Value = counted.Sum(x => x.Line.Cartons);
        sheet.Cell(row, totalCol++).Value = counted.Sum(x => x.Line.Cartons * x.Item.CartonQty);
        sheet.Cell(row, totalCol++).Value = counted.Sum(x => LineAmount(x.Line.Cartons, x.Item));
        sheet.Cell(row, totalCol).Value = Math.Round(counted.Sum(x => LineCbm(x.Line.Cartons, x.Item)), 3);
        sheet.Row(1).Style.Font.Bold = true;
        sheet.Row(row).Style.Font.Bold = true;
        sheet.Column(7).Style.NumberFormat.Format = "0.00";
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, Math.Min(row, 300));

        var storeSheet = workbook.Worksheets.Add("Stores");
        var storeHeaders = new[] { "Store", "Status", "Items", "Cartons", "Pieces", "Value", "CBM" };
        for (var c = 0; c < storeHeaders.Length; c++)
        {
            storeSheet.Cell(1, c + 1).Value = storeHeaders[c];
        }

        var storeRow = 2;
        foreach (var store in snapshot.EnabledStores)
        {
            var lines = counted.Where(x => string.Equals(x.Order.StoreGuid, store.StoreGuid, StringComparison.OrdinalIgnoreCase)).ToList();
            var order = snapshot.Orders.FirstOrDefault(o => string.Equals(o.StoreGuid, store.StoreGuid, StringComparison.OrdinalIgnoreCase));
            storeSheet.Cell(storeRow, 1).Value = store.StoreName;
            storeSheet.Cell(storeRow, 2).Value = order?.Status ?? "None";
            storeSheet.Cell(storeRow, 3).Value = lines.Count;
            storeSheet.Cell(storeRow, 4).Value = lines.Sum(x => x.Line.Cartons);
            storeSheet.Cell(storeRow, 5).Value = lines.Sum(x => x.Line.Cartons * x.Item.CartonQty);
            storeSheet.Cell(storeRow, 6).Value = lines.Sum(x => LineAmount(x.Line.Cartons, x.Item));
            storeSheet.Cell(storeRow, 7).Value = Math.Round(lines.Sum(x => LineCbm(x.Line.Cartons, x.Item)), 3);
            storeRow++;
        }

        storeSheet.Row(1).Style.Font.Bold = true;
        storeSheet.Columns().AdjustToContents();

        return new SupplierPreorderExportFile(SaveWorkbook(workbook), XlsxContentType, SafeFileName($"{snapshot.Form.Title} - 汇总.xlsx"));
    }

    private async Task<SupplierPreorderExportFile> ExportOriginalAsync(FormSnapshot snapshot)
    {
        var form = snapshot.Form;
        var stored = ReadStoredMapping(form.ColumnMappingJson);
        if (string.IsNullOrEmpty(form.SourceFileGuid) || stored is null || string.IsNullOrEmpty(stored.SheetName))
        {
            throw new SupplierPreorderException("这张订单表是粘贴导入的，没有原表可以填回，请用通用格式导出", "NO_SOURCE_FILE");
        }

        var file = await _db.Queryable<SupplierPreorderSourceFile>().FirstAsync(f => f.FileGuid == form.SourceFileGuid);
        if (file is null)
        {
            throw new SupplierPreorderException("原表文件已丢失，请用通用格式导出", "NO_SOURCE_FILE");
        }

        var counted = snapshot.CountedLines().ToList();
        var stores = snapshot.EnabledStores
            .Where(s => counted.Any(x => string.Equals(x.Order.StoreGuid, s.StoreGuid, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var pieces = form.ExportQtyUnit == "Pieces";
        var unitLabel = pieces ? "pcs" : "ctn";
        int Qty(int cartons, SupplierPreorderFormItem item) => pieces ? cartons * item.CartonQty : cartons;

        using var workbook = SupplierPreorderSheetParser.OpenWorkbook(file.Content);
        if (!workbook.TryGetWorksheet(stored.SheetName, out var sheet))
        {
            throw new SupplierPreorderException($"原表里找不到工作表「{stored.SheetName}」", "SOURCE_SHEET_MISSING");
        }

        var headerRow = Math.Max(stored.HeaderRowNo, 1);
        var firstNewColumn = (sheet.LastColumnUsed()?.ColumnNumber() ?? 1) + 2;
        for (var i = 0; i < stores.Count; i++)
        {
            sheet.Cell(headerRow, firstNewColumn + i).Value = $"{stores[i].StoreName} ({unitLabel})";
        }

        var totalColumn = firstNewColumn + stores.Count;
        sheet.Cell(headerRow, totalColumn).Value = $"HB TOTAL ({unitLabel})";
        sheet.Range(headerRow, firstNewColumn, headerRow, totalColumn).Style.Font.Bold = true;

        var byItem = counted.GroupBy(x => x.Item.ItemGuid).ToDictionary(g => g.Key, g => g.ToList());
        var orderQtyColumn = stored.Mapping.OrderQty is int index ? index + 1 : (int?)null;
        var written = 0;
        foreach (var item in snapshot.Items.Where(i => i.IsActive && i.SourceRowNo > headerRow))
        {
            if (!byItem.TryGetValue(item.ItemGuid, out var lines))
            {
                // 没下单的行：原表下单数量列清成 0。上传的原表可能是某家店填过的副本，不清会把旧数量带给供应商
                if (orderQtyColumn.HasValue)
                {
                    sheet.Cell(item.SourceRowNo, orderQtyColumn.Value).Value = 0;
                }

                continue;
            }

            var total = 0;
            for (var i = 0; i < stores.Count; i++)
            {
                var cartons = lines.Where(x => string.Equals(x.Order.StoreGuid, stores[i].StoreGuid, StringComparison.OrdinalIgnoreCase)).Sum(x => x.Line.Cartons);
                if (cartons > 0)
                {
                    sheet.Cell(item.SourceRowNo, firstNewColumn + i).Value = Qty(cartons, item);
                }

                total += cartons;
            }

            sheet.Cell(item.SourceRowNo, totalColumn).Value = Qty(total, item);
            if (orderQtyColumn.HasValue)
            {
                // 原表自己的下单数量列（KFC「Cartons Ordered」、Brazco「ORDER # CARTONS HERE」）写合计，原表里的件数/金额公式会自动算
                sheet.Cell(item.SourceRowNo, orderQtyColumn.Value).Value = Qty(total, item);
            }

            written++;
        }

        // 打开时让 Excel 全部重算，原表的件数、金额、体积合计公式才会更新
        workbook.FullCalculationOnLoad = true;
        _logger.LogInformation("供应商预订单填回原表：{FormGuid} 写入 {Count} 款，{Stores} 家分店", form.FormGuid, written, stores.Count);
        var baseName = Path.GetFileNameWithoutExtension(file.FileName);
        return new SupplierPreorderExportFile(SaveWorkbook(workbook), XlsxContentType, SafeFileName($"{baseName} - HB.xlsx"));
    }

    public async Task<SupplierPreorderExportFile> ExportStoreOrderAsync(string formGuid, string storeGuid)
    {
        var (snapshot, _, myStores) = await LoadOrderingAsync(formGuid);
        var store = RequireMyStore(myStores, storeGuid);
        var order = snapshot.Orders.FirstOrDefault(o => string.Equals(o.StoreGuid, store.StoreGuid, StringComparison.OrdinalIgnoreCase));
        var lines = order is null
            ? new List<SupplierPreorderStoreOrderLine>()
            : snapshot.Lines.Where(l => l.OrderGuid == order.OrderGuid && l.Cartons > 0).ToList();
        var cartonsByItem = lines.ToDictionary(l => l.ItemGuid, l => l.Cartons);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Order");
        var headers = new[] { "Seq", "Item Code", "Barcode", "Description", "Unit", "Ctn Qty", "Unit Price", "Cartons", "Pieces", "Value", "CBM" };
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = headers[c];
        }

        var row = 2;
        foreach (var item in snapshot.Items.Where(i => i.IsActive && cartonsByItem.ContainsKey(i.ItemGuid)))
        {
            var cartons = cartonsByItem[item.ItemGuid];
            sheet.Cell(row, 1).Value = item.Seq;
            sheet.Cell(row, 2).Value = item.SupplierItemCode;
            sheet.Cell(row, 3).SetValue(item.Barcode ?? string.Empty);
            sheet.Cell(row, 4).Value = item.Name;
            sheet.Cell(row, 5).Value = item.Unit ?? string.Empty;
            sheet.Cell(row, 6).Value = item.CartonQty;
            sheet.Cell(row, 7).Value = item.Price;
            sheet.Cell(row, 8).Value = cartons;
            sheet.Cell(row, 9).Value = cartons * item.CartonQty;
            sheet.Cell(row, 10).Value = LineAmount(cartons, item);
            sheet.Cell(row, 11).Value = Math.Round(LineCbm(cartons, item), 3);
            row++;
        }

        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, Math.Min(row, 300));
        return new SupplierPreorderExportFile(SaveWorkbook(workbook), XlsxContentType, SafeFileName($"{snapshot.Form.Title} - {store.StoreName}.xlsx"));
    }

    private static byte[] SaveWorkbook(XLWorkbook workbook)
    {
        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    private static string SafeFileName(string name)
    {
        var cleaned = Regex.Replace(name, @"[\\/:*?""<>|]+", " ").Trim();
        return cleaned.Length > 150 ? cleaned[..150] + ".xlsx" : cleaned;
    }
}

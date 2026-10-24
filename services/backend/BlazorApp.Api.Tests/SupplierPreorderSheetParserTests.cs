using BlazorApp.Api.Services.React.SupplierPreorders;
using ClosedXML.Excel;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 供应商订单表解析：用 ClosedXML 现场造出与真实 KFC / Dats / Brazco 原表同结构的工作簿（结构取自 2026 年三份原表）。
/// </summary>
public sealed class SupplierPreorderSheetParserTests
{
    private static MemoryStream Save(XLWorkbook workbook)
    {
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    /// <summary>KFC（Uncle Bills）：表头在第 14–15 行两行拼接，「Product」横跨 H:I 两列合并，图片列是 HYPERLINK 公式，尾部有合计行。</summary>
    private static MemoryStream BuildKfcWorkbook()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("PriceList");
        sheet.Cell(2, 2).Value = "Order Form";
        sheet.Cell(4, 2).Value = "Customer Name:";
        sheet.Cell(4, 4).Value = "Australia - Metro Delivery Only";
        sheet.Cell(5, 2).Value = "Terms:";
        sheet.Cell(5, 4).Value = "$AU Prices FIW";
        sheet.Cell(5, 6).Value = "40GP";
        sheet.Cell(6, 2).Value = "Christmas 2026";
        sheet.Cell(6, 4).Value = "40GP Container";
        sheet.Cell(7, 2).Value = "QT0013074";
        sheet.Cell(8, 2).Value = "Offer Date:";
        sheet.Cell(8, 4).Value = new DateTime(2026, 2, 5);
        sheet.Cell(9, 2).Value = "Orders to be completed by:";
        sheet.Cell(9, 4).Value = new DateTime(2026, 3, 6);

        string[] top = { "Seq", "Image", "Stock", "Description", "Unit", "Ctn", "Carton", "Product", "", "", "Cartons", "Pieces", "Total", "Total", "Notes" };
        string[] bottom = { "No", "Link", "Code", "", "", "Qty", "Cub. Mtr.", "Barcode", "Price", "", "Ordered", "Ordered", "Cu Mtr", "Value", "" };
        for (var c = 0; c < top.Length; c++)
        {
            if (top[c].Length > 0) sheet.Cell(14, c + 1).Value = top[c];
            if (bottom[c].Length > 0) sheet.Cell(15, c + 1).Value = bottom[c];
        }

        // 第 16 行是表头下的合计 0 行（只有数字，没有货号和名称）
        sheet.Cell(16, 11).Value = 0;
        sheet.Cell(16, 13).Value = 0;

        void Row(int r, int seq, string code, string name, int ctn, double cbm, string barcode, double price)
        {
            sheet.Cell(r, 1).Value = seq;
            sheet.Cell(r, 2).FormulaA1 = $"HYPERLINK((CONCATENATE(\"https://unclebills.hosted-by-files.com/Christmas/2026/\",C{r},\".jpg\")),\"Click Image\")";
            sheet.Cell(r, 3).Value = code;
            sheet.Cell(r, 4).Value = name;
            sheet.Cell(r, 5).Value = "PC";
            sheet.Cell(r, 6).Value = ctn;
            sheet.Cell(r, 7).Value = cbm;
            sheet.Cell(r, 8).Value = double.Parse(barcode, System.Globalization.CultureInfo.InvariantCulture);
            sheet.Cell(r, 9).Value = price;
            sheet.Cell(r, 11).Value = 0;
        }

        Row(17, 1, "XD0001815", "Flocked Rib Glitter Bauble 10cm Asst", 24, 0.052, "9333527865090", 1.1);
        Row(18, 2, "XD0001817", "Lantern Flocked Bauble 12cm Asst", 24, 0.045, "9333527865151", 1.53);
        Row(19, 3, "XA3694", "Shopper Bag Non-Woven Character Print Asst", 36, 0.029, "9333527125170", 0.68);
        sheet.Cell(21, 12).Value = "Total:";
        sheet.Cell(21, 14).Value = 0;
        sheet.Cell(22, 12).Value = "GST";
        sheet.Cell(23, 12).Value = "Grand Total:";
        return Save(workbook);
    }

    [Fact]
    public void KFC原表_两行表头拼接_公式取图片_合计行跳过_表头信息读出()
    {
        using var stream = BuildKfcWorkbook();
        var result = SupplierPreorderSheetParser.ParseWorkbook(stream, "QT0013074 _MasterQuote_UBA_Aus - Christmas 2026 Order Form.xlsx");

        Assert.Equal("PriceList", result.SheetName);
        Assert.Equal(15, result.HeaderRowNo);
        var m = result.SuggestedMapping;
        Assert.Equal(2, m.ItemCode);   // C Stock Code
        Assert.Equal(3, m.Name);       // D Description
        Assert.Equal(5, m.CartonQty);  // F Ctn Qty
        Assert.Equal(6, m.CartonCbm);  // G Carton Cub. Mtr.
        Assert.Equal(7, m.Barcode);    // H Product Barcode
        Assert.Equal(8, m.Price);      // I Price
        Assert.Equal(1, m.Image);      // B Image Link
        Assert.Equal(10, m.OrderQty);  // K Cartons Ordered
        Assert.Equal("Cartons", result.SuggestedExportQtyUnit);

        // 表头下的 0 行保留给预览（无货号无名称会被规范化静默跳过）；3 行合计被跳过
        Assert.Equal(3, result.SkippedRowCount);
        var first = result.Rows.Single(r => r.RowNo == 17);
        Assert.Equal("https://unclebills.hosted-by-files.com/Christmas/2026/XD0001815.jpg", first.Cells[1]);
        Assert.Equal("9333527865090", first.Cells[7]);
        Assert.Equal("1.1", first.Cells[8]);

        Assert.Equal("QT0013074", result.HeaderInfo.QuoteNo);
        Assert.Equal(new DateTime(2026, 2, 5), result.HeaderInfo.OfferDate);
        Assert.Equal(new DateTime(2026, 3, 6), result.HeaderInfo.DeadlineDate);
        Assert.Equal("40GP", result.HeaderInfo.ContainerType);
        Assert.Equal("$AU Prices FIW", result.HeaderInfo.PriceTerms);
        Assert.Equal("Christmas", result.HeaderInfo.HolidayType);
        Assert.Equal(2026, result.HeaderInfo.SeasonYear);

        var items = SupplierPreorderItemRules.Normalize(result.Rows, m);
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.False(i.Invalid));
        Assert.Equal(24, items[0].CartonQty);
        Assert.Equal(0.052m, items[0].CartonCbm);
    }

    [Fact]
    public void Dats价格表_进价取PNP_箱规取Inner_带年份货号区分本年与去年_无下单列时建议按件填回()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("XMAS");
        sheet.Cell(1, 1).Value = "DATS 2026 Xmas Price List";
        string[] headers = { "Shelf Reference", "Category", " Image", "Description", "Inner", "Outer", "Barcode", "2025 Item Code", "2026 Item Code", "  PNP PRICE", " BULK PRICE", "Orion", "Kawana" };
        for (var c = 0; c < headers.Length; c++) sheet.Cell(2, c + 1).Value = headers[c];
        object[] row = { "X001", "Tinsel", 75186, "Tinsel Xmas Garland 1.8M 3 Asst", 36, 36, 9311192751863d, "NEW", 75186, 2.35, 2.1, 72, 0 };
        for (var c = 0; c < row.Length; c++) sheet.Cell(3, c + 1).Value = XLCellValue.FromObject(row[c]);
        // 灯饰行：PNP 与 Inner 留空，只有 BULK PRICE 与 Outer
        object[] lights = { "L001", "Lights", "", "Halloween Witch H60cm", "", 8, 9311192743189d, "", 74318, "", 32.94, "", "" };
        for (var c = 0; c < lights.Length; c++) sheet.Cell(4, c + 1).Value = XLCellValue.FromObject(lights[c]);
        using var stream = Save(workbook);

        var result = SupplierPreorderSheetParser.ParseWorkbook(stream, "QLD-2026 XMAS PRICE LIST_WH20.xlsx");
        var m = result.SuggestedMapping;
        Assert.Equal(2, result.HeaderRowNo);
        Assert.Equal(8, m.ItemCode);
        Assert.Equal(7, m.PrevYearItemCode);
        Assert.Equal(9, m.Price);       // PNP PRICE，不是 BULK PRICE
        Assert.Equal(4, m.CartonQty);   // Inner，不是 Outer
        Assert.Equal(10, m.PriceFallback);     // PNP 为空的行退回 BULK PRICE
        Assert.Equal(5, m.CartonQtyFallback);  // Inner 为空的行退回 Outer
        Assert.Equal(1, m.Category);
        Assert.Null(m.OrderQty);
        Assert.Equal("Pieces", result.SuggestedExportQtyUnit);
        Assert.Equal("DATS 2026 Xmas Price List", result.HeaderInfo.Title);

        var items = SupplierPreorderItemRules.Normalize(result.Rows, m);
        Assert.Equal(2, items.Count);
        Assert.False(items[1].Invalid);
        Assert.Equal(32.94m, items[1].Price);
        Assert.Equal(8, items[1].CartonQty);
        Assert.Null(items[0].PrevYearItemCode); // NEW 不是货号
        Assert.Null(items[0].ImageSourceUrl);   // Dats 的 Image 列是编号，不是链接
        Assert.Equal(2.35m, items[0].Price);
    }

    [Fact]
    public void Brazco订单表_下单数量列按箱()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Sheet1");
        sheet.Cell(1, 1).Value = "ABN 37 091 903 013";
        string[] headers = { "SEQ", "ITEM IMAGE", "ITEM #", "BARCODE", "ITEM DESCRIPTION", "CTN QTY", "CTN CBM", "PRICE", "ORDER # CARTONS HERE", "PCS ORDERED", "ORDER VALUE", "ORDER CBM" };
        for (var c = 0; c < headers.Length; c++) sheet.Cell(7, c + 1).Value = headers[c];
        sheet.Cell(8, 1).Value = 1;
        sheet.Cell(8, 3).Value = "XM178369";
        sheet.Cell(8, 4).Value = 9328644178369d;
        sheet.Cell(8, 5).Value = "Tea Towel Coastal Print";
        sheet.Cell(8, 6).Value = 48;
        sheet.Cell(8, 7).Value = 0.026;
        sheet.Cell(8, 8).Value = 1.1;
        using var stream = Save(workbook);

        var result = SupplierPreorderSheetParser.ParseWorkbook(stream, "Brazco International Christmas Range 2026 Order Form.xlsm");
        var m = result.SuggestedMapping;
        Assert.Equal(2, m.ItemCode);
        Assert.Equal(4, m.Name);
        Assert.Equal(5, m.CartonQty);
        Assert.Equal(6, m.CartonCbm);
        Assert.Equal(7, m.Price);
        Assert.Equal(8, m.OrderQty);
        Assert.Equal("Cartons", result.SuggestedExportQtyUnit);
        Assert.Equal(0, m.Seq);
    }

    [Fact]
    public void 粘贴内容_认出表头_没有表头时报错()
    {
        var rows = new List<List<string?>>
        {
            new() { "Seq", "Stock Code", "Description", "Unit", "Ctn Qty", "Barcode", "Price" },
            new() { "1", "XD0001815", "Flocked Rib Glitter Bauble 10cm Asst", "PC", "24", "9333527865090", "1.10" },
            new() { "", "", "", "", "", "", "" },
            new() { "2", "XD0001817", "Lantern Flocked Bauble 12cm Asst", "PC", "24", "9333527865151", "$1.53" },
        };
        var result = SupplierPreorderSheetParser.ParsePasted(rows);
        Assert.Equal(1, result.HeaderRowNo);
        Assert.Equal(2, result.Rows.Count);
        var items = SupplierPreorderItemRules.Normalize(result.Rows, result.SuggestedMapping);
        Assert.Equal(1.53m, items[1].Price);

        var noHeader = new List<List<string?>> { new() { "XD0001815", "Bauble", "24", "1.10" } };
        var ex = Assert.Throws<SupplierPreorderSheetParser.ParseException>(() => SupplierPreorderSheetParser.ParsePasted(noHeader));
        Assert.Equal("HEADER_NOT_FOUND", ex.ErrorCode);
    }

    [Fact]
    public void Dats日记本_NEW货号是本年_LY货号是去年()
    {
        var headers = new[] { "shelf reference", "description", "barcode", "inner", "outer", "ly item code", "new item code", "pnp price", "bulk price", "order qty" };
        var m = SupplierPreorderSheetParser.SuggestMapping(headers, out _);
        Assert.Equal(6, m.ItemCode);
        Assert.Equal(5, m.PrevYearItemCode);
        Assert.Equal(7, m.Price);
        Assert.Equal(8, m.PriceFallback);
        Assert.Equal(3, m.CartonQty);
        Assert.Equal(4, m.CartonQtyFallback);
    }

    [Theory]
    [InlineData("=HYPERLINK((CONCATENATE(\"https://a.example/x/\",C17,\".jpg\")),\"Click Image\")", "https://a.example/x/XD1.jpg")]
    [InlineData("HYPERLINK(\"https://a.example/x/\"&$C$17&\".jpg\",\"img\")", "https://a.example/x/XD1.jpg")]
    [InlineData("HYPERLINK(\"https://a.example/fixed.jpg\")", "https://a.example/fixed.jpg")]
    [InlineData("HYPERLINK(VLOOKUP(C17,Sheet2!A:B,2,0),\"img\")", null)]
    [InlineData("HYPERLINK(\"#Sheet2!A1\",\"go\")", null)]
    public void HYPERLINK公式求值_只认得出的拼接方式(string formula, string? expected)
    {
        var url = SupplierPreorderSheetParser.TryEvaluateHyperlinkFormula(formula, reference => reference == "C17" ? "XD1" : null);
        Assert.Equal(expected, url);
    }

    [Fact]
    public void 图片名超过31个字符的工作簿_截短图片名后照常打开()
    {
        // 1x1 PNG
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        byte[] bytes;
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.Worksheets.Add("Diaries");
            string[] headers = { "Category", "Image", "Description", "Inner", "Barcode", "2026 Item Code", "PNP PRICE" };
            for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];
            sheet.Cell(2, 3).Value = "2027 Diary A5";
            sheet.Cell(2, 4).Value = 12;
            sheet.Cell(2, 6).Value = "D2701";
            sheet.Cell(2, 7).Value = 3.5;
            sheet.AddPicture(new MemoryStream(png), "Pic1").MoveTo(sheet.Cell(2, 2));
            using var saved = new MemoryStream();
            workbook.SaveAs(saved);
            bytes = saved.ToArray();
        }

        // 模拟供应商原表：把图片名改成超过 31 个字符
        using (var zipStream = new MemoryStream())
        {
            zipStream.Write(bytes);
            using (var zip = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
            {
                var entry = zip.Entries.First(e => e.FullName.StartsWith("xl/drawings/") && e.FullName.EndsWith(".xml"));
                string xml;
                using (var reader = new StreamReader(entry.Open())) xml = reader.ReadToEnd();
                entry.Delete();
                using var writer = new StreamWriter(zip.CreateEntry(entry.FullName).Open());
                writer.Write(xml.Replace("name=\"Pic1\"", "name=\"A very long picture name exported by the supplier tool 0001\""));
            }

            bytes = zipStream.ToArray();
        }

        Assert.ThrowsAny<Exception>(() => new XLWorkbook(new MemoryStream(bytes)));
        using var stream = new MemoryStream(bytes);
        var result = SupplierPreorderSheetParser.ParseWorkbook(stream, "2027 DIARIES PRICE LIST.xlsx");
        var items = SupplierPreorderItemRules.Normalize(result.Rows, result.SuggestedMapping);
        Assert.Equal("D2701", Assert.Single(items).SupplierItemCode);
        using var reopened = SupplierPreorderSheetParser.OpenWorkbook(bytes);
        Assert.Equal("Diaries", reopened.Worksheets.First().Name);
    }

    [Fact]
    public void xls文件之外的坏文件给出可读错误()
    {
        using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        var ex = Assert.Throws<SupplierPreorderSheetParser.ParseException>(() => SupplierPreorderSheetParser.ParseWorkbook(stream, "bad.xlsx"));
        Assert.Equal("INVALID_EXCEL", ex.ErrorCode);
    }
}

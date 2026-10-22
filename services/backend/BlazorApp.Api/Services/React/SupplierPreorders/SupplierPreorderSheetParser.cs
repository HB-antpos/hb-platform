using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BlazorApp.Shared.DTOs;
using ClosedXML.Excel;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>
/// 供应商订单表解析（纯函数，不碰数据库）：把上传的 Excel 或粘贴内容变成「表头 + 数据行」，
/// 自动找表头（支持 KFC 那种两行拼成一个表头）、按同义词猜列映射、从 HYPERLINK 公式取图片地址、跳过合计行、
/// 从表头上方读出报价单号/报价日期/截止日期/柜型等信息。
/// 判断某行是不是商品行要等列映射确定后再做（KFC 有 32 行货号不是 X 开头，不能按前缀判断）。
/// </summary>
public static class SupplierPreorderSheetParser
{
    public const int MaxDataRows = 6000;
    public const int MaxColumns = 80;
    private const int HeaderSearchRows = 40;

    private static readonly string[] TotalRowLabels =
    {
        "total", "total:", "subtotal", "subtotal:", "sub total", "gst", "gst:", "grand total", "grand total:", "totals", "totals -",
    };

    public sealed class ParseException : Exception
    {
        public ParseException(string message, string errorCode) : base(message) => ErrorCode = errorCode;
        public string ErrorCode { get; }
    }

    // ---------------- 入口 ----------------

    /// <summary>解析 .xlsx / .xlsm：在所有工作表里挑表头得分最高的一张。</summary>
    public static SupplierPreorderParseResultDto ParseWorkbook(Stream stream, string fileName)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        XLWorkbook workbook;
        try
        {
            workbook = OpenWorkbook(buffer.ToArray());
        }
        catch (Exception ex)
        {
            throw new ParseException($"无法读取 Excel 文件：{ex.Message}。.xls 请先另存为 .xlsx。", "INVALID_EXCEL");
        }

        using (workbook)
        {
            SupplierPreorderParseResultDto? best = null;
            var bestScore = -1;
            foreach (var worksheet in workbook.Worksheets)
            {
                var grid = ReadWorksheetGrid(worksheet, out var letters);
                if (grid.Count == 0)
                {
                    continue;
                }

                var header = DetectHeader(grid);
                if (header.Score > bestScore)
                {
                    bestScore = header.Score;
                    best = BuildResult(grid, header, letters, worksheet.Name, fileName);
                }
            }

            if (best is null || bestScore < 3)
            {
                throw new ParseException("没有找到订单表的表头（需要至少能认出货号、名称、价格三列）。", "HEADER_NOT_FOUND");
            }

            best.FileName = fileName;
            return best;
        }
    }

    /// <summary>解析粘贴内容（前端已按行、按制表符拆好）。</summary>
    public static SupplierPreorderParseResultDto ParsePasted(List<List<string?>> rows)
    {
        var grid = rows
            .Take(MaxDataRows + HeaderSearchRows)
            .Select(row => row.Take(MaxColumns).Select(cell => NormalizeCellText(cell)).ToList())
            .ToList();
        if (grid.All(row => row.All(string.IsNullOrWhiteSpace)))
        {
            throw new ParseException("粘贴内容为空。", "EMPTY_PASTE");
        }

        var header = DetectHeader(grid);
        if (header.Score < 3)
        {
            throw new ParseException("粘贴内容里没有找到表头，请连同表头行一起复制（需要能认出货号、名称、价格三列）。", "HEADER_NOT_FOUND");
        }

        return BuildResult(grid, header, letters: null, sheetName: "粘贴", fileName: null);
    }

    // ---------------- 读表 ----------------

    /// <summary>
    /// 读成字符串网格：数字用不变区域格式，日期转 yyyy-MM-dd，公式取缓存值；
    /// 带超链接（HYPERLINK 公式或单元格链接）的单元格直接用链接地址，「Click Image」这类显示文字没有用。
    /// 合并单元格把左上角的值铺满整个区域，这样两行表头里被合并的「Product | Price」能拼成「Product Price」。
    /// </summary>
    private static List<List<string?>> ReadWorksheetGrid(IXLWorksheet worksheet, out List<string?> letters)
    {
        letters = new List<string?>();
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 0;
        var lastColumn = Math.Min(worksheet.LastColumnUsed()?.ColumnNumber() ?? 0, MaxColumns);
        if (lastRow == 0 || lastColumn == 0)
        {
            return new List<List<string?>>();
        }

        lastRow = Math.Min(lastRow, MaxDataRows + HeaderSearchRows);
        for (var column = 1; column <= lastColumn; column++)
        {
            letters.Add(XLHelper.GetColumnLetterFromNumber(column));
        }

        var grid = new List<List<string?>>(lastRow);
        for (var row = 1; row <= lastRow; row++)
        {
            var cells = new List<string?>(lastColumn);
            for (var column = 1; column <= lastColumn; column++)
            {
                cells.Add(ReadCellText(worksheet, worksheet.Cell(row, column)));
            }

            grid.Add(cells);
        }

        foreach (var range in worksheet.MergedRanges)
        {
            var first = range.FirstCell();
            var value = ReadCellText(worksheet, first);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var top = range.RangeAddress.FirstAddress.RowNumber;
            var bottom = Math.Min(range.RangeAddress.LastAddress.RowNumber, lastRow);
            var left = range.RangeAddress.FirstAddress.ColumnNumber;
            var right = Math.Min(range.RangeAddress.LastAddress.ColumnNumber, lastColumn);
            // 只铺表头区域附近的小合并块，避免大标题横幅把整行都铺成同一个字
            if (bottom - top > 3 || right - left > 6)
            {
                continue;
            }

            for (var r = top; r <= bottom; r++)
            {
                for (var c = left; c <= right; c++)
                {
                    if (string.IsNullOrWhiteSpace(grid[r - 1][c - 1]))
                    {
                        grid[r - 1][c - 1] = value;
                    }
                }
            }
        }

        return grid;
    }

    /// <summary>
    /// 打开工作簿。ClosedXML 不接受超过 31 个字符的图片名（Dats 2027 日记本价格表就是这样），
    /// 遇到时把 xl/drawings/*.xml 里的图片名改成短名后重试；只改图片显示名，不动单元格数据。
    /// 「填回原表」导出也走这里。
    /// </summary>
    public static XLWorkbook OpenWorkbook(byte[] bytes)
    {
        try
        {
            return new XLWorkbook(new MemoryStream(bytes, writable: false));
        }
        catch (Exception ex) when (ex.Message.Contains("Picture names", StringComparison.OrdinalIgnoreCase))
        {
            return new XLWorkbook(new MemoryStream(ShortenDrawingNames(bytes), writable: false));
        }
    }

    private static byte[] ShortenDrawingNames(byte[] bytes)
    {
        using var output = new MemoryStream();
        output.Write(bytes);
        using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            var counter = 0;
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/drawings/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                string xml;
                using (var reader = new StreamReader(entry.Open()))
                {
                    xml = reader.ReadToEnd();
                }

                var fixedXml = Regex.Replace(xml, @"(<(?:xdr:)?cNvPr\b[^>]*?\bname="")([^""]{32,})("")", m => m.Groups[1].Value + "Picture " + (++counter) + m.Groups[3].Value);
                if (fixedXml == xml)
                {
                    continue;
                }

                var name = entry.FullName;
                entry.Delete();
                var replacement = zip.CreateEntry(name);
                using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false));
                writer.Write(fixedXml);
            }
        }

        return output.ToArray();
    }

    private static string? ReadCellText(IXLWorksheet worksheet, IXLCell cell)
    {
        try
        {
            if (cell.HasFormula)
            {
                var formula = cell.FormulaA1 ?? string.Empty;
                if (formula.Contains("HYPERLINK", StringComparison.OrdinalIgnoreCase))
                {
                    var url = TryEvaluateHyperlinkFormula(formula, reference => ReadPlainCellText(worksheet.Cell(reference)));
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }

                return FormatCellValue(cell.CachedValue);
            }

            var text = FormatCellValue(cell.Value);
            // 单元格自带链接：只有显示文字为空或是「Click Image / View / Photo」这类链接字样时才取地址，
            // 货号、名称这种本身有意义的文字即使挂了链接也保留原文
            if (cell.HasHyperlink && (text is null || LooksLikeLinkCaption(text)))
            {
                var link = cell.GetHyperlink();
                var address = link.IsExternal ? link.ExternalAddress?.ToString() : null;
                if (!string.IsNullOrWhiteSpace(address))
                {
                    return address;
                }
            }

            return text;
        }
        catch
        {
            // 个别单元格读失败（坏公式、不支持的类型）不影响整张表
            return null;
        }
    }

    private static bool LooksLikeLinkCaption(string text)
    {
        var lower = text.Trim().ToLowerInvariant();
        return lower.Length <= 20 && (lower.Contains("image") || lower.Contains("click") || lower.Contains("photo") || lower.Contains("picture") || lower.Contains("view") || lower.Contains("link") || lower == "img" || lower.Contains("图"));
    }

    private static string? ReadPlainCellText(IXLCell cell)
    {
        try
        {
            return FormatCellValue(cell.HasFormula ? cell.CachedValue : cell.Value);
        }
        catch
        {
            return null;
        }
    }

    private static string? FormatCellValue(XLCellValue value)
    {
        if (value.IsBlank || value.IsError)
        {
            return null;
        }

        if (value.IsNumber)
        {
            return value.GetNumber().ToString("0.############", CultureInfo.InvariantCulture);
        }

        if (value.IsDateTime)
        {
            return value.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (value.IsBoolean)
        {
            return value.GetBoolean() ? "TRUE" : "FALSE";
        }

        return NormalizeCellText(value.IsText ? value.GetText() : value.ToString());
    }

    private static string? NormalizeCellText(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var trimmed = text.Replace(' ', ' ').Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// 从 HYPERLINK 公式取出链接：支持第一参数是字符串、CONCATENATE(...) 或用 &amp; 拼接（含同表单元格引用）。
    /// KFC：HYPERLINK((CONCATENATE("https://unclebills.hosted-by-files.com/Christmas/2026/",C17,".jpg")),"Click Image")。
    /// 解析不了就返回 null，不猜。
    /// </summary>
    public static string? TryEvaluateHyperlinkFormula(string formula, Func<string, string?> readCell)
    {
        var text = formula.TrimStart('=').Trim();
        var start = text.IndexOf("HYPERLINK(", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var args = SplitTopLevelArguments(text, start + "HYPERLINK(".Length);
        if (args is null || args.Count == 0)
        {
            return null;
        }

        var url = EvaluateStringExpression(args[0], readCell);
        return url is not null && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ? url
            : null;
    }

    private static string? EvaluateStringExpression(string expression, Func<string, string?> readCell)
    {
        var expr = expression.Trim();
        while (expr.StartsWith('(') && expr.EndsWith(')') && MatchingParenIndex(expr, 0) == expr.Length - 1)
        {
            expr = expr[1..^1].Trim();
        }

        if (expr.StartsWith("CONCATENATE(", StringComparison.OrdinalIgnoreCase) || expr.StartsWith("CONCAT(", StringComparison.OrdinalIgnoreCase))
        {
            var open = expr.IndexOf('(');
            var parts = SplitTopLevelArguments(expr, open + 1);
            if (parts is null)
            {
                return null;
            }

            var builder = new StringBuilder();
            foreach (var part in parts)
            {
                var piece = EvaluateStringExpression(part, readCell);
                if (piece is null)
                {
                    return null;
                }

                builder.Append(piece);
            }

            return builder.ToString();
        }

        var amp = SplitTopLevel(expr, '&');
        if (amp.Count > 1)
        {
            var builder = new StringBuilder();
            foreach (var part in amp)
            {
                var piece = EvaluateStringExpression(part, readCell);
                if (piece is null)
                {
                    return null;
                }

                builder.Append(piece);
            }

            return builder.ToString();
        }

        if (expr.Length >= 2 && expr[0] == '"' && expr[^1] == '"')
        {
            return expr[1..^1].Replace("\"\"", "\"");
        }

        var reference = expr.Replace("$", string.Empty);
        if (Regex.IsMatch(reference, "^[A-Za-z]{1,3}[0-9]{1,7}$"))
        {
            return readCell(reference) ?? string.Empty;
        }

        return null;
    }

    /// <summary>从 openIndex（左括号之后）开始，按顶层逗号切参数，直到匹配的右括号。</summary>
    private static List<string>? SplitTopLevelArguments(string text, int openIndex)
    {
        var args = new List<string>();
        var depth = 0;
        var inQuote = false;
        var current = new StringBuilder();
        for (var i = openIndex; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"')
            {
                inQuote = !inQuote;
                current.Append(ch);
                continue;
            }

            if (!inQuote)
            {
                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    if (depth == 0)
                    {
                        args.Add(current.ToString());
                        return args;
                    }

                    depth--;
                }
                else if (ch == ',' && depth == 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    continue;
                }
            }

            current.Append(ch);
        }

        return null;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var inQuote = false;
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote && ch == '(')
            {
                depth++;
            }
            else if (!inQuote && ch == ')')
            {
                depth--;
            }
            else if (!inQuote && depth == 0 && ch == separator)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        parts.Add(current.ToString());
        return parts;
    }

    private static int MatchingParenIndex(string text, int openIndex)
    {
        var depth = 0;
        var inQuote = false;
        for (var i = openIndex; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote && ch == '(')
            {
                depth++;
            }
            else if (!inQuote && ch == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    // ---------------- 表头识别与列映射 ----------------

    public sealed record HeaderDetection(int StartIndex, int RowCount, List<string> Headers, int Score);

    /// <summary>
    /// 在前 40 行里找表头：每行单独打分，也和下一行拼起来打分（KFC 第 14、15 行拼起来才是完整表头），取得分最高的。
    /// 得分 = 能认出的字段数，货号、名称、价格各额外加 1 分。
    /// </summary>
    public static HeaderDetection DetectHeader(List<List<string?>> grid)
    {
        var best = new HeaderDetection(0, 1, new List<string>(), -1);
        var limit = Math.Min(grid.Count, HeaderSearchRows);
        for (var i = 0; i < limit; i++)
        {
            var single = grid[i].Select(cell => NormalizeHeader(cell)).ToList();
            var singleScore = ScoreHeaders(single);
            if (singleScore > best.Score)
            {
                best = new HeaderDetection(i, 1, single, singleScore);
            }

            if (i + 1 < grid.Count)
            {
                var width = Math.Max(grid[i].Count, grid[i + 1].Count);
                var combined = new List<string>(width);
                for (var c = 0; c < width; c++)
                {
                    var top = c < grid[i].Count ? grid[i][c] : null;
                    var bottom = c < grid[i + 1].Count ? grid[i + 1][c] : null;
                    combined.Add(NormalizeHeader(string.Join(' ', new[] { top, bottom }.Where(part => !string.IsNullOrWhiteSpace(part)))));
                }

                var combinedScore = ScoreHeaders(combined);
                var nextScore = ScoreHeaders(grid[i + 1].Select(cell => NormalizeHeader(cell)).ToList());
                // 两行拼接必须比这两行各自单独都好才采用：避免把标题行（Dats 第 1 行）或第一行数据拼进表头
                if (combinedScore > best.Score && combinedScore > singleScore && combinedScore > nextScore)
                {
                    best = new HeaderDetection(i, 2, combined, combinedScore);
                }
            }
        }

        return best;
    }

    private static int ScoreHeaders(List<string> headers)
    {
        var mapping = SuggestMapping(headers, out _);
        var score = 0;
        foreach (var value in new[]
                 {
                     mapping.ItemCode, mapping.Name, mapping.Price, mapping.CartonQty, mapping.Barcode, mapping.Unit,
                     mapping.CartonCbm, mapping.Image, mapping.Category, mapping.PrevYearItemCode, mapping.Seq, mapping.OrderQty,
                 })
        {
            if (value.HasValue)
            {
                score++;
            }
        }

        if (mapping.ItemCode.HasValue) score++;
        if (mapping.Name.HasValue) score++;
        if (mapping.Price.HasValue) score++;
        return score;
    }

    public static string NormalizeHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return string.Empty;
        }

        var text = Regex.Replace(header.Replace('\n', ' ').Replace('\r', ' ').ToLowerInvariant(), @"\s+", " ").Trim();
        return text.TrimEnd(':', '.', ' ');
    }

    /// <summary>
    /// 按同义词猜列映射。每列只分给一个字段；先认体积、下单数量、条码等「排他」列，再认货号/名称/价格/箱规。
    /// 已定口径：Dats 的进价取 PNP PRICE（优先于 BULK PRICE），箱规取 Inner（优先于 Outer）。
    /// orderQtyUnit 输出原表下单数量列的单位：列名含 carton/ctn 为 Cartons，否则 Pieces。
    /// </summary>
    public static SupplierPreorderColumnMappingDto SuggestMapping(IReadOnlyList<string> headers, out string orderQtyUnit)
    {
        var mapping = new SupplierPreorderColumnMappingDto();
        var used = new HashSet<int>();
        orderQtyUnit = "Pieces";

        int? Pick(Func<string, int?> rank)
        {
            int? bestIndex = null;
            var bestRank = int.MaxValue;
            for (var i = 0; i < headers.Count; i++)
            {
                if (used.Contains(i) || string.IsNullOrEmpty(headers[i]))
                {
                    continue;
                }

                var r = rank(headers[i]);
                if (r.HasValue && r.Value < bestRank)
                {
                    bestRank = r.Value;
                    bestIndex = i;
                }
            }

            if (bestIndex.HasValue)
            {
                used.Add(bestIndex.Value);
            }

            return bestIndex;
        }

        static bool Has(string h, string word) => h.Contains(word, StringComparison.Ordinal);

        // 带年份的货号列（Dats：2025 Item Code / 2026 Item Code）：年份大的是本年货号，小的是去年货号
        var yearCodeColumns = headers
            .Select((h, i) => (h, i, m: Regex.Match(h, @"^(20\d{2}) ?item ?(code|#|no)")))
            .Where(x => x.m.Success)
            .Select(x => (x.i, year: int.Parse(x.m.Groups[1].Value, CultureInfo.InvariantCulture)))
            .OrderByDescending(x => x.year)
            .ToList();

        mapping.CartonCbm = Pick(h => Has(h, "cbm") || Has(h, "cub") || Has(h, "m3") || Has(h, "volume") ? 1 : null);
        mapping.OrderQty = Pick(h =>
        {
            if ((Has(h, "ordered") || Has(h, "order #") || Has(h, "order qty") || Has(h, "order quantity")) && !Has(h, "total"))
            {
                return Has(h, "carton") || Has(h, "ctn") ? 1 : 2;
            }

            return null;
        });
        if (mapping.OrderQty.HasValue)
        {
            var h = headers[mapping.OrderQty.Value];
            orderQtyUnit = Has(h, "carton") || Has(h, "ctn") ? "Cartons" : "Pieces";
            // 同时存在「Pieces Ordered」之类的列也占掉，免得被认成别的字段
            Pick(x => Has(x, "ordered") && !Has(x, "total") ? 1 : null);
        }

        mapping.Barcode = Pick(h => Has(h, "barcode") || h is "apn" or "ean" or "upc" || Has(h, "条码") ? 1 : null);
        mapping.Image = Pick(h => Has(h, "image") || Has(h, "photo") || Has(h, "picture") || Has(h, "图片") ? 1 : null);
        mapping.Category = Pick(h => Has(h, "category") || Has(h, "分类") ? 1 : null);
        mapping.Notes = Pick(h => h is "notes" or "note" or "remarks" or "remark" or "comments" or "comment" or "备注" ? 1 : null);
        mapping.Unit = Pick(h => h is "unit" or "uom" or "单位" ? 1 : null);

        // Dats 日记本：「NEW Item Code」是本年货号、「LY Item Code」（Last Year）是去年货号
        var newCodeIndex = headers.Select((h, i) => (h, i)).Where(x => Regex.IsMatch(x.h, @"^(new|ty|this year) ?item ?(code|#|no)")).Select(x => (int?)x.i).FirstOrDefault();
        var lastYearCodeIndex = headers.Select((h, i) => (h, i)).Where(x => Regex.IsMatch(x.h, @"^(ly|last year|old) ?item ?(code|#|no)")).Select(x => (int?)x.i).FirstOrDefault();
        if (newCodeIndex.HasValue && yearCodeColumns.Count == 0)
        {
            mapping.ItemCode = newCodeIndex;
            used.Add(newCodeIndex.Value);
            if (lastYearCodeIndex.HasValue)
            {
                mapping.PrevYearItemCode = lastYearCodeIndex;
                used.Add(lastYearCodeIndex.Value);
            }
        }
        else if (yearCodeColumns.Count > 0)
        {
            mapping.ItemCode = yearCodeColumns[0].i;
            used.Add(yearCodeColumns[0].i);
            if (yearCodeColumns.Count > 1)
            {
                mapping.PrevYearItemCode = yearCodeColumns[1].i;
                used.Add(yearCodeColumns[1].i);
            }
        }
        else
        {
            mapping.ItemCode = Pick(h => h switch
            {
                "stock code" or "item #" or "item#" or "item code" or "item no" or "item number" or "货号" => 1,
                "sku" or "product code" or "code" or "stock no" or "stock #" => 2,
                _ when Has(h, "item code") || Has(h, "stock code") => 3,
                _ => null,
            });
        }

        mapping.Name = Pick(h => h switch
        {
            "description" or "item description" or "product description" or "名称" or "商品名称" => 1,
            "product name" or "name" or "item name" => 2,
            _ when Has(h, "description") => 3,
            _ => null,
        });

        mapping.Price = Pick(h =>
        {
            if (Has(h, "total") || Has(h, "rrp") || Has(h, "retail") || Has(h, "value"))
            {
                return null;
            }

            return h switch
            {
                "product price" or "pnp price" or "pnp" => 1,
                "unit price" or "price" or "cost" or "unit cost" or "进价" => 2,
                _ when Has(h, "pnp") => 1,
                _ when Has(h, "bulk") && Has(h, "price") => 4,
                _ when Has(h, "price") => 3,
                _ => null,
            };
        });

        mapping.CartonQty = Pick(h =>
        {
            if (Has(h, "ordered") || Has(h, "cbm") || Has(h, "cub"))
            {
                return null;
            }

            return h switch
            {
                "ctn qty" or "carton qty" or "qty per carton" or "qty per ctn" or "moq" or "箱规" => 1,
                "inner" or "inner qty" => 1,
                "ctn" or "carton" or "pack qty" or "case qty" or "pack" => 2,
                "outer" or "outer qty" => 3,
                _ when Has(h, "inner") => 1,
                _ when Has(h, "outer") => 3,
                _ when Has(h, "ctn qty") || Has(h, "carton qty") => 1,
                _ => null,
            };
        });

        // 已定口径：Dats 进价取 PNP、箱规取 Inner；同表还有 BULK PRICE / Outer 时作为按行退回的备用列
        if (mapping.Price is int priceIndex && Has(headers[priceIndex], "pnp"))
        {
            mapping.PriceFallback = Pick(h => Has(h, "bulk") && Has(h, "price") ? 1 : null);
        }

        if (mapping.CartonQty is int cartonIndex && Has(headers[cartonIndex], "inner"))
        {
            mapping.CartonQtyFallback = Pick(h => Has(h, "outer") && !Has(h, "ordered") ? 1 : null);
        }

        mapping.Seq = Pick(h => h is "seq" or "seq no" or "seq #" or "序号" ? 1 : null);
        return mapping;
    }

    // ---------------- 结果组装 ----------------

    private static SupplierPreorderParseResultDto BuildResult(
        List<List<string?>> grid,
        HeaderDetection header,
        List<string?>? letters,
        string sheetName,
        string? fileName
    )
    {
        var headerLastIndex = header.StartIndex + header.RowCount - 1;
        var width = Math.Min(MaxColumns, Math.Max(header.Headers.Count, grid.Skip(headerLastIndex + 1).Select(r => r.Count).DefaultIfEmpty(0).Max()));
        var mapping = SuggestMapping(header.Headers, out var orderQtyUnit);

        var rows = new List<SupplierPreorderParsedRowDto>();
        var skipped = 0;
        var skippedSamples = new List<string>();
        for (var i = headerLastIndex + 1; i < grid.Count; i++)
        {
            var cells = grid[i];
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (IsTotalRow(cells))
            {
                skipped++;
                if (skippedSamples.Count < 5)
                {
                    skippedSamples.Add($"第 {i + 1} 行：{string.Join(" | ", cells.Where(c => !string.IsNullOrWhiteSpace(c)).Take(4))}");
                }

                continue;
            }

            if (rows.Count >= MaxDataRows)
            {
                throw new ParseException($"商品行超过 {MaxDataRows} 行，请拆分后再导入。", "TOO_MANY_ROWS");
            }

            var padded = new List<string?>(width);
            for (var c = 0; c < width; c++)
            {
                padded.Add(c < cells.Count ? cells[c] : null);
            }

            rows.Add(new SupplierPreorderParsedRowDto { RowNo = i + 1, Cells = padded });
        }

        var columns = new List<SupplierPreorderParsedColumnDto>(width);
        for (var c = 0; c < width; c++)
        {
            var rawHeader = c < header.Headers.Count ? header.Headers[c] : string.Empty;
            columns.Add(new SupplierPreorderParsedColumnDto
            {
                Index = c,
                Letter = letters is not null && c < letters.Count ? letters[c] : null,
                Header = string.IsNullOrEmpty(rawHeader) ? $"列{c + 1}" : OriginalHeaderText(grid, header, c),
                SampleValues = rows.Select(r => r.Cells[c]).Where(v => !string.IsNullOrWhiteSpace(v)).Take(3).Select(v => v!.Length > 60 ? v[..60] + "…" : v).ToList()!,
            });
        }

        var warnings = new List<string>();
        if (!mapping.CartonQty.HasValue)
        {
            warnings.Add("没有认出箱规（起订量）列，请在列映射里手动选择。");
        }

        if (!mapping.Price.HasValue)
        {
            warnings.Add("没有认出进价列，请在列映射里手动选择。");
        }

        return new SupplierPreorderParseResultDto
        {
            SheetName = sheetName,
            HeaderRowNo = headerLastIndex + 1,
            HeaderSignature = ComputeHeaderSignature(header.Headers),
            Columns = columns,
            SuggestedMapping = mapping,
            SuggestedExportQtyUnit = mapping.OrderQty.HasValue ? orderQtyUnit : "Pieces",
            Rows = rows,
            SkippedRowCount = skipped,
            SkippedSamples = skippedSamples,
            HeaderInfo = ExtractHeaderInfo(grid, header.StartIndex, sheetName, fileName),
            Warnings = warnings,
        };
    }

    private static string OriginalHeaderText(List<List<string?>> grid, HeaderDetection header, int column)
    {
        var parts = new List<string>();
        for (var r = header.StartIndex; r < header.StartIndex + header.RowCount; r++)
        {
            var text = column < grid[r].Count ? grid[r][column] : null;
            if (!string.IsNullOrWhiteSpace(text) && !parts.Contains(text))
            {
                parts.Add(text);
            }
        }

        return parts.Count == 0 ? $"列{column + 1}" : string.Join(' ', parts);
    }

    private static bool IsTotalRow(List<string?> cells)
    {
        // 合计行：前几个非空单元格里出现 Total / Subtotal / GST / Grand Total
        foreach (var cell in cells.Where(c => !string.IsNullOrWhiteSpace(c)).Take(3))
        {
            var text = NormalizeHeader(cell);
            if (TotalRowLabels.Contains(text) || TotalRowLabels.Contains(text + ":"))
            {
                return true;
            }
        }

        return false;
    }

    public static string ComputeHeaderSignature(IEnumerable<string> headers)
    {
        var joined = string.Join("|", headers.Select(NormalizeHeader));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    /// <summary>从表头上方的说明区读报价单号、报价日期、截止日期、价格条款、柜型，并从标题/工作表名/文件名猜节日和年份。</summary>
    public static SupplierPreorderHeaderInfoDto ExtractHeaderInfo(List<List<string?>> grid, int headerStartIndex, string? sheetName, string? fileName)
    {
        var info = new SupplierPreorderHeaderInfoDto();
        var texts = new List<string>();
        for (var r = 0; r < Math.Min(headerStartIndex, HeaderSearchRows); r++)
        {
            var row = grid[r];
            for (var c = 0; c < row.Count; c++)
            {
                var cell = row[c];
                if (string.IsNullOrWhiteSpace(cell))
                {
                    continue;
                }

                texts.Add(cell);
                var label = NormalizeHeader(cell);
                string? Next() => row.Skip(c + 1).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

                if (label.Contains("offer date", StringComparison.Ordinal) && info.OfferDate is null)
                {
                    info.OfferDate = ParseDate(Next());
                }
                else if ((label.Contains("completed by", StringComparison.Ordinal) || label.Contains("deadline", StringComparison.Ordinal) || label.Contains("order by", StringComparison.Ordinal) || label.Contains("closing date", StringComparison.Ordinal)) && info.DeadlineDate is null)
                {
                    info.DeadlineDate = ParseDate(Next());
                }
                else if (label is "terms" && info.PriceTerms is null)
                {
                    info.PriceTerms = Next();
                }

                var quote = Regex.Match(cell, @"\bQT\d{5,}\b", RegexOptions.IgnoreCase);
                if (quote.Success && info.QuoteNo is null)
                {
                    info.QuoteNo = quote.Value.ToUpperInvariant();
                }

                var container = Regex.Match(cell, @"\b(20|40)\s?(GP|HC|HQ)\b", RegexOptions.IgnoreCase);
                if (container.Success && info.ContainerType is null)
                {
                    info.ContainerType = (container.Groups[1].Value + container.Groups[2].Value).ToUpperInvariant();
                }
            }
        }

        var haystack = string.Join(" ", texts.Append(sheetName ?? string.Empty).Append(fileName ?? string.Empty));
        var lower = haystack.ToLowerInvariant();
        info.HolidayType = lower.Contains("christmas") || lower.Contains("xmas") ? "Christmas"
            : lower.Contains("halloween") ? "Halloween"
            : lower.Contains("easter") ? "Easter"
            : lower.Contains("diar") ? "Diaries"
            : null;

        var years = Regex.Matches(haystack, @"\b(20[2-4]\d)\b").Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        if (years.Count > 0)
        {
            // 取出现最多的年份，同次数取较大的（文件名里常带下载日期，标题里的才是节日年份）
            info.SeasonYear = years.GroupBy(y => y).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
        }

        info.Title = texts.FirstOrDefault(t =>
        {
            var l = t.ToLowerInvariant();
            return l.Length <= 120 && (l.Contains("christmas") || l.Contains("xmas") || l.Contains("halloween") || l.Contains("easter") || l.Contains("price list") || l.Contains("order form") || l.Contains("range"));
        });
        if (info.Title is null && !string.IsNullOrWhiteSpace(fileName))
        {
            info.Title = Path.GetFileNameWithoutExtension(fileName).Trim();
        }

        return info;
    }

    private static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var formats = new[] { "yyyy-MM-dd", "d/M/yyyy", "dd/MM/yyyy", "d/M/yy", "d MMM yyyy", "d MMMM yyyy", "MMM d, yyyy" };
        if (DateTime.TryParseExact(text.Trim(), formats, CultureInfo.GetCultureInfo("en-AU"), DateTimeStyles.None, out var exact))
        {
            return exact.Date;
        }

        return DateTime.TryParse(text, CultureInfo.GetCultureInfo("en-AU"), DateTimeStyles.None, out var parsed) ? parsed.Date : null;
    }
}

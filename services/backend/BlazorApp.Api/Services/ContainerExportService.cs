using ClosedXML.Excel;
using iTextSharp.text;
using iTextSharp.text.pdf;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Api.Services.Pdf;
using System.Net.Http;

namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 货柜明细导出服务
    /// </summary>
    public class ContainerExportService
    {
        private readonly ILogger<ContainerExportService> _logger;
        private readonly HttpClient _httpClient;
        private static readonly List<string> DefaultExportColumns = new()
        {
            "image",
            "itemNumber",
            "barcode",
            "englishName",
            "importPrice",
            "oemPrice",
            "loadingQuantity",
        };
        private static readonly Dictionary<string, string> ExportColumnAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["productImage"] = "image",
            ["barcodeImage"] = "barcode",
            ["productName"] = "chineseName",
            ["containerPieces"] = "loadingPieces",
            ["containerQuantity"] = "loadingQuantity",
            ["middlePackQuantity"] = "packingQuantity",
        };
        private static readonly Dictionary<string, string> ExportColumnHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["index"] = "序号",
            ["image"] = "商品图片",
            ["itemNumber"] = "货号",
            ["barcode"] = "条码",
            ["chineseName"] = "中文名称",
            ["englishName"] = "英文名称",
            ["domesticPrice"] = "国内价格",
            ["transportCost"] = "运输成本",
            ["adjustmentRate"] = "调整浮率",
            ["importPrice"] = "进口价格",
            ["oemPrice"] = "零售价",
            ["packingQuantity"] = "单件装箱数",
            ["loadingPieces"] = "件数",
            ["loadingQuantity"] = "总数量",
            ["unitVolume"] = "单件体积",
            ["totalVolume"] = "总体积",
            ["remarks"] = "备注",
        };

        public ContainerExportService(
            ILogger<ContainerExportService> logger,
            HttpClient httpClient)
        {
            _logger = logger;
            _httpClient = httpClient;
        }

        #region Excel导出

        /// <summary>
        /// 生成Excel文件
        /// </summary>
        public async Task<byte[]> GenerateExcelFileAsync(
            YiwuContainerDto container,
            List<YiwuContainerDetailDto> details,
            List<string>? exportColumns = null)
        {
            try
            {
                var selectedColumns = ResolveExportColumns(exportColumns);

                using var workbook = new XLWorkbook();
                var worksheet = workbook.Worksheets.Add($"货柜明细_{container.ContainerNumber}");

                // 设置货柜基本信息
                int row = 1;
                worksheet.Cell(row, 1).Value = "货柜编号";
                worksheet.Cell(row, 2).Value = container.ContainerNumber;
                worksheet.Cell(row, 3).Value = "装柜日期";
                worksheet.Cell(row, 4).Value = container.LoadingDate?.ToString("yyyy-MM-dd");

                row++;
                worksheet.Cell(row, 1).Value = "预计到岸";
                worksheet.Cell(row, 2).Value = container.EstimatedArrivalDate?.ToString("yyyy-MM-dd");
                worksheet.Cell(row, 3).Value = "实际到货";
                worksheet.Cell(row, 4).Value = container.ActualArrivalDate?.ToString("yyyy-MM-dd");

                row++;
                worksheet.Cell(row, 1).Value = "合计件数";
                worksheet.Cell(row, 2).Value = container.TotalPieces;
                worksheet.Cell(row, 3).Value = "合计数量";
                worksheet.Cell(row, 4).Value = container.TotalQuantity;

                row++;
                worksheet.Cell(row, 1).Value = "合计金额";
                worksheet.Cell(row, 2).Value = container.TotalAmount;
                worksheet.Cell(row, 3).Value = "总体积";
                worksheet.Cell(row, 4).Value = container.TotalVolume;

                // 空行
                row += 2;

                // 设置表头
                int headerRow = row;
                int col = 1;

                var columnMapping = selectedColumns.ToDictionary(
                    column => column,
                    column => (Header: ExportColumnHeaders[column], Index: col++),
                    StringComparer.OrdinalIgnoreCase
                );

                // 设置表头
                foreach (var column in columnMapping)
                {
                    worksheet.Cell(headerRow, column.Value.Index).Value = column.Value.Header;
                }

                // 设置表头样式
                var headerRange = worksheet.Range(headerRow, 1, headerRow, col - 1);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;
                headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                // 如果包含图片列，批量下载图片
                Dictionary<string, byte[]>? imageDict = null;
                if (selectedColumns.Contains("image", StringComparer.OrdinalIgnoreCase))
                {
                    var imageUrls = details
                        .Where(d => !string.IsNullOrEmpty(d.Product?.ImageUrl))
                        .Select(d => d.Product!.ImageUrl!)
                        .Distinct()
                        .ToList();

                    if (imageUrls.Any())
                    {
                        _logger.LogInformation("开始并行下载 {ImageCount} 张图片", imageUrls.Count);
                        imageDict = await DownloadImagesInParallelAsync(imageUrls);
                        _logger.LogInformation("图片下载完成，成功下载 {SuccessCount} 张", imageDict.Count);
                    }
                }

                // 按货号升序排列数据
                var sortedDetails = details.OrderBy(d => d.Product?.ItemNumber).ToList();

                // 填充数据
                row = headerRow + 1;
                var rowIndex = 1;
                foreach (var detail in sortedDetails)
                {
                    foreach (var column in selectedColumns)
                    {
                        var cell = worksheet.Cell(row, columnMapping[column].Index);
                        if (column.Equals("image", StringComparison.OrdinalIgnoreCase))
                        {
                            await InsertImageToCell(worksheet, row, columnMapping[column].Index, detail.Product?.ImageUrl, imageDict);
                            continue;
                        }

                        if (column.Equals("barcode", StringComparison.OrdinalIgnoreCase))
                        {
                            cell.Style.NumberFormat.Format = "@";
                        }

                        cell.Value = GetExportCellValue(detail, column, rowIndex);
                    }

                    // 设置数据行居中对齐
                    var dataRange = worksheet.Range(row, 1, row, columnMapping.Count);
                    dataRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    dataRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                    if (selectedColumns.Contains("image", StringComparer.OrdinalIgnoreCase))
                    {
                        worksheet.Row(row).Height = 70;
                    }

                    row++;
                    rowIndex++;
                }

                // 调整列宽
                foreach (var column in selectedColumns)
                {
                    worksheet.Column(columnMapping[column].Index).Width = GetExcelColumnWidth(column);
                }


                // 转换为字节数组
                using var stream = new MemoryStream();
                workbook.SaveAs(stream);
                return stream.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成Excel文件失败");
                throw;
            }
        }

        #endregion

        #region PDF导出

        /// <summary>
        /// 生成PDF文件
        /// </summary>
        public async Task<byte[]> GeneratePdfFileAsync(
            YiwuContainerDto container,
            List<YiwuContainerDetailDto> details,
            List<string>? exportColumns = null)
        {
            try
            {
                var selectedColumns = ResolveExportColumns(exportColumns);

                using var stream = new MemoryStream();
                var document = new Document(PageSize.A4.Rotate(), 20, 20, 30, 30);
                var writer = PdfWriter.GetInstance(document, stream);

                document.Open();

                // 设置中文字体：走线程安全的共享提供者，避免冷启动并发首次创建 CJK 字体时 iTextSharp 静态缓存重复 Add
                var baseFont = PdfCjkFontProvider.SimplifiedChinese;
                var titleFont = new iTextSharp.text.Font(baseFont, 16, iTextSharp.text.Font.BOLD);
                var headerFont = new iTextSharp.text.Font(baseFont, 12, iTextSharp.text.Font.BOLD);
                var normalFont = new iTextSharp.text.Font(baseFont, 10, iTextSharp.text.Font.NORMAL);

                // 标题
                var title = new Paragraph($"货柜明细 - {container.ContainerNumber}", titleFont);
                title.Alignment = Element.ALIGN_CENTER;
                document.Add(title);
                document.Add(new Paragraph(" ")); // 空行

                // 货柜基本信息
                var infoTable = new PdfPTable(4);
                infoTable.WidthPercentage = 100;
                infoTable.SetWidths(new float[] { 1, 1, 1, 1 });

                infoTable.AddCell(new PdfPCell(new Phrase("货柜编号", headerFont)) { BackgroundColor = new BaseColor(240, 240, 240) });
                infoTable.AddCell(new PdfPCell(new Phrase(container.ContainerNumber ?? "", normalFont)));
                infoTable.AddCell(new PdfPCell(new Phrase("装柜日期", headerFont)) { BackgroundColor = new BaseColor(240, 240, 240) });
                infoTable.AddCell(new PdfPCell(new Phrase(container.LoadingDate?.ToString("yyyy-MM-dd") ?? "", normalFont)));

                infoTable.AddCell(new PdfPCell(new Phrase("预计到岸", headerFont)) { BackgroundColor = new BaseColor(240, 240, 240) });
                infoTable.AddCell(new PdfPCell(new Phrase(container.EstimatedArrivalDate?.ToString("yyyy-MM-dd") ?? "", normalFont)));
                infoTable.AddCell(new PdfPCell(new Phrase("实际到货", headerFont)) { BackgroundColor = new BaseColor(240, 240, 240) });
                infoTable.AddCell(new PdfPCell(new Phrase(container.ActualArrivalDate?.ToString("yyyy-MM-dd") ?? "", normalFont)));

                infoTable.AddCell(new PdfPCell(new Phrase("合计件数", headerFont)) { BackgroundColor = new BaseColor(240, 240, 240) });
                infoTable.AddCell(new PdfPCell(new Phrase(container.TotalPieces?.ToString("N2") ?? "", normalFont)));
                infoTable.AddCell(new PdfPCell(new Phrase("总体积", headerFont)) { BackgroundColor = new BaseColor(240, 240, 240) });
                infoTable.AddCell(new PdfPCell(new Phrase(container.TotalVolume?.ToString("N3") ?? "", normalFont)));

                document.Add(infoTable);
                document.Add(new Paragraph(" ")); // 空行

                // 如果包含图片列，批量下载图片
                Dictionary<string, byte[]>? imageDict = null;
                if (selectedColumns.Contains("image", StringComparer.OrdinalIgnoreCase))
                {
                    var imageUrls = details
                        .Where(d => !string.IsNullOrEmpty(d.Product?.ImageUrl))
                        .Select(d => d.Product!.ImageUrl!)
                        .Distinct()
                        .ToList();

                    if (imageUrls.Any())
                    {
                        _logger.LogInformation("开始并行下载 {ImageCount} 张图片用于PDF", imageUrls.Count);
                        imageDict = await DownloadImagesInParallelAsync(imageUrls);
                        _logger.LogInformation("PDF图片下载完成，成功下载 {SuccessCount} 张", imageDict.Count);
                    }
                }

                // 明细表格
                var columnCount = selectedColumns.Count;
                var detailTable = new PdfPTable(columnCount);
                detailTable.WidthPercentage = 100;

                // 设置列宽（根据列的类型调整）
                var widths = new List<float>();
                foreach (var column in selectedColumns)
                {
                    widths.Add(column switch
                    {
                        "image" => 2f,
                        "englishName" => 3f,
                        "chineseName" => 3f,
                        "remarks" => 2f,
                        _ => 1f
                    });
                }
                detailTable.SetWidths(widths.ToArray());

                // 表头
                foreach (var column in selectedColumns)
                {
                    var headerText = ExportColumnHeaders[column];

                    detailTable.AddCell(new PdfPCell(new Phrase(headerText, headerFont))
                    {
                        BackgroundColor = new BaseColor(240, 240, 240),
                        HorizontalAlignment = Element.ALIGN_CENTER,
                        VerticalAlignment = Element.ALIGN_MIDDLE
                    });
                }

                // 按货号升序排列数据
                var sortedDetails = details.OrderBy(d => d.Product?.ItemNumber).ToList();

                // 数据行
                var rowIndex = 1;
                foreach (var detail in sortedDetails)
                {
                    foreach (var column in selectedColumns)
                    {
                        PdfPCell cell;

                        if (column == "image")
                        {
                            // 处理图片列
                            cell = CreateImageCell(detail.Product?.ImageUrl, imageDict, normalFont);
                        }
                        else
                        {
                            // 处理其他列
                            var cellValue = GetExportCellValue(detail, column, rowIndex);

                            cell = new PdfPCell(new Phrase(cellValue, normalFont))
                            {
                                HorizontalAlignment = Element.ALIGN_CENTER,
                                VerticalAlignment = Element.ALIGN_MIDDLE
                            };
                        }

                        detailTable.AddCell(cell);
                    }
                    rowIndex++;
                }

                document.Add(detailTable);
                document.Close();

                return stream.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成PDF文件失败");
                throw;
            }
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 图片计数器，用于生成唯一的短名称
        /// </summary>
        private static int _imageCounter = 0;

        private static List<string> ResolveExportColumns(List<string>? exportColumns)
        {
            var sourceColumns = exportColumns?.Count > 0 ? exportColumns : DefaultExportColumns;
            var selectedColumns = new List<string>();
            foreach (var requestedColumn in sourceColumns)
            {
                var normalized = requestedColumn?.Trim();
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                if (ExportColumnAliases.TryGetValue(normalized, out var alias))
                {
                    normalized = alias;
                }

                // 导出列来自前端，必须通过白名单过滤并规范化，避免大小写不同导致空列。
                var canonicalColumn = ExportColumnHeaders.Keys.FirstOrDefault(column =>
                    string.Equals(column, normalized, StringComparison.OrdinalIgnoreCase)
                );
                if (
                    canonicalColumn != null
                    && !selectedColumns.Contains(canonicalColumn, StringComparer.OrdinalIgnoreCase)
                )
                {
                    selectedColumns.Add(canonicalColumn);
                }
            }

            return selectedColumns.Count > 0 ? selectedColumns : DefaultExportColumns.ToList();
        }

        private static double GetExcelColumnWidth(string column)
        {
            return column switch
            {
                "index" => 8,
                "image" => 12,
                "itemNumber" => 15,
                "barcode" => 18,
                "chineseName" => 24,
                "englishName" => 25,
                "remarks" => 24,
                _ => 12,
            };
        }

        private static string GetExportCellValue(YiwuContainerDetailDto detail, string column, int rowIndex)
        {
            return column switch
            {
                "index" => rowIndex.ToString(),
                "itemNumber" => detail.Product?.ItemNumber ?? "",
                "barcode" => detail.Product?.Barcode ?? "--",
                "chineseName" => detail.Product?.ChineseName ?? "",
                "englishName" => detail.Product?.EnglishName ?? "",
                "domesticPrice" => detail.DomesticPrice?.ToString("N2") ?? "",
                "transportCost" => detail.TransportCost?.ToString("N2") ?? "",
                "adjustmentRate" => detail.AdjustmentRate?.ToString("N2") ?? "",
                "importPrice" => detail.ImportPrice?.ToString("N2") ?? "",
                "oemPrice" => detail.OEMPrice?.ToString("N2") ?? "",
                "packingQuantity" => detail.PackingQuantity?.ToString("N0") ?? "",
                "loadingPieces" => detail.LoadingPieces?.ToString("N2") ?? "",
                "loadingQuantity" => detail.LoadingQuantity?.ToString("N0") ?? "",
                "unitVolume" => detail.UnitVolume?.ToString("N3") ?? "",
                "totalVolume" => detail.TotalVolume?.ToString("N3") ?? "",
                "remarks" => detail.Remarks ?? "",
                _ => "",
            };
        }

        /// <summary>
        /// 插入图片到Excel单元格（参考YiwuOrderService实现）
        /// </summary>
        private async Task InsertImageToCell(IXLWorksheet worksheet, int row, int col, string? imageUrl, Dictionary<string, byte[]>? imageDict)
        {
            var imageCell = worksheet.Cell(row, col);

            if (!string.IsNullOrEmpty(imageUrl) && imageDict != null && imageDict.TryGetValue(imageUrl, out var imageBytes))
            {
                try
                {
                    // 验证图片数据
                    if (imageBytes == null || imageBytes.Length == 0)
                    {
                        throw new ArgumentException("图片数据为空");
                    }

                    // 创建内存流并添加图片
                    using var imageStream = new MemoryStream(imageBytes);

                    // 使用ClosedXML正确方式嵌入图片到单元格
                    var imageId = Interlocked.Increment(ref _imageCounter);
                    var imageName = $"Img_{imageId}";
                    var picture = worksheet.AddPicture(imageStream, imageName);

                    // 先将图片定位到单元格（这样才能设置尺寸）
                    picture.MoveTo(imageCell, 5, 5); // 5像素边距，确保图片完全在单元格内

                    // 然后设置固定图片尺寸（避免覆盖其他列）
                    picture.Width = 60;  // 固定宽度60像素
                    picture.Height = 60; // 固定高度60像素

                    // 设置图片单元格样式
                    imageCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    imageCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                    _logger.LogDebug("成功插入图片到Excel: {ImageUrl}, 大小: {Width}x{Height}", imageUrl, picture.Width, picture.Height);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "插入图片到Excel失败: {ImageUrl}, 错误: {Error}", imageUrl, ex.Message);
                    // 插入失败时显示文本
                    imageCell.Value = "图片插入失败";
                    imageCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    imageCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    imageCell.Style.Font.FontSize = 8;
                }
            }
            else if (!string.IsNullOrEmpty(imageUrl))
            {
                imageCell.Value = "图片下载失败";
                imageCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                imageCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                imageCell.Style.Font.FontSize = 8;
            }
            else
            {
                imageCell.Value = "无图片";
                imageCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                imageCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                imageCell.Style.Font.FontSize = 8;
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// 下载单个图片
        /// </summary>
        private async Task<byte[]?> DownloadImageAsync(string imageUrl)
        {
            try
            {
                using var response = await _httpClient.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsByteArrayAsync();
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "下载图片失败: {ImageUrl}", imageUrl);
                return null;
            }
        }

        /// <summary>
        /// 并行下载多个图片
        /// </summary>
        private async Task<Dictionary<string, byte[]>> DownloadImagesInParallelAsync(IEnumerable<string> imageUrls, int maxConcurrency = 10)
        {
            var semaphore = new SemaphoreSlim(maxConcurrency);
            var imageTasks = imageUrls.Select(async imageUrl =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var imageBytes = await DownloadImageAsync(imageUrl);
                    return new { ImageUrl = imageUrl, ImageBytes = imageBytes };
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var results = await Task.WhenAll(imageTasks);
            return results
                .Where(r => r.ImageBytes != null)
                .ToDictionary(r => r.ImageUrl, r => r.ImageBytes!);
        }

        /// <summary>
        /// 创建包含图片的PDF单元格
        /// </summary>
        private PdfPCell CreateImageCell(string? imageUrl, Dictionary<string, byte[]>? imageDict, iTextSharp.text.Font font)
        {
            try
            {
                if (!string.IsNullOrEmpty(imageUrl) && imageDict != null && imageDict.TryGetValue(imageUrl, out var imageBytes))
                {
                    // 创建图片对象
                    var image = iTextSharp.text.Image.GetInstance(imageBytes);

                    // 设置固定的单元格尺寸限制
                    float maxWidth = 50f;
                    float maxHeight = 50f;

                    // 计算缩放比例，保持宽高比
                    float scaleX = maxWidth / image.Width;
                    float scaleY = maxHeight / image.Height;
                    float scale = Math.Min(scaleX, scaleY); // 选择较小的缩放比例以确保图片完全适配

                    // 应用缩放（使用ScalePercent而不是ScaleAbsolute）
                    image.ScalePercent(scale * 100);

                    // 创建包含图片的单元格
                    var cell = new PdfPCell(image, true)
                    {
                        HorizontalAlignment = Element.ALIGN_CENTER,
                        VerticalAlignment = Element.ALIGN_MIDDLE,
                        FixedHeight = 55f, // 固定单元格高度
                        Padding = 2f
                    };

                    return cell;
                }
                else if (!string.IsNullOrEmpty(imageUrl))
                {
                    // 图片下载失败
                    return new PdfPCell(new Phrase("图片下载失败", font))
                    {
                        HorizontalAlignment = Element.ALIGN_CENTER,
                        VerticalAlignment = Element.ALIGN_MIDDLE,
                        MinimumHeight = 55f
                    };
                }
                else
                {
                    // 无图片
                    return new PdfPCell(new Phrase("无图片", font))
                    {
                        HorizontalAlignment = Element.ALIGN_CENTER,
                        VerticalAlignment = Element.ALIGN_MIDDLE,
                        MinimumHeight = 55f
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "创建PDF图片单元格失败: {ImageUrl}", imageUrl);
                return new PdfPCell(new Phrase("图片处理失败", font))
                {
                    HorizontalAlignment = Element.ALIGN_CENTER,
                    VerticalAlignment = Element.ALIGN_MIDDLE,
                    MinimumHeight = 55f
                };
            }
        }

        #endregion
    }
}

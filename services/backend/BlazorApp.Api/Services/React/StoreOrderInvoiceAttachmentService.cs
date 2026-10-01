using System.Text;
using BlazorApp.Api.Features.StoreOrders.Invoice;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.Pdf;
using BlazorApp.Shared.DTOs;
using ClosedXML.Excel;
using iTextSharp.text;
using iTextSharp.text.pdf;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 分店订货发票邮件附件生成服务。
    /// </summary>
    public class StoreOrderInvoiceAttachmentService : IStoreOrderInvoiceAttachmentService
    {
        private const string PdfContentType = "application/pdf";
        private const string ExcelContentType =
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        private const string WarehouseAddressTitle = "WAREHOUSE ADDRESS:";
        private const string WarehouseAddressLine =
            "3 Rogilla close Maryland, NSW, 2287, Australia";
        private const string WarehouseAbn = "A.B.N. 35 160 589 793";
        private const string WarehouseEmail = "WAREHOUSE EMAIL: dong@hotbargain.com.au";
        private const string PaymentDetailTitle = "PAYMENT DETAIL: DIRECT DEBIT";
        private const string PaymentName = "HOT BARGAIN INTERNATIONAL";
        // 公司收款 BSB 需保留银行分隔符，PDF/邮件附件与网页发票保持一致。
        private const string PaymentBsb = "012-532";
        private const string PaymentAccount = "208034605";
        private const string PaymentDisclaimer =
            "All products remain the property of Hot Bargain International Pty Ltd until payment is received in full for the invoiced amount. Payment strictly within 30 days of the invoice date.";

        private readonly IStoreOrderInvoiceDetailReader _invoiceDetailReader;
        private readonly ILogger<StoreOrderInvoiceAttachmentService> _logger;

        public StoreOrderInvoiceAttachmentService(
            IStoreOrderInvoiceDetailReader invoiceDetailReader,
            ILogger<StoreOrderInvoiceAttachmentService> logger
        )
        {
            _invoiceDetailReader = invoiceDetailReader;
            _logger = logger;
        }

        public async Task<ApiResponse<StoreOrderInvoiceAttachmentBundle>> GenerateAttachmentsAsync(
            string orderGuid,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                var normalizedOrderGuid = orderGuid.Trim();
                var orderResult = await _invoiceDetailReader.GetInvoiceDetailAsync(
                    normalizedOrderGuid,
                    cancellationToken
                );
                if (!orderResult.Success || orderResult.Data == null)
                {
                    return ApiResponse<StoreOrderInvoiceAttachmentBundle>.Error(
                        string.IsNullOrWhiteSpace(orderResult.Message)
                            ? "订单不存在"
                            : orderResult.Message,
                        "STORE_ORDER_INVOICE_ATTACHMENT_ORDER_NOT_FOUND"
                    );
                }

                var order = orderResult.Data;
                var baseFileName = BuildBaseFileName(order);
                var pdfBytes = GeneratePdf(order);
                var excelBytes = GenerateExcel(order);

                return ApiResponse<StoreOrderInvoiceAttachmentBundle>.OK(
                    new StoreOrderInvoiceAttachmentBundle
                    {
                        OrderGUID = order.OrderGUID,
                        OrderNo = order.OrderNo,
                        StoreCode = order.StoreCode,
                        Attachments = new List<StoreOrderInvoiceEmailAttachment>
                        {
                            new()
                            {
                                FileName = $"{baseFileName}.pdf",
                                ContentType = PdfContentType,
                                Bytes = pdfBytes,
                            },
                            new()
                            {
                                FileName = $"{baseFileName}.xlsx",
                                ContentType = ExcelContentType,
                                Bytes = excelBytes,
                            },
                        },
                    },
                    "发票附件生成成功"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成分店订货发票邮件附件失败: {OrderGuid}", orderGuid);
                return ApiResponse<StoreOrderInvoiceAttachmentBundle>.Error(
                    "生成发票附件失败",
                    "STORE_ORDER_INVOICE_ATTACHMENT_GENERATION_FAILED"
                );
            }
        }

        private static byte[] GenerateExcel(StoreOrderCartDto order)
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Invoice");
            var invoiceDate = FormatInvoiceDate(order);

            worksheet.Cell(1, 1).Value = "INVOICE";
            var titleRange = worksheet.Range(1, 1, 1, 9).Merge();
            titleRange.Style.Font.Bold = true;
            titleRange.Style.Font.FontSize = 14;

            worksheet.Cell(2, 1).Value = $"INVOICE NO. {order.OrderNo ?? order.OrderGUID}";
            worksheet.Range(2, 1, 2, 4).Merge();
            worksheet.Cell(2, 5).Value = $"INVOICE DATE: {invoiceDate}";
            worksheet.Range(2, 5, 2, 9).Merge();
            worksheet.Cell(3, 1).Value = "CUSTOMER:";
            worksheet.Cell(3, 2).Value = order.StoreName ?? order.StoreCode ?? "--";
            worksheet.Cell(4, 1).Value = "CUSTOMER CONTACT:";
            worksheet.Cell(4, 2).Value = order.StoreContactEmail ?? "--";
            worksheet.Cell(5, 1).Value = "ADDRESS:";
            worksheet.Cell(5, 2).Value = order.StoreAddress ?? "--";
            worksheet.Range(3, 1, 5, 1).Style.Font.Bold = true;

            // Excel 附件页头与前端导出保持同一核对口径，明细表从基础信息后开始。
            var headerRowIndex = 7;
            worksheet.Cell(headerRowIndex, 1).Value = "#";
            worksheet.Cell(headerRowIndex, 2).Value = "Item No";
            worksheet.Cell(headerRowIndex, 3).Value = "Name";
            worksheet.Cell(headerRowIndex, 4).Value = "Barcode";
            worksheet.Cell(headerRowIndex, 5).Value = "Cost";
            worksheet.Cell(headerRowIndex, 6).Value = "RRP";
            worksheet.Cell(headerRowIndex, 7).Value = "Order Qty";
            worksheet.Cell(headerRowIndex, 8).Value = "Ship Qty";
            worksheet.Cell(headerRowIndex, 9).Value = "Subtotal";
            worksheet.Range(headerRowIndex, 1, headerRowIndex, 9).Style.Font.Bold = true;

            var rowIndex = headerRowIndex + 1;
            var itemIndex = 1;
            foreach (var item in SortItems(order.Items))
            {
                var orderQuantity = item.Quantity;
                var allocQuantity = item.AllocQuantity ?? 0m;
                var subtotal = decimal.Round(item.AllocatedImportAmount, 2);

                worksheet.Cell(rowIndex, 1).Value = itemIndex;
                worksheet.Cell(rowIndex, 2).Value = item.ItemNumber ?? string.Empty;
                worksheet.Cell(rowIndex, 3).Value = item.ProductName ?? string.Empty;
                worksheet.Cell(rowIndex, 4).Value = item.Barcode ?? item.ProductCode;
                worksheet.Cell(rowIndex, 5).Value = item.ImportPrice;
                if (item.RRP.HasValue)
                {
                    worksheet.Cell(rowIndex, 6).Value = item.RRP.Value;
                }
                worksheet.Cell(rowIndex, 7).Value = orderQuantity;
                worksheet.Cell(rowIndex, 8).Value = allocQuantity;
                worksheet.Cell(rowIndex, 9).Value = subtotal;
                rowIndex += 1;
                itemIndex += 1;
            }

            var subTotal = order.TotalAllocatedImportAmount;
            var gst = decimal.Round(subTotal * 0.1m, 2);
            var freight = order.ShippingFee ?? 0m;
            var total = decimal.Round(subTotal + gst + freight, 2);

            rowIndex += 1;
            worksheet.Cell(rowIndex++, 3).Value = "Sub-Total:";
            worksheet.Cell(rowIndex - 1, 9).Value = subTotal;
            worksheet.Cell(rowIndex++, 3).Value = "GST 10%:";
            worksheet.Cell(rowIndex - 1, 9).Value = gst;
            worksheet.Cell(rowIndex++, 3).Value = "Freight:";
            worksheet.Cell(rowIndex - 1, 9).Value = freight;
            worksheet.Cell(rowIndex++, 3).Value = "Total:";
            worksheet.Cell(rowIndex - 1, 9).Value = total;
            worksheet.Cell(rowIndex + 1, 3).Value = "Remarks";
            worksheet.Cell(rowIndex + 1, 4).Value = string.IsNullOrWhiteSpace(order.Remarks)
                ? "Image for reference only, actual product may vary"
                : order.Remarks.Trim();

            worksheet.Columns().AdjustToContents();
            worksheet.Column(5).Style.NumberFormat.Format = "$#,##0.00";
            worksheet.Column(6).Style.NumberFormat.Format = "$#,##0.00";
            worksheet.Column(9).Style.NumberFormat.Format = "$#,##0.00";

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return stream.ToArray();
        }

        private byte[] GeneratePdf(StoreOrderCartDto order)
        {
            using var stream = new MemoryStream();
            using var document = new Document(PageSize.A4.Rotate(), 24, 24, 24, 24);
            PdfWriter.GetInstance(document, stream);
            document.Open();

            // 中文字体走线程安全的共享提供者，避免冷启动并发首次创建 CJK 字体时 iTextSharp 静态缓存重复 Add
            var baseFont = PdfCjkFontProvider.SimplifiedChinese;
            var englishBaseFont = BaseFont.CreateFont(
                BaseFont.HELVETICA,
                BaseFont.WINANSI,
                BaseFont.NOT_EMBEDDED
            );
            var headerFont = new iTextSharp.text.Font(baseFont, 9, iTextSharp.text.Font.BOLD);
            var bodyFont = new iTextSharp.text.Font(baseFont, 8, iTextSharp.text.Font.NORMAL);
            var englishTitleFont = new iTextSharp.text.Font(
                englishBaseFont,
                14,
                iTextSharp.text.Font.BOLD
            );
            var englishHeaderFont = new iTextSharp.text.Font(
                englishBaseFont,
                9,
                iTextSharp.text.Font.BOLD
            );
            var englishBodyFont = new iTextSharp.text.Font(
                englishBaseFont,
                8,
                iTextSharp.text.Font.NORMAL
            );
            var englishBoldFont = new iTextSharp.text.Font(
                englishBaseFont,
                8,
                iTextSharp.text.Font.BOLD
            );

            AddInvoiceHeader(document, englishTitleFont, englishHeaderFont, englishBodyFont);
            AddDivider(document);
            AddInvoiceSummary(document, order, englishTitleFont);

            var infoTable = new PdfPTable(4) { WidthPercentage = 100, SpacingAfter = 10 };
            infoTable.SetWidths(new float[] { 1.2f, 2.2f, 1.2f, 2.2f });
            AddLabelCell(infoTable, "CUSTOMER:", englishBoldFont);
            AddValueCell(infoTable, order.StoreCode ?? "--", englishBodyFont);
            AddLabelCell(infoTable, "CUSTOMER CONTACT:", englishBoldFont);
            AddValueCell(infoTable, order.StoreContactEmail ?? "--", englishBodyFont);
            AddLabelCell(infoTable, "ADDRESS:", englishBoldFont);
            AddValueCell(infoTable, order.StoreAddress ?? "--", bodyFont);
            AddLabelCell(infoTable, string.Empty, englishBoldFont);
            AddValueCell(infoTable, string.Empty, englishBodyFont);
            document.Add(infoTable);

            var detailTable = new PdfPTable(9) { WidthPercentage = 100, SpacingAfter = 10 };
            detailTable.SetWidths(new float[] { 0.45f, 1.05f, 1.45f, 2.9f, 0.85f, 0.85f, 0.85f, 0.85f, 1.1f });
            foreach (var header in new[]
                     {
                         "#",
                         "Item No.",
                         "Barcode",
                         "Name",
                         "Cost",
                         "RRP",
                         "Order Qty",
                         "Ship Qty",
                         "Subtotal",
                     })
            {
                AddHeaderCell(detailTable, header, headerFont);
            }

            var itemIndex = 1;
            foreach (var item in SortItems(order.Items))
            {
                var allocQuantity = item.AllocQuantity ?? 0m;
                var subtotal = decimal.Round(item.AllocatedImportAmount, 2);
                AddValueCell(detailTable, itemIndex.ToString(), bodyFont);
                AddValueCell(detailTable, item.ItemNumber ?? string.Empty, bodyFont);
                AddValueCell(detailTable, item.Barcode ?? item.ProductCode, bodyFont);
                AddValueCell(detailTable, item.ProductName ?? string.Empty, bodyFont);
                AddValueCell(detailTable, FormatCurrency(item.ImportPrice), bodyFont);
                AddValueCell(detailTable, FormatOptionalCurrency(item.RRP), bodyFont);
                AddValueCell(detailTable, item.Quantity.ToString("0.##"), bodyFont);
                AddValueCell(detailTable, allocQuantity.ToString("0.##"), bodyFont);
                AddValueCell(detailTable, FormatCurrency(subtotal), bodyFont);
                itemIndex += 1;
            }
            document.Add(detailTable);

            var subTotal = order.TotalAllocatedImportAmount;
            var gst = decimal.Round(subTotal * 0.1m, 2);
            var freight = order.ShippingFee ?? 0m;
            var total = decimal.Round(subTotal + gst + freight, 2);
            AddPaymentAndTotalsFooter(
                document,
                subTotal,
                gst,
                freight,
                total,
                englishBoldFont,
                englishBodyFont
            );
            if (!string.IsNullOrWhiteSpace(order.Remarks))
            {
                document.Add(
                    new Paragraph(SanitizePdfText($"REMARKS: {order.Remarks.Trim()}"), bodyFont)
                );
            }

            document.Close();
            return stream.ToArray();
        }

        private void AddInvoiceHeader(
            Document document,
            iTextSharp.text.Font logoFallbackFont,
            iTextSharp.text.Font headerFont,
            iTextSharp.text.Font bodyFont
        )
        {
            var headerTable = new PdfPTable(2) { WidthPercentage = 100, SpacingAfter = 6 };
            headerTable.SetWidths(new float[] { 1.25f, 2f });

            var logoCell = CreateNoBorderCell();
            var logo = TryCreateInvoiceLogo();
            if (logo != null)
            {
                logoCell.AddElement(logo);
            }
            else
            {
                logoCell.AddElement(new Paragraph("HOT BARGAIN", logoFallbackFont));
            }
            headerTable.AddCell(logoCell);

            var warehouseCell = CreateNoBorderCell();
            warehouseCell.HorizontalAlignment = Element.ALIGN_RIGHT;
            warehouseCell.AddElement(
                new Paragraph(WarehouseAddressTitle, headerFont)
                {
                    Alignment = Element.ALIGN_RIGHT,
                    SpacingAfter = 4,
                }
            );
            foreach (var line in new[] { WarehouseAddressLine, WarehouseAbn, WarehouseEmail })
            {
                warehouseCell.AddElement(
                    new Paragraph(line, bodyFont) { Alignment = Element.ALIGN_RIGHT }
                );
            }
            headerTable.AddCell(warehouseCell);

            document.Add(headerTable);
        }

        private iTextSharp.text.Image? TryCreateInvoiceLogo()
        {
            var logoPath = ResolveInvoiceLogoPath();
            if (logoPath == null)
            {
                _logger.LogWarning("发票邮件 PDF logo 文件不存在，已使用文字抬头继续生成附件");
                return null;
            }

            try
            {
                var logo = iTextSharp.text.Image.GetInstance(logoPath);
                logo.ScaleToFit(210f, 112f);
                logo.Alignment = Element.ALIGN_LEFT;
                return logo;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取发票邮件 PDF logo 失败: {LogoPath}", logoPath);
                return null;
            }
        }

        private static string? ResolveInvoiceLogoPath()
        {
            var outputPath = Path.Combine(AppContext.BaseDirectory, "Assets", "invoice-logo.png");
            if (File.Exists(outputPath))
            {
                return outputPath;
            }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var sourcePath = Path.Combine(
                    directory.FullName,
                    "BlazorApp.Shared",
                    "Helper",
                    "logo",
                    "HB_logo(1).png"
                );
                if (File.Exists(sourcePath))
                {
                    return sourcePath;
                }

                directory = directory.Parent;
            }

            return null;
        }

        private static void AddDivider(Document document)
        {
            var divider = new PdfPTable(1) { WidthPercentage = 100, SpacingAfter = 8 };
            divider.AddCell(
                new PdfPCell
                {
                    Border = Rectangle.TOP_BORDER,
                    BorderColorTop = new BaseColor(70, 70, 70),
                    BorderWidthTop = 1.5f,
                    FixedHeight = 2,
                    Padding = 0,
                }
            );
            document.Add(divider);
        }

        private static void AddInvoiceSummary(
            Document document,
            StoreOrderCartDto order,
            iTextSharp.text.Font font
        )
        {
            var summaryTable = new PdfPTable(2) { WidthPercentage = 100, SpacingAfter = 8 };
            summaryTable.SetWidths(new float[] { 1f, 1f });
            summaryTable.AddCell(
                new PdfPCell(
                    new Phrase(
                        SanitizePdfText($"INVOICE NO. {order.OrderNo ?? order.OrderGUID}"),
                        font
                    )
                )
                {
                    Border = Rectangle.BOTTOM_BORDER,
                    BorderColorBottom = new BaseColor(130, 130, 130),
                    PaddingBottom = 8,
                }
            );
            summaryTable.AddCell(
                new PdfPCell(new Phrase($"INVOICE DATE: {FormatInvoiceDate(order)}", font))
                {
                    Border = Rectangle.BOTTOM_BORDER,
                    BorderColorBottom = new BaseColor(130, 130, 130),
                    HorizontalAlignment = Element.ALIGN_RIGHT,
                    PaddingBottom = 8,
                }
            );
            document.Add(summaryTable);
        }

        private static void AddPaymentAndTotalsFooter(
            Document document,
            decimal subTotal,
            decimal gst,
            decimal freight,
            decimal total,
            iTextSharp.text.Font boldFont,
            iTextSharp.text.Font bodyFont
        )
        {
            var footerTable = new PdfPTable(2) { WidthPercentage = 100, SpacingAfter = 8 };
            footerTable.SetWidths(new float[] { 1.55f, 1f });

            var paymentCell = CreateNoBorderCell();
            paymentCell.AddElement(
                new Paragraph(PaymentDetailTitle, boldFont) { SpacingAfter = 8 }
            );

            var paymentTable = new PdfPTable(2)
            {
                WidthPercentage = 65,
                HorizontalAlignment = Element.ALIGN_LEFT,
            };
            paymentTable.SetWidths(new float[] { 0.8f, 2f });
            AddNoBorderLabelCell(paymentTable, "NAME:", boldFont);
            AddNoBorderValueCell(paymentTable, PaymentName, bodyFont);
            AddNoBorderLabelCell(paymentTable, "BSB:", boldFont);
            AddNoBorderValueCell(paymentTable, PaymentBsb, bodyFont);
            AddNoBorderLabelCell(paymentTable, "ACCOUNT:", boldFont);
            AddNoBorderValueCell(paymentTable, PaymentAccount, bodyFont);
            paymentCell.AddElement(paymentTable);
            paymentCell.AddElement(
                new Paragraph(PaymentDisclaimer, bodyFont)
                {
                    SpacingBefore = 8,
                    Leading = 11,
                }
            );
            footerTable.AddCell(paymentCell);

            var totalTable = new PdfPTable(2) { WidthPercentage = 100 };
            totalTable.SetWidths(new float[] { 1.45f, 1f });
            AddNoBorderLabelCell(totalTable, "Sub-Total:", bodyFont);
            AddNoBorderRightValueCell(totalTable, FormatCurrency(subTotal), bodyFont);
            AddNoBorderLabelCell(totalTable, "GST 10%:", bodyFont);
            AddNoBorderRightValueCell(totalTable, FormatCurrency(gst), bodyFont);
            AddNoBorderLabelCell(totalTable, "Freight:", bodyFont);
            AddNoBorderRightValueCell(totalTable, FormatCurrency(freight), bodyFont);
            AddTotalDividerRow(totalTable);
            AddNoBorderLabelCell(totalTable, "Total Before Discount:", boldFont);
            AddNoBorderRightValueCell(totalTable, FormatCurrency(total), boldFont);

            var totalsCell = CreateNoBorderCell();
            totalsCell.AddElement(totalTable);
            footerTable.AddCell(totalsCell);

            document.Add(footerTable);
        }

        private static List<StoreOrderCartItemDto> SortItems(IEnumerable<StoreOrderCartItemDto>? items)
        {
            return (items ?? Enumerable.Empty<StoreOrderCartItemDto>())
                .OrderBy(item => (item.AllocQuantity ?? 0m) == 0m ? 1 : 0)
                .ThenBy(item => item.ItemNumber ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ProductCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string BuildBaseFileName(StoreOrderCartDto order)
        {
            var storePart = SanitizeFileNamePart(order.StoreCode ?? "UnknownStore");
            var orderPart = SanitizeFileNamePart(order.OrderNo ?? order.OrderGUID);
            var datePart = SanitizeFileNamePart(FormatInvoiceFileDate(order));
            return $"Invoice_{storePart}_{orderPart}_{datePart}";
        }

        private static DateTime ResolveInvoiceDate(StoreOrderCartDto order)
        {
            // 发票日期优先使用出库日期；历史数据没有出库日期时才回退订单日期。
            return (order.OutboundDate ?? order.OrderDate ?? DateTime.Now).Date;
        }

        private static string FormatInvoiceDate(StoreOrderCartDto order)
        {
            return $"{ResolveInvoiceDate(order):yyyy/M/d}";
        }

        private static string FormatInvoiceFileDate(StoreOrderCartDto order)
        {
            return $"{ResolveInvoiceDate(order):yyyy-MM-dd}";
        }

        private static string SanitizeFileNamePart(string value)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var cleaned = new string(
                (value ?? string.Empty)
                    .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
                    .ToArray()
            );
            cleaned = string.Join("_", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
        }

        private static string FormatCurrency(decimal value)
        {
            return $"${value:0.00}";
        }

        private static string FormatOptionalCurrency(decimal? value)
        {
            // RRP 为空代表商品主数据缺失，附件里留空比显示 $0.00 更安全。
            return value.HasValue ? FormatCurrency(value.Value) : string.Empty;
        }

        private static void AddHeaderCell(PdfPTable table, string text, iTextSharp.text.Font font)
        {
            table.AddCell(new PdfPCell(new Phrase(text, font))
            {
                BackgroundColor = new BaseColor(240, 240, 240),
                Padding = 4,
            });
        }

        private static void AddLabelCell(PdfPTable table, string text, iTextSharp.text.Font font)
        {
            table.AddCell(new PdfPCell(new Phrase(text, font))
            {
                Padding = 4,
                BorderColor = new BaseColor(220, 220, 220),
            });
        }

        private static void AddValueCell(PdfPTable table, string text, iTextSharp.text.Font font)
        {
            table.AddCell(new PdfPCell(new Phrase(SanitizePdfText(text), font))
            {
                Padding = 4,
                BorderColor = new BaseColor(220, 220, 220),
            });
        }

        private static string SanitizePdfText(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var result = new StringBuilder(value.Length);
            for (var index = 0; index < value.Length; index += 1)
            {
                var character = value[index];
                if (char.IsHighSurrogate(character)
                    && index + 1 < value.Length
                    && char.IsLowSurrogate(value[index + 1]))
                {
                    // 当前 CJK 字体不支持非 BMP 字符，用可打印占位符避免 iTextSharp 越界。
                    result.Append('?');
                    index += 1;
                    continue;
                }

                result.Append(char.IsSurrogate(character) ? '?' : character);
            }

            return result.ToString();
        }

        private static PdfPCell CreateNoBorderCell()
        {
            return new PdfPCell
            {
                Border = Rectangle.NO_BORDER,
                Padding = 4,
            };
        }

        private static void AddNoBorderLabelCell(
            PdfPTable table,
            string text,
            iTextSharp.text.Font font
        )
        {
            table.AddCell(
                new PdfPCell(new Phrase(text, font))
                {
                    Border = Rectangle.NO_BORDER,
                    Padding = 3,
                }
            );
        }

        private static void AddNoBorderValueCell(
            PdfPTable table,
            string text,
            iTextSharp.text.Font font
        )
        {
            table.AddCell(
                new PdfPCell(new Phrase(text, font))
                {
                    Border = Rectangle.NO_BORDER,
                    Padding = 3,
                }
            );
        }

        private static void AddNoBorderRightValueCell(
            PdfPTable table,
            string text,
            iTextSharp.text.Font font
        )
        {
            table.AddCell(
                new PdfPCell(new Phrase(text, font))
                {
                    Border = Rectangle.NO_BORDER,
                    HorizontalAlignment = Element.ALIGN_RIGHT,
                    Padding = 3,
                }
            );
        }

        private static void AddTotalDividerRow(PdfPTable table)
        {
            table.AddCell(
                new PdfPCell(new Phrase(string.Empty))
                {
                    Border = Rectangle.TOP_BORDER,
                    BorderColorTop = new BaseColor(130, 130, 130),
                    Colspan = 2,
                    FixedHeight = 6,
                }
            );
        }
    }
}

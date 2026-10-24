using System.Text;
using BlazorApp.Api.Features.StoreOrders.Common;
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
        // 退款单 / 带退货调整项发票的固定说明文案，取自设计图 ③（版式示意，字段与措辞以会计确认为准）。
        private const string ReturnReference = "Return of goods previously supplied";
        private const string CreditNoteFooterText =
            "This credit adjusts the GST previously charged on the returned goods.";
        private const string InvoiceWithReturnsFooterText =
            "Returned goods are credited at the price supplied and netted against this invoice.";
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
                        DocumentType = ResolveDocumentType(order),
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
            var isCreditNote = IsCreditNote(order);
            var (goodsItems, returnItems) = SplitItems(order, isCreditNote);
            var hasReturns = returnItems.Count > 0;
            var worksheet = workbook.Worksheets.Add(isCreditNote ? "Credit Note" : "Invoice");
            var invoiceDate = FormatInvoiceDate(order);
            // 有退货行时多一列 Reason；没有退货行的普通发票列结构与之前完全一致。
            var lastColumn = hasReturns ? 10 : 9;

            worksheet.Cell(1, 1).Value = isCreditNote ? "ADJUSTMENT NOTE" : "INVOICE";
            var titleRange = worksheet.Range(1, 1, 1, lastColumn).Merge();
            titleRange.Style.Font.Bold = true;
            titleRange.Style.Font.FontSize = 14;

            worksheet.Cell(2, 1).Value = isCreditNote
                ? $"ADJUSTMENT NOTE NO. {order.OrderNo ?? order.OrderGUID}"
                : $"INVOICE NO. {order.OrderNo ?? order.OrderGUID}";
            worksheet.Range(2, 1, 2, 4).Merge();
            worksheet.Cell(2, 5).Value = isCreditNote
                ? $"DATE: {invoiceDate}"
                : $"INVOICE DATE: {invoiceDate}";
            worksheet.Range(2, 5, 2, lastColumn).Merge();
            worksheet.Cell(3, 1).Value = isCreditNote ? "CREDIT TO:" : "CUSTOMER:";
            worksheet.Cell(3, 2).Value = order.StoreName ?? order.StoreCode ?? "--";
            worksheet.Cell(4, 1).Value = "CUSTOMER CONTACT:";
            worksheet.Cell(4, 2).Value = order.StoreContactEmail ?? "--";
            worksheet.Cell(5, 1).Value = "ADDRESS:";
            worksheet.Cell(5, 2).Value = order.StoreAddress ?? "--";
            worksheet.Range(3, 1, 5, 1).Style.Font.Bold = true;
            if (isCreditNote)
            {
                // 空着的第 6 行放参考说明（版式示意，字段以会计确认为准）。
                worksheet.Cell(6, 1).Value = "REFERENCE:";
                worksheet.Cell(6, 1).Style.Font.Bold = true;
                worksheet.Cell(6, 2).Value = ReturnReference;
            }

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
            if (hasReturns)
            {
                worksheet.Cell(headerRowIndex, 10).Value = "Reason";
            }
            worksheet.Range(headerRowIndex, 1, headerRowIndex, lastColumn).Style.Font.Bold = true;

            var rowIndex = headerRowIndex + 1;
            var itemIndex = 1;

            void WriteItemRow(StoreOrderCartItemDto item, bool isReturnRow)
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
                if (isReturnRow)
                {
                    worksheet.Cell(rowIndex, 10).Value =
                        StoreOrderReturnRules.ToDocumentReason(item.ReturnReason) ?? string.Empty;
                }
                rowIndex += 1;
                itemIndex += 1;
            }

            void WriteSectionRow(string title)
            {
                worksheet.Cell(rowIndex, 1).Value = title;
                var sectionRange = worksheet.Range(rowIndex, 1, rowIndex, lastColumn).Merge();
                sectionRange.Style.Font.Bold = true;
                sectionRange.Style.Fill.BackgroundColor = XLColor.FromHtml(
                    title == ReturnedGoodsSectionTitle(isCreditNote) ? "#FDECEC" : "#F2F2F2"
                );
                rowIndex += 1;
            }

            if (hasReturns && goodsItems.Count > 0)
            {
                WriteSectionRow("Goods supplied");
            }

            foreach (var item in goodsItems)
            {
                WriteItemRow(item, isReturnRow: false);
            }

            if (hasReturns)
            {
                // 退货行单独成段（Adjustments / Returned goods），带原因英文文案；按原价冲减，金额为负。
                WriteSectionRow(ReturnedGoodsSectionTitle(isCreditNote));
                foreach (var item in returnItems)
                {
                    WriteItemRow(item, isReturnRow: true);
                }
            }

            // 所有金额都用同一个净额（TotalAllocatedImportAmount = 各行 发货数量 × 进口价 之和）；GST 对负数同向冲减。
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
            worksheet.Cell(rowIndex++, 3).Value = isCreditNote ? "Total Credit:" : "Total:";
            worksheet.Cell(rowIndex - 1, 9).Value = total;
            worksheet.Cell(rowIndex + 1, 3).Value = "Remarks";
            worksheet.Cell(rowIndex + 1, 4).Value = string.IsNullOrWhiteSpace(order.Remarks)
                ? "Image for reference only, actual product may vary"
                : order.Remarks.Trim();
            if (isCreditNote)
            {
                worksheet.Cell(rowIndex + 2, 3).Value = "Note";
                worksheet.Cell(rowIndex + 2, 4).Value = CreditNoteFooterText;
            }
            else if (hasReturns)
            {
                worksheet.Cell(rowIndex + 2, 3).Value = "Note";
                worksheet.Cell(rowIndex + 2, 4).Value = InvoiceWithReturnsFooterText;
            }

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

            // 单据类型按净额判定（净额 < 0 为退款单）；退货行单独成段。所有金额都用同一个净额。
            var isCreditNote = IsCreditNote(order);
            var (goodsItems, returnItems) = SplitItems(order, isCreditNote);
            var hasReturns = returnItems.Count > 0;

            AddInvoiceHeader(document, englishTitleFont, englishHeaderFont, englishBodyFont);
            AddDivider(document);
            if (isCreditNote)
            {
                AddCreditNoteBanner(document, englishTitleFont);
            }
            AddInvoiceSummary(document, order, englishTitleFont, isCreditNote);

            var infoTable = new PdfPTable(4) { WidthPercentage = 100, SpacingAfter = 10 };
            infoTable.SetWidths(new float[] { 1.2f, 2.2f, 1.2f, 2.2f });
            AddLabelCell(infoTable, isCreditNote ? "CREDIT TO:" : "CUSTOMER:", englishBoldFont);
            AddValueCell(infoTable, order.StoreCode ?? "--", englishBodyFont);
            AddLabelCell(infoTable, "CUSTOMER CONTACT:", englishBoldFont);
            AddValueCell(infoTable, order.StoreContactEmail ?? "--", englishBodyFont);
            AddLabelCell(infoTable, "ADDRESS:", englishBoldFont);
            AddValueCell(infoTable, order.StoreAddress ?? "--", bodyFont);
            AddLabelCell(infoTable, isCreditNote ? "REFERENCE:" : string.Empty, englishBoldFont);
            AddValueCell(infoTable, isCreditNote ? ReturnReference : string.Empty, englishBodyFont);
            document.Add(infoTable);

            AddDetailTable(
                document,
                goodsItems,
                returnItems,
                isCreditNote,
                headerFont,
                bodyFont,
                englishBoldFont
            );

            // 净额 = 各行 发货数量 × 进口价 之和（ex GST）；GST 对负数同向冲减，Total 与 Excel / 前端同一口径。
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
                englishBodyFont,
                isCreditNote
            );
            if (hasReturns && !isCreditNote)
            {
                document.Add(new Paragraph(InvoiceWithReturnsFooterText, englishBodyFont));
            }
            if (!string.IsNullOrWhiteSpace(order.Remarks))
            {
                document.Add(
                    new Paragraph(SanitizePdfText($"REMARKS: {order.Remarks.Trim()}"), bodyFont)
                );
            }

            document.Close();
            return stream.ToArray();
        }

        /// <summary>
        /// 明细表：没有退货行时与原发票完全一致（9 列）；有退货行时在商品行之后加「Adjustments / Returned goods」段，
        /// 退货行金额为负并带原因英文文案；退款单（净额 &lt; 0）用 7 列（Price / Qty / Credit）。
        /// </summary>
        private static void AddDetailTable(
            Document document,
            IReadOnlyList<StoreOrderCartItemDto> goodsItems,
            IReadOnlyList<StoreOrderCartItemDto> returnItems,
            bool isCreditNote,
            iTextSharp.text.Font headerFont,
            iTextSharp.text.Font bodyFont,
            iTextSharp.text.Font sectionFont
        )
        {
            var hasReturns = returnItems.Count > 0;
            var columnCount = isCreditNote ? 7 : 9;
            var detailTable = new PdfPTable(columnCount) { WidthPercentage = 100, SpacingAfter = 10 };
            detailTable.SetWidths(
                isCreditNote
                    ? new float[] { 0.45f, 1.05f, 1.45f, 3.4f, 0.9f, 0.8f, 1.1f }
                    : new float[] { 0.45f, 1.05f, 1.45f, 2.9f, 0.85f, 0.85f, 0.85f, 0.85f, 1.1f }
            );
            var headers = isCreditNote
                ? new[] { "#", "Item No.", "Barcode", "Name", "Price", "Qty", "Credit" }
                : new[]
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
                };
            foreach (var header in headers)
            {
                AddHeaderCell(detailTable, header, headerFont);
            }

            // 原因文案是英文，用英文字体（同 section 行字体）。
            var reasonFont = new iTextSharp.text.Font(
                sectionFont.BaseFont,
                7,
                iTextSharp.text.Font.NORMAL,
                new BaseColor(120, 120, 120)
            );
            var itemIndex = 1;

            void AddItemRow(StoreOrderCartItemDto item, bool isReturnRow)
            {
                var allocQuantity = item.AllocQuantity ?? 0m;
                var subtotal = decimal.Round(item.AllocatedImportAmount, 2);
                AddValueCell(detailTable, itemIndex.ToString(), bodyFont);
                AddValueCell(detailTable, item.ItemNumber ?? string.Empty, bodyFont);
                AddValueCell(detailTable, item.Barcode ?? item.ProductCode, bodyFont);
                if (isReturnRow)
                {
                    // 退货行：商品名下面再起一行 Reason: 英文原因文案。
                    var nameCell = new Phrase(
                        SanitizePdfText(item.ProductName ?? string.Empty),
                        bodyFont
                    );
                    var reason = StoreOrderReturnRules.ToDocumentReason(item.ReturnReason);
                    if (!string.IsNullOrEmpty(reason))
                    {
                        nameCell.Add(new Chunk($"\nReason: {reason}", reasonFont));
                    }
                    detailTable.AddCell(
                        new PdfPCell(nameCell)
                        {
                            Padding = 4,
                            BorderColor = new BaseColor(220, 220, 220),
                        }
                    );
                }
                else
                {
                    AddValueCell(detailTable, item.ProductName ?? string.Empty, bodyFont);
                }

                AddValueCell(detailTable, FormatCurrency(item.ImportPrice), bodyFont);
                if (!isCreditNote)
                {
                    AddValueCell(detailTable, FormatOptionalCurrency(item.RRP), bodyFont);
                    AddValueCell(detailTable, item.Quantity.ToString("0.##"), bodyFont);
                }
                AddValueCell(detailTable, allocQuantity.ToString("0.##"), bodyFont);
                AddValueCell(detailTable, FormatCurrency(subtotal), bodyFont);
                itemIndex += 1;
            }

            void AddSectionRow(string title, bool isReturnSection)
            {
                detailTable.AddCell(
                    new PdfPCell(new Phrase(title, sectionFont))
                    {
                        Colspan = columnCount,
                        Padding = 4,
                        BackgroundColor = isReturnSection
                            ? new BaseColor(253, 236, 236)
                            : new BaseColor(242, 242, 242),
                        BorderColor = new BaseColor(220, 220, 220),
                    }
                );
            }

            if (hasReturns && goodsItems.Count > 0)
            {
                AddSectionRow("Goods supplied", isReturnSection: false);
            }

            foreach (var item in goodsItems)
            {
                AddItemRow(item, isReturnRow: false);
            }

            if (hasReturns)
            {
                AddSectionRow(ReturnedGoodsSectionTitle(isCreditNote), isReturnSection: true);
                foreach (var item in returnItems)
                {
                    AddItemRow(item, isReturnRow: true);
                }
            }

            document.Add(detailTable);
        }

        /// <summary>退款单顶部醒目标识：红色 ADJUSTMENT NOTE + CREDIT，避免被当成普通发票付款。</summary>
        private static void AddCreditNoteBanner(Document document, iTextSharp.text.Font titleFont)
        {
            var red = new BaseColor(200, 16, 46);
            var redTitleFont = new iTextSharp.text.Font(titleFont.BaseFont, 16, iTextSharp.text.Font.BOLD, red);
            var stampFont = new iTextSharp.text.Font(titleFont.BaseFont, 11, iTextSharp.text.Font.BOLD, red);
            var banner = new PdfPTable(2) { WidthPercentage = 100, SpacingAfter = 6 };
            banner.SetWidths(new float[] { 3f, 1f });
            banner.AddCell(
                new PdfPCell(new Phrase("ADJUSTMENT NOTE", redTitleFont))
                {
                    Border = Rectangle.NO_BORDER,
                    Padding = 4,
                }
            );
            banner.AddCell(
                new PdfPCell(new Phrase("CREDIT", stampFont))
                {
                    Border = Rectangle.BOX,
                    BorderColor = red,
                    BorderWidth = 1.5f,
                    HorizontalAlignment = Element.ALIGN_CENTER,
                    Padding = 4,
                }
            );
            document.Add(banner);
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
            iTextSharp.text.Font font,
            bool isCreditNote
        )
        {
            var summaryTable = new PdfPTable(2) { WidthPercentage = 100, SpacingAfter = 8 };
            summaryTable.SetWidths(new float[] { 1f, 1f });
            var documentNumberLabel = isCreditNote ? "ADJUSTMENT NOTE NO." : "INVOICE NO.";
            var dateLabel = isCreditNote ? "DATE:" : "INVOICE DATE:";
            summaryTable.AddCell(
                new PdfPCell(
                    new Phrase(
                        SanitizePdfText($"{documentNumberLabel} {order.OrderNo ?? order.OrderGUID}"),
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
                new PdfPCell(new Phrase($"{dateLabel} {FormatInvoiceDate(order)}", font))
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
            iTextSharp.text.Font bodyFont,
            bool isCreditNote
        )
        {
            var footerTable = new PdfPTable(2) { WidthPercentage = 100, SpacingAfter = 8 };
            footerTable.SetWidths(new float[] { 1.55f, 1f });

            var paymentCell = CreateNoBorderCell();
            if (isCreditNote)
            {
                // 退款单不是付款通知：不带收款账户与「30 天内付款」条款，只说明这是对已收 GST 的冲减。
                paymentCell.AddElement(
                    new Paragraph(CreditNoteFooterText, bodyFont) { Leading = 11 }
                );
            }
            else
            {
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
            }
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
            AddNoBorderLabelCell(
                totalTable,
                isCreditNote ? "Total Credit:" : "Total Before Discount:",
                boldFont
            );
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

        /// <summary>单据类型按净额判定：全站共用 StoreOrderReturnRules.ResolveDocumentType。</summary>
        private static string ResolveDocumentType(StoreOrderCartDto order)
        {
            return StoreOrderReturnRules.ResolveDocumentType(order.TotalAllocatedImportAmount);
        }

        private static bool IsCreditNote(StoreOrderCartDto order)
        {
            return ResolveDocumentType(order) == StoreOrderReturnRules.DocumentTypeCreditNote;
        }

        private static string ReturnedGoodsSectionTitle(bool isCreditNote)
        {
            return isCreditNote ? "Returned goods" : "Adjustments / Returned goods";
        }

        /// <summary>
        /// 把明细拆成「发货商品」与「退货行」（发货数量为负）。退款单里没有发货的行（发货数量 0）没有意义，不输出。
        /// </summary>
        private static (List<StoreOrderCartItemDto> Goods, List<StoreOrderCartItemDto> Returns) SplitItems(
            StoreOrderCartDto order,
            bool isCreditNote
        )
        {
            var sorted = SortItems(order.Items);
            var returns = sorted
                .Where(item => StoreOrderReturnRules.IsReturnLine(item.AllocQuantity))
                .ToList();
            var goods = sorted
                .Where(item => !StoreOrderReturnRules.IsReturnLine(item.AllocQuantity))
                .Where(item => !isCreditNote || (item.AllocQuantity ?? 0m) != 0m)
                .ToList();
            return (goods, returns);
        }

        private static string BuildBaseFileName(StoreOrderCartDto order)
        {
            var storePart = SanitizeFileNamePart(order.StoreCode ?? "UnknownStore");
            var orderPart = SanitizeFileNamePart(order.OrderNo ?? order.OrderGUID);
            var datePart = SanitizeFileNamePart(FormatInvoiceFileDate(order));
            // 附件名与单据类型同步切换，避免退款单被当成发票归档。
            var prefix = IsCreditNote(order) ? "CreditNote" : "Invoice";
            return $"{prefix}_{storePart}_{orderPart}_{datePart}";
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
            // 负数写成 -$7.50（符号在 $ 前，退货 / 退款单上一眼可见）；四舍五入后为 0 的不显示负零。
            var rounded = decimal.Round(value, 2);
            return rounded < 0 ? $"-${Math.Abs(rounded):0.00}" : $"${rounded:0.00}";
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

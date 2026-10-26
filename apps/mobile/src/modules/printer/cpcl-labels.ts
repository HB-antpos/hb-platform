import type {
  CashRegisterUserBarcodeLabelPrintPayload,
  EmployeeCashierBarcodeLabelPrintPayload,
  ProductLabelPrintPayload,
  WarehouseLocationLabelPrintPayload,
  WarehouseProductLabelPrintPayload,
} from "@/modules/printer/types";

const CRLF = "\r\n";
const STANDARD_WIDTH = 570;
const STANDARD_HEIGHT = 400;
const SMALL_WIDTH = 472;
const SMALL_HEIGHT = 320;
// 商品标签 EAN13 条码的横坐标：给左侧外面的首位数字留位置（原 5 会让首位被裁掉）；与 Swift / Kotlin 保持一致。
const EAN13_BARCODE_X = 30;
const WAREHOUSE_HEIGHT = 208;

function command(lines: string[]) {
  return lines.join(CRLF) + CRLF;
}

function cpclText(value: unknown, maxLength = 80) {
  const text = typeof value === "string" ? value : value == null ? "" : String(value);
  return text.replace(/[\r\n]+/g, " ").replace(/\s+/g, " ").trim().slice(0, maxLength);
}

function text(font: number, x: number, y: number, value: unknown, maxLength?: number) {
  return `TEXT ${font} 0 ${x} ${y} ${cpclText(value, maxLength) || " "}`;
}

function line(x1: number, y1: number, x2: number, y2: number, width = 2) {
  return `LINE ${x1} ${y1} ${x2} ${y2} ${width}`;
}

function barcode(kind: "EAN13" | "128", x: number, y: number, value: unknown, height = 44, ratio = 2) {
  const safeValue = cpclText(value, 64);
  return safeValue ? `BARCODE ${kind} 1 ${ratio} ${height} ${x} ${y} ${safeValue}` : null;
}

function formatMoney(value: number | null | undefined) {
  const cents = Math.round(((Number.isFinite(value) ? Number(value) : 0) + 1e-8) * 100);
  return (cents / 100).toFixed(2);
}

function formatPriceParts(value: number | null | undefined) {
  const [integer, decimal] = formatMoney(value).split(".");
  return { integer, decimal };
}

function formatOptionalMoney(value: number | null | undefined) {
  return Number.isFinite(value) ? formatMoney(value) : "--";
}

function formatOptionalQuantity(value: number | null | undefined) {
  if (!Number.isFinite(value)) {
    return "--";
  }
  const rounded = Math.round(Number(value));
  return Math.abs(Number(value) - rounded) < 0.01 ? String(rounded) : Number(value).toFixed(2);
}

function formatDate(now = new Date()) {
  const year = now.getFullYear();
  const month = String(now.getMonth() + 1).padStart(2, "0");
  const day = String(now.getDate()).padStart(2, "0");
  return `${year}/${month}/${day}`;
}

function asNumber(value: number | null | undefined, fallback = 0) {
  return Number.isFinite(value) ? Number(value) : fallback;
}

function discountPercent(value: number | null | undefined) {
  return Math.round(asNumber(value) * 100);
}

function discountLabel(value: number | null | undefined) {
  return `${String(discountPercent(value)).padStart(2, "0")}%OFF`;
}

function discountedPrice(payload: ProductLabelPrintPayload) {
  return asNumber(payload.retailPrice) * (1 - asNumber(payload.discountRate));
}

function isSmallLabel(printType?: string | null) {
  return printType?.trim().toLowerCase() === "small";
}

function isValidEan13(value: string) {
  const barcodeValue = cpclText(value);
  if (!/^\d{13}$/.test(barcodeValue)) {
    return false;
  }
  const checkDigit = barcodeValue
    .slice(0, 12)
    .split("")
    .reduce((sum, char, index) => sum + Number(char) * (index % 2 === 0 ? 1 : 3), 0);
  return (10 - (checkDigit % 10)) % 10 === Number(barcodeValue[12]);
}

function barcodeKind(value: string) {
  return isValidEan13(value) ? "EAN13" : "128";
}

function estimateTextWidth(value: unknown, font: number) {
  const unit = font === 7 ? 28 : 12;
  const text = cpclText(value) || " ";
  const width = Array.from(text).reduce((total, char) => {
    const code = char.codePointAt(0) ?? 0;
    return total + (code > 127 ? unit * 2 : unit);
  }, 0);
  return Math.max(1, width);
}

function truncateTextByWidth(value: unknown, maxWidth: number, font: number) {
  let result = "";
  Array.from(cpclText(value)).some((char) => {
    const next = result + char;
    if (estimateTextWidth(next, font) > maxWidth) {
      return true;
    }
    result = next;
    return false;
  });
  return result || " ";
}

function wrapTextByWidth(value: unknown, maxWidth: number, font: number, maxLines: number) {
  const chars = Array.from(cpclText(value));
  if (!chars.length) {
    return [" "];
  }

  const lines: string[] = [];
  let current = "";
  chars.forEach((char) => {
    const next = current + char;
    if (current && estimateTextWidth(next, font) > maxWidth && lines.length < maxLines - 1) {
      lines.push(current);
      current = char;
      return;
    }
    current = next;
  });

  lines.push(current || " ");
  // CPCL TEXT 不会裁剪宽度，最后一行也要按 Android 位图画布宽度截断。
  return lines.slice(0, maxLines).map((lineText) => truncateTextByWidth(lineText, maxWidth, font));
}

function formatSupplierAbbreviation(value: unknown) {
  const words = cpclText(value)
    .toLocaleLowerCase("en-US")
    .split(/\s+/)
    .filter(Boolean)
    .map((word) => word.charAt(0).toLocaleUpperCase("en-US") + word.slice(1));

  if (!words.length) {
    return "";
  }
  if (words.length === 1) {
    return words[0].slice(0, 3).toLocaleUpperCase("en-US");
  }
  return words
    .slice(0, 4)
    .map((word) => word.charAt(0).toLocaleUpperCase("en-US"))
    .join(".");
}

function formatGrade(value: unknown) {
  return cpclText(value).toLocaleUpperCase("en-US").charAt(0);
}

function addBarcode(
  lines: string[],
  kind: "EAN13" | "128",
  x: number,
  y: number,
  value: unknown,
  height?: number,
  ratio?: number
) {
  const nextBarcode = barcode(kind, x, y, value, height, ratio);
  if (nextBarcode) {
    lines.push(nextBarcode);
  }
}

export function buildProductLabelCommand(payload: ProductLabelPrintPayload, printType?: string | null) {
  const small = isSmallLabel(printType);
  const width = small ? SMALL_WIDTH : STANDARD_WIDTH;
  const height = small ? SMALL_HEIGHT : STANDARD_HEIGHT;
  const barcodeValue = cpclText(payload.barcode);
  const price = formatPriceParts(payload.retailPrice);
  const priceTopY = 30;
  const priceDecimalWidth = estimateTextWidth(price.decimal, 4);
  const priceDotWidth = estimateTextWidth(".", 4);
  const priceIntegerWidth = estimateTextWidth(price.integer, 7);
  const priceCurrencyWidth = estimateTextWidth("$", 4);
  const priceDecimalX = width - priceDecimalWidth;
  const priceDotX = priceDecimalX - priceDotWidth;
  const priceIntegerX = priceDotX - priceIntegerWidth;
  const priceCurrencyX = priceIntegerX - priceCurrencyWidth;
  const itemNumber = cpclText(payload.itemNumber);
  const itemNumberX = 5;
  const itemNumberY = 120;
  const supplierName = formatSupplierAbbreviation(payload.supplierName);
  const dateValue = formatDate();
  const dateWidth = estimateTextWidth(dateValue, 4);
  const dateX = width - dateWidth;
  const grade = formatGrade(payload.grade);
  const nameMaxWidth = Math.max(1, priceCurrencyX - 10);
  const productNameLines = wrapTextByWidth(payload.productName, nameMaxWidth, 4, 2);
  const lines = [
    `! 0 200 200 ${height} 1`,
    `PAGE-WIDTH ${width}`,
  ];

  // iOS 使用 CPCL 文字近似 Android 位图模板，坐标保持和 Android 普通标签一致。
  productNameLines.forEach((lineText, index) => {
    lines.push(text(4, 5, 5 + index * 32, lineText));
  });
  lines.push(text(4, itemNumberX, itemNumberY, itemNumber));
  lines.push(text(4, itemNumberX + estimateTextWidth(itemNumber, 4) + 10, 118, supplierName));

  if (barcodeValue) {
    lines.push("BARCODE-TEXT 7 0 5");
    // EAN13 的首位数字印在条码左侧外面：x=5 会被挤出标签左边界裁掉，必须留出位置；Code128 无此问题。
    const kind = barcodeKind(barcodeValue);
    addBarcode(lines, kind, kind === "EAN13" ? EAN13_BARCODE_X : 5, 145, barcodeValue, 30);
  }

  if (asNumber(payload.discountRate) > 0) {
    const nextDiscountLabel = discountLabel(payload.discountRate);
    lines.push(text(4, width - estimateTextWidth(nextDiscountLabel, 4) - dateWidth - 20, 175, nextDiscountLabel));
  }
  if (grade) {
    lines.push(text(4, 300, 175, grade));
  }

  lines.push(text(4, priceCurrencyX, priceTopY, "$"));
  lines.push(text(7, priceIntegerX, priceTopY, price.integer));
  lines.push(text(4, priceDotX, priceTopY + 38, "."));
  lines.push(text(4, priceDecimalX, priceTopY, price.decimal));
  lines.push(text(4, dateX, 175, dateValue));
  lines.push("PRINT");
  return command(lines);
}

// 一维码版面：窄条 3 点 × 95 模块 = 285 点宽；左侧留 ≥11 模块（33 点）静区。
const EMPLOYEE_EAN13_UNIT = 3;
const EMPLOYEE_EAN13_X = 36;
const EMPLOYEE_EAN13_Y = 104;
const EMPLOYEE_EAN13_HEIGHT = 70;

function buildEmployeeCashierEan13LabelCommand(
  payload: EmployeeCashierBarcodeLabelPrintPayload,
  barcodeValue: string
) {
  // 一维码放在下方，姓名和用户名可以使用整行宽度，不必像左右布局那样收窄。
  const employeeName = truncateTextByWidth(cpclText(payload.employeeName) || "--", 530, 7);
  const rawUsername = cpclText(payload.username);
  const username = truncateTextByWidth(rawUsername ? `@${rawUsername}` : "--", 530, 4);
  return command([
    `! 0 200 200 ${STANDARD_HEIGHT} 1`,
    `PAGE-WIDTH ${STANDARD_WIDTH}`,
    text(7, 20, 16, employeeName),
    text(4, 20, 60, username),
    // 由打印机在条码下方自带可读数字（约 y=179~204，仍在价格标签单张安全高度内）。
    "BARCODE-TEXT 7 0 5",
    `BARCODE EAN13 ${EMPLOYEE_EAN13_UNIT} 2 ${EMPLOYEE_EAN13_HEIGHT} ${EMPLOYEE_EAN13_X} ${EMPLOYEE_EAN13_Y} ${barcodeValue}`,
    // BARCODE-TEXT 会跨标签保持，用完立即关闭，避免影响后续标签。
    "BARCODE-TEXT OFF",
    "PRINT",
  ]);
}

export function buildEmployeeCashierBarcodeLabelCommand(
  payload: EmployeeCashierBarcodeLabelPrintPayload
) {
  const employeeName = truncateTextByWidth(cpclText(payload.employeeName) || "--", 320, 7);
  const rawUsername = cpclText(payload.username);
  const username = truncateTextByWidth(rawUsername ? `@${rawUsername}` : "--", 320, 4);
  const barcodeValue = cpclText(payload.barcode);
  if (!isValidEan13(barcodeValue)) {
    throw new Error("Employee cashier barcode must be a valid EAN13 value.");
  }
  if (payload.symbology === "barcode") {
    return buildEmployeeCashierEan13LabelCommand(payload, barcodeValue);
  }

  const qrUnitWidth = 8;
  // 13 位纯数字在 M 纠错下使用 Version 1（21×21），U8 对应 168×168 点。
  const qrWidth = 21 * qrUnitWidth;
  const qrX = 374;
  // 实体价格标签的单张安全高度约 220 点；二维码和可读编号必须在此范围内完成。
  const qrY = 8;
  // CPCL 字体 4 的数字实际宽度约 16 点，不能沿用通用的 12 点近似值。
  const barcodeTextWidth = barcodeValue.length * 16;
  const barcodeTextX = Math.max(
    20,
    Math.min(
      qrX + Math.round((qrWidth - barcodeTextWidth) / 2),
      STANDARD_WIDTH - barcodeTextWidth - 20
    )
  );
  const lines = [
    `! 0 200 200 ${STANDARD_HEIGHT} 1`,
    `PAGE-WIDTH ${STANDARD_WIDTH}`,
    text(7, 20, 42, employeeName),
    text(4, 20, 128, username),
    `BARCODE QR ${qrX} ${qrY} M 2 U ${qrUnitWidth}`,
    `MA,${barcodeValue}`,
    "ENDQR",
    // CPCL 二维码不会自动打印可读文本，单独保留编号供人工核对和输入。
    text(4, barcodeTextX, 180, barcodeValue),
  ];
  lines.push("PRINT");
  return command(lines);
}

export function buildCashRegisterUserBarcodeLabelCommand(
  payload: CashRegisterUserBarcodeLabelPrintPayload
) {
  const barcodeValue = cpclText(payload.barcode, 50);
  if (!barcodeValue) {
    throw new Error("Cash register user barcode is required.");
  }
  const operatorName = truncateTextByWidth(cpclText(payload.operatorName) || "--", 530, 7);
  const storeName = truncateTextByWidth(cpclText(payload.storeName) || "--", 530, 4);
  // 老收银扫码枪读一维码：合法 EAN13 用 EAN13（窄条 2 点，约 24mm 宽），否则按 Web 规则退回 Code128。
  const kind = barcodeKind(barcodeValue);
  const narrowBarWidth = kind === "EAN13" ? 2 : 1;
  // 实体价格标签单张安全高度约 220 点：条码 96-176，可读编号放右侧，所有元素都不越过 180。
  const lines = [
    `! 0 200 200 ${STANDARD_HEIGHT} 1`,
    `PAGE-WIDTH ${STANDARD_WIDTH}`,
    text(7, 20, 8, operatorName),
    text(4, 20, 44, storeName),
    `BARCODE ${kind} ${narrowBarWidth} 2 80 20 96 ${barcodeValue}`,
    text(4, 330, 120, truncateTextByWidth(barcodeValue, 220, 4)),
    "PRINT",
  ];
  return command(lines);
}

export function buildDiscountLabelCommand(payload: ProductLabelPrintPayload, printType?: string | null) {
  const small = isSmallLabel(printType);
  const width = small ? SMALL_WIDTH : STANDARD_WIDTH;
  const height = small ? SMALL_HEIGHT : STANDARD_HEIGHT;
  const barcodeValue = cpclText(payload.barcode, Number.MAX_SAFE_INTEGER)
    || cpclText(payload.itemNumber, Number.MAX_SAFE_INTEGER);
  // Hermes 的旧版本没有 TextEncoder；按 Unicode 码点计算 UTF-8 长度。
  const barcodeBytes = Array.from(barcodeValue).reduce((total, char) => {
    const point = char.codePointAt(0) ?? 0;
    return total + (point <= 0x7f ? 1 : point <= 0x7ff ? 2 : point <= 0xffff ? 3 : 4);
  }, 0);
  if (barcodeBytes > 251) {
    throw new Error("Discount label QR content is too long for the left QR area.");
  }
  // M 纠错的 byte 模式按容量上界选倍率，二维码始终在 64×64 点内，内容不截断。
  const qrUnit = barcodeBytes <= 14 ? 3 : barcodeBytes <= 42 ? 2 : 1;
  const nowPrice = Math.round(discountedPrice(payload) * 100 + 1e-8) / 100;
  const hasWasPrice = Number.isFinite(payload.retailPrice)
    && Number.isFinite(nowPrice)
    && Number(payload.retailPrice) > nowPrice
    && nowPrice >= 0;
  const wasText = `$${formatMoney(payload.retailPrice)}`;
  const nowText = `$${formatMoney(nowPrice)}`;
  // CPCL 固定字体：font 0 为 8×9 点，font 7 为 12×24 点；非 ASCII 保守按双宽。
  const textWidth = (value: string, font: 0 | 7) => Array.from(value).reduce(
    (total, char) => total + ((char.codePointAt(0) ?? 0) > 127 ? 2 : 1) * (font === 0 ? 8 : 12),
    0
  );
  const fitDisplay = (value: string, maxWidth: number, font: 0 | 7) => {
    const chars = Array.from(value.trim());
    if (textWidth(chars.join(""), font) <= maxWidth) return chars.join("");
    while (chars.length && textWidth(chars.join("") + "...", font) > maxWidth) chars.pop();
    return chars.join("").trimEnd() + "...";
  };
  const infoX = 84;
  const infoWidth = small ? 100 : 124;
  const wasX = infoX + infoWidth + 10;
  const baseWasWidth = small ? 76 : 96;
  const wasFont: 0 | 7 = textWidth(wasText, 7) <= baseWasWidth ? 7 : 0;
  const wasWidth = Math.max(baseWasWidth, textWidth(wasText, wasFont));
  const nowX = hasWasPrice ? wasX + wasWidth + 10 : wasX;
  const nowRight = width - 12;
  const nowLabelWidth = textWidth("NOW", 7);
  const nowAmountMaxWidth = nowRight - nowX - nowLabelWidth - 18;
  const nowFont: 0 | 7 = textWidth(nowText, 7) <= nowAmountMaxWidth ? 7 : 0;
  const nowBaseWidth = textWidth(nowText, nowFont);
  if (nowBaseWidth > nowAmountMaxWidth) {
    throw new Error("Discount label price is too long for its price area.");
  }
  const nowScaleX = Math.min(2, Math.floor(nowAmountMaxWidth / nowBaseWidth));
  const nowScaleY = nowFont === 7 ? nowScaleX : 2;
  const nowPriceHeight = (nowFont === 7 ? 24 : 9) * nowScaleY;
  const nowHeight = Math.max(24, nowPriceHeight) + 12;
  const nowTop = 194 - nowHeight;
  const discountText = String(discountPercent(payload.discountRate)).padStart(2, "0");
  const discountWidth = textWidth(discountText, 7) * 4;
  const discountX = width - 12 - discountWidth - 8 - 48;
  const nameMaxWidth = Math.max(36, discountX - 15);
  const nameChars = Array.from(cpclText(payload.productName));
  let firstLineLength = 0;
  while (firstLineLength < nameChars.length
    && textWidth(nameChars.slice(0, firstLineLength + 1).join(""), 7) <= nameMaxWidth) firstLineLength++;
  // 第一行若切在单词中间，回退到词边界；第二行超长时明确显示省略号。
  if (firstLineLength < nameChars.length && nameChars[firstLineLength] !== " ") {
    const wordBoundary = nameChars.slice(0, firstLineLength).lastIndexOf(" ");
    if (wordBoundary > 0) firstLineLength = wordBoundary;
  }
  const nameLines = [nameChars.slice(0, firstLineLength).join("").trim() || " "];
  if (firstLineLength < nameChars.length) {
    nameLines.push(fitDisplay(nameChars.slice(firstLineLength).join(""), nameMaxWidth, 7));
  }
  const rawItem = cpclText(payload.itemNumber) || "--";
  const itemFont: 0 | 7 = textWidth(rawItem, 7) <= infoWidth ? 7 : 0;
  const item = fitDisplay(rawItem, infoWidth, itemFont);
  const lines = [`! 0 200 200 ${height} 1`, `PAGE-WIDTH ${width}`, "LEFT", "SETBOLD 0"];
  const addText = (font: 0 | 7, x: number, y: number, value: string, scaleX = 1, scaleY = 1) => {
    lines.push(`SETMAG ${scaleX} ${scaleY}`, text(font, x, y, value));
  };
  nameLines.forEach((value, index) => addText(7, 5, 5 + index * 30, value));
  addText(7, discountX, 20, discountText, 4, 4);
  addText(7, discountX + discountWidth + 8, 20, "%", 2, 2);
  addText(7, discountX + discountWidth + 20, 92, "OFF");
  addText(itemFont, infoX, 142, item, 1, itemFont === 0 ? 2 : 1);
  addText(0, infoX + 2, 174, formatDate(), 1, 2);
  lines.push(`INVERSE-LINE ${infoX} 172 ${infoX + 84} 172 22`);
  if (hasWasPrice) {
    const wasPriceHeight = wasFont === 7 ? 24 : 18;
    const wasPriceY = 194 - wasPriceHeight;
    addText(0, wasX, wasPriceY - 13, "WAS");
    addText(wasFont, wasX, wasPriceY, wasText, 1, wasFont === 0 ? 2 : 1);
    lines.push(line(wasX, wasPriceY + wasPriceHeight / 2, wasX + textWidth(wasText, wasFont) - 1, wasPriceY + wasPriceHeight / 2, 2));
  }
  addText(7, nowX + 6, nowTop + (nowHeight - 24) / 2, "NOW");
  addText(nowFont, nowRight - 6 - nowBaseWidth * nowScaleX, nowTop + (nowHeight - nowPriceHeight) / 2, nowText, nowScaleX, nowScaleY);
  // 在同一矩形内反显 NOW 和完整金额，右侧留 6 点空白。
  lines.push(`INVERSE-LINE ${nowX} ${nowTop} ${nowRight} ${nowTop} ${nowHeight}`, "SETMAG 0 0");
  if (barcodeValue) lines.push(`BARCODE QR 10 130 M 2 U ${qrUnit}`, `MA,${barcodeValue}`, "ENDQR");
  lines.push("PRINT");
  return command(lines);
}

export function buildClearanceLabelCommand(payload: ProductLabelPrintPayload) {
  const clearancePrice = Number.isFinite(payload.clearancePrice)
    ? Number(payload.clearancePrice)
    : discountedPrice(payload);
  const barcodeValue = cpclText(payload.clearanceBarcode) || cpclText(payload.barcode);
  const lines = [
    "! 0 200 200 205 1",
    "PAGE-WIDTH 614",
    text(4, 20, 20, payload.productName),
    text(7, 360, 48, `$${formatMoney(clearancePrice)}`),
    text(4, 20, 80, payload.itemNumber || "--"),
    text(7, 260, 145, "CLEARANCE"),
    text(4, 360, 145, formatDate()),
  ];
  addBarcode(lines, "128", 20, 110, barcodeValue, 44);
  lines.push("PRINT");
  return command(lines);
}

export function buildBigDiscountLabelCommand(payload: ProductLabelPrintPayload, printType?: string | null) {
  const discount = discountPercent(payload.discountRate);
  const afterDiscount = discountedPrice(payload);
  const saveAmount = asNumber(payload.retailPrice) - afterDiscount;
  const title = cpclText(printType) || (discount > 0 ? `${discount}% OFF` : "SPECIAL");
  const lines = [
    "! 0 200 200 1200 1",
    "PAGE-WIDTH 480",
    text(7, 120, 70, title),
    text(7, 120, 230, `$${formatMoney(afterDiscount)}`),
    text(4, 20, 360, `WAS $${formatMoney(payload.retailPrice)}`),
    line(20, 388, 160, 388),
    text(4, 20, 410, `SAVE $${formatMoney(saveAmount)}`),
    text(4, 20, 510, payload.productName),
  ];
  addBarcode(lines, "128", 20, 560, payload.barcode, 44);
  lines.push(text(4, 340, 650, formatDate()));
  lines.push("PRINT");
  return command(lines);
}

export function buildWarehouseProductLabelCommand(payload: WarehouseProductLabelPrintPayload) {
  const barcodeValue = cpclText(payload.barcode);
  const middlePackageQuantity = Number(payload.middlePackageQuantity);
  // 仓库商品标签只在实际中包数大于 1 时显示 INNER。
  const shouldPrintInner = Number.isFinite(middlePackageQuantity) && middlePackageQuantity > 1;
  const displayPrice = payload.retailPrice ?? payload.domesticPrice ?? payload.oemPrice ?? payload.importPrice;
  const costPrice = payload.purchasePrice ?? payload.importPrice ?? payload.domesticPrice ?? payload.oemPrice;
  const lines = [
    `! 0 200 200 ${WAREHOUSE_HEIGHT} 1`,
    `PAGE-WIDTH ${STANDARD_WIDTH}`,
    text(7, 20, 14, "WAREHOUSE PRODUCT"),
    text(4, 20, 46, payload.productName),
    text(4, 20, 66, `ITEM ${payload.itemNumber || "--"}`),
    text(4, 20, 86, `LOC ${payload.locationCode || "UNASSIGNED"}`),
  ];
  if (barcodeValue) {
    lines.push("BARCODE-TEXT 7 0 5");
  }
  addBarcode(lines, "128", 20, 132, barcodeValue, 38, 1);
  if (shouldPrintInner) {
    lines.push(text(4, 360, 124, `INNER ${formatOptionalQuantity(payload.middlePackageQuantity)}`));
  }
  lines.push(text(4, 360, 152, `COST ${formatOptionalMoney(costPrice)}`));
  lines.push(text(4, 360, 180, `RRP ${formatOptionalMoney(displayPrice)}`));
  lines.push("PRINT");
  return command(lines);
}

export function buildWarehouseLocationLabelCommand(payload: WarehouseLocationLabelPrintPayload) {
  const displayCode = cpclText(payload.locationCode) || cpclText(payload.locationBarcode) || cpclText(payload.locationGuid);
  const barcodeValue = cpclText(payload.locationBarcode) || displayCode;
  const printableCode = displayCode || "--";
  const maxCodeWidth = 540;
  const maxCodeHeight = 120;
  const font7BaseHeight = 24;
  const font7BaseWidth = Array.from(printableCode).reduce((width, char) => {
    const codePoint = char.codePointAt(0) ?? 0;
    return width + (codePoint > 127 ? 24 : 12);
  }, 0);

  // Font 7 的最小倍率为 1；物理上无法完整容纳时终止打印，禁止输出会被裁切的标签。
  if (font7BaseWidth > maxCodeWidth) {
    throw new Error("货位代码过长，无法在标签安全宽度内完整打印");
  }

  // Font 7 基础尺寸为 12×24 dots，标准代码使用 4×4 等比例放大到约 96 dots 高。
  const codeMultiplier = Math.max(
    1,
    Math.min(4, Math.floor(maxCodeWidth / font7BaseWidth), Math.floor(maxCodeHeight / font7BaseHeight))
  );
  const upperZoneHeight = Math.floor((WAREHOUSE_HEIGHT * 2) / 3);
  const codeY = Math.max(0, Math.floor((upperZoneHeight - font7BaseHeight * codeMultiplier) / 2));
  const lines = [
    `! 0 200 200 ${WAREHOUSE_HEIGHT} 1`,
    `PAGE-WIDTH ${STANDARD_WIDTH}`,
    "CENTER",
    // 货位代码在上方区域使用最大安全倍率；打印后必须复位，避免污染后续标签。
    "SETBOLD 2",
    `SETMAG ${codeMultiplier} ${codeMultiplier}`,
    text(7, 0, codeY, printableCode),
    "SETMAG 0 0",
    "SETBOLD 0",
    // BARCODE-TEXT 会跨标签保持，必须显式关闭，避免继承上一张商品标签的可读数字。
    "BARCODE-TEXT OFF",
  ];
  addBarcode(lines, "128", 0, 151, barcodeValue, 44, 1);
  lines.push("PRINT");
  return command(lines);
}

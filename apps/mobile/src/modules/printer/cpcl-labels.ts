import type {
  CashRegisterUserBarcodeLabelPrintPayload,
  EmployeeCashierBarcodeLabelPrintPayload,
  PackCartonLabelPrintPayload,
  PackPalletLabelPrintPayload,
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

// ---- 打包箱标签 / 托盘标签（70×26 mm 英文版，纯 CPCL 文本，走 print(rawCommand)，不依赖原生改动） ----

export interface PackLabelSpec {
  kind: "BOX" | "PLT";
  kindCaption: "CARTON" | "PALLET";
  number: string;
  fromLine: string;
  destinationName: string;
  statLine: string;
  /** 右下第一行：订货单号或运单号；可空。 */
  referenceLine: string;
  printedAt: string;
  operatorName: string;
  /** 托盘张数，如 1/2；箱标签为空。 */
  sheetText: string;
  /** 托盘标签加整圈粗黑框。 */
  framed: boolean;
}

// CPCL 固定字体：font 0 为 8×9 点，font 7 为 12×24 点，SETMAG 按倍数放大；非 ASCII 保守按双宽。
function packTextWidth(value: string, font: 0 | 7, scaleX = 1) {
  return Array.from(value).reduce(
    (total, char) => total + ((char.codePointAt(0) ?? 0) > 127 ? 2 : 1) * (font === 0 ? 8 : 12) * scaleX,
    0
  );
}

/** 超宽时直接截断（不换行），保证不压到二维码或标签边缘。 */
function packFit(value: string, maxWidth: number, font: 0 | 7, scaleX = 1) {
  const chars = Array.from(value);
  while (chars.length && packTextWidth(chars.join(""), font, scaleX) > maxWidth) chars.pop();
  return chars.join("").trimEnd();
}

function packUpper(value: unknown, maxLength = 80) {
  return cpclText(value, maxLength).toUpperCase();
}

function packCount(value: number) {
  return Number.isFinite(value) ? String(Math.max(0, Math.trunc(value))) : "0";
}

function buildPackLabelCommand(spec: PackLabelSpec, printType?: string | null) {
  const small = isSmallLabel(printType);
  const width = small ? SMALL_WIDTH : STANDARD_WIDTH;
  const height = WAREHOUSE_HEIGHT;
  const number = packUpper(spec.number, 40).replace(/\s+/g, "");
  if (!number) {
    throw new Error("Pack label number is required.");
  }
  const frame = spec.framed ? 6 : 0;
  const kindWidth = small ? 58 : 70;
  const mainX = kindWidth + 10;
  const rightEdge = width - frame - 8;

  // 二维码：编号 ≤14 字节是 Version 1（21 模块），更长按 Version 2 / 3 缩小单元，边长始终不超过约 108 点。
  const qrModules = number.length <= 14 ? 21 : number.length <= 26 ? 25 : 29;
  const qrUnit = Math.max(2, Math.floor((small ? 88 : 108) / qrModules));
  const qrX = rightEdge - qrModules * qrUnit;
  const qrY = frame + 6;
  const textMaxWidth = qrX - 8 - mainX;

  const lines = [
    `! 0 200 200 ${height} 1`,
    `PAGE-WIDTH ${width}`,
    "LEFT",
    "SETBOLD 0",
    // BARCODE-TEXT 会跨标签保持：显式关闭，避免继承上一张商品标签的可读数字。
    "BARCODE-TEXT OFF",
  ];
  const addText = (font: 0 | 7, x: number, y: number, value: string, scaleX = 1, scaleY = 1) => {
    if (value) lines.push(`SETMAG ${scaleX} ${scaleY}`, text(font, Math.round(x), Math.round(y), value));
  };

  // 左侧类型条：先印黑字，最后整条反白成黑底白字。
  const kindCenter = (frame + kindWidth) / 2;
  addText(7, kindCenter - packTextWidth(spec.kind, 7) / 2, 64, spec.kind, 1, 2);
  addText(0, kindCenter - packTextWidth(spec.kindCaption, 0) / 2, 120, spec.kindCaption, 1, 2);

  // 第一行：发出地（托盘带承运商 / DIRECT）。
  addText(0, mainX, frame + 8, packFit(spec.fromLine, textMaxWidth, 0), 1, 2);

  // 第二行：TO + 去向大字；放不下先缩一号，再截断。
  const toY = 34;
  addText(7, mainX, toY + 20, "TO");
  const destinationX = mainX + 32;
  const destinationMaxWidth = textMaxWidth - 32;
  const destinationScaleX = packTextWidth(spec.destinationName, 7, 2) <= destinationMaxWidth ? 2 : 1;
  addText(
    7,
    destinationX,
    toY,
    packFit(spec.destinationName, destinationMaxWidth, 7, destinationScaleX),
    destinationScaleX,
    2
  );

  // 第三行：编号前 8 位小字 + 流水尾号大字 + 数量。
  const codeY = 86;
  const prefix = number.length > 8 ? number.slice(0, 8) : "";
  const tail = number.length > 8 ? number.slice(8) : number;
  const tailX = mainX + (prefix ? packTextWidth(prefix, 7) + 8 : 0);
  const tailScale = packTextWidth(tail, 7, 2) <= textMaxWidth - (tailX - mainX) ? 2 : 1;
  addText(7, mainX, codeY + 22, prefix);
  const fittedTail = packFit(tail, textMaxWidth - (tailX - mainX), 7, tailScale);
  addText(7, tailX, tailScale === 2 ? codeY : codeY + 22, fittedTail, tailScale, tailScale);
  const statX = tailX + packTextWidth(fittedTail, 7, tailScale) + 10;
  addText(0, statX, codeY + 26, packFit(spec.statLine, qrX - 8 - statX, 0), 1, 2);

  // 底部：Code128 给扫码枪；右下两行（订货单号 / 运单号，时间与操作人）右对齐。
  const barcodeY = 144;
  const barcodeHeight = 46;
  // Code128-B 估宽：(字符数 + 起始 + 校验) × 11 + 终止 13 模块。
  const barcodeModules = (number.length + 2) * 11 + 13;
  const sheetWidth = spec.sheetText ? packTextWidth(spec.sheetText, 7) + 8 : 0;
  const metaWidthFor = (unit: number) => rightEdge - (mainX + barcodeModules * unit + 10);
  // 右侧至少放得下时间（加托盘张数）才用 2 点窄条，否则退到 1 点，文字优先不被条码压住。
  const barcodeUnit = metaWidthFor(2) - sheetWidth >= packTextWidth("00 XXX 00:00", 0) ? 2 : 1;
  const metaWidth = metaWidthFor(barcodeUnit);
  lines.push(`BARCODE 128 ${barcodeUnit} 1 ${barcodeHeight} ${mainX} ${barcodeY} ${number}`);
  const referenceLine = packFit(spec.referenceLine, metaWidth, 0);
  addText(0, rightEdge - packTextWidth(referenceLine, 0), barcodeY + 4, referenceLine, 1, 2);
  // 小标签的托盘右下只打时间，不打操作人（宽度不够）。
  const withOperator = Boolean(spec.operatorName) && !(small && spec.framed);
  const timeLine = packFit(
    withOperator ? `${spec.printedAt} ${spec.operatorName}` : spec.printedAt,
    metaWidth - sheetWidth,
    0
  );
  addText(0, rightEdge - sheetWidth - packTextWidth(timeLine, 0), barcodeY + 28, timeLine, 1, 2);
  addText(7, rightEdge - packTextWidth(spec.sheetText, 7), barcodeY + 23, spec.sheetText);

  lines.push(`BARCODE QR ${qrX} ${qrY} M 2 U ${qrUnit}`, `MA,${number}`, "ENDQR");
  if (spec.framed) {
    // 托盘标签整圈粗黑框；类型条只反白框内部分，和黑框连成一片。
    lines.push(`BOX 0 0 ${width - 1} ${height - 1} ${frame}`);
  }
  lines.push(
    `INVERSE-LINE ${frame} ${frame} ${kindWidth} ${frame} ${height - frame * 2}`,
    "SETMAG 0 0",
    "PRINT"
  );
  return command(lines);
}

// 打包标签上的去向类型，与后端 PackDestinationTypes 一致：0 未指定、1 本地存放、2 其他分店、3 总仓。
const PACK_DESTINATION_LOCAL = 1;
const PACK_DESTINATION_SHIPPABLE = [2, 3];

/** TO 一行：要发出的取收货方名称；本地存放打 STORAGE；未指定（或缺名称）打 UNASSIGNED。 */
function packToText(destinationType: number, destinationName: string | null | undefined) {
  if (destinationType === PACK_DESTINATION_LOCAL) return "STORAGE";
  if (PACK_DESTINATION_SHIPPABLE.includes(destinationType)) return packUpper(destinationName) || "UNASSIGNED";
  return "UNASSIGNED";
}

/**
 * FROM 一行：FROM <发出地>；有运输公司时加 " - VIA <公司>"；要发出（其他分店 / 总仓）且没有运输公司时加 " - DIRECT"；
 * 本地存放和未指定不加。
 */
function packFromText(
  destinationType: number,
  originName: string | null | undefined,
  carrierName: string | null | undefined
) {
  const origin = packUpper(originName) || "--";
  const carrier = packUpper(carrierName);
  const shippable = PACK_DESTINATION_SHIPPABLE.includes(destinationType);
  if (!shippable) return `FROM ${origin}`;
  return carrier ? `FROM ${origin} - VIA ${carrier}` : `FROM ${origin} - DIRECT`;
}

/** 箱标签的版面内容（打印与弹层预览共用，保证所见即所得）。 */
export function buildPackCartonLabelSpec(payload: PackCartonLabelPrintPayload): PackLabelSpec {
  const orderNo = packUpper(payload.orderNo, 30);
  return {
    kind: "BOX",
    kindCaption: "CARTON",
    number: payload.cartonNo,
    fromLine: packFromText(payload.destinationType, payload.originName, payload.carrierName),
    destinationName: packToText(payload.destinationType, payload.destinationName),
    statLine: `${packCount(payload.skuCount)} SKU - ${packCount(payload.pieceCount)} PCS`,
    referenceLine: orderNo ? `ORDER ${orderNo}` : "",
    printedAt: packUpper(payload.printedAt, 20),
    operatorName: packUpper(payload.operatorName, 20),
    sheetText: "",
    framed: false,
  };
}

/** 托盘标签的版面内容：右下优先运单号（CN），没有再打订货单号；带 1/2 张数。 */
export function buildPackPalletLabelSpec(payload: PackPalletLabelPrintPayload): PackLabelSpec {
  const consignmentNo = packUpper(payload.consignmentNo, 40);
  const orderNo = packUpper(payload.orderNo, 30);
  const sheetTotal = Math.trunc(Number(payload.sheetTotal));
  const sheetIndex = Math.trunc(Number(payload.sheetIndex));
  return {
    kind: "PLT",
    kindCaption: "PALLET",
    number: payload.palletNo,
    fromLine: packFromText(payload.destinationType, payload.originName, payload.carrierName),
    destinationName: packToText(payload.destinationType, payload.destinationName),
    statLine: `${packCount(payload.cartonCount)} CTN - ${packCount(payload.pieceCount)} PCS`,
    referenceLine: consignmentNo ? `CN ${consignmentNo}` : orderNo ? `ORDER ${orderNo}` : "",
    printedAt: packUpper(payload.printedAt, 20),
    operatorName: packUpper(payload.operatorName, 20),
    sheetText: sheetTotal >= 1 && sheetIndex >= 1 && sheetIndex <= sheetTotal ? `${sheetIndex}/${sheetTotal}` : "",
    framed: true,
  };
}

/** 箱标签：左侧 BOX 黑条，FROM / TO / 编号 + N SKU - M PCS，右上二维码、底部 Code128（内容都是完整箱号）。 */
export function buildPackCartonLabelCommand(payload: PackCartonLabelPrintPayload, printType?: string | null) {
  return buildPackLabelCommand(buildPackCartonLabelSpec(payload), printType);
}

/** 托盘标签：PLT 黑条加整圈粗黑框；FROM 行按去向带 VIA / DIRECT，右下运单号（CN）；带 1/2 张数。 */
export function buildPackPalletLabelCommand(payload: PackPalletLabelPrintPayload, printType?: string | null) {
  return buildPackLabelCommand(buildPackPalletLabelSpec(payload), printType);
}

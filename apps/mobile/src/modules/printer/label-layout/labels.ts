/**
 * 6 种标签的 JS 排版：坐标、字号、文案逐条移植自 Android HbPrinterModule.kt 的 buildXxxLabelCommand（现有纸面以安卓为准）。
 * 以后改样式只改这里，发 OTA 即可；原生层只负责把文字画成位图。
 */
import type {
  ProductLabelPrintPayload,
  WarehouseLocationLabelPrintPayload,
  WarehouseProductLabelPrintPayload,
} from "@/modules/printer/types";
import {
  cpclText,
  formatMoney,
  formatOptionalMoney,
  formatOptionalQuantity,
  formatPriceParts,
  formatSupplierAbbreviation,
  isValidEan13,
  payloadNumber,
  payloadString,
  roundHalfEven,
  roundToInt,
  todayString,
} from "./format";
import { bitmapItem, commandItem, type LabelDoc, type LabelDocItem } from "./label-doc";
import { MonoBitmap } from "./mono-bitmap";
import {
  androidFont,
  descending,
  estimateIndex,
  fittedText,
  fittedTexts,
  longTextBitmap,
  pickLargestFitting,
  pt,
  textBitmap,
  textBitmaps,
  wrapChars,
  drawLines,
  type LabelTextRenderer,
  type TextSpec,
} from "./text";

const LABEL_WIDTH = 570;
const LABEL_HEIGHT = 400;
const SMALL_WIDTH = 472;
const SMALL_HEIGHT = 320;
const WAREHOUSE_HEIGHT = 208;

export type LabelBuildContext = {
  renderer: LabelTextRenderer;
  /** 打印日期；测试传固定值。 */
  now: Date;
};

function isSmall(printType?: string | null) {
  return (printType ?? "").trim().toLowerCase() === "small";
}

const black = (text: string, size: number, extra: Partial<TextSpec> = {}): TextSpec => ({
  text,
  size,
  bold: true,
  family: "sans-serif-black",
  ...extra,
});

// ---------- 打印机自带图形 ----------

function utf8Length(value: string) {
  let length = 0;
  for (const char of value) {
    const code = char.codePointAt(0) ?? 0;
    length += code < 0x80 ? 1 : code < 0x800 ? 2 : code < 0x10000 ? 3 : 4;
  }
  return length;
}

/** 估算二维码版本（纠错 L、自动模式）对应的模块数，用来定单元点数与占位，目标边长约 64 点（对应旧 ZXing 64×64）。 */
export function qrModules(value: string) {
  const capacities = /^\d+$/.test(value)
    ? [41, 77, 127, 187, 255, 322]
    : /^[0-9A-Z $%*+\-./:]+$/.test(value)
      ? [25, 47, 77, 114, 154, 195]
      : [17, 32, 53, 78, 106, 134];
  const length = /^\d+$/.test(value) || /^[0-9A-Z $%*+\-./:]+$/.test(value) ? value.length : utf8Length(value);
  const version = capacities.findIndex((capacity) => length <= capacity) + 1 || capacities.length + 1;
  return 17 + 4 * version;
}

/** 旧原生用 ZXing 出 64×64 的二维码位图（内容居中），排版一律按 64 点占位。 */
const QR_BOX = 64;

/**
 * 打印机自带二维码（CPCL BARCODE QR，纠错 L），放在 (x, y) 起的 64×64 占位框里居中，
 * 与旧 ZXing 位图（模块整数倍放大后居中）的落点一致。
 */
export function qrCode(x: number, y: number, value: string) {
  const modules = qrModules(value);
  const unit = Math.max(1, Math.floor(QR_BOX / modules));
  const offset = Math.max(0, Math.trunc((QR_BOX - modules * unit) / 2));
  return commandItem(`BARCODE QR ${x + offset} ${y + offset} M 2 U ${unit}`, `LA,${value}`, "ENDQR");
}

/** Kotlin createDashLineBitmap：每 18 点画 10 点长、高 height 的实线段。 */
function dashLine(width: number, height: number) {
  const bitmap = new MonoBitmap(Math.max(1, width), Math.max(1, height));
  for (let x = 0; x < width; x += 18) bitmap.fillRect(x, 0, Math.min(10, width - x), height, true);
  return bitmap;
}

type PriceSizes = { currency: number; integer: number; dot: number; decimal: number };

/** 价格四件套（$ / 整数 / . / 小数）；多组字号一次批量画完，每组返回各部分位图与总宽。 */
async function pricePartsBatch(
  renderer: LabelTextRenderer,
  price: { integer: string; decimal: string },
  sizeList: readonly PriceSizes[],
  currencyBold: boolean
) {
  const bitmaps = await textBitmaps(
    renderer,
    sizeList.flatMap((sizes) => [
      black("$", sizes.currency, { bold: currencyBold }),
      black(price.integer, sizes.integer),
      black(".", sizes.dot),
      black(price.decimal, sizes.decimal),
    ])
  );
  return sizeList.map((_, index) => {
    const [currency, integer, dot, decimal] = bitmaps.slice(index * 4, index * 4 + 4);
    return { currency, integer, dot, decimal, width: currency.width + integer.width + dot.width + decimal.width };
  });
}

async function priceParts(
  renderer: LabelTextRenderer,
  price: { integer: string; decimal: string },
  sizes: PriceSizes,
  currencyBold: boolean
) {
  return (await pricePartsBatch(renderer, price, [sizes], currencyBold))[0];
}

// ---------- 普通商品标签 ----------

export async function buildProductLabelDoc(
  payload: ProductLabelPrintPayload,
  printType: string | null | undefined,
  { renderer, now }: LabelBuildContext
): Promise<LabelDoc> {
  const small = isSmall(printType);
  const w = small ? SMALL_WIDTH : LABEL_WIDTH;
  const h = small ? SMALL_HEIGHT : LABEL_HEIGHT;
  const productName = payloadString(payload.productName);
  const itemNumber = payloadString(payload.itemNumber);
  const supplierName = formatSupplierAbbreviation(payloadString(payload.supplierName));
  const barcode = payloadString(payload.barcode);
  const discountRate = payloadNumber(payload.discountRate) ?? 0;
  const grade = payloadString(payload.grade).toUpperCase().charAt(0);
  const price = formatPriceParts(payloadNumber(payload.retailPrice));

  const specs: TextSpec[] = [
    black(price.integer, 40),
    black(".", 20),
    black(price.decimal, 20),
    black("$", 20, { bold: false }),
    black(itemNumber, 8),
    { text: supplierName, size: 8, bold: true, family: "sans-serif-light", inverse: true, padding: 2 },
    black(todayString(now), 8, { inverse: true, padding: 2 }),
  ];
  if (discountRate > 0) {
    specs.push(black(`${String(roundToInt(discountRate * 100)).padStart(2, "0")}%OFF`, 8, { inverse: true, padding: 2 }));
  }
  if (grade) specs.push(black(grade, 8, { inverse: true, padding: 4 }));
  const [integer, dot, decimal, currency, item, supplier, date, ...rest] = await textBitmaps(renderer, specs);
  const discount = discountRate > 0 ? rest.shift() : undefined;
  const gradeBitmap = grade ? rest.shift() : undefined;

  const nameMaxWidth = Math.max(1, w - decimal.width - dot.width - integer.width - currency.width);
  const name = await longTextBitmap(renderer, cpclText(productName), 10, false, "Arial", 2, nameMaxWidth);

  const items: LabelDocItem[] = [bitmapItem(5, 5, name), bitmapItem(5, 120, item), bitmapItem(5 + item.width + 10, 118, supplier)];
  if (barcode) {
    const kind = isValidEan13(barcode) ? "EAN13" : "128";
    // EAN13 的首位数字印在条码竖线左侧外面约 8 点：x=5 会被裁掉，取 14 让首位数字与货号（x=5）左对齐（与原生 / TS 一致）。
    const barcodeX = kind === "EAN13" ? 14 : 5;
    items.push(commandItem("BARCODE-TEXT 7 0 5", `BARCODE ${kind} 1 2 30 ${barcodeX} 145 ${barcode}`));
  }
  if (discount) items.push(bitmapItem(w - discount.width - date.width - 20, 175, discount));
  if (gradeBitmap) items.push(bitmapItem(300, 175, gradeBitmap));

  const startY = 30;
  const startX = w - decimal.width;
  items.push(
    bitmapItem(startX, startY, decimal),
    bitmapItem(startX - dot.width, startY + integer.height - 10, dot),
    bitmapItem(startX - dot.width - integer.width, startY, integer),
    bitmapItem(startX - dot.width - integer.width - currency.width, startY, currency),
    bitmapItem(w - date.width, 175, date)
  );
  return { width: w, height: h, items };
}

// ---------- 特价标签 ----------

/** 特价标签商品名：词间换行优先，每行超宽补省略号（对应 Kotlin 内联逻辑）。 */
async function discountNameBitmap(renderer: LabelTextRenderer, productName: string, maxWidth: number) {
  const font = androidFont("Arial", false, renderer.fonts);
  const size = pt(10);
  const lines = await wrapChars(renderer, cpclText(productName), font, size, maxWidth, 2);
  if (lines.length === 2 && !lines[0].endsWith(" ") && !lines[1].startsWith(" ")) {
    const split = lines[0].lastIndexOf(" ");
    if (split > 0) {
      lines[1] = lines[0].slice(split).trim() + lines[1];
      lines[0] = lines[0].slice(0, split);
    }
  }
  const display: string[] = [];
  const trimmed = lines.map((line) => line.trim());
  const fullMetrics = await renderer.measure(trimmed.map((text) => ({ text, font, size })));
  for (const [index, value] of trimmed.entries()) {
    if (fullMetrics[index].advance <= maxWidth) {
      display.push(value);
      continue;
    }
    // 从长到短批量量「前缀 + ...」，取第一个放得下的（对应逐字删到放下为止）。
    const chars = Array.from(value);
    const candidates: string[] = [];
    for (let length = chars.length - 1; length >= 0; length -= 1) candidates.push(chars.slice(0, length).join(""));
    const metrics = await renderer.measure(candidates.map((text) => ({ text: `${text}...`, font, size })));
    const fitIndex = metrics.findIndex((metric) => metric.advance <= maxWidth);
    display.push(`${fitIndex < 0 ? "" : candidates[fitIndex]}...`);
  }
  const [metrics] = await renderer.measure([{ text: display[0] || " ", font, size }]);
  const lineHeight = Math.ceil(metrics.descent + metrics.ascent);
  return drawLines(renderer, display, font, size, maxWidth, lineHeight, metrics.ascent);
}

export async function buildDiscountLabelDoc(
  payload: ProductLabelPrintPayload,
  printType: string | null | undefined,
  { renderer, now }: LabelBuildContext
): Promise<LabelDoc> {
  const small = isSmall(printType);
  const w = small ? SMALL_WIDTH : LABEL_WIDTH;
  const h = small ? SMALL_HEIGHT : LABEL_HEIGHT;
  const productName = payloadString(payload.productName);
  const itemNumber = payloadString(payload.itemNumber);
  const barcode = payloadString(payload.barcode) || itemNumber;
  const retailPrice = payloadNumber(payload.retailPrice) ?? 0;
  const discountRate = payloadNumber(payload.discountRate) ?? 0;
  const discountValue = discountRate * 100;
  // 先按分舍入（与 Kotlin kotlin.math.round 一样五成双），避免浮点尾差两端显示不同价格。
  const nowPrice = roundHalfEven(retailPrice * (1 - discountRate) * 100 + 1e-8) / 100;
  const showOriginalPrice = Number.isFinite(retailPrice) && Number.isFinite(nowPrice) && retailPrice > nowPrice && nowPrice >= 0;

  const columnGap = 10;
  const infoX = 84;
  const infoWidth = small ? 100 : 124;
  const wasX = infoX + infoWidth + columnGap;
  const wasWidth = small ? 76 : 96;
  // 右下黑底：折扣率（如「25% OFF」）；右上大字：折后新价格。
  const boxX = showOriginalPrice ? wasX + wasWidth + columnGap : wasX;
  const boxWidth = w - 12 - boxX;
  const boxPadding = 6;
  const offGap = 8;
  // 互不依赖的几段一起画（一次原生往返）；折扣率的限宽取决于 OFF 的宽度，只能放到后面。
  // 超长货号明确显示省略号；二维码继续编码完整条码 / 货号。
  const itemDisplay = itemNumber.length > 24 ? `${itemNumber.slice(0, 21)}...` : itemNumber;
  const [off, wasLabel, wasPrice, date, itemFitted] = await fittedTexts(renderer, [
    { spec: black("OFF", 10, { inverse: true }), maxWidth: Math.trunc(boxWidth / 3) },
    { spec: black("WAS", 6), maxWidth: wasWidth },
    { spec: black(`$${formatMoney(retailPrice)}`, 8), maxWidth: wasWidth, maxHeight: 30 },
    { spec: black(todayString(now), 6, { inverse: true, padding: 2 }), maxWidth: infoWidth, maxHeight: 24 },
    { spec: black(itemDisplay, 7), maxWidth: infoWidth, maxHeight: 28 },
  ]);
  // 货号为空时不画（上面只是占位）。
  const item = itemDisplay.trim() ? itemFitted : null;
  const rate = await fittedText(
    renderer,
    black(`${roundToInt(discountValue)}%`, 16, { inverse: true }),
    boxWidth - off.width - offGap - boxPadding * 2,
    52
  );
  const boxHeight = Math.max(rate.height, off.height) + boxPadding * 2;
  const box = MonoBitmap.filled(boxWidth, boxHeight, true);
  // 折扣率与「OFF」整体水平居中，底部对齐。
  const groupX = Math.max(boxPadding, Math.trunc((boxWidth - rate.width - offGap - off.width) / 2));
  const bottom = boxPadding + rate.height;
  box.draw(rate, groupX, boxPadding, "copy");
  box.draw(off, groupX + rate.width + offGap, bottom - off.height, "copy");

  // 新价格：整数 44 号大字，「$」「.」小数 20 号上对齐；放不下时整组按 0.05 逐级等比缩小，不截断金额。
  const price = formatPriceParts(nowPrice);
  const headMaxWidth = w - 12 - 160;
  const sizesAt = (scale: number): PriceSizes => ({ currency: 20 * scale, integer: 44 * scale, dot: 20 * scale, decimal: 20 * scale });
  const partsAt = (scales: number[]) => pricePartsBatch(renderer, price, scales.map(sizesAt), true);
  const fitsHead = (parts: Awaited<ReturnType<typeof partsAt>>[number]) =>
    parts.width <= headMaxWidth && parts.integer.height <= 108;
  let [parts] = await partsAt([1]);
  if (!fitsHead(parts)) {
    const scales = descending(1, 0.1, 0.05);
    const ratio = Math.min(headMaxWidth / parts.width, 108 / parts.integer.height);
    // 每轮 5 个候选字号一次批量画完。
    parts = (await pickLargestFitting(scales, estimateIndex(scales, 1, ratio), partsAt, fitsHead)).result;
  }
  // 合成一张位图再下发：避免相邻位图按字节补齐的空白互相覆盖。
  const priceBitmap = new MonoBitmap(
    parts.width,
    Math.max(parts.currency.height, parts.integer.height, parts.dot.height, parts.decimal.height)
  );
  let x = 0;
  priceBitmap.draw(parts.currency, x, 0);
  x += parts.currency.width;
  priceBitmap.draw(parts.integer, x, 0);
  x += parts.integer.width;
  // 小数点贴整数底部。
  priceBitmap.draw(parts.dot, x, parts.integer.height - parts.dot.height);
  x += parts.dot.width;
  priceBitmap.draw(parts.decimal, x, 0);

  const startY = 20;
  const startX = w - 12 - priceBitmap.width;
  // 两种纸宽都遵守 204 点有效打印区，底部保留 10 点。
  const infoBandBottom = 194;
  const qrY = infoBandBottom - QR_BOX;
  const dateY = infoBandBottom - date.height;
  const itemY = dateY - (item?.height ?? 0) - 6;
  const wasPriceY = infoBandBottom - wasPrice.height;
  const name = await discountNameBitmap(renderer, productName, Math.max(1, startX - 15));

  const items: LabelDocItem[] = [bitmapItem(5, 5, name), bitmapItem(startX, startY, priceBitmap)];
  if (item) items.push(bitmapItem(infoX, itemY, item));
  if (barcode) items.push(qrCode(10, qrY, barcode));
  items.push(bitmapItem(infoX, dateY, date));
  if (showOriginalPrice) {
    items.push(bitmapItem(wasX, wasPriceY - wasLabel.height - 4, wasLabel), bitmapItem(wasX, wasPriceY, wasPrice));
    const strikeY = wasPriceY + Math.trunc(wasPrice.height / 2);
    items.push(commandItem(`LINE ${wasX} ${strikeY} ${wasX + wasPrice.width - 1} ${strikeY} 2`));
  }
  items.push(bitmapItem(boxX, infoBandBottom - box.height, box));
  return { width: w, height: h, items };
}

// ---------- 清货标签 ----------

export async function buildClearanceLabelDoc(
  payload: ProductLabelPrintPayload,
  { renderer, now }: LabelBuildContext
): Promise<LabelDoc> {
  const w = 614;
  const h = 205;
  const productName = payloadString(payload.productName);
  const itemNumber = payloadString(payload.itemNumber);
  const barcode = payloadString(payload.clearanceBarcode) || payloadString(payload.barcode);
  const retailPrice = payloadNumber(payload.retailPrice) ?? 0;
  const discountRate = payloadNumber(payload.discountRate) ?? 0;
  const clearancePrice = payloadNumber(payload.clearancePrice) ?? retailPrice * (1 - discountRate);

  const [clearanceLabel, date, item] = await textBitmaps(renderer, [
    black("CLEARANCE", 30, { inverse: true, padding: 2 }),
    black(todayString(now), 7, { inverse: true, padding: 2 }),
    black(itemNumber, 7),
  ]);
  const price = await priceParts(renderer, formatPriceParts(clearancePrice), { currency: 20, integer: 40, dot: 20, decimal: 20 }, false);
  const infoAreaLeft = (barcode ? QR_BOX : 0) + 15;
  const nameMaxWidth = Math.max(1, w - price.width - infoAreaLeft - 15);
  const name = await longTextBitmap(renderer, cpclText(productName), 8, false, "Arial", 2, nameMaxWidth);

  const nameY = 21;
  const priceStartY = nameY + 8;
  const priceStartX = w - price.decimal.width - 68;
  const clearanceLabelY = h - clearanceLabel.height - 5;
  const clearanceLabelX = Math.max(0, Math.trunc((w - clearanceLabel.width) / 2));
  const qrY = clearanceLabelY - QR_BOX - 5;
  const infoY = qrY + 5;
  const dateY = infoY + Math.max(item.height, date.height) + 3;

  const items: LabelDocItem[] = [
    bitmapItem(5, nameY, name),
    bitmapItem(priceStartX, priceStartY, price.decimal),
    bitmapItem(priceStartX - price.dot.width, priceStartY + price.integer.height - 10, price.dot),
    bitmapItem(priceStartX - price.dot.width - price.integer.width, priceStartY, price.integer),
    bitmapItem(priceStartX - price.dot.width - price.integer.width - price.currency.width, priceStartY, price.currency),
    bitmapItem(clearanceLabelX, clearanceLabelY, clearanceLabel),
    bitmapItem(infoAreaLeft, infoY, item),
    bitmapItem(infoAreaLeft, dateY, date),
  ];
  if (barcode) items.push(qrCode(5, qrY, barcode));
  return { width: w, height: h, items };
}

// ---------- 大折扣标签 ----------

async function bigDiscountHeader(
  renderer: LabelTextRenderer,
  discountRate: number,
  printType: string,
  paperWidth: number
): Promise<LabelDocItem[]> {
  const discount = discountRate * 100;
  if (printType) {
    const title = await textBitmap(renderer, black(printType, 25));
    return [bitmapItem(Math.trunc(paperWidth / 2) - Math.trunc(title.width / 2), 80, title)];
  }
  if (discount <= 10 || discount > 100) {
    const special = await textBitmap(renderer, black("Special", 40));
    return [bitmapItem(Math.trunc(paperWidth / 2) - Math.trunc(special.width / 2), 40, special)];
  }
  if (Math.abs(discount - 50) < 0.01) {
    const [half, priceText] = await textBitmaps(renderer, [black("1/2", 40), black("PRICE", 25)]);
    return [bitmapItem(130, 20, half), bitmapItem(120, 20 + half.height, priceText)];
  }
  const [value, percent, off] = await textBitmaps(renderer, [
    black(String(roundToInt(discount)), 40),
    { text: "%", size: 24, bold: true, family: "sans-serif-condensed" },
    black("OFF", 20),
  ]);
  const startX = Math.trunc((paperWidth - value.width - percent.width) / 2);
  return [
    bitmapItem(startX, 20, value),
    bitmapItem(startX + value.width, 20, percent),
    bitmapItem(Math.trunc((paperWidth - off.width) / 2), 20 + value.height + 20, off),
  ];
}

export async function buildBigDiscountLabelDoc(
  payload: ProductLabelPrintPayload,
  printType: string | null | undefined,
  { renderer, now }: LabelBuildContext
): Promise<LabelDoc> {
  const productName = payloadString(payload.productName);
  const barcode = payloadString(payload.barcode);
  const retailPrice = payloadNumber(payload.retailPrice) ?? 0;
  const discountRate = payloadNumber(payload.discountRate) ?? 0;
  const paperWidth = 480;
  const price = formatPriceParts(retailPrice * (1 - discountRate));
  const saveAmount = retailPrice * discountRate;

  const items: LabelDocItem[] = await bigDiscountHeader(renderer, discountRate, (printType ?? "").trim(), paperWidth);

  const [currency, wasCurrency, saveCurrency, ea, was, save, integer, decimal, dot, rrp, saveValue, date] = await textBitmaps(renderer, [
    black("$", 20, { bold: false }),
    black("$", 8),
    black("$", 8, { bold: false }),
    { text: "ea", size: 8, bold: false, family: "sans-serif-light", inverse: true, padding: 2 },
    { text: "WAS ", size: 10, bold: true, family: "sans-serif-light" },
    { text: "SAVE", size: 16, bold: true, family: "sans-serif-light", inverse: true, padding: 2 },
    black(price.integer, 60),
    black(price.decimal, 24),
    black(".", 20),
    { text: formatMoney(retailPrice), size: 10, bold: true, family: "sans-serif-condensed" },
    { text: formatMoney(saveAmount), size: 16, bold: true, family: "sans-serif-condensed" },
    { text: todayString(now), size: 8, bold: false, family: "Arial", inverse: true, padding: 2 },
  ]);
  const name = await longTextBitmap(renderer, cpclText(productName), 10, false, "Arial", 4, paperWidth);

  const startY = 220;
  let startX = Math.trunc((paperWidth - currency.width - integer.width) / 2);
  const tailY = startY + Math.trunc(integer.height * 0.9);
  if (Number(price.decimal) !== 0) {
    startX = Math.trunc((paperWidth - currency.width - integer.width - dot.width - decimal.width) / 2);
    items.push(
      bitmapItem(startX + currency.width + integer.width, tailY, dot),
      bitmapItem(startX + currency.width + integer.width + dot.width, startY, decimal)
    );
  }
  items.push(
    bitmapItem(startX, startY, currency),
    bitmapItem(startX + currency.width, startY, integer),
    bitmapItem(startX + currency.width + integer.width + 30, tailY, ea)
  );

  const rrpStartY = startY + integer.height + 20;
  items.push(
    bitmapItem(5, rrpStartY, was),
    bitmapItem(5 + was.width, rrpStartY, wasCurrency),
    bitmapItem(5 + was.width + wasCurrency.width, rrpStartY, rrp)
  );
  if (discountRate > 0) {
    const right = 5 + was.width + rrp.width;
    items.push(
      commandItem(
        `LINE 5 ${rrpStartY} ${right} ${rrpStartY + was.height} 2`,
        `LINE 5 ${rrpStartY + was.height} ${right} ${rrpStartY} 2`
      )
    );
    const saveStartX = 30 + was.width + wasCurrency.width + rrp.width;
    items.push(
      bitmapItem(saveStartX, rrpStartY, save),
      bitmapItem(saveStartX + save.width + 5, rrpStartY, saveCurrency),
      bitmapItem(saveStartX + save.width + 5 + saveCurrency.width + 5, rrpStartY, saveValue)
    );
  }

  items.push(bitmapItem(5, 550 - name.height - 5, name), bitmapItem(15, 550, dashLine(450, 2)));
  if (barcode) items.push(commandItem(`BARCODE 128 1 2 30 15 560 ${cpclText(barcode)}`));
  items.push(bitmapItem(paperWidth - date.width - 10, 630 - date.height, date));
  return { width: paperWidth, height: 1200, items };
}

// ---------- 仓库商品标签 ----------

export async function buildWarehouseProductLabelDoc(
  payload: WarehouseProductLabelPrintPayload,
  { renderer, now }: LabelBuildContext
): Promise<LabelDoc> {
  const w = LABEL_WIDTH;
  const h = WAREHOUSE_HEIGHT;
  const productName = payloadString(payload.productName);
  const itemNumber = payloadString(payload.itemNumber);
  const barcode = payloadString(payload.barcode);
  const middlePackageQuantity = payloadNumber(payload.middlePackageQuantity);
  const purchasePrice = payloadNumber(payload.purchasePrice);
  const retailPrice = payloadNumber(payload.retailPrice);
  const locationCode = payloadString(payload.locationCode);
  const domesticPrice = payloadNumber(payload.domesticPrice);
  const oemPrice = payloadNumber(payload.oemPrice);
  const importPrice = payloadNumber(payload.importPrice);
  const displayPrice = retailPrice ?? domesticPrice ?? oemPrice ?? importPrice;
  const costPrice = purchasePrice ?? importPrice ?? domesticPrice ?? oemPrice;
  // 只在实际中包数大于 1 时显示 INNER。
  const innerText = middlePackageQuantity != null && middlePackageQuantity > 1 ? `INNER ${formatOptionalQuantity(middlePackageQuantity)}` : null;
  const contentWidth = w - 40;

  const specs: TextSpec[] = [
    black("WAREHOUSE PRODUCT", 8, { inverse: true, padding: 2 }),
    black(`ITEM ${cpclText(itemNumber || "--")}`, 7),
    black(`COST ${formatOptionalMoney(costPrice)}`, 7),
    black(`RRP ${formatOptionalMoney(displayPrice)}`, 7),
    black(`LOC ${cpclText(locationCode || "UNASSIGNED")}`, 8, { inverse: true, padding: 2 }),
    black(todayString(now), 7, { inverse: true, padding: 2 }),
  ];
  if (innerText) specs.push(black(innerText, 7));
  const [title, item, cost, rrp, location, date, inner] = await textBitmaps(renderer, specs);
  const name = await longTextBitmap(renderer, productName, 8, true, "Arial", 1, Math.max(180, contentWidth));
  const centerX = (bitmap: MonoBitmap) => Math.max(0, Math.trunc((w - bitmap.width) / 2));

  const items: LabelDocItem[] = [
    bitmapItem(centerX(title), 12, title),
    bitmapItem(centerX(name), 38, name),
    bitmapItem(centerX(item), 66, item),
    bitmapItem(20, 92, location),
    bitmapItem(w - date.width - 20, 92, date),
  ];
  if (barcode) {
    items.push(commandItem("BARCODE-TEXT 7 0 5", `BARCODE 128 1 1 38 24 146 ${cpclText(barcode)}`));
    if (inner) items.push(bitmapItem(380, 132, inner));
    items.push(bitmapItem(380, 156, cost), bitmapItem(380, 180, rrp));
  } else {
    if (inner) items.push(bitmapItem(centerX(inner), 132, inner));
    items.push(bitmapItem(centerX(cost), 156, cost), bitmapItem(centerX(rrp), 180, rrp));
  }
  return { width: w, height: h, items };
}

// ---------- 仓库货位标签 ----------

export async function buildWarehouseLocationLabelDoc(
  payload: WarehouseLocationLabelPrintPayload,
  { renderer }: LabelBuildContext
): Promise<LabelDoc> {
  const w = LABEL_WIDTH;
  const h = WAREHOUSE_HEIGHT;
  const locationCode = payloadString(payload.locationCode);
  const locationBarcode = payloadString(payload.locationBarcode);
  const locationGuid = payloadString(payload.locationGuid);
  // 显示代码优先级：locationCode > locationBarcode > locationGuid；全空时打印占位符。
  const displayCode = locationCode || locationBarcode || locationGuid;
  const printableCode = displayCode || "--";
  // 条码优先级：locationBarcode，其次显示代码；全空则不出条码。
  const barcode = locationBarcode || displayCode;

  // 上方 2/3：完整货位代码黑字白底；安全区 540 × 120，字号从 30 逐级减 1，取最大放得下的，不换行不截断。
  const maxCodeWidth = 540;
  const maxCodeHeight = 120;
  const sizes = descending(30, 1, 1);
  const fits = (bitmap: MonoBitmap) => bitmap.width <= maxCodeWidth && bitmap.height <= maxCodeHeight;
  // 先只量不画：超长代码按 30 号直接画会超出原生单图上限（PRINTER_RENDER_TOO_LARGE）。
  const [measured] = await renderer.measure([
    { text: printableCode, font: androidFont("sans-serif-black", true, renderer.fonts), size: pt(30) },
  ]);
  const estimatedWidth = Math.ceil(Math.max(measured.advance, measured.inkRight - measured.inkLeft));
  const estimatedHeight = Math.ceil(measured.inkBottom - measured.inkTop);
  let code: MonoBitmap | null = null;
  if (estimatedWidth <= maxCodeWidth && estimatedHeight <= maxCodeHeight) {
    code = await textBitmap(renderer, black(printableCode, 30));
  }
  if (!code || !fits(code)) {
    const ratio = Math.min(maxCodeWidth / Math.max(1, estimatedWidth), maxCodeHeight / Math.max(1, estimatedHeight));
    code = (
      await pickLargestFitting(
        sizes,
        estimateIndex(sizes, 30, ratio),
        (values) => textBitmaps(renderer, values.map((size) => black(printableCode, size))),
        fits
      )
    ).result;
  }
  // 极端长代码最小字号仍放不下时，按比例缩放整张位图，避免裁切或越界。
  if (!fits(code)) {
    const scale = Math.min(maxCodeWidth / code.width, maxCodeHeight / code.height);
    code = code.scale(Math.max(1, Math.trunc(code.width * scale)), Math.max(1, Math.trunc(code.height * scale)));
  }
  const codeX = Math.max(0, Math.trunc((w - code.width) / 2));
  const codeY = Math.trunc((Math.trunc((h * 2) / 3) - code.height) / 2);

  const items: LabelDocItem[] = [bitmapItem(codeX, codeY, code)];
  if (barcode) {
    // BARCODE-TEXT 会跨标签保持，必须显式关闭；CENTER 让 Code128 水平居中。
    items.push(commandItem("BARCODE-TEXT OFF", "CENTER", `BARCODE 128 1 1 44 0 151 ${cpclText(barcode)}`));
  }
  return { width: w, height: h, items };
}

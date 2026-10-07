import {
  appendEscPosInitialize,
  encodeEscPosText,
} from "./esc-pos-text-encoding";
import {
  receiptCode128,
  receiptCode128ModuleWidth,
} from "./receipt-code128";

export type ReceiptPaper = "58mm" | "80mm";
export type ReceiptLocale = "en" | "zh-CN";
export type ReceiptAlignment = "left" | "center" | "right";

export type ReceiptTextLine = Readonly<{
  kind: "text";
  text: string;
  align: ReceiptAlignment;
  bold: boolean;
}>;
export type ReceiptSeparatorLine = Readonly<{
  kind: "separator";
  text: string;
  align: ReceiptAlignment;
  bold: boolean;
}>;
export type ReceiptFeedLine = Readonly<{ kind: "feed"; text?: string }>;
export type ReceiptBarcodeLine = Readonly<{ kind: "barcode"; value: string; text?: string }>;
export type ReceiptQrLine = Readonly<{ kind: "qr"; value: string; text?: string }>;
export type ReceiptLine = ReceiptTextLine | ReceiptSeparatorLine | ReceiptFeedLine | ReceiptBarcodeLine | ReceiptQrLine;
export type EscPosDocument = Readonly<{ paper: ReceiptPaper; lines: readonly ReceiptLine[] }>;

export type ReceiptStoreHeading = Readonly<{
  brandName: string;
  storeName: string;
  address: string;
  phone: string;
  abn: string;
  returnPolicy: string;
}>;

/** 小票底部的条款块：标题（加粗）加若干条款行，文案由调用方提供，本文件只负责排版。 */
export type ReceiptTermsBlock = Readonly<{
  title: string;
  lines: readonly string[];
}>;

export type SaleInput = Readonly<{
  locale: ReceiptLocale;
  paper: ReceiptPaper;
  store: ReceiptStoreHeading;
  orderNumber: string;
  soldAtIso: string;
  cashierName: string;
  deviceCode: string;
  lines: readonly Readonly<{ name: string; lookupCode: string; quantity: string; discountCents: number; totalCents: number }>[];
  subtotalCents: number;
  discountCents: number;
  totalCents: number;
  tenders: readonly Readonly<{ method: "cash" | "card" | "voucher" | "other"; amountCents: number; reference?: string | null }>[];
  cashChangeCents: number | null;
  isReprint?: boolean;
  title?: string;
  /** 完整订单 GUID，用于机读码；普通销售也可选择直接作为可见订单标识。 */
  orderGuid?: string;
  /** 面向顾客的订单号；未提供时兼容旧版 orderNumber。 */
  orderDisplay?: string;
  /** labelled 保留业务编号；guid-only 只显示完整 GUID，不增加 Order 标签。 */
  orderPresentation?: "labelled" | "guid-only";
  storeCode?: string;
  statusText?: string;
  printedAtIso?: string;
  includeMachineCodes?: boolean;
  /** 由票据领域生成并安全校验的业务扩展行；UI 不得注入原始支付材料。 */
  extraInfoLines?: readonly string[];
  /**
   * 退货政策之后、条码之前的条款块（如分期条款）。不传（默认）则完全不打印，
   * 其它所有小票的输出保持不变；目前只有分期补打传入。
   */
  termsBlock?: ReceiptTermsBlock;
}>;
type DailyInput = Readonly<{ locale: ReceiptLocale; paper: ReceiptPaper; storeName: string; businessDate: string; deviceCode: string; cashierName: string; paymentTotals: readonly Readonly<{ method: string; salesCents: number; refundCents: number; netCents: number }>[]; orderCount: number; salesCents: number; refundCents: number; netCents: number; expectedCashCents: number; countedCashCents: number; differenceCents: number }>;
type BankInput = Readonly<{ locale: ReceiptLocale; paper: ReceiptPaper; status: string; cardType?: string; maskedCardNumber?: string; reference?: string; rawText?: string }>;

export function buildSaleReceiptDocument(input: SaleInput): EscPosDocument {
  assertSafeSaleText(input);
  const width = lineWidth(input.paper);
  const zh = input.locale === "zh-CN";
  const b = new Builder(input.paper, width);
  const orderGuid = nonBlank(input.orderGuid);
  const storeName = nonBlank(input.store.storeName);
  const brandName = receiptStoreHeading(
    input.store.brandName,
    input.store.storeName,
    input.storeCode,
  );
  const phone = nonBlank(input.store.phone);
  const abn = nonBlank(input.store.abn);
  const title = nonBlank(input.title) ?? (zh ? "===== 税务发票 =====" : "===== TAX INVOICE =====");
  const statusText = nonBlank(input.statusText) ?? (zh ? "*** 已支付 ***" : "*** Paid ***");
  const displayOrder = nonBlank(input.orderDisplay) ?? nonBlank(input.orderNumber) ?? "-";
  const printedAtIso = nonBlank(input.printedAtIso) ?? input.soldAtIso;
  const [itemWidth, quantityWidth, priceWidth] = saleColumnWidths(width);

  b.text(brandName, "center", true);
  // 中文注释：无品牌时分店名本身就是抬头；品牌与分店同名也只打印一次。
  if (storeName && storeName.toLocaleLowerCase() !== brandName.toLocaleLowerCase()) {
    b.text(storeName, "center");
  }
  for (const addressLine of wrapByWord(input.store.address, Math.min(35, width))) {
    b.text(addressLine, "center");
  }
  if (phone) b.text(`Tel: ${phone}`, "center");
  if (abn) b.text(`ABN: ${abn}`, "center");
  b.blank();

  b.text(title, "center");
  b.blank();
  b.text(statusText, "center", true);
  if (input.isReprint) {
    b.text(zh ? "*** 重打 / REPRINT ***" : "*** REPRINT ***", "center", true);
  }
  b.blank();

  if (input.orderPresentation === "guid-only") {
    if (!orderGuid) throw new Error("Sale receipt GUID display requires orderGuid.");
    b.text(orderGuid);
  } else {
    b.text(`${zh ? "订单" : "Order"}: ${displayOrder}`);
  }
  b.text(`${zh ? "时间" : "Date"}: ${formatDate(input.soldAtIso)}`);
  b.text(`${zh ? "收银员" : "Cashier"}: ${input.cashierName}`);
  for (const line of wrapByWord(`Store: ${formatStoreDisplay(input.store.storeName, input.storeCode ?? "")}`, width)) {
    b.text(line);
  }
  b.text(`${zh ? "设备" : "Device"}: ${input.deviceCode}`);
  for (const infoLine of input.extraInfoLines ?? []) {
    if (infoLine.trim()) b.text(infoLine.trim());
  }
  b.separator();
  b.text(columns(
    [zh ? "商品" : "ITEM", zh ? "数量" : "QTY", zh ? "金额" : "PRICE"],
    [itemWidth, quantityWidth, priceWidth],
    width,
    true,
  ));
  b.separator();

  for (const line of input.lines) {
    for (const nameLine of wrapByWord(line.name, width)) b.text(nameLine);
    b.text(columns(
      [line.lookupCode, line.quantity, money(line.totalCents)],
      [itemWidth, quantityWidth, priceWidth],
      width,
      true,
    ));
    if (line.discountCents !== 0) b.two(zh ? "折扣" : "Dis", `-${money(Math.abs(line.discountCents))}`);
  }

  b.separator();
  b.two(zh ? "小计" : "Subtotal", money(input.subtotalCents));
  if (input.discountCents !== 0) b.two(zh ? "折扣" : "Discount", `-${money(Math.abs(input.discountCents))}`);
  b.two("GST", money(roundAwayFromZero(input.totalCents / 11)));
  b.two(zh ? "总计(含GST)" : "Total(inc GST)", money(input.totalCents), true);
  b.separator();
  b.text("Payment:");
  for (const tender of input.tenders) {
    b.two(tenderLabel(tender.method, zh), money(tender.amountCents));
    if (tender.reference) b.text(`  Ref: ${maskReference(tender.reference)}`);
  }

  const returnPolicy = nonBlank(input.store.returnPolicy);
  if (returnPolicy) {
    b.separator();
    b.text(zh ? "退款与退货" : "Refunds and returns", "left", true);
    for (const policyLine of wrapByWord(returnPolicy, width)) {
      b.text(policyLine);
    }
  }

  // 中文注释：条款块紧跟退货政策（无退货政策则紧跟付款块），用分隔线与上文隔开；
  // 与 WPF 顺序一致：付款 → 退货政策 → 条款 → 分隔线 → 条码。
  // 标题加粗左对齐（与「Refunds and returns」同款），条款行左对齐并按纸宽在单词边界换行。
  // 只有传入了 termsBlock 且含可打印内容才输出，因此未传入时与原输出字节级一致。
  const termsTitle = nonBlank(input.termsBlock?.title);
  const termsLines = (input.termsBlock?.lines ?? []).filter((line) => line.trim());
  if (termsTitle || termsLines.length > 0) {
    b.separator();
    if (termsTitle) b.text(termsTitle, "left", true);
    for (const termsLine of termsLines) {
      for (const wrapped of wrapByWord(termsLine, width)) b.text(wrapped);
    }
  }

  b.separator();
  // 中文注释：顾客看到本机序号，机读码仍固定使用完整 orderGuid，二者不得混用。
  if (input.includeMachineCodes && orderGuid) {
    const code128 = receiptCode128(orderGuid);
    // 中文注释：完整 GUID 在窄纸上未必能满足 Code128 静区；放不下时保留完整 QR，绝不截断载荷。
    if (receiptCode128ModuleWidth(code128, input.paper) !== null) {
      b.barcode(orderGuid);
    }
    b.qr(orderGuid);
  }
  b.text(`Print Time: ${formatDate(printedAtIso)}`);
  b.text(`${zh ? "设备" : "Device"}: ${input.deviceCode}`);
  b.blank();
  b.text(zh ? "感谢惠顾" : "Thank you for your purchase!", "center", true);
  b.blank();
  b.feed();
  return b.build();
}

/** 所有票据共用同一抬头回退顺序，并拒绝可注入打印指令的控制字符。 */
export function receiptStoreHeading(
  brandName: string,
  storeName: string,
  storeCode?: string,
): string {
  assertSafeText(brandName, "store.brandName", false);
  assertSafeText(storeName, "store.storeName", false);
  assertSafeText(storeCode, "storeCode", false);
  return nonBlank(brandName) ?? nonBlank(storeName) ?? nonBlank(storeCode) ?? "-";
}

export function buildDailyCloseDocument(input: DailyInput): EscPosDocument {
  const width = lineWidth(input.paper);
  const b = new Builder(input.paper, width);
  b.text(input.storeName, "center", true);
  b.text(input.locale === "zh-CN" ? "日结" : "DAILY CLOSE", "center", true);
  b.text(`${input.locale === "zh-CN" ? "日期" : "Date"}: ${input.businessDate}`);
  b.text(`Terminal: ${input.deviceCode}`);
  b.text(`Cashier: ${input.cashierName}`);
  b.separator();
  for (const p of input.paymentTotals) b.text(columns([p.method, money(p.salesCents), money(p.refundCents), money(p.netCents)], [Math.max(8, width - 27), 9, 9, 9], width));
  b.separator();
  b.two("Orders", String(input.orderCount));
  b.two("Sales Amount", money(input.salesCents));
  b.two("Refund Amount", money(input.refundCents));
  b.two("Net Amount", money(input.netCents), true);
  b.separator();
  b.two("Cash Expected", money(input.expectedCashCents));
  b.two("Cash Counted", money(input.countedCashCents));
  b.two("Cash Difference", money(input.differenceCents), true);
  b.feed();
  return b.build();
}

export function buildBankReceiptDocument(input: BankInput): EscPosDocument {
  const b = new Builder(input.paper, lineWidth(input.paper));
  b.text(input.locale === "zh-CN" ? "银行卡回单" : "CARD RECEIPT", "center", true);
  b.text(sanitizeBankText(input.status), "center", true);
  if (input.cardType || input.maskedCardNumber) b.text([input.cardType, maskPan(input.maskedCardNumber ?? "")].filter(Boolean).join(" "));
  if (input.reference) b.text(`Ref: ${maskReference(input.reference)}`);
  for (const raw of (input.rawText ?? "").split(/\r?\n/)) b.wrap(sanitizeBankText(raw));
  b.feed();
  return b.build();
}

export function documentToEscPosBytes(document: EscPosDocument): Uint8Array {
  const chunks: number[] = [];
  appendEscPosInitialize(chunks);
  const utf8 = new TextEncoder();
  for (const line of document.lines) {
    switch (line.kind) {
      case "feed":
        chunks.push(0x1b, 0x64, 0x03);
        break;
      case "separator":
        chunks.push(
          0x1b, 0x61, 0,
          0x1b, 0x45, 0,
          ...encodeEscPosText(line.text), 0x0a,
        );
        break;
      case "text":
        chunks.push(
          0x1b, 0x61, line.align === "center" ? 1 : line.align === "right" ? 2 : 0,
          0x1b, 0x45, line.bold ? 1 : 0,
          ...encodeEscPosText(line.text), 0x0a,
        );
        break;
      case "barcode":
        appendCode128(chunks, line.value, document.paper, utf8);
        break;
      case "qr":
        appendQrModel2(chunks, line.value, utf8);
        break;
    }
  }
  chunks.push(0x1d, 0x56, 0x00);
  return Uint8Array.from(chunks);
}

export function displayWidth(value: string): number {
  return [...value].reduce((n, c) => n + (isWide(c) ? 2 : 1), 0);
}

class Builder {
  private readonly out: ReceiptLine[] = [];

  public constructor(private readonly paper: ReceiptPaper, private readonly width: number) {}

  public text(text: string, align: ReceiptAlignment = "left", bold = false): void {
    for (const line of wrap(text, this.width)) this.out.push({ kind: "text", text: line, align, bold });
  }

  public wrap(text: string, align: ReceiptAlignment = "left"): void {
    this.text(text, align);
  }

  public blank(): void {
    this.out.push({ kind: "text", text: "", align: "left", bold: false });
  }

  public separator(): void {
    this.out.push({ kind: "separator", text: "-".repeat(this.width), align: "left", bold: false });
  }

  public two(left: string, right: string, bold = false): void {
    this.text(two(left, right, this.width), "left", bold);
  }

  public barcode(value: string): void {
    this.out.push({ kind: "barcode", value });
  }

  public qr(value: string): void {
    this.out.push({ kind: "qr", value });
  }

  public feed(): void {
    this.out.push({ kind: "feed" });
  }

  public build(): EscPosDocument {
    return { paper: this.paper, lines: this.out };
  }
}

/** 80mm 与 WPF 共用 42 字符，58mm 保持 32 字符。 */
function lineWidth(paper: ReceiptPaper): number {
  return paper === "58mm" ? 32 : 42;
}

function appendCode128(
  chunks: number[],
  value: string,
  paper: ReceiptPaper,
  utf8: TextEncoder,
): void {
  const encoding = receiptCode128(value);
  const moduleWidth = receiptCode128ModuleWidth(encoding, paper);
  if (moduleWidth === null) {
    throw new Error("Code128 receipt value does not fit the configured paper.");
  }
  const payload = utf8.encode(encoding.payload);
  if (payload.length === 0 || payload.length > 255) throw new Error("Code128 receipt value is out of range.");
  // 中文注释：GS k Function B；固定使用可移植的 2 dot，并保留自动 B/C 集合切换。
  chunks.push(0x1b, 0x61, 1, 0x1d, 0x68, 100, 0x1d, 0x77, moduleWidth, 0x1d, 0x48, 0, 0x1d, 0x6b, 73, payload.length, ...payload, 0x0a);
}

function appendQrModel2(chunks: number[], value: string, utf8: TextEncoder): void {
  const payload = utf8.encode(value);
  const size = payload.length + 3;
  if (payload.length === 0 || size > 0xffff) throw new Error("QR receipt value is out of range.");
  const pL = size & 0xff;
  const pH = size >> 8;
  // 中文注释：依次选择 QR Model 2、模块大小、纠错级别、存储 UTF-8 数据并打印。
  chunks.push(
    0x1b, 0x61, 1,
    0x1d, 0x28, 0x6b, 4, 0, 49, 65, 50, 0,
    0x1d, 0x28, 0x6b, 3, 0, 49, 67, 6,
    0x1d, 0x28, 0x6b, 3, 0, 49, 69, 49,
    0x1d, 0x28, 0x6b, pL, pH, 49, 80, 48, ...payload,
    0x1d, 0x28, 0x6b, 3, 0, 49, 81, 48,
    0x0a,
  );
}

function formatDate(value: string): string {
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) {
    throw new TypeError("Receipt time is invalid.");
  }
  const pad = (part: number) => String(part).padStart(2, "0");
  // 中文注释：与 WPF DateTimeOffset.ToLocalTime 对齐，票面时间使用终端本地时区。
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

function formatStoreDisplay(storeName: string, storeCode: string): string {
  const name = storeName.trim();
  const code = storeCode.trim();
  if (!name) return code || "-";
  if (!code) return name;
  return name.toLocaleLowerCase() === code.toLocaleLowerCase() ? code : `${name} (${code})`;
}

function nonBlank(value: string | undefined): string | undefined {
  return value?.trim() || undefined;
}

function assertSafeSaleText(input: SaleInput): void {
  const strictFields: readonly [string, string | undefined][] = [
    ["store.brandName", input.store.brandName],
    ["store.storeName", input.store.storeName],
    ["store.phone", input.store.phone],
    ["store.abn", input.store.abn],
    ["orderNumber", input.orderNumber],
    ["orderGuid", input.orderGuid],
    ["orderDisplay", input.orderDisplay],
    ["soldAtIso", input.soldAtIso],
    ["cashierName", input.cashierName],
    ["storeCode", input.storeCode],
    ["deviceCode", input.deviceCode],
    ["statusText", input.statusText],
    ["printedAtIso", input.printedAtIso],
    ["title", input.title],
  ];
  for (const [fieldName, value] of strictFields) {
    assertSafeText(value, fieldName, false);
  }
  assertSafeMultilineText(input.store.address, "store.address");
  assertSafeMultilineText(input.store.returnPolicy, "store.returnPolicy");
  input.lines.forEach((line, index) => {
    assertSafeText(line.name, `lines[${index}].name`, true);
    assertSafeText(line.lookupCode, `lines[${index}].lookupCode`, false);
    assertSafeText(line.quantity, `lines[${index}].quantity`, false);
  });
  input.tenders.forEach((tender, index) => {
    assertSafeText(tender.reference ?? undefined, `tenders[${index}].reference`, false);
  });
  input.extraInfoLines?.forEach((line, index) => {
    assertSafeText(line, `extraInfoLines[${index}]`, false);
  });
  // 中文注释：条款块与其它票面文本同样不得夹带控制字符（防止注入打印指令）。
  assertSafeText(input.termsBlock?.title, "termsBlock.title", false);
  input.termsBlock?.lines.forEach((line, index) => {
    assertSafeText(line, `termsBlock.lines[${index}]`, false);
  });
}

function assertSafeText(
  value: string | undefined,
  fieldName: string,
  allowLineBreaks: boolean,
): void {
  if (value === undefined) return;
  const candidate = allowLineBreaks ? value.replace(/[\r\n]/g, "") : value;
  if (/[\u0000-\u001f\u007f-\u009f]/u.test(candidate)) {
    throw new TypeError(`${fieldName} contains unsafe control characters.`);
  }
}

/** 地址与退货政策需要换行/制表排版，仅放行 CR/LF/TAB。 */
function assertSafeMultilineText(value: string | undefined, fieldName: string): void {
  if (value === undefined) return;
  const candidate = value.replace(/[\r\n\t]/g, "");
  if (/[\u0000-\u001f\u007f-\u009f]/u.test(candidate)) {
    throw new TypeError(`${fieldName} contains unsafe control characters.`);
  }
}

function money(cents: number): string {
  const sign = cents < 0 ? "-" : "";
  const value = Math.abs(assertCents(cents));
  return `$${sign}${Math.floor(value / 100)}.${String(value % 100).padStart(2, "0")}`;
}

function assertCents(value: number): number {
  if (!Number.isSafeInteger(value)) throw new Error("Receipt amounts must be integer cents.");
  return value;
}

function two(left: string, right: string, width: number): string {
  const trimmedRight = trimWithEllipsis(right, Math.min(16, width - 2));
  const trimmedLeft = trimWithEllipsis(left, Math.max(1, width - 18));
  return trimmedLeft + " ".repeat(Math.max(1, width - displayWidth(trimmedLeft) - displayWidth(trimmedRight))) + trimmedRight;
}

function columns(
  values: readonly string[],
  widths: readonly number[],
  width: number,
  ellipsis = false,
): string {
  const result = values
    .map((value, index) => {
      const columnWidth = widths[index]!;
      const fitted = ellipsis ? trimWithEllipsis(value, columnWidth) : trim(value, columnWidth);
      const padding = " ".repeat(Math.max(0, columnWidth - displayWidth(fitted)));
      return index === 0 ? fitted + padding : padding + fitted;
    })
    .join("");
  return trim(result, width);
}

function saleColumnWidths(width: number): readonly [number, number, number] {
  return [width - 17, 5, 12];
}

function wrap(text: string, width: number): readonly string[] {
  const lines: string[] = [];
  let current = "";
  for (const char of [...(text || "")]) {
    if (char === "\n") {
      lines.push(current);
      current = "";
      continue;
    }
    if (displayWidth(current) + displayWidth(char) > width) {
      lines.push(current);
      current = "";
    }
    current += char;
  }
  if (current || lines.length === 0) lines.push(current);
  return lines;
}

function trim(value: string, width: number): string {
  let output = "";
  for (const char of [...value]) {
    if (displayWidth(output) + displayWidth(char) > width) break;
    output += char;
  }
  return output;
}

function trimWithEllipsis(value: string, width: number): string {
  if (displayWidth(value) <= width) return value;
  if (width <= 3) return trim(value, width);
  return `${trim(value, width - 3)}...`;
}

function wrapByWord(value: string, width: number): readonly string[] {
  if (!value.trim()) return [];
  const lines: string[] = [];
  for (const paragraph of value.replace(/\r\n/g, "\n").split("\n")) {
    let current = "";
    for (const word of paragraph.trim().split(/\s+/).filter(Boolean)) {
      if (displayWidth(word) > width) {
        if (current) {
          lines.push(current);
          current = "";
        }
        lines.push(...wrap(word, width));
        continue;
      }
      const candidate = current ? `${current} ${word}` : word;
      if (displayWidth(candidate) > width) {
        lines.push(current);
        current = word;
      } else {
        current = candidate;
      }
    }
    if (current) lines.push(current);
  }
  return lines;
}

function roundAwayFromZero(value: number): number {
  return value < 0 ? -Math.round(Math.abs(value)) : Math.round(value);
}

function isWide(char: string): boolean {
  const codePoint = char.codePointAt(0) ?? 0;
  return codePoint >= 0x1100 && (codePoint <= 0x115f || codePoint >= 0x2e80 || codePoint >= 0x1f300);
}

function tenderLabel(method: string, zh: boolean): string {
  return zh
    ? ({ cash: "现金", card: "银行卡", voucher: "代金券", other: "其他" }[method] ?? method)
    : ({ cash: "Cash", card: "Card", voucher: "Voucher", other: "Other" }[method] ?? method);
}

function maskReference(value: string): string {
  const clean = value.replace(/\s/g, "");
  return clean.length <= 4 ? "****" : `****${clean.slice(-4)}`;
}

function maskPan(value: string): string {
  const digits = value.replace(/\D/g, "");
  return digits.length >= 12 ? `****${digits.slice(-4)}` : maskReference(value);
}

function sanitizeBankText(value: string): string {
  if (/\b(token|voucher|authorization|auth\s*code)\b/i.test(value)) return "[REDACTED]";
  const pan = value.replace(/(?<!\d)(?:\d[ .-]*){11,18}\d(?!\d)/g, (match) => maskPan(match));
  return /\b(rrn|txn\s*ref|stan|trace|invoice)\b/i.test(pan)
    ? pan.replace(/(:\s*)(\S+)/, (_match, prefix) => `${prefix}${maskReference(String(_match).split(prefix)[1] ?? "")}`)
    : pan;
}

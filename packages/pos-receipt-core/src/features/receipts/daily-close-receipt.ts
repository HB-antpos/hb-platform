import {
  appendEscPosInitialize,
  encodeEscPosText,
} from "./esc-pos-text-encoding";
import {
  receiptStoreHeading,
  type ReceiptLocale,
  type ReceiptPaper,
} from "./receipt-document";

import type { DailyCloseArchive } from "@hb/pos-domain/core/contracts/daily-close";

export const DAILY_CLOSE_RECEIPT_WIDTH = 42;
export const DAILY_CLOSE_RECEIPT_NARROW_WIDTH = 32;

type DailyCloseReceiptWidth =
  | typeof DAILY_CLOSE_RECEIPT_NARROW_WIDTH
  | typeof DAILY_CLOSE_RECEIPT_WIDTH;

export type DailyCloseReceiptDocument = Readonly<{
  lines: readonly string[];
  paper: ReceiptPaper;
  width: DailyCloseReceiptWidth;
  /** 需要加粗打印的行下标（营业日期、核对提示等）；缺省表示全部正常字重。 */
  emphasizedLines?: readonly number[];
}>;

export type DailyCloseReceiptInput = Readonly<{
  archive: DailyCloseArchive;
  locale: ReceiptLocale;
  paper: ReceiptPaper;
  reprint: boolean;
  brandName?: string | undefined;
  storeName: string;
  returnPolicy?: string | undefined;
}>;

const DENOMINATION_LABELS = new Map<number, string>([
  [10_000, "$100"],
  [5_000, "$50"],
  [2_000, "$20"],
  [1_000, "$10"],
  [500, "$5"],
  [200, "$2"],
  [100, "$1"],
  [50, "50c"],
  [20, "20c"],
  [10, "10c"],
  [5, "5c"],
]);

export function buildDailyCloseReceipt(
  input: DailyCloseReceiptInput,
): DailyCloseReceiptDocument {
  const zh = input.locale === "zh-CN";
  const width = receiptWidth(input.paper);
  const lines: string[] = [];
  const emphasized: number[] = [];
  appendCentered(
    lines,
    receiptStoreHeading(
      input.brandName ?? "",
      input.storeName,
      input.archive.storeCode,
    ),
    width,
  );
  appendCentered(
    lines,
    zh ? "日结 / DAILY CLOSE" : "DAILY CLOSE / 日结",
    width,
  );
  if (input.reprint) {
    appendCentered(
      lines,
      zh ? "*** 补打 / REPRINT ***" : "*** REPRINT / 补打 ***",
      width,
    );
  }
  appendBusinessDateBlock(lines, emphasized, input.archive, zh, width);
  appendWrapped(
    lines,
    `${zh ? "门店/Store" : "Store"}: ${storeDisplay(
      input.storeName,
      input.archive.storeCode,
    )}`,
    width,
  );
  appendWrapped(
    lines,
    `${zh ? "终端/Terminal" : "Terminal"}: ${cleanText(input.archive.deviceCode)}`,
    width,
  );
  appendWrapped(
    lines,
    `${zh ? "收银员/Cashier" : "Cashier"}: ${cleanText(input.archive.savedCashierName)}`,
    width,
  );
  lines.push(separator(width));
  const tenderWidths = width === DAILY_CLOSE_RECEIPT_NARROW_WIDTH
    ? [8, 8, 8, 8]
    : [12, 10, 10, 10];
  lines.push(
    columns(
      [
        zh ? "方式" : "METHOD",
        zh ? "销售" : "SALES",
        zh ? "退款" : "REFUND",
        zh ? "净额" : "NET",
      ],
      tenderWidths,
    ),
  );
  for (const method of ["cash", "card", "voucher"] as const) {
    const tender = input.archive.tenders.find(
      (candidate) => candidate.method === method,
    );
    if (!tender) {
      throw new TypeError("Daily close receipt requires all tender methods.");
    }
    lines.push(
      columns(
        [
          tenderLabel(method, zh),
          money(tender.salesCents),
          money(tender.refundCents),
          money(tender.netCents),
        ],
        tenderWidths,
      ),
    );
  }
  lines.push(separator(width));
  lines.push(
    twoColumns(
      zh ? "订单数/Orders" : "Orders",
      String(input.archive.orderCount),
      width,
    ),
  );
  lines.push(
    twoColumns(
      zh ? "退货数量/Return Qty" : "Return Qty",
      input.archive.returnQuantity,
      width,
    ),
  );
  // 现金进位（5 分进位）单独一行：Cash 行是订单精确金额，Expected = Cash 净额 + 进位（对齐 WPF #914）。
  lines.push(
    twoColumns(
      zh ? "现金进位/Rounding" : "Cash Rounding",
      signedMoney(input.archive.cashRoundingCents),
      width,
    ),
  );
  lines.push(
    twoColumns(
      zh ? "应有现金/Expected" : "Expected Cash",
      money(input.archive.expectedCashCents),
      width,
    ),
  );
  lines.push(
    twoColumns(
      zh ? "实点现金/Counted" : "Counted Cash",
      money(input.archive.countedCashCents),
      width,
    ),
  );
  lines.push(
    twoColumns(
      zh ? "差额/Variance" : "Variance",
      signedMoney(input.archive.varianceCents),
      width,
    ),
  );
  lines.push(
    twoColumns(
      zh ? "纸币/Notes" : "Notes",
      money(input.archive.notesSubtotalCents),
      width,
    ),
  );
  lines.push(
    twoColumns(
      zh ? "硬币/Coins" : "Coins",
      money(input.archive.coinsSubtotalCents),
      width,
    ),
  );
  lines.push(separator(width));
  lines.push(zh ? "面额明细 / DENOMINATIONS" : "DENOMINATIONS / 面额明细");
  appendDenominationLines(lines, input.archive, width);
  lines.push(separator(width));
  appendWrapped(
    lines,
    `${zh ? "归档/Archive" : "Archive"}: ${cleanText(input.archive.closeId)}`,
    width,
  );
  const returnPolicy = normalizedReturnPolicy(input.returnPolicy);
  if (returnPolicy) {
    lines.push(separator(width));
    lines.push(zh ? "退款与退货" : "Refunds and returns");
    for (const policyLine of returnPolicy.split("\n")) {
      appendWrapped(lines, policyLine.replaceAll("\t", " "), width);
    }
  }

  return Object.freeze({
    lines: Object.freeze(lines),
    paper: input.paper,
    width,
    emphasizedLines: Object.freeze(emphasized),
  });
}

/**
 * 现金单（对齐 WPF #478）：只含点钞面额明细、纸币/硬币合计与实盘金额，单独出一张纸随现金封存交接；
 * 不含销售汇总与长短款。日结单打完驱动会自动切纸，现金单作为第二个打印任务下发。
 */
export function buildDailyCloseCashSlip(
  input: DailyCloseReceiptInput,
): DailyCloseReceiptDocument {
  const zh = input.locale === "zh-CN";
  const width = receiptWidth(input.paper);
  const lines: string[] = [];
  const emphasized: number[] = [];
  appendCentered(
    lines,
    receiptStoreHeading(
      input.brandName ?? "",
      input.storeName,
      input.archive.storeCode,
    ),
    width,
  );
  appendCentered(
    lines,
    zh ? "日结现金单 / CASH SLIP" : "DAILY CLOSE CASH SLIP / 现金单",
    width,
  );
  if (input.reprint) {
    appendCentered(
      lines,
      zh ? "*** 补打 / REPRINT ***" : "*** REPRINT / 补打 ***",
      width,
    );
  }
  appendBusinessDateBlock(lines, emphasized, input.archive, zh, width);
  appendWrapped(
    lines,
    `${zh ? "门店/Store" : "Store"}: ${storeDisplay(
      input.storeName,
      input.archive.storeCode,
    )}`,
    width,
  );
  appendWrapped(
    lines,
    `${zh ? "终端/Terminal" : "Terminal"}: ${cleanText(input.archive.deviceCode)}`,
    width,
  );
  appendWrapped(
    lines,
    `${zh ? "归档/Archive" : "Archive"}: ${cleanText(input.archive.closeId)}`,
    width,
  );
  lines.push(separator(width));
  lines.push(zh ? "面额明细 / DENOMINATIONS" : "DENOMINATIONS / 面额明细");
  appendDenominationLines(lines, input.archive, width);
  lines.push(
    twoColumns(
      zh ? "纸币/Notes" : "Notes",
      money(input.archive.notesSubtotalCents),
      width,
    ),
  );
  lines.push(
    twoColumns(
      zh ? "硬币/Coins" : "Coins",
      money(input.archive.coinsSubtotalCents),
      width,
    ),
  );
  lines.push(separator(width));
  emphasized.push(lines.length);
  lines.push(
    twoColumns(
      zh ? "实点现金/Cash Counted" : "Cash Counted",
      money(input.archive.countedCashCents),
      width,
    ),
  );
  lines.push(separator(width));
  emphasized.push(lines.length);
  appendCentered(lines, "KEEP THIS SLIP", width);
  emphasized.push(lines.length);
  appendCentered(lines, "WITH THE CASH", width);
  if (zh) appendCentered(lines, "请随现金一同保存", width);
  lines.push("");
  lines.push(signatureLine(zh ? "点钞人/Counted by: " : "Counted by: ", width));
  lines.push("");
  lines.push(signatureLine(zh ? "复核人/Checked by: " : "Checked by: ", width));

  return Object.freeze({
    lines: Object.freeze(lines),
    paper: input.paper,
    width,
    emphasizedLines: Object.freeze(emphasized),
  });
}

export function dailyCloseReceiptToEscPosBytes(
  document: DailyCloseReceiptDocument,
): Uint8Array {
  const expectedWidth = receiptWidth(document.paper);
  if (
    document.width !== expectedWidth ||
    (document.paper !== "58mm" && document.paper !== "80mm") ||
    document.lines.length === 0 ||
    document.lines.length > 1_000
  ) {
    throw new TypeError("Daily close receipt document is invalid.");
  }
  const output: number[] = [];
  appendEscPosInitialize(output);
  const emphasized = new Set(document.emphasizedLines ?? []);
  for (const [index, line] of document.lines.entries()) {
    if (
      typeof line !== "string" ||
      /[\u0000-\u0009\u000b-\u001f\u007f]/u.test(line) ||
      dailyCloseReceiptDisplayWidth(line) > document.width
    ) {
      throw new TypeError("Daily close receipt line is invalid.");
    }
    // ESC E n：营业日期、核对提示、实盘金额等加粗行；其余行显式关闭加粗，避免状态串到下一行。
    output.push(0x1b, 0x45, emphasized.has(index) ? 1 : 0);
    output.push(...encodeEscPosText(line), 0x0a);
  }
  output.push(0x1b, 0x45, 0x00, 0x1b, 0x64, 0x03, 0x1d, 0x56, 0x00);
  if (output.length > 256 * 1_024) {
    throw new RangeError("Daily close receipt is too large.");
  }
  return Uint8Array.from(output);
}

export function dailyCloseReceiptDisplayWidth(value: string): number {
  return [...value].reduce(
    (width, character) => width + (isWide(character) ? 2 : 1),
    0,
  );
}

/** 每种面额一行：面额、张数/枚数、小计。日结单与现金单共用。 */
function appendDenominationLines(
  lines: string[],
  archive: DailyCloseArchive,
  width: DailyCloseReceiptWidth,
): void {
  const denominationWidths = width === DAILY_CLOSE_RECEIPT_NARROW_WIDTH
    ? [8, 8, 16]
    : [12, 10, 20];
  for (const denomination of archive.denominations) {
    const label = DENOMINATION_LABELS.get(denomination.denominationCents);
    if (!label) {
      throw new TypeError("Daily close receipt denomination is invalid.");
    }
    lines.push(
      columns(
        [
          label,
          `x${denomination.quantity}`,
          money(denomination.subtotalCents),
        ],
        denominationWidths,
      ),
    );
  }
}

/**
 * 营业日期单独成块并加粗（对齐 WPF #478）；保存当天（设备本地日期）与营业日期不一致时追加核对提示，
 * 例如隔夜未重启按旧日期日结，店长一眼就能发现。
 */
function appendBusinessDateBlock(
  lines: string[],
  emphasized: number[],
  archive: DailyCloseArchive,
  zh: boolean,
  width: DailyCloseReceiptWidth,
): void {
  lines.push(separator(width));
  appendCentered(lines, zh ? "营业日期 / BUSINESS DATE" : "BUSINESS DATE", width);
  emphasized.push(lines.length);
  appendCentered(lines, formatBusinessDate(archive.businessDate), width);
  const savedDate = localDateKey(archive.savedAtIso);
  if (savedDate !== null && savedDate !== archive.businessDate) {
    emphasized.push(lines.length);
    appendCentered(lines, zh ? "** 请核对日期 / CHECK DATE **" : "** CHECK DATE **", width);
    appendCentered(
      lines,
      zh
        ? `保存于 ${savedDate}，不是营业日期`
        : `Saved on ${savedDate}, not the business date`,
      width,
    );
  }
  lines.push(separator(width));
}

const WEEKDAY_LABELS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

/** yyyy-MM-dd + 星期缩写；日期不合法时原样返回，不抛错阻断打印。 */
function formatBusinessDate(value: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/u.exec(value);
  if (!match) return cleanText(value);
  const date = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])));
  return `${value} ${WEEKDAY_LABELS[date.getUTCDay()] ?? ""}`.trim();
}

/** 保存时间转设备本地日期（yyyy-MM-dd）；时间非法返回 null（不提示）。 */
function localDateKey(iso: string): string | null {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return null;
  const pad = (part: number) => String(part).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/** 签名栏：标签后用下划线补满当前纸宽（58mm 与 80mm 都不超宽）。 */
function signatureLine(label: string, width: DailyCloseReceiptWidth): string {
  return `${label}${"_".repeat(Math.max(4, width - dailyCloseReceiptDisplayWidth(label)))}`;
}

function appendCentered(
  lines: string[],
  value: string,
  width: DailyCloseReceiptWidth,
): void {
  for (const line of wrap(value, width)) {
    const padding = Math.max(
      0,
      Math.floor(
        (width - dailyCloseReceiptDisplayWidth(line)) / 2,
      ),
    );
    lines.push(`${" ".repeat(padding)}${line}`);
  }
}

function appendWrapped(
  lines: string[],
  value: string,
  width: DailyCloseReceiptWidth,
): void {
  lines.push(...wrap(value, width));
}

function wrap(
  value: string,
  width: DailyCloseReceiptWidth,
): readonly string[] {
  const result: string[] = [];
  let current = "";
  for (const character of [...cleanText(value)]) {
    if (
      dailyCloseReceiptDisplayWidth(current) +
        dailyCloseReceiptDisplayWidth(character) >
      width
    ) {
      result.push(current);
      current = "";
    }
    current += character;
  }
  if (current || result.length === 0) result.push(current);
  return result;
}

function columns(values: readonly string[], widths: readonly number[]): string {
  if (values.length !== widths.length) {
    throw new TypeError("Daily close receipt columns are invalid.");
  }
  return values
    .map((value, index) => {
      const width = widths[index] ?? 0;
      const fitted = trimToWidth(value, width);
      return index === 0
        ? padRight(fitted, width)
        : padLeft(fitted, width);
    })
    .join("");
}

function twoColumns(
  left: string,
  right: string,
  width: DailyCloseReceiptWidth,
): string {
  const fittedRight = trimToWidth(
    right,
    width === DAILY_CLOSE_RECEIPT_NARROW_WIDTH ? 10 : 14,
  );
  const leftWidth =
    width -
    dailyCloseReceiptDisplayWidth(fittedRight) -
    1;
  const fittedLeft = trimToWidth(left, leftWidth);
  return `${fittedLeft}${" ".repeat(
    width -
      dailyCloseReceiptDisplayWidth(fittedLeft) -
      dailyCloseReceiptDisplayWidth(fittedRight),
  )}${fittedRight}`;
}

function padLeft(value: string, width: number): string {
  return `${" ".repeat(
    Math.max(0, width - dailyCloseReceiptDisplayWidth(value)),
  )}${value}`;
}

function padRight(value: string, width: number): string {
  return `${value}${" ".repeat(
    Math.max(0, width - dailyCloseReceiptDisplayWidth(value)),
  )}`;
}

function trimToWidth(value: string, width: number): string {
  let result = "";
  for (const character of [...cleanText(value)]) {
    if (
      dailyCloseReceiptDisplayWidth(result) +
        dailyCloseReceiptDisplayWidth(character) >
      width
    ) {
      break;
    }
    result += character;
  }
  return result;
}

function cleanText(value: string): string {
  return value.replace(/[\u0000-\u001f\u007f]/g, " ").trim();
}

function normalizedReturnPolicy(value: string | undefined): string | null {
  if (value === undefined) return null;
  if (
    value.length > 500 ||
    /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/u.test(value)
  ) {
    throw new TypeError("Daily close return policy is invalid.");
  }
  const normalized = value.replaceAll("\r\n", "\n").replaceAll("\r", "\n").trim();
  return normalized || null;
}

function storeDisplay(storeName: string, storeCode: string): string {
  const name = cleanText(storeName);
  const code = cleanText(storeCode);
  return !name || name.toLocaleLowerCase() === code.toLocaleLowerCase()
    ? code
    : `${name} (${code})`;
}

function separator(width: DailyCloseReceiptWidth): string {
  return "-".repeat(width);
}

function receiptWidth(paper: ReceiptPaper): DailyCloseReceiptWidth {
  return paper === "58mm"
    ? DAILY_CLOSE_RECEIPT_NARROW_WIDTH
    : DAILY_CLOSE_RECEIPT_WIDTH;
}

function tenderLabel(
  method: "cash" | "card" | "voucher",
  zh: boolean,
): string {
  if (!zh) {
    return { cash: "Cash", card: "Card", voucher: "Voucher" }[method];
  }
  return { cash: "现金", card: "银行卡", voucher: "代金券" }[method];
}

function money(cents: number): string {
  const value = assertCents(cents);
  const sign = value < 0 ? "-" : "";
  const absolute = Math.abs(value);
  return `${sign}$${Math.floor(absolute / 100)}.${String(
    absolute % 100,
  ).padStart(2, "0")}`;
}

function signedMoney(cents: number): string {
  return cents > 0 ? `+${money(cents)}` : money(cents);
}

function assertCents(value: number): number {
  if (!Number.isSafeInteger(value)) {
    throw new TypeError("Daily close receipt amount must use integer cents.");
  }
  return value;
}

function isWide(character: string): boolean {
  const codePoint = character.codePointAt(0) ?? 0;
  return (
    codePoint >= 0x1100 &&
    (codePoint <= 0x115f ||
      codePoint >= 0x2e80 ||
      codePoint >= 0x1f300)
  );
}

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
  appendWrapped(
    lines,
    `${zh ? "营业日/Date" : "Business Date"}: ${input.archive.businessDate}`,
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
  const denominationWidths = width === DAILY_CLOSE_RECEIPT_NARROW_WIDTH
    ? [8, 8, 16]
    : [12, 10, 20];
  for (const denomination of input.archive.denominations) {
    const label = DENOMINATION_LABELS.get(
      denomination.denominationCents,
    );
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
  for (const line of document.lines) {
    if (
      typeof line !== "string" ||
      /[\u0000-\u0009\u000b-\u001f\u007f]/u.test(line) ||
      dailyCloseReceiptDisplayWidth(line) > document.width
    ) {
      throw new TypeError("Daily close receipt line is invalid.");
    }
    output.push(...encodeEscPosText(line), 0x0a);
  }
  output.push(0x1b, 0x64, 0x03, 0x1d, 0x56, 0x00);
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

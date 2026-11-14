import type {
  CashMovementReasonCode,
  LocalCashMovement,
} from "@hb/pos-domain/core/contracts/cash-movement";

import {
  DAILY_CLOSE_RECEIPT_NARROW_WIDTH,
  DAILY_CLOSE_RECEIPT_WIDTH,
  dailyCloseReceiptDisplayWidth,
  dailyCloseReceiptToEscPosBytes,
  type DailyCloseReceiptDocument,
} from "./daily-close-receipt";
import { receiptStoreHeading, type ReceiptPaper } from "./receipt-document";

/**
 * 现金进出凭条（每笔 1 张，英文，纯文本 ESC/POS，80mm 为 42 列、58mm 为 32 列）。
 * 凭条随钱箱保存（KEEP THIS SLIP IN THE DRAWER），所以只放事实：原因、金额、备注、设备、收银员、
 * 授权人、时间、流水号、当日累计，以及签名栏（SafeDrop 另有店长签收栏）。
 * 版式 helper（居中、两列、分隔线、换行）在本文件自己实现，与日结回单互不依赖，
 * 只复用日结回单的 ESC/POS 编码函数（逐行校验宽度与控制字符、末尾走纸切刀）。
 */
export type CashMovementReceiptDocument = DailyCloseReceiptDocument;

export const CASH_MOVEMENT_REASON_LABELS: Readonly<Record<CashMovementReasonCode, string>> =
  Object.freeze({
    ChangeTopUp: "Change top-up",
    OtherIn: "Other cash in",
    SafeDrop: "Safe drop",
    PaidOut: "Paid out",
  });

export type CashMovementReceiptInput = Readonly<{
  movement: Pick<
    LocalCashMovement,
    | "direction"
    | "reasonCode"
    | "amountCents"
    | "note"
    | "voidsMovementGuid"
    | "occurredAtIso"
    | "cashierName"
    | "authorizedByName"
    | "displayNumber"
    | "deviceCode"
    | "storeCode"
  >;
  /** 冲正凭条必须带上被冲正的原记录（Voids 流水号与 Original 信息）。 */
  voidedOriginal?:
    | Readonly<{
        displayNumber: string;
        reasonCode: CashMovementReasonCode;
        amountCents: number;
        occurredAtIso: string;
      }>
    | null
    | undefined;
  /** 当日（营业日）现金存入 / 取出合计，冲正抵消后的净额；打印时已包含本笔。 */
  todayCashInCents: number;
  todayCashOutCents: number;
  paper: ReceiptPaper;
  /** 补打：抬头加 REPRINT。 */
  reprint: boolean;
  brandName?: string | undefined;
  storeName: string;
  /** 门店营业时区（IANA），Time 行按它显示，与营业日口径一致。 */
  businessTimeZone: string;
}>;

type Width = typeof DAILY_CLOSE_RECEIPT_NARROW_WIDTH | typeof DAILY_CLOSE_RECEIPT_WIDTH;

export function buildCashMovementReceipt(
  input: CashMovementReceiptInput,
): CashMovementReceiptDocument {
  const { movement } = input;
  const width: Width =
    input.paper === "58mm" ? DAILY_CLOSE_RECEIPT_NARROW_WIDTH : DAILY_CLOSE_RECEIPT_WIDTH;
  const isVoid = movement.voidsMovementGuid !== null;
  if (isVoid && !input.voidedOriginal) {
    throw new TypeError("Cash movement void receipt requires the original movement.");
  }
  const lines: string[] = [];
  const emphasized: number[] = [];

  appendCentered(
    lines,
    receiptStoreHeading(input.brandName ?? "", input.storeName, movement.storeCode),
    width,
  );
  // 有品牌名时抬头是品牌，再补一行店名，凭条才看得出是哪家店（与品牌名相同则不重复）。
  const brand = clean(input.brandName ?? "");
  const store = clean(input.storeName);
  if (brand.length > 0 && store.length > 0 && store.toLowerCase() !== brand.toLowerCase()) {
    appendCentered(lines, store, width);
  }
  emphasized.push(lines.length);
  appendCentered(
    lines,
    isVoid
      ? "==== CASH MOVEMENT VOID ===="
      : movement.direction === "In"
        ? "==== CASH IN ===="
        : "==== CASH OUT ====",
    width,
  );
  if (input.reprint) appendCentered(lines, "*** REPRINT ***", width);
  lines.push(separator(width));

  if (isVoid && input.voidedOriginal) {
    const original = input.voidedOriginal;
    appendWrapped(lines, `Voids: ${clean(original.displayNumber)}`, width);
    appendWrapped(
      lines,
      `Original: ${CASH_MOVEMENT_REASON_LABELS[original.reasonCode]} ${money(original.amountCents)}`,
      width,
    );
    appendWrapped(
      lines,
      `Original time: ${formatTime(original.occurredAtIso, input.businessTimeZone)}`,
      width,
    );
  } else {
    appendWrapped(lines, `Reason: ${CASH_MOVEMENT_REASON_LABELS[movement.reasonCode]}`, width);
  }
  emphasized.push(lines.length);
  lines.push(twoColumns("Amount", money(movement.amountCents), width));
  // 冲正方向与原记录相反，明确写出这张凭条对钱箱的实际影响。
  appendWrapped(
    lines,
    `Direction: ${movement.direction === "In" ? "Cash in" : "Cash out"}${isVoid ? " (void)" : ""}`,
    width,
  );
  if (movement.note !== null && clean(movement.note).length > 0) {
    appendWrapped(lines, `Note: ${clean(movement.note)}`, width);
  }
  appendWrapped(lines, `Till: ${clean(movement.deviceCode)}`, width);
  appendWrapped(lines, `Cashier: ${clean(movement.cashierName)}`, width);
  if (movement.authorizedByName !== null) {
    appendWrapped(lines, `Authorised: ${clean(movement.authorizedByName)}`, width);
  }
  appendWrapped(lines, `Time: ${formatTime(movement.occurredAtIso, input.businessTimeZone)}`, width);
  appendWrapped(lines, `Ref: ${clean(movement.displayNumber)}`, width);
  lines.push(separator(width));
  lines.push(twoColumns("Today cash in", money(input.todayCashInCents), width));
  lines.push(twoColumns("Today cash out", money(input.todayCashOutCents), width));
  lines.push(separator(width));

  lines.push(signatureLine("Cashier sign: ", width));
  // 交店长：店长在凭条上签收，凭条随现金一起保管。冲正不需要。
  if (!isVoid && movement.reasonCode === "SafeDrop") {
    lines.push("");
    lines.push(signatureLine("Received by: ", width));
  }
  lines.push(separator(width));
  emphasized.push(lines.length);
  appendCentered(lines, "KEEP THIS SLIP IN THE DRAWER", width);

  return Object.freeze({
    lines: Object.freeze(lines),
    paper: input.paper,
    width,
    emphasizedLines: Object.freeze(emphasized),
  });
}

/** ESC/POS 字节：与日结回单同一编码器（逐行校验宽度与控制字符，末尾走纸切刀）。 */
export function cashMovementReceiptToEscPosBytes(
  document: CashMovementReceiptDocument,
): Uint8Array {
  return dailyCloseReceiptToEscPosBytes(document);
}

/** yyyy-MM-dd HH:mm（门店营业时区）。时间或时区非法时抛错，不打印错误的时间。 */
function formatTime(iso: string, timeZone: string): string {
  const instant = Date.parse(iso);
  if (!Number.isFinite(instant)) {
    throw new TypeError("Cash movement receipt time is invalid.");
  }
  const parts = new Intl.DateTimeFormat("en-CA", {
    calendar: "gregory",
    day: "2-digit",
    hour: "2-digit",
    hourCycle: "h23",
    minute: "2-digit",
    month: "2-digit",
    numberingSystem: "latn",
    timeZone,
    year: "numeric",
  }).formatToParts(new Date(instant));
  const value = (type: string) => parts.find((part) => part.type === type)?.value ?? "";
  return `${value("year")}-${value("month")}-${value("day")} ${value("hour")}:${value("minute")}`;
}

function money(cents: number): string {
  if (!Number.isSafeInteger(cents)) {
    throw new TypeError("Cash movement receipt amount must use integer cents.");
  }
  const sign = cents < 0 ? "-" : "";
  const absolute = Math.abs(cents);
  return `${sign}$${Math.floor(absolute / 100)}.${String(absolute % 100).padStart(2, "0")}`;
}

function clean(value: string): string {
  return value.replace(/[\u0000-\u001f\u007f]/g, " ").trim();
}

function separator(width: Width): string {
  return "-".repeat(width);
}

/** 签名栏：标签后用下划线补满当前纸宽。 */
function signatureLine(label: string, width: Width): string {
  return `${label}${"_".repeat(Math.max(4, width - dailyCloseReceiptDisplayWidth(label)))}`;
}

function appendCentered(lines: string[], value: string, width: Width): void {
  for (const line of wrap(value, width)) {
    const padding = Math.max(0, Math.floor((width - dailyCloseReceiptDisplayWidth(line)) / 2));
    lines.push(`${" ".repeat(padding)}${line}`);
  }
}

function appendWrapped(lines: string[], value: string, width: Width): void {
  lines.push(...wrap(value, width));
}

function wrap(value: string, width: Width): readonly string[] {
  const result: string[] = [];
  let current = "";
  for (const character of [...clean(value)]) {
    if (
      dailyCloseReceiptDisplayWidth(current) + dailyCloseReceiptDisplayWidth(character) >
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

function twoColumns(left: string, right: string, width: Width): string {
  const rightWidth = dailyCloseReceiptDisplayWidth(right);
  const leftBudget = Math.max(0, width - rightWidth - 1);
  let fittedLeft = "";
  for (const character of [...clean(left)]) {
    if (
      dailyCloseReceiptDisplayWidth(fittedLeft) + dailyCloseReceiptDisplayWidth(character) >
      leftBudget
    ) {
      break;
    }
    fittedLeft += character;
  }
  const gap = width - dailyCloseReceiptDisplayWidth(fittedLeft) - rightWidth;
  return `${fittedLeft}${" ".repeat(Math.max(1, gap))}${right}`;
}

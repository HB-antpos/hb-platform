import {
  appendEscPosInitialize,
  encodeEscPosText,
} from "./esc-pos-text-encoding";
import { receiptStoreHeading } from "./receipt-document";
import type {
  FrozenReturnReceiptSettings,
  RenderedReturnReceipt,
  ReturnReceiptSettingsPort,
} from "./return-receipt-renderer";

import type { LocalOrder } from "@hb/pos-domain/core/contracts/order";
import type { OrderRepositoryPort } from "@hb/pos-domain/core/contracts/repositories";

export type ProtectedRefundVoucherPrintMaterial = Readonly<{
  returnOrderGuid: string;
  voucherCode: string;
  /** 正数整数分币；必须与一笔 voucher tender 的绝对值完全一致。 */
  refundAmountCents: number;
}>;

export interface ProtectedRefundVoucherPrintMaterialPort {
  /**
   * 实现只能从二次加密的已批准 voucher state 解析材料，且不得缓存、记录或
   * 返回到 route/UI。退货单中每笔 voucher tender 对应一份材料；任何绑定缺失或
   * 不唯一时返回 null，由渲染器失败关闭。
   */
  resolveApprovedRefundVouchers(
    actionId: string,
    returnOrderGuid: string,
  ): Promise<readonly ProtectedRefundVoucherPrintMaterial[] | null>;
}

/** 退货单是否含新签发的退款券（负数 voucher tender），供组合根决定是否追加券面。 */
export function hasRefundVoucherTender(order: LocalOrder | null): boolean {
  return (
    order !== null &&
    order.tenders.some(
      (tender) => tender.method === "voucher" && tender.amount.cents < 0,
    )
  );
}

/**
 * WPF 等价的独立退款券面，每张退款券一张（各自切纸，放在同一冻结 print job）。
 * 普通本地订单只有脱敏 tender；券码仅在本方法内短暂解密，并直接编码为打印字节，
 * 不进入普通 receipt document 或日志。混合退款（现金/刷卡 + 退款券）同样适用。
 */
export class ProtectedRefundVoucherReceiptRenderer {
  public constructor(
    private readonly orders: Pick<OrderRepositoryPort, "getByGuid">,
    private readonly materials: ProtectedRefundVoucherPrintMaterialPort,
    private readonly settings: ReturnReceiptSettingsPort,
    private readonly now: () => Date,
  ) {}

  public async render(
    actionIdInput: string,
    returnOrderGuidInput: string,
  ): Promise<RenderedReturnReceipt> {
    const actionId = safeText(
      actionIdInput,
      "REFUND_VOUCHER_ACTION_ID_INVALID",
      128,
    );
    const returnOrderGuid = safeText(
      returnOrderGuidInput,
      "REFUND_VOUCHER_ORDER_ID_INVALID",
      128,
    );
    const loaded = await this.orders.getByGuid(returnOrderGuid);
    const { order, voucherTenderCents } = assertVoucherRefundReturn(loaded, returnOrderGuid);

    const [materials, settings] = await Promise.all([
      this.materials.resolveApprovedRefundVouchers(
        actionId,
        returnOrderGuid,
      ),
      this.settings.getFrozenReturnReceiptSettings(),
    ]);
    if (!materials || materials.length !== voucherTenderCents.length) {
      throw new Error("REFUND_VOUCHER_MATERIAL_INVALID");
    }
    const normalizedMaterials = materials.map((material) =>
      normalizeMaterial(material, returnOrderGuid),
    );
    // 券面金额必须与 voucher tender 逐笔一一对应（按金额多重集合比对）。
    const expected = [...voucherTenderCents].sort((left, right) => left - right);
    const actual = normalizedMaterials
      .map((material) => material.refundAmountCents)
      .sort((left, right) => left - right);
    if (expected.some((cents, index) => cents !== actual[index])) {
      throw new Error("REFUND_VOUCHER_AMOUNT_MISMATCH");
    }

    return encodeRefundVoucherDocuments({
      settings,
      storeCode: order.storeCode,
      orderLabel: returnOrderGuid,
      vouchers: normalizedMaterials.map((material) => ({
        voucherCode: material.voucherCode,
        amountCents: material.refundAmountCents,
      })),
      printedAt: this.now(),
    });
  }
}

/**
 * 退款券面公共编码：每张券一段（各自切纸）拼入同一冻结 print job。
 * 退货与取消分期共用；券码必须是可打印 ASCII，金额为正整数分。
 */
export function encodeRefundVoucherDocuments(input: Readonly<{
  settings: FrozenReturnReceiptSettings | null;
  storeCode: string;
  orderLabel: string;
  vouchers: readonly Readonly<{ voucherCode: string; amountCents: number }>[];
  printedAt: Date;
}>): RenderedReturnReceipt {
  const settings = normalizeSettings(input.settings);
  const orderLabel = safeText(input.orderLabel, "REFUND_VOUCHER_ORDER_ID_INVALID", 128);
  if (input.vouchers.length === 0) {
    throw new Error("REFUND_VOUCHER_MATERIAL_INVALID");
  }
  const printedAt = formatLocalDateTime(input.printedAt);
  const heading = receiptStoreHeading(
    settings.store.brandName,
    settings.store.storeName,
    input.storeCode,
  );
  const returnPolicy = normalizedReturnPolicy(settings.store.returnPolicy);
  const documents = input.vouchers.map((voucher) => {
    const voucherCode = normalizeVoucherCode(voucher.voucherCode);
    if (!Number.isSafeInteger(voucher.amountCents) || voucher.amountCents <= 0) {
      throw new Error("REFUND_VOUCHER_AMOUNT_INVALID");
    }
    return encodeRefundVoucher({
      paper: settings.paper,
      locale: settings.locale,
      orderGuid: orderLabel,
      voucherCode,
      amountCents: voucher.amountCents,
      printedAt,
      heading,
      returnPolicy,
    });
  });
  return {
    printerId: settings.printerId,
    receiptBytes: concatBytes(documents),
  };
}

function concatBytes(parts: readonly Uint8Array[]): Uint8Array {
  const total = parts.reduce((sum, part) => sum + part.byteLength, 0);
  const output = new Uint8Array(total);
  let offset = 0;
  for (const part of parts) {
    output.set(part, offset);
    offset += part.byteLength;
  }
  return output;
}

/** 校验退货单并返回每笔 voucher tender 的退款金额（正数分）。 */
function assertVoucherRefundReturn(
  order: LocalOrder | null,
  returnOrderGuid: string,
): Readonly<{ order: LocalOrder; voucherTenderCents: number[] }> {
  if (
    !order ||
    order.orderGuid !== returnOrderGuid ||
    !isCompletedState(order.state) ||
    order.total.currency !== "AUD" ||
    order.actualAmount.currency !== "AUD" ||
    order.discount.currency !== "AUD" ||
    !Number.isSafeInteger(order.total.cents) ||
    !Number.isSafeInteger(order.actualAmount.cents) ||
    order.total.cents >= 0 ||
    order.total.cents !== order.actualAmount.cents ||
    order.discount.cents !== 0 ||
    order.lines.length === 0 ||
    order.tenders.length === 0
  ) {
    throw new Error("REFUND_VOUCHER_ORDER_INVALID");
  }
  let tenderTotal = 0;
  const voucherCents: number[] = [];
  for (const tender of order.tenders) {
    if (
      tender.amount.currency !== "AUD" ||
      !Number.isSafeInteger(tender.amount.cents) ||
      tender.amount.cents >= 0
    ) {
      throw new Error("REFUND_VOUCHER_ORDER_INVALID");
    }
    tenderTotal += tender.amount.cents;
    if (tender.method === "voucher") voucherCents.push(-tender.amount.cents);
  }
  if (
    voucherCents.length === 0 ||
    !Number.isSafeInteger(tenderTotal) ||
    tenderTotal !== order.actualAmount.cents
  ) {
    throw new Error("REFUND_VOUCHER_ORDER_INVALID");
  }
  return { order, voucherTenderCents: voucherCents };
}

function normalizeMaterial(
  material: ProtectedRefundVoucherPrintMaterial | null,
  returnOrderGuid: string,
): ProtectedRefundVoucherPrintMaterial {
  if (
    !material ||
    material.returnOrderGuid !== returnOrderGuid ||
    !Number.isSafeInteger(material.refundAmountCents) ||
    material.refundAmountCents <= 0
  ) {
    throw new Error("REFUND_VOUCHER_MATERIAL_INVALID");
  }
  return {
    returnOrderGuid,
    voucherCode: normalizeVoucherCode(material.voucherCode),
    refundAmountCents: material.refundAmountCents,
  };
}

function normalizeVoucherCode(value: unknown): string {
  const voucherCode = safeText(value, "REFUND_VOUCHER_CODE_INVALID", 80);
  // CODE128/QR 仅接受可打印 ASCII；控制字符和 ESC/POS 注入一律拒绝。
  if (!/^[\x20-\x7e]+$/u.test(voucherCode)) {
    throw new Error("REFUND_VOUCHER_CODE_INVALID");
  }
  return voucherCode;
}

function normalizeSettings(
  settings: FrozenReturnReceiptSettings | null,
): FrozenReturnReceiptSettings {
  if (
    !settings ||
    !/^[A-Za-z0-9._:-]{1,128}$/u.test(settings.printerId) ||
    (settings.paper !== "58mm" && settings.paper !== "80mm") ||
    (settings.locale !== "en" && settings.locale !== "zh-CN") ||
    !settings.store
  ) {
    throw new Error("REFUND_VOUCHER_SETTINGS_MISSING");
  }
  normalizedReturnPolicy(settings.store.returnPolicy);
  return settings;
}

function encodeRefundVoucher(input: Readonly<{
  paper: "58mm" | "80mm";
  locale: "en" | "zh-CN";
  orderGuid: string;
  voucherCode: string;
  amountCents: number;
  printedAt: string;
  heading: string;
  returnPolicy: string | null;
}>): Uint8Array {
  const bytes: number[] = [];
  appendEscPosInitialize(bytes);
  const width = input.paper === "58mm" ? 32 : 48;
  const title =
    input.locale === "zh-CN" ? "===== 退款券 =====" : "===== REFUND VOUCHER =====";
  appendWrappedText(bytes, input.heading, width, "center", true);
  appendText(bytes, "", "left", false);
  appendText(bytes, title, "center", true);
  appendText(bytes, "", "left", false);
  appendWrappedText(
    bytes,
    `${input.locale === "zh-CN" ? "券码" : "Voucher"}: ${input.voucherCode}`,
    width,
    "center",
    true,
  );
  appendText(
    bytes,
    `${input.locale === "zh-CN" ? "金额" : "Amount"}: ${money(input.amountCents)}`,
    "center",
    true,
  );
  appendText(bytes, "-".repeat(width), "left", false);
  appendWrappedText(
    bytes,
    `${input.locale === "zh-CN" ? "订单" : "Order"}: ${input.orderGuid}`,
    width,
    "left",
    false,
  );
  appendText(
    bytes,
    `${input.locale === "zh-CN" ? "打印时间" : "Print Time"}: ${input.printedAt}`,
    "left",
    false,
  );
  appendReturnPolicy(
    bytes,
    input.returnPolicy,
    width,
    input.locale,
  );
  appendCode128(bytes, input.voucherCode);
  appendQrCode(bytes, input.voucherCode);
  bytes.push(0x1b, 0x64, 0x03);
  // 芯烨 ESC/POS 全切；每个冻结 print job 只包含一次切纸，避免 adapter 猜测。
  bytes.push(0x1d, 0x56, 0x00);
  return Uint8Array.from(bytes);
}

function appendText(
  output: number[],
  value: string,
  alignment: "left" | "center" | "right",
  bold: boolean,
): void {
  const align = alignment === "center" ? 1 : alignment === "right" ? 2 : 0;
  output.push(0x1b, 0x61, align, 0x1b, 0x45, bold ? 1 : 0);
  output.push(...encodeEscPosText(value), 0x0a);
}

function appendWrappedText(
  output: number[],
  value: string,
  width: number,
  alignment: "left" | "center" | "right",
  bold: boolean,
): void {
  let line = "";
  for (const character of value) {
    if (line.length >= width) {
      appendText(output, line, alignment, bold);
      line = "";
    }
    line += character;
  }
  appendText(output, line, alignment, bold);
}

function appendReturnPolicy(
  output: number[],
  returnPolicy: string | null,
  width: number,
  locale: "en" | "zh-CN",
): void {
  if (!returnPolicy) return;
  appendText(output, "-".repeat(width), "left", false);
  appendText(
    output,
    locale === "zh-CN" ? "退款与退货" : "Refunds and returns",
    "left",
    true,
  );
  for (const sourceLine of returnPolicy.split("\n")) {
    appendReceiptWrappedText(
      output,
      sourceLine.replaceAll("\t", " "),
      width,
      "left",
      false,
    );
  }
}

function appendReceiptWrappedText(
  output: number[],
  value: string,
  width: number,
  alignment: "left" | "center" | "right",
  bold: boolean,
): void {
  let line = "";
  let lineWidth = 0;
  for (const character of [...value]) {
    const characterWidth = receiptCharacterWidth(character);
    if (line && lineWidth + characterWidth > width) {
      appendText(output, line, alignment, bold);
      line = "";
      lineWidth = 0;
    }
    line += character;
    lineWidth += characterWidth;
  }
  appendText(output, line, alignment, bold);
}

function receiptCharacterWidth(character: string): number {
  const codePoint = character.codePointAt(0) ?? 0;
  return codePoint >= 0x1100 &&
    (codePoint <= 0x115f || codePoint >= 0x2e80 || codePoint >= 0x1f300)
    ? 2
    : 1;
}

function normalizedReturnPolicy(value: unknown): string | null {
  if (
    typeof value !== "string" ||
    value.length > 500 ||
    /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/u.test(value)
  ) {
    throw new Error("REFUND_VOUCHER_SETTINGS_MISSING");
  }
  const normalized = value.replaceAll("\r\n", "\n").replaceAll("\r", "\n").trim();
  return normalized || null;
}

function appendCode128(output: number[], voucherCode: string): void {
  // CODE128 的 { 是控制前缀；字面左花括号必须双写，并计入 payload 长度。
  const escapedVoucherCode = voucherCode.replaceAll("{", "{{");
  const data = new TextEncoder().encode(`{B${escapedVoucherCode}`);
  if (data.byteLength > 255) {
    throw new Error("REFUND_VOUCHER_CODE_INVALID");
  }
  output.push(
    0x1b,
    0x61,
    0x01,
    0x1d,
    0x48,
    0x02,
    0x1d,
    0x68,
    0x50,
    0x1d,
    0x77,
    0x02,
    0x1d,
    0x6b,
    0x49,
    data.byteLength,
    ...data,
    0x0a,
  );
}

function appendQrCode(output: number[], voucherCode: string): void {
  const data = new TextEncoder().encode(voucherCode);
  const storeLength = data.byteLength + 3;
  const pL = storeLength & 0xff;
  const pH = (storeLength >> 8) & 0xff;
  output.push(
    0x1d,
    0x28,
    0x6b,
    0x04,
    0x00,
    0x31,
    0x41,
    0x32,
    0x00,
    0x1d,
    0x28,
    0x6b,
    0x03,
    0x00,
    0x31,
    0x43,
    0x05,
    0x1d,
    0x28,
    0x6b,
    0x03,
    0x00,
    0x31,
    0x45,
    0x31,
    0x1d,
    0x28,
    0x6b,
    pL,
    pH,
    0x31,
    0x50,
    0x30,
    ...data,
    0x1d,
    0x28,
    0x6b,
    0x03,
    0x00,
    0x31,
    0x51,
    0x30,
  );
}

function money(cents: number): string {
  if (!Number.isSafeInteger(cents) || cents <= 0) {
    throw new Error("REFUND_VOUCHER_AMOUNT_INVALID");
  }
  return `$${Math.floor(cents / 100)}.${String(cents % 100).padStart(2, "0")}`;
}

function formatLocalDateTime(value: Date): string {
  if (!(value instanceof Date) || !Number.isFinite(value.getTime())) {
    throw new Error("REFUND_VOUCHER_PRINT_TIME_INVALID");
  }
  return [
    value.getFullYear(),
    String(value.getMonth() + 1).padStart(2, "0"),
    String(value.getDate()).padStart(2, "0"),
  ].join("-") +
    " " +
    [
      String(value.getHours()).padStart(2, "0"),
      String(value.getMinutes()).padStart(2, "0"),
      String(value.getSeconds()).padStart(2, "0"),
    ].join(":");
}

function safeText(value: unknown, code: string, maxLength: number): string {
  if (typeof value !== "string") throw new Error(code);
  const normalized = value.trim();
  if (
    normalized.length === 0 ||
    normalized.length > maxLength ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    throw new Error(code);
  }
  return normalized;
}

function isCompletedState(value: LocalOrder["state"]): boolean {
  return (
    value === "CompletedLocal" ||
    value === "PendingSync" ||
    value === "Syncing" ||
    value === "Synced" ||
    value === "Blocked403" ||
    value === "Rejected"
  );
}

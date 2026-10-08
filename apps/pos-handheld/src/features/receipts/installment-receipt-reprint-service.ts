import { INSTALLMENT_RECEIPT_TERMS } from "@hb/pos-receipt-core/features/receipts/installment-receipt-terms";
import {
  buildSaleReceiptDocument,
  documentToEscPosBytes,
} from "@hb/pos-receipt-core/features/receipts/receipt-document";
import {
  resolveReceiptTermsBlock,
  type ReceiptCustomTermsText,
} from "@hb/pos-receipt-core/features/receipts/receipt-terms-text";
import type { FrozenReceiptReprintSettings } from "@hb/pos-receipt-core/features/receipts/receipt-reprint-service";

import type { RenderedReturnReceipt } from "@hb/pos-receipt-core/features/receipts/return-receipt-renderer";

import type { PreparedLastReceiptReprint } from "@hb/pos-domain/features/fulfilment/fulfilment-service";
import type {
  InstallmentDetails,
  InstallmentPayment,
  InstallmentsRemotePort,
} from "@/features/installments/installment-models";

/**
 * 分期小票用的冻结设置：在重打冻结设置之外，多带总部下发的「分期条款」自定义正文（可选）。
 * 缺省、空白或不合规时按内置默认文案打印（见 resolveReceiptTermsBlock）。
 */
export type FrozenInstallmentReceiptSettings = FrozenReceiptReprintSettings &
  Pick<ReceiptCustomTermsText, "installmentTerms">;

/** 分期补打读取冻结设置的端口；普通的 ReceiptReprintSettingsSource（不带 installmentTerms）同样满足它。 */
export interface InstallmentReceiptSettingsSource {
  getFrozenReceiptSettings(): Promise<FrozenInstallmentReceiptSettings | null>;
}

export type InstallmentReceiptReprintPreparationServiceOptions = Readonly<{
  installments: Pick<InstallmentsRemotePort, "getDetails">;
  settings: InstallmentReceiptSettingsSource;
  trustedStoreCode: string;
  trustedDeviceCode: string;
  nowIso(): string;
  /** 已取消分期补打时追加本机签发的退款券券面（与 WPF 一致）；缺省不追加。 */
  refundVouchers?: Readonly<{
    renderVouchers(installmentGuid: string, orderLabel: string): Promise<RenderedReturnReceipt | null>;
  }> | null;
}>;

/**
 * 点击重打时重新读取服务端分期事实；页面缓存、外设设置和跨设备订单都不能成为打印来源。
 */
export class InstallmentReceiptReprintPreparationService {
  public constructor(
    private readonly options: InstallmentReceiptReprintPreparationServiceOptions,
  ) {}

  public async prepare(
    installmentGuid: string,
  ): Promise<PreparedLastReceiptReprint | null> {
    try {
      if (
        !isExactText(installmentGuid) ||
        !isExactText(this.options.trustedStoreCode) ||
        !isExactText(this.options.trustedDeviceCode)
      ) {
        return null;
      }

      const details = await this.options.installments.getDetails(installmentGuid);
      if (
        !details ||
        details.installmentGuid !== installmentGuid ||
        details.storeCode !== this.options.trustedStoreCode ||
        details.deviceCode !== this.options.trustedDeviceCode ||
        !isInstallmentReceiptReprintEligible(details)
      ) {
        return null;
      }

      // 中文注释：详情核验通过后才读取并冻结本次动作的打印机、纸张、语言和门店抬头。
      const settings = await this.options.settings.getFrozenReceiptSettings();
      if (!isValidSettings(settings)) return null;

      const recordedPayments = details.payments
        .filter((payment) => payment.status === "Recorded")
        .slice()
        .sort((left, right) => left.recordedAtIso.localeCompare(right.recordedAtIso));
      const document = buildSaleReceiptDocument({
        locale: settings.locale,
        paper: settings.paper,
        store: {
          ...settings.store,
          brandName: settings.store.brandName.trim() || details.storeCode,
        },
        orderNumber: details.installmentNumber,
        orderGuid: details.installmentGuid,
        orderDisplay: details.installmentNumber,
        soldAtIso: details.createdAtIso,
        cashierName: details.cashierName,
        storeCode: details.storeCode,
        deviceCode: details.deviceCode,
        lines: details.lines.map((line) => ({
          name: line.displayName,
          lookupCode: line.lookupCode || line.itemNumber || line.productCode,
          quantity: line.quantity,
          discountCents: line.discountCents,
          totalCents: line.actualAmountCents,
        })),
        subtotalCents: details.totalCents,
        discountCents: 0,
        totalCents: details.totalCents,
        tenders: recordedPayments.map((payment) => ({
          method: payment.method,
          amountCents: payment.amountCents,
          reference: safePaymentReference(payment) ?? null,
        })),
        cashChangeCents: null,
        statusText: statusText(details),
        isReprint: true,
        includeMachineCodes: true,
        printedAtIso: this.options.nowIso(),
        extraInfoLines: installmentInfoLines(details, recordedPayments),
        // 中文注释：分期条款只印在进行中的分期上；未带条款时与原小票字节一致。
        // 条款正文：总部下发了可打印的自定义正文就用它（标题固定为 INSTALLMENT TERMS），
        // 否则（未定制、全空白、不合规被丢弃）逐字打印内置默认文案。
        ...(shouldPrintInstallmentTerms(details)
          ? {
              termsBlock: resolveReceiptTermsBlock(
                INSTALLMENT_RECEIPT_TERMS,
                settings.installmentTerms,
              ),
            }
          : {}),
      });

      const receiptBytes = documentToEscPosBytes(document);
      const voucherBytes = details.status === "Cancelled"
        ? await this.refundVoucherBytes(installmentGuid, details.installmentNumber, settings.printerId)
        : null;
      return {
        orderGuid: installmentGuid,
        externalOrderGuid: installmentGuid,
        receiptBytes: voucherBytes ? concatBytes(receiptBytes, voucherBytes) : receiptBytes,
        printerId: settings.printerId,
      };
    } catch {
      // 中文注释：任一远程、格式或渲染事实不可验证时都不创建耐久打印任务。
      return null;
    }
  }

  // 退款券材料缺失（如跨机取消）或打印机不一致时只补打分期小票，不因券面阻断补打。
  private async refundVoucherBytes(
    installmentGuid: string,
    orderLabel: string,
    printerId: string,
  ): Promise<Uint8Array | null> {
    if (!this.options.refundVouchers) return null;
    try {
      const rendered = await this.options.refundVouchers.renderVouchers(installmentGuid, orderLabel);
      return rendered && rendered.printerId === printerId ? rendered.receiptBytes : null;
    } catch {
      return null;
    }
  }
}

function concatBytes(first: Uint8Array, second: Uint8Array): Uint8Array {
  const output = new Uint8Array(first.byteLength + second.byteLength);
  output.set(first, 0);
  output.set(second, first.byteLength);
  return output;
}

/**
 * 分期条款（订单至少 $50、首付至少 $20、每次还款至少 $5）只对「进行中」的分期有意义：
 * 已付清、已提货、已取消后再印「最低首付/还款」没有意义，取消单上更会误导顾客。
 * 带提货信息的进行中单属于不一致数据，同样不印，与 WPF 的判断一致。
 */
function shouldPrintInstallmentTerms(details: InstallmentDetails): boolean {
  return details.status === "Active" && !details.pickupInfo;
}

function installmentInfoLines(
  details: InstallmentDetails,
  recordedPayments: readonly InstallmentPayment[],
): readonly string[] {
  const lines = [
    `Installment No: ${details.installmentNumber}`,
    `Customer: ${details.customerName}`,
    `Phone: ${details.customerPhone ?? ""}`,
    `Deposit paid: ${money(details.downPaymentCents)}`,
    `Balance due: ${money(details.balanceCents)}`,
  ];
  if (details.pickupInfo) {
    lines.push(
      "Pickup: Confirmed",
      `Picked up at: ${formatLocalMinute(details.pickupInfo.pickedUpAtIso)}`,
      `Picked up by: ${details.pickupInfo.pickedUpBy}`,
    );
    if (details.pickupInfo.note?.trim()) {
      lines.push(`Pickup note: ${details.pickupInfo.note.trim()}`);
    }
  } else if (details.status === "PaidOff") {
    lines.push("Pickup: Pending");
  }
  if (recordedPayments.length > 0) {
    lines.push("Payment history:");
    lines.push(...recordedPayments.map(paymentHistoryLine));
  }
  return Object.freeze(lines);
}

function paymentHistoryLine(payment: InstallmentPayment): string {
  const base = `${formatLocalMinute(payment.recordedAtIso)} ${paymentLabel(payment.method)} ${money(payment.amountCents)}`;
  const reference = safePaymentReference(payment);
  return reference ? `${base} Ref: ${reference}` : base;
}

function paymentLabel(method: InstallmentPayment["method"]): string {
  if (method === "cash") return "Cash";
  if (method === "card") return "Card";
  return "Voucher";
}

function safePaymentReference(payment: InstallmentPayment): string | undefined {
  if (payment.method !== "card") return undefined;
  const masked = payment.maskedCardNumber?.trim() ?? "";
  if (!/^\*{2,}\d{1,4}$/u.test(masked)) return undefined;
  const cardType = payment.cardType?.trim();
  return cardType ? `${cardType} ${masked}` : masked;
}

function statusText(details: InstallmentDetails): string {
  if (details.pickupInfo || details.status === "PickedUp") {
    return "*** Paid - Picked Up ***";
  }
  if (details.status === "Cancelled") {
    return "*** Installment Cancelled ***";
  }
  if (details.status === "PaidOff") {
    return "*** Paid - Pickup Pending ***";
  }
  return "*** Deposit Received ***";
}

export function isInstallmentReceiptReprintEligible(
  details: InstallmentDetails,
): boolean {
  if (
    !isSafeRequiredText(details.installmentGuid) ||
    !isSafeRequiredText(details.installmentNumber) ||
    !isSafeRequiredText(details.storeCode) ||
    !isSafeRequiredText(details.deviceCode) ||
    !isSafeRequiredText(details.cashierName) ||
    !isSafeRequiredText(details.customerName) ||
    !isSafeOptionalText(details.customerPhone) ||
    !Number.isFinite(new Date(details.createdAtIso).getTime()) ||
    !Array.isArray(details.lines) ||
    !Array.isArray(details.payments)
  ) {
    return false;
  }
  const amounts = [
    details.totalCents,
    details.minimumDownPaymentCents,
    details.downPaymentCents,
    details.paidCents,
    details.balanceCents,
  ];
  if (
    amounts.some((amount) => !Number.isSafeInteger(amount) || amount < 0) ||
    details.totalCents <= 0 ||
    details.downPaymentCents > details.totalCents ||
    details.minimumDownPaymentCents > details.totalCents ||
    details.lines.length === 0
  ) {
    return false;
  }
  if (!hasConsistentInstallmentBalance(details)) {
    return false;
  }
  if (
    details.lines.some((line) =>
      !isSafeRequiredText(line.displayName) ||
      !isSafeRequiredText(line.productCode) ||
      !isSafeOptionalText(line.lookupCode) ||
      !isSafeOptionalText(line.itemNumber) ||
      !isPositiveDecimal(line.quantity) ||
      !Number.isSafeInteger(line.unitPriceCents) ||
      line.unitPriceCents < 0 ||
      !Number.isSafeInteger(line.discountCents) ||
      line.discountCents < 0 ||
      !Number.isSafeInteger(line.actualAmountCents) ||
      line.actualAmountCents < 0)
  ) {
    return false;
  }
  if (
    exactCentsSum(details.lines.map((line) => line.actualAmountCents)) !==
    details.totalCents
  ) {
    return false;
  }
  const recorded = details.payments.filter((payment) => payment.status === "Recorded");
  if (
    recorded.some((payment) =>
      !Number.isSafeInteger(payment.amountCents) ||
      !Number.isFinite(new Date(payment.recordedAtIso).getTime()) ||
      !isSafeOptionalText(payment.cardType) ||
      !isSafeOptionalText(payment.maskedCardNumber))
  ) {
    return false;
  }
  const hasInvalidRecordedPayments =
    details.status === "Cancelled" &&
    details.cancellationInfo?.kind === "RefundCancel"
      ? !hasBalancedRefundPayments(recorded)
      : recorded.some((payment) => payment.amountCents <= 0);
  if (hasInvalidRecordedPayments) {
    return false;
  }
  if (
    details.pickupInfo &&
    (!Number.isFinite(new Date(details.pickupInfo.pickedUpAtIso).getTime()) ||
      !isSafeRequiredText(details.pickupInfo.pickedUpBy) ||
      !isSafeOptionalText(details.pickupInfo.note))
  ) {
    return false;
  }
  if (
    details.cancellationInfo &&
    (!Number.isFinite(new Date(details.cancellationInfo.cancelledAtIso).getTime()) ||
      !isSafeRequiredText(details.cancellationInfo.cancelledBy) ||
      !isSafeOptionalText(details.cancellationInfo.reason))
  ) {
    return false;
  }
  return exactCentsSum(recorded.map((payment) => payment.amountCents)) === details.paidCents;
}

function hasConsistentInstallmentBalance(details: InstallmentDetails): boolean {
  if (details.status !== "Cancelled") {
    return details.cancellationInfo === null &&
      exactCentsSum([details.paidCents, details.balanceCents]) === details.totalCents;
  }
  const cancellation = details.cancellationInfo;
  if (!cancellation) return false;
  if (cancellation.kind === "RefundCancel") {
    return details.paidCents === 0 && details.balanceCents === 0;
  }
  return cancellation.kind === "VoidCancel" &&
    details.balanceCents > 0 &&
    exactCentsSum([details.paidCents, details.balanceCents]) === details.totalCents;
}

function hasBalancedRefundPayments(
  payments: readonly InstallmentPayment[],
): boolean {
  if (
    !payments.some((payment) => payment.amountCents > 0) ||
    !payments.some((payment) => payment.amountCents < 0)
  ) {
    return false;
  }
  // 中文注释：原路退按方式逐项抵平。
  const balancedByMethod = (["cash", "card", "voucher"] as const).every((method) => {
    const amounts = payments
      .filter((payment) => payment.method === method)
      .map((payment) => payment.amountCents);
    return amounts.length === 0 || exactCentsSum(amounts) === 0;
  });
  if (balancedByMethod) return true;
  // 中文注释：退代金券模式下现金/刷卡原付款也以退款券退回，只能按合计抵平，且退款必须全为代金券。
  return (
    payments
      .filter((payment) => payment.amountCents < 0)
      .every((payment) => payment.method === "voucher") &&
    exactCentsSum(payments.map((payment) => payment.amountCents)) === 0
  );
}

function exactCentsSum(values: readonly number[]): number | null {
  let sum = 0;
  for (const value of values) {
    if (!Number.isSafeInteger(value)) return null;
    sum += value;
    if (!Number.isSafeInteger(sum)) return null;
  }
  return sum;
}

function isPositiveDecimal(value: string): boolean {
  return /^(?:0|[1-9]\d*)(?:\.\d+)?$/u.test(value) && Number(value) > 0;
}

function money(cents: number): string {
  const sign = cents < 0 ? "-" : "";
  const absolute = Math.abs(cents);
  return `${sign}$${Math.trunc(absolute / 100)}.${String(absolute % 100).padStart(2, "0")}`;
}

function formatLocalMinute(value: string): string {
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) throw new TypeError("Installment time is invalid.");
  const pad = (part: number) => String(part).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function isExactText(value: unknown): value is string {
  return typeof value === "string" && value.length > 0 && value.trim() === value;
}

function isSafeRequiredText(value: unknown): value is string {
  return isExactText(value) && !/[\u0000-\u001f\u007f-\u009f]/u.test(value);
}

function isSafeOptionalText(value: unknown): value is string | null {
  return value === null ||
    (typeof value === "string" && !/[\u0000-\u001f\u007f-\u009f]/u.test(value));
}

function isValidSettings(
  value: FrozenInstallmentReceiptSettings | null,
): value is FrozenInstallmentReceiptSettings {
  if (!value || !isExactText(value.printerId)) return false;
  if (value.paper !== "58mm" && value.paper !== "80mm") return false;
  if (value.locale !== "en" && value.locale !== "zh-CN") return false;
  const store = value.store;
  return Boolean(
    store &&
      typeof store.brandName === "string" &&
      typeof store.storeName === "string" &&
      typeof store.address === "string" &&
      typeof store.phone === "string" &&
      typeof store.abn === "string" &&
      typeof store.returnPolicy === "string",
  );
}

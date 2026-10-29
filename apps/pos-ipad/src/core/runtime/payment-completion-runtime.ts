import type {
  PaymentCompletionProjectionPort,
  PaymentCompletionSettingsPort,
  PaymentReceiptRenderInput,
  PaymentReceiptRendererPort,
} from "../../features/payments/runtime/payment-completion-planner";
import {
  buildSaleReceiptDocument,
  defaultSaleStatusText,
  documentToEscPosBytes,
} from "../../features/receipts";
import type {
  LocalOrder,
  OrderRepositoryPort,
  OrderTender,
} from "../contracts";
import type { ReceiptPrinterSettings } from "../db/pos-settings-repository";

export interface PaymentReceiptSettingsPort {
  getReceiptPrinterSettings(): Promise<ReceiptPrinterSettings>;
}

export type PaymentCompletionSessionPort = Readonly<{
  canOpenCashDrawer(): boolean;
}>;

/**
 * 只从 SQLCipher 订单/tender 事实投影已付金额；页面显示的 remaining 不参与。
 */
export class OrderRepositoryPaymentCompletionProjection
implements PaymentCompletionProjectionPort {
  public constructor(
    private readonly orders: Pick<OrderRepositoryPort, "getByGuid">,
  ) {}

  public async read(orderGuid: string) {
    const order = await this.orders.getByGuid(requiredText(orderGuid));
    if (!order) return null;
    const paidCents = order.tenders.reduce(
      (total, tender) => checkedAdd(total, tender.amount.cents),
      0,
    );
    return {
      orderGuid: order.orderGuid,
      total: order.actualAmount,
      paid: { currency: "AUD" as const, cents: paidCents },
    };
  }
}

/**
 * 当前设置只决定新建履约任务；已经排队的任务仍使用 SQLCipher 中冻结的 bytes。
 * 普通混合现金不自动打印，但符合 WPF 流程的卡/券完成可按打印开关排队。
 */
export class PersistedPaymentCompletionSettings
implements PaymentCompletionSettingsPort {
  public constructor(
    private readonly settings: PaymentReceiptSettingsPort,
    private readonly session: PaymentCompletionSessionPort,
  ) {}

  public async load() {
    const settings = await this.settings.getReceiptPrinterSettings();
    return {
      printerId: normalizedPeripheralId(settings.peripheralId),
      automaticPrint: {
        cash: false,
        card: settings.printEnabled,
        voucher: settings.printEnabled,
      },
      cashDrawerEnabled: settings.drawerEnabled,
      cashDrawerPermissionAllowed: this.session.canOpenCashDrawer(),
    };
  }
}

/**
 * approved tender 尚未写入时，以持久订单加“本次即将提交的单一 tender”预渲染。
 * 仅金额与 method 可进入 renderer；attempt/provider reference 不会写到普通小票。
 */
export class OrderRepositoryPaymentReceiptRenderer
implements PaymentReceiptRendererPort {
  public constructor(
    private readonly orders: Pick<OrderRepositoryPort, "getByGuid">,
    private readonly settings: PaymentReceiptSettingsPort,
  ) {}

  public async render(input: PaymentReceiptRenderInput): Promise<Uint8Array> {
    const order = await this.orders.getByGuid(requiredText(input.orderGuid));
    if (!order || order.orderGuid !== input.orderGuid) {
      throw new Error("PAYMENT_RECEIPT_ORDER_NOT_FOUND");
    }
    assertRenderablePaymentDraft(order);
    if (
      input.amount.currency !== "AUD" ||
      !Number.isSafeInteger(input.amount.cents) ||
      input.amount.cents <= 0
    ) {
      throw new Error("PAYMENT_RECEIPT_AMOUNT_INVALID");
    }
    const settings = await this.settings.getReceiptPrinterSettings();
    const tenders = appendPlannedTender(order.tenders, input);
    const tenderTotal = tenders.reduce(
      (total, tender) => checkedAdd(total, tender.amount.cents),
      0,
    );
    if (tenderTotal !== order.actualAmount.cents) {
      throw new Error("PAYMENT_RECEIPT_TENDER_TOTAL_MISMATCH");
    }

    return documentToEscPosBytes(
      buildSaleReceiptDocument({
        locale: settings.locale,
        paper: settings.paper,
        store: {
          brandName: settings.brandName,
          storeName: settings.storeName,
          address: settings.address,
          phone: settings.phone,
          abn: settings.abn,
          returnPolicy: settings.returnPolicy,
        },
        orderNumber: order.orderGuid,
        orderGuid: order.orderGuid,
        orderDisplay: `#${order.localSequence}`,
        orderPresentation: "guid-only",
        soldAtIso: order.soldAtIso,
        cashierName: order.cashierName,
        storeCode: order.storeCode,
        deviceCode: order.deviceCode,
        lines: order.lines.map((line) => ({
          name: line.displayName,
          lookupCode: line.lookupCode,
          quantity: line.quantity,
          discountCents: line.discount.cents,
          totalCents: line.actualAmount.cents,
        })),
        subtotalCents: order.total.cents,
        discountCents: order.discount.cents,
        totalCents: order.actualAmount.cents,
        tenders: tenders.map((tender) => ({
          method: tender.method,
          amountCents: tender.amount.cents,
          // provider reference/authorization code 不属于普通销售小票输入。
          reference: null,
        })),
        cashChangeCents: null,
        statusText: defaultSaleStatusText(order.actualAmount.cents),
        includeMachineCodes: true,
        printedAtIso: order.soldAtIso,
      }),
    );
  }
}

function appendPlannedTender(
  persisted: readonly OrderTender[],
  input: PaymentReceiptRenderInput,
): readonly OrderTender[] {
  const method =
    input.method === "voucher"
      ? "voucher"
      : input.method === "cash"
        ? "cash"
        : "card";
  return [
    ...persisted,
    {
      tenderGuid: `planned:${requiredText(input.attemptId ?? input.orderGuid)}`,
      method,
      amount: { ...input.amount },
      reference: null,
      reservationToken: null,
    },
  ];
}

function assertRenderablePaymentDraft(order: LocalOrder): void {
  if (
    (order.state !== "Draft" && order.state !== "Completing") ||
    order.actualAmount.currency !== "AUD" ||
    !Number.isSafeInteger(order.actualAmount.cents) ||
    order.actualAmount.cents <= 0 ||
    order.lines.length === 0
  ) {
    throw new Error("PAYMENT_RECEIPT_ORDER_INVALID");
  }
}

function normalizedPeripheralId(value: string | null): string | null {
  const normalized = value?.trim();
  return normalized ? normalized : null;
}

function requiredText(value: string): string {
  const normalized = value.trim();
  if (!normalized) throw new Error("PAYMENT_RECEIPT_ID_REQUIRED");
  return normalized;
}

function checkedAdd(left: number, right: number): number {
  const result = left + right;
  if (!Number.isSafeInteger(result)) {
    throw new Error("PAYMENT_COMPLETION_AMOUNT_OVERFLOW");
  }
  return result;
}

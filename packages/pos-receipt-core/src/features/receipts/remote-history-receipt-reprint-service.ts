import {
  buildSaleReceiptDocument,
  defaultSaleStatusText,
  documentToEscPosBytes,
} from "./receipt-document";
import type {
  FrozenReceiptReprintSettings,
  ReceiptReprintSettingsSource,
} from "./receipt-reprint-service";

import type {
  RemoteOrderHistoryDetails,
  RemoteOrderHistoryPort,
} from "@hb/pos-domain/core/contracts/remote-history";
import type { PreparedLastReceiptReprint } from "@hb/pos-domain/features/fulfilment/fulfilment-service";

export type RemoteHistoryReceiptReprintPreparationServiceOptions = Readonly<{
  history: Pick<RemoteOrderHistoryPort, "getDetails">;
  settings: ReceiptReprintSettingsSource;
  trustedStoreCode: string;
}>;

/**
 * 远程历史没有本地现金完成审计，因此只允许已精确对账的卡或券订单进入重打流程。
 */
export function isRemoteHistoryReceiptReprintEligible(
  details: RemoteOrderHistoryDetails,
): boolean {
  if (!Number.isSafeInteger(details.actualAmountCents) || details.actualAmountCents <= 0) {
    return false;
  }
  if (!Array.isArray(details.lines) || details.lines.length === 0) return false;
  if (!Array.isArray(details.payments) || details.payments.length === 0) return false;
  if (
    !Number.isSafeInteger(details.totalCents)
    || !Number.isSafeInteger(details.discountCents)
    || details.totalCents < 0
    || details.discountCents < 0
    || details.totalCents - details.discountCents !== details.actualAmountCents
    || details.lines.some((line) =>
      line.kind !== "sale"
      || !isPositiveDecimal(line.quantity)
      || !Number.isSafeInteger(line.unitPriceCents)
      || line.unitPriceCents < 0
      || !Number.isSafeInteger(line.discountCents)
      || line.discountCents < 0
      || !Number.isSafeInteger(line.actualAmountCents)
      || line.actualAmountCents < 0)
  ) {
    return false;
  }

  const lineAmountCents = exactCentsSum(
    details.lines.map((line) => line.actualAmountCents),
  );
  if (lineAmountCents !== details.actualAmountCents) return false;
  const lineDiscountCents = exactCentsSum(
    details.lines.map((line) => line.discountCents),
  );
  if (lineDiscountCents !== details.discountCents) return false;

  if (details.payments.some(
    (payment) =>
      (payment.method !== "card" && payment.method !== "voucher")
      || !Number.isSafeInteger(payment.amountCents)
      || payment.amountCents <= 0,
  )) {
    return false;
  }
  const paymentAmountCents = exactCentsSum(
    details.payments.map((payment) => payment.amountCents),
  );
  return paymentAmountCents === details.actualAmountCents;
}

function isPositiveDecimal(value: string): boolean {
  return /^(?:0|[1-9]\d*)(?:\.\d+)?$/u.test(value) && Number(value) > 0;
}

/**
 * 从远程订单事实重新生成票据；详情、门店和打印设置任一不可验证时均不创建打印任务。
 */
export class RemoteHistoryReceiptReprintPreparationService {
  public constructor(
    private readonly options: RemoteHistoryReceiptReprintPreparationServiceOptions,
  ) {}

  public async prepare(
    orderGuid: string,
  ): Promise<PreparedLastReceiptReprint | null> {
    try {
      if (!isExactText(orderGuid) || !isExactText(this.options.trustedStoreCode)) {
        return null;
      }

      // 中文注释：不能使用页面缓存详情；点击时必须按原始 GUID 重新向远程来源读取。
      const details = await this.options.history.getDetails(orderGuid);
      if (
        !details
        || details.orderGuid !== orderGuid
        || !isTrustedStore(details.storeCode, this.options.trustedStoreCode)
        || !isRemoteHistoryReceiptReprintEligible(details)
      ) {
        return null;
      }

      // 中文注释：一次性读取并冻结本次动作的打印机、纸张、语言和门店抬头。
      const settings = await this.options.settings.getFrozenReceiptSettings();
      if (!isValidSettings(settings)) return null;

      const document = buildSaleReceiptDocument({
        locale: settings.locale,
        paper: settings.paper,
        store: {
          ...settings.store,
          brandName: settings.store.brandName.trim() || details.storeCode,
        },
        orderNumber: details.orderGuid,
        orderGuid: details.orderGuid,
        orderDisplay: shortOrderGuid(details.orderGuid),
        orderPresentation: "guid-only",
        soldAtIso: details.soldAtIso,
        cashierName: details.cashierName,
        storeCode: details.storeCode,
        deviceCode: details.deviceCode,
        lines: details.lines.map((line) => ({
          name: line.displayName,
          lookupCode: line.lookupCode ?? line.itemNumber ?? line.productCode,
          quantity: line.quantity,
          discountCents: line.discountCents,
          totalCents: line.actualAmountCents,
        })),
        subtotalCents: details.totalCents,
        discountCents: details.discountCents,
        totalCents: details.actualAmountCents,
        tenders: details.payments.map((payment) => ({
          method: payment.method,
          amountCents: payment.amountCents,
          // 中文注释：远程合同只允许脱敏展示引用进入票据，不读取卡类型或掩码卡号等旁路字段。
          reference: payment.displayReference,
        })),
        cashChangeCents: null,
        statusText: defaultSaleStatusText(details.actualAmountCents),
        isReprint: true,
        includeMachineCodes: true,
        printedAtIso: details.soldAtIso,
      });

      return {
        // 中文注释：返回调用方请求的原始值；严禁裁剪、改大小写或改绑远程返回的其他订单。
        orderGuid,
        receiptBytes: documentToEscPosBytes(document),
        printerId: settings.printerId.trim(),
      };
    } catch {
      return null;
    }
  }
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

function shortOrderGuid(value: string): string {
  return `#${value.slice(-8).toUpperCase()}`;
}

function isTrustedStore(value: unknown, trustedStoreCode: string): boolean {
  return isExactText(value)
    && value.toLocaleUpperCase() === trustedStoreCode.toLocaleUpperCase();
}

function isExactText(value: unknown): value is string {
  return typeof value === "string" && value.length > 0 && value.trim() === value;
}

function isValidSettings(
  value: FrozenReceiptReprintSettings | null,
): value is FrozenReceiptReprintSettings {
  if (!value || !isExactText(value.printerId)) return false;
  if (value.paper !== "58mm" && value.paper !== "80mm") return false;
  if (value.locale !== "en" && value.locale !== "zh-CN") return false;
  const store = value.store;
  return Boolean(
    store
      && typeof store.brandName === "string"
      && typeof store.storeName === "string"
      && typeof store.address === "string"
      && typeof store.phone === "string"
      && typeof store.abn === "string"
      && typeof store.returnPolicy === "string",
  );
}

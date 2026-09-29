import {
  buildSaleReceiptDocument,
  documentToEscPosBytes,
  type EscPosDocument,
  type ReceiptLocale,
  type ReceiptPaper,
  type ReceiptStoreHeading,
} from "./receipt-document";

import type { LocalOrder } from "@hb/pos-domain/core/contracts/order";
import type { OrderRepositoryPort } from "@hb/pos-domain/core/contracts/repositories";
import type { PreparedLastReceiptReprint } from "@hb/pos-domain/features/fulfilment/fulfilment-service";

/**
 * 重打只能读取订单账本。实现不得将 print_jobs 作为订单来源或用其替换 orderGuid。
 */
export interface ReceiptReprintOrderSource {
  getByOrderGuid(orderGuid: string): Promise<LocalOrder | null>;
  getLastByLocalSequence(): Promise<LocalOrder | null>;
}

/**
 * 复用订单仓储的账本顺序：SqliteOrderRepository 的 listLocal 已按 local_sequence DESC 查询。
 */
export class OrderRepositoryReceiptReprintSource implements ReceiptReprintOrderSource {
  public constructor(
    private readonly orders: Pick<OrderRepositoryPort, "getByGuid" | "listLocal">,
  ) {}

  public getByOrderGuid(orderGuid: string): Promise<LocalOrder | null> {
    return this.orders.getByGuid(orderGuid);
  }

  public async getLastByLocalSequence(): Promise<LocalOrder | null> {
    const [last] = await this.orders.listLocal(1);
    return last ?? null;
  }
}

/**
 * 调用方须从持久化设置一次性读取此快照，不能混用当前 UI 的打印机、纸张或门店抬头。
 */
export type FrozenReceiptDocumentSettings = Readonly<{
  paper: ReceiptPaper;
  locale: ReceiptLocale;
  store: ReceiptStoreHeading;
}>;

export type FrozenReceiptReprintSettings = FrozenReceiptDocumentSettings &
  Readonly<{ printerId: string }>;

export interface ReceiptReprintSettingsSource {
  getFrozenReceiptSettings(): Promise<FrozenReceiptReprintSettings | null>;
}

/**
 * 现金找零只能取自完成审计的持久化值。null 表示没有可证明的完成审计，不是零找零。
 */
export type ReceiptCompletionSettlement = Readonly<{
  cashChangeCents: number;
}>;

export interface ReceiptCompletionSettlementSource {
  getCompletionSettlement(orderGuid: string): Promise<ReceiptCompletionSettlement | null>;
}

export type ReceiptReprintPreparationServiceOptions = Readonly<{
  orders: ReceiptReprintOrderSource;
  settings: ReceiptReprintSettingsSource;
  settlements: ReceiptCompletionSettlementSource;
  /** 生产运行时必须注入可信时钟；可选仅为兼容既有纯单元测试。 */
  nowIso?: () => string;
}>;

/**
 * 根据已提交的本地订单重新渲染小票。服务不读 print_jobs，不使用设备时间，也不读取结账 UI。
 */
export class ReceiptReprintPreparationService {
  public constructor(private readonly options: ReceiptReprintPreparationServiceOptions) {}

  public async prepareCurrent(orderGuid: string): Promise<PreparedLastReceiptReprint | null> {
    try {
      return await this.prepare(await this.options.orders.getByOrderGuid(orderGuid));
    } catch {
      // 中文注释：重打没有可验证的账本事实时宁可不创建任务，也绝不猜测金额、找零或外设。
      return null;
    }
  }

  public async prepareLast(): Promise<PreparedLastReceiptReprint | null> {
    try {
      return await this.prepare(await this.options.orders.getLastByLocalSequence());
    } catch {
      // 中文注释：最后一单仅允许订单源按 local_sequence 决定；读取失败同样保守拒绝。
      return null;
    }
  }

  private async prepare(order: LocalOrder | null): Promise<PreparedLastReceiptReprint | null> {
    if (!isRenderableOrder(order)) return null;

    const settings = await this.options.settings.getFrozenReceiptSettings();
    if (!isValidReprintSettings(settings)) return null;

    const cashChangeCents = await this.readCashChange(order);
    if (cashChangeCents === undefined) return null;

    // 中文注释：重打时间在任务准备阶段冻结，后续排队或重试不得重新取时钟。
    const printedAtIso = this.options.nowIso?.() ?? order.soldAtIso;
    const receiptBytes = renderReceiptReprint(
      order,
      settings,
      cashChangeCents,
      printedAtIso,
    );
    return receiptBytes
      ? {
          // 中文注释：原样保留账本 orderGuid；不得裁剪、规范化或从历史打印任务改绑。
          orderGuid: order.orderGuid,
          receiptBytes,
          printerId: settings.printerId.trim(),
        }
      : null;
  }

  private async readCashChange(order: LocalOrder): Promise<number | null | undefined> {
    if (!requiresCashSettlementAudit(order)) return null;

    const settlement = await this.options.settlements.getCompletionSettlement(order.orderGuid);
    if (!settlement || !isNonNegativeCents(settlement.cashChangeCents)) return undefined;
    return settlement.cashChangeCents;
  }
}

/** 纯函数：只将本地订单、冻结设置和已持久化的找零映射为带重打标记的 ESC/POS bytes。 */
export function renderReceiptReprint(
  order: LocalOrder,
  settings: FrozenReceiptReprintSettings,
  cashChangeCents: number | null,
  printedAtIso: string = order.soldAtIso,
): Uint8Array | null {
  if (!isValidReprintSettings(settings)) return null;

  const document = renderLocalOrderReceiptDocument(
    order,
    settings,
    cashChangeCents,
    { isReprint: true, printedAtIso },
  );
  return document ? documentToEscPosBytes(document) : null;
}

/**
 * 预览和真实打印共用的纯票据文档映射。这里是支付引用脱敏进入 UI 前的唯一边界。
 */
export function renderLocalOrderReceiptDocument(
  order: LocalOrder,
  settings: FrozenReceiptDocumentSettings,
  cashChangeCents: number | null,
  context: Readonly<{ isReprint: boolean; printedAtIso: string }>,
): EscPosDocument | null {
  if (!isRenderableOrder(order) || !isValidDocumentSettings(settings)) return null;
  if (requiresCashSettlementAudit(order) && !isNonNegativeCents(cashChangeCents)) return null;
  if (!requiresCashSettlementAudit(order) && cashChangeCents !== null) return null;
  if (!isCanonicalIso(context.printedAtIso)) return null;

  try {
    return buildSaleReceiptDocument({
      locale: settings.locale,
      paper: settings.paper,
      store: {
        ...settings.store,
        // 中文注释：品牌可为空，但回退只能使用订单自身可信门店编码。
        brandName: settings.store.brandName.trim() || order.storeCode,
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
      tenders: order.tenders.map((tender) => ({
        method: tender.method,
        amountCents: tender.amount.cents,
        // 中文注释：document 层只输出掩码引用；绝不传递 reservationToken、PAN 或授权码。
        reference: tender.reference,
      })),
      cashChangeCents,
      statusText: "*** Paid ***",
      isReprint: context.isReprint,
      includeMachineCodes: true,
      // 中文注释：WPF 的普通打印和补打都使用成交时间；实际任务时间仍由履约审计记录。
      printedAtIso: order.soldAtIso,
    });
  } catch {
    return null;
  }
}

export function requiresCashSettlementAudit(order: LocalOrder): boolean {
  // 中文注释：退单（实收为负）是把钱退给顾客，不存在找零结算；要求结算记录会让
  // 现金退单的历史重打与预览永远失败。WPF 可重打任何退单（负数金额整单小票）。
  if (order.actualAmount.cents < 0) return false;
  return order.actualAmount.cents === 0 || order.tenders.some((tender) => tender.method === "cash");
}

function isRenderableOrder(value: LocalOrder | null): value is LocalOrder {
  if (!value || !isMachineCodeText(value.orderGuid) || !isCanonicalIso(value.soldAtIso) || !hasText(value.storeCode) || !hasText(value.cashierName) || !hasText(value.deviceCode)) return false;
  if (!Number.isSafeInteger(value.localSequence) || value.localSequence <= 0 || !isCompletedOrder(value)) return false;
  if (!isAudCents(value.total) || !isAudCents(value.discount) || !isAudCents(value.actualAmount) || !Array.isArray(value.lines) || !value.lines.length || !Array.isArray(value.tenders)) return false;

  let lineTotal = 0;
  for (const line of value.lines) {
    if (!hasText(line.displayName) || !hasText(line.quantity) || !isAudCents(line.unitPrice) || !isAudCents(line.discount) || !isAudCents(line.actualAmount)) return false;
    lineTotal += line.actualAmount.cents;
    if (!Number.isSafeInteger(lineTotal)) return false;
  }
  if (lineTotal !== value.actualAmount.cents) return false;

  let tenderTotal = 0;
  for (const tender of value.tenders) {
    if (!isAudCents(tender.amount) || !isTenderMethod(tender.method) || (tender.reference !== null && typeof tender.reference !== "string")) return false;
    tenderTotal += tender.amount.cents;
    if (!Number.isSafeInteger(tenderTotal)) return false;
  }
  return tenderTotal === value.actualAmount.cents;
}

function isCompletedOrder(order: LocalOrder): boolean {
  return order.state === "CompletedLocal"
    || order.state === "PendingSync"
    || order.state === "Syncing"
    || order.state === "Synced"
    || order.state === "Blocked403"
    || order.state === "Rejected";
}

function isValidReprintSettings(value: FrozenReceiptReprintSettings | null): value is FrozenReceiptReprintSettings {
  return Boolean(
    value
      && hasText(value.printerId)
      && isValidDocumentSettings(value),
  );
}

function isValidDocumentSettings(
  value: FrozenReceiptDocumentSettings | null,
): value is FrozenReceiptDocumentSettings {
  return Boolean(
    value
      && (value.paper === "58mm" || value.paper === "80mm")
      && (value.locale === "en" || value.locale === "zh-CN")
      && isStoreHeading(value.store),
  );
}

function isStoreHeading(value: unknown): value is ReceiptStoreHeading {
  if (!value || typeof value !== "object") return false;
  const heading = value as Readonly<Record<string, unknown>>;
  return typeof heading.brandName === "string"
    && typeof heading.storeName === "string"
    && typeof heading.address === "string"
    && typeof heading.phone === "string"
    && typeof heading.abn === "string"
    && typeof heading.returnPolicy === "string";
}

function isAudCents(value: unknown): value is Readonly<{ currency: "AUD"; cents: number }> {
  return Boolean(value && typeof value === "object" && (value as Readonly<Record<string, unknown>>).currency === "AUD" && Number.isSafeInteger((value as Readonly<Record<string, unknown>>).cents));
}

function isNonNegativeCents(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}

function isCanonicalIso(value: unknown): value is string {
  if (typeof value !== "string" || !/(?:Z|[+-]\d{2}:\d{2})$/u.test(value)) {
    return false;
  }
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) && new Date(timestamp).toISOString() === value;
}

function hasText(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function isMachineCodeText(value: unknown): value is string {
  return typeof value === "string" && /^[\x20-\x7e]{1,128}$/u.test(value);
}

function isTenderMethod(value: unknown): value is "cash" | "card" | "voucher" {
  return value === "cash" || value === "card" || value === "voucher";
}

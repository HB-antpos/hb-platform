import {
  normalizeLineSyncProvenance,
  type LineSyncProvenance,
} from "../../core/contracts/line-sync-provenance";

export type ReturnTenderMethod = "cash" | "card" | "voucher" | "installment";
export type ReturnSourceKind = "receipt" | "no-receipt-product" | "no-receipt-open-item";
export type ReturnCaseKind = "receipt" | "no-receipt";

export type ReturnErrorCode =
  | "RETURN_QUERY_REQUIRED"
  | "RETURN_ORDER_NOT_FOUND"
  | "RETURN_PRODUCT_NOT_FOUND"
  | "RETURN_SOURCE_MISMATCH"
  | "RETURN_LINE_NOT_FOUND"
  | "RETURN_QUANTITY_INVALID"
  | "RETURN_QUANTITY_EXCEEDED"
  | "RETURN_AMOUNT_EXCEEDED"
  | "RETURN_NO_LINES_SELECTED"
  | "RETURN_CAPACITY_EXCEEDED"
  | "RETURN_ONLINE_REQUIRED"
  | "RETURN_SUPERVISOR_REQUIRED"
  | "RETURN_OPEN_ITEM_INVALID"
  | "RETURN_UNKNOWN_RECOVERY_REQUIRED"
  | "RETURN_OPERATION_IN_PROGRESS"
  | "RETURN_SESSION_EXPIRED"
  | "RETURN_LOOKUP_FAILED"
  | "RETURN_EXECUTION_DECLINED"
  | "RETURN_EXECUTION_FAILED"
  | "RETURN_RECOVERY_FAILED"
  | "RETURN_ORIGINAL_REFUND_UNAVAILABLE"
  | "RETURN_VOUCHER_SUBSTITUTE_UNAVAILABLE";

export class ReturnFeatureError extends Error {
  public constructor(
    public readonly code: ReturnErrorCode,
    message = code,
  ) {
    super(message);
    this.name = "ReturnFeatureError";
  }
}

export type OfflineCashCapacityProof = Readonly<{
  evidenceId: string;
  capacityId: string;
  originalOrderGuid: string;
  remainingCents: number;
}>;

/**
 * 原支付引用只允许由执行适配器按 capacityId 在可信仓储中解析。
 * 本合同刻意不携带 PaymentId、RFN、券码或终端授权码。
 */
export type OriginalReturnTenderCapacity = Readonly<{
  capacityId: string;
  originalOrderGuid: string;
  method: ReturnTenderMethod;
  remainingCents: number;
  offlineCashProof: OfflineCashCapacityProof | null;
  /**
   * 原支付不能原路退回（如独立刷卡机人工确认的 MANUAL 刷卡，与 WPF 口径一致），
   * 只能以现金或代金券代替退款；额度仍按原支付扣减，防止超额。缺省视为可原路退回。
   */
  substituteOnly?: boolean;
}>;

export type ReceiptReturnLine = Readonly<{
  selectionKey: string;
  originalOrderGuid: string;
  originalOrderDetailGuid: string;
  returnSourceKey: string;
  productCode: string;
  itemNumber: string | null;
  lookupCode: string;
  displayName: string;
  availableQuantity: number;
  unitRefundCents: number;
  remainingAmountCents: number;
  syncProvenance: LineSyncProvenance;
}>;

export type ReceiptReturnContext = Readonly<{
  originalOrderGuid: string;
  receiptLabel: string;
  loadedFrom: "remote" | "local";
  returnRecordsMayBeStale: boolean;
  lines: readonly ReceiptReturnLine[];
  tenderCapacities: readonly OriginalReturnTenderCapacity[];
}>;

export type NoReceiptReturnItem = Readonly<{
  sourceKind: Extract<
    ReturnSourceKind,
    "no-receipt-product" | "no-receipt-open-item"
  >;
  selectionKey: string;
  returnSourceKey: string;
  productCode: string;
  itemNumber: string | null;
  lookupCode: string;
  displayName: string;
  unitRefundCents: number;
  syncProvenance: LineSyncProvenance;
}>;

export type ReturnDraftLine =
  | Readonly<{
      sourceKind: "receipt";
      selectionKey: string;
      originalOrderGuid: string;
      originalOrderDetailGuid: string;
      returnSourceKey: string;
      productCode: string;
      itemNumber: string | null;
      lookupCode: string;
      displayName: string;
      availableQuantity: number;
      selectedQuantity: number;
      unitRefundCents: number;
      remainingAmountCents: number;
      syncProvenance: LineSyncProvenance;
    }>
  | Readonly<{
      sourceKind: Extract<
        ReturnSourceKind,
        "no-receipt-product" | "no-receipt-open-item"
      >;
      selectionKey: string;
      originalOrderGuid: null;
      originalOrderDetailGuid: null;
      returnSourceKey: string;
      productCode: string;
      itemNumber: string | null;
      lookupCode: string;
      displayName: string;
      availableQuantity: number;
      selectedQuantity: number;
      unitRefundCents: number;
      remainingAmountCents: number;
      syncProvenance: LineSyncProvenance;
    }>;

export type ReturnRefundLine = Readonly<{
  sourceKind: ReturnSourceKind;
  returnSourceKey: string;
  originalOrderGuid: string | null;
  originalOrderDetailGuid: string | null;
  productCode: string;
  quantity: number;
  /** 订单和支付账本使用负数表示退款。 */
  signedAmountCents: number;
  syncProvenance: LineSyncProvenance;
}>;

export type ReturnRefundAllocation = Readonly<{
  method: ReturnTenderMethod;
  /** 现金和支付 attempt 均使用负数表示退款。 */
  signedAmountCents: number;
  originalCapacityId: string | null;
  originalOrderGuid: string | null;
  offlineCashProof: OfflineCashCapacityProof | null;
}>;

export type ReturnRefundPlan = Readonly<{
  sourceKind: ReturnCaseKind;
  totalRefundCents: number;
  lines: readonly ReturnRefundLine[];
  allocations: readonly ReturnRefundAllocation[];
  online: boolean;
}>;

export function validateReceiptReturnContext(
  context: ReceiptReturnContext,
): void {
  assertNonEmpty(context.originalOrderGuid, "RETURN_SOURCE_MISMATCH");
  assertNonEmpty(context.receiptLabel, "RETURN_SOURCE_MISMATCH");
  const selectionKeys = new Set<string>();
  const sourceKeys = new Set<string>();
  const detailKeys = new Set<string>();
  const capacityKeys = new Set<string>();

  for (const line of context.lines) {
    if (
      line.originalOrderGuid !== context.originalOrderGuid ||
      !line.originalOrderDetailGuid.trim() ||
      !line.returnSourceKey.trim() ||
      !line.lookupCode.trim() ||
      !line.selectionKey.trim()
    ) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    if (
      selectionKeys.has(line.selectionKey) ||
      sourceKeys.has(line.returnSourceKey) ||
      detailKeys.has(line.originalOrderDetailGuid)
    ) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    selectionKeys.add(line.selectionKey);
    sourceKeys.add(line.returnSourceKey);
    detailKeys.add(line.originalOrderDetailGuid);
    normalizeReturnLineSyncProvenance(line.syncProvenance);
    assertWholeQuantity(line.availableQuantity, true);
    assertPositiveCents(line.unitRefundCents);
    assertNonNegativeCents(line.remainingAmountCents);
  }

  for (const capacity of context.tenderCapacities) {
    if (
      !capacity.capacityId.trim() ||
      capacity.originalOrderGuid !== context.originalOrderGuid ||
      capacityKeys.has(capacity.capacityId)
    ) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    capacityKeys.add(capacity.capacityId);
    assertNonNegativeCents(capacity.remainingCents);
    if (capacity.offlineCashProof) {
      const proof = capacity.offlineCashProof;
      if (
        capacity.method !== "cash" ||
        proof.capacityId !== capacity.capacityId ||
        proof.originalOrderGuid !== capacity.originalOrderGuid ||
        proof.remainingCents !== capacity.remainingCents ||
        !proof.evidenceId.trim()
      ) {
        throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
      }
    }
  }
}

export function createReceiptDraftLines(
  context: ReceiptReturnContext,
): readonly ReturnDraftLine[] {
  validateReceiptReturnContext(context);
  return context.lines.map((line) => ({
    ...line,
    syncProvenance: normalizeReturnLineSyncProvenance(
      line.syncProvenance,
    ),
    sourceKind: "receipt" as const,
    selectedQuantity: 0,
  }));
}

export function createNoReceiptDraftLine(
  item: NoReceiptReturnItem,
): ReturnDraftLine {
  if (
    !item.selectionKey.trim() ||
    !item.returnSourceKey.trim() ||
    !item.productCode.trim() ||
    !item.displayName.trim() ||
    !item.lookupCode.trim()
  ) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
  if (
    item.sourceKind === "no-receipt-open-item" &&
    item.lookupCode.toUpperCase() !== "OPENITEM"
  ) {
    throw new ReturnFeatureError("RETURN_OPEN_ITEM_INVALID");
  }
  if (
    item.sourceKind === "no-receipt-product" &&
    item.lookupCode.toUpperCase() === "OPENITEM"
  ) {
    throw new ReturnFeatureError("RETURN_OPEN_ITEM_INVALID");
  }
  assertPositiveCents(item.unitRefundCents);
  const syncProvenance = normalizeReturnLineSyncProvenance(
    item.syncProvenance,
  );
  return {
    sourceKind: item.sourceKind,
    selectionKey: item.selectionKey,
    originalOrderGuid: null,
    originalOrderDetailGuid: null,
    returnSourceKey: item.returnSourceKey,
    productCode: item.productCode,
    itemNumber: item.itemNumber,
    lookupCode: item.lookupCode,
    displayName: item.displayName,
    availableQuantity: Number.MAX_SAFE_INTEGER,
    selectedQuantity: 1,
    unitRefundCents: item.unitRefundCents,
    remainingAmountCents: Number.MAX_SAFE_INTEGER,
    syncProvenance,
  };
}

export function updateReturnLineQuantity(
  lines: readonly ReturnDraftLine[],
  selectionKey: string,
  quantity: number,
): readonly ReturnDraftLine[] {
  assertWholeQuantity(quantity, true);
  let found = false;
  const next = lines.map((line) => {
    if (line.selectionKey !== selectionKey) return line;
    found = true;
    if (quantity > line.availableQuantity) {
      throw new ReturnFeatureError("RETURN_QUANTITY_EXCEEDED");
    }
    const amount = selectedLineAmountCents(line, quantity);
    if (amount > line.remainingAmountCents) {
      throw new ReturnFeatureError("RETURN_AMOUNT_EXCEEDED");
    }
    return { ...line, selectedQuantity: quantity };
  });
  if (!found) throw new ReturnFeatureError("RETURN_LINE_NOT_FOUND");
  return next;
}

export function buildReturnRefundPlan(input: Readonly<{
  sourceKind: ReturnCaseKind;
  originalOrderGuid: string | null;
  lines: readonly ReturnDraftLine[];
  capacities: readonly OriginalReturnTenderCapacity[];
  online: boolean;
  preferredMethod: ReturnTenderMethod | null;
}>): ReturnRefundPlan {
  const selected = input.lines.filter((line) => line.selectedQuantity > 0);
  if (!selected.length) {
    throw new ReturnFeatureError("RETURN_NO_LINES_SELECTED");
  }

  const refundLines = selected.map((line) => {
    assertWholeQuantity(line.selectedQuantity, false);
    const amountCents = selectedLineAmountCents(line, line.selectedQuantity);
    if (amountCents <= 0 || amountCents > line.remainingAmountCents) {
      throw new ReturnFeatureError("RETURN_AMOUNT_EXCEEDED");
    }
    if (
      input.sourceKind === "receipt" &&
      (line.sourceKind !== "receipt" ||
        !input.originalOrderGuid ||
        line.originalOrderGuid !== input.originalOrderGuid ||
        !line.originalOrderDetailGuid ||
        !line.returnSourceKey)
    ) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    if (
      input.sourceKind !== "receipt" &&
      (line.sourceKind === "receipt" ||
        line.originalOrderGuid !== null ||
        line.originalOrderDetailGuid !== null)
    ) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    return {
      sourceKind: line.sourceKind,
      returnSourceKey: line.returnSourceKey,
      originalOrderGuid: line.originalOrderGuid,
      originalOrderDetailGuid: line.originalOrderDetailGuid,
      productCode: line.productCode,
      quantity: line.selectedQuantity,
      signedAmountCents: -amountCents,
      syncProvenance: normalizeReturnLineSyncProvenance(
        line.syncProvenance,
      ),
    };
  });
  const totalRefundCents = safeAdd(
    refundLines.map((line) => -line.signedAmountCents),
  );

  if (input.sourceKind !== "receipt") {
    if (!input.online) throw new ReturnFeatureError("RETURN_ONLINE_REQUIRED");
    const method = input.preferredMethod ?? "cash";
    return {
      sourceKind: input.sourceKind,
      totalRefundCents,
      lines: refundLines,
      allocations: [
        {
          method,
          signedAmountCents: -totalRefundCents,
          originalCapacityId: null,
          originalOrderGuid: null,
          offlineCashProof: null,
        },
      ],
      online: true,
    };
  }

  if (!input.originalOrderGuid) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
  // 离线仅允许带原单证明的现金退款；代金券代替需要在线，
  // 避免生成携带 cash proof 的 voucher allocation 造成预览与确认不一致。
  // 现金代替或 card/installment 排序偏好不改变 method，仍按原支付方式退回，离线放行。
  if (!input.online && input.preferredMethod === "voucher") {
    throw new ReturnFeatureError("RETURN_ONLINE_REQUIRED");
  }
  const eligible = input.capacities
    .filter(
      (capacity) =>
        capacity.originalOrderGuid === input.originalOrderGuid &&
        capacity.remainingCents > 0,
    )
    .filter((capacity) =>
      input.online
        ? true
        : capacity.method === "cash" && capacity.offlineCashProof !== null,
    )
    .map((capacity, index) => ({ capacity, index }))
    .sort((left, right) => {
      const leftPreferred =
        input.preferredMethod !== null &&
        left.capacity.method === input.preferredMethod;
      const rightPreferred =
        input.preferredMethod !== null &&
        right.capacity.method === input.preferredMethod;
      if (leftPreferred !== rightPreferred) return leftPreferred ? -1 : 1;
      return left.index - right.index;
    });

  let remaining = totalRefundCents;
  const allocations: ReturnRefundAllocation[] = [];
  for (const { capacity } of eligible) {
    if (remaining === 0) break;
    if (!input.online) validateOfflineCashCapacity(capacity);
    const amount = Math.min(remaining, capacity.remainingCents);
    if (amount <= 0) continue;
    const substituted =
      input.preferredMethod === "cash" || input.preferredMethod === "voucher";
    if (capacity.substituteOnly === true && !substituted) {
      // 不能原路退回的额度必须由收银员明确选择现金或代金券，绝不静默改走刷卡机。
      throw new ReturnFeatureError("RETURN_ORIGINAL_REFUND_UNAVAILABLE");
    }
    if (input.preferredMethod === "voucher" && capacity.method !== "voucher") {
      // 代金券代替刷卡/现金额度的执行、订单同步与退款券打印链路尚未打通，
      // 放行会在提交阶段失败并卡在未知恢复；先在计划阶段明确拒绝，改用现金代替。
      throw new ReturnFeatureError("RETURN_VOUCHER_SUBSTITUTE_UNAVAILABLE");
    }
    allocations.push({
      // 用户选定代替方式（现金/代金券）时，整单统一使用该方式退款；
      // 未选定或偏好为其他方式（card/installment）时，保持原支付方式原路退回。
      // 代替退款仍绑定原 capacity 扣减额度，防止超额退款。
      method:
        input.preferredMethod === "cash" || input.preferredMethod === "voucher"
          ? input.preferredMethod
          : capacity.method,
      signedAmountCents: -amount,
      originalCapacityId: capacity.capacityId,
      originalOrderGuid: capacity.originalOrderGuid,
      offlineCashProof: input.online ? null : capacity.offlineCashProof,
    });
    remaining -= amount;
  }
  if (remaining !== 0) {
    throw new ReturnFeatureError(
      input.online ? "RETURN_CAPACITY_EXCEEDED" : "RETURN_ONLINE_REQUIRED",
    );
  }

  return {
    sourceKind: input.sourceKind,
    totalRefundCents,
    lines: refundLines,
    allocations,
    online: input.online,
  };
}

export function selectedLineAmountCents(
  line: ReturnDraftLine,
  quantity: number,
): number {
  assertWholeQuantity(quantity, true);
  if (quantity === 0) return 0;
  if (
    line.sourceKind === "receipt" &&
    quantity === line.availableQuantity
  ) {
    return line.remainingAmountCents;
  }
  const amount = line.unitRefundCents * quantity;
  if (!Number.isSafeInteger(amount)) {
    throw new ReturnFeatureError("RETURN_AMOUNT_EXCEEDED");
  }
  return amount;
}

function validateOfflineCashCapacity(
  capacity: OriginalReturnTenderCapacity,
): void {
  const proof = capacity.offlineCashProof;
  if (
    capacity.method !== "cash" ||
    !proof ||
    proof.capacityId !== capacity.capacityId ||
    proof.originalOrderGuid !== capacity.originalOrderGuid ||
    proof.remainingCents !== capacity.remainingCents ||
    !proof.evidenceId.trim()
  ) {
    throw new ReturnFeatureError("RETURN_ONLINE_REQUIRED");
  }
}

function safeAdd(values: readonly number[]): number {
  return values.reduce((total, value) => {
    const next = total + value;
    if (!Number.isSafeInteger(next)) {
      throw new ReturnFeatureError("RETURN_AMOUNT_EXCEEDED");
    }
    return next;
  }, 0);
}

function assertWholeQuantity(quantity: number, allowZero: boolean): void {
  if (
    !Number.isSafeInteger(quantity) ||
    quantity < (allowZero ? 0 : 1)
  ) {
    throw new ReturnFeatureError("RETURN_QUANTITY_INVALID");
  }
}

function assertPositiveCents(value: number): void {
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new ReturnFeatureError("RETURN_AMOUNT_EXCEEDED");
  }
}

function assertNonNegativeCents(value: number): void {
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new ReturnFeatureError("RETURN_AMOUNT_EXCEEDED");
  }
}

function assertNonEmpty(value: string, code: ReturnErrorCode): void {
  if (!value.trim()) throw new ReturnFeatureError(code);
}

function normalizeReturnLineSyncProvenance(
  input: unknown,
): LineSyncProvenance {
  try {
    return normalizeLineSyncProvenance(input);
  } catch {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
}

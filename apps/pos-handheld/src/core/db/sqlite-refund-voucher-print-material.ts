import type { SqliteConnectionPort } from "@hb/pos-db/core/db/types";

import type { VoucherProtectedTokenPort } from "@/features/payments/voucher";
import type {
  ProtectedRefundVoucherPrintMaterial,
  ProtectedRefundVoucherPrintMaterialPort,
} from "@hb/pos-receipt-core/features/receipts/refund-voucher-receipt-renderer";

type RefundVoucherBindingRow = Readonly<{
  plan_action_id: unknown;
  plan_return_order_guid: unknown;
  receipt_kind: unknown;
  print_receipt: unknown;
  print_job_id: unknown;
  action_action_id: unknown;
  action_return_order_guid: unknown;
  action_state: unknown;
  action_total_refund_cents: unknown;
  action_store_code: unknown;
  action_device_code: unknown;
  action_cashier_id: unknown;
  allocation_total_cents: unknown;
  card_allocation_count: unknown;
  voucher_allocation_count: unknown;
  allocation_binding_count: unknown;
  allocation_action_id: unknown;
  allocation_id: unknown;
  allocation_index: unknown;
  execution_kind: unknown;
  allocation_method: unknown;
  allocation_signed_amount_cents: unknown;
  allocation_capacity_id: unknown;
  allocation_original_order_guid: unknown;
  external_attempt_id: unknown;
  allocation_external_attempt_kind: unknown;
  allocation_external_action_id: unknown;
  allocation_durable_attempt_id: unknown;
  allocation_status: unknown;
  capacity_reservation_state: unknown;
  capacity_id: unknown;
  capacity_original_order_guid: unknown;
  capacity_method: unknown;
  capacity_original_amount_cents: unknown;
  capacity_remaining_amount_cents: unknown;
  capacity_context_length: unknown;
  binding_tender_guid: unknown;
  binding_action_id: unknown;
  binding_allocation_id: unknown;
  binding_external_attempt_kind: unknown;
  binding_external_action_id: unknown;
  binding_durable_attempt_id: unknown;
  order_guid: unknown;
  order_state: unknown;
  store_code: unknown;
  device_code: unknown;
  cashier_id: unknown;
  total_cents: unknown;
  discount_cents: unknown;
  actual_amount_cents: unknown;
  line_count: unknown;
  return_line_count: unknown;
  tender_total_cents: unknown;
  non_negative_tender_count: unknown;
  voucher_tender_count: unknown;
  approved_voucher_attempt_count: unknown;
  protected_state_count: unknown;
  tender_guid: unknown;
  tender_order_guid: unknown;
  tender_method: unknown;
  tender_amount_cents: unknown;
  payment_attempt_id: unknown;
  attempt_id: unknown;
  idempotency_key: unknown;
  attempt_order_guid: unknown;
  provider: unknown;
  operation: unknown;
  attempt_amount_cents: unknown;
  attempt_state: unknown;
  protected_reference: unknown;
  state_attempt_id: unknown;
  state_idempotency_key: unknown;
  state_order_guid: unknown;
}>;

/** 公开行校验通过、尚待解密核对的单张退款券绑定。 */
type VerifiedVoucherBinding = Readonly<{
  attemptId: string;
  idempotencyKey: string;
  signedAmountCents: number;
  /** 受保护状态表的明文绑定列；须在解密之后核对，好让密文仓储的 typed integrity error 先穿透。 */
  protectedReference: string | null;
  stateAttemptId: string | null;
  stateIdempotencyKey: string | null;
  stateOrderGuid: string | null;
}>;

type OrderIdentity = Readonly<{
  storeCode: string;
  cashierId: string;
}>;

const COMPLETED_ORDER_STATES = new Set([
  "CompletedLocal",
  "PendingSync",
  "Syncing",
  "Synced",
  "Blocked403",
  "Rejected",
]);

export class SqliteRefundVoucherPrintMaterial
implements ProtectedRefundVoucherPrintMaterialPort {
  public constructor(
    private readonly connection: SqliteConnectionPort,
    private readonly protectedTokens: Pick<
      VoucherProtectedTokenPort,
      "getByAttempt"
    >,
  ) {}

  /**
   * 退货单内每笔已批准的 voucher refund allocation 各返回一份材料（按 allocation 顺序）。
   * 纯券退款与混合退款（现金/刷卡 + 退款券、多张券）同一口径；任一绑定缺失、
   * 不唯一或与订单/attempt/受保护状态不一致时整体返回 null（失败关闭）。
   */
  public async resolveApprovedRefundVouchers(
    actionIdInput: string,
    returnOrderGuidInput: string,
  ): Promise<readonly ProtectedRefundVoucherPrintMaterial[] | null> {
    const actionId = exactText(actionIdInput, 128);
    const returnOrderGuid = exactText(returnOrderGuidInput, 128);
    if (!actionId || !returnOrderGuid) return null;

    const rows = await this.connection.getAll<RefundVoucherBindingRow>(
      `SELECT
        plan.action_id AS plan_action_id,
        plan.return_order_guid AS plan_return_order_guid,
        plan.receipt_kind,
        plan.print_receipt,
        plan.print_job_id,
        action.action_id AS action_action_id,
        action.return_order_guid AS action_return_order_guid,
        action.state AS action_state,
        action.total_refund_cents AS action_total_refund_cents,
        action.store_code AS action_store_code,
        action.device_code AS action_device_code,
        action.cashier_id AS action_cashier_id,
        (
          SELECT TOTAL(action_allocation.signed_amount_cents)
          FROM return_action_allocations action_allocation
          WHERE action_allocation.action_id = action.action_id
        ) AS allocation_total_cents,
        (
          SELECT COUNT(*)
          FROM return_action_allocations card_allocation
          WHERE card_allocation.action_id = action.action_id
            AND card_allocation.method = 'card'
        ) AS card_allocation_count,
        (
          SELECT COUNT(*)
          FROM return_action_allocations voucher_allocation
          WHERE voucher_allocation.action_id = action.action_id
            AND voucher_allocation.method = 'voucher'
        ) AS voucher_allocation_count,
        (
          SELECT COUNT(*)
          FROM return_tender_attempt_bindings allocation_binding
          WHERE allocation_binding.action_id = allocation.action_id
            AND allocation_binding.allocation_id = allocation.allocation_id
        ) AS allocation_binding_count,
        allocation.action_id AS allocation_action_id,
        allocation.allocation_id,
        allocation.allocation_index,
        allocation.execution_kind,
        allocation.method AS allocation_method,
        allocation.signed_amount_cents AS allocation_signed_amount_cents,
        allocation.capacity_id AS allocation_capacity_id,
        allocation.original_order_guid AS allocation_original_order_guid,
        allocation.external_attempt_id,
        allocation.external_attempt_kind
          AS allocation_external_attempt_kind,
        allocation.external_action_id AS allocation_external_action_id,
        allocation.durable_attempt_id AS allocation_durable_attempt_id,
        allocation.status AS allocation_status,
        allocation.capacity_reservation_state,
        capacity.capacity_id,
        capacity.original_order_guid AS capacity_original_order_guid,
        capacity.method AS capacity_method,
        capacity.original_amount_cents AS capacity_original_amount_cents,
        capacity.remaining_amount_cents AS capacity_remaining_amount_cents,
        LENGTH(capacity.protected_context_ciphertext)
          AS capacity_context_length,
        binding.tender_guid AS binding_tender_guid,
        binding.action_id AS binding_action_id,
        binding.allocation_id AS binding_allocation_id,
        binding.external_attempt_kind AS binding_external_attempt_kind,
        binding.external_action_id AS binding_external_action_id,
        binding.durable_attempt_id AS binding_durable_attempt_id,
        o.order_guid,
        o.state AS order_state,
        o.store_code,
        o.device_code,
        o.cashier_id,
        o.total_cents,
        o.discount_cents,
        o.actual_amount_cents,
        (
          SELECT COUNT(*)
          FROM local_order_lines line
          WHERE line.order_guid = o.order_guid
        ) AS line_count,
        (
          SELECT COUNT(*)
          FROM local_order_lines line
          WHERE line.order_guid = o.order_guid
            AND line.line_kind = 'return'
        ) AS return_line_count,
        (
          SELECT TOTAL(order_tender.amount_cents)
          FROM order_tenders order_tender
          WHERE order_tender.order_guid = o.order_guid
        ) AS tender_total_cents,
        (
          SELECT COUNT(*)
          FROM order_tenders order_tender
          WHERE order_tender.order_guid = o.order_guid
            AND order_tender.amount_cents >= 0
        ) AS non_negative_tender_count,
        (
          SELECT COUNT(*)
          FROM order_tenders order_tender
          WHERE order_tender.order_guid = o.order_guid
            AND order_tender.method = 'voucher'
        ) AS voucher_tender_count,
        (
          SELECT COUNT(*)
          FROM payment_attempts approved
          WHERE approved.order_guid = o.order_guid
            AND approved.provider = 'voucher'
            AND approved.operation = 'refund'
            AND approved.state = 'Approved'
        ) AS approved_voucher_attempt_count,
        (
          SELECT COUNT(*)
          FROM payment_attempts approved
          INNER JOIN voucher_protected_attempt_states protected
            ON protected.attempt_id = approved.attempt_id
          WHERE approved.order_guid = o.order_guid
            AND approved.provider = 'voucher'
            AND approved.operation = 'refund'
            AND approved.state = 'Approved'
        ) AS protected_state_count,
        tender.tender_guid,
        tender.order_guid AS tender_order_guid,
        tender.method AS tender_method,
        tender.amount_cents AS tender_amount_cents,
        tender.payment_attempt_id,
        attempt.attempt_id,
        attempt.idempotency_key,
        attempt.order_guid AS attempt_order_guid,
        attempt.provider,
        attempt.operation,
        attempt.amount_cents AS attempt_amount_cents,
        attempt.state AS attempt_state,
        protected.protected_reference,
        protected.attempt_id AS state_attempt_id,
        protected.idempotency_key AS state_idempotency_key,
        protected.order_guid AS state_order_guid
       FROM return_fulfilment_plans plan
       INNER JOIN return_actions action
         ON action.action_id = plan.action_id
        AND action.return_order_guid = plan.return_order_guid
       INNER JOIN local_orders o
         ON o.order_guid = plan.return_order_guid
       INNER JOIN return_action_allocations allocation
         ON allocation.action_id = action.action_id
        AND allocation.method = 'voucher'
       INNER JOIN return_tender_capacities capacity
         ON capacity.capacity_id = allocation.capacity_id
       INNER JOIN return_tender_attempt_bindings binding
         ON binding.action_id = allocation.action_id
        AND binding.allocation_id = allocation.allocation_id
       INNER JOIN order_tenders tender
         ON tender.tender_guid = binding.tender_guid
       INNER JOIN payment_attempts attempt
         ON attempt.attempt_id = tender.payment_attempt_id
       INNER JOIN voucher_protected_attempt_states protected
         ON protected.attempt_id = attempt.attempt_id
       WHERE plan.action_id = ?
         AND plan.return_order_guid = ?
         AND plan.receipt_kind IN ('refund-voucher', 'refund-receipt')
       ORDER BY allocation.allocation_index, binding.tender_guid`,
      [actionId, returnOrderGuid],
    );
    const first = rows[0];
    if (!first) return null;
    const order = verifyOrderHeader(first, actionId, returnOrderGuid);
    if (!order) return null;
    // 每笔 voucher allocation 恰好一行；行数、券 tender 数、Approved attempt 数、受保护状态数必须一致。
    const voucherCount = safeInteger(first.voucher_allocation_count);
    if (
      voucherCount === null ||
      voucherCount <= 0 ||
      rows.length !== voucherCount ||
      safeInteger(first.voucher_tender_count) !== voucherCount ||
      safeInteger(first.approved_voucher_attempt_count) !== voucherCount ||
      safeInteger(first.protected_state_count) !== voucherCount
    ) {
      return null;
    }

    const bindings: VerifiedVoucherBinding[] = [];
    const seen = new Set<string>();
    for (const row of rows) {
      // 汇总列每行相同；逐行与首行比对，防止 JOIN 扇出混入其他退货单。
      if (!sameHeader(row, first)) return null;
      const binding = verifyVoucherBindingRow(row, actionId, returnOrderGuid);
      if (!binding) return null;
      const allocationId = String(row.allocation_id);
      const tenderGuid = String(row.tender_guid);
      for (const key of [
        `allocation:${allocationId}`,
        `tender:${tenderGuid}`,
        `attempt:${binding.attemptId}`,
        `protected:${binding.protectedReference}`,
      ]) {
        if (seen.has(key)) return null;
        seen.add(key);
      }
      bindings.push(binding);
    }

    const materials: ProtectedRefundVoucherPrintMaterial[] = [];
    for (const binding of bindings) {
      // 密文仓储负责 JSON/version/schema 与密文-明文绑定完整性；其 typed
      // integrity error 及 decrypt/Keychain/IO 原错必须原样穿透。
      const state = await this.protectedTokens.getByAttempt(binding.attemptId);
      if (!state) return null;
      const voucherCode = printableVoucherCode(state.voucherCode);
      if (
        !binding.protectedReference ||
        binding.stateAttemptId !== binding.attemptId ||
        binding.stateIdempotencyKey !== binding.idempotencyKey ||
        binding.stateOrderGuid !== returnOrderGuid ||
        state.protectedReference !== binding.protectedReference ||
        state.attemptId !== binding.attemptId ||
        state.idempotencyKey !== binding.idempotencyKey ||
        state.orderGuid !== returnOrderGuid ||
        state.operation !== "refund" ||
        state.phase !== "approved" ||
        state.storeCode !== order.storeCode ||
        state.cashierId !== order.cashierId ||
        state.amountCents !== binding.signedAmountCents ||
        state.reservationToken !== null ||
        !isCanonicalIso(state.expiresAtIso) ||
        !voucherCode
      ) {
        return null;
      }
      materials.push(Object.freeze({
        returnOrderGuid,
        voucherCode,
        refundAmountCents: -binding.signedAmountCents,
      }));
    }
    return Object.freeze(materials);
  }
}

/** 退货单、action 与 fulfilment plan 的整体一致性（与具体哪张券无关）。 */
function verifyOrderHeader(
  row: RefundVoucherBindingRow,
  actionId: string,
  returnOrderGuid: string,
): OrderIdentity | null {
  const storeCode = exactText(row.store_code, 64);
  const deviceCode = exactText(row.device_code, 128);
  const cashierId = exactText(row.cashier_id, 128);
  const signedAmountCents = safeInteger(row.actual_amount_cents);
  const lineCount = safeInteger(row.line_count);
  const returnLineCount = safeInteger(row.return_line_count);
  const cardAllocationCount = safeInteger(row.card_allocation_count);
  if (
    exactText(row.plan_action_id, 128) !== actionId ||
    exactText(row.plan_return_order_guid, 128) !== returnOrderGuid ||
    // 与履约策略一致：含刷卡 → 退货小票（追加券面）；不含刷卡 → 纯券面。
    cardAllocationCount === null ||
    (row.receipt_kind === "refund-receipt"
      ? cardAllocationCount <= 0
      : row.receipt_kind !== "refund-voucher" || cardAllocationCount !== 0) ||
    safeInteger(row.print_receipt) !== 1 ||
    !exactText(row.print_job_id, 128) ||
    exactText(row.action_action_id, 128) !== actionId ||
    exactText(row.action_return_order_guid, 128) !== returnOrderGuid ||
    row.action_state !== "completed" ||
    signedAmountCents === null ||
    signedAmountCents >= 0 ||
    safeInteger(row.action_total_refund_cents) !== -signedAmountCents ||
    // SQLite TOTAL() 返回浮点；整数分合计须精确等于订单实退金额。
    row.allocation_total_cents !== signedAmountCents ||
    row.tender_total_cents !== signedAmountCents ||
    safeInteger(row.non_negative_tender_count) !== 0 ||
    !storeCode ||
    !deviceCode ||
    !cashierId ||
    exactText(row.action_store_code, 64) !== storeCode ||
    exactText(row.action_device_code, 128) !== deviceCode ||
    exactText(row.action_cashier_id, 128) !== cashierId ||
    exactText(row.order_guid, 128) !== returnOrderGuid ||
    !COMPLETED_ORDER_STATES.has(row.order_state as string) ||
    safeInteger(row.total_cents) !== signedAmountCents ||
    safeInteger(row.discount_cents) !== 0 ||
    lineCount === null ||
    lineCount <= 0 ||
    returnLineCount !== lineCount
  ) {
    return null;
  }
  return { storeCode, cashierId };
}

const HEADER_COLUMNS = [
  "plan_action_id",
  "plan_return_order_guid",
  "receipt_kind",
  "print_receipt",
  "print_job_id",
  "action_action_id",
  "action_return_order_guid",
  "action_state",
  "action_total_refund_cents",
  "action_store_code",
  "action_device_code",
  "action_cashier_id",
  "allocation_total_cents",
  "card_allocation_count",
  "voucher_allocation_count",
  "order_guid",
  "order_state",
  "store_code",
  "device_code",
  "cashier_id",
  "total_cents",
  "discount_cents",
  "actual_amount_cents",
  "line_count",
  "return_line_count",
  "tender_total_cents",
  "non_negative_tender_count",
  "voucher_tender_count",
  "approved_voucher_attempt_count",
  "protected_state_count",
] as const satisfies readonly (keyof RefundVoucherBindingRow)[];

function sameHeader(
  row: RefundVoucherBindingRow,
  first: RefundVoucherBindingRow,
): boolean {
  return HEADER_COLUMNS.every((column) => row[column] === first[column]);
}

/** 单笔 voucher allocation → binding → tender → Approved attempt → 受保护状态的公开链路。 */
function verifyVoucherBindingRow(
  row: RefundVoucherBindingRow,
  actionId: string,
  returnOrderGuid: string,
): VerifiedVoucherBinding | null {
  const attemptId = exactText(row.attempt_id, 128);
  const idempotencyKey = exactText(row.idempotency_key, 256);
  const signedAmountCents = safeInteger(row.allocation_signed_amount_cents);
  const allocationId = exactText(row.allocation_id, 128);
  const capacityId = exactText(row.capacity_id, 128);
  const originalOrderGuid = exactText(
    row.allocation_original_order_guid,
    128,
  );
  const externalAttemptId = exactText(row.external_attempt_id, 128);
  const externalActionId = exactText(
    row.allocation_external_action_id,
    128,
  );
  const durableAttemptId = exactText(
    row.allocation_durable_attempt_id,
    128,
  );
  const tenderGuid = exactText(row.tender_guid, 128);
  const protectedReference = exactText(row.protected_reference, 128);
  const capacityOriginalAmountCents = safeInteger(
    row.capacity_original_amount_cents,
  );
  const capacityRemainingAmountCents = safeInteger(
    row.capacity_remaining_amount_cents,
  );
  if (
    signedAmountCents === null ||
    signedAmountCents >= 0 ||
    safeInteger(row.allocation_binding_count) !== 1 ||
    exactText(row.allocation_action_id, 128) !== actionId ||
    !allocationId ||
    safeInteger(row.allocation_index) === null ||
    Number(row.allocation_index) < 0 ||
    row.execution_kind !== "online-refund" ||
    row.allocation_method !== "voucher" ||
    exactText(row.allocation_capacity_id, 128) !== capacityId ||
    !capacityId ||
    !originalOrderGuid ||
    !externalAttemptId ||
    row.allocation_external_attempt_kind !== "payment-provider" ||
    !externalActionId ||
    externalActionId !== externalAttemptId ||
    !durableAttemptId ||
    row.allocation_status !== "completed" ||
    row.capacity_reservation_state !== "Committed" ||
    exactText(row.capacity_original_order_guid, 128) !==
      originalOrderGuid ||
    // 退款券可来自原礼券额度原路退回，也可代替刷卡/现金额度签发。
    (row.capacity_method !== "voucher" &&
      row.capacity_method !== "card" &&
      row.capacity_method !== "cash") ||
    capacityOriginalAmountCents === null ||
    capacityOriginalAmountCents <= 0 ||
    capacityOriginalAmountCents < -signedAmountCents ||
    capacityRemainingAmountCents === null ||
    capacityRemainingAmountCents < 0 ||
    capacityRemainingAmountCents > capacityOriginalAmountCents ||
    // 现金额度按设计不保存 provider context，其余额度必须有受保护 context。
    (row.capacity_method === "cash"
      ? row.capacity_context_length !== null &&
        safeInteger(row.capacity_context_length) !== 0
      : safeInteger(row.capacity_context_length) === null ||
        Number(row.capacity_context_length) <= 0) ||
    exactText(row.binding_tender_guid, 128) !== tenderGuid ||
    !tenderGuid ||
    exactText(row.binding_action_id, 128) !== actionId ||
    exactText(row.binding_allocation_id, 128) !== allocationId ||
    row.binding_external_attempt_kind !== "payment-provider" ||
    exactText(row.binding_external_action_id, 128) !== externalActionId ||
    exactText(row.binding_durable_attempt_id, 128) !== durableAttemptId ||
    exactText(row.tender_order_guid, 128) !== returnOrderGuid ||
    row.tender_method !== "voucher" ||
    safeInteger(row.tender_amount_cents) !== signedAmountCents ||
    exactText(row.payment_attempt_id, 128) !== attemptId ||
    !attemptId ||
    attemptId !== durableAttemptId ||
    !idempotencyKey ||
    exactText(row.attempt_order_guid, 128) !== returnOrderGuid ||
    row.provider !== "voucher" ||
    row.operation !== "refund" ||
    safeInteger(row.attempt_amount_cents) !== signedAmountCents ||
    row.attempt_state !== "Approved"
  ) {
    return null;
  }
  return {
    attemptId,
    idempotencyKey,
    signedAmountCents,
    protectedReference,
    stateAttemptId: exactText(row.state_attempt_id, 128),
    stateIdempotencyKey: exactText(row.state_idempotency_key, 256),
    stateOrderGuid: exactText(row.state_order_guid, 128),
  };
}

function exactText(value: unknown, maxLength: number): string | null {
  if (
    typeof value !== "string" ||
    value.length === 0 ||
    value.length > maxLength ||
    value.trim() !== value ||
    /[\u0000-\u001f\u007f]/u.test(value)
  ) {
    return null;
  }
  return value;
}

function safeInteger(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value)
    ? value
    : null;
}

function printableVoucherCode(value: unknown): string | null {
  return (
    typeof value === "string" &&
      value.length <= 80 &&
      value.trim() === value &&
      /^[\x20-\x7e]+$/u.test(value)
  )
    ? value
    : null;
}

function isCanonicalIso(value: unknown): boolean {
  if (typeof value !== "string") return false;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) && new Date(parsed).toISOString() === value;
}

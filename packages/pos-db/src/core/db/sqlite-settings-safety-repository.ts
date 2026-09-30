import type { SqliteConnectionPort } from "./types";

/**
 * Settings 只需要判断本地耐久事实能否安全跨配置切换；不暴露订单、支付或队列内容。
 * 活动购物车属于内存会话，由组合根在同一外层门闩内另行合并。
 */
export type SettingsSafetySnapshot = Readonly<{
  pendingDurableWriteCount: number;
  pendingReturnCount: number;
  pendingSaleCount: number;
  unresolvedPaymentCount: number;
  paymentConfigurationSensitiveOrderCount: number;
}>;

/**
 * 读取当前 SQLCipher 账本的一致安全快照。任何未知枚举、无行订单或数值解析异常
 * 都失败关闭，避免危险设置操作把损坏状态误判为“无待处理数据”。
 */
export class SqliteSettingsSafetyRepository {
  public constructor(private readonly db: SqliteConnectionPort) {}

  public read(): Promise<SettingsSafetySnapshot> {
    return this.db.withExclusiveTransaction(async (transaction) => {
      const orderRows = await transaction.getAll<OrderSafetyRow>(
        `SELECT
           orders.order_guid,
           orders.state,
           COUNT(lines.line_id) AS line_count,
           SUM(CASE WHEN lines.line_kind = 'sale' THEN 1 ELSE 0 END)
             AS sale_line_count,
           SUM(CASE WHEN lines.line_kind = 'return' THEN 1 ELSE 0 END)
             AS return_line_count,
           SUM(
             CASE
               WHEN lines.line_id IS NOT NULL
                 AND lines.line_kind NOT IN ('sale', 'return')
               THEN 1
               ELSE 0
             END
           ) AS invalid_line_count
         FROM local_orders orders
         LEFT JOIN local_order_lines lines
           ON lines.order_guid = orders.order_guid
         GROUP BY orders.order_guid, orders.state
         ORDER BY orders.order_guid`,
      );
      const orderCounts = countPendingOrders(orderRows);

      const paymentRows = await transaction.getAll<PaymentSafetyRow>(
        `SELECT
           attempts.attempt_id,
           attempts.order_guid,
           attempts.state,
           attempts.provider,
           attempts.operation,
           attempts.amount_cents,
           orders.state AS order_state,
           COUNT(tenders.tender_guid) AS bound_tender_count,
           SUM(
             CASE
               WHEN tenders.tender_guid IS NOT NULL
                 AND tenders.method NOT IN ('cash', 'card', 'voucher')
               THEN 1
               ELSE 0
             END
           ) AS invalid_tender_count,
           SUM(
             CASE
               WHEN tenders.tender_guid IS NOT NULL
                 AND tenders.order_guid = attempts.order_guid
                 AND tenders.amount_cents = attempts.amount_cents
                 AND (
                   (
                     attempts.provider IN ('square', 'linkly-cloud', 'manual-card')
                     AND tenders.method = 'card'
                   )
                   OR (
                     attempts.provider = 'voucher'
                     AND tenders.method = 'voucher'
                   )
                 )
               THEN 1
               ELSE 0
             END
           ) AS matching_tender_count
         FROM payment_attempts attempts
         LEFT JOIN local_orders orders
           ON orders.order_guid = attempts.order_guid
         LEFT JOIN order_tenders tenders
           ON tenders.payment_attempt_id = attempts.attempt_id
         GROUP BY
           attempts.attempt_id,
           attempts.order_guid,
           attempts.state,
           attempts.provider,
           attempts.operation,
           attempts.amount_cents,
           orders.state
         ORDER BY attempts.attempt_id`,
      );
      const unresolvedPaymentCount = countUnresolvedPayments(paymentRows);
      const paymentConfigurationSensitiveOrderCount =
        countPaymentConfigurationSensitiveOrders(paymentRows);

      const outboxRows = await transaction.getAll<OutboxSafetyRow>(
        `SELECT message_id, kind, state
         FROM outbox_messages
         ORDER BY message_id`,
      );
      const auditRows = await transaction.getAll<AuditSafetyRow>(
        `SELECT event_id, uploaded_at_iso, delivery_state
         FROM audit_events
         ORDER BY event_id`,
      );
      const printRows = await transaction.getAll<PrintSafetyRow>(
        `SELECT job_id, state
         FROM print_jobs
         ORDER BY job_id`,
      );
      const drawerRows = await transaction.getAll<DrawerSafetyRow>(
        `SELECT event_id, state
         FROM drawer_events
         ORDER BY event_id`,
      );
      const pendingDurableWriteCount = countPendingDurableWrites({
        outboxRows,
        auditRows,
        printRows,
        drawerRows,
      });

      return {
        pendingDurableWriteCount,
        pendingReturnCount: orderCounts.pendingReturnCount,
        pendingSaleCount: orderCounts.pendingSaleCount,
        unresolvedPaymentCount,
        paymentConfigurationSensitiveOrderCount,
      };
    });
  }
}

type OrderSafetyRow = Readonly<{
  order_guid: unknown;
  state: unknown;
  line_count: unknown;
  sale_line_count: unknown;
  return_line_count: unknown;
  invalid_line_count: unknown;
}>;

type PaymentSafetyRow = Readonly<{
  attempt_id: unknown;
  order_guid: unknown;
  state: unknown;
  provider: unknown;
  operation: unknown;
  amount_cents: unknown;
  order_state: unknown;
  bound_tender_count: unknown;
  invalid_tender_count: unknown;
  matching_tender_count: unknown;
}>;

type OutboxSafetyRow = Readonly<{
  message_id: unknown;
  kind: unknown;
  state: unknown;
}>;

type AuditSafetyRow = Readonly<{
  event_id: unknown;
  uploaded_at_iso: unknown;
  delivery_state: unknown;
}>;

type PrintSafetyRow = Readonly<{
  job_id: unknown;
  state: unknown;
}>;

type DrawerSafetyRow = Readonly<{
  event_id: unknown;
  state: unknown;
}>;

type PersistedOrderState =
  | "Draft"
  | "Completing"
  | "CompletedLocal"
  | "PendingSync"
  | "Syncing"
  | "Synced"
  | "Blocked403"
  | "Rejected";

function countPendingOrders(
  rows: readonly OrderSafetyRow[],
): Readonly<{
  pendingReturnCount: number;
  pendingSaleCount: number;
}> {
  let pendingReturnCount = 0;
  let pendingSaleCount = 0;

  for (const row of rows) {
    requiredIdentifier(row.order_guid, "order guid");
    const state = orderState(row.state);
    const lineCount = nonNegativeInteger(row.line_count, "order line count");
    const saleLineCount = nonNegativeInteger(
      row.sale_line_count,
      "sale line count",
    );
    const returnLineCount = nonNegativeInteger(
      row.return_line_count,
      "return line count",
    );
    const invalidLineCount = nonNegativeInteger(
      row.invalid_line_count,
      "invalid line count",
    );
    if (
      lineCount === 0 ||
      invalidLineCount !== 0 ||
      saleLineCount + returnLineCount !== lineCount
    ) {
      throw new Error("Invalid persisted settings safety order lines.");
    }
    if (state === "Synced") continue;
    // 一笔订单若同时包含销售和退货行，两类风险都必须显式保留。
    if (saleLineCount > 0) {
      pendingSaleCount = incrementSafe(
        pendingSaleCount,
        "pending sale count",
      );
    }
    if (returnLineCount > 0) {
      pendingReturnCount = incrementSafe(
        pendingReturnCount,
        "pending return count",
      );
    }
  }
  return { pendingReturnCount, pendingSaleCount };
}

function countUnresolvedPayments(rows: readonly PaymentSafetyRow[]): number {
  let unresolvedPaymentCount = 0;
  for (const row of rows) {
    requiredIdentifier(row.attempt_id, "payment attempt id");
    const state = paymentState(row.state);
    paymentProvider(row.provider);
    const operation = paymentOperation(row.operation);
    const amountCents = safeInteger(row.amount_cents, "payment amount");
    const persistedOrderState = orderState(row.order_state);
    const boundTenderCount = nonNegativeInteger(
      row.bound_tender_count,
      "bound payment tender count",
    );
    const matchingTenderCount = nonNegativeInteger(
      row.matching_tender_count,
      "matching payment tender count",
    );
    const invalidTenderCount = nonNegativeInteger(
      row.invalid_tender_count,
      "invalid payment tender count",
    );
    if (
      amountCents === 0 ||
      (operation === "purchase" && amountCents < 0) ||
      (operation === "refund" && amountCents > 0) ||
      boundTenderCount > 1 ||
      invalidTenderCount !== 0 ||
      matchingTenderCount > boundTenderCount
    ) {
      throw new Error("Invalid persisted settings safety payment facts.");
    }

    const unresolved =
      state === "Created" ||
      state === "Submitted" ||
      state === "Pending" ||
      state === "Unknown" ||
      (state === "Approved" &&
        (matchingTenderCount !== 1 ||
          persistedOrderState === "Draft" ||
          persistedOrderState === "Completing"));
    if (unresolved) {
      unresolvedPaymentCount = incrementSafe(
        unresolvedPaymentCount,
        "unresolved payment count",
      );
    }

  }
  return unresolvedPaymentCount;
}

function countPaymentConfigurationSensitiveOrders(
  rows: readonly PaymentSafetyRow[],
): number {
  const orderGuids = new Set<string>();
  for (const row of rows) {
    const orderGuid = requiredIdentifier(row.order_guid, "payment order guid");
    const provider = paymentProvider(row.provider);
    const persistedOrderState = orderState(row.order_state);
    const matchingTenderCount = nonNegativeInteger(
      row.matching_tender_count,
      "matching payment tender count",
    );
    // 订单同步仍会读取当前 Linkly 环境，因此只保护已精确落成 card tender 的订单。
    if (
      provider === "linkly-cloud" &&
      persistedOrderState !== "Synced" &&
      matchingTenderCount === 1
    ) {
      orderGuids.add(orderGuid);
    }
  }
  return orderGuids.size;
}

function countPendingDurableWrites(input: Readonly<{
  outboxRows: readonly OutboxSafetyRow[];
  auditRows: readonly AuditSafetyRow[];
  printRows: readonly PrintSafetyRow[];
  drawerRows: readonly DrawerSafetyRow[];
}>): number {
  let count = 0;
  for (const row of input.outboxRows) {
    requiredIdentifier(row.message_id, "outbox message id");
    outboxKind(row.kind);
    if (outboxState(row.state) !== "succeeded") {
      count = incrementSafe(count, "pending durable write count");
    }
  }
  for (const row of input.auditRows) {
    requiredIdentifier(row.event_id, "audit event id");
    const deliveryState = auditDeliveryState(row.delivery_state);
    if (row.uploaded_at_iso === null) {
      if (deliveryState === "uploaded") {
        throw new Error("Invalid persisted settings safety audit delivery.");
      }
      // rejected 是投递终态：服务端已拒收或本地诊断被隔离，上传器永不再选中；
      // 计入待处理只会让危险操作永久阻断且无可处理路径，业务事实仍保留在本地。
      if (deliveryState === "pending") {
        count = incrementSafe(count, "pending durable write count");
      }
    } else {
      canonicalIso(row.uploaded_at_iso, "audit uploaded timestamp");
    }
  }
  for (const row of input.printRows) {
    requiredIdentifier(row.job_id, "print job id");
    if (printState(row.state) !== "Printed") {
      count = incrementSafe(count, "pending durable write count");
    }
  }
  for (const row of input.drawerRows) {
    requiredIdentifier(row.event_id, "drawer event id");
    if (drawerState(row.state) !== "Completed") {
      count = incrementSafe(count, "pending durable write count");
    }
  }
  return count;
}

function orderState(value: unknown): PersistedOrderState {
  if (
    value === "Draft" ||
    value === "Completing" ||
    value === "CompletedLocal" ||
    value === "PendingSync" ||
    value === "Syncing" ||
    value === "Synced" ||
    value === "Blocked403" ||
    value === "Rejected"
  ) {
    return value;
  }
  throw new Error("Invalid persisted settings safety order state.");
}

function paymentState(
  value: unknown,
):
  | "Created"
  | "Submitted"
  | "Pending"
  | "Approved"
  | "Declined"
  | "Cancelled"
  | "Unknown" {
  if (
    value === "Created" ||
    value === "Submitted" ||
    value === "Pending" ||
    value === "Approved" ||
    value === "Declined" ||
    value === "Cancelled" ||
    value === "Unknown"
  ) {
    return value;
  }
  throw new Error("Invalid persisted settings safety payment state.");
}

// 必须与 PaymentProvider 契约及上方 SQL 的 provider 集合保持一致；漏一个正式
// provider 会让含该支付记录的设备在所有危险设置操作上永久失败关闭。
function paymentProvider(
  value: unknown,
): "square" | "linkly-cloud" | "voucher" | "manual-card" {
  if (
    value === "square" ||
    value === "linkly-cloud" ||
    value === "voucher" ||
    value === "manual-card"
  ) {
    return value;
  }
  throw new Error("Invalid persisted settings safety payment provider.");
}

function paymentOperation(value: unknown): "purchase" | "refund" {
  if (value === "purchase" || value === "refund") return value;
  throw new Error("Invalid persisted settings safety payment operation.");
}

function outboxKind(value: unknown): "order-sync" | "audit-batch" {
  if (value === "order-sync" || value === "audit-batch") return value;
  throw new Error("Invalid persisted settings safety outbox kind.");
}

function outboxState(
  value: unknown,
): "pending" | "leased" | "succeeded" | "blocked403" | "rejected" {
  if (
    value === "pending" ||
    value === "leased" ||
    value === "succeeded" ||
    value === "blocked403" ||
    value === "rejected"
  ) {
    return value;
  }
  throw new Error("Invalid persisted settings safety outbox state.");
}

function auditDeliveryState(
  value: unknown,
): "pending" | "uploaded" | "rejected" {
  if (value === "pending" || value === "uploaded" || value === "rejected") {
    return value;
  }
  throw new Error("Invalid persisted settings safety audit delivery state.");
}

function printState(
  value: unknown,
): "Queued" | "Sending" | "Printed" | "Failed" | "Ambiguous" {
  if (
    value === "Queued" ||
    value === "Sending" ||
    value === "Printed" ||
    value === "Failed" ||
    value === "Ambiguous"
  ) {
    return value;
  }
  throw new Error("Invalid persisted settings safety print state.");
}

function drawerState(
  value: unknown,
): "Required" | "Requested" | "Completed" | "Failed" | "Unknown" {
  if (
    value === "Required" ||
    value === "Requested" ||
    value === "Completed" ||
    value === "Failed" ||
    value === "Unknown"
  ) {
    return value;
  }
  throw new Error("Invalid persisted settings safety drawer state.");
}

function requiredIdentifier(value: unknown, label: string): string {
  if (
    typeof value !== "string" ||
    !value.trim() ||
    value.length > 512 ||
    /[\u0000-\u001f\u007f]/u.test(value)
  ) {
    throw new Error(`Invalid persisted settings safety ${label}.`);
  }
  return value;
}

function safeInteger(value: unknown, label: string): number {
  if (typeof value === "string" && !/^-?(0|[1-9]\d*)$/u.test(value)) {
    throw new Error(`Invalid persisted settings safety ${label}.`);
  }
  if (
    typeof value !== "number" &&
    typeof value !== "string" &&
    typeof value !== "bigint"
  ) {
    throw new Error(`Invalid persisted settings safety ${label}.`);
  }
  const numberValue = Number(value);
  if (!Number.isSafeInteger(numberValue)) {
    throw new Error(`Invalid persisted settings safety ${label}.`);
  }
  return numberValue;
}

function nonNegativeInteger(value: unknown, label: string): number {
  const parsed = safeInteger(value, label);
  if (parsed < 0) {
    throw new Error(`Invalid persisted settings safety ${label}.`);
  }
  return parsed;
}

function incrementSafe(value: number, label: string): number {
  const next = value + 1;
  if (!Number.isSafeInteger(next)) {
    throw new Error(`Invalid persisted settings safety ${label}.`);
  }
  return next;
}

function canonicalIso(value: unknown, label: string): string {
  if (typeof value !== "string") {
    throw new Error(`Invalid persisted settings safety ${label}.`);
  }
  const timestamp = Date.parse(value);
  if (
    !Number.isFinite(timestamp) ||
    new Date(timestamp).toISOString() !== value
  ) {
    throw new Error(`Invalid persisted settings safety ${label}.`);
  }
  return value;
}

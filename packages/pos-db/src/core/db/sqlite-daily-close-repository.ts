import {
  AUD_CASH_DENOMINATIONS_CENTS,
  normalizeDailyCloseCounts,
  sumCashRoundingCents,
  type AudCashDenominationCents,
  type DailyCloseArchive,
  type DailyCloseArchiveCommit,
  type DailyCloseRepositoryPort,
  type DailyCloseScope,
  type DailyCloseSummary,
  type DailyCloseTenderBreakdown,
  type DailyCloseTenderMethod,
} from "@hb/pos-domain/core/contracts/daily-close";
import type { AuditEventDraft } from "@hb/pos-domain/core/contracts/order";

import type { SqliteConnectionPort, SqlValue } from "./types";

const COMPLETED_ORDER_STATES = Object.freeze([
  "CompletedLocal",
  "PendingSync",
  "Syncing",
  "Synced",
  "Blocked403",
  "Rejected",
] as const);

const TENDER_METHODS = Object.freeze([
  "cash",
  "card",
  "voucher",
] as const);

type CountRow = Readonly<{ count: unknown }>;
type QuantityRow = Readonly<{ quantity: unknown }>;
type TenderSummaryRow = Readonly<{
  method: unknown;
  sales_cents: unknown;
  refund_cents: unknown;
}>;
type OrderCashRow = Readonly<{ cash_cents: unknown }>;
type DailyCloseRow = Readonly<{
  close_id: unknown;
  business_date: unknown;
  period_from_iso: unknown;
  period_to_iso: unknown;
  store_code: unknown;
  device_code: unknown;
  saved_cashier_id: unknown;
  saved_cashier_name: unknown;
  order_count: unknown;
  return_quantity: unknown;
  expected_cash_cents: unknown;
  cash_rounding_cents: unknown;
  counted_cash_cents: unknown;
  notes_subtotal_cents: unknown;
  coins_subtotal_cents: unknown;
  variance_cents: unknown;
  terminal_audit_event_id: unknown;
  saved_at_iso: unknown;
  source_kind: unknown;
  state: unknown;
}>;
type DailyCloseTenderRow = Readonly<{
  tender_method: unknown;
  sales_cents: unknown;
  refund_cents: unknown;
  net_cents: unknown;
}>;
type DailyCloseDenominationRow = Readonly<{
  denomination_cents: unknown;
  quantity: unknown;
  subtotal_cents: unknown;
}>;
type AuditRow = Readonly<{
  event_id: unknown;
  event_type: unknown;
  occurred_at_iso: unknown;
  order_guid: unknown;
  correlation_id: unknown;
  payload_json: unknown;
}>;

export class SqliteDailyCloseRepository
  implements DailyCloseRepositoryPort
{
  public constructor(private readonly connection: SqliteConnectionPort) {}

  public summarize(scope: DailyCloseScope): Promise<DailyCloseSummary> {
    const normalizedScope = validateScope(scope);
    // 三组聚合必须共享同一 SQLite 快照，不能让并发完成订单夹在查询之间。
    return this.connection.withExclusiveTransaction((transaction) =>
      summarizeSnapshot(transaction, normalizedScope),
    );
  }

  public saveArchive(
    input: DailyCloseArchiveCommit,
  ): Promise<Readonly<{ replayed: boolean; archive: DailyCloseArchive }>> {
    const archive = validateArchive(input.archive);
    const audit = validateAudit(input.audit, archive);

    return this.connection.withExclusiveTransaction(async (transaction) => {
      const existingRow = await transaction.getFirst<DailyCloseRow>(
        `SELECT *
         FROM local_daily_closes
         WHERE close_id = ?`,
        [archive.closeId],
      );
      if (existingRow) {
        const existing = await readArchive(transaction, existingRow);
        const existingAudit = await readAudit(
          transaction,
          requiredText(
            existingRow.terminal_audit_event_id,
            "daily close audit id",
          ),
        );
        if (
          stableJson(existing) !== stableJson(archive) ||
          stableJson(existingAudit) !== stableJson(audit)
        ) {
          throw new Error("Daily close replay does not match persisted facts.");
        }
        return Object.freeze({ replayed: true, archive: existing });
      }

      await insertDailyCloseAudit(transaction, archive, audit);
      await transaction.run(
        `INSERT INTO local_daily_closes (
          close_id, business_date, period_from_iso, period_to_iso,
          store_code, device_code, saved_cashier_id, saved_cashier_name,
          order_count, return_quantity, expected_cash_cents,
          counted_cash_cents, notes_subtotal_cents, coins_subtotal_cents,
          variance_cents, terminal_audit_event_id, saved_at_iso,
          cash_rounding_cents, source_kind, state
        ) VALUES (
          ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?,
          'native', 'Preparing'
        )`,
        [
          archive.closeId,
          archive.businessDate,
          archive.periodFromIso,
          archive.periodToIso,
          archive.storeCode,
          archive.deviceCode,
          archive.savedCashierId,
          archive.savedCashierName,
          archive.orderCount,
          archive.returnQuantity,
          archive.expectedCashCents,
          archive.countedCashCents,
          archive.notesSubtotalCents,
          archive.coinsSubtotalCents,
          archive.varianceCents,
          audit.eventId,
          archive.savedAtIso,
          archive.cashRoundingCents,
        ],
      );
      for (const tender of archive.tenders) {
        await transaction.run(
          `INSERT INTO daily_close_totals (
            close_id, tender_method, sales_cents, refund_cents, net_cents
          ) VALUES (?, ?, ?, ?, ?)`,
          [
            archive.closeId,
            tender.method,
            tender.salesCents,
            tender.refundCents,
            tender.netCents,
          ],
        );
      }
      for (const count of archive.denominations) {
        await transaction.run(
          `INSERT INTO cash_denominations (
            close_id, denomination_cents, quantity, subtotal_cents
          ) VALUES (?, ?, ?, ?)`,
          [
            archive.closeId,
            count.denominationCents,
            count.quantity,
            count.subtotalCents,
          ],
        );
      }
      const finalized = await transaction.run(
        `UPDATE local_daily_closes
         SET state = 'Archived'
         WHERE close_id = ? AND state = 'Preparing'`,
        [archive.closeId],
      );
      if (finalized.changes !== 1) {
        throw new Error("Daily close archive could not be finalized.");
      }
      const persistedRow = await transaction.getFirst<DailyCloseRow>(
        "SELECT * FROM local_daily_closes WHERE close_id = ?",
        [archive.closeId],
      );
      if (!persistedRow) {
        throw new Error("Daily close archive disappeared during save.");
      }
      return Object.freeze({
        replayed: false,
        archive: await readArchive(transaction, persistedRow),
      });
    });
  }

  public async getArchive(closeId: string): Promise<DailyCloseArchive | null> {
    const normalizedCloseId = boundedText(closeId, "daily close id", 128);
    const row = await this.connection.getFirst<DailyCloseRow>(
      `SELECT *
       FROM local_daily_closes
       WHERE close_id = ? AND state = 'Archived'`,
      [normalizedCloseId],
    );
    return row ? readArchive(this.connection, row) : null;
  }

  public async listArchives(
    scope: Readonly<{
      storeCode: string;
      deviceCode: string;
      businessDate?: string | null;
      limit: number;
    }>,
  ): Promise<readonly DailyCloseArchive[]> {
    const storeCode = boundedText(scope.storeCode, "store code", 128);
    const deviceCode = boundedText(scope.deviceCode, "device code", 128);
    const limit = positiveLimit(scope.limit);
    const businessDate =
      scope.businessDate === undefined || scope.businessDate === null
        ? null
        : validateBusinessDate(scope.businessDate);
    const parameters: SqlValue[] = [storeCode, deviceCode];
    let dateFilter = "";
    if (businessDate !== null) {
      dateFilter = "AND business_date = ?";
      parameters.push(businessDate);
    }
    parameters.push(limit);
    const rows = await this.connection.getAll<DailyCloseRow>(
      `SELECT *
       FROM local_daily_closes
       WHERE store_code = ?
         AND device_code = ?
         AND state = 'Archived'
         ${dateFilter}
       ORDER BY saved_at_iso DESC, close_id DESC
       LIMIT ?`,
      parameters,
    );
    const archives: DailyCloseArchive[] = [];
    for (const row of rows) {
      archives.push(await readArchive(this.connection, row));
    }
    return Object.freeze(archives);
  }
}

/** 日结没有订单可供触发器反查，必须从已验证且即将归档的事实冻结可信终端。 */
async function insertDailyCloseAudit(
  transaction: SqliteConnectionPort,
  archive: DailyCloseArchive,
  audit: AuditEventDraft,
): Promise<void> {
  try {
    await transaction.run(
      `INSERT INTO audit_events (
        event_id, event_type, occurred_at_iso, order_guid, correlation_id,
        payload_json, uploaded_at_iso, delivery_state, attempt_count,
        next_attempt_at_iso, last_error_code, scope_store_code, scope_device_code
      ) VALUES (?, ?, ?, NULL, ?, ?, NULL, 'pending', 0, ?, NULL, ?, ?)`,
      [
        audit.eventId,
        audit.eventType,
        audit.occurredAtIso,
        audit.correlationId,
        stableJson(audit.payload),
        audit.occurredAtIso,
        archive.storeCode,
        archive.deviceCode,
      ],
    );
  } catch (error) {
    if (!isLegacyAuditEventsSchema(error)) throw error;

    // M26 前旧库逐级升级时没有投递列；M30 现行库不会走此兼容分支。
    await transaction.run(
      `INSERT INTO audit_events (
        event_id, event_type, occurred_at_iso, order_guid,
        correlation_id, payload_json, uploaded_at_iso
      ) VALUES (?, ?, ?, NULL, ?, ?, NULL)`,
      [
        audit.eventId,
        audit.eventType,
        audit.occurredAtIso,
        audit.correlationId,
        stableJson(audit.payload),
      ],
    );
  }
}

function isLegacyAuditEventsSchema(error: unknown): boolean {
  return error instanceof Error
    && /audit_events has no column named (delivery_state|scope_store_code)/i.test(error.message);
}

async function summarizeSnapshot(
  connection: SqliteConnectionPort,
  normalizedScope: DailyCloseScope,
): Promise<DailyCloseSummary> {
  const orderFilter = completedOrderFilter();
  const orderParameters = scopeParameters(normalizedScope);
  const countRow = await connection.getFirst<CountRow>(
    `SELECT COUNT(*) AS count
     FROM local_orders orders
     WHERE ${orderFilter}`,
    orderParameters,
  );
  const orderCount = safeInteger(countRow?.count, "daily close order count");
  if (orderCount < 0) {
    throw new Error("Daily close order count is invalid.");
  }

  const tenderRows = await connection.getAll<TenderSummaryRow>(
    `SELECT
       tenders.method AS method,
       COALESCE(SUM(
         CASE WHEN tenders.amount_cents > 0
           THEN tenders.amount_cents ELSE 0 END
       ), 0) AS sales_cents,
       COALESCE(SUM(
         CASE WHEN tenders.amount_cents < 0
           THEN tenders.amount_cents ELSE 0 END
       ), 0) AS refund_cents
     FROM order_tenders tenders
     INNER JOIN local_orders orders
       ON orders.order_guid = tenders.order_guid
     WHERE ${orderFilter}
       AND tenders.method IN ('cash', 'card', 'voucher')
     GROUP BY tenders.method`,
    orderParameters,
  );
  const byMethod = new Map<
    DailyCloseTenderMethod,
    DailyCloseTenderBreakdown
  >();
  for (const row of tenderRows) {
    const method = tenderMethod(row.method);
    const salesCents = safeInteger(
      row.sales_cents,
      `daily close ${method} sales`,
    );
    const refundCents = safeInteger(
      row.refund_cents,
      `daily close ${method} refunds`,
    );
    const netCents = safeSum(
      salesCents,
      refundCents,
      `daily close ${method} net`,
    );
    byMethod.set(
      method,
      Object.freeze({ method, salesCents, refundCents, netCents }),
    );
  }
  const tenders = Object.freeze(
    TENDER_METHODS.map(
      (method) =>
        byMethod.get(method) ??
        Object.freeze({
          method,
          salesCents: 0,
          refundCents: 0,
          netCents: 0,
        }),
    ),
  );

  const quantityRows = await connection.getAll<QuantityRow>(
    `SELECT lines.quantity AS quantity
     FROM local_order_lines lines
     INNER JOIN local_orders orders
       ON orders.order_guid = lines.order_guid
     WHERE ${orderFilter}
       AND lines.line_kind = 'return'`,
    orderParameters,
  );
  const returnQuantity = sumDecimalStrings(
    quantityRows.map((row) => requiredText(row.quantity, "return quantity")),
  );
  // 每张订单的现金 tender 合计（销售为正、退款为负）；同一订单混合支付时多笔现金先合并再进位，
  // 与收银时“最后一笔现金按 5 分取整”的实际收付一致。
  const orderCashRows = await connection.getAll<OrderCashRow>(
    `SELECT SUM(tenders.amount_cents) AS cash_cents
     FROM order_tenders tenders
     INNER JOIN local_orders orders
       ON orders.order_guid = tenders.order_guid
     WHERE ${orderFilter}
       AND tenders.method = 'cash'
     GROUP BY tenders.order_guid`,
    orderParameters,
  );
  const cashRoundingCents = sumCashRoundingCents(
    orderCashRows.map((row) => safeInteger(row.cash_cents, "daily close order cash")),
  );
  const cashNetCents =
    tenders.find((item) => item.method === "cash")?.netCents ?? 0;
  // 应有现金 = 现金净额（订单精确金额）+ 现金进位：钱箱实点必然是 5 分整数倍。
  const expectedCashCents = safeSum(
    cashNetCents,
    cashRoundingCents,
    "daily close expected cash",
  );

  return Object.freeze({
    ...normalizedScope,
    orderCount,
    returnQuantity,
    tenders,
    cashRoundingCents,
    expectedCashCents,
  });
}

async function readArchive(
  connection: SqliteConnectionPort,
  row: DailyCloseRow,
): Promise<DailyCloseArchive> {
  if (requiredText(row.state, "daily close state") !== "Archived") {
    throw new Error("Daily close archive is not finalized.");
  }
  const closeId = boundedText(row.close_id, "daily close id", 128);
  const tenderRows = await connection.getAll<DailyCloseTenderRow>(
    `SELECT tender_method, sales_cents, refund_cents, net_cents
     FROM daily_close_totals
     WHERE close_id = ?
     ORDER BY CASE tender_method
       WHEN 'cash' THEN 0 WHEN 'card' THEN 1 ELSE 2 END`,
    [closeId],
  );
  const denominationRows =
    await connection.getAll<DailyCloseDenominationRow>(
      `SELECT denomination_cents, quantity, subtotal_cents
       FROM cash_denominations
       WHERE close_id = ?
       ORDER BY denomination_cents DESC`,
      [closeId],
    );
  const tenders = tenderRows.map(readTender);
  const denominations = denominationRows.map((entry) => ({
    denominationCents: denomination(entry.denomination_cents),
    quantity: safeInteger(entry.quantity, "daily close quantity"),
    subtotalCents: safeInteger(
      entry.subtotal_cents,
      "daily close denomination subtotal",
    ),
  }));
  const rawArchive: DailyCloseArchive = {
    closeId,
    businessDate: requiredText(row.business_date, "business date"),
    periodFromIso: requiredText(row.period_from_iso, "period from"),
    periodToIso: requiredText(row.period_to_iso, "period to"),
    storeCode: requiredText(row.store_code, "store code"),
    deviceCode: requiredText(row.device_code, "device code"),
    savedCashierId: requiredText(row.saved_cashier_id, "cashier id"),
    savedCashierName: requiredText(row.saved_cashier_name, "cashier name"),
    orderCount: safeInteger(row.order_count, "daily close order count"),
    returnQuantity: requiredText(row.return_quantity, "return quantity"),
    tenders,
    // 迁移给旧存档补 0：旧存档的应有现金本来就等于现金净额，口径不变。
    // 列不存在（M54 之前的库结构）等同于没有进位。
    cashRoundingCents: safeInteger(
      row.cash_rounding_cents ?? 0,
      "cash rounding",
    ),
    expectedCashCents: safeInteger(
      row.expected_cash_cents,
      "expected cash",
    ),
    denominations,
    notesSubtotalCents: safeInteger(
      row.notes_subtotal_cents,
      "notes subtotal",
    ),
    coinsSubtotalCents: safeInteger(
      row.coins_subtotal_cents,
      "coins subtotal",
    ),
    countedCashCents: safeInteger(row.counted_cash_cents, "counted cash"),
    varianceCents: safeInteger(row.variance_cents, "cash variance"),
    savedAtIso: requiredText(row.saved_at_iso, "saved at"),
  };
  if (requiredText(row.source_kind, "daily close source") === "native") {
    return validateArchive(rawArchive);
  }
  return validateLegacyArchive(rawArchive);
}

function validateLegacyArchive(input: DailyCloseArchive): DailyCloseArchive {
  const normalizedScope = validateScope(input);
  const tenders = normalizeTenders(input.tenders);
  const denominations = normalizeDailyCloseCounts(input.denominations);
  if (input.denominations.length !== AUD_CASH_DENOMINATIONS_CENTS.length) {
    throw new TypeError("Legacy daily close requires all denominations.");
  }
  for (const entry of input.denominations) {
    const normalized = denominations.find(
      (candidate) =>
        candidate.denominationCents === entry.denominationCents,
    );
    if (!normalized || normalized.subtotalCents !== entry.subtotalCents) {
      throw new TypeError("Legacy daily close denomination is invalid.");
    }
  }
  return Object.freeze({
    ...normalizedScope,
    closeId: boundedText(input.closeId, "daily close id", 128),
    savedCashierId: boundedText(input.savedCashierId, "cashier id", 128),
    savedCashierName: boundedText(
      input.savedCashierName,
      "cashier name",
      256,
    ),
    savedAtIso: validateIso(input.savedAtIso, "saved at"),
    orderCount: nonNegativeInteger(
      input.orderCount,
      "daily close order count",
    ),
    returnQuantity: normalizeUnsignedDecimal(
      input.returnQuantity,
      "daily close return quantity",
    ),
    tenders,
    cashRoundingCents: safeInteger(
      input.cashRoundingCents,
      "cash rounding",
    ),
    expectedCashCents: safeInteger(
      input.expectedCashCents,
      "expected cash",
    ),
    denominations,
    notesSubtotalCents: nonNegativeInteger(
      input.notesSubtotalCents,
      "notes subtotal",
    ),
    coinsSubtotalCents: nonNegativeInteger(
      input.coinsSubtotalCents,
      "coins subtotal",
    ),
    countedCashCents: nonNegativeInteger(
      input.countedCashCents,
      "counted cash",
    ),
    varianceCents: safeInteger(input.varianceCents, "cash variance"),
  });
}

function validateArchive(input: DailyCloseArchive): DailyCloseArchive {
  const normalizedScope = validateScope(input);
  const closeId = boundedText(input.closeId, "daily close id", 128);
  const savedCashierId = boundedText(
    input.savedCashierId,
    "cashier id",
    128,
  );
  const savedCashierName = boundedText(
    input.savedCashierName,
    "cashier name",
    256,
  );
  const savedAtIso = validateIso(input.savedAtIso, "saved at");
  const orderCount = safeInteger(input.orderCount, "daily close order count");
  if (orderCount < 0) {
    throw new TypeError("Daily close order count is invalid.");
  }
  const returnQuantity = normalizeUnsignedDecimal(
    input.returnQuantity,
    "daily close return quantity",
  );
  const tenders = normalizeTenders(input.tenders);
  const expectedCashCents = safeInteger(
    input.expectedCashCents,
    "expected cash",
  );
  const cashRoundingCents = safeInteger(
    input.cashRoundingCents,
    "cash rounding",
  );
  const cash = tenders.find((item) => item.method === "cash");
  // 每张订单现金进位最多 ±2 分，合计不可能超过 订单数 × 2 分（与服务端 INVALID_CASH_ROUNDING 同口径）。
  if (Math.abs(cashRoundingCents) > orderCount * 2) {
    throw new TypeError("Daily close cash rounding exceeds the order count limit.");
  }
  if (
    !cash ||
    safeSum(cash.netCents, cashRoundingCents, "expected cash") !==
      expectedCashCents
  ) {
    throw new TypeError(
      "Daily close expected cash does not match cash net plus rounding.",
    );
  }
  const denominations = normalizeDailyCloseCounts(input.denominations);
  if (input.denominations.length !== AUD_CASH_DENOMINATIONS_CENTS.length) {
    throw new TypeError("Daily close archive requires all denominations.");
  }
  for (const entry of input.denominations) {
    const normalized = denominations.find(
      (candidate) =>
        candidate.denominationCents === entry.denominationCents,
    );
    if (!normalized || normalized.subtotalCents !== entry.subtotalCents) {
      throw new TypeError("Daily close denomination subtotal is invalid.");
    }
  }
  const notesSubtotalCents = safeInteger(
    input.notesSubtotalCents,
    "notes subtotal",
  );
  const coinsSubtotalCents = safeInteger(
    input.coinsSubtotalCents,
    "coins subtotal",
  );
  const countedCashCents = safeInteger(
    input.countedCashCents,
    "counted cash",
  );
  const varianceCents = safeInteger(input.varianceCents, "cash variance");
  const expectedNotes = denominations
    .filter((entry) => entry.denominationCents >= 500)
    .reduce(
      (total, entry) =>
        safeSum(total, entry.subtotalCents, "notes subtotal"),
      0,
    );
  const expectedCoins = denominations
    .filter((entry) => entry.denominationCents < 500)
    .reduce(
      (total, entry) =>
        safeSum(total, entry.subtotalCents, "coins subtotal"),
      0,
    );
  if (
    notesSubtotalCents !== expectedNotes ||
    coinsSubtotalCents !== expectedCoins ||
    countedCashCents !==
      safeSum(notesSubtotalCents, coinsSubtotalCents, "counted cash") ||
    varianceCents !==
      safeSum(countedCashCents, -expectedCashCents, "cash variance")
  ) {
    throw new TypeError("Daily close cash count facts are inconsistent.");
  }
  return Object.freeze({
    ...normalizedScope,
    closeId,
    savedCashierId,
    savedCashierName,
    savedAtIso,
    orderCount,
    returnQuantity,
    tenders,
    cashRoundingCents,
    expectedCashCents,
    denominations,
    notesSubtotalCents,
    coinsSubtotalCents,
    countedCashCents,
    varianceCents,
  });
}

function validateScope(scope: DailyCloseScope): DailyCloseScope {
  const businessDate = validateBusinessDate(scope.businessDate);
  const periodFromIso = validateIso(scope.periodFromIso, "period from");
  const periodToIso = validateIso(scope.periodToIso, "period to");
  if (Date.parse(periodFromIso) >= Date.parse(periodToIso)) {
    throw new TypeError("Daily close period must be a non-empty interval.");
  }
  return Object.freeze({
    businessDate,
    periodFromIso,
    periodToIso,
    storeCode: boundedText(scope.storeCode, "store code", 128),
    deviceCode: boundedText(scope.deviceCode, "device code", 128),
  });
}

function normalizeTenders(
  input: readonly DailyCloseTenderBreakdown[],
): readonly DailyCloseTenderBreakdown[] {
  const byMethod = new Map<
    DailyCloseTenderMethod,
    DailyCloseTenderBreakdown
  >();
  for (const entry of input) {
    const method = tenderMethod(entry.method);
    if (byMethod.has(method)) {
      throw new TypeError("Daily close tender method is duplicated.");
    }
    const salesCents = safeInteger(
      entry.salesCents,
      `daily close ${method} sales`,
    );
    const refundCents = safeInteger(
      entry.refundCents,
      `daily close ${method} refunds`,
    );
    const netCents = safeInteger(
      entry.netCents,
      `daily close ${method} net`,
    );
    if (
      salesCents < 0 ||
      refundCents > 0 ||
      netCents !==
        safeSum(salesCents, refundCents, `daily close ${method} net`)
    ) {
      throw new TypeError("Daily close tender facts are inconsistent.");
    }
    byMethod.set(
      method,
      Object.freeze({ method, salesCents, refundCents, netCents }),
    );
  }
  if (byMethod.size !== TENDER_METHODS.length) {
    throw new TypeError("Daily close requires all tender methods.");
  }
  return Object.freeze(
    TENDER_METHODS.map((method) => {
      const entry = byMethod.get(method);
      if (!entry) {
        throw new TypeError("Daily close requires all tender methods.");
      }
      return entry;
    }),
  );
}

function readTender(row: DailyCloseTenderRow): DailyCloseTenderBreakdown {
  return {
    method: tenderMethod(row.tender_method),
    salesCents: safeInteger(row.sales_cents, "daily close sales"),
    refundCents: safeInteger(row.refund_cents, "daily close refunds"),
    netCents: safeInteger(row.net_cents, "daily close net"),
  };
}

function validateAudit(
  input: AuditEventDraft,
  archive: DailyCloseArchive,
): AuditEventDraft {
  const eventId = boundedText(input.eventId, "audit event id", 128);
  if (input.eventType !== "DAILY_CLOSE_SAVE") {
    throw new TypeError("Daily close audit type must be DAILY_CLOSE_SAVE.");
  }
  const occurredAtIso = validateIso(input.occurredAtIso, "audit occurred at");
  if (
    input.orderGuid !== null ||
    input.correlationId !== archive.closeId ||
    occurredAtIso !== archive.savedAtIso
  ) {
    throw new TypeError("Daily close audit identity does not match archive.");
  }
  if (
    !input.payload ||
    typeof input.payload !== "object" ||
    Array.isArray(input.payload)
  ) {
    throw new TypeError("Daily close audit payload is invalid.");
  }
  stableJson(input.payload);
  return Object.freeze({
    eventId,
    eventType: "DAILY_CLOSE_SAVE",
    occurredAtIso,
    orderGuid: null,
    correlationId: archive.closeId,
    payload: input.payload,
  });
}

async function readAudit(
  connection: SqliteConnectionPort,
  eventId: string,
): Promise<AuditEventDraft> {
  const row = await connection.getFirst<AuditRow>(
    `SELECT event_id, event_type, occurred_at_iso, order_guid,
       correlation_id, payload_json
     FROM audit_events
     WHERE event_id = ?`,
    [eventId],
  );
  if (!row) {
    throw new Error("Daily close audit event is missing.");
  }
  let payload: unknown;
  try {
    payload = JSON.parse(requiredText(row.payload_json, "audit payload"));
  } catch {
    throw new Error("Daily close audit payload is invalid.");
  }
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) {
    throw new Error("Daily close audit payload is invalid.");
  }
  return Object.freeze({
    eventId: requiredText(row.event_id, "audit event id"),
    eventType: requiredText(row.event_type, "audit event type"),
    occurredAtIso: requiredText(row.occurred_at_iso, "audit occurred at"),
    orderGuid:
      row.order_guid === null
        ? null
        : requiredText(row.order_guid, "audit order id"),
    correlationId: requiredText(row.correlation_id, "audit correlation id"),
    payload: payload as Readonly<Record<string, unknown>>,
  });
}

function completedOrderFilter(): string {
  const states = COMPLETED_ORDER_STATES.map((state) => `'${state}'`).join(
    ", ",
  );
  return `orders.store_code = ?
    AND orders.device_code = ?
    AND julianday(orders.sold_at_iso) >= julianday(?)
    AND julianday(orders.sold_at_iso) < julianday(?)
    AND orders.state IN (${states})`;
}

function scopeParameters(scope: DailyCloseScope): readonly SqlValue[] {
  return [
    scope.storeCode,
    scope.deviceCode,
    scope.periodFromIso,
    scope.periodToIso,
  ];
}

function sumDecimalStrings(values: readonly string[]): string {
  let total = 0n;
  let scale = 0;
  for (const value of values) {
    const normalized = normalizeUnsignedDecimal(value, "return quantity");
    const [whole = "0", fraction = ""] = normalized.split(".");
    const nextScale = Math.max(scale, fraction.length);
    total *= 10n ** BigInt(nextScale - scale);
    total +=
      BigInt(`${whole}${fraction}`) *
      10n ** BigInt(nextScale - fraction.length);
    scale = nextScale;
  }
  if (scale === 0) {
    return total.toString();
  }
  const padded = total.toString().padStart(scale + 1, "0");
  const whole = padded.slice(0, -scale);
  const fraction = padded.slice(-scale).replace(/0+$/u, "");
  return fraction.length === 0 ? whole : `${whole}.${fraction}`;
}

function normalizeUnsignedDecimal(value: unknown, label: string): string {
  const textValue = requiredText(value, label).trim();
  if (
    textValue.length > 128 ||
    !/^(?:0|[1-9]\d*)(?:\.\d+)?$/u.test(textValue)
  ) {
    throw new TypeError(`${label} is invalid.`);
  }
  const [whole = "0", rawFraction = ""] = textValue.split(".");
  const fraction = rawFraction.replace(/0+$/u, "");
  return fraction.length === 0 ? whole : `${whole}.${fraction}`;
}

function tenderMethod(value: unknown): DailyCloseTenderMethod {
  if (value === "cash" || value === "card" || value === "voucher") {
    return value;
  }
  throw new Error("Daily close tender method is invalid.");
}

function denomination(value: unknown): AudCashDenominationCents {
  const numeric = safeInteger(value, "daily close denomination");
  if (
    !AUD_CASH_DENOMINATIONS_CENTS.includes(
      numeric as AudCashDenominationCents,
    )
  ) {
    throw new Error("Daily close denomination is invalid.");
  }
  return numeric as AudCashDenominationCents;
}

function validateBusinessDate(value: unknown): string {
  const date = requiredText(value, "business date").trim();
  if (!/^\d{4}-\d{2}-\d{2}$/u.test(date)) {
    throw new TypeError("Daily close business date is invalid.");
  }
  const parsed = new Date(`${date}T00:00:00.000Z`);
  if (
    !Number.isFinite(parsed.getTime()) ||
    parsed.toISOString().slice(0, 10) !== date
  ) {
    throw new TypeError("Daily close business date is invalid.");
  }
  return date;
}

function validateIso(value: unknown, label: string): string {
  const iso = requiredText(value, label).trim();
  if (iso.length > 64 || !Number.isFinite(Date.parse(iso))) {
    throw new TypeError(`${label} is invalid.`);
  }
  return iso;
}

function positiveLimit(value: unknown): number {
  const limit = safeInteger(value, "daily close list limit");
  if (limit <= 0 || limit > 500) {
    throw new TypeError("Daily close list limit is invalid.");
  }
  return limit;
}

function boundedText(value: unknown, label: string, maxLength: number): string {
  const normalized = requiredText(value, label).trim();
  if (
    normalized.length === 0 ||
    normalized.length > maxLength ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    throw new TypeError(`${label} is invalid.`);
  }
  return normalized;
}

function requiredText(value: unknown, label: string): string {
  if (typeof value !== "string") {
    throw new TypeError(`${label} is invalid.`);
  }
  return value;
}

function safeInteger(value: unknown, label: string): number {
  const numeric = Number(value);
  if (!Number.isSafeInteger(numeric)) {
    throw new TypeError(`${label} must be a safe integer.`);
  }
  return numeric;
}

function nonNegativeInteger(value: unknown, label: string): number {
  const numeric = safeInteger(value, label);
  if (numeric < 0) {
    throw new TypeError(`${label} must be non-negative.`);
  }
  return numeric;
}

function safeSum(left: number, right: number, label: string): number {
  const total = left + right;
  if (!Number.isSafeInteger(total)) {
    throw new TypeError(`${label} must be a safe integer.`);
  }
  return total;
}

function stableJson(value: unknown): string {
  return JSON.stringify(sortJson(value));
}

function sortJson(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(sortJson);
  }
  if (value && typeof value === "object") {
    return Object.fromEntries(
      Object.entries(value as Readonly<Record<string, unknown>>)
        .sort(([left], [right]) => left.localeCompare(right))
        .map(([key, entry]) => [key, sortJson(entry)]),
    );
  }
  if (
    value === null ||
    typeof value === "string" ||
    typeof value === "number" ||
    typeof value === "boolean"
  ) {
    return value;
  }
  throw new TypeError("Daily close audit payload is not JSON serializable.");
}

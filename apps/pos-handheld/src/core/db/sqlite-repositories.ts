import {
  freezeAuditScope,
  type AuditScope,
} from "@hb/pos-domain/core/contracts/audit-scope";
import type { CartSnapshot } from "@hb/pos-domain/core/contracts/cart";
import type {
  HeldOrderActor,
  HeldOrderPayloadV1,
  HeldOrderRecordRepositoryPort,
  HeldOrderScope,
  HeldOrderStatus,
  HeldOrderSummary,
  HoldCartCommand,
  RecallClaim,
} from "../contracts/held-orders";
import {
  normalizeLineSyncProvenance,
  type LineSyncProvenance,
} from "@hb/pos-domain/core/contracts/line-sync-provenance";
import {
  createAud,
  MoneySchema,
  multiplyCentsAwayFromZero,
} from "@hb/pos-domain/core/contracts/money";
import type {
  AuditEventDraft,
  LocalOrder,
  OrderTender,
  OutboxMessageDraft,
} from "@hb/pos-domain/core/contracts/order";
import {
  normalizeCardSyncEvidence,
  type CardSyncEvidenceV1,
  type PaymentAttempt,
} from "@hb/pos-domain/core/contracts/payment";
import type { PricingCartStateSnapshot } from "@hb/pos-domain/core/contracts/pricing-cart-state";
import type {
  AuditRepositoryPort,
  DrawerEventRepositoryPort,
  HeldOrderRepositoryPort,
  OrderRepositoryPort,
  OutboxLease,
  OutboxRepositoryPort,
  OperationAuditDeliveryPort,
  OperationAuditDeliveryEvent,
  PaymentAttemptRepositoryPort,
  PrintJobRepositoryPort,
} from "@hb/pos-domain/core/contracts/repositories";
import { canTransitionOrder } from "@hb/pos-domain/core/contracts/state-machines";
import type {
  RecallActiveBinding,
  TerminalCartFence,
} from "@hb/pos-domain/core/contracts/terminal-cart";

import {
  decryptPaymentProtectedMaterial,
  encryptPaymentProtectedMaterial,
} from "@hb/pos-db/core/db/sqlite-payment-protected-material";
import type { SqliteConnectionPort, SqlValue } from "@hb/pos-db/core/db/types";

export interface SensitivePayloadEncryptor {
  encrypt(plaintext: string): Promise<Uint8Array>;
  decrypt(ciphertext: Uint8Array): Promise<string>;
}

export type PosRepositoryBundle = Readonly<{
  orders: OrderRepositoryPort;
  /** M3 legacy 挂单只供兼容导出；新收银流程必须使用 heldOrderRecords。 */
  heldOrders: HeldOrderRepositoryPort;
  heldOrderRecords: HeldOrderRecordRepositoryPort;
  payments: PaymentAttemptRepositoryPort;
  outbox: OutboxRepositoryPort;
  audit: AuditRepositoryPort;
  auditDelivery: OperationAuditDeliveryPort;
  printJobs: PrintJobRepositoryPort;
  drawerEvents: DrawerEventRepositoryPort;
}>;

export function createSqliteRepositories(
  connection: SqliteConnectionPort,
  options: Readonly<{
    nowIso: () => string;
    createLeaseId: () => string;
    encryptor: SensitivePayloadEncryptor;
    /** 组合根注入当前可信终端；缺失时员工审计投递必须 fail-closed。 */
    auditScope?: AuditScope;
  }>,
): PosRepositoryBundle {
  const auditScope = options.auditScope
    ? freezeAuditScope(options.auditScope)
    : null;
  return {
    orders: new SqliteOrderRepository(connection),
    heldOrders: new SqliteHeldOrderRepository(connection, options.encryptor, options.nowIso),
    heldOrderRecords: new SqliteHeldOrderRecordRepository(connection, options.encryptor),
    payments: new SqlitePaymentAttemptRepository(connection, options.encryptor),
    outbox: new SqliteOutboxRepository(connection, options.nowIso, options.createLeaseId),
    audit: new SqliteAuditRepository(connection, options.nowIso, auditScope),
    auditDelivery: new SqliteOperationAuditDelivery(
      connection,
      options.nowIso,
      auditScope,
    ),
    printJobs: new SqlitePrintJobRepository(connection),
    drawerEvents: new SqliteDrawerEventRepository(connection),
  };
}

class SqliteOrderRepository implements OrderRepositoryPort {
  public constructor(private readonly db: SqliteConnectionPort) {}
  public async nextLocalSequence(): Promise<number> {
    return this.db.withExclusiveTransaction(async (tx) => {
      await tx.run("INSERT INTO app_settings (setting_key, setting_value, updated_at_iso) VALUES ('local_sequence', '0', '1970-01-01T00:00:00.000Z') ON CONFLICT(setting_key) DO NOTHING");
      const row = await tx.getFirst<{ value: number | string }>("UPDATE app_settings SET setting_value = CAST(setting_value AS INTEGER) + 1 WHERE setting_key = 'local_sequence' RETURNING setting_value AS value");
      const value = Number(row?.value);
      if (!Number.isSafeInteger(value) || value <= 0) throw new Error("Invalid local sequence.");
      return value;
    });
  }
  public async saveDraft(order: LocalOrder): Promise<void> {
    const lines = order.lines.map((line) => ({
      line,
      syncProvenance: normalizeLineSyncProvenance(
        line.syncProvenance,
      ),
    }));
    await this.db.withExclusiveTransaction(async (tx) => {
      await tx.run("INSERT INTO local_orders (order_guid,local_sequence,store_code,device_code,cashier_id,cashier_name,sold_at_iso,state,total_cents,discount_cents,actual_amount_cents,original_order_guid,created_at_iso,updated_at_iso) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)", [order.orderGuid, order.localSequence, order.storeCode, order.deviceCode, order.cashierId, order.cashierName, order.soldAtIso, order.state, order.total.cents, order.discount.cents, order.actualAmount.cents, order.originalOrderGuid, order.soldAtIso, order.soldAtIso]);
      for (const [index, entry] of lines.entries()) {
        const { line, syncProvenance } = entry;
        await tx.run(
          "INSERT INTO local_order_lines (line_id,order_guid,line_sequence,product_code,item_number,lookup_code,display_name,quantity,unit_price_cents,discount_cents,actual_amount_cents,price_source,line_kind,return_source_key,original_order_guid,original_order_detail_guid,reference_code,sync_price_source) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
          [
            line.lineId,
            order.orderGuid,
            index + 1,
            line.productCode,
            line.itemNumber,
            line.lookupCode,
            line.displayName,
            line.quantity,
            line.unitPrice.cents,
            line.discount.cents,
            line.actualAmount.cents,
            line.priceSource,
            line.kind,
            line.returnSourceKey,
            line.originalOrderGuid,
            line.originalOrderDetailGuid,
            syncProvenance.referenceCode,
            syncProvenance.priceSource,
          ],
        );
      }
      for (const tender of order.tenders) {
        if (tender.reference !== null || tender.reservationToken !== null) throw new Error("Tender references require an encrypted payment attempt.");
        await tx.run("INSERT INTO order_tenders (tender_guid,order_guid,method,amount_cents,payment_attempt_id,created_at_iso) VALUES (?,?,?,?,NULL,?)", [tender.tenderGuid, order.orderGuid, tender.method, tender.amount.cents, order.soldAtIso]);
      }
    });
  }
  public async getByGuid(orderGuid: string): Promise<LocalOrder | null> { return this.readOne("WHERE order_guid = ?", [orderGuid]); }
  public async listLocal(limit: number, beforeSequence?: number): Promise<readonly LocalOrder[]> {
    const rows = await this.db.getAll<OrderRow>(`SELECT * FROM local_orders ${beforeSequence === undefined ? "" : "WHERE local_sequence < ?"} ORDER BY local_sequence DESC LIMIT ?`, beforeSequence === undefined ? [limit] : [beforeSequence, limit]);
    return Promise.all(rows.map((row) => this.readOrder(row)));
  }
  public async transition(orderGuid: string, expected: LocalOrder["state"], next: LocalOrder["state"]): Promise<boolean> {
    if (!canTransitionOrder(expected, next)) return false;
    return (await this.db.run("UPDATE local_orders SET state = ?, updated_at_iso = sold_at_iso WHERE order_guid = ? AND state = ?", [next, orderGuid, expected])).changes === 1;
  }
  private async readOne(where: string, params: readonly SqlValue[]): Promise<LocalOrder | null> { const row = await this.db.getFirst<OrderRow>(`SELECT * FROM local_orders ${where}`, params); return row ? this.readOrder(row) : null; }
  private async readOrder(row: OrderRow): Promise<LocalOrder> {
    const lines = await this.db.getAll<LineRow>("SELECT * FROM local_order_lines WHERE order_guid = ? ORDER BY line_sequence", [text(row.order_guid)]);
    const tenders = await this.db.getAll<TenderRow>(
      `SELECT
         t.*,
         p.order_guid AS attempt_order_guid,
         p.provider AS attempt_provider,
         p.operation AS attempt_operation,
         p.amount_cents AS attempt_amount_cents,
         p.state AS attempt_state,
         p.payment_id AS attempt_payment_id,
         p.provider_response_code AS attempt_response_code
       FROM order_tenders t
       LEFT JOIN payment_attempts p
         ON p.attempt_id = t.payment_attempt_id
        AND p.order_guid = t.order_guid
        AND p.state = 'Approved'
        AND p.amount_cents = t.amount_cents
       WHERE t.order_guid = ?
       ORDER BY t.created_at_iso`,
      [text(row.order_guid)],
    );
    return { orderGuid: text(row.order_guid), localSequence: int(row.local_sequence), storeCode: text(row.store_code), deviceCode: text(row.device_code), cashierId: text(row.cashier_id), cashierName: text(row.cashier_name), soldAtIso: text(row.sold_at_iso), state: orderState(row.state), total: money(row.total_cents), discount: money(row.discount_cents), actualAmount: money(row.actual_amount_cents), originalOrderGuid: nullable(row.original_order_guid), lines: lines.map(line => {
      const syncProvenance = readLineSyncProvenance(line);
      return {
        lineId: text(line.line_id),
        productCode: text(line.product_code),
        itemNumber: nullable(line.item_number),
        lookupCode: text(line.lookup_code),
        displayName: text(line.display_name),
        quantity: text(line.quantity),
        unitPrice: money(line.unit_price_cents),
        discount: money(line.discount_cents),
        actualAmount: money(line.actual_amount_cents),
        priceSource: line.price_source as never,
        ...(syncProvenance ? { syncProvenance } : {}),
        kind: line.line_kind as never,
        returnSourceKey: nullable(line.return_source_key),
        originalOrderGuid: nullable(line.original_order_guid),
        originalOrderDetailGuid: nullable(line.original_order_detail_guid),
      };
    }), tenders: tenders.map(readOrderTender) };
  }
}

class SqliteHeldOrderRepository implements HeldOrderRepositoryPort {
  public constructor(private readonly db: SqliteConnectionPort, private readonly encryptor: SensitivePayloadEncryptor, private readonly nowIso: () => string) {}
  public async hold(holdId: string, snapshot: CartSnapshot, localSequence: number): Promise<void> { const cipher = await this.encryptor.encrypt(JSON.stringify(snapshot)); await this.db.run("INSERT INTO held_orders (hold_id,local_sequence,cart_ciphertext,created_at_iso,updated_at_iso) VALUES (?,?,?,?,?) ON CONFLICT(hold_id) DO UPDATE SET local_sequence=excluded.local_sequence,cart_ciphertext=excluded.cart_ciphertext,updated_at_iso=excluded.updated_at_iso", [holdId, localSequence, cipher, this.nowIso(), this.nowIso()]); }
  public async resume(holdId: string): Promise<CartSnapshot | null> { const row = await this.db.getFirst<{ cart_ciphertext: unknown }>("SELECT cart_ciphertext FROM held_orders WHERE hold_id = ?", [holdId]); if (!row) return null; if (!(row.cart_ciphertext instanceof Uint8Array)) throw new Error("Invalid held cart ciphertext."); return parseCart(await this.encryptor.decrypt(row.cart_ciphertext)); }
  public remove(holdId: string): Promise<void> { return this.db.run("DELETE FROM held_orders WHERE hold_id = ?", [holdId]).then(() => undefined); }
}

type HeldOrderDeleteStage = Readonly<{
  holdId: string;
  remoteCancellationRequired: boolean;
}>;

type HeldOrderSummaryWithSynthetic = HeldOrderSummary & {
  isSyntheticSharedClaim?: boolean;
};

type HeldOrderRecordRow = Readonly<{
  hold_id: unknown;
  local_sequence: unknown;
  store_code: unknown;
  device_code: unknown;
  held_by_cashier_id: unknown;
  held_by_cashier_name: unknown;
  status: unknown;
  item_count: unknown;
  subtotal_cents: unknown;
  discount_cents: unknown;
  actual_amount_cents: unknown;
  held_at_iso: unknown;
  recalling_at_iso: unknown;
  recall_attempt_id: unknown;
  payload_version?: unknown;
  payload_ciphertext?: unknown;
  is_synthetic_shared_claim?: unknown;
}>;

type TerminalCartFenceRow = Readonly<{
  store_code: unknown;
  device_code: unknown;
  kind: unknown;
  hold_id: unknown;
  recall_attempt_id: unknown;
  bound_order_guid: unknown;
  created_at_iso: unknown;
}>;

/**
 * M9 挂单与 M10 终端购物车栅栏组成一个本地工作流：它不能落入
 * local_orders、tender 或 outbox。每个状态动作都在同一 BEGIN IMMEDIATE
 * 内更新挂单和栅栏，进程被杀后仍能判断应清车、恢复或释放。
 */
export class SqliteHeldOrderRecordRepository implements HeldOrderRecordRepositoryPort {
  private syntheticClaimColumnSupport: Promise<boolean> | null = null;

  public constructor(
    private readonly db: SqliteConnectionPort,
    private readonly encryptor: SensitivePayloadEncryptor,
  ) {}

  public async hold(command: HoldCartCommand): Promise<HeldOrderSummary> {
    const payload = validateHeldOrderPayload(command.payload);
    const scope = validateHeldOrderScope(command.scope);
    const heldBy = validateHeldOrderActor(command.heldBy);
    const holdId = nonBlank(command.holdId, "hold id");
    const heldAtIso = canonicalIso(command.heldAtIso, "held at");
    const audit = validateHeldOrderAudit(command.audit, "ORDER_HOLD");
    const summaryValues = summarizePricingState(payload.pricingState);
    const ciphertext = await this.encryptor.encrypt(JSON.stringify(payload));
    if (!(ciphertext instanceof Uint8Array) || ciphertext.length === 0) {
      throw new Error("Held order payload encryption failed.");
    }

    return this.db.withExclusiveTransaction(async (transaction) => {
      const localSequence = await allocateLocalSequence(transaction);
      await transaction.run(
        `INSERT INTO held_order_records (
          hold_id, local_sequence, store_code, device_code,
          held_by_cashier_id, held_by_cashier_name, status, payload_version,
          payload_ciphertext, item_count, subtotal_cents, discount_cents,
          actual_amount_cents, recalling_at_iso, recall_attempt_id,
          recalling_cashier_id, recalling_cashier_name, recalled_at_iso,
          held_at_iso, created_at_iso, updated_at_iso
        ) VALUES (?, ?, ?, ?, ?, ?, 'Pending', 1, ?, ?, ?, ?, ?,
          NULL, NULL, NULL, NULL, NULL, ?, ?, ?)`,
        [
          holdId,
          localSequence,
          scope.storeCode,
          scope.deviceCode,
          heldBy.cashierId,
          heldBy.cashierName,
          ciphertext,
          summaryValues.itemCount,
          summaryValues.subtotalCents,
          summaryValues.discountCents,
          summaryValues.actualAmountCents,
          heldAtIso,
          heldAtIso,
          heldAtIso,
        ],
      );
      // 挂单没有 order_guid 可回查，必须用发生操作时已验证的 command scope 冻结。
      await appendAuditEvent(transaction, audit, scope);
      await transaction.run(
        `INSERT INTO terminal_cart_fences (
          store_code, device_code, kind, hold_id, recall_attempt_id,
          bound_order_guid, created_at_iso
        ) VALUES (?, ?, 'HoldClear', ?, NULL, NULL, ?)`,
        [scope.storeCode, scope.deviceCode, holdId, heldAtIso],
      );
      return {
        holdId,
        localSequence,
        scope,
        heldBy,
        status: "Pending",
        ...summaryValues,
        heldAtIso,
        recallingAtIso: null,
      };
    });
  }

  public async listPending(
    scopeInput: HeldOrderScope,
    limit: number,
  ): Promise<readonly HeldOrderSummary[]> {
    const scope = validateHeldOrderScope(scopeInput);
    const safeLimit = listLimit(limit);
    // 兼容只升级到 M9-M39 的旧开发库测试/恢复工具；正式启动完成 M40 后固定走真实列。
    const syntheticClaimProjection = await this.supportsSyntheticClaimColumn()
      ? "is_synthetic_shared_claim"
      : "0 AS is_synthetic_shared_claim";
    const rows = await this.db.getAll<HeldOrderRecordRow>(
      `SELECT hold_id, local_sequence, store_code, device_code,
        held_by_cashier_id, held_by_cashier_name, status, item_count,
        subtotal_cents, discount_cents, actual_amount_cents, held_at_iso,
        recalling_at_iso, ${syntheticClaimProjection}
       FROM held_order_records
       WHERE store_code = ? AND device_code = ? AND status = 'Pending'
       ORDER BY local_sequence DESC
       LIMIT ?`,
      [scope.storeCode, scope.deviceCode, safeLimit],
    );
    return rows.map(readHeldOrderSummary);
  }

  private supportsSyntheticClaimColumn(): Promise<boolean> {
    this.syntheticClaimColumnSupport ??= this.db
      .getFirst<{ count: unknown }>(
        `SELECT COUNT(*) AS count
         FROM pragma_table_info('held_order_records')
         WHERE name = 'is_synthetic_shared_claim'`,
      )
      .then((row) => Number(row?.count ?? 0) === 1);
    return this.syntheticClaimColumnSupport;
  }

  public async stageDeletePending(input: Readonly<{
    holdId: string;
    scope: HeldOrderScope;
    stagedAtIso: string;
  }>): Promise<HeldOrderDeleteStage | null> {
    const holdId = nonBlank(input.holdId, "hold id");
    const scope = validateHeldOrderScope(input.scope);
    const stagedAtIso = canonicalIso(input.stagedAtIso, "delete staged at");
    return this.db.withExclusiveTransaction(async (transaction) => {
      const row = await transaction.getFirst<{
        share_state: string;
        publish_block_reason: string | null;
        remote_revision: number | null;
      }>(
        `SELECT share_state, publish_block_reason, remote_revision
         FROM held_order_records
         WHERE hold_id = ? AND store_code = ? AND device_code = ?
           AND status = 'Pending' AND is_synthetic_shared_claim = 0`,
        [holdId, scope.storeCode, scope.deviceCode],
      );
      if (!row) return null;

      const changed = await transaction.run(
        `UPDATE held_order_records
         SET share_state = 'Blocked',
             publish_block_reason = 'LOCAL_DELETE_PENDING',
             next_publish_at_iso = NULL,
             publish_error_code = NULL,
             updated_at_iso = ?
         WHERE hold_id = ? AND store_code = ? AND device_code = ?
           AND status = 'Pending' AND is_synthetic_shared_claim = 0
           AND NOT EXISTS (
             SELECT 1 FROM terminal_cart_fences fence
             WHERE fence.hold_id = held_order_records.hold_id
           )
           AND NOT EXISTS (
             SELECT 1 FROM shared_held_order_claim_records claim
             WHERE claim.hold_guid = held_order_records.hold_id
               AND claim.state IN ('Prepared', 'Active')
           )`,
        [stagedAtIso, holdId, scope.storeCode, scope.deviceCode],
      );
      if (changed.changes !== 1) return null;
      return {
        holdId,
        remoteCancellationRequired:
          row.remote_revision !== null ||
          row.share_state === "PendingPublish" ||
          row.share_state === "Published" ||
          row.publish_block_reason === "LOCAL_DELETE_PENDING",
      };
    });
  }

  public async deleteStagedPending(input: Readonly<{
    holdId: string;
    scope: HeldOrderScope;
  }>): Promise<boolean> {
    const holdId = nonBlank(input.holdId, "hold id");
    const scope = validateHeldOrderScope(input.scope);
    return this.db.withExclusiveTransaction(async (transaction) => {
      const deleted = await transaction.run(
        `DELETE FROM held_order_records
         WHERE hold_id = ? AND store_code = ? AND device_code = ?
           AND status = 'Pending' AND is_synthetic_shared_claim = 0
           AND share_state = 'Blocked'
           AND publish_block_reason = 'LOCAL_DELETE_PENDING'
           AND NOT EXISTS (
             SELECT 1 FROM terminal_cart_fences fence
             WHERE fence.hold_id = held_order_records.hold_id
           )
           AND NOT EXISTS (
             SELECT 1 FROM shared_held_order_claim_records claim
             WHERE claim.hold_guid = held_order_records.hold_id
               AND claim.state IN ('Prepared', 'Active')
           )`,
        [holdId, scope.storeCode, scope.deviceCode],
      );
      return deleted.changes === 1;
    });
  }

  public async claimRecall(input: Readonly<{
    holdId: string;
    scope: HeldOrderScope;
    recalledBy: HeldOrderActor;
    recallAttemptId: string;
    recallingAtIso: string;
  }>): Promise<RecallClaim | null> {
    const holdId = nonBlank(input.holdId, "hold id");
    const scope = validateHeldOrderScope(input.scope);
    const recalledBy = validateHeldOrderActor(input.recalledBy);
    const recallAttemptId = nonBlank(input.recallAttemptId, "recall attempt id");
    const recallingAtIso = canonicalIso(input.recallingAtIso, "recalling at");

    return this.db.withExclusiveTransaction(async (transaction) => {
      const existingFence = await transaction.getFirst<{ hold_id: unknown }>(
        `SELECT hold_id
         FROM terminal_cart_fences
         WHERE store_code = ? AND device_code = ?`,
        [scope.storeCode, scope.deviceCode],
      );
      if (existingFence) {
        throw new Error(
          `Terminal cart already has an active fence for hold ${nonBlank(existingFence.hold_id, "terminal fence hold id")}.`,
        );
      }
      const row = await getFirstHeldOrderRecordCompat(
        transaction,
        `SELECT hold_id, local_sequence, store_code, device_code,
          held_by_cashier_id, held_by_cashier_name, status, item_count,
          subtotal_cents, discount_cents, actual_amount_cents, held_at_iso,
          recalling_at_iso, payload_version, payload_ciphertext,
          is_synthetic_shared_claim
         FROM held_order_records
         WHERE hold_id = ? AND store_code = ? AND device_code = ?
           AND status = 'Pending'`,
        [holdId, scope.storeCode, scope.deviceCode],
      );
      if (!row) return null;

      // 先解密并验证，再写 Recalling；损坏密文不能把一笔可用挂单永久卡住。
      const payload = await decryptHeldOrderPayload(row, this.encryptor);
      const changed = await transaction.run(
        `UPDATE held_order_records
         SET status = 'Recalling', recalling_at_iso = ?, recall_attempt_id = ?,
             recalling_cashier_id = ?, recalling_cashier_name = ?,
             updated_at_iso = ?
         WHERE hold_id = ? AND store_code = ? AND device_code = ?
           AND status = 'Pending'`,
        [
          recallingAtIso,
          recallAttemptId,
          recalledBy.cashierId,
          recalledBy.cashierName,
          recallingAtIso,
          holdId,
          scope.storeCode,
          scope.deviceCode,
        ],
      );
      if (changed.changes !== 1) return null;
      await transaction.run(
        `INSERT INTO terminal_cart_fences (
          store_code, device_code, kind, hold_id, recall_attempt_id,
          bound_order_guid, created_at_iso
        ) VALUES (?, ?, 'RecallActive', ?, ?, NULL, ?)`,
        [
          scope.storeCode,
          scope.deviceCode,
          holdId,
          recallAttemptId,
          recallingAtIso,
        ],
      );

      const pending = readHeldOrderSummary(row);
      return {
        hold: { ...pending, status: "Recalling", recallingAtIso },
        recallAttemptId,
        payload,
      };
    });
  }

  public async getTerminalFence(
    scopeInput: HeldOrderScope,
  ): Promise<TerminalCartFence | null> {
    const scope = validateHeldOrderScope(scopeInput);
    const row = await this.db.getFirst<TerminalCartFenceRow>(
      `SELECT store_code, device_code, kind, hold_id, recall_attempt_id,
        bound_order_guid, created_at_iso
       FROM terminal_cart_fences
       WHERE store_code = ? AND device_code = ?`,
      [scope.storeCode, scope.deviceCode],
    );
    return row ? readTerminalCartFence(row) : null;
  }

  public async loadRecallForFence(
    bindingInput: RecallActiveBinding,
  ): Promise<RecallClaim | null> {
    const binding = validateRecallBinding(bindingInput);
    const row = await getFirstHeldOrderRecordCompat(
      this.db,
      `SELECT held.hold_id, held.local_sequence, held.store_code, held.device_code,
        held.held_by_cashier_id, held.held_by_cashier_name, held.status,
        held.item_count, held.subtotal_cents, held.discount_cents,
        held.actual_amount_cents, held.held_at_iso, held.recalling_at_iso,
        held.recall_attempt_id, held.payload_version, held.payload_ciphertext,
        held.is_synthetic_shared_claim
       FROM terminal_cart_fences fence
       INNER JOIN held_order_records held
         ON held.hold_id = fence.hold_id
        AND held.store_code = fence.store_code
        AND held.device_code = fence.device_code
        AND held.recall_attempt_id = fence.recall_attempt_id
       WHERE fence.store_code = ? AND fence.device_code = ?
         AND fence.kind = 'RecallActive'
         AND fence.hold_id = ? AND fence.recall_attempt_id = ?
         AND held.status = 'Recalling'`,
      [
        binding.scope.storeCode,
        binding.scope.deviceCode,
        binding.holdId,
        binding.recallAttemptId,
      ],
    );
    if (!row) return null;
    return {
      hold: readHeldOrderSummary(row),
      recallAttemptId: binding.recallAttemptId,
      payload: await decryptHeldOrderPayload(row, this.encryptor),
    };
  }

  public async confirmHoldCartCleared(input: Readonly<{
    scope: HeldOrderScope;
    holdId: string;
  }>): Promise<boolean> {
    const scope = validateHeldOrderScope(input.scope);
    const holdId = nonBlank(input.holdId, "hold id");
    const changed = await this.db.run(
      `DELETE FROM terminal_cart_fences
       WHERE store_code = ? AND device_code = ?
         AND kind = 'HoldClear' AND hold_id = ?
         AND recall_attempt_id IS NULL AND bound_order_guid IS NULL`,
      [scope.storeCode, scope.deviceCode, holdId],
    );
    return changed.changes === 1;
  }

  public async releaseRecallAfterCartCleared(input: Readonly<{
    binding: RecallActiveBinding;
    releasedAtIso: string;
  }>): Promise<boolean> {
    const binding = validateRecallBinding(input.binding);
    const releasedAtIso = canonicalIso(input.releasedAtIso, "recall release time");
    return this.db.withExclusiveTransaction(async (transaction) => {
      const fence = await transaction.getFirst<{ hold_id: unknown }>(
        `SELECT hold_id
         FROM terminal_cart_fences
         WHERE store_code = ? AND device_code = ?
           AND kind = 'RecallActive' AND hold_id = ?
           AND recall_attempt_id = ? AND bound_order_guid IS NULL`,
        [
          binding.scope.storeCode,
          binding.scope.deviceCode,
          binding.holdId,
          binding.recallAttemptId,
        ],
      );
      if (!fence) return false;

      // recall_attempt_id 是 fence 的 FK；先删 fence，再做状态 CAS。后续 CAS
      // 若失败，外层事务会把删除一并回滚，绝不留下无栅栏的 Recalling。
      const deleted = await transaction.run(
        `DELETE FROM terminal_cart_fences
         WHERE store_code = ? AND device_code = ?
           AND kind = 'RecallActive' AND hold_id = ?
           AND recall_attempt_id = ? AND bound_order_guid IS NULL`,
        [
          binding.scope.storeCode,
          binding.scope.deviceCode,
          binding.holdId,
          binding.recallAttemptId,
        ],
      );
      if (deleted.changes !== 1) {
        throw new Error("Recall fence changed before it could be released.");
      }
      const changed = await transaction.run(
        `UPDATE held_order_records
         SET status = 'Pending', recalling_at_iso = NULL, recall_attempt_id = NULL,
             recalling_cashier_id = NULL, recalling_cashier_name = NULL,
             updated_at_iso = ?
         WHERE hold_id = ? AND store_code = ? AND device_code = ?
           AND recall_attempt_id = ? AND status = 'Recalling'`,
        [
          releasedAtIso,
          binding.holdId,
          binding.scope.storeCode,
          binding.scope.deviceCode,
          binding.recallAttemptId,
        ],
      );
      if (changed.changes !== 1) {
        throw new Error("Recall claim changed before it could be released.");
      }
      return true;
    });
  }

  public async listRecoverable(
    scopeInput: HeldOrderScope,
  ): Promise<readonly RecallClaim[]> {
    const scope = validateHeldOrderScope(scopeInput);
    const rows = await getAllHeldOrderRecordsCompat(
      this.db,
      `SELECT held.hold_id, held.local_sequence, held.store_code,
        held.device_code, held.held_by_cashier_id, held.held_by_cashier_name,
        held.status, held.item_count, held.subtotal_cents,
        held.discount_cents, held.actual_amount_cents, held.held_at_iso,
        held.recalling_at_iso, held.recall_attempt_id, held.payload_version,
        held.payload_ciphertext, held.is_synthetic_shared_claim
       FROM held_order_records held
       INNER JOIN terminal_cart_fences fence
         ON fence.store_code = held.store_code
        AND fence.device_code = held.device_code
        AND fence.kind = 'RecallActive'
        AND fence.hold_id = held.hold_id
        AND fence.recall_attempt_id = held.recall_attempt_id
       WHERE held.store_code = ? AND held.device_code = ?
         AND held.status = 'Recalling'
       ORDER BY held.local_sequence DESC`,
      [scope.storeCode, scope.deviceCode],
    );
    // 任一恢复记录的密文或结构异常即整体拒绝，不能静默跳过并让收银员
    // 误以为挂单已经安全处理；上层应进入支持导出/人工处置路径。
    return Promise.all(rows.map(async (row) => {
      const hold = readHeldOrderSummary(row);
      const recallAttemptId = nonBlank(row.recall_attempt_id, "recoverable recall attempt id");
      return {
        hold,
        recallAttemptId,
        payload: await decryptHeldOrderPayload(row, this.encryptor),
      };
    }));
  }
}

async function getFirstHeldOrderRecordCompat(
  db: SqliteConnectionPort,
  sql: string,
  parameters: readonly SqlValue[],
): Promise<HeldOrderRecordRow | null> {
  try {
    return await db.getFirst<HeldOrderRecordRow>(sql, parameters);
  } catch (error) {
    if (!isMissingSyntheticSharedClaimColumn(error)) throw error;
    return db.getFirst<HeldOrderRecordRow>(
      withoutSyntheticSharedClaimColumn(sql),
      parameters,
    );
  }
}

async function getAllHeldOrderRecordsCompat(
  db: SqliteConnectionPort,
  sql: string,
  parameters: readonly SqlValue[],
): Promise<readonly HeldOrderRecordRow[]> {
  try {
    return await db.getAll<HeldOrderRecordRow>(sql, parameters);
  } catch (error) {
    if (!isMissingSyntheticSharedClaimColumn(error)) throw error;
    return db.getAll<HeldOrderRecordRow>(
      withoutSyntheticSharedClaimColumn(sql),
      parameters,
    );
  }
}

/** 旧迁移阶段没有 synthetic 列，也不可能存在远端 claim 行；仅投影为本地行。 */
function withoutSyntheticSharedClaimColumn(sql: string): string {
  const fallback = sql.replace(
    /\b(?:held\.)?is_synthetic_shared_claim\b/,
    "0 AS is_synthetic_shared_claim",
  );
  if (fallback === sql) {
    throw new Error("Synthetic shared claim projection is missing.");
  }
  return fallback;
}

function isMissingSyntheticSharedClaimColumn(error: unknown): boolean {
  return error instanceof Error &&
    /\bno such column:\s*(?:held\.)?is_synthetic_shared_claim\b/i.test(error.message);
}

/**
 * 取单完成事务内的共享挂单来源解析与写入（仅 held recall 完成路径使用）：
 * - 根据 holdId + recallAttemptId 读取 durable claim 事实；只有真实本地挂单的
 *   0 claim 行可判为 OfflineOrigin。synthetic 远端挂单若 claim 丢失必须整体失败，
 *   不能静默降级成 OfflineOrigin；多行同样视为数据损坏。
 * - 来源行与 local order/outbox 在同一事务内写入，之后任何列不可改写；
 *   普通订单绝不调用这些函数，因此不查询、不写入来源表。
 */
export type ResolvedCompletionHeldSource = Readonly<{
  claimGuid: string | null;
  sourceKind: 1 | 2;
  source: "RemoteClaim" | "OfflineOrigin" | null;
  claimState: "Prepared" | "Active" | null;
  activateIdempotencyKey: string | null;
}>;

export async function resolveHeldOrderSourceInTransaction(
  transaction: SqliteConnectionPort,
  holdId: string,
  recallAttemptId: string,
): Promise<ResolvedCompletionHeldSource> {
  const rows = await transaction.getAll<{
    claim_guid: string;
    source: "RemoteClaim" | "OfflineOrigin";
    state: "Prepared" | "Active";
    activate_idempotency_key: string | null;
  }>(
    `SELECT claim_guid, source, state, activate_idempotency_key
     FROM shared_held_order_claim_records
     WHERE hold_guid = ? AND recall_attempt_id = ?
       AND state IN ('Prepared', 'Active')`,
    [holdId, recallAttemptId],
  );
  if (rows.length > 1) {
    throw new Error(
      "Multiple durable shared hold claims match one recall attempt.",
    );
  }
  const row = rows[0];
  if (!row) {
    const held = await transaction.getFirst<{
      is_synthetic_shared_claim: number;
    }>(
      `SELECT is_synthetic_shared_claim
       FROM held_order_records
       WHERE hold_id = ? AND recall_attempt_id = ?
         AND status IN ('Recalling', 'Recalled')`,
      [holdId, recallAttemptId],
    );
    if (!held) {
      throw new Error("SHARED_HELD_ORDER_SOURCE_HOLD_MISSING");
    }
    if (held.is_synthetic_shared_claim === 1) {
      throw new Error("SHARED_HELD_ORDER_SOURCE_CLAIM_MISSING");
    }
    if (held.is_synthetic_shared_claim !== 0) {
      throw new Error("SHARED_HELD_ORDER_SOURCE_KIND_INVALID");
    }
    return {
      claimGuid: null,
      sourceKind: 2,
      source: null,
      claimState: null,
      activateIdempotencyKey: null,
    };
  }
  if (row.source !== "RemoteClaim" && row.source !== "OfflineOrigin") {
    throw new Error("Shared held order claim source is invalid.");
  }
  return {
    claimGuid: row.claim_guid,
    sourceKind: row.source === "RemoteClaim" ? 1 : 2,
    source: row.source,
    claimState: row.state,
    activateIdempotencyKey: row.activate_idempotency_key,
  };
}

export async function writeOrderHeldOrderSourceInTransaction(
  transaction: SqliteConnectionPort,
  input: Readonly<{
    orderGuid: string;
    holdId: string;
    claimGuid: string | null;
    sourceKind: 1 | 2;
    atIso: string;
  }>,
): Promise<void> {
  const inserted = await transaction.run(
    `INSERT INTO order_held_order_sources (
      order_guid, hold_guid, claim_guid, source_kind, created_at_iso
    ) VALUES (?, ?, ?, ?, ?)`,
    [
      input.orderGuid,
      input.holdId,
      input.claimGuid,
      input.sourceKind,
      input.atIso,
    ],
  );
  if (inserted.changes !== 1) {
    throw new Error("Shared held order source insert failed.");
  }
  // 标记列与来源行同事务写入；解析器只在标记为 1 时查询来源表，
  // 保证普通订单零来源查询。
  const marked = await transaction.run(
    `UPDATE local_orders
     SET is_shared_held_origin = 1, updated_at_iso = ?
     WHERE order_guid = ? AND is_shared_held_origin = 0`,
    [input.atIso, input.orderGuid],
  );
  if (marked.changes !== 1) {
    throw new Error("Shared held origin marker changed before completion.");
  }
}

/** durable 本地 claim 在同一事务内 Active -> 绑定 -> Completed。 */
export async function completeSharedClaimInTransaction(
  transaction: SqliteConnectionPort,
  input: Readonly<{
    claimGuid: string;
    activateIdempotencyKey: string;
    boundOrderGuid: string;
    completedAtIso: string;
  }>,
): Promise<void> {
  const bound = await transaction.run(
    `UPDATE shared_held_order_claim_records
     SET bound_order_guid = ?, updated_at_iso = ?
     WHERE claim_guid = ? AND state = 'Active'
       AND activate_idempotency_key = ? AND bound_order_guid IS NULL`,
    [
      input.boundOrderGuid,
      input.completedAtIso,
      input.claimGuid,
      input.activateIdempotencyKey,
    ],
  );
  if (bound.changes !== 1) {
    throw new Error("Shared hold claim changed before order binding.");
  }
  const releaseKey = `completed:${input.boundOrderGuid}`;
  const completed = await transaction.run(
    `UPDATE shared_held_order_claim_records
     SET state = 'Completed', release_idempotency_key = ?, updated_at_iso = ?
     WHERE claim_guid = ? AND state = 'Active'
       AND activate_idempotency_key = ? AND bound_order_guid = ?
       AND release_idempotency_key IS NULL`,
    [
      releaseKey,
      input.completedAtIso,
      input.claimGuid,
      input.activateIdempotencyKey,
      input.boundOrderGuid,
    ],
  );
  if (completed.changes !== 1) {
    throw new Error("Shared hold claim changed before completion.");
  }
}

/** activate 结果未知但订单已成交时，原子关闭 Prepared fence，防止崩溃恢复重复购物车。 */
async function supersedePreparedSharedClaimInTransaction(
  transaction: SqliteConnectionPort,
  input: Readonly<{
    claimGuid: string;
    orderGuid: string;
    completedAtIso: string;
  }>,
): Promise<void> {
  const supersedeKey = `completed:${input.orderGuid}`;
  const superseded = await transaction.run(
    `UPDATE shared_held_order_claim_records
     SET state = 'Superseded', supersede_idempotency_key = ?, updated_at_iso = ?
     WHERE claim_guid = ? AND state = 'Prepared'
       AND activate_idempotency_key IS NULL
       AND release_idempotency_key IS NULL
       AND supersede_idempotency_key IS NULL
       AND bound_order_guid IS NULL`,
    [supersedeKey, input.completedAtIso, input.claimGuid],
  );
  if (superseded.changes !== 1) {
    throw new Error("Shared hold prepared claim changed before supersede.");
  }
}

/**
 * 现金/批准/混合现金共用：先解析 durable claim 事实，再原子写不可变来源；
 * claim 已 Active 时在同一事务绑定并 Completed；Prepared（activate 结果未知）
 * 在订单落地后同事务 Superseded，既保留来源证据也关闭恢复 fence。
 */
export async function persistRecalledHoldOrderSourceAndClaim(
  transaction: SqliteConnectionPort,
  input: Readonly<{
    orderGuid: string;
    holdId: string;
    recallAttemptId: string;
    recalledAtIso: string;
  }>,
): Promise<void> {
  const source = await resolveHeldOrderSourceInTransaction(
    transaction,
    input.holdId,
    input.recallAttemptId,
  );
  await writeOrderHeldOrderSourceInTransaction(transaction, {
    orderGuid: input.orderGuid,
    holdId: input.holdId,
    claimGuid: source.sourceKind === 1 ? source.claimGuid : null,
    sourceKind: source.sourceKind,
    atIso: input.recalledAtIso,
  });
  if (
    source.claimState === "Active" &&
    source.claimGuid !== null &&
    source.activateIdempotencyKey !== null
  ) {
    await completeSharedClaimInTransaction(transaction, {
      claimGuid: source.claimGuid,
      activateIdempotencyKey: source.activateIdempotencyKey,
      boundOrderGuid: input.orderGuid,
      completedAtIso: input.recalledAtIso,
    });
  } else if (
    source.claimState === "Prepared" &&
    source.claimGuid !== null &&
    source.activateIdempotencyKey === null
  ) {
    await supersedePreparedSharedClaimInTransaction(transaction, {
      claimGuid: source.claimGuid,
      orderGuid: input.orderGuid,
      completedAtIso: input.recalledAtIso,
    });
  }
}

class SqliteOutboxRepository implements OutboxRepositoryPort {
  public constructor(private readonly db: SqliteConnectionPort, private readonly nowIso: () => string, private readonly createLeaseId: () => string) {}
  public enqueue(message: OutboxMessageDraft): Promise<void> { const now = this.nowIso(); return this.db.run("INSERT INTO outbox_messages (message_id,aggregate_id,kind,payload_json,state,attempt_count,next_attempt_at_iso,lease_id,lease_expires_at_iso,last_error_code,created_at_iso,updated_at_iso) VALUES (?,?,?,?,'pending',0,?,NULL,NULL,NULL,?,?)", [message.messageId, message.aggregateId, message.kind, message.payloadJson, message.nextAttemptAtIso, now, now]).then(() => undefined); }
  public async nextReadyAtIso(): Promise<string | null> {
    const row = await this.db.getFirst<{ ready_at_iso: unknown }>(
      `SELECT MIN(
         CASE
           WHEN state = 'leased'
             AND lease_expires_at_iso > next_attempt_at_iso
             THEN lease_expires_at_iso
           ELSE next_attempt_at_iso
         END
       ) AS ready_at_iso
       FROM outbox_messages
       WHERE state IN ('pending', 'leased')`,
    );
    return nullable(row?.ready_at_iso);
  }
  public async leaseReady(limit: number, leaseSeconds: number): Promise<readonly OutboxLease[]> {
    const now = this.nowIso();
    const expiry = new Date(Date.parse(now) + leaseSeconds * 1000).toISOString();
    return this.db.withExclusiveTransaction(async (transaction) => {
      const rows = await transaction.getAll<OutboxRow>(
        "SELECT * FROM outbox_messages WHERE (state = 'pending' OR (state = 'leased' AND lease_expires_at_iso <= ?)) AND next_attempt_at_iso <= ? ORDER BY created_at_iso LIMIT ?",
        [now, now, limit],
      );
      const leases: OutboxLease[] = [];
      for (const row of rows) {
        const messageId = text(row.message_id);
        const aggregateId = text(row.aggregate_id);
        const kind = outboxKind(row.kind);
        const previousOutboxState = text(row.state);
        if (kind === "order-sync") {
          await prepareOrderForSyncLease(
            transaction,
            aggregateId,
            previousOutboxState,
            now,
          );
        }
        const leaseId = this.createLeaseId();
        const changed = await transaction.run(
          "UPDATE outbox_messages SET state='leased',lease_id=?,lease_expires_at_iso=?,attempt_count=attempt_count+1,updated_at_iso=? WHERE message_id=? AND (state='pending' OR (state='leased' AND lease_expires_at_iso <= ?))",
          [leaseId, expiry, now, messageId, now],
        );
        if (changed.changes !== 1) {
          throw new Error(`Outbox message is no longer available for lease: ${messageId}`);
        }
        leases.push({
          messageId,
          leaseId,
          aggregateId,
          kind,
          payloadJson: text(row.payload_json),
          attemptCount: int(row.attempt_count) + 1,
        });
      }
      return leases;
    });
  }
  public markSucceeded(lease: OutboxLease): Promise<void> {
    return this.complete(
      lease,
      "Synced",
      "UPDATE outbox_messages SET state='succeeded',lease_id=NULL,lease_expires_at_iso=NULL,updated_at_iso=? WHERE message_id=? AND state='leased' AND lease_id=?",
      [this.nowIso(), lease.messageId, lease.leaseId],
    );
  }
  public releaseRetry(lease: OutboxLease, next: string, code: string): Promise<void> {
    return this.complete(
      lease,
      "PendingSync",
      "UPDATE outbox_messages SET state='pending',next_attempt_at_iso=?,last_error_code=?,lease_id=NULL,lease_expires_at_iso=NULL,updated_at_iso=? WHERE message_id=? AND state='leased' AND lease_id=?",
      [next, code, this.nowIso(), lease.messageId, lease.leaseId],
    );
  }
  public markBlocked403(lease: OutboxLease, code: string): Promise<void> {
    return this.complete(
      lease,
      "Blocked403",
      "UPDATE outbox_messages SET state='blocked403',last_error_code=?,lease_id=NULL,lease_expires_at_iso=NULL,updated_at_iso=? WHERE message_id=? AND state='leased' AND lease_id=?",
      [code, this.nowIso(), lease.messageId, lease.leaseId],
    );
  }
  public markRejected(lease: OutboxLease, code: string): Promise<void> {
    return this.complete(
      lease,
      "Rejected",
      "UPDATE outbox_messages SET state='rejected',last_error_code=?,lease_id=NULL,lease_expires_at_iso=NULL,updated_at_iso=? WHERE message_id=? AND state='leased' AND lease_id=?",
      [code, this.nowIso(), lease.messageId, lease.leaseId],
    );
  }
  private complete(
    lease: OutboxLease,
    nextOrderState: Extract<LocalOrder["state"], "Synced" | "PendingSync" | "Blocked403" | "Rejected">,
    sql: string,
    parameters: readonly SqlValue[],
  ): Promise<void> {
    return this.db.withExclusiveTransaction(async (transaction) => {
      const owned = await requireOwnedOutboxLease(transaction, lease);
      if (owned.kind === "order-sync") {
        if (!canTransitionOrder("Syncing", nextOrderState)) {
          throw new Error(`Order cannot leave Syncing for ${nextOrderState}.`);
        }
        const orderChanged = await transaction.run(
          "UPDATE local_orders SET state = ?, updated_at_iso = ? WHERE order_guid = ? AND state = 'Syncing'",
          [nextOrderState, this.nowIso(), owned.aggregateId],
        );
        if (orderChanged.changes !== 1) {
          throw new Error(`Order cannot leave Syncing: ${owned.aggregateId}`);
        }
      }
      const outboxChanged = await transaction.run(sql, parameters);
      if (outboxChanged.changes !== 1) {
        throw new Error(`Outbox lease is no longer owned: ${lease.messageId}`);
      }
    });
  }
}

class SqliteAuditRepository implements AuditRepositoryPort {
  public constructor(
    private readonly db: SqliteConnectionPort,
    private readonly nowIso: () => string,
    private readonly auditScope: AuditScope | null,
  ) {}

  public async append(events: readonly AuditEventDraft[]): Promise<void> {
    const scope = requiredAuditScope(this.auditScope);
    for (const event of events) {
      assertSafe(event.payload);
      // order_guid 可证明时只使用订单账本身份；非订单事实使用组合根冻结的终端身份。
      const eventScope = event.orderGuid === null
        ? scope
        : await readOrderAuditScope(this.db, event.orderGuid);
      await insertAuditEvent(this.db, event, eventScope, this.nowIso());
    }
  }

  public async listPending(limit: number): Promise<readonly AuditEventDraft[]> {
    const scope = this.auditScope;
    // 兼容旧 Port 读取时同样不能以未证明的 runtime 身份选择事实。
    if (!scope) return [];
    const rows = await this.db.getAll<AuditRow>(
      `SELECT * FROM audit_events
       WHERE uploaded_at_iso IS NULL
         AND delivery_state = 'pending'
         AND scope_store_code = ?
         AND scope_device_code = ?
       ORDER BY occurred_at_iso, event_id
       LIMIT ?`,
      [scope.storeCode, scope.deviceCode, limit],
    );
    return rows.map(readAuditEvent);
  }

  public async markUploaded(ids: readonly string[]): Promise<void> {
    const scope = this.auditScope;
    if (!scope) return;
    await Promise.all(ids.map((id) => this.db.run(
      `UPDATE audit_events
       SET uploaded_at_iso = ?, delivery_state = 'uploaded', last_error_code = NULL
       WHERE event_id = ?
         AND uploaded_at_iso IS NULL
         AND scope_store_code = ?
         AND scope_device_code = ?`,
      [this.nowIso(), id, scope.storeCode, scope.deviceCode],
    )));
  }
}

class SqliteOperationAuditDelivery implements OperationAuditDeliveryPort {
  public constructor(
    private readonly db: SqliteConnectionPort,
    private readonly nowIso: () => string,
    private readonly auditScope: AuditScope | null,
  ) {}

  public async listReady(limit: number): Promise<readonly OperationAuditDeliveryEvent[]> {
    if (!this.auditScope) return [];
    const nowIso = this.nowIso();
    const rows = await this.db.getAll<AuditRow>(
      `WITH scoped_pending AS (
         SELECT *, COALESCE(next_attempt_at_iso, occurred_at_iso) AS ready_at_iso
         FROM audit_events
         WHERE uploaded_at_iso IS NULL
           AND delivery_state = 'pending'
           AND scope_store_code = ?
           AND scope_device_code = ?
       ), ordered_pending AS (
         SELECT *,
           MAX(CASE WHEN ready_at_iso > ? THEN 1 ELSE 0 END) OVER (
             ORDER BY occurred_at_iso, event_id
             ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
           ) AS has_delayed_predecessor
         FROM scoped_pending
       )
       SELECT * FROM ordered_pending
       WHERE ready_at_iso <= ?
         AND has_delayed_predecessor = 0
       ORDER BY occurred_at_iso, event_id
       LIMIT ?`,
      [
        this.auditScope.storeCode,
        this.auditScope.deviceCode,
        nowIso,
        nowIso,
        limit,
      ],
    );
    return rows.map((row) => ({
      ...readAuditEvent(row),
      attemptCount: Math.max(0, Number(row.attempt_count ?? 0) || 0),
    }));
  }
  public async nextReadyAtIso(): Promise<string | null> {
    if (!this.auditScope) return null;
    const row = await this.db.getFirst<{ next_attempt_at_iso: unknown }>(
      `SELECT COALESCE(next_attempt_at_iso, occurred_at_iso) AS next_attempt_at_iso
       FROM audit_events
       WHERE uploaded_at_iso IS NULL
         AND delivery_state = 'pending'
         AND scope_store_code = ?
         AND scope_device_code = ?
       ORDER BY occurred_at_iso, event_id
       LIMIT 1`,
      [this.auditScope.storeCode, this.auditScope.deviceCode],
    );
    return typeof row?.next_attempt_at_iso === "string"
      ? row.next_attempt_at_iso
      : null;
  }
  public async markUploaded(eventIds: readonly string[]): Promise<void> {
    const auditScope = this.auditScope;
    if (!auditScope) return;
    await Promise.all(eventIds.map((eventId) => this.db.run(
      `UPDATE audit_events
       SET uploaded_at_iso = ?, delivery_state = 'uploaded', last_error_code = NULL
       WHERE event_id = ?
         AND delivery_state = 'pending'
         AND scope_store_code = ?
         AND scope_device_code = ?`,
      [
        this.nowIso(),
        eventId,
        auditScope.storeCode,
        auditScope.deviceCode,
      ],
    )));
  }
  public async markRejected(entries: readonly Readonly<{ eventId: string; code: string }>[]): Promise<void> {
    const auditScope = this.auditScope;
    if (!auditScope) return;
    await Promise.all(entries.map((entry) => this.db.run(
      `UPDATE audit_events
       SET delivery_state = 'rejected', last_error_code = ?
       WHERE event_id = ?
         AND delivery_state = 'pending'
         AND scope_store_code = ?
         AND scope_device_code = ?`,
      [
        entry.code,
        entry.eventId,
        auditScope.storeCode,
        auditScope.deviceCode,
      ],
    )));
  }
  public async releaseRetry(eventIds: readonly string[], nextAttemptAtIso: string, errorCode: string): Promise<void> {
    const auditScope = this.auditScope;
    if (!auditScope) return;
    await Promise.all(eventIds.map((eventId) => this.db.run(
      `UPDATE audit_events
       SET attempt_count = attempt_count + 1, next_attempt_at_iso = ?, last_error_code = ?
       WHERE event_id = ?
         AND delivery_state = 'pending'
         AND scope_store_code = ?
         AND scope_device_code = ?`,
      [
        nextAttemptAtIso,
        errorCode,
        eventId,
        auditScope.storeCode,
        auditScope.deviceCode,
      ],
    )));
  }
}

class SqlitePaymentAttemptRepository implements PaymentAttemptRepositoryPort {
  public constructor(private readonly db: SqliteConnectionPort, private readonly encryptor: SensitivePayloadEncryptor) {}
  public async insertIfUnblocked(attempt: PaymentAttempt): Promise<PaymentAttempt | null> {
    if (attempt.state !== "Created") throw new Error("A new payment attempt must start in Created.");
    const ciphertext = await this.encryptReferences(attempt);
    const receiptCiphertext = await this.encryptReceipt(attempt);
    const responseCode = responseCodeOrNull(attempt.responseCode);
    return this.db.withExclusiveTransaction(async (transaction) => {
      // 支付渠道调用前的最终持久门：订单完成与创建 attempt 必须由同一个
      // BEGIN IMMEDIATE 排序，完成态或缺单一律失败关闭，绝不能留下 Created。
      const order = await transaction.getFirst<{ state: unknown }>(
        "SELECT state FROM local_orders WHERE order_guid = ?",
        [attempt.orderGuid],
      );
      if (order?.state !== "Draft" && order?.state !== "Completing") {
        throw new Error("Payment attempts require a Draft or Completing local order.");
      }
      const blocking = await this.findBlockingWith(transaction, attempt.orderGuid);
      if (blocking) return blocking;
      const inserted = await transaction.run(
        "INSERT INTO payment_attempts (attempt_id,idempotency_key,order_guid,provider,operation,amount_cents,state,checkout_id,payment_id,session_id,txn_ref,rfn,provider_payload_ciphertext,provider_receipt_ciphertext,provider_response_code,created_at_iso,updated_at_iso,last_error_code,provider_environment,provider_acknowledged_at_iso) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
        insertParameters(attempt, ciphertext, receiptCiphertext, responseCode),
      );
      if (inserted.changes !== 1) {
        throw new Error("Payment attempt was not durably inserted.");
      }
      return null;
    });
  }

  public async compareAndUpdate(
    expected: PaymentAttempt,
    next: PaymentAttempt,
    protectedSyncEvidence?: CardSyncEvidenceV1,
  ): Promise<boolean> {
    // attempt 身份参与支付渠道幂等，CAS 只允许推进状态和补充渠道回执，绝不能借更新改绑订单或金额。
    if (!hasSamePaymentAttemptIdentity(expected, next)) return false;
    const normalizedEvidence =
      protectedSyncEvidence === undefined
        ? undefined
        : normalizeCardSyncEvidence(protectedSyncEvidence);
    if (normalizedEvidence !== undefined) {
      assertCardSyncEvidenceBinding(next, normalizedEvidence);
    }
    const receiptCiphertext = await this.encryptReceipt(next);
    const responseCode = responseCodeOrNull(next.responseCode);
    return this.db.withExclusiveTransaction(async (transaction) => {
      const current = await transaction.getFirst<PaymentAttemptCasRow>(
        `SELECT idempotency_key, order_guid, provider, operation,
          amount_cents, created_at_iso, provider_payload_ciphertext
         FROM payment_attempts
         WHERE attempt_id = ? AND state = ? AND updated_at_iso = ?`,
        [expected.attemptId, expected.state, expected.updatedAtIso],
      );
      if (!current || !persistedAttemptMatchesExpected(current, expected)) {
        return false;
      }
      const existing = await decryptPaymentProtectedMaterial(
        this.encryptor,
        bytesOrNull(
          current.provider_payload_ciphertext,
          "Invalid payment ciphertext.",
        ),
      );
      const ciphertext = await this.encryptReferences(
        next,
        normalizedEvidence ?? existing.cardSyncEvidence,
      );
      const changed = await transaction.run(
        "UPDATE payment_attempts SET state=?,checkout_id=?,payment_id=?,session_id=?,txn_ref=?,rfn=?,provider_payload_ciphertext=?,provider_receipt_ciphertext=?,provider_response_code=?,updated_at_iso=?,last_error_code=? WHERE attempt_id=? AND idempotency_key=? AND order_guid=? AND provider=? AND operation=? AND amount_cents=? AND created_at_iso=? AND state=? AND updated_at_iso=?",
        [
          next.state, next.references.checkoutId, next.references.paymentId, next.references.sessionId, next.references.txnRef,
          next.references.rfn, ciphertext, receiptCiphertext, responseCode, next.updatedAtIso, next.lastErrorCode,
          expected.attemptId, expected.idempotencyKey, expected.orderGuid,
          expected.provider, expected.operation, expected.amount.cents,
          expected.createdAtIso, expected.state, expected.updatedAtIso,
        ],
      );
      return changed.changes === 1;
    });
  }

  public async get(id: string): Promise<PaymentAttempt | null> { const row = await this.db.getFirst<PaymentRow>("SELECT * FROM payment_attempts WHERE attempt_id=?", [id]); return row ? this.read(row) : null; }
  public findBlocking(orderGuid: string): Promise<PaymentAttempt | null> { return this.findBlockingWith(this.db, orderGuid); }

  public async markProviderAcknowledged(
    expected: PaymentAttempt,
    acknowledgedAtIso: string,
  ): Promise<boolean> {
    const environment = providerEnvironmentOrNull(expected.providerEnvironment);
    if (expected.provider !== "linkly-cloud" || environment === null || (expected.providerAcknowledgedAtIso !== null && expected.providerAcknowledgedAtIso !== undefined) || !isLinklyFinalState(expected.state) || !isIsoInstant(acknowledgedAtIso)) return false;
    if (expected.state === "Approved" && expected.operation === "refund" && !(await this.hasBoundRefundCardEvidence(expected))) return false;
    // 中文注释：退款 allocation 已完成且与原 attempt 精确绑定后可逐笔 ACK；
    // 仍要求受保护的卡证据存在，不能因整张 return action 尚未结束而提前放行。
    const changed = await this.db.run(
      `UPDATE payment_attempts AS p
       SET provider_acknowledged_at_iso = ?
       WHERE p.attempt_id = ? AND p.idempotency_key = ? AND p.order_guid = ?
         AND p.provider = 'linkly-cloud' AND p.provider_environment = ?
         AND p.provider_acknowledged_at_iso IS NULL
         AND p.state IN ('Approved', 'Declined', 'Cancelled')
         AND (p.state <> 'Approved'
           OR (p.operation = 'purchase' AND EXISTS (SELECT 1 FROM order_tenders t WHERE t.payment_attempt_id = p.attempt_id GROUP BY t.payment_attempt_id HAVING COUNT(*) = 1 AND MAX(CASE WHEN t.order_guid = p.order_guid AND t.amount_cents = p.amount_cents AND t.method = 'card' THEN 1 ELSE 0 END) = 1))
           OR (p.operation = 'refund' AND EXISTS (SELECT 1 FROM return_action_allocations allocation INNER JOIN return_actions action ON action.action_id = allocation.action_id INNER JOIN payment_action_bindings binding ON binding.action_id = allocation.external_action_id AND binding.attempt_id = p.attempt_id AND binding.idempotency_key = p.idempotency_key AND binding.order_guid = p.order_guid WHERE allocation.durable_attempt_id = p.attempt_id AND allocation.external_attempt_kind = 'payment-provider' AND allocation.execution_kind = 'online-refund' AND allocation.method = 'card' AND allocation.signed_amount_cents = p.amount_cents AND allocation.status = 'completed' AND action.return_order_guid = p.order_guid AND p.provider_payload_ciphertext IS NOT NULL)))`,
      [acknowledgedAtIso, expected.attemptId, expected.idempotencyKey, expected.orderGuid, environment],
    );
    return changed.changes === 1;
  }
  public async canProviderAcknowledged(
    attempt: PaymentAttempt,
  ): Promise<boolean> {
    if (attempt.provider !== "linkly-cloud" || !isLinklyFinalState(attempt.state)) return false;
    if (attempt.state !== "Approved") return true;
    if (attempt.operation === "refund" && !(await this.hasBoundRefundCardEvidence(attempt))) return false;
    const row = await this.db.getFirst<{ matched: unknown }>(
      `SELECT 1 AS matched FROM payment_attempts p WHERE p.attempt_id = ? AND p.idempotency_key = ? AND p.order_guid = ? AND p.provider = 'linkly-cloud' AND p.state = 'Approved' AND ((p.operation = 'purchase' AND EXISTS (SELECT 1 FROM order_tenders t WHERE t.payment_attempt_id = p.attempt_id GROUP BY t.payment_attempt_id HAVING COUNT(*) = 1 AND MAX(CASE WHEN t.order_guid = p.order_guid AND t.amount_cents = p.amount_cents AND t.method = 'card' THEN 1 ELSE 0 END) = 1)) OR (p.operation = 'refund' AND EXISTS (SELECT 1 FROM return_action_allocations allocation INNER JOIN return_actions action ON action.action_id = allocation.action_id INNER JOIN payment_action_bindings binding ON binding.action_id = allocation.external_action_id AND binding.attempt_id = p.attempt_id AND binding.idempotency_key = p.idempotency_key AND binding.order_guid = p.order_guid WHERE allocation.durable_attempt_id = p.attempt_id AND allocation.external_attempt_kind = 'payment-provider' AND allocation.execution_kind = 'online-refund' AND allocation.method = 'card' AND allocation.signed_amount_cents = p.amount_cents AND allocation.status = 'completed' AND action.return_order_guid = p.order_guid AND p.provider_payload_ciphertext IS NOT NULL)))`,
      [attempt.attemptId, attempt.idempotencyKey, attempt.orderGuid],
    );
    return row?.matched === 1;
  }
  public async verifyProviderEnvironment(expected: PaymentAttempt, verifiedEnvironment: string): Promise<boolean> {
    const environment = providerEnvironmentOrNull(verifiedEnvironment);
    if (expected.provider !== "linkly-cloud" || environment === null || !isLinklyEnvironmentVerifiableState(expected.state) || expected.providerEnvironment) return false;
    // 只冻结经过只读核验的原交易；状态、金额或引用有任一变化都必须重新核验。
    const changed = await this.db.run(
      `UPDATE payment_attempts SET provider_environment = ?
       WHERE attempt_id = ? AND idempotency_key = ? AND order_guid = ?
         AND provider = 'linkly-cloud' AND operation = ? AND amount_cents = ? AND state = ?
         AND session_id IS ? AND txn_ref IS ?
         AND provider_environment IS NULL AND provider_acknowledged_at_iso IS NULL`,
      [environment, expected.attemptId, expected.idempotencyKey, expected.orderGuid, expected.operation, expected.amount.cents, expected.state, expected.references.sessionId, expected.references.txnRef],
    );
    return changed.changes === 1;
  }

  private async findBlockingWith(database: SqliteConnectionPort, orderGuid: string): Promise<PaymentAttempt | null> {
    const row = await database.getFirst<PaymentRow>(
      `SELECT p.* FROM payment_attempts p
       WHERE p.order_guid = ?
         AND (
           p.state IN ('Created', 'Submitted', 'Pending', 'Unknown')
           OR (p.state = 'Approved' AND NOT EXISTS (
             SELECT 1
             FROM order_tenders t
             WHERE t.payment_attempt_id = p.attempt_id
             GROUP BY t.payment_attempt_id
             HAVING COUNT(*) = 1
                AND MAX(CASE WHEN t.order_guid = p.order_guid
                                  AND t.amount_cents = p.amount_cents
                                    AND ((p.provider IN ('square', 'linkly-cloud', 'manual-card') AND t.method = 'card')
                                    OR (p.provider = 'voucher' AND t.method = 'voucher'))
                             THEN 1 ELSE 0 END) = 1
           ))
         )
       ORDER BY p.updated_at_iso DESC, p.attempt_id DESC LIMIT 1`,
      [orderGuid],
    );
    return row ? this.read(row) : null;
  }

  private async hasBoundRefundCardEvidence(attempt: PaymentAttempt): Promise<boolean> {
    const row = await this.db.getFirst<{ provider_payload_ciphertext: unknown }>(
      "SELECT provider_payload_ciphertext FROM payment_attempts WHERE attempt_id = ? AND idempotency_key = ? AND order_guid = ?",
      [attempt.attemptId, attempt.idempotencyKey, attempt.orderGuid],
    );
    try {
      const material = await decryptPaymentProtectedMaterial(this.encryptor, bytesOrNull(row?.provider_payload_ciphertext, "Invalid payment ciphertext."));
      if (material.cardSyncEvidence === null) return false;
      assertCardSyncEvidenceBinding(attempt, material.cardSyncEvidence);
      // 同额退款仍可能属于另一笔交易，ACK 必须绑定原 session 的交易号。
      return Boolean(attempt.references.sessionId?.trim()) &&
        Boolean(attempt.references.txnRef?.trim()) &&
        material.cardSyncEvidence.txnRef === attempt.references.txnRef;
    } catch {
      return false;
    }
  }

  private encryptReferences(
    attempt: PaymentAttempt,
    cardSyncEvidence: CardSyncEvidenceV1 | null = null,
  ): Promise<Uint8Array | null> {
    return encryptPaymentProtectedMaterial(this.encryptor, {
      voucherReservationToken: attempt.references.voucherReservationToken,
      cardSyncEvidence,
    });
  }

  private async encryptReceipt(attempt: PaymentAttempt): Promise<Uint8Array | null> {
    const receipt = attempt.receiptText ?? null;
    if (receipt === null) return null;
    if (receipt.length > 16_384) throw new Error("Payment receipt is too large to persist.");
    return this.encryptor.encrypt(receipt);
  }

  private async read(r: PaymentRow): Promise<PaymentAttempt> {
    const cipher = bytesOrNull(r.provider_payload_ciphertext, "Invalid payment ciphertext.");
    const receiptCipher = bytesOrNull(r.provider_receipt_ciphertext, "Invalid payment receipt ciphertext.");
    const [protectedMaterial, receiptText] = await Promise.all([
      decryptPaymentProtectedMaterial(this.encryptor, cipher),
      receiptCipher ? this.encryptor.decrypt(receiptCipher) : Promise.resolve(null),
    ]);
    const responseCode = responseCodeOrNull(nullable(r.provider_response_code));
    const providerEnvironment = providerEnvironmentOrNull(nullable(r.provider_environment)); const providerAcknowledgedAtIso = nullable(r.provider_acknowledged_at_iso);
    return { attemptId:text(r.attempt_id),idempotencyKey:text(r.idempotency_key),orderGuid:text(r.order_guid),provider:r.provider as never,operation:r.operation as never,amount:money(r.amount_cents),state:r.state as never,references:{checkoutId:nullable(r.checkout_id),paymentId:nullable(r.payment_id),sessionId:nullable(r.session_id),txnRef:nullable(r.txn_ref),rfn:nullable(r.rfn),voucherReservationToken:protectedMaterial.voucherReservationToken},createdAtIso:text(r.created_at_iso),updatedAtIso:text(r.updated_at_iso),lastErrorCode:nullable(r.last_error_code),...(providerEnvironment ? { providerEnvironment } : {}),...(providerAcknowledgedAtIso ? { providerAcknowledgedAtIso } : {}),receiptText,responseCode};
  }
}

function hasSamePaymentAttemptIdentity(expected: PaymentAttempt, next: PaymentAttempt): boolean {
  return expected.attemptId === next.attemptId
    && expected.idempotencyKey === next.idempotencyKey
    && expected.orderGuid === next.orderGuid
    && expected.provider === next.provider
    && expected.operation === next.operation
    && expected.amount.currency === next.amount.currency
    && expected.amount.cents === next.amount.cents
    && expected.createdAtIso === next.createdAtIso;
}

function persistedAttemptMatchesExpected(
  row: PaymentAttemptCasRow,
  expected: PaymentAttempt,
): boolean {
  return row.idempotency_key === expected.idempotencyKey &&
    row.order_guid === expected.orderGuid &&
    row.provider === expected.provider &&
    row.operation === expected.operation &&
    row.amount_cents === expected.amount.cents &&
    row.created_at_iso === expected.createdAtIso;
}

function assertCardSyncEvidenceBinding(
  attempt: PaymentAttempt,
  evidence: CardSyncEvidenceV1,
): void {
  if (
    attempt.state !== "Approved" ||
    attempt.provider === "voucher" ||
    evidence.provider !== attempt.provider ||
    evidence.operation !== attempt.operation ||
    evidence.amountCents !== Math.abs(attempt.amount.cents) ||
    (attempt.operation === "purchase" && attempt.amount.cents <= 0) ||
    (attempt.operation === "refund" && attempt.amount.cents >= 0)
  ) {
    throw new TypeError(
      "Card sync evidence does not match its approved payment attempt.",
    );
  }
}

function insertParameters(attempt: PaymentAttempt, ciphertext: Uint8Array | null, receiptCiphertext: Uint8Array | null, responseCode: string | null): readonly SqlValue[] {
  return [attempt.attemptId,attempt.idempotencyKey,attempt.orderGuid,attempt.provider,attempt.operation,attempt.amount.cents,attempt.state,attempt.references.checkoutId,attempt.references.paymentId,attempt.references.sessionId,attempt.references.txnRef,attempt.references.rfn,ciphertext,receiptCiphertext,responseCode,attempt.createdAtIso,attempt.updatedAtIso,attempt.lastErrorCode,providerEnvironmentOrNull(attempt.providerEnvironment),null];
}

function providerEnvironmentOrNull(value: unknown): string | null { if (value === null || value === undefined) return null; if (typeof value !== "string") throw new Error("Payment provider environment is invalid."); const normalized = value.trim(); if (!normalized || normalized.length > 64 || /[\u0000-\u001f\u007f]/u.test(normalized)) throw new Error("Payment provider environment is invalid."); return normalized; }
function isLinklyFinalState(state: PaymentAttempt["state"]): boolean { return state === "Approved" || state === "Declined" || state === "Cancelled"; }
function isLinklyEnvironmentVerifiableState(state: PaymentAttempt["state"]): boolean { return state === "Submitted" || state === "Pending" || state === "Unknown" || isLinklyFinalState(state); }
function isIsoInstant(value: string): boolean { return Number.isFinite(Date.parse(value)) && /(?:Z|[+-]\d{2}:\d{2})$/u.test(value); }

class SqlitePrintJobRepository implements PrintJobRepositoryPort { public constructor(private readonly db: SqliteConnectionPort) {} public async transition(id: string, expected: never, next: never): Promise<boolean> { return (await this.db.run("UPDATE print_jobs SET state=? WHERE job_id=? AND state=?",[next,id,expected])).changes===1; } }
class SqliteDrawerEventRepository implements DrawerEventRepositoryPort { public constructor(private readonly db: SqliteConnectionPort) {} public async transition(id: string, expected: never, next: never): Promise<boolean> { return (await this.db.run("UPDATE drawer_events SET state=? WHERE event_id=? AND state=?",[next,id,expected])).changes===1; } }

type OrderRow = Record<string, unknown>; type LineRow = Record<string, unknown>; type TenderRow = Record<string, unknown>; type OutboxRow = Record<string, unknown>; type AuditRow = Record<string, unknown>; type PaymentRow = Record<string, unknown>;
type PaymentAttemptCasRow = Readonly<{
  idempotency_key: unknown;
  order_guid: unknown;
  provider: unknown;
  operation: unknown;
  amount_cents: unknown;
  created_at_iso: unknown;
  provider_payload_ciphertext: unknown;
}>;
function readOrderTender(row: TenderRow): OrderTender {
  const method = tenderMethod(row.method);
  const amount = money(row.amount_cents);
  return {
    tenderGuid: text(row.tender_guid),
    method,
    amount,
    reference: method === "card" ? approvedCardReference(row, amount.cents) : null,
    // 现有密文列只保存 vpr_* 保护句柄，不含后端要消费的 voucherCode/reservationToken。
    // 在增加受保护状态表和解析 Port 前必须保持 null，让同步适配器失败关闭。
    reservationToken: null,
  };
}
function approvedCardReference(row: TenderRow, tenderAmountCents: number): string | null {
  if (
    nullable(row.payment_attempt_id) === null ||
    nullable(row.attempt_order_guid) !== text(row.order_guid) ||
    nullable(row.attempt_state) !== "Approved" ||
    intOrNull(row.attempt_amount_cents) !== tenderAmountCents ||
    nullable(row.attempt_provider) !== "square"
  ) {
    return null;
  }
  const operation = nullable(row.attempt_operation);
  const paymentId = providerReferencePart(row.attempt_payment_id);
  if (operation === "purchase" && tenderAmountCents > 0 && paymentId) {
    return `SQ:${paymentId}`;
  }
  if (operation === "refund" && tenderAmountCents < 0 && paymentId) {
    const refundId = providerReferencePart(row.attempt_response_code);
    if (!refundId) return null;
    return `CARD_REFUND|refund=${encodeURIComponent(`SQRF:${refundId}`)}|original=${encodeURIComponent(`SQ:${paymentId}`)}`;
  }
  // Linkly 的 WPF 等价引用还需要 environment 和可能独立于 txnRef 的 RFN；
  // 当前 payment_attempts 未持久化完整字段，不能在订单同步前猜造 ANZBACKEND 引用。
  return null;
}
function tenderMethod(value: unknown): OrderTender["method"] {
  const method = text(value);
  if (method !== "cash" && method !== "card" && method !== "voucher") {
    throw new Error("Invalid tender method.");
  }
  return method;
}
function providerReferencePart(value: unknown): string | null {
  const candidate = nullable(value)?.trim() ?? null;
  if (
    candidate === null ||
    candidate.length === 0 ||
    candidate.length > 256 ||
    /[\u0000-\u001f\u007f]/.test(candidate)
  ) {
    return null;
  }
  return candidate;
}
function intOrNull(value: unknown): number | null {
  if (value === null || value === undefined) return null;
  return int(value);
}
async function prepareOrderForSyncLease(
  transaction: SqliteConnectionPort,
  orderGuid: string,
  previousOutboxState: string,
  nowIso: string,
): Promise<void> {
  if (previousOutboxState === "pending") {
    if (
      !canTransitionOrder("CompletedLocal", "PendingSync") ||
      !canTransitionOrder("PendingSync", "Syncing")
    ) {
      throw new Error("Order state machine cannot enter Syncing.");
    }
    // CompletedLocal 不能直接跳到 Syncing；同一事务内先走合法的 PendingSync 中间态。
    await transaction.run(
      "UPDATE local_orders SET state = 'PendingSync', updated_at_iso = ? WHERE order_guid = ? AND state = 'CompletedLocal'",
      [nowIso, orderGuid],
    );
    const syncing = await transaction.run(
      "UPDATE local_orders SET state = 'Syncing', updated_at_iso = ? WHERE order_guid = ? AND state = 'PendingSync'",
      [nowIso, orderGuid],
    );
    if (syncing.changes !== 1) {
      throw new Error(`Order cannot enter Syncing from its current state: ${orderGuid}`);
    }
    return;
  }
  if (previousOutboxState === "leased") {
    // 过期租约恢复不是状态迁移，只以原状态 CAS 验证仍由 Syncing 账本承接。
    const syncing = await transaction.run(
      "UPDATE local_orders SET updated_at_iso = ? WHERE order_guid = ? AND state = 'Syncing'",
      [nowIso, orderGuid],
    );
    if (syncing.changes !== 1) {
      throw new Error(`Expired order-sync lease has invalid order state: ${orderGuid}`);
    }
    return;
  }
  throw new Error(`Invalid outbox state selected for lease: ${previousOutboxState}`);
}
async function requireOwnedOutboxLease(
  transaction: SqliteConnectionPort,
  lease: OutboxLease,
): Promise<Readonly<{ aggregateId: string; kind: OutboxLease["kind"] }>> {
  const row = await transaction.getFirst<OutboxRow>(
    "SELECT aggregate_id, kind, state, lease_id FROM outbox_messages WHERE message_id = ?",
    [lease.messageId],
  );
  if (
    !row ||
    nullable(row.state) !== "leased" ||
    nullable(row.lease_id) !== lease.leaseId
  ) {
    throw new Error(`Outbox lease is no longer owned: ${lease.messageId}`);
  }
  const aggregateId = text(row.aggregate_id);
  const kind = outboxKind(row.kind);
  if (aggregateId !== lease.aggregateId || kind !== lease.kind) {
    throw new Error(`Outbox lease identity changed: ${lease.messageId}`);
  }
  return { aggregateId, kind };
}
function outboxKind(value: unknown): OutboxLease["kind"] {
  const kind = text(value);
  if (kind !== "order-sync" && kind !== "audit-batch") {
    throw new Error("Invalid outbox kind.");
  }
  return kind;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function nonBlank(value: unknown, label: string): string {
  if (typeof value !== "string") {
    throw new Error(`Invalid ${label}.`);
  }
  const normalized = value.trim();
  if (normalized.length === 0) throw new Error(`Invalid ${label}.`);
  return normalized;
}

function canonicalIso(value: unknown, label: string): string {
  const raw = nonBlank(value, label);
  const time = Date.parse(raw);
  if (!Number.isFinite(time)) throw new Error(`Invalid ${label}.`);
  return new Date(time).toISOString();
}

function nonNegativeSafeInteger(value: unknown, label: string): number {
  const numberValue = Number(value);
  if (!Number.isSafeInteger(numberValue) || numberValue < 0) {
    throw new Error(`Invalid ${label}.`);
  }
  return numberValue;
}

function validateCatalogDiscountBasisPoints(value: unknown): number {
  // 旧挂单快照没有该字段时按零恢复；显式 null 或越界值仍视为损坏快照。
  if (value === undefined) return 0;
  if (value === null) {
    throw new Error("Invalid held catalog discount basis points.");
  }
  const basisPoints = nonNegativeSafeInteger(
    value,
    "held catalog discount basis points",
  );
  if (basisPoints > 10_000) {
    throw new Error("Invalid held catalog discount basis points.");
  }
  return basisPoints;
}

function validateHeldOrderScope(input: HeldOrderScope): HeldOrderScope {
  return {
    storeCode: nonBlank(input.storeCode, "held order store code"),
    deviceCode: nonBlank(input.deviceCode, "held order device code"),
  };
}

function validateRecallBinding(input: RecallActiveBinding): RecallActiveBinding {
  if (input.kind !== "recalled") {
    throw new Error("Invalid recall binding kind.");
  }
  return {
    kind: "recalled",
    scope: validateHeldOrderScope(input.scope),
    holdId: nonBlank(input.holdId, "recall binding hold id"),
    recallAttemptId: nonBlank(
      input.recallAttemptId,
      "recall binding attempt id",
    ),
  };
}

function validateHeldOrderActor(input: HeldOrderActor): HeldOrderActor {
  return {
    cashierId: nonBlank(input.cashierId, "held order cashier id"),
    cashierName: nonBlank(input.cashierName, "held order cashier name"),
  };
}

function validateHeldOrderPayload(value: unknown): HeldOrderPayloadV1 {
  if (!isRecord(value) || value.version !== 1 || !isRecord(value.pricingState)) {
    throw new Error("Invalid held order payload.");
  }
  return {
    version: 1,
    pricingState: validatePricingState(value.pricingState),
  };
}

/**
 * 此处不依赖 sales feature 的 PricingCart，避免 persistence 反向引用业务层。
 * 校验与金额摘要均从冻结快照重建；任一字段缺失或越界都拒绝恢复。
 */
function validatePricingState(value: Record<string, unknown>): PricingCartStateSnapshot {
  const revision = nonNegativeSafeInteger(value.revision, "held cart revision");
  const mode = value.mode;
  // 首版 V2 仅保存普通 sale 购物车；退货和分期必须走各自的完整账本流程，
  // 绝不能借挂单绕过支付容量、原单关联或在线门禁。
  if (mode !== "sale") {
    throw new Error("Invalid held cart mode.");
  }
  const asOfIso = canonicalIso(value.asOfIso, "held cart as-of time");
  if (!Array.isArray(value.promotions) || !Array.isArray(value.lines)) {
    throw new Error("Invalid held cart collections.");
  }
  if (value.lines.length === 0) {
    throw new Error("Held cart must contain at least one sale line.");
  }

  const promotions = value.promotions.map(validatePromotion);
  const seenLineIds = new Set<string>();
  const lines = value.lines.map((line) => {
    if (!isRecord(line)) throw new Error("Invalid held cart line.");
    const lineId = nonBlank(line.lineId, "held cart line id");
    if (seenLineIds.has(lineId)) throw new Error("Duplicate held cart line id.");
    seenLineIds.add(lineId);
    const productCode = nonBlank(line.productCode, "held cart product code");
    const lookupCode = nonBlank(line.lookupCode, "held cart lookup code");
    const displayName = nonBlank(line.displayName, "held cart display name");
    const quantity = heldCartQuantity(line.quantity, "held cart quantity");
    const unitPriceCents = nonNegativeSafeInteger(line.unitPriceCents, "held cart unit price");
    const basePriceSource = line.basePriceSource;
    if (
      basePriceSource !== "catalog" &&
      basePriceSource !== "manual" &&
      basePriceSource !== "open-item"
    ) {
      throw new Error("Invalid held cart price source.");
    }
    const catalogDiscountBasisPoints = validateCatalogDiscountBasisPoints(
      line.catalogDiscountBasisPoints,
    );
    if (basePriceSource === "open-item" && catalogDiscountBasisPoints > 0) {
      throw new Error("Held open-item line cannot contain a catalog discount.");
    }
    let syncProvenance: LineSyncProvenance;
    try {
      syncProvenance = normalizeLineSyncProvenance(
        line.syncProvenance,
      );
    } catch {
      throw new Error("Held cart line sync provenance is invalid.");
    }
    if (line.kind !== "sale") throw new Error("Held cart only supports sale lines.");
    const kind = "sale" as const;
    const itemNumber = nullableText(line.itemNumber, "held cart item number");
    const returnSourceKey = nullableText(line.returnSourceKey, "held cart return source");
    const originalOrderGuid = nullableText(line.originalOrderGuid, "held cart original order");
    const originalOrderDetailGuid = nullableText(line.originalOrderDetailGuid, "held cart original detail");
    const discountState = validatePricingDiscount(line.discountState, kind, unitPriceCents, quantity, basePriceSource);
    if (
      catalogDiscountBasisPoints > 0 &&
      discountState.kind === "promotion"
    ) {
      throw new Error(
        "Held catalog discount cannot coexist with promotion discount state.",
      );
    }
    return {
      lineId,
      productCode,
      itemNumber,
      lookupCode,
      displayName,
      quantity,
      unitPriceCents,
      basePriceSource,
      catalogDiscountBasisPoints,
      syncProvenance,
      kind,
      returnSourceKey,
      originalOrderGuid,
      originalOrderDetailGuid,
      discountState,
    };
  });

  return { revision, mode, asOfIso, promotions, lines } as PricingCartStateSnapshot;
}

function validatePromotion(value: unknown): PricingCartStateSnapshot["promotions"][number] {
  if (!isRecord(value) || !isRecord(value.fixedPrice) || !Array.isArray(value.products)) {
    throw new Error("Invalid held cart promotion.");
  }
  const fixedPriceCents = nonNegativeSafeInteger(value.fixedPrice.cents, "held promotion fixed price");
  if (value.fixedPrice.currency !== "AUD") throw new Error("Invalid held promotion currency.");
  const maxApplications = value.maxApplicationsPerOrder;
  if (maxApplications !== null && maxApplications !== undefined) {
    nonNegativeSafeInteger(maxApplications, "held promotion maximum applications");
  }
  const effectiveStartIso = canonicalIso(value.effectiveStartIso, "held promotion start");
  const effectiveEndIso = canonicalIso(value.effectiveEndIso, "held promotion end");
  if (Date.parse(effectiveStartIso) > Date.parse(effectiveEndIso)) {
    throw new Error("Invalid held promotion date range.");
  }
  if (typeof value.isExclusive !== "boolean") {
    throw new Error("Invalid held promotion exclusivity.");
  }
  return {
    id: nonBlank(value.id, "held promotion id"),
    name: nonBlank(value.name, "held promotion name"),
    effectiveStartIso,
    effectiveEndIso,
    isExclusive: value.isExclusive,
    priority: int(value.priority),
    applyQuantity: nonNegativeSafeInteger(value.applyQuantity, "held promotion apply quantity"),
    fixedPrice: createAud(fixedPriceCents),
    maxApplicationsPerOrder: maxApplications === null || maxApplications === undefined
      ? null
      : nonNegativeSafeInteger(maxApplications, "held promotion maximum applications"),
    products: value.products.map((product) => {
      if (!isRecord(product)) throw new Error("Invalid held promotion product.");
      return {
        productCode: nonBlank(product.productCode, "held promotion product code"),
        unitWeight: int(product.unitWeight),
      };
    }),
  };
}

function nullableText(value: unknown, label: string): string | null {
  if (value === null || value === undefined) return null;
  return nonBlank(value, label);
}

function validatePricingDiscount(
  value: unknown,
  kind: "sale" | "return",
  unitPriceCents: number,
  quantity: number,
  basePriceSource: "catalog" | "manual" | "open-item",
): PricingCartStateSnapshot["lines"][number]["discountState"] {
  if (!isRecord(value) || typeof value.kind !== "string") {
    throw new Error("Invalid held cart discount.");
  }
  if (kind === "return" && value.kind !== "none") {
    throw new Error("Return held cart line cannot have a discount.");
  }
  const gross = multiplyCents(quantity, unitPriceCents, "held cart line gross");
  switch (value.kind) {
    case "none":
      return { kind: "none" };
    case "manual-amount": {
      const cents = nonNegativeSafeInteger(value.cents, "held manual discount");
      if (cents > gross) throw new Error("Held manual discount exceeds gross.");
      return { kind: "manual-amount", cents };
    }
    case "manual-percent": {
      const basisPoints = nonNegativeSafeInteger(value.basisPoints, "held percent discount");
      if (basisPoints > 10_000) throw new Error("Held percent discount exceeds 100%.");
      return { kind: "manual-percent", basisPoints };
    }
    case "promotion": {
      const cents = nonNegativeSafeInteger(value.cents, "held promotion discount");
      if (cents > gross || basePriceSource === "open-item" || !Array.isArray(value.promotionIds)) {
        throw new Error("Invalid held promotion discount.");
      }
      const promotionIds = value.promotionIds.map((id) => nonBlank(id, "held promotion discount id"));
      return { kind: "promotion", cents, promotionIds };
    }
    default:
      throw new Error("Invalid held cart discount.");
  }
}

function summarizePricingState(state: PricingCartStateSnapshot): Readonly<{
  itemCount: number;
  subtotalCents: number;
  discountCents: number;
  actualAmountCents: number;
}> {
  let itemCount = 0n;
  let subtotal = 0n;
  let discount = 0n;
  let actual = 0n;
  for (const line of state.lines) {
    const gross = BigInt(multiplyCents(line.quantity, line.unitPriceCents, "held cart line gross"));
    const lineDiscount = line.kind === "return"
      ? 0
      : discountCents(
          line.discountState,
          Number(gross),
          line.catalogDiscountBasisPoints ?? 0,
        );
    itemCount += heldLineItemCount(line.quantity);
    subtotal += line.kind === "return" ? -gross : gross;
    discount += BigInt(lineDiscount);
    actual += line.kind === "return" ? -gross : gross - BigInt(lineDiscount);
  }
  return {
    itemCount: bigintToSafeInteger(itemCount, "held cart item count"),
    subtotalCents: bigintToSafeInteger(subtotal, "held cart subtotal"),
    discountCents: bigintToSafeInteger(discount, "held cart discount"),
    actualAmountCents: bigintToSafeInteger(actual, "held cart actual amount"),
  };
}

function discountCents(
  state: PricingCartStateSnapshot["lines"][number]["discountState"],
  gross: number,
  catalogDiscountBasisPoints: number,
): number {
  // manual-amount:0 是整单折扣的显式人工覆盖，必须先于 catalog 基线处理；
  // 否则挂单列表摘要会与密文中的购物车及召回后金额不一致。
  if (state.kind === "manual-amount") {
    return Math.min(gross, state.cents);
  }
  if (state.kind === "manual-percent") {
    return Math.min(gross, roundRatio(gross, state.basisPoints, 10_000));
  }
  if (catalogDiscountBasisPoints > 0) {
    return Math.min(
      gross,
      roundRatio(gross, catalogDiscountBasisPoints, 10_000),
    );
  }

  switch (state.kind) {
    case "none":
      return 0;
    case "promotion":
      return Math.min(gross, state.cents);
  }
}

function multiplyCents(quantity: number, cents: number, label: string): number {
  // 与 canonical SharedSaleCartV1 一致：整数走 BigInt，称重小数按 C# decimal
  // AwayFromZero 精确取整（0.29 * 50 = 15，而不是 BigInt 直接抛错）。
  return multiplyCentsAwayFromZero(quantity, cents, label);
}

/** 称重小数数量按 1 行计件（CHECK item_count > 0）；整数路径保持原数量求和语义。 */
function heldLineItemCount(quantity: number): bigint {
  return BigInt(Number.isSafeInteger(quantity) && quantity > 0 ? quantity : 1);
}

/** 称重商品允许正有限小数（与 canonical SharedSaleCartV1 一致，上限 1_000_000）。 */
function heldCartQuantity(value: unknown, label: string): number {
  if (
    typeof value !== "number" ||
    !Number.isFinite(value) ||
    value <= 0 ||
    value > 1_000_000
  ) {
    throw new Error(`Invalid ${label}.`);
  }
  return value;
}

/** 与 PricingCart 一致：整数分币百分比在中点向远离零方向取整。 */
function roundRatio(left: number, right: number, denominator: number): number {
  let numerator = BigInt(left) * BigInt(right);
  const divisor = BigInt(denominator);
  const sign = numerator < 0n ? -1n : 1n;
  numerator = numerator < 0n ? -numerator : numerator;
  let quotient = numerator / divisor;
  if ((numerator % divisor) * 2n >= divisor) quotient += 1n;
  return bigintToSafeInteger(sign * quotient, "held percent discount");
}

function bigintToSafeInteger(value: bigint, label: string): number {
  if (
    value > BigInt(Number.MAX_SAFE_INTEGER) ||
    value < BigInt(Number.MIN_SAFE_INTEGER)
  ) {
    throw new Error(`Invalid ${label}.`);
  }
  return Number(value);
}

function readHeldOrderSummary(row: HeldOrderRecordRow): HeldOrderSummaryWithSynthetic {
  const status = text(row.status);
  if (status !== "Pending" && status !== "Recalling" && status !== "Recalled") {
    throw new Error("Invalid held order status.");
  }
  const recallingAtIso = nullable(row.recalling_at_iso);
  if (status === "Pending" && recallingAtIso !== null) {
    throw new Error("Invalid pending held order state.");
  }
  if (status !== "Pending" && recallingAtIso === null) {
    throw new Error("Invalid recalled held order state.");
  }
  const isSyntheticSharedClaim = int(row.is_synthetic_shared_claim ?? 0) === 1;
  return {
    holdId: nonBlank(row.hold_id, "held order id"),
    localSequence: nonNegativeSafeInteger(row.local_sequence, "held order sequence"),
    scope: {
      storeCode: nonBlank(row.store_code, "held order store code"),
      deviceCode: nonBlank(row.device_code, "held order device code"),
    },
    heldBy: {
      cashierId: nonBlank(row.held_by_cashier_id, "held order cashier id"),
      cashierName: nonBlank(row.held_by_cashier_name, "held order cashier name"),
    },
    status: status as HeldOrderStatus,
    itemCount: nonNegativeSafeInteger(row.item_count, "held order item count"),
    subtotalCents: int(row.subtotal_cents),
    discountCents: nonNegativeSafeInteger(row.discount_cents, "held order discount"),
    actualAmountCents: int(row.actual_amount_cents),
    heldAtIso: canonicalIso(row.held_at_iso, "held order time"),
    recallingAtIso: recallingAtIso === null
      ? null
      : canonicalIso(recallingAtIso, "held order recalling time"),
    ...(isSyntheticSharedClaim ? { isSyntheticSharedClaim: true } : {}),
  };
}

function readTerminalCartFence(row: TerminalCartFenceRow): TerminalCartFence {
  const kind = text(row.kind);
  if (kind !== "HoldClear" && kind !== "RecallActive") {
    throw new Error("Invalid terminal cart fence kind.");
  }
  const recallAttemptId = nullable(row.recall_attempt_id);
  const boundOrderGuid = nullable(row.bound_order_guid);
  if (
    (kind === "HoldClear" &&
      (recallAttemptId !== null || boundOrderGuid !== null)) ||
    (kind === "RecallActive" && recallAttemptId === null)
  ) {
    throw new Error("Invalid terminal cart fence state.");
  }
  return {
    scope: {
      storeCode: nonBlank(row.store_code, "terminal fence store code"),
      deviceCode: nonBlank(row.device_code, "terminal fence device code"),
    },
    kind,
    holdId: nonBlank(row.hold_id, "terminal fence hold id"),
    recallAttemptId: recallAttemptId === null
      ? null
      : nonBlank(recallAttemptId, "terminal fence recall attempt id"),
    boundOrderGuid: boundOrderGuid === null
      ? null
      : nonBlank(boundOrderGuid, "terminal fence bound order guid"),
    createdAtIso: canonicalIso(row.created_at_iso, "terminal fence created at"),
  };
}

async function decryptHeldOrderPayload(
  row: HeldOrderRecordRow,
  encryptor: SensitivePayloadEncryptor,
): Promise<HeldOrderPayloadV1> {
  if (int(row.payload_version) !== 1 || !(row.payload_ciphertext instanceof Uint8Array)) {
    throw new Error("Invalid held order payload record.");
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(await encryptor.decrypt(row.payload_ciphertext));
  } catch {
    throw new Error("Invalid held order payload ciphertext.");
  }
  if (int(row.is_synthetic_shared_claim ?? 0) === 1) {
    parsed = mapLegacySyntheticSharedPayload(parsed);
  }
  return validateHeldOrderPayload(parsed);
}

/**
 * 兼容旧版 RemoteClaim synthetic 行曾误存的 SharedSaleCartV1 wire 结构。
 * 仅 synthetic 标记行可进入；字段仍交给现有严格 validator fail-closed 校验。
 */
function mapLegacySyntheticSharedPayload(value: unknown): unknown {
  if (!isRecord(value) || !isRecord(value.pricingState)) return value;
  const pricingState = value.pricingState;
  if (!Array.isArray(pricingState.promotions) || !Array.isArray(pricingState.lines)) {
    return value;
  }
  return {
    version: value.version,
    pricingState: {
      ...pricingState,
      promotions: pricingState.promotions.map((promotion) => {
        if (!isRecord(promotion) || !("fixedPriceCents" in promotion)) {
          return promotion;
        }
        const { fixedPriceCents, ...rest } = promotion;
        return {
          ...rest,
          fixedPrice: { currency: "AUD", cents: fixedPriceCents },
        };
      }),
      lines: pricingState.lines.map((line) => {
        if (!isRecord(line) || !isRecord(line.discountState)) return line;
        const discount = line.discountState;
        switch (discount.mode) {
          case "none":
            return { ...line, discountState: { kind: "none" } };
          case "manual-amount":
            return {
              ...line,
              discountState: { kind: "manual-amount", cents: discount.cents },
            };
          case "manual-percent":
            return {
              ...line,
              discountState: {
                kind: "manual-percent",
                basisPoints: discount.basisPoints,
              },
            };
          case "promotion":
            return {
              ...line,
              discountState: {
                kind: "promotion",
                cents: discount.cents,
                promotionIds: discount.promotionIds,
              },
            };
          default:
            return line;
        }
      }),
    },
  };
}

function validateHeldOrderAudit(
  event: AuditEventDraft,
  expectedType: "ORDER_HOLD" | "ORDER_RECALL",
): AuditEventDraft {
  if (event.eventType !== expectedType) {
    throw new Error(`Held order audit must be ${expectedType}.`);
  }
  if (event.orderGuid !== null) {
    throw new Error("Held order audit must not reference a completed order.");
  }
  const payload = event.payload;
  if (!isRecord(payload)) throw new Error("Invalid held order audit payload.");
  assertSafe(payload);
  return {
    eventId: nonBlank(event.eventId, "held order audit event id"),
    eventType: expectedType,
    occurredAtIso: canonicalIso(event.occurredAtIso, "held order audit time"),
    orderGuid: null,
    correlationId: nonBlank(event.correlationId, "held order audit correlation"),
    payload,
  };
}

async function appendAuditEvent(
  transaction: SqliteConnectionPort,
  event: AuditEventDraft,
  scope: AuditScope,
): Promise<void> {
  await insertAuditEvent(transaction, event, scope, event.occurredAtIso);
}

/** 所有新员工审计写入经此处固定 scope，禁止在上传期读取当前设备身份。 */
async function insertAuditEvent(
  transaction: SqliteConnectionPort,
  event: AuditEventDraft,
  scope: AuditScope,
  nextAttemptAtIso: string,
): Promise<void> {
  const frozenScope = freezeAuditScope(scope);
  try {
    await transaction.run(
      `INSERT INTO audit_events (
        event_id, event_type, occurred_at_iso, order_guid, correlation_id,
        payload_json, uploaded_at_iso, delivery_state, attempt_count,
        next_attempt_at_iso, last_error_code, scope_store_code, scope_device_code
      ) VALUES (?, ?, ?, ?, ?, ?, NULL, 'pending', 0, ?, NULL, ?, ?)`,
      [
        event.eventId,
        event.eventType,
        event.occurredAtIso,
        event.orderGuid,
        event.correlationId,
        JSON.stringify(event.payload),
        nextAttemptAtIso,
        frozenScope.storeCode,
        frozenScope.deviceCode,
      ],
    );
  } catch (error) {
    if (!isLegacyAuditEventsSchema(error)) throw error;

    // 仅供 M26 前数据库逐级升级时的旧挂单回归；现行库必定走上方冻结 scope 的写入。
    await transaction.run(
      `INSERT INTO audit_events (
        event_id, event_type, occurred_at_iso, order_guid, correlation_id,
        payload_json, uploaded_at_iso
      ) VALUES (?, ?, ?, ?, ?, ?, NULL)`,
      [
        event.eventId,
        event.eventType,
        event.occurredAtIso,
        event.orderGuid,
        event.correlationId,
        JSON.stringify(event.payload),
      ],
    );
  }
}

function isLegacyAuditEventsSchema(error: unknown): boolean {
  return error instanceof Error
    && /audit_events has no column named (delivery_state|scope_store_code)/i.test(error.message);
}

async function allocateLocalSequence(transaction: SqliteConnectionPort): Promise<number> {
  await transaction.run(
    `INSERT INTO app_settings (setting_key, setting_value, updated_at_iso)
     VALUES ('local_sequence', '0', '1970-01-01T00:00:00.000Z')
     ON CONFLICT(setting_key) DO NOTHING`,
  );
  const row = await transaction.getFirst<{ value: unknown }>(
    `UPDATE app_settings
     SET setting_value = CAST(setting_value AS INTEGER) + 1
     WHERE setting_key = 'local_sequence'
     RETURNING setting_value AS value`,
  );
  const sequence = nonNegativeSafeInteger(row?.value, "held order local sequence");
  if (sequence === 0) throw new Error("Invalid held order local sequence.");
  return sequence;
}

function listLimit(value: number): number {
  if (!Number.isSafeInteger(value) || value <= 0 || value > 500) {
    throw new Error("Invalid held order list limit.");
  }
  return value;
}

function readLineSyncProvenance(
  row: Readonly<Record<string, unknown>>,
): LineSyncProvenance | undefined {
  const referenceCode = row.reference_code;
  const priceSource = row.sync_price_source;
  if (
    (referenceCode === null || referenceCode === undefined) &&
    (priceSource === null || priceSource === undefined)
  ) {
    return undefined;
  }
  if (priceSource === null || priceSource === undefined) {
    throw new Error("Invalid persisted line sync provenance.");
  }
  try {
    return normalizeLineSyncProvenance({
      referenceCode:
        referenceCode === null ? null : text(referenceCode),
      priceSource: int(priceSource),
    });
  } catch {
    throw new Error("Invalid persisted line sync provenance.");
  }
}

function text(value: unknown): string { if (typeof value !== "string") throw new Error("Invalid database text."); return value; } function nullable(value: unknown): string | null { return value === null || value === undefined ? null : text(value); } function int(value: unknown): number { const n=Number(value); if (!Number.isSafeInteger(n)) throw new Error("Invalid database integer."); return n; } function money(value: unknown) { return MoneySchema.parse(createAud(int(value))); } function json(raw: unknown): Readonly<Record<string, unknown>> { try { const parsed=JSON.parse(text(raw)); if (!parsed || typeof parsed!=="object" || Array.isArray(parsed)) throw new Error(); return parsed as Readonly<Record<string,unknown>>; } catch { throw new Error("Invalid database JSON."); } } function bytesOrNull(value: unknown, error: string): Uint8Array | null { if (value === null || value === undefined) return null; if (!(value instanceof Uint8Array)) throw new Error(error); return value; } function responseCodeOrNull(value: string | null | undefined): string | null { if (value === null || value === undefined) return null; if (!/^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$/.test(value)) throw new Error("Payment response code must be a short safe code."); return value; } function orderState(value: unknown): LocalOrder["state"] { const state=text(value); if (!["Draft","Completing","CompletedLocal","PendingSync","Syncing","Synced","Blocked403","Rejected"].includes(state)) throw new Error("Invalid order state."); return state as LocalOrder["state"]; } function parseCart(raw:string): CartSnapshot { const value=json(raw); if (!Number.isSafeInteger(value.revision) || !Array.isArray(value.lines) || typeof value.mode!=="string") throw new Error("Invalid held cart JSON."); return value as unknown as CartSnapshot; }

function readAuditEvent(row: AuditRow): AuditEventDraft {
  const auditScope = readPersistedAuditScope(row);
  return {
    eventId: text(row.event_id),
    eventType: text(row.event_type),
    occurredAtIso: text(row.occurred_at_iso),
    orderGuid: nullable(row.order_guid) ?? nullable(row.external_order_guid),
    correlationId: text(row.correlation_id),
    payload: json(row.payload_json),
    ...(auditScope ? { auditScope } : {}),
  };
}

async function readOrderAuditScope(
  database: SqliteConnectionPort,
  orderGuid: string,
): Promise<AuditScope> {
  const row = await database.getFirst<{
    store_code: unknown;
    device_code: unknown;
  }>(
    `SELECT store_code, device_code
     FROM local_orders
     WHERE order_guid = ?`,
    [orderGuid],
  );
  if (!row) throw new Error("AUDIT_ORDER_SCOPE_UNPROVEN");
  return freezeAuditScope({
    storeCode: text(row.store_code),
    deviceCode: text(row.device_code),
  });
}

function readPersistedAuditScope(row: AuditRow): AuditScope | null {
  const storeCode = nullable(row.scope_store_code);
  const deviceCode = nullable(row.scope_device_code);
  if (storeCode === null && deviceCode === null) return null;
  if (storeCode === null || deviceCode === null) {
    throw new Error("Invalid persisted audit scope.");
  }
  return freezeAuditScope({ storeCode, deviceCode });
}

function requiredAuditScope(scope: AuditScope | null): AuditScope {
  if (!scope) {
    // 没有组合根注入的 scope 时不能猜测本机或当前登录员工，必须停止写入/投递。
    throw new Error("AUDIT_SCOPE_REQUIRED");
  }
  return scope;
}

function assertSafe(value: unknown): void {
  if (!value || typeof value !== "object") return;
  for (const [key, item] of Object.entries(value as Record<string, unknown>)) {
    // authorizationMode 只描述授权方式，不是 token；宽泛匹配会让合法的主管审计永远无法落库。
    const normalized = key.replaceAll(/[_-]/gu, "").toLowerCase();
    if (
      normalized !== "authorizationmode" &&
      /authorization|token|card|pan|cvv|voucher|secret|payment|session|txn|rfn|reference/i.test(key)
    ) {
      throw new Error("Sensitive audit payload key is not allowed.");
    }
    assertSafe(item);
  }
}

import {
  auditActorPayload,
  auditActorSnapshotFromPayload,
  createAud,
  type AuditActorSnapshot,
  type AuditEventDraft,
  type CashFulfilmentDraft,
  type LocalOrder,
  type Money,
  type OrderTender,
  type OutboxMessageDraft,
  type RecalledHoldCompletion,
} from "../contracts";

import { SqliteMixedPaymentOrderTruthStore } from "./sqlite-mixed-payment-order-truth-store";
import {
  persistRecalledHoldOrderSourceAndClaim,
  type SensitivePayloadEncryptor,
} from "./sqlite-repositories";
import type { SqliteConnectionPort } from "@hb/pos-db/core/db/types";

import type {
  MixedCashTenderCommand,
  MixedCashTenderMutation,
  MixedCashTenderPort,
  MixedPaymentOrderTruth,
  MixedTenderReversalCommand,
  MixedTenderReversalMutation,
  MixedTenderReversalPort,
} from "@/features/payments/mixed";

export type MixedPaymentPersistenceIds = Readonly<{
  createTenderGuid(): string;
  createAuditEventId(): string;
}>;

export type MixedCashOrderCompletionPlan = Readonly<{
  completionAuditEvents: readonly AuditEventDraft[];
  outbox: OutboxMessageDraft;
  fulfilment: CashFulfilmentDraft;
}>;

export interface MixedCashOrderCompletionPlannerPort {
  planFinalCash(input: Readonly<{
    actionId: string;
    orderGuid: string;
    amount: Money;
    expectedRemaining: Money;
    actor: AuditActorSnapshot;
  }>): Promise<MixedCashOrderCompletionPlan>;
}

export type MixedCashFinalCompletionDependencies = Readonly<{
  planner: MixedCashOrderCompletionPlannerPort;
  encryptor: SensitivePayloadEncryptor;
  recallCompletion?: Readonly<{
    resolve(
      orderGuid: string,
      actor: AuditActorSnapshot,
    ): RecalledHoldCompletion | null | Promise<RecalledHoldCompletion | null>;
  }>;
}>;

type PreparedFinalCash = Readonly<{
  plan: MixedCashOrderCompletionPlan;
  receiptCiphertext: Uint8Array | null;
  recalledHoldCompletion: RecalledHoldCompletion | null;
}>;

type CashActionRow = Readonly<{
  order_guid: unknown;
  amount_cents: unknown;
  tendered_cents: unknown;
  change_cents: unknown;
  tender_guid: unknown;
  audit_event_id: unknown;
}>;

type OrderRow = Readonly<{
  order_guid: unknown;
  state: unknown;
  actual_amount_cents: unknown;
}>;

type TenderRow = Readonly<{
  tender_guid: unknown;
  order_guid: unknown;
  method: unknown;
  amount_cents: unknown;
}>;

type ReversalLinkRow = Readonly<{
  action_id: unknown;
  source_tender_guid: unknown;
  reversal_tender_guid: unknown;
}>;

/**
 * Mixed payment 的现金变更仅追加账本事实。部分现金只推进到 Completing；
 * 最后一笔现金必须先由 planner 生成完整完成计划，再与订单、outbox 和履约原子提交。
 */
export class SqliteMixedPaymentTenderStore
implements MixedCashTenderPort, MixedTenderReversalPort {
  public constructor(
    private readonly connection: SqliteConnectionPort,
    private readonly ids: MixedPaymentPersistenceIds,
    private readonly nowIso: () => string,
    private readonly finalCash?: MixedCashFinalCompletionDependencies,
  ) {}

  public async appendCashTenderAtomically(
    command: MixedCashTenderCommand,
  ): Promise<MixedCashTenderMutation> {
    const actionId = strictId(command.actionId, "cash action id");
    const orderGuid = strictId(command.orderGuid, "cash order guid");
    const actor = normalizedActor(command.actor);
    const amountCents = positiveAud(command.amount);
    const tenderedCents = command.tenderedAmount
      ? nonNegativeAud(command.tenderedAmount)
      : amountCents;
    const changeCents = command.change
      ? nonNegativeAud(command.change)
      : tenderedCents - amountCents;
    const observed = await new SqliteMixedPaymentOrderTruthStore(
      this.connection,
    ).getPaymentTruth(orderGuid);
    if (!observed) {
      throw new Error("Persisted mixed payment order was not found.");
    }
    const observedRemaining =
      observed.actualAmount.cents - tenderTotal(observed.tenders);
    let finalCompletion: PreparedFinalCash | null = null;
    if (
      (observed.state === "Draft" || observed.state === "Completing") &&
      observedRemaining === amountCents
    ) {
      if (!this.finalCash) {
        throw new Error(
          "Final mixed cash requires a durable completion planner.",
        );
      }
      assertCashSettlement(amountCents, tenderedCents, changeCents, true);
      const plan = await this.finalCash.planner.planFinalCash({
        actionId,
        orderGuid,
        amount: createAud(amountCents),
        expectedRemaining: createAud(observedRemaining),
        actor,
      });
      validateCompletionPlan(plan, orderGuid);
      const receiptCiphertext = plan.fulfilment.print
        ? await encryptReceipt(
          this.finalCash.encryptor,
          plan.fulfilment.print.receiptBytes,
        )
        : null;
      const recalledHoldCompletion =
        await this.finalCash.recallCompletion?.resolve(orderGuid, actor) ??
        null;
      finalCompletion = {
        plan,
        receiptCiphertext,
        recalledHoldCompletion,
      };
    }

    return this.connection.withExclusiveTransaction(async (transaction) => {
      const replay = await transaction.getFirst<CashActionRow>(
        `SELECT order_guid, amount_cents, tendered_cents,
           change_cents, tender_guid, audit_event_id
         FROM mixed_cash_tender_actions
         WHERE order_guid = ? AND action_id = ?`,
        [orderGuid, actionId],
      );
      if (replay) {
        const replayOrderGuid = text(replay.order_guid, "cash action order");
        const replayAmountCents = integer(
          replay.amount_cents,
          "cash action amount",
        );
        const legacySettlement =
          replay.tendered_cents === null &&
          replay.change_cents === null;
        if (
          !legacySettlement &&
          (replay.tendered_cents === null ||
            replay.change_cents === null)
        ) {
          throw new Error(
            "Persisted mixed cash settlement is incomplete.",
          );
        }
        const replayTenderedCents = legacySettlement
          ? replayAmountCents
          : integer(
              replay.tendered_cents,
              "cash action tendered amount",
            );
        const replayChangeCents = legacySettlement
          ? 0
          : integer(replay.change_cents, "cash action change");
        if (
          replayOrderGuid !== orderGuid ||
          replayAmountCents !== amountCents ||
          replayTenderedCents !== tenderedCents ||
          replayChangeCents !== changeCents ||
          !isPersistedCashSettlement(
            replayAmountCents,
            replayTenderedCents,
            replayChangeCents,
          )
        ) {
          throw new Error(
            "Mixed cash action was replayed with different immutable content.",
          );
        }
        const tenderGuid = text(
          replay.tender_guid,
          "cash action tender guid",
        );
        await assertTender(
          transaction,
          orderGuid,
          tenderGuid,
          "cash",
          amountCents,
        );
        await assertPersistedAuditActor(
          transaction,
          text(replay.audit_event_id, "cash action audit event"),
          actor,
        );
        return {
          replayed: true,
          tenderGuid,
          truth: await requireTruth(transaction, orderGuid),
          cashSettlement: persistedCashSettlement(
            replayAmountCents,
            replayTenderedCents,
            replayChangeCents,
          ),
        };
      }

      const before = await requireTruth(transaction, orderGuid);
      if (before.state !== "Draft" && before.state !== "Completing") {
        throw new Error(
          "Mixed cash tender requires a Draft or Completing order.",
        );
      }
      if (before.actualAmount.cents <= 0) {
        throw new Error("Mixed cash tender requires a positive sale order.");
      }
      const paidCents = tenderTotal(before.tenders);
      const remainingCents = before.actualAmount.cents - paidCents;
      if (remainingCents <= 0 || amountCents > remainingCents) {
        throw new Error(
          "Mixed cash tender exceeds the persisted remaining balance.",
        );
      }
      const isFinal = amountCents === remainingCents;
      // 事务内以当前 remaining 重验，避免并发状态下把部分现金伪造成最终舍入。
      assertCashSettlement(
        amountCents,
        tenderedCents,
        changeCents,
        isFinal,
      );
      if (isFinal !== (finalCompletion !== null)) {
        throw new Error(
          "Mixed cash balance changed after final completion planning.",
        );
      }

      const tenderGuid = generatedId(
        this.ids.createTenderGuid(),
        "cash tender guid",
      );
      const auditEventId = generatedId(
        this.ids.createAuditEventId(),
        "cash audit event id",
      );
      const now = canonicalIso(this.nowIso(), "cash action time");

      const nextState = isFinal ? "PendingSync" : "Completing";
      if (before.state !== nextState) {
        const transitioned = await transaction.run(
          `UPDATE local_orders
           SET state = ?, updated_at_iso = ?
           WHERE order_guid = ? AND state = ?`,
          [nextState, now, orderGuid, before.state],
        );
        if (transitioned.changes !== 1) {
          throw new Error(
            "Mixed cash order state changed before tender append.",
          );
        }
      }
      await transaction.run(
        `INSERT INTO order_tenders (
          tender_guid, order_guid, method, amount_cents,
          payment_attempt_id, created_at_iso
        ) VALUES (?, ?, 'cash', ?, NULL, ?)`,
        [tenderGuid, orderGuid, amountCents, now],
      );
      await transaction.run(
        `INSERT INTO audit_events (
          event_id, event_type, occurred_at_iso, order_guid,
          correlation_id, payload_json, uploaded_at_iso
        ) VALUES (?, 'MIXED_CASH_TENDER_APPENDED', ?, ?, ?, ?, NULL)`,
        [
          auditEventId,
          now,
          orderGuid,
          actionId,
          JSON.stringify({
            action: "mixed-cash-tender-appended",
            appliedCents: amountCents,
            tenderedCents,
            changeCents,
            tenderGuid,
            ...auditActorPayload({
              cashierId: actor.cashierId,
              cashierName: actor.cashierName,
              userGuid: actor.userGuid,
            }),
          }),
        ],
      );
      await transaction.run(
        `INSERT INTO mixed_cash_tender_actions (
          action_id, order_guid, amount_cents, tendered_cents,
          change_cents, tender_guid,
          audit_event_id, created_at_iso
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
        [
          actionId,
          orderGuid,
          amountCents,
          tenderedCents,
          changeCents,
          tenderGuid,
          auditEventId,
          now,
        ],
      );
      if (finalCompletion) {
        await persistFinalCashCompletion(
          transaction,
          finalCompletion,
          orderGuid,
          now,
        );
        await completeRecalledHoldInMixedCashTransaction(
          transaction,
          orderGuid,
          finalCompletion.recalledHoldCompletion,
        );
      }

      return {
        replayed: false,
        tenderGuid,
        truth: await requireTruth(transaction, orderGuid),
        cashSettlement: persistedCashSettlement(
          amountCents,
          tenderedCents,
          changeCents,
        ),
      };
    });
  }

  public reverseTender(
    command: MixedTenderReversalCommand,
  ): Promise<MixedTenderReversalMutation> {
    const actionId = strictId(command.actionId, "reversal action id");
    const orderGuid = strictId(command.orderGuid, "reversal order guid");
    const sourceTenderGuid = strictId(
      command.tenderGuid,
      "source tender guid",
    );
    const actor = normalizedActor(command.actor);

    return this.connection.withExclusiveTransaction(async (transaction) => {
      const actionReplay = await transaction.getFirst<ReversalLinkRow>(
        `SELECT action_id, source_tender_guid, reversal_tender_guid
         FROM payment_tender_reversal_links
         WHERE order_guid = ? AND action_id = ?`,
        [orderGuid, actionId],
      );
      if (actionReplay) {
        const replaySource = text(
          actionReplay.source_tender_guid,
          "reversal source",
        );
        if (replaySource !== sourceTenderGuid) {
          throw new Error(
            "Tender reversal action was replayed with a different source.",
          );
        }
        const reversalTenderGuid = text(
          actionReplay.reversal_tender_guid,
          "reversal tender",
        );
        const truth = await requireTruth(transaction, orderGuid);
        assertReversalTruth(
          truth,
          actionId,
          sourceTenderGuid,
          reversalTenderGuid,
        );
        await assertPersistedCorrelationActor(
          transaction,
          orderGuid,
          actionId,
          actor,
        );
        return {
          state: "reversed",
          replayed: true,
          reversalTenderGuid,
          truth,
        };
      }

      const truth = await requireTruth(transaction, orderGuid);
      if (truth.state !== "Completing") {
        throw new Error(
          "Tender reversal requires an order in Completing state.",
        );
      }
      const source = truth.tenders.find(
        (tender) => tender.tenderGuid === sourceTenderGuid,
      );
      if (!source || source.amount.cents <= 0) {
        throw new Error("Tender source is missing or is already a reversal.");
      }
      const prior = truth.reversalLinks.find(
        (link) => link.sourceTenderGuid === sourceTenderGuid,
      );
      if (prior) {
        throw new Error("Tender source already has an immutable reversal.");
      }

      // 卡和券必须先由 provider 完成撤销/退款并恢复其 Unknown 结果；数据库
      // 不会在没有外部证明时伪造一笔本地 reversal。
      if (source.method !== "cash") {
        return {
          state: "pending",
          replayed: false,
          reversalTenderGuid: null,
          truth,
        };
      }

      const reversalTenderGuid = generatedId(
        this.ids.createTenderGuid(),
        "reversal tender guid",
      );
      const auditEventId = generatedId(
        this.ids.createAuditEventId(),
        "reversal audit event id",
      );
      const now = canonicalIso(this.nowIso(), "reversal time");
      await transaction.run(
        `INSERT INTO order_tenders (
          tender_guid, order_guid, method, amount_cents,
          payment_attempt_id, created_at_iso
        ) VALUES (?, ?, 'cash', ?, NULL, ?)`,
        [
          reversalTenderGuid,
          orderGuid,
          -source.amount.cents,
          now,
        ],
      );
      await transaction.run(
        `INSERT INTO payment_tender_reversal_links (
          order_guid, action_id, source_tender_guid,
          reversal_tender_guid, created_at_iso
        ) VALUES (?, ?, ?, ?, ?)`,
        [
          orderGuid,
          actionId,
          sourceTenderGuid,
          reversalTenderGuid,
          now,
        ],
      );
      await transaction.run(
        `INSERT INTO audit_events (
          event_id, event_type, occurred_at_iso, order_guid,
          correlation_id, payload_json, uploaded_at_iso
        ) VALUES (?, 'MIXED_CASH_TENDER_REVERSED', ?, ?, ?, ?, NULL)`,
        [
          auditEventId,
          now,
          orderGuid,
          actionId,
          JSON.stringify({
            action: "mixed-cash-tender-reversed",
            amountCents: -source.amount.cents,
            reversalTenderGuid,
            sourceTenderGuid,
            ...auditActorPayload({
              cashierId: actor.cashierId,
              cashierName: actor.cashierName,
              userGuid: actor.userGuid,
            }),
          }),
        ],
      );
      const after = await requireTruth(transaction, orderGuid);
      assertReversalTruth(
        after,
        actionId,
        sourceTenderGuid,
        reversalTenderGuid,
      );
      return {
        state: "reversed",
        replayed: false,
        reversalTenderGuid,
        truth: after,
      };
    });
  }
}

async function requireTruth(
  transaction: SqliteConnectionPort,
  orderGuid: string,
): Promise<MixedPaymentOrderTruth> {
  const order = await transaction.getFirst<OrderRow>(
    `SELECT order_guid, state, actual_amount_cents
     FROM local_orders
     WHERE order_guid = ?`,
    [orderGuid],
  );
  if (!order) throw new Error("Persisted mixed payment order was not found.");
  const tenders = await transaction.getAll<TenderRow>(
    `SELECT tender_guid, order_guid, method, amount_cents
     FROM order_tenders
     WHERE order_guid = ?
     ORDER BY created_at_iso ASC, tender_guid ASC`,
    [orderGuid],
  );
  const links = await transaction.getAll<ReversalLinkRow>(
    `SELECT action_id, source_tender_guid, reversal_tender_guid
     FROM payment_tender_reversal_links
     WHERE order_guid = ?
     ORDER BY created_at_iso ASC, action_id ASC`,
    [orderGuid],
  );
  return {
    orderGuid: text(order.order_guid, "order guid"),
    state: orderState(order.state),
    actualAmount: createAud(
      integer(order.actual_amount_cents, "order actual amount"),
    ),
    tenders: tenders.map((row) => ({
      tenderGuid: text(row.tender_guid, "tender guid"),
      method: tenderMethod(row.method),
      amount: createAud(integer(row.amount_cents, "tender amount")),
      reference: null,
      reservationToken: null,
    })),
    reversalLinks: links.map((row) => ({
      actionId: text(row.action_id, "reversal action id"),
      sourceTenderGuid: text(
        row.source_tender_guid,
        "reversal source tender guid",
      ),
      reversalTenderGuid: text(
        row.reversal_tender_guid,
        "reversal tender guid",
      ),
    })),
  };
}

async function assertTender(
  transaction: SqliteConnectionPort,
  orderGuid: string,
  tenderGuid: string,
  method: OrderTender["method"],
  amountCents: number,
): Promise<void> {
  const row = await transaction.getFirst<TenderRow>(
    `SELECT tender_guid, order_guid, method, amount_cents
     FROM order_tenders
     WHERE tender_guid = ?`,
    [tenderGuid],
  );
  if (
    !row ||
    text(row.order_guid, "tender order guid") !== orderGuid ||
    tenderMethod(row.method) !== method ||
    integer(row.amount_cents, "tender amount") !== amountCents
  ) {
    throw new Error("Mixed payment action points to an invalid tender.");
  }
}

function validateCompletionPlan(
  plan: MixedCashOrderCompletionPlan,
  orderGuid: string,
): void {
  if (!plan || typeof plan !== "object") {
    throw new TypeError("Final mixed cash completion plan is required.");
  }
  if (
    plan.outbox.kind !== "order-sync" ||
    plan.outbox.aggregateId !== orderGuid ||
    !strictId(plan.outbox.messageId, "completion outbox message id") ||
    !canonicalIso(
      plan.outbox.nextAttemptAtIso,
      "completion outbox next attempt",
    )
  ) {
    throw new TypeError("Final mixed cash outbox is invalid.");
  }
  assertSafeJson(plan.outbox.payloadJson, "completion outbox");
  const auditIds = new Set<string>();
  for (const event of plan.completionAuditEvents) {
    const eventId = strictId(event.eventId, "completion audit event id");
    if (auditIds.has(eventId)) {
      throw new TypeError("Final mixed cash audit event ids must be unique.");
    }
    auditIds.add(eventId);
    if (
      event.orderGuid !== orderGuid ||
      !strictId(event.eventType, "completion audit type") ||
      !strictId(event.correlationId, "completion audit correlation id")
    ) {
      throw new TypeError("Final mixed cash audit identity is invalid.");
    }
    canonicalIso(event.occurredAtIso, "completion audit time");
    assertSafeObject(event.payload, "completion audit payload");
  }

  const { print, drawer } = plan.fulfilment;
  if (
    print &&
    (print.orderGuid !== orderGuid ||
      !strictId(print.jobId, "completion print job id") ||
      !strictId(print.printerId, "completion printer id") ||
      print.receiptBytes.length === 0 ||
      print.receiptBytes.length > 1_048_576 ||
      print.isReprint)
  ) {
    throw new TypeError("Final mixed cash print fulfilment is invalid.");
  }
  if (
    drawer &&
    (drawer.orderGuid !== orderGuid ||
      !strictId(drawer.eventId, "completion drawer event id") ||
      !strictId(drawer.printerId, "completion drawer printer id") ||
      !strictId(drawer.reason, "completion drawer reason") ||
      drawer.printJobId !== (print?.jobId ?? null) ||
      (print !== null && drawer.printerId !== print.printerId))
  ) {
    throw new TypeError("Final mixed cash drawer fulfilment is invalid.");
  }
}

async function encryptReceipt(
  encryptor: SensitivePayloadEncryptor,
  receiptBytes: Uint8Array,
): Promise<Uint8Array> {
  const ciphertext = await encryptor.encrypt(
    JSON.stringify(Array.from(receiptBytes)),
  );
  if (!(ciphertext instanceof Uint8Array) || ciphertext.length === 0) {
    throw new Error("Final mixed cash receipt encryption failed.");
  }
  return ciphertext;
}

async function completeRecalledHoldInMixedCashTransaction(
  transaction: SqliteConnectionPort,
  orderGuidInput: string,
  completion: RecalledHoldCompletion | null,
): Promise<void> {
  const orderGuid = strictId(orderGuidInput, "mixed cash order guid");
  const order = await transaction.getFirst<{
    store_code: unknown;
    device_code: unknown;
  }>(
    `SELECT store_code, device_code
     FROM local_orders WHERE order_guid = ?`,
    [orderGuid],
  );
  if (!order) throw new Error("Mixed cash completion order is missing.");
  const storeCode = strictId(
    text(order.store_code, "mixed cash store"),
    "mixed cash store",
  );
  const deviceCode = strictId(
    text(order.device_code, "mixed cash device"),
    "mixed cash device",
  );
  const fence = await transaction.getFirst<{
    kind: unknown;
    hold_id: unknown;
    recall_attempt_id: unknown;
    bound_order_guid: unknown;
    held_status: unknown;
    held_recall_attempt_id: unknown;
  }>(
    `SELECT fence.kind, fence.hold_id, fence.recall_attempt_id,
      fence.bound_order_guid, held.status AS held_status,
      held.recall_attempt_id AS held_recall_attempt_id
     FROM terminal_cart_fences fence
     INNER JOIN held_order_records held ON held.hold_id = fence.hold_id
     WHERE fence.store_code = ? AND fence.device_code = ?`,
    [storeCode, deviceCode],
  );
  if (completion === null) {
    if (fence) {
      throw new Error(
        "Ordinary mixed cash completion is blocked by an active terminal cart fence.",
      );
    }
    return;
  }
  if (!fence || completion.binding.kind !== "recalled") {
    throw new Error(
      "Recalled mixed cash completion has no active recall fence.",
    );
  }
  const binding = completion.binding;
  const holdId = strictId(binding.holdId, "mixed cash recall hold id");
  const recallAttemptId = strictId(
    binding.recallAttemptId,
    "mixed cash recall attempt id",
  );
  if (
    binding.scope.storeCode !== storeCode ||
    binding.scope.deviceCode !== deviceCode ||
    text(fence.kind, "mixed cash recall fence kind") !== "RecallActive" ||
    text(fence.hold_id, "mixed cash recall fence hold") !== holdId ||
    text(fence.recall_attempt_id, "mixed cash recall fence attempt") !==
      recallAttemptId ||
    fence.bound_order_guid !== null ||
    text(fence.held_status, "mixed cash held status") !== "Recalling" ||
    text(fence.held_recall_attempt_id, "mixed cash held attempt") !==
      recallAttemptId
  ) {
    throw new Error(
      "Recalled mixed cash completion does not match the active recall fence.",
    );
  }
  const recalledAtIso = canonicalIso(
    completion.recalledAtIso,
    "mixed cash recall completion time",
  );
  const audit = completion.recallAudit;
  if (
    audit.eventType !== "ORDER_RECALL" ||
    audit.orderGuid !== orderGuid ||
    audit.correlationId !== holdId ||
    canonicalIso(audit.occurredAtIso, "mixed cash recall audit time") !==
      recalledAtIso
  ) {
    throw new Error("Mixed cash recall completion audit is invalid.");
  }
  assertSafeObject(audit.payload, "Mixed cash recall audit payload");
  const bound = await transaction.run(
    `UPDATE terminal_cart_fences SET bound_order_guid = ?
     WHERE store_code = ? AND device_code = ?
       AND kind = 'RecallActive' AND hold_id = ?
       AND recall_attempt_id = ? AND bound_order_guid IS NULL`,
    [orderGuid, storeCode, deviceCode, holdId, recallAttemptId],
  );
  if (bound.changes !== 1) {
    throw new Error("Recall fence changed before mixed cash completion.");
  }
  // 绑定后同一事务写不可变订单来源；RemoteClaim 且已 Active 的本地 claim 绑定并 Completed。
  await persistRecalledHoldOrderSourceAndClaim(transaction, {
    orderGuid,
    holdId,
    recallAttemptId,
    recalledAtIso,
  });
  const changed = await transaction.run(
    `UPDATE held_order_records
     SET status = 'Recalled', recalled_at_iso = ?, updated_at_iso = ?
     WHERE hold_id = ? AND store_code = ? AND device_code = ?
       AND recall_attempt_id = ? AND status = 'Recalling'`,
    [
      recalledAtIso,
      recalledAtIso,
      holdId,
      storeCode,
      deviceCode,
      recallAttemptId,
    ],
  );
  if (changed.changes !== 1) {
    throw new Error("Recalled hold changed before mixed cash completion.");
  }
  await transaction.run(
    `INSERT INTO audit_events (
      event_id, event_type, occurred_at_iso, order_guid,
      correlation_id, payload_json, uploaded_at_iso,
      scope_store_code, scope_device_code
    ) VALUES (?, 'ORDER_RECALL', ?, ?, ?, ?, NULL, ?, ?)`,
    [
      strictId(audit.eventId, "mixed cash recall audit id"),
      recalledAtIso,
      orderGuid,
      holdId,
      JSON.stringify(audit.payload),
      storeCode,
      deviceCode,
    ],
  );
  const deleted = await transaction.run(
    `DELETE FROM terminal_cart_fences
     WHERE store_code = ? AND device_code = ?
       AND kind = 'RecallActive' AND hold_id = ?
       AND recall_attempt_id = ? AND bound_order_guid = ?`,
    [storeCode, deviceCode, holdId, recallAttemptId, orderGuid],
  );
  if (deleted.changes !== 1) {
    throw new Error("Recall fence changed before mixed cash completion.");
  }
}

async function persistFinalCashCompletion(
  transaction: SqliteConnectionPort,
  prepared: PreparedFinalCash,
  orderGuid: string,
  now: string,
): Promise<void> {
  const { plan, receiptCiphertext } = prepared;
  for (const event of plan.completionAuditEvents) {
    await transaction.run(
      `INSERT INTO audit_events (
        event_id, event_type, occurred_at_iso, order_guid,
        correlation_id, payload_json, uploaded_at_iso
      ) VALUES (?, ?, ?, ?, ?, ?, NULL)`,
      [
        event.eventId,
        event.eventType,
        event.occurredAtIso,
        orderGuid,
        event.correlationId,
        JSON.stringify(event.payload),
      ],
    );
  }
  await transaction.run(
    `INSERT INTO outbox_messages (
      message_id, aggregate_id, kind, payload_json, state, attempt_count,
      next_attempt_at_iso, lease_id, lease_expires_at_iso, last_error_code,
      created_at_iso, updated_at_iso
    ) VALUES (?, ?, 'order-sync', ?, 'pending', 0, ?, NULL, NULL, NULL, ?, ?)`,
    [
      plan.outbox.messageId,
      orderGuid,
      plan.outbox.payloadJson,
      plan.outbox.nextAttemptAtIso,
      now,
      now,
    ],
  );
  if (plan.fulfilment.print && receiptCiphertext) {
    await transaction.run(
      `INSERT INTO print_jobs (
        job_id, order_guid, state, printer_id, receipt_ciphertext,
        is_reprint, retry_count, last_error_code, created_at_iso, updated_at_iso
      ) VALUES (?, ?, 'Queued', ?, ?, 0, 0, NULL, ?, ?)`,
      [
        plan.fulfilment.print.jobId,
        orderGuid,
        plan.fulfilment.print.printerId,
        receiptCiphertext,
        now,
        now,
      ],
    );
  }
  if (plan.fulfilment.drawer) {
    await transaction.run(
      `INSERT INTO drawer_events (
        event_id, order_guid, printer_id, print_job_id, state, reason,
        retry_count, requested_at_iso, completed_at_iso, last_error_code,
        created_at_iso, updated_at_iso
      ) VALUES (?, ?, ?, ?, 'Required', ?, 0, NULL, NULL, NULL, ?, ?)`,
      [
        plan.fulfilment.drawer.eventId,
        orderGuid,
        plan.fulfilment.drawer.printerId,
        plan.fulfilment.drawer.printJobId,
        plan.fulfilment.drawer.reason,
        now,
        now,
      ],
    );
  }
}

function assertReversalTruth(
  truth: MixedPaymentOrderTruth,
  actionId: string,
  sourceTenderGuid: string,
  reversalTenderGuid: string,
): void {
  const source = truth.tenders.find(
    (tender) => tender.tenderGuid === sourceTenderGuid,
  );
  const reversal = truth.tenders.find(
    (tender) => tender.tenderGuid === reversalTenderGuid,
  );
  const link = truth.reversalLinks.find(
    (candidate) => candidate.actionId === actionId,
  );
  if (
    !source ||
    source.amount.cents <= 0 ||
    !reversal ||
    reversal.method !== source.method ||
    reversal.amount.cents !== -source.amount.cents ||
    link?.sourceTenderGuid !== sourceTenderGuid ||
    link.reversalTenderGuid !== reversalTenderGuid
  ) {
    throw new Error("Persisted tender reversal truth is inconsistent.");
  }
}

function assertSafeJson(value: string, label: string): void {
  let parsed: unknown;
  try {
    parsed = JSON.parse(value);
  } catch {
    throw new TypeError(`${label} must be valid JSON.`);
  }
  assertSafeObject(parsed, label);
}

function assertSafeObject(value: unknown, label: string): void {
  const serialized = JSON.stringify(value);
  if (serialized.length > 1_048_576) {
    throw new TypeError(`${label} is too large.`);
  }
  // 只按字段名拦截受保护支付数据。旧实现对整段 JSON（含值）做子串匹配，
  // 收银员姓名等普通值（如 "Sean Pan" 命中 pan\b）会被误判，导致最后一笔现金
  // 无法提交、订单进入恢复中心（MIXED_CASH_COMMIT_FAILED）。
  if (hasProtectedPaymentKey(value)) {
    throw new TypeError(`${label} contains protected payment data.`);
  }
}

const PROTECTED_PAYMENT_KEY =
  /authorization|reservationtoken|vouchercode|access[_-]?token|refresh[_-]?token|cardnumber|pan\b|hardware[_-]?id/i;

/** 递归检查对象 / 数组中是否出现受保护支付字段名；值本身不参与匹配。 */
function hasProtectedPaymentKey(value: unknown): boolean {
  if (Array.isArray(value)) return value.some(hasProtectedPaymentKey);
  if (!value || typeof value !== "object") return false;
  return Object.entries(value).some(
    ([key, child]) =>
      PROTECTED_PAYMENT_KEY.test(key) || hasProtectedPaymentKey(child),
  );
}

function normalizedActor(actor: AuditActorSnapshot): AuditActorSnapshot {
  const payload = auditActorPayload({
    cashierId: actor.cashierId,
    cashierName: actor.cashierName,
    userGuid: actor.userGuid,
  });
  const normalized = auditActorSnapshotFromPayload(payload);
  if (!normalized) {
    throw new TypeError("Payment audit actor is invalid.");
  }
  return normalized;
}

async function assertPersistedAuditActor(
  transaction: SqliteConnectionPort,
  eventId: string,
  expected: AuditActorSnapshot,
): Promise<void> {
  const row = await transaction.getFirst<{ payload_json: unknown }>(
    "SELECT payload_json FROM audit_events WHERE event_id = ?",
    [eventId],
  );
  assertPersistedActorPayload(row?.payload_json, expected);
}

async function assertPersistedCorrelationActor(
  transaction: SqliteConnectionPort,
  orderGuid: string,
  correlationId: string,
  expected: AuditActorSnapshot,
): Promise<void> {
  const rows = await transaction.getAll<{ payload_json: unknown }>(
    `SELECT payload_json
     FROM audit_events
     WHERE order_guid = ? AND correlation_id = ?
     ORDER BY occurred_at_iso, event_id
     LIMIT 2`,
    [orderGuid, correlationId],
  );
  if (rows.length !== 1) {
    throw new Error("Persisted payment audit actor is ambiguous.");
  }
  assertPersistedActorPayload(rows[0]?.payload_json, expected);
}

function assertPersistedActorPayload(
  payloadJson: unknown,
  expected: AuditActorSnapshot,
): void {
  if (typeof payloadJson !== "string") {
    throw new Error("Persisted payment audit actor is missing.");
  }
  let payload: unknown;
  try {
    payload = JSON.parse(payloadJson);
  } catch {
    throw new Error("Persisted payment audit actor is invalid.");
  }
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) {
    throw new Error("Persisted payment audit actor is invalid.");
  }
  const actual = auditActorSnapshotFromPayload(
    payload as Readonly<Record<string, unknown>>,
  );
  // 中文注释：M26 前历史 action 没有 actor；只允许整体缺失，禁止半新半旧身份。
  if (actual === null) {
    const record = payload as Readonly<Record<string, unknown>>;
    const hasAnyActorField =
      Object.hasOwn(record, "requestingCashierId") ||
      Object.hasOwn(record, "requestingCashierName") ||
      Object.hasOwn(record, "requestingUserGuid");
    if (!hasAnyActorField) return;
    throw new Error("Persisted payment audit actor is incomplete.");
  }
  if (
    actual.cashierId !== expected.cashierId ||
    actual.cashierName !== expected.cashierName ||
    actual.userGuid !== expected.userGuid
  ) {
    throw new Error(
      "Payment action was replayed with a different immutable actor.",
    );
  }
}

function tenderTotal(tenders: readonly OrderTender[]): number {
  let total = 0;
  for (const tender of tenders) {
    total += tender.amount.cents;
    if (!Number.isSafeInteger(total)) {
      throw new Error("Persisted tender total exceeds safe integer bounds.");
    }
  }
  return total;
}

function positiveAud(amount: MixedCashTenderCommand["amount"]): number {
  if (
    amount.currency !== "AUD" ||
    !Number.isSafeInteger(amount.cents) ||
    amount.cents <= 0
  ) {
    throw new TypeError("Mixed cash tender must be positive AUD cents.");
  }
  return amount.cents;
}

function nonNegativeAud(amount: Money): number {
  if (
    amount.currency !== "AUD" ||
    !Number.isSafeInteger(amount.cents) ||
    amount.cents < 0
  ) {
    throw new TypeError("Mixed cash change must be non-negative AUD cents.");
  }
  return amount.cents;
}

function assertCashSettlement(
  amountCents: number,
  tenderedCents: number,
  changeCents: number,
  isFinal: boolean,
): void {
  const cashDueCents = isFinal ? roundCashDueCents(amountCents) : amountCents;
  if (
    tenderedCents < cashDueCents ||
    // 最终现金须镜像运行时的五分规范化；部分现金保留原始精确实收。
    (isFinal && tenderedCents % 5 !== 0) ||
    changeCents !== tenderedCents - cashDueCents
  ) {
    throw new TypeError(
      "Mixed cash tendered, applied and change amounts are inconsistent.",
    );
  }
}

function isPersistedCashSettlement(
  amountCents: number,
  tenderedCents: number,
  changeCents: number,
): boolean {
  try {
    // M24 历史库先按旧的入账等于实收、找零为零（也是部分现金）规则回放。
    assertCashSettlement(amountCents, tenderedCents, changeCents, false);
    return true;
  } catch {
    try {
      assertCashSettlement(amountCents, tenderedCents, changeCents, true);
      return true;
    } catch {
      return false;
    }
  }
}

function roundCashDueCents(amountCents: number): number {
  const remainder = amountCents % 5;
  return amountCents - remainder + (remainder >= 3 ? 5 : 0);
}

function persistedCashSettlement(
  appliedCents: number,
  tenderedCents: number,
  changeCents: number,
): Readonly<{ tendered: Money; applied: Money; change: Money }> {
  return Object.freeze({
    tendered: createAud(tenderedCents),
    applied: createAud(appliedCents),
    change: createAud(changeCents),
  });
}

function orderState(value: unknown): LocalOrder["state"] {
  const state = text(value, "order state");
  if (
    state === "Draft" ||
    state === "Completing" ||
    state === "CompletedLocal" ||
    state === "PendingSync" ||
    state === "Syncing" ||
    state === "Synced" ||
    state === "Blocked403" ||
    state === "Rejected"
  ) {
    return state;
  }
  throw new Error("Persisted order state is invalid.");
}

function tenderMethod(value: unknown): OrderTender["method"] {
  const method = text(value, "tender method");
  if (method === "cash" || method === "card" || method === "voucher") {
    return method;
  }
  throw new Error("Persisted tender method is invalid.");
}

function generatedId(value: string, label: string): string {
  return strictId(value, label);
}

function strictId(value: string, label: string): string {
  const normalized = value.trim();
  if (
    !normalized ||
    normalized.length > 128 ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    throw new TypeError(`${label} is invalid.`);
  }
  return normalized;
}

function canonicalIso(value: string, label: string): string {
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed) || new Date(parsed).toISOString() !== value) {
    throw new TypeError(`${label} must be canonical ISO UTC.`);
  }
  return value;
}

function text(value: unknown, label: string): string {
  if (typeof value !== "string" || !value.trim()) {
    throw new Error(`Persisted ${label} is invalid.`);
  }
  return value;
}

function integer(value: unknown, label: string): number {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed)) {
    throw new Error(`Persisted ${label} is invalid.`);
  }
  return parsed;
}

import assert from "node:assert/strict";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import type { LocalOrder } from "@hb/pos-domain/core/contracts/order";

import {
  applyMigrations,
  POS_DATABASE_MIGRATIONS,
} from "./migrations";
import { ProtectedMaterialIntegrityError } from "@hb/pos-db/core/db/protected-material-integrity-error";
import {
  OrderSyncMaterialError,
  SqliteOrderSyncMaterialResolver,
} from "./sqlite-order-sync-material";
import {
  encryptPaymentProtectedMaterial,
  SqlitePaymentProtectedMaterialReader,
} from "@hb/pos-db/core/db/sqlite-payment-protected-material";
import { createSqliteRepositories } from "./sqlite-repositories";
import { SqliteReturnCapacityVault } from "./sqlite-return-capacity-vault";
import { SqliteVoucherProtectedTokenStore } from "./sqlite-voucher-protected-token-store";
import { SqliteVoucherTenderReversalStore } from "./sqlite-voucher-tender-reversal-store";
import type {
  SqliteConnectionPort,
  SqlRunResult,
  SqlValue,
} from "@hb/pos-db/core/db/types";

const NOW = "2026-07-28T00:00:00.000Z";
const VOUCHER_REVERSAL_ACTOR = Object.freeze({
  cashierId: "cashier-1",
  cashierName: "Cashier One",
  userGuid: "user-guid-1",
});
const EXPIRES = "2026-07-29T00:00:00.000Z";
const DEFAULT_LINE_SYNC_PROVENANCE = Object.freeze({
  referenceCode: null,
  priceSource: 0 as const,
});
const RICH_LINE_SYNC_PROVENANCE = Object.freeze({
  referenceCode: "REF-一",
  priceSource: 3 as const,
});

const encryptor = {
  async encrypt(plaintext: string): Promise<Uint8Array> {
    return Uint8Array.from(
      new TextEncoder().encode(plaintext),
      (value) => value ^ 0xa5,
    );
  },
  async decrypt(ciphertext: Uint8Array): Promise<string> {
    return new TextDecoder().decode(
      Uint8Array.from(ciphertext, (value) => value ^ 0xa5),
    );
  },
};

test("Square 与 Linkly purchase 只在受信任副本恢复 WPF 引用，普通仓储保持脱敏", async () => {
  await withDatabase(async (connection) => {
    await seedOrderTender(connection, {
      orderGuid: "order-square-purchase",
      tenderGuid: "tender-square-purchase",
      attemptId: "attempt-square-purchase",
      provider: "square",
      operation: "purchase",
      amountCents: 1_250,
      syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
      paymentId: "payment-1",
    });
    await seedOrderTender(connection, {
      orderGuid: "order-linkly-purchase",
      tenderGuid: "tender-linkly-purchase",
      attemptId: "attempt-linkly-purchase",
      provider: "linkly-cloud",
      operation: "purchase",
      amountCents: 2_500,
      syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
      sessionId: "session :/1",
      txnRef: "txn :/1",
      rfn: "RFN /1",
    });

    const ordinarySquare = await readOrdinaryOrder(
      connection,
      "order-square-purchase",
    );
    const ordinaryLinkly = await readOrdinaryOrder(
      connection,
      "order-linkly-purchase",
    );
    assert.equal(ordinarySquare.tenders[0]?.reference, "SQ:payment-1");
    assert.equal(ordinarySquare.tenders[0]?.reservationToken, null);
    assert.equal(ordinaryLinkly.tenders[0]?.reference, null);
    assert.equal(ordinaryLinkly.tenders[0]?.reservationToken, null);

    const resolver = createResolver(connection);
    const square = await resolver.resolve(ordinarySquare, null);
    const linkly = await resolver.resolve(ordinaryLinkly, "Sandbox");

    assert.equal(square.tenders[0]?.reference, "SQ:payment-1");
    assert.equal(
      linkly.tenders[0]?.reference,
      "ANZBACKEND:txn%20%3A%2F1:RFN%20%2F1:" +
        "session=session%20%3A%2F1:environment=Sandbox",
    );
    assert.equal(linkly.tenders[0]?.reservationToken, null);

    assert.deepEqual(
      await readOrdinaryOrder(connection, "order-linkly-purchase"),
      ordinaryLinkly,
    );
    assert.notEqual(linkly, ordinaryLinkly);
    assert.notEqual(linkly.tenders, ordinaryLinkly.tenders);
  });
});

test("订单同步材料按 tender 绑定即时恢复卡证据，公开订单与普通 resolve 仍保持脱敏", async () => {
  await withDatabase(async (connection) => {
    const orderGuid = "order-square-card-evidence";
    const tenderGuid = "tender-square-card-evidence";
    const attemptId = "attempt-square-card-evidence";
    await seedOrderTender(connection, {
      orderGuid,
      tenderGuid,
      attemptId,
      provider: "square",
      operation: "purchase",
      amountCents: 1_250,
      syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
      paymentId: "payment-card-evidence",
    });
    const evidence = {
      version: 1,
      provider: "square",
      operation: "purchase",
      processor: "Square",
      txnRef: "payment-card-evidence",
      authCode: "AUTH01",
      cardType: "VISA",
      cardBin: 411111,
      maskedCardNumber: "411111******1111",
      merchantId: "merchant-1",
      responseCode: "00",
      responseText: "APPROVED",
      stan: "123456",
      bankDateTimeIso: NOW,
      amountCents: 1_250,
      refundReference: null,
    } as const;
    const ciphertext = await encryptPaymentProtectedMaterial(encryptor, {
      voucherReservationToken: null,
      cardSyncEvidence: evidence,
    });
    assert.ok(ciphertext);
    await connection.run(
      `UPDATE payment_attempts
       SET provider_payload_ciphertext = ?
       WHERE attempt_id = ?`,
      [ciphertext, attemptId],
    );

    const ordinary = await readOrdinaryOrder(connection, orderGuid);
    const resolver = createResolver(connection);
    const ordinaryResolved = await resolver.resolve(ordinary, null);
    assert.equal(
      "cardSyncEvidenceByTenderGuid" in ordinaryResolved,
      false,
    );

    const material = await resolver.resolveForSync(ordinary, null);
    assert.deepEqual(material.order, ordinaryResolved);
    assert.deepEqual(
      material.cardSyncEvidenceByTenderGuid.get(tenderGuid),
      evidence,
    );
    assert.equal(material.cardSyncEvidenceByTenderGuid.size, 1);
    assert.equal(JSON.stringify(ordinary).includes("AUTH01"), false);
    assert.equal(JSON.stringify(material.order).includes("AUTH01"), false);
  });
});

test("M15 迁移保留 legacy 空来源供普通读取，但同步仍稳定失败关闭", async () => {
  const connection = new TestSqliteConnection();
  try {
    await applyMigrations(
      connection,
      () => NOW,
      POS_DATABASE_MIGRATIONS.filter(({ version }) => version <= 14),
    );
    const orderGuid = "order-legacy-line-provenance";
    await connection.run(
      `INSERT INTO local_orders (
        order_guid, local_sequence, store_code, device_code,
        cashier_id, cashier_name, sold_at_iso, state,
        total_cents, discount_cents, actual_amount_cents,
        original_order_guid, created_at_iso, updated_at_iso
      ) VALUES (?, ?, 'S1', 'IPAD-1', 'cashier-1', 'Cashier', ?,
        'PendingSync', 500, 0, 500, NULL, ?, ?)`,
      [orderGuid, nextSequence++, NOW, NOW, NOW],
    );
    await connection.run(
      `INSERT INTO local_order_lines (
        line_id, order_guid, line_sequence, product_code, item_number,
        lookup_code, display_name, quantity, unit_price_cents,
        discount_cents, actual_amount_cents, price_source, line_kind,
        return_source_key, original_order_guid, original_order_detail_guid
      ) VALUES (?, ?, 1, 'P1', NULL, 'P1', 'Legacy Product', '1', 500,
        0, 500, 'catalog', 'sale', NULL, NULL, NULL)`,
      [`line-${orderGuid}`, orderGuid],
    );
    await connection.run(
      `INSERT INTO order_tenders (
        tender_guid, order_guid, method, amount_cents,
        payment_attempt_id, created_at_iso
      ) VALUES (?, ?, 'cash', 500, NULL, ?)`,
      ["tender-legacy-line-provenance", orderGuid, NOW],
    );
    await applyMigrations(connection, () => NOW);

    const ordinary = await readOrdinaryOrder(connection, orderGuid);
    assert.equal(ordinary.lines[0]?.syncProvenance, undefined);
    const resolver = createResolver(connection);
    assert.equal(
      (await resolver.resolve(ordinary, null)).lines[0]?.syncProvenance,
      undefined,
    );
    await assert.rejects(
      () => resolver.resolveForSync(ordinary, null),
      (error: unknown) =>
        error instanceof OrderSyncMaterialError &&
        error.code === "ORDER_SYNC_LINE_PROVENANCE_MISSING",
    );
  } finally {
    await connection.close();
  }
});

test("同步材料拒绝调用输入与持久来源不一致，数据库拒绝篡改持久来源", async () => {
  await withDatabase(async (connection) => {
    const orderGuid = "order-line-provenance-mismatch";
    await seedOrderTender(connection, {
      orderGuid,
      tenderGuid: "tender-line-provenance-mismatch",
      attemptId: null,
      provider: null,
      operation: null,
      amountCents: 500,
      syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
    });
    const ordinary = await readOrdinaryOrder(connection, orderGuid);
    const resolver = createResolver(connection);
    const first = ordinary.lines[0];
    const firstProvenance = first?.syncProvenance;
    assert.ok(firstProvenance);
    await assert.rejects(
      () =>
        resolver.resolve(
          replaceFirstLine(ordinary, {
            syncProvenance: {
              referenceCode: firstProvenance.referenceCode,
              priceSource: firstProvenance.priceSource === 0 ? 1 : 0,
            },
          }),
          null,
        ),
      (error: unknown) =>
        error instanceof OrderSyncMaterialError &&
        error.code === "ORDER_SYNC_LINE_PROVENANCE_MISMATCH",
    );

    await assert.rejects(
      () =>
        connection.run(
          `UPDATE local_order_lines
           SET sync_price_source = 9
           WHERE order_guid = ?`,
          [orderGuid],
        ),
      /ORDER_LINE_SYNC_PROVENANCE_IMMUTABLE/u,
    );
    await assert.rejects(
      () =>
        connection.run(
          `UPDATE local_order_lines
           SET reference_code = 'tampered-reference'
           WHERE order_guid = ?`,
          [orderGuid],
        ),
      /ORDER_LINE_SYNC_PROVENANCE_IMMUTABLE/u,
    );
    const persisted = await connection.getFirst<{
      reference_code: unknown;
      sync_price_source: unknown;
    }>(
      `SELECT reference_code, sync_price_source
       FROM local_order_lines
       WHERE order_guid = ?`,
      [orderGuid],
    );
    assert.equal(
      persisted?.reference_code,
      DEFAULT_LINE_SYNC_PROVENANCE.referenceCode,
    );
    assert.equal(
      persisted?.sync_price_source,
      DEFAULT_LINE_SYNC_PROVENANCE.priceSource,
    );
  });
});

test("Square refund 使用与 WPF 相同的 UTF-8 Base64 CardRefundReference", async () => {
  await withDatabase(async (connection) => {
    const paymentId = "原支付-🧾";
    const refundId = "退款-😀";
    const fixture = await seedRefundTender(connection, {
      orderGuid: "order-square-refund",
      tenderGuid: "tender-square-refund",
      attemptId: "attempt-square-refund",
      provider: "square",
      method: "card",
      amountCents: -500,
      syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
      paymentId,
      responseCode: refundId,
      protectedContext: {
        version: 1,
        provider: "square",
        paymentId,
      },
    });

    const ordinary = await readOrdinaryOrder(
      connection,
      "order-square-refund",
    );
    const expected =
      `CARD_REFUND|refund=${utf8Base64(`SQRF:${refundId}`)}` +
      `|original=${utf8Base64(`SQ:${paymentId}`)}`;
    assert.notEqual(ordinary.tenders[0]?.reference, expected);

    const resolved = await fixture.resolver.resolve(ordinary, null);
    assert.equal(resolved.tenders[0]?.reference, expected);
    assert.equal(
      decodeCardRefundPart(resolved.tenders[0]?.reference, "refund"),
      `SQRF:${refundId}`,
    );
    assert.equal(
      decodeCardRefundPart(resolved.tenders[0]?.reference, "original"),
      `SQ:${paymentId}`,
    );
  });
});

test("Linkly refund 的本次引用来自本次 txn/session/env，原引用只来自对应容量上下文", async () => {
  await withDatabase(async (connection) => {
    const originalReference =
      "ANZBACKEND:sale%20txn:SALE-RFN:" +
      "session=sale-session:environment=Sandbox";
    const fixture = await seedRefundTender(connection, {
      orderGuid: "order-linkly-refund",
      tenderGuid: "tender-linkly-refund",
      attemptId: "attempt-linkly-refund",
      provider: "linkly-cloud",
      method: "card",
      amountCents: -700,
      syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
      sessionId: "refund session",
      txnRef: "refund txn/1",
      // 当前实现此列是原交易 RFN；resolver 绝不能把它猜成本次 refund RFN。
      rfn: "SALE-RFN",
      protectedContext: {
        version: 1,
        provider: "linkly-cloud",
        rfn: "SALE-RFN",
        originalReference,
      },
    });
    const ordinary = await readOrdinaryOrder(
      connection,
      "order-linkly-refund",
    );
    const resolved = await fixture.resolver.resolve(ordinary, "Sandbox");
    const expectedRefundReference =
      "ANZBACKEND:refund%20txn%2F1:" +
      "session=refund%20session:environment=Sandbox";

    assert.equal(
      resolved.tenders[0]?.reference,
      `CARD_REFUND|refund=${utf8Base64(expectedRefundReference)}` +
        `|original=${utf8Base64(originalReference)}`,
    );
    assert.equal(
      decodeCardRefundPart(resolved.tenders[0]?.reference, "refund"),
      expectedRefundReference,
    );
    assert.equal(
      decodeCardRefundPart(resolved.tenders[0]?.reference, "original"),
      originalReference,
    );
    assert.equal(
      resolved.tenders[0]?.reference.includes(utf8Base64("SALE-RFN")),
      false,
    );
  });
});

test("Voucher purchase/refund 仅从 approved 受保护状态恢复券码和 token", async () => {
  await withDatabase(async (connection) => {
    await seedOrderTender(connection, {
      orderGuid: "order-voucher-purchase",
      tenderGuid: "tender-voucher-purchase",
      attemptId: "attempt-voucher-purchase",
      provider: "voucher",
      operation: "purchase",
      method: "voucher",
      amountCents: 1_000,
      syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
    });
    const purchaseTokens = createVoucherTokens(connection, 1);
    await purchaseTokens.save({
      attemptId: "attempt-voucher-purchase",
      idempotencyKey: "idem-attempt-voucher-purchase",
      orderGuid: "order-voucher-purchase",
      operation: "purchase",
      phase: "approved",
      storeCode: "S1",
      cashierId: "cashier-1",
      voucherCode: "VOUCHER-新-1",
      reservationToken: "reservation-secret-1",
      amountCents: 1_000,
      expiresAtIso: EXPIRES,
      reason: null,
    });

    const refund = await seedRefundTender(connection, {
      orderGuid: "order-voucher-refund",
      tenderGuid: "tender-voucher-refund",
      attemptId: "attempt-voucher-refund",
      provider: "voucher",
      method: "voucher",
      amountCents: -300,
      syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
      protectedContext: { version: 1, provider: "voucher" },
    });
    const refundTokens = createVoucherTokens(connection, 2);
    await refundTokens.save({
      attemptId: "attempt-voucher-refund",
      idempotencyKey: "idem-attempt-voucher-refund",
      orderGuid: "order-voucher-refund",
      operation: "refund",
      phase: "approved",
      storeCode: "S1",
      cashierId: "cashier-1",
      voucherCode: "VOUCHER-REFUND-新",
      reservationToken: null,
      amountCents: -300,
      expiresAtIso: EXPIRES,
      reason: "RETURN_REFUND",
    });

    const ordinaryPurchase = await readOrdinaryOrder(
      connection,
      "order-voucher-purchase",
    );
    const ordinaryRefund = await readOrdinaryOrder(
      connection,
      "order-voucher-refund",
    );
    assert.deepEqual(
      ordinaryPurchase.tenders.map(({ reference, reservationToken }) => ({
        reference,
        reservationToken,
      })),
      [{ reference: null, reservationToken: null }],
    );
    assert.deepEqual(
      ordinaryRefund.tenders.map(({ reference, reservationToken }) => ({
        reference,
        reservationToken,
      })),
      [{ reference: null, reservationToken: null }],
    );

    const purchase = await createResolver(connection).resolve(
      ordinaryPurchase,
      null,
    );
    const resolvedRefund = await refund.resolver.resolve(
      ordinaryRefund,
      null,
    );
    assert.deepEqual(
      purchase.tenders.map(({ reference, reservationToken }) => ({
        reference,
        reservationToken,
      })),
      [{
        reference: "VOUCHER-新-1",
        reservationToken: "reservation-secret-1",
      }],
    );
    assert.deepEqual(
      resolvedRefund.tenders.map(({ reference, reservationToken }) => ({
        reference,
        reservationToken,
      })),
      [{ reference: "VOUCHER-REFUND-新", reservationToken: null }],
    );
  });
});

test("礼券代替刷卡/现金额度的退款可同步；刷卡退款绑定现金额度仍失败关闭", async () => {
  for (const [capacityMethod, protectedContext] of [
    ["card", { version: 1, provider: "manual-card" }],
    ["cash", null],
  ] as const) {
    await withDatabase(async (connection) => {
      const refund = await seedRefundTender(connection, {
        orderGuid: "order-voucher-substitute",
        tenderGuid: "tender-voucher-substitute",
        attemptId: "attempt-voucher-substitute",
        provider: "voucher",
        method: "voucher",
        capacityMethod,
        amountCents: -250,
        syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
        protectedContext,
      });
      await createVoucherTokens(connection, 3).save({
        attemptId: "attempt-voucher-substitute",
        idempotencyKey: "idem-attempt-voucher-substitute",
        orderGuid: "order-voucher-substitute",
        operation: "refund",
        phase: "approved",
        storeCode: "S1",
        cashierId: "cashier-1",
        voucherCode: "VOUCHER-SUBSTITUTE-1",
        reservationToken: null,
        amountCents: -250,
        expiresAtIso: EXPIRES,
        reason: "RETURN_REFUND",
      });
      const resolved = await refund.resolver.resolve(
        await readOrdinaryOrder(connection, "order-voucher-substitute"),
        null,
      );
      assert.deepEqual(
        resolved.tenders.map(({ method, reference }) => ({ method, reference })),
        [{ method: "voucher", reference: "VOUCHER-SUBSTITUTE-1" }],
      );
    });
  }

  await withDatabase(async (connection) => {
    const refund = await seedRefundTender(connection, {
      orderGuid: "order-card-on-cash",
      tenderGuid: "tender-card-on-cash",
      attemptId: "attempt-card-on-cash",
      provider: "square",
      method: "card",
      capacityMethod: "cash",
      amountCents: -250,
      syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
      paymentId: "payment-card-on-cash",
      responseCode: "refund-card-on-cash",
      protectedContext: null,
    });
    await assertMaterialRejects(
      refund.resolver,
      await readOrdinaryOrder(connection, "order-card-on-cash"),
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    );
  });
});

test("M16 voucher reversal：普通读取保留完整账本，同步只在严格 Reversed 事实下成对剔除", async () => {
  await withDatabase(async (connection) => {
    const fixture = await seedApprovedVoucherPurchase(
      connection,
      "m16-reversed",
    );
    await connection.run(
      "UPDATE local_orders SET state = 'Completing' WHERE order_guid = ?",
      [fixture.order.orderGuid],
    );
    const store = createVoucherReversalStore(connection);
    const prepared = await store.prepareOrLoad({
      actionId: "m16-reversal-action",
      orderGuid: fixture.order.orderGuid,
      sourceTenderGuid: fixture.order.tenders[0]?.tenderGuid ?? "",
      reason: "SALE",
      actor: VOUCHER_REVERSAL_ACTOR,
    });
    const submitted = await store.markSubmitted(prepared);
    await fixture.tokens.save({
      attemptId: fixture.attemptId,
      idempotencyKey: `idem-${fixture.attemptId}`,
      orderGuid: fixture.order.orderGuid,
      operation: "purchase",
      phase: "release-submitted",
      storeCode: "S1",
      cashierId: "cashier-1",
      voucherCode: "VOUCHER-m16-reversed",
      reservationToken: "reservation-m16-reversed",
      amountCents: 500,
      expiresAtIso: EXPIRES,
      reason: null,
    });
    await fixture.tokens.save({
      attemptId: fixture.attemptId,
      idempotencyKey: `idem-${fixture.attemptId}`,
      orderGuid: fixture.order.orderGuid,
      operation: "purchase",
      phase: "released",
      storeCode: "S1",
      cashierId: "cashier-1",
      voucherCode: "VOUCHER-m16-reversed",
      reservationToken: "reservation-m16-reversed",
      amountCents: 500,
      expiresAtIso: EXPIRES,
      reason: null,
    });
    await store.commitReleased(submitted, {
      state: "Cancelled",
      responseCode: "VOUCHER_RELEASED",
    });
    await connection.run(
      "UPDATE local_orders SET state = 'PendingSync' WHERE order_guid = ?",
      [fixture.order.orderGuid],
    );

    const ordinary = await readOrdinaryOrder(
      connection,
      fixture.order.orderGuid,
    );
    const resolved = await fixture.resolver.resolve(ordinary, null);
    assert.equal(resolved.tenders.length, 2);
    assert.deepEqual(
      resolved.tenders.map((tender) => tender.amount.cents).sort(),
      [-500, 500],
    );

    const sync = await fixture.resolver.resolveForSync(ordinary, null);
    assert.equal(sync.order.tenders.length, 0);
  });
});

test("M16 voucher reversal：Prepared/Unknown/Blocked 均以同一稳定码阻止同步且不剔除单边 tender", async (t) => {
  for (const state of ["Prepared", "Unknown", "Blocked"] as const) {
    await t.test(state, async () => {
      await withDatabase(async (connection) => {
        const fixture = await seedApprovedVoucherPurchase(
          connection,
          `m16-${state.toLowerCase()}`,
        );
        await connection.run(
          "UPDATE local_orders SET state = 'Completing' WHERE order_guid = ?",
          [fixture.order.orderGuid],
        );
        const store = createVoucherReversalStore(connection);
        const prepared = await store.prepareOrLoad({
          actionId: `m16-${state.toLowerCase()}-action`,
          orderGuid: fixture.order.orderGuid,
          sourceTenderGuid: fixture.order.tenders[0]?.tenderGuid ?? "",
          reason: "SALE",
          actor: VOUCHER_REVERSAL_ACTOR,
        });
        if (state === "Unknown") {
          const submitted = await store.markSubmitted(prepared);
          await store.markUnknown(
            submitted,
            "VOUCHER_RELEASE_RESULT_UNRESOLVED",
          );
        } else if (state === "Blocked") {
          await store.markBlocked(prepared, "VOUCHER_RELEASE_REJECTED");
        }
        const ordinary = await readOrdinaryOrder(
          connection,
          fixture.order.orderGuid,
        );
        assert.equal(
          (await fixture.resolver.resolve(ordinary, null)).tenders.length,
          1,
        );
        await assert.rejects(
          () => fixture.resolver.resolveForSync(ordinary, null),
          (error: unknown) =>
            error instanceof OrderSyncMaterialError &&
            error.code === "ORDER_SYNC_VOUCHER_REVERSAL_UNRESOLVED",
        );
      });
    });
  }
});

test("M16 voucher reversal 错绑/成功 audit 损坏稳定 MISMATCH，真实 voucher refund 不受影响", async () => {
  await withDatabase(async (connection) => {
    const fixture = await seedApprovedVoucherPurchase(
      connection,
      "m16-mismatch",
    );
    await connection.run(
      "UPDATE local_orders SET state = 'Completing' WHERE order_guid = ?",
      [fixture.order.orderGuid],
    );
    const store = createVoucherReversalStore(connection);
    const submitted = await store.markSubmitted(
      await store.prepareOrLoad({
        actionId: "m16-mismatch-action",
        orderGuid: fixture.order.orderGuid,
        sourceTenderGuid: fixture.order.tenders[0]?.tenderGuid ?? "",
        reason: "SALE",
        actor: VOUCHER_REVERSAL_ACTOR,
      }),
    );
    for (const phase of ["release-submitted", "released"] as const) {
      await fixture.tokens.save({
        attemptId: fixture.attemptId,
        idempotencyKey: `idem-${fixture.attemptId}`,
        orderGuid: fixture.order.orderGuid,
        operation: "purchase",
        phase,
        storeCode: "S1",
        cashierId: "cashier-1",
        voucherCode: "VOUCHER-m16-mismatch",
        reservationToken: "reservation-m16-mismatch",
        amountCents: 500,
        expiresAtIso: EXPIRES,
        reason: null,
      });
    }
    await store.commitReleased(submitted, {
      state: "Cancelled",
      responseCode: "VOUCHER_RELEASED",
    });
    await connection.run(
      "UPDATE local_orders SET state = 'PendingSync' WHERE order_guid = ?",
      [fixture.order.orderGuid],
    );
    await connection.exec(
      "DROP TRIGGER trg_voucher_tender_reversal_audit_immutable;",
    );
    await connection.run(
      `UPDATE audit_events
       SET payload_json = '{"action":"payment-tender-remove","outcome":"blocked"}'
       WHERE correlation_id = 'm16-mismatch-action'`,
    );
    const ordinary = await readOrdinaryOrder(
      connection,
      fixture.order.orderGuid,
    );
    await assert.rejects(
      () => fixture.resolver.resolveForSync(ordinary, null),
      (error: unknown) =>
        error instanceof OrderSyncMaterialError &&
        error.code === "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
    );
  });

  await withDatabase(async (connection) => {
    const refund = await seedRefundTender(connection, {
      orderGuid: "order-m16-real-voucher-refund",
      tenderGuid: "tender-m16-real-voucher-refund",
      attemptId: "attempt-m16-real-voucher-refund",
      provider: "voucher",
      method: "voucher",
      amountCents: -300,
      syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
      protectedContext: { version: 1, provider: "voucher" },
    });
    const tokens = createVoucherTokens(connection, 88);
    await tokens.save({
      attemptId: "attempt-m16-real-voucher-refund",
      idempotencyKey: "idem-attempt-m16-real-voucher-refund",
      orderGuid: "order-m16-real-voucher-refund",
      operation: "refund",
      phase: "approved",
      storeCode: "S1",
      cashierId: "cashier-1",
      voucherCode: "VOUCHER-M16-REFUND",
      reservationToken: null,
      amountCents: -300,
      expiresAtIso: EXPIRES,
      reason: "RETURN_REFUND",
    });
    const ordinary = await readOrdinaryOrder(
      connection,
      "order-m16-real-voucher-refund",
    );
    const sync = await new SqliteOrderSyncMaterialResolver(connection, {
      returnCapacityVault: createReturnCapacityVault(connection),
      voucherProtectedTokens: tokens,
    }).resolveForSync(ordinary, null);
    assert.equal(sync.order.tenders.length, 1);
    assert.equal(sync.order.tenders[0]?.amount.cents, -300);
    assert.equal(
      sync.order.tenders[0]?.reference,
      "VOUCHER-M16-REFUND",
    );
    assert.ok(refund.resolver);
  });
});

test("card reversal link 稳定 unsupported，且在失败关闭前不读取任何 card evidence", async () => {
  await withDatabase(async (connection) => {
    await seedOrderTender(connection, {
      orderGuid: "order-card-reversal-unsupported",
      tenderGuid: "tender-card-source",
      attemptId: "attempt-card-source",
      provider: "square",
      operation: "purchase",
      amountCents: 500,
      syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
      paymentId: "payment-card-source",
    });
    await connection.run(
      `INSERT INTO order_tenders (
        tender_guid, order_guid, method, amount_cents,
        payment_attempt_id, created_at_iso
      ) VALUES (
        'tender-card-reversal', 'order-card-reversal-unsupported',
        'card', -500, NULL, ?
      )`,
      [NOW],
    );
    await connection.run(
      `INSERT INTO payment_tender_reversal_links (
        order_guid, action_id, source_tender_guid,
        reversal_tender_guid, created_at_iso
      ) VALUES (
        'order-card-reversal-unsupported', 'card-reversal-action',
        'tender-card-source', 'tender-card-reversal', ?
      )`,
      [NOW],
    );
    const ordinary = await readOrdinaryOrder(
      connection,
      "order-card-reversal-unsupported",
    );
    let evidenceReads = 0;
    const resolver = new SqliteOrderSyncMaterialResolver(connection, {
      returnCapacityVault: createReturnCapacityVault(connection),
      voucherProtectedTokens: createVoucherTokens(connection, 89),
      paymentProtectedMaterials: {
        async read() {
          evidenceReads += 1;
          throw new Error("card evidence must not be read");
        },
      },
    });

    assert.equal((await resolver.resolve(ordinary, null)).tenders.length, 2);
    await assert.rejects(
      () => resolver.resolveForSync(ordinary, null),
      (error: unknown) =>
        error instanceof OrderSyncMaterialError &&
        error.code === "ORDER_SYNC_CARD_REVERSAL_UNSUPPORTED",
    );
    assert.equal(evidenceReads, 0);
  });
});

test("现金 tender 不恢复敏感引用，resolver 不写库也不缓存副本", async () => {
  await withDatabase(async (connection) => {
    await seedOrderTender(connection, {
      orderGuid: "order-cash",
      tenderGuid: "tender-cash",
      attemptId: null,
      provider: null,
      operation: null,
      method: "cash",
      amountCents: 500,
      syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
    });
    const ordinary = await readOrdinaryOrder(connection, "order-cash");
    const before = await totalChanges(connection);
    const resolver = createResolver(connection);
    const first = await resolver.resolve(ordinary, null);
    const second = await resolver.resolve(ordinary, null);

    assert.deepEqual(first.tenders, [{
      tenderGuid: "tender-cash",
      method: "cash",
      amount: { currency: "AUD", cents: 500 },
      reference: null,
      reservationToken: null,
    }]);
    assert.notEqual(first, second);
    assert.notEqual(first.tenders, second.tenders);
    assert.equal(await totalChanges(connection), before);
  });
});

test("退货容量受保护上下文只把已解密的确定性损坏转成 typed integrity error", async (t) => {
  await t.test("malformed JSON 映射稳定同步拒绝", async () => {
    await withDatabase(async (connection) => {
      const fixture = await seedRefundTender(connection, {
        orderGuid: "order-context-json-corrupt",
        tenderGuid: "tender-context-json-corrupt",
        attemptId: "attempt-context-json-corrupt",
        provider: "square",
        method: "card",
        amountCents: -500,
        syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
        paymentId: "payment-context-json-corrupt",
        responseCode: "refund-context-json-corrupt",
        protectedContext: {
          version: 1,
          provider: "square",
          paymentId: "payment-context-json-corrupt",
        },
      });
      const capacityId = "capacity-attempt-context-json-corrupt";
      await tamperReturnCapacityContext(
        connection,
        capacityId,
        "{malformed-json",
      );
      const vault = createReturnCapacityVault(connection);

      await assertProtectedIntegrityRejects(
        () => vault.resolveProtectedContext(capacityId),
        "PROTECTED_MATERIAL_JSON_INVALID",
      );
      await assertMaterialRejects(
        fixture.resolver,
        await readOrdinaryOrder(connection, "order-context-json-corrupt"),
        "ORDER_SYNC_RETURN_CONTEXT_MISMATCH",
        null,
      );
    });
  });

  await t.test("已解密数组属于 shape 损坏", async () => {
    await withDatabase(async (connection) => {
      const fixture = await seedRefundTender(connection, {
        orderGuid: "order-context-shape-corrupt",
        tenderGuid: "tender-context-shape-corrupt",
        attemptId: "attempt-context-shape-corrupt",
        provider: "square",
        method: "card",
        amountCents: -500,
        syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
        paymentId: "payment-context-shape-corrupt",
        responseCode: "refund-context-shape-corrupt",
        protectedContext: {
          version: 1,
          provider: "square",
          paymentId: "payment-context-shape-corrupt",
        },
      });
      const capacityId = "capacity-attempt-context-shape-corrupt";
      await tamperReturnCapacityContext(connection, capacityId, "[]");

      await assertProtectedIntegrityRejects(
        () => createReturnCapacityVault(connection)
          .resolveProtectedContext(capacityId),
        "PROTECTED_MATERIAL_SHAPE_INVALID",
      );
      await assertMaterialRejects(
        fixture.resolver,
        await readOrdinaryOrder(connection, "order-context-shape-corrupt"),
        "ORDER_SYNC_RETURN_CONTEXT_MISMATCH",
        null,
      );
    });
  });

  await t.test("非现金明确缺失 context 稳定拒绝，现金 null context 合法", async () => {
    await withDatabase(async (connection) => {
      const fixture = await seedRefundTender(connection, {
        orderGuid: "order-context-missing",
        tenderGuid: "tender-context-missing",
        attemptId: "attempt-context-missing",
        provider: "square",
        method: "card",
        amountCents: -500,
        syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
        paymentId: "payment-context-missing",
        responseCode: "refund-context-missing",
        protectedContext: {
          version: 1,
          provider: "square",
          paymentId: "payment-context-missing",
        },
      });
      const capacityId = "capacity-attempt-context-missing";
      await tamperReturnCapacityContext(connection, capacityId, null);

      await assertProtectedIntegrityRejects(
        () => createReturnCapacityVault(connection)
          .resolveProtectedContext(capacityId),
        "PROTECTED_MATERIAL_CONTEXT_MISSING",
      );
      await assertMaterialRejects(
        fixture.resolver,
        await readOrdinaryOrder(connection, "order-context-missing"),
        "ORDER_SYNC_RETURN_CONTEXT_MISMATCH",
        null,
      );

      const cashVault = createReturnCapacityVault(connection);
      await cashVault.seedOrLoad({
        capacityId: "capacity-cash-null-context",
        originalOrderGuid: "original-cash-null-context",
        method: "cash",
        originalAmountCents: 500,
        remainingAmountCents: 500,
        protectedContext: null,
        observedAtIso: NOW,
      });
      assert.equal(
        await cashVault.resolveProtectedContext(
          "capacity-cash-null-context",
        ),
        null,
      );
    });
  });
});

test("Voucher 受保护状态的 JSON/version/shape/明文绑定损坏均 typed 且稳定拒绝", async (t) => {
  const cases = [
    {
      name: "malformed JSON",
      plaintext: "{malformed-json",
      code: "PROTECTED_MATERIAL_JSON_INVALID",
    },
    {
      name: "unsupported version",
      plaintext: JSON.stringify({ version: 2, state: {} }),
      code: "PROTECTED_MATERIAL_VERSION_INVALID",
    },
    {
      name: "invalid state shape",
      plaintext: JSON.stringify({
        version: 1,
        state: { attemptId: 42 },
      }),
      code: "PROTECTED_MATERIAL_SHAPE_INVALID",
    },
  ] as const;

  for (const [index, fixtureCase] of cases.entries()) {
    await t.test(fixtureCase.name, async () => {
      await withDatabase(async (connection) => {
        const fixture = await seedApprovedVoucherPurchase(
          connection,
          `corrupt-${index}`,
        );
        await connection.run(
          `UPDATE voucher_protected_attempt_states
           SET state_ciphertext = ?
           WHERE attempt_id = ?`,
          [
            await encryptor.encrypt(fixtureCase.plaintext),
            fixture.attemptId,
          ],
        );

        await assertProtectedIntegrityRejects(
          () => fixture.tokens.getByAttempt(fixture.attemptId),
          fixtureCase.code,
        );
        await assertMaterialRejects(
          fixture.resolver,
          fixture.order,
          "ORDER_SYNC_VOUCHER_STATE_MISMATCH",
          null,
        );
      });
    });
  }

  await t.test("decrypted state 与明文列换绑", async () => {
    await withDatabase(async (connection) => {
      const fixture = await seedApprovedVoucherPurchase(
        connection,
        "binding",
      );
      await connection.exec(
        "DROP TRIGGER trg_voucher_protected_state_binding_immutable",
      );
      await connection.run(
        `UPDATE voucher_protected_attempt_states
         SET idempotency_key = ?
         WHERE attempt_id = ?`,
        ["tampered-idempotency-key", fixture.attemptId],
      );

      await assertProtectedIntegrityRejects(
        () => fixture.tokens.getByAttempt(fixture.attemptId),
        "PROTECTED_MATERIAL_BINDING_MISMATCH",
      );
      await assertMaterialRejects(
        fixture.resolver,
        fixture.order,
        "ORDER_SYNC_VOUCHER_STATE_MISMATCH",
        null,
      );
    });
  });
});

test("decrypt 与数据库错误穿透 resolver，保留 outbox 重试语义", async (t) => {
  await t.test("退货容量 decrypt 错误原样穿透", async () => {
    await withDatabase(async (connection) => {
      await seedRefundTender(connection, {
        orderGuid: "order-context-decrypt-error",
        tenderGuid: "tender-context-decrypt-error",
        attemptId: "attempt-context-decrypt-error",
        provider: "square",
        method: "card",
        amountCents: -500,
        syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
        paymentId: "payment-context-decrypt-error",
        responseCode: "refund-context-decrypt-error",
        protectedContext: {
          version: 1,
          provider: "square",
          paymentId: "payment-context-decrypt-error",
        },
      });
      const decryptError = new Error(
        "Sensitive payload ciphertext is invalid.",
      );
      const resolver = new SqliteOrderSyncMaterialResolver(connection, {
        returnCapacityVault: new SqliteReturnCapacityVault(
          connection,
          {
            encrypt: encryptor.encrypt,
            async decrypt() {
              throw decryptError;
            },
          },
          () => NOW,
        ),
        voucherProtectedTokens: createVoucherTokens(connection, 70),
      });
      const order = await readOrdinaryOrder(
        connection,
        "order-context-decrypt-error",
      );

      await assert.rejects(
        () => resolver.resolve(order, null),
        (error: unknown) => error === decryptError,
      );
    });
  });

  await t.test("Voucher decrypt 错误原样穿透", async () => {
    await withDatabase(async (connection) => {
      const fixture = await seedApprovedVoucherPurchase(
        connection,
        "decrypt-error",
      );
      const decryptError = new Error(
        "Sensitive payload ciphertext is invalid.",
      );
      const resolver = new SqliteOrderSyncMaterialResolver(connection, {
        returnCapacityVault: createReturnCapacityVault(connection),
        voucherProtectedTokens: new SqliteVoucherProtectedTokenStore(
          connection,
          {
            encrypt: encryptor.encrypt,
            async decrypt() {
              throw decryptError;
            },
          },
          () => "vpr_abcdefghijklmn71",
          () => NOW,
        ),
      });

      await assert.rejects(
        () => resolver.resolve(fixture.order, null),
        (error: unknown) => error === decryptError,
      );
    });
  });

  await t.test("数据库/IO 错误原样穿透", async () => {
    await withDatabase(async (connection) => {
      await seedOrderTender(connection, {
        orderGuid: "order-database-error",
        tenderGuid: "tender-database-error",
        attemptId: null,
        provider: null,
        operation: null,
        method: "cash",
        amountCents: 500,
        syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
      });
      const ordinary = await readOrdinaryOrder(
        connection,
        "order-database-error",
      );
      const databaseError = new Error("database temporarily unavailable");
      const resolver = new SqliteOrderSyncMaterialResolver(
        withGetFirstFailure(connection, databaseError),
        {
          returnCapacityVault: createReturnCapacityVault(connection),
          voucherProtectedTokens: createVoucherTokens(connection, 72),
        },
      );

      await assert.rejects(
        () => resolver.resolve(ordinary, null),
        (error: unknown) => error === databaseError,
      );
    });
  });
});

test("完整订单明细逐字段绑定持久化顺序，任何调用前替换均稳定失败关闭", async () => {
  await withDatabase(async (connection) => {
    const ordinary = await seedRichLineOrder(connection);
    const [first, second] = ordinary.lines;
    assert.ok(first);
    assert.ok(second);
    const variants: readonly LocalOrder[] = [
      { ...ordinary, lines: [first] },
      { ...ordinary, lines: [second, first] },
      replaceFirstLine(ordinary, { lineId: "changed-line-id" }),
      replaceFirstLine(ordinary, { productCode: "changed-product" }),
      replaceFirstLine(ordinary, { itemNumber: null }),
      replaceFirstLine(ordinary, { lookupCode: "changed-lookup" }),
      replaceFirstLine(ordinary, { displayName: "changed-display" }),
      replaceFirstLine(ordinary, { quantity: "9.99" }),
      replaceFirstLine(ordinary, {
        unitPrice: { currency: "AUD", cents: 301 },
      }),
      replaceFirstLine(ordinary, {
        discount: { currency: "AUD", cents: 21 },
      }),
      replaceFirstLine(ordinary, {
        actualAmount: { currency: "AUD", cents: 281 },
      }),
      replaceFirstLine(ordinary, { priceSource: "catalog" }),
      replaceFirstLine(ordinary, { kind: "sale" }),
      replaceFirstLine(ordinary, { returnSourceKey: "changed-source" }),
      replaceFirstLine(ordinary, {
        originalOrderGuid: "changed-original-order",
      }),
      replaceFirstLine(ordinary, {
        originalOrderDetailGuid: "changed-original-detail",
      }),
    ];

    for (const variant of variants) {
      await assertMaterialRejects(
        createResolver(connection),
        variant,
        "ORDER_SYNC_ORDER_MISMATCH",
        null,
      );
    }
  });
});

test("resolver 从持久化事实重建完整深冻结订单，调用方后续嵌套突变不污染结果", async () => {
  await withDatabase(async (connection) => {
    const ordinary = await seedRichLineOrder(connection);
    const resolved = await createResolver(connection).resolve(ordinary, null);
    const snapshot = structuredClone(resolved);

    assert.deepEqual(resolved, ordinary);
    assert.notEqual(resolved, ordinary);
    assert.notEqual(resolved.total, ordinary.total);
    assert.notEqual(resolved.discount, ordinary.discount);
    assert.notEqual(resolved.actualAmount, ordinary.actualAmount);
    assert.notEqual(resolved.lines, ordinary.lines);
    assert.notEqual(resolved.lines[0], ordinary.lines[0]);
    assert.notEqual(
      resolved.lines[0]?.unitPrice,
      ordinary.lines[0]?.unitPrice,
    );
    assert.notEqual(resolved.tenders, ordinary.tenders);
    assert.notEqual(resolved.tenders[0], ordinary.tenders[0]);
    assert.notEqual(
      resolved.tenders[0]?.amount,
      ordinary.tenders[0]?.amount,
    );
    assertDeepFrozenOrder(resolved);

    const mutable = ordinary as unknown as {
      storeCode: string;
      total: { cents: number };
      discount: { cents: number };
      actualAmount: { cents: number };
      lines: {
        displayName: string;
        unitPrice: { cents: number };
        discount: { cents: number };
        actualAmount: { cents: number };
      }[];
      tenders: {
        reference: string | null;
        amount: { cents: number };
      }[];
    };
    mutable.storeCode = "MUTATED";
    mutable.total.cents = 1;
    mutable.discount.cents = 2;
    mutable.actualAmount.cents = 3;
    if (mutable.lines[0]) {
      mutable.lines[0].displayName = "MUTATED";
      mutable.lines[0].unitPrice.cents = 4;
      mutable.lines[0].discount.cents = 5;
      mutable.lines[0].actualAmount.cents = 6;
    }
    mutable.lines.reverse();
    if (mutable.tenders[0]) {
      mutable.tenders[0].reference = "MUTATED";
      mutable.tenders[0].amount.cents = 7;
    }

    assert.deepEqual(resolved, snapshot);
  });
});

test("order、amount、state、provider、environment 与 capacity context 任一冲突均失败关闭", async (t) => {
  await t.test("cross-order", async () => {
    await withDatabase(async (connection) => {
      await seedOrderTender(connection, {
        orderGuid: "order-cross-a",
        tenderGuid: "tender-cross-a",
        attemptId: "attempt-cross-a",
        provider: "square",
        operation: "purchase",
        amountCents: 500,
        syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
        paymentId: "payment-cross-a",
      });
      const ordinary = await readOrdinaryOrder(connection, "order-cross-a");
      await assertMaterialRejects(
        createResolver(connection),
        { ...ordinary, orderGuid: "order-cross-b" },
        "ORDER_SYNC_ORDER_MISMATCH",
      );
    });
  });

  await t.test("amount", async () => {
    await withDatabase(async (connection) => {
      await seedOrderTender(connection, {
        orderGuid: "order-amount",
        tenderGuid: "tender-amount",
        attemptId: "attempt-amount",
        provider: "square",
        operation: "purchase",
        amountCents: 500,
        syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
        paymentId: "payment-amount",
      });
      const ordinary = await readOrdinaryOrder(connection, "order-amount");
      await assertMaterialRejects(
        createResolver(connection),
        {
          ...ordinary,
          tenders: ordinary.tenders.map((tender) => ({
            ...tender,
            amount: { currency: "AUD" as const, cents: 499 },
          })),
        },
        "ORDER_SYNC_TENDER_MISMATCH",
      );
    });
  });

  await t.test("state", async () => {
    await withDatabase(async (connection) => {
      await seedOrderTender(connection, {
        orderGuid: "order-state",
        tenderGuid: "tender-state",
        attemptId: "attempt-state",
        provider: "square",
        operation: "purchase",
        amountCents: 500,
        syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
        paymentId: "payment-state",
      });
      const ordinary = await readOrdinaryOrder(connection, "order-state");
      await connection.run(
        "UPDATE payment_attempts SET state = 'Declined' WHERE attempt_id = ?",
        ["attempt-state"],
      );
      await assertMaterialRejects(
        createResolver(connection),
        ordinary,
        "ORDER_SYNC_ATTEMPT_MISMATCH",
      );
    });
  });

  await t.test("provider", async () => {
    await withDatabase(async (connection) => {
      await seedOrderTender(connection, {
        orderGuid: "order-provider",
        tenderGuid: "tender-provider",
        attemptId: "attempt-provider",
        provider: "voucher",
        operation: "purchase",
        method: "card",
        amountCents: 500,
        syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
      });
      const ordinary = await readOrdinaryOrder(connection, "order-provider");
      await assertMaterialRejects(
        createResolver(connection),
        ordinary,
        "ORDER_SYNC_ATTEMPT_MISMATCH",
      );
    });
  });

  await t.test("refund binding", async () => {
    await withDatabase(async (connection) => {
      await seedOrderTender(connection, {
        orderGuid: "order-missing-return-binding",
        tenderGuid: "tender-missing-return-binding",
        attemptId: "attempt-missing-return-binding",
        provider: "square",
        operation: "refund",
        method: "card",
        amountCents: -500,
        syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
        paymentId: "original-payment",
        responseCode: "refund-payment",
      });
      const ordinary = await readOrdinaryOrder(
        connection,
        "order-missing-return-binding",
      );
      await assertMaterialRejects(
        createResolver(connection),
        ordinary,
        "ORDER_SYNC_RETURN_BINDING_MISMATCH",
      );
    });
  });

  await t.test("environment", async () => {
    await withDatabase(async (connection) => {
      await seedOrderTender(connection, {
        orderGuid: "order-environment",
        tenderGuid: "tender-environment",
        attemptId: "attempt-environment",
        provider: "linkly-cloud",
        operation: "purchase",
        amountCents: 500,
        syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
        sessionId: "session-env",
        txnRef: "txn-env",
        rfn: "rfn-env",
      });
      const ordinary = await readOrdinaryOrder(
        connection,
        "order-environment",
      );
      await assertMaterialRejects(
        createResolver(connection),
        ordinary,
        "ORDER_SYNC_ENVIRONMENT_INVALID",
        null,
      );
      await assertMaterialRejects(
        createResolver(connection),
        ordinary,
        "ORDER_SYNC_ENVIRONMENT_INVALID",
        "Staging",
      );
    });
  });

  await t.test("refund capacity provider context", async () => {
    await withDatabase(async (connection) => {
      const fixture = await seedRefundTender(connection, {
        orderGuid: "order-context-provider",
        tenderGuid: "tender-context-provider",
        attemptId: "attempt-context-provider",
        provider: "linkly-cloud",
        method: "card",
        amountCents: -500,
        syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
        sessionId: "session-context",
        txnRef: "txn-context",
        rfn: "RFN-context",
        protectedContext: {
          version: 1,
          provider: "square",
          paymentId: "not-this-provider",
        },
      });
      const ordinary = await readOrdinaryOrder(
        connection,
        "order-context-provider",
      );
      await assertMaterialRejects(
        fixture.resolver,
        ordinary,
        "ORDER_SYNC_RETURN_CONTEXT_MISMATCH",
      );
    });
  });

  await t.test("refund original Linkly environment", async () => {
    await withDatabase(async (connection) => {
      const fixture = await seedRefundTender(connection, {
        orderGuid: "order-context-environment",
        tenderGuid: "tender-context-environment",
        attemptId: "attempt-context-environment",
        provider: "linkly-cloud",
        method: "card",
        amountCents: -500,
        syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
        sessionId: "session-context-env",
        txnRef: "txn-context-env",
        rfn: "RFN-context-env",
        protectedContext: {
          version: 1,
          provider: "linkly-cloud",
          rfn: "RFN-context-env",
          originalReference:
            "ANZBACKEND:original:ORIGINAL-RFN:" +
            "session=original-session:environment=Production",
        },
      });
      const ordinary = await readOrdinaryOrder(
        connection,
        "order-context-environment",
      );
      await assertMaterialRejects(
        fixture.resolver,
        ordinary,
        "ORDER_SYNC_RETURN_CONTEXT_MISMATCH",
        "Sandbox",
      );
    });
  });
});

type SeedTenderInput = Readonly<{
  orderGuid: string;
  tenderGuid: string;
  attemptId: string | null;
  provider: "square" | "linkly-cloud" | "voucher" | null;
  operation: "purchase" | "refund" | null;
  method?: "cash" | "card" | "voucher";
  amountCents: number;
  syncProvenance: NonNullable<
    LocalOrder["lines"][number]["syncProvenance"]
  >;
  paymentId?: string | null;
  sessionId?: string | null;
  txnRef?: string | null;
  rfn?: string | null;
  responseCode?: string | null;
}>;

type ApprovedVoucherPurchaseFixture = Readonly<{
  attemptId: string;
  order: LocalOrder;
  tokens: SqliteVoucherProtectedTokenStore;
  resolver: SqliteOrderSyncMaterialResolver;
}>;

async function seedApprovedVoucherPurchase(
  connection: SqliteConnectionPort,
  suffix: string,
): Promise<ApprovedVoucherPurchaseFixture> {
  const attemptId = `attempt-voucher-${suffix}`;
  const orderGuid = `order-voucher-${suffix}`;
  await seedOrderTender(connection, {
    orderGuid,
    tenderGuid: `tender-voucher-${suffix}`,
    attemptId,
    provider: "voucher",
    operation: "purchase",
    method: "voucher",
    amountCents: 500,
    syncProvenance: DEFAULT_LINE_SYNC_PROVENANCE,
  });
  const tokens = new SqliteVoucherProtectedTokenStore(
    connection,
    encryptor,
    () => `vpr_${`protected_${suffix}`.replace(/[^A-Za-z0-9_-]/gu, "_")}`,
    () => NOW,
  );
  await tokens.save({
    attemptId,
    idempotencyKey: `idem-${attemptId}`,
    orderGuid,
    operation: "purchase",
    phase: "approved",
    storeCode: "S1",
    cashierId: "cashier-1",
    voucherCode: `VOUCHER-${suffix}`,
    reservationToken: `reservation-${suffix}`,
    amountCents: 500,
    expiresAtIso: EXPIRES,
    reason: null,
  });
  return {
    attemptId,
    order: await readOrdinaryOrder(connection, orderGuid),
    tokens,
    resolver: new SqliteOrderSyncMaterialResolver(connection, {
      returnCapacityVault: createReturnCapacityVault(connection),
      voucherProtectedTokens: tokens,
    }),
  };
}

function createReturnCapacityVault(
  connection: SqliteConnectionPort,
): SqliteReturnCapacityVault {
  return new SqliteReturnCapacityVault(connection, encryptor, () => NOW);
}

async function tamperReturnCapacityContext(
  connection: SqliteConnectionPort,
  capacityId: string,
  plaintext: string | null,
): Promise<void> {
  await connection.exec(
    "DROP TRIGGER trg_return_tender_capacity_identity_immutable",
  );
  if (plaintext === null) {
    await connection.exec("PRAGMA ignore_check_constraints = ON");
  }
  await connection.run(
    `UPDATE return_tender_capacities
     SET protected_context_ciphertext = ?
     WHERE capacity_id = ?`,
    [
      plaintext === null ? null : await encryptor.encrypt(plaintext),
      capacityId,
    ],
  );
}

async function assertProtectedIntegrityRejects(
  operation: () => Promise<unknown>,
  code: string,
): Promise<void> {
  await assert.rejects(
    operation,
    (error: unknown) =>
      error instanceof ProtectedMaterialIntegrityError &&
      error.code === code,
  );
}

function withGetFirstFailure(
  connection: SqliteConnectionPort,
  failure: Error,
): SqliteConnectionPort {
  return {
    exec: (sql) => connection.exec(sql),
    run: (sql, parameters) => connection.run(sql, parameters),
    async getFirst() {
      throw failure;
    },
    getAll: (sql, parameters) => connection.getAll(sql, parameters),
    withExclusiveTransaction: (operation) =>
      connection.withExclusiveTransaction(operation),
    close: () => connection.close(),
  };
}

async function seedOrderTender(
  connection: SqliteConnectionPort,
  input: SeedTenderInput,
): Promise<void> {
  await connection.run(
    `INSERT INTO local_orders (
      order_guid, local_sequence, store_code, device_code,
      cashier_id, cashier_name, sold_at_iso, state,
      total_cents, discount_cents, actual_amount_cents,
      original_order_guid, created_at_iso, updated_at_iso
    ) VALUES (?, ?, 'S1', 'IPAD-1', 'cashier-1', 'Cashier', ?,
      'PendingSync', ?, 0, ?, NULL, ?, ?)`,
    [
      input.orderGuid,
      nextSequence++,
      NOW,
      input.amountCents,
      input.amountCents,
      NOW,
      NOW,
    ],
  );
  await connection.run(
    `INSERT INTO local_order_lines (
      line_id, order_guid, line_sequence, product_code, item_number,
      lookup_code, display_name, quantity, unit_price_cents,
      discount_cents, actual_amount_cents, price_source, line_kind,
      return_source_key, original_order_guid, original_order_detail_guid,
      reference_code, sync_price_source
    ) VALUES (?, ?, 1, 'P1', NULL, 'P1', 'Product', '1', ?,
      0, ?, 'catalog', 'sale', NULL, NULL, NULL, ?, ?)`,
    [
      `line-${input.orderGuid}`,
      input.orderGuid,
      input.amountCents,
      input.amountCents,
      input.syncProvenance.referenceCode,
      input.syncProvenance.priceSource,
    ],
  );
  if (input.attemptId !== null) {
    await connection.run(
      `INSERT INTO payment_attempts (
        attempt_id, idempotency_key, order_guid, provider, operation,
        amount_cents, state, checkout_id, payment_id, session_id,
        txn_ref, rfn, provider_payload_ciphertext,
        provider_receipt_ciphertext, provider_response_code,
        created_at_iso, updated_at_iso, last_error_code
      ) VALUES (?, ?, ?, ?, ?, ?, 'Approved', NULL, ?, ?, ?, ?, NULL,
        NULL, ?, ?, ?, NULL)`,
      [
        input.attemptId,
        `idem-${input.attemptId}`,
        input.orderGuid,
        input.provider,
        input.operation,
        input.amountCents,
        input.paymentId ?? null,
        input.sessionId ?? null,
        input.txnRef ?? null,
        input.rfn ?? null,
        input.responseCode ?? null,
        NOW,
        NOW,
      ],
    );
  }
  await connection.run(
    `INSERT INTO order_tenders (
      tender_guid, order_guid, method, amount_cents,
      payment_attempt_id, created_at_iso
    ) VALUES (?, ?, ?, ?, ?, ?)`,
    [
      input.tenderGuid,
      input.orderGuid,
      input.method ??
        (input.provider === null
          ? "cash"
          : input.provider === "voucher" ? "voucher" : "card"),
      input.amountCents,
      input.attemptId,
      NOW,
    ],
  );
}

async function seedRichLineOrder(
  connection: SqliteConnectionPort,
): Promise<LocalOrder> {
  const orderGuid = `order-rich-lines-${nextSequence}`;
  await seedOrderTender(connection, {
    orderGuid,
    tenderGuid: `tender-rich-lines-${nextSequence}`,
    attemptId: `attempt-rich-lines-${nextSequence}`,
    provider: "square",
    operation: "purchase",
    amountCents: 500,
    syncProvenance: RICH_LINE_SYNC_PROVENANCE,
    paymentId: `payment-rich-lines-${nextSequence}`,
  });
  await connection.run(
    `UPDATE local_order_lines
     SET product_code = 'PRODUCT-一',
       item_number = 'ITEM-一',
       lookup_code = 'LOOKUP-一',
       display_name = '退货商品 😀',
       quantity = '1.25',
       unit_price_cents = 300,
       discount_cents = 20,
       actual_amount_cents = 280,
       price_source = 'manual',
       line_kind = 'return',
       return_source_key = 'return-source-一',
       original_order_guid = 'original-order-一',
       original_order_detail_guid = 'original-detail-一'
     WHERE order_guid = ? AND line_sequence = 1`,
    [orderGuid],
  );
  await connection.run(
    `INSERT INTO local_order_lines (
      line_id, order_guid, line_sequence, product_code, item_number,
      lookup_code, display_name, quantity, unit_price_cents,
      discount_cents, actual_amount_cents, price_source, line_kind,
      return_source_key, original_order_guid, original_order_detail_guid,
      reference_code, sync_price_source
    ) VALUES (
      ?, ?, 2, 'PRODUCT-二', NULL, 'LOOKUP-二', '普通商品 🧾', '2',
      110, 0, 220, 'promotion', 'sale', NULL, NULL, NULL, NULL, 4
    )`,
    [`line-${orderGuid}-second`, orderGuid],
  );
  return readOrdinaryOrder(connection, orderGuid);
}

function replaceFirstLine(
  order: LocalOrder,
  patch: Partial<LocalOrder["lines"][number]>,
): LocalOrder {
  const first = order.lines[0];
  if (!first) throw new Error("Test first line is missing.");
  return {
    ...order,
    lines: [{ ...first, ...patch }, ...order.lines.slice(1)],
  };
}

function assertDeepFrozenOrder(order: LocalOrder): void {
  assert.equal(Object.isFrozen(order), true);
  assert.equal(Object.isFrozen(order.total), true);
  assert.equal(Object.isFrozen(order.discount), true);
  assert.equal(Object.isFrozen(order.actualAmount), true);
  assert.equal(Object.isFrozen(order.lines), true);
  for (const line of order.lines) {
    assert.equal(Object.isFrozen(line), true);
    assert.equal(Object.isFrozen(line.unitPrice), true);
    assert.equal(Object.isFrozen(line.discount), true);
    assert.equal(Object.isFrozen(line.actualAmount), true);
  }
  assert.equal(Object.isFrozen(order.tenders), true);
  for (const tender of order.tenders) {
    assert.equal(Object.isFrozen(tender), true);
    assert.equal(Object.isFrozen(tender.amount), true);
  }
}

async function seedRefundTender(
  connection: SqliteConnectionPort,
  input: Omit<
    SeedTenderInput,
    "attemptId" | "provider" | "operation" | "method"
  > & Readonly<{
    attemptId: string;
    provider: "square" | "linkly-cloud" | "voucher";
    method: "card" | "voucher";
    protectedContext: Readonly<Record<string, unknown>> | null;
    /** 原额度方式；缺省与退款方式相同（原路退款）。 */
    capacityMethod?: "cash" | "card" | "voucher";
  }>,
): Promise<Readonly<{ resolver: SqliteOrderSyncMaterialResolver }>> {
  await seedOrderTender(connection, { ...input, operation: "refund" });
  const capacityId = `capacity-${input.attemptId}`;
  const originalOrderGuid = `original-${input.orderGuid}`;
  const vault = new SqliteReturnCapacityVault(
    connection,
    encryptor,
    () => NOW,
  );
  await vault.seedOrLoad({
    capacityId,
    originalOrderGuid,
    method: input.capacityMethod ?? input.method,
    originalAmountCents: -input.amountCents,
    remainingAmountCents: 0,
    protectedContext: input.protectedContext,
    observedAtIso: NOW,
  });
  const actionId = `action-${input.attemptId}`;
  const allocationId = `allocation-${input.attemptId}`;
  const externalActionId = `external-action-${input.attemptId}`;
  await connection.run(
    `INSERT INTO return_actions (
      action_id, request_fingerprint, return_order_guid,
      action_recovery_token, source_kind, total_refund_cents, online,
      store_code, device_code, cashier_id, cashier_name, session_epoch,
      supervisor_grant_id, plan_json, state, created_at_iso,
      completed_at_iso, updated_at_iso
    ) VALUES (?, ?, ?, ?, 'receipt', ?, 1, 'S1', 'IPAD-1',
      'cashier-1', 'Cashier', 'session-epoch-1', NULL, '{}',
      'completed', ?, ?, ?)`,
    [
      actionId,
      `fingerprint-${input.attemptId}`,
      input.orderGuid,
      `recovery-${input.attemptId}`,
      -input.amountCents,
      NOW,
      NOW,
      NOW,
    ],
  );
  await connection.run(
    `INSERT INTO return_action_allocations (
      action_id, allocation_id, allocation_index, execution_kind,
      method, signed_amount_cents, capacity_id, original_order_guid,
      offline_evidence_id, offline_evidence_remaining_cents,
      external_attempt_id, external_attempt_kind, external_action_id,
      durable_attempt_id, status, protected_recovery_ciphertext,
      capacity_reservation_state, created_at_iso, updated_at_iso
    ) VALUES (?, ?, 0, 'online-refund', ?, ?, ?, ?, NULL, NULL,
      ?, 'payment-provider', ?, ?, 'completed', NULL, 'Committed', ?, ?)`,
    [
      actionId,
      allocationId,
      input.method,
      input.amountCents,
      capacityId,
      originalOrderGuid,
      `external-attempt-${input.attemptId}`,
      externalActionId,
      input.attemptId,
      NOW,
      NOW,
    ],
  );
  await connection.run(
    `INSERT INTO return_tender_attempt_bindings (
      tender_guid, action_id, allocation_id, external_attempt_kind,
      external_action_id, durable_attempt_id, created_at_iso
    ) VALUES (?, ?, ?, 'payment-provider', ?, ?, ?)`,
    [
      input.tenderGuid,
      actionId,
      allocationId,
      externalActionId,
      input.attemptId,
      NOW,
    ],
  );
  return { resolver: createResolver(connection) };
}

function createResolver(
  connection: SqliteConnectionPort,
): SqliteOrderSyncMaterialResolver {
  return new SqliteOrderSyncMaterialResolver(connection, {
    returnCapacityVault: new SqliteReturnCapacityVault(
      connection,
      encryptor,
      () => NOW,
    ),
    voucherProtectedTokens: createVoucherTokens(connection, 99),
    paymentProtectedMaterials: new SqlitePaymentProtectedMaterialReader(
      connection,
      encryptor,
    ),
  });
}

function createVoucherReversalStore(
  connection: SqliteConnectionPort,
): SqliteVoucherTenderReversalStore {
  let tenderSequence = 0;
  let auditSequence = 0;
  return new SqliteVoucherTenderReversalStore(
    connection,
    encryptor,
    {
      createReversalTenderGuid: () =>
        `sync-voucher-reversal-${++tenderSequence}`,
      createAuditEventId: () =>
        `sync-voucher-reversal-audit-${++auditSequence}`,
    },
    () => NOW,
  );
}

function createVoucherTokens(
  connection: SqliteConnectionPort,
  suffix: number,
): SqliteVoucherProtectedTokenStore {
  return new SqliteVoucherProtectedTokenStore(
    connection,
    encryptor,
    () => `vpr_abcdefghijklmn${suffix.toString().padStart(2, "0")}`,
    () => NOW,
  );
}

async function readOrdinaryOrder(
  connection: SqliteConnectionPort,
  orderGuid: string,
): Promise<LocalOrder> {
  const repositories = createSqliteRepositories(connection, {
    encryptor,
    nowIso: () => NOW,
    createLeaseId: () => "lease-order-sync-material",
  });
  const order = await repositories.orders.getByGuid(orderGuid);
  if (!order) throw new Error("Test order is missing.");
  return order;
}

async function assertMaterialRejects(
  resolver: SqliteOrderSyncMaterialResolver,
  order: LocalOrder,
  code: string,
  environment: string | null = "Production",
): Promise<void> {
  await assert.rejects(
    () => resolver.resolve(order, environment),
    (error: unknown) =>
      error instanceof OrderSyncMaterialError && error.code === code,
  );
}

function utf8Base64(value: string): string {
  return Buffer.from(value, "utf8").toString("base64");
}

function decodeCardRefundPart(
  reference: string | null | undefined,
  key: "refund" | "original",
): string | null {
  const encoded = reference
    ?.split("|")
    .find((part) => part.startsWith(`${key}=`))
    ?.slice(key.length + 1);
  return encoded ? Buffer.from(encoded, "base64").toString("utf8") : null;
}

async function totalChanges(connection: SqliteConnectionPort): Promise<number> {
  return Number(
    (await connection.getFirst<{ count: unknown }>(
      "SELECT total_changes() AS count",
    ))?.count,
  );
}

let nextSequence = 1;

async function withDatabase(
  operation: (connection: TestSqliteConnection) => Promise<void>,
): Promise<void> {
  const connection = new TestSqliteConnection();
  try {
    await applyMigrations(connection, () => NOW);
    await operation(connection);
  } finally {
    await connection.close();
  }
}

class TestSqliteConnection implements SqliteConnectionPort {
  private readonly database = new DatabaseSync(":memory:");

  public constructor() {
    this.database.exec("PRAGMA foreign_keys = ON");
  }

  public async exec(sql: string): Promise<void> {
    this.database.exec(sql);
  }

  public async run(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<SqlRunResult> {
    const result = this.database.prepare(sql).run(
      ...parameters.map(toSqlInputValue),
    );
    return {
      changes: Number(result.changes),
      lastInsertRowId: Number(result.lastInsertRowid),
    };
  }

  public async getFirst<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<T | null> {
    return (
      this.database.prepare(sql).get(
        ...parameters.map(toSqlInputValue),
      ) as T | undefined
    ) ?? null;
  }

  public async getAll<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<readonly T[]> {
    return this.database.prepare(sql).all(
      ...parameters.map(toSqlInputValue),
    ) as unknown as readonly T[];
  }

  public async withExclusiveTransaction<T>(
    operation: (transaction: SqliteConnectionPort) => Promise<T>,
  ): Promise<T> {
    this.database.exec("BEGIN IMMEDIATE");
    try {
      const result = await operation(this);
      this.database.exec("COMMIT");
      return result;
    } catch (error) {
      this.database.exec("ROLLBACK");
      throw error;
    }
  }

  public async close(): Promise<void> {
    this.database.close();
  }
}

function toSqlInputValue(value: SqlValue): SQLInputValue {
  return value as SQLInputValue;
}

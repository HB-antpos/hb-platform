import assert from "node:assert/strict";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import { applyMigrations } from "./migrations";
import { ProtectedMaterialIntegrityError } from "@hb/pos-db/core/db/protected-material-integrity-error";
import { SqliteRefundVoucherPrintMaterial } from "./sqlite-refund-voucher-print-material";
import { SqliteVoucherProtectedTokenStore } from "./sqlite-voucher-protected-token-store";
import type {
  SqliteConnectionPort,
  SqlRunResult,
  SqlValue,
} from "@hb/pos-db/core/db/types";

import type { VoucherProtectedAttemptState } from "@/features/payments/voucher";
import {
  hasRefundVoucherTender,
  ProtectedRefundVoucherReceiptRenderer,
} from "@hb/pos-receipt-core/features/receipts/refund-voucher-receipt-renderer";
import { renderRefundReceiptWithVouchers } from "@/core/runtime/return-fulfilment-runtime";
import type { LocalOrder } from "@hb/pos-domain/core/contracts/order";

const NOW = "2026-07-28T00:00:00.000Z";
const EXPIRES = "2027-07-28T00:00:00.000Z";

/** 混合退款夹具里第 index 笔 allocation 的券到期时刻：每张不同，用来证明到期日不会串券。 */
function mixedExpiry(index: number): string {
  return `2027-07-${String(10 + index).padStart(2, "0")}T13:59:59.000Z`;
}

const encryptor = {
  async encrypt(plaintext: string): Promise<Uint8Array> {
    return new TextEncoder().encode(plaintext);
  },
  async decrypt(ciphertext: Uint8Array): Promise<string> {
    return new TextDecoder().decode(ciphertext);
  },
};

test("只从唯一完成退货、负数 voucher tender、Approved attempt 与受保护状态恢复券码", async () => {
  await withFixture(async ({ adapter, connection }) => {
    assert.deepEqual(
      await adapter.resolveApprovedRefundVouchers(
        "return-action-1",
        "return-order-1",
      ),
      [{
        returnOrderGuid: "return-order-1",
        voucherCode: "REFUND-VOUCHER-001",
        refundAmountCents: 500,
        // 到期日就是发券时服务端返回并写入受保护状态的那一个，客户端不反推。
        expiresAtIso: EXPIRES,
      }],
    );

    const publicRow = await connection.getFirst<{
      tender_reference: unknown;
    }>(
      `SELECT payment_attempt_id AS tender_reference
       FROM order_tenders
       WHERE order_guid = ?`,
      ["return-order-1"],
    );
    assert.equal(publicRow?.tender_reference, "voucher-attempt-1");
    assert.equal(
      JSON.stringify(publicRow).includes("REFUND-VOUCHER-001"),
      false,
    );
  });
});

test("到期日取自这张券自己的受保护状态；缺失、null 或格式异常时仍恢复券码材料，只是不带到期日（不再因此失败关闭）", async () => {
  await withFixture(async ({ connection }) => {
    const store = new SqliteVoucherProtectedTokenStore(
      connection,
      encryptor,
      () => "vpr_abcdefghijklmnop",
      () => NOW,
    );
    // 真实存储层保证 approved 状态一定带规范到期日；这里在读取口模拟旧数据、异常数据，
    // 证明到期日问题只会让券面少一行，绝不会让整张退款券打不出来。
    const withExpiry = (expiresAtIso: unknown) =>
      new SqliteRefundVoucherPrintMaterial(connection, {
        async getByAttempt(attemptId) {
          const state = await store.getByAttempt(attemptId);
          return state === null
            ? null
            : { ...state, expiresAtIso: expiresAtIso as string | null };
        },
      });
    const base = {
      returnOrderGuid: "return-order-1",
      voucherCode: "REFUND-VOUCHER-001",
      refundAmountCents: 500,
    };

    assert.deepEqual(
      await withExpiry(EXPIRES).resolveApprovedRefundVouchers(
        "return-action-1",
        "return-order-1",
      ),
      [{ ...base, expiresAtIso: EXPIRES }],
    );
    for (const missing of [
      null,
      undefined,
      "",
      "not-a-date",
      "2027-07-28T00:00:00+00:00",
      20270728,
    ]) {
      assert.deepEqual(
        await withExpiry(missing).resolveApprovedRefundVouchers(
          "return-action-1",
          "return-order-1",
        ),
        [base],
        `expiresAtIso=${JSON.stringify(missing)}`,
      );
    }
  });
});

test("退货首次打印端到端：真实 SQLite 材料 + 渲染器印出受保护状态里的到期日与使用说明", async () => {
  await withFixture(async ({ adapter }) => {
    const renderWith = async (businessTimeZone: string | undefined): Promise<string> => {
      const renderer = new ProtectedRefundVoucherReceiptRenderer(
        { async getByGuid() { return returnOrderForRender(); } },
        adapter,
        {
          async getFrozenReturnReceiptSettings() {
            return {
              printerId: "printer-1",
              paper: "58mm",
              locale: "en",
              store: { brandName: "Hot Bargain", storeName: "Main", address: "", phone: "", abn: "", returnPolicy: "" },
            };
          },
        },
        () => new Date(2026, 6, 28, 9, 0, 0),
        businessTimeZone,
      );
      return new TextDecoder().decode(
        (await renderer.render("return-action-1", "return-order-1")).receiptBytes,
      );
    };

    // EXPIRES = 2027-07-28T00:00:00Z：布里斯班(+10) 是 7 月 28 日 10:00；洛杉矶(-7) 还停在 7 月 27 日。
    const brisbane = await renderWith("Australia/Brisbane");
    assert.match(brisbane, /Voucher: REFUND-VOUCHER-001/u);
    assert.match(brisbane, /Valid until: 2027-07-28/u);
    assert.match(brisbane, /VOUCHER TERMS/u);
    assert.match(await renderWith(undefined), /Valid until: 2027-07-28/u);
    assert.match(await renderWith("America/Los_Angeles"), /Valid until: 2027-07-27/u);
  });
});

test("刷卡 + 券混合退款首次打印：退货小票之后追加带到期日与使用说明的券面（真实 SQLite 材料）", async () => {
  await withMixedFixture(
    {
      receiptKind: "refund-receipt",
      allocations: [
        { method: "voucher", amountCents: 250, capacityMethod: "card" },
        { method: "card", amountCents: 750, capacityMethod: "card" },
      ],
    },
    async ({ adapter }) => {
      const base = returnOrderForRender();
      const order: LocalOrder = {
        ...base,
        total: { currency: "AUD", cents: -1_000 },
        actualAmount: { currency: "AUD", cents: -1_000 },
        lines: [{
          ...base.lines[0]!,
          unitPrice: { currency: "AUD", cents: 1_000 },
          actualAmount: { currency: "AUD", cents: -1_000 },
        }],
        tenders: [
          {
            tenderGuid: "mixed-tender-0",
            method: "voucher",
            amount: { currency: "AUD", cents: -250 },
            reference: null,
            reservationToken: null,
          },
          {
            tenderGuid: "mixed-tender-1",
            method: "card",
            amount: { currency: "AUD", cents: -750 },
            reference: null,
            reservationToken: null,
          },
        ],
      };
      const renderer = new ProtectedRefundVoucherReceiptRenderer(
        { async getByGuid() { return order; } },
        adapter,
        {
          async getFrozenReturnReceiptSettings() {
            return {
              printerId: "printer-1",
              paper: "80mm",
              locale: "en",
              store: { brandName: "Hot Bargain", storeName: "Main", address: "", phone: "", abn: "", returnPolicy: "" },
            };
          },
        },
        () => new Date(2026, 6, 28, 9, 0, 0),
        "Australia/Brisbane",
      );

      const rendered = await renderRefundReceiptWithVouchers({
        renderReceipt: async () => ({
          printerId: "printer-1",
          receiptBytes: new TextEncoder().encode("RETURN-RECEIPT\n"),
        }),
        hasRefundVoucherTender: async () => hasRefundVoucherTender(order),
        renderRefundVouchers: () =>
          renderer.render("return-action-1", "return-order-1"),
      });
      const text = new TextDecoder().decode(rendered.receiptBytes);

      assert.ok(text.startsWith("RETURN-RECEIPT\n"), "退货小票在前");
      // 第 0 笔 allocation 的券到期时刻是 2027-07-10T13:59:59Z（布里斯班 23:59:59 当天）。
      const voucherIndex = text.indexOf("Voucher: MIXED-VOUCHER-0");
      assert.ok(voucherIndex > "RETURN-RECEIPT\n".length, "券面追加在退货小票之后");
      assert.ok(text.indexOf("Valid until: 2027-07-10") > voucherIndex);
      assert.ok(text.indexOf("VOUCHER TERMS") > text.indexOf("Valid until: 2027-07-10"));
      assert.equal(text.match(/Valid until/gu)?.length, 1);
    },
  );
});

function returnOrderForRender(): LocalOrder {
  return {
    orderGuid: "return-order-1",
    localSequence: 1,
    storeCode: "S1",
    deviceCode: "IPAD-1",
    cashierId: "cashier-1",
    cashierName: "Cashier",
    soldAtIso: NOW,
    state: "PendingSync",
    total: { currency: "AUD", cents: -500 },
    discount: { currency: "AUD", cents: 0 },
    actualAmount: { currency: "AUD", cents: -500 },
    originalOrderGuid: "original-order-1",
    lines: [
      {
        lineId: "return-line-1",
        productCode: "P1",
        itemNumber: null,
        lookupCode: "P1",
        displayName: "Returned product",
        quantity: "1",
        unitPrice: { currency: "AUD", cents: 500 },
        discount: { currency: "AUD", cents: 0 },
        actualAmount: { currency: "AUD", cents: -500 },
        priceSource: "catalog",
        kind: "return",
        returnSourceKey: "return-source-1",
        originalOrderGuid: "original-order-1",
        originalOrderDetailGuid: "original-detail-1",
      },
    ],
    tenders: [
      {
        tenderGuid: "voucher-tender-1",
        method: "voucher",
        amount: { currency: "AUD", cents: -500 },
        reference: null,
        reservationToken: null,
      },
    ],
  };
}

test("缺少 action/fulfilment 绑定时不得只凭 returnOrderGuid 恢复券码", async () => {
  await withUnboundFixture(async ({ adapter }) => {
    assert.equal(
      await adapter.resolveApprovedRefundVouchers(
        "return-action-1",
        "return-order-1",
      ),
      null,
    );
  });
});

test("fulfilment plan 的 action 与订单交叉绑定时不得恢复券码", async () => {
  await withUnboundFixture(async ({ adapter, connection }) => {
    await seedCrossBoundFulfilmentPlan(connection);
    assert.equal(
      await adapter.resolveApprovedRefundVouchers(
        "return-action-1",
        "return-order-1",
      ),
      null,
    );
  });
});

test("关系不唯一、非终态、金额或受保护上下文换绑时失败关闭", async (t) => {
  await t.test("订单 tender 合计与实退金额不符", async () => {
    await withFixture(async ({ adapter, connection }) => {
      await connection.run(
        `INSERT INTO order_tenders (
          tender_guid, order_guid, method, amount_cents,
          payment_attempt_id, created_at_iso
        ) VALUES ('cash-extra', 'return-order-1', 'cash', -1, NULL, ?)`,
        [NOW],
      );
      assert.equal(
        await adapter.resolveApprovedRefundVouchers(
          "return-action-1",
          "return-order-1",
        ),
        null,
      );
    });
  });

  await t.test("attempt 非 Approved", async () => {
    await withFixture(async ({ adapter, connection }) => {
      await connection.run(
        "UPDATE payment_attempts SET state = 'Pending' WHERE attempt_id = ?",
        ["voucher-attempt-1"],
      );
      assert.equal(
        await adapter.resolveApprovedRefundVouchers(
          "return-action-1",
          "return-order-1",
        ),
        null,
      );
    });
  });

  await t.test("同单存在第二个 Approved voucher refund attempt", async () => {
    await withFixture(async ({ adapter, connection }) => {
      await connection.run(
        `INSERT INTO payment_attempts (
          attempt_id, idempotency_key, order_guid, provider, operation,
          amount_cents, state, checkout_id, payment_id, session_id,
          txn_ref, rfn, provider_payload_ciphertext,
          provider_receipt_ciphertext, provider_response_code,
          created_at_iso, updated_at_iso, last_error_code
        ) VALUES (
          'voucher-attempt-2', 'voucher-idem-2', 'return-order-1',
          'voucher', 'refund', -500, 'Approved',
          NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, ?, ?, NULL
        )`,
        [NOW, NOW],
      );
      assert.equal(
        await adapter.resolveApprovedRefundVouchers(
          "return-action-1",
          "return-order-1",
        ),
        null,
      );
    });
  });

  await t.test("订单未完成或明细不是退货", async () => {
    await withFixture(async ({ adapter, connection }) => {
      await connection.run(
        "UPDATE local_orders SET state = 'Draft' WHERE order_guid = ?",
        ["return-order-1"],
      );
      assert.equal(
        await adapter.resolveApprovedRefundVouchers(
          "return-action-1",
          "return-order-1",
        ),
        null,
      );
      await connection.run(
        "UPDATE local_orders SET state = 'PendingSync' WHERE order_guid = ?",
        ["return-order-1"],
      );
      await connection.run(
        "UPDATE local_order_lines SET line_kind = 'sale' WHERE order_guid = ?",
        ["return-order-1"],
      );
      assert.equal(
        await adapter.resolveApprovedRefundVouchers(
          "return-action-1",
          "return-order-1",
        ),
        null,
      );
    });
  });

  await t.test("tender 与 attempt 金额不一致", async () => {
    await withFixture(async ({ adapter, connection }) => {
      await connection.run(
        "UPDATE order_tenders SET amount_cents = -499 WHERE tender_guid = ?",
        ["voucher-tender-1"],
      );
      assert.equal(
        await adapter.resolveApprovedRefundVouchers(
          "return-action-1",
          "return-order-1",
        ),
        null,
      );
    });
  });

  await t.test("密文内 store 换绑", async () => {
    await withFixture(async ({ adapter, connection }) => {
      await replaceProtectedState(connection, {
        storeCode: "OTHER-STORE",
      });
      assert.equal(
        await adapter.resolveApprovedRefundVouchers(
          "return-action-1",
          "return-order-1",
        ),
        null,
      );
    });
  });

  await t.test("密文内 cashier 换绑", async () => {
    await withFixture(async ({ adapter, connection }) => {
      await replaceProtectedState(connection, {
        cashierId: "other-cashier",
      });
      assert.equal(
        await adapter.resolveApprovedRefundVouchers(
          "return-action-1",
          "return-order-1",
        ),
        null,
      );
    });
  });

  await t.test("控制字符券码", async () => {
    await withFixture(async ({ connection }) => {
      const tokens = {
        async getByAttempt(): Promise<VoucherProtectedAttemptState> {
          return protectedState({ voucherCode: "BAD\u001bCODE" });
        },
      };
      const adapter = new SqliteRefundVoucherPrintMaterial(
        connection,
        tokens,
      );
      assert.equal(
        await adapter.resolveApprovedRefundVouchers(
          "return-action-1",
          "return-order-1",
        ),
        null,
      );
    });
  });

  await t.test("缺少保护材料", async () => {
    await withDatabase(async (connection) => {
      await seedPublicReturn(connection);
      await seedValidReturnIdentity(connection);
      const adapter = new SqliteRefundVoucherPrintMaterial(connection, {
        async getByAttempt() {
          return null;
        },
      });
      assert.equal(
        await adapter.resolveApprovedRefundVouchers(
          "return-action-1",
          "return-order-1",
        ),
        null,
      );
    });
  });
});

test("礼券代替刷卡/现金额度签发的退款券同样可恢复；分期或带 context 的现金额度失败关闭", async (t) => {
  const cases = [
    { name: "代替刷卡额度", method: "card", keepContext: true, expected: true },
    { name: "代替现金额度（无 context）", method: "cash", keepContext: false, expected: true },
    { name: "现金额度却带 context", method: "cash", keepContext: true, expected: false },
    { name: "分期额度", method: "installment", keepContext: true, expected: false },
  ] as const;
  for (const testCase of cases) {
    await t.test(testCase.name, async () => {
      await withFixture(async ({ adapter }) => {
        const resolved = await adapter.resolveApprovedRefundVouchers(
          "return-action-1",
          "return-order-1",
        );
        assert.equal(resolved !== null, testCase.expected);
        if (testCase.expected) {
          assert.equal(resolved?.[0]?.voucherCode, "REFUND-VOUCHER-001");
        }
      }, { method: testCase.method, context: testCase.keepContext });
    });
  }
});

test("已解密 JSON/绑定损坏使用 typed integrity error", async (t) => {
  await t.test("JSON 损坏", async () => {
    await withFixture(async ({ adapter, connection }) => {
      await connection.run(
        `UPDATE voucher_protected_attempt_states
         SET state_ciphertext = ?
         WHERE attempt_id = ?`,
        [await encryptor.encrypt("{broken-json"), "voucher-attempt-1"],
      );
      await assert.rejects(
        () => adapter.resolveApprovedRefundVouchers(
          "return-action-1",
          "return-order-1",
        ),
        (error: unknown) =>
          error instanceof ProtectedMaterialIntegrityError &&
          error.code === "PROTECTED_MATERIAL_JSON_INVALID",
      );
    });
  });

  await t.test("明文绑定换绑", async () => {
    await withFixture(async ({ adapter, connection }) => {
      await connection.exec(
        "DROP TRIGGER trg_voucher_protected_state_binding_immutable",
      );
      await connection.run(
        `UPDATE voucher_protected_attempt_states
         SET idempotency_key = ?
         WHERE attempt_id = ?`,
        ["tampered-idempotency", "voucher-attempt-1"],
      );
      await assert.rejects(
        () => adapter.resolveApprovedRefundVouchers(
          "return-action-1",
          "return-order-1",
        ),
        (error: unknown) =>
          error instanceof ProtectedMaterialIntegrityError &&
          error.code === "PROTECTED_MATERIAL_BINDING_MISMATCH",
      );
    });
  });
});

test("数据库与解密错误保持原对象透传", async (t) => {
  await t.test("数据库错误", async () => {
    const expected = new Error("database unavailable");
    const adapter = new SqliteRefundVoucherPrintMaterial(
      {
        exec: async () => undefined,
        run: async () => ({ changes: 0, lastInsertRowId: 0 }),
        getFirst: async () => null,
        async getAll() {
          throw expected;
        },
        withExclusiveTransaction: async (operation) =>
          operation(this as never),
        close: async () => undefined,
      },
      { getByAttempt: async () => null },
    );
    await assert.rejects(
      () => adapter.resolveApprovedRefundVouchers(
        "return-action-1",
        "return-order-1",
      ),
      (error: unknown) => error === expected,
    );
  });

  await t.test("解密错误", async () => {
    await withDatabase(async (connection) => {
      await seedPublicReturn(connection);
      await seedValidReturnIdentity(connection);
      await connection.run(
        `INSERT INTO voucher_protected_attempt_states (
          protected_reference, attempt_id, idempotency_key, order_guid,
          state_ciphertext, created_at_iso, updated_at_iso
        ) VALUES (?, ?, ?, ?, ?, ?, ?)`,
        [
          "vpr_abcdefghijklmnop",
          "voucher-attempt-1",
          "voucher-idem-1",
          "return-order-1",
          new Uint8Array([1]),
          NOW,
          NOW,
        ],
      );
      const expected = new Error("keychain locked");
      const adapter = new SqliteRefundVoucherPrintMaterial(connection, {
        async getByAttempt() {
          throw expected;
        },
      });
      await assert.rejects(
        () => adapter.resolveApprovedRefundVouchers(
          "return-action-1",
          "return-order-1",
        ),
        (error: unknown) => error === expected,
      );
    });
  });
});

test("混合退款：现金/刷卡 + 退款券、多张券时每笔已批准券 allocation 各返回一份材料", async (t) => {
  const cases: readonly Readonly<{
    name: string;
    receiptKind: "refund-voucher" | "refund-receipt";
    allocations: readonly MixedAllocationSeed[];
  }>[] = [
    {
      name: "现金 + 券",
      receiptKind: "refund-voucher",
      allocations: [
        { method: "cash", amountCents: 300, capacityMethod: "cash" },
        { method: "voucher", amountCents: 700, capacityMethod: "voucher" },
      ],
    },
    {
      name: "刷卡 + 券（退货小票后追加券面）",
      receiptKind: "refund-receipt",
      allocations: [
        { method: "voucher", amountCents: 250, capacityMethod: "card" },
        { method: "card", amountCents: 750, capacityMethod: "card" },
      ],
    },
    {
      name: "多张券",
      receiptKind: "refund-voucher",
      allocations: [
        { method: "voucher", amountCents: 400, capacityMethod: "voucher" },
        { method: "voucher", amountCents: 600, capacityMethod: "cash" },
      ],
    },
  ];
  for (const testCase of cases) {
    await t.test(testCase.name, async () => {
      await withMixedFixture(
        { receiptKind: testCase.receiptKind, allocations: testCase.allocations },
        async ({ adapter }) => {
          const expected = testCase.allocations.flatMap((allocation, index) =>
            allocation.method === "voucher"
              ? [{
                  returnOrderGuid: "return-order-1",
                  voucherCode: `MIXED-VOUCHER-${index}`,
                  refundAmountCents: allocation.amountCents,
                  // 每张券各带自己受保护状态里的到期时刻（各不相同，防止串券）。
                  expiresAtIso: mixedExpiry(index),
                }]
              : []);
          assert.deepEqual(
            await adapter.resolveApprovedRefundVouchers(
              "return-action-1",
              "return-order-1",
            ),
            expected,
          );
        },
      );
    });
  }
});

test("混合退款任一券绑定缺失、不唯一或与履约策略不符时整体失败关闭", async (t) => {
  const twoVouchers: readonly MixedAllocationSeed[] = [
    { method: "cash", amountCents: 200, capacityMethod: "cash" },
    { method: "voucher", amountCents: 300, capacityMethod: "voucher" },
    { method: "voucher", amountCents: 500, capacityMethod: "cash" },
  ];

  await t.test("第二张券缺少 tender 绑定", async () => {
    await withMixedFixture(
      { receiptKind: "refund-voucher", allocations: twoVouchers, skipBindingIndex: 2 },
      async ({ adapter }) => {
        assert.equal(
          await adapter.resolveApprovedRefundVouchers(
            "return-action-1",
            "return-order-1",
          ),
          null,
        );
      },
    );
  });

  await t.test("第二张券缺少受保护状态", async () => {
    await withMixedFixture(
      { receiptKind: "refund-voucher", allocations: twoVouchers, skipProtectedIndex: 2 },
      async ({ adapter }) => {
        assert.equal(
          await adapter.resolveApprovedRefundVouchers(
            "return-action-1",
            "return-order-1",
          ),
          null,
        );
      },
    );
  });

  await t.test("券 attempt 非 Approved", async () => {
    await withMixedFixture(
      { receiptKind: "refund-voucher", allocations: twoVouchers },
      async ({ adapter, connection }) => {
        await connection.run(
          "UPDATE payment_attempts SET state = 'Pending' WHERE attempt_id = ?",
          ["mixed-attempt-2"],
        );
        assert.equal(
          await adapter.resolveApprovedRefundVouchers(
            "return-action-1",
            "return-order-1",
          ),
          null,
        );
      },
    );
  });

  await t.test("含刷卡却标为纯券面", async () => {
    await withMixedFixture(
      {
        receiptKind: "refund-voucher",
        allocations: [
          { method: "voucher", amountCents: 250, capacityMethod: "card" },
          { method: "card", amountCents: 750, capacityMethod: "card" },
        ],
      },
      async ({ adapter }) => {
        assert.equal(
          await adapter.resolveApprovedRefundVouchers(
            "return-action-1",
            "return-order-1",
          ),
          null,
        );
      },
    );
  });

  await t.test("不含刷卡却标为退货小票", async () => {
    await withMixedFixture(
      { receiptKind: "refund-receipt", allocations: twoVouchers },
      async ({ adapter }) => {
        assert.equal(
          await adapter.resolveApprovedRefundVouchers(
            "return-action-1",
            "return-order-1",
          ),
          null,
        );
      },
    );
  });

  await t.test("没有任何券 allocation", async () => {
    await withMixedFixture(
      {
        receiptKind: "refund-receipt",
        allocations: [
          { method: "card", amountCents: 1000, capacityMethod: "card" },
        ],
      },
      async ({ adapter }) => {
        assert.equal(
          await adapter.resolveApprovedRefundVouchers(
            "return-action-1",
            "return-order-1",
          ),
          null,
        );
      },
    );
  });

  await t.test("订单另有未绑定的 voucher tender", async () => {
    await withMixedFixture(
      {
        receiptKind: "refund-voucher",
        allocations: [
          { method: "cash", amountCents: 300, capacityMethod: "cash" },
          { method: "voucher", amountCents: 700, capacityMethod: "voucher" },
        ],
      },
      async ({ adapter, connection }) => {
        // 把现金 tender 改成 voucher：金额合计不变，但券 tender 数与券 allocation 数不一致。
        await connection.run(
          "UPDATE order_tenders SET method = 'voucher' WHERE tender_guid = ?",
          ["mixed-tender-0"],
        );
        assert.equal(
          await adapter.resolveApprovedRefundVouchers(
            "return-action-1",
            "return-order-1",
          ),
          null,
        );
      },
    );
  });
});

type MixedAllocationSeed = Readonly<{
  method: "cash" | "card" | "voucher";
  amountCents: number;
  capacityMethod: "cash" | "card" | "voucher";
}>;

/**
 * 混合退款夹具：每笔 allocation 一条 tender；券 allocation 额外有 Approved voucher refund
 * attempt 与受保护状态，非券 allocation 走 hbpos-api（现金）或 payment-provider（刷卡）绑定。
 */
async function withMixedFixture(
  spec: Readonly<{
    receiptKind: "refund-voucher" | "refund-receipt";
    allocations: readonly MixedAllocationSeed[];
    skipBindingIndex?: number;
    skipProtectedIndex?: number;
  }>,
  operation: (fixture: Readonly<{
    connection: SqliteConnectionPort;
    adapter: SqliteRefundVoucherPrintMaterial;
  }>) => Promise<void>,
): Promise<void> {
  await withDatabase(async (connection) => {
    const totalCents = spec.allocations.reduce(
      (sum, allocation) => sum + allocation.amountCents,
      0,
    );
    await connection.run(
      `INSERT INTO local_orders (
        order_guid, local_sequence, store_code, device_code,
        cashier_id, cashier_name, sold_at_iso, state,
        total_cents, discount_cents, actual_amount_cents,
        original_order_guid, created_at_iso, updated_at_iso
      ) VALUES (
        'return-order-1', 1, 'S1', 'IPAD-1', 'cashier-1', 'Cashier',
        ?, 'PendingSync', ?, 0, ?, 'original-order-1', ?, ?
      )`,
      [NOW, -totalCents, -totalCents, NOW, NOW],
    );
    await connection.run(
      `INSERT INTO local_order_lines (
        line_id, order_guid, line_sequence, product_code, item_number,
        lookup_code, display_name, quantity, unit_price_cents,
        discount_cents, actual_amount_cents, price_source, line_kind,
        return_source_key, original_order_guid, original_order_detail_guid,
        reference_code, sync_price_source
      ) VALUES (
        'return-line-1', 'return-order-1', 1, 'P1', NULL,
        'P1', 'Returned product', '1', ?, 0, ?,
        'catalog', 'return', 'return-source-1',
        'original-order-1', 'original-detail-1', 'REF-P1', 2
      )`,
      [totalCents, -totalCents],
    );
    await connection.run(
      `INSERT INTO return_actions (
        action_id, request_fingerprint, return_order_guid,
        action_recovery_token, source_kind, total_refund_cents, online,
        store_code, device_code, cashier_id, cashier_name, session_epoch,
        supervisor_grant_id, plan_json, state, created_at_iso,
        completed_at_iso, updated_at_iso
      ) VALUES (
        'return-action-1', 'fingerprint-1', 'return-order-1',
        'recovery-1', 'receipt', ?, 1,
        'S1', 'IPAD-1', 'cashier-1', 'Cashier', 'session-1',
        NULL, '{}', 'completed', ?, ?, ?
      )`,
      [totalCents, NOW, NOW, NOW],
    );
    let protectedSequence = 0;
    const tokens = new SqliteVoucherProtectedTokenStore(
      connection,
      encryptor,
      () => `vpr_mixed_reference_${String.fromCharCode(97 + protectedSequence++)}`,
      () => NOW,
    );
    for (const [index, allocation] of spec.allocations.entries()) {
      const attemptId = `mixed-attempt-${index}`;
      const tenderGuid = `mixed-tender-${index}`;
      const allocationId = `mixed-allocation-${index}`;
      const capacityId = `mixed-capacity-${index}`;
      const externalId = `mixed-external-${index}`;
      const viaProvider = allocation.method !== "cash";
      await connection.run(
        `INSERT INTO return_tender_capacities (
          capacity_id, original_order_guid, method,
          original_amount_cents, remaining_amount_cents,
          protected_context_ciphertext, observed_at_iso,
          created_at_iso, updated_at_iso
        ) VALUES (?, 'original-order-1', ?, ?, 0, ?, ?, ?, ?)`,
        [
          capacityId,
          allocation.capacityMethod,
          allocation.amountCents,
          allocation.capacityMethod === "cash" ? null : new Uint8Array([1]),
          NOW,
          NOW,
          NOW,
        ],
      );
      if (viaProvider) {
        await connection.run(
          `INSERT INTO payment_attempts (
            attempt_id, idempotency_key, order_guid, provider, operation,
            amount_cents, state, checkout_id, payment_id, session_id,
            txn_ref, rfn, provider_payload_ciphertext,
            provider_receipt_ciphertext, provider_response_code,
            created_at_iso, updated_at_iso, last_error_code
          ) VALUES (
            ?, ?, 'return-order-1', ?, 'refund', ?, 'Approved',
            NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, ?, ?, NULL
          )`,
          [
            attemptId,
            `mixed-idem-${index}`,
            allocation.method === "voucher" ? "voucher" : "square",
            -allocation.amountCents,
            NOW,
            NOW,
          ],
        );
      }
      await connection.run(
        `INSERT INTO order_tenders (
          tender_guid, order_guid, method, amount_cents,
          payment_attempt_id, created_at_iso
        ) VALUES (?, 'return-order-1', ?, ?, ?, ?)`,
        [
          tenderGuid,
          allocation.method,
          -allocation.amountCents,
          viaProvider ? attemptId : null,
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
        ) VALUES (
          'return-action-1', ?, ?, 'online-refund',
          ?, ?, ?, 'original-order-1',
          NULL, NULL, ?, ?, ?, ?, 'completed', NULL,
          'Committed', ?, ?
        )`,
        [
          allocationId,
          index,
          allocation.method,
          -allocation.amountCents,
          capacityId,
          externalId,
          viaProvider ? "payment-provider" : "hbpos-api",
          externalId,
          viaProvider ? attemptId : `mixed-api-${index}`,
          NOW,
          NOW,
        ],
      );
      if (spec.skipBindingIndex !== index) {
        await connection.run(
          `INSERT INTO return_tender_attempt_bindings (
            tender_guid, action_id, allocation_id, external_attempt_kind,
            external_action_id, durable_attempt_id, created_at_iso
          ) VALUES (?, 'return-action-1', ?, ?, ?, ?, ?)`,
          [
            tenderGuid,
            allocationId,
            viaProvider ? "payment-provider" : "hbpos-api",
            externalId,
            viaProvider ? attemptId : `mixed-api-${index}`,
            NOW,
          ],
        );
      }
      if (allocation.method === "voucher" && spec.skipProtectedIndex !== index) {
        await tokens.save(protectedState({
          attemptId,
          idempotencyKey: `mixed-idem-${index}`,
          voucherCode: `MIXED-VOUCHER-${index}`,
          amountCents: -allocation.amountCents,
          expiresAtIso: mixedExpiry(index),
        }));
      }
    }
    await connection.run(
      `INSERT INTO return_fulfilment_plans (
        action_id, return_order_guid, print_job_id, drawer_event_id,
        receipt_kind, print_receipt, drawer_required,
        materialized_at_iso, created_at_iso
      ) VALUES (
        'return-action-1', 'return-order-1', 'print-return-action-1', ?,
        ?, 1, ?, NULL, ?
      )`,
      [
        spec.allocations.some((allocation) => allocation.method === "cash")
          ? "drawer-return-action-1"
          : null,
        spec.receiptKind,
        spec.allocations.some((allocation) => allocation.method === "cash") ? 1 : 0,
        NOW,
      ],
    );
    await operation(Object.freeze({
      connection,
      adapter: new SqliteRefundVoucherPrintMaterial(connection, tokens),
    }));
  });
}

async function withFixture(
  operation: (fixture: Readonly<{
    connection: SqliteConnectionPort;
    adapter: SqliteRefundVoucherPrintMaterial;
  }>) => Promise<void>,
  capacity: CapacitySeed = { method: "voucher", context: true },
): Promise<void> {
  await withUnboundFixture(async (fixture) => {
    await seedValidReturnIdentity(fixture.connection, capacity);
    await operation(fixture);
  });
}

async function withUnboundFixture(
  operation: (fixture: Readonly<{
    connection: SqliteConnectionPort;
    adapter: SqliteRefundVoucherPrintMaterial;
  }>) => Promise<void>,
): Promise<void> {
  await withDatabase(async (connection) => {
    await seedPublicReturn(connection);
    const tokens = new SqliteVoucherProtectedTokenStore(
      connection,
      encryptor,
      () => "vpr_abcdefghijklmnop",
      () => NOW,
    );
    await tokens.save(protectedState());
    await operation(Object.freeze({
      connection,
      adapter: new SqliteRefundVoucherPrintMaterial(connection, tokens),
    }));
  });
}

async function withDatabase(
  operation: (connection: SqliteConnectionPort) => Promise<void>,
): Promise<void> {
  const connection = new NodeSqliteConnection();
  try {
    await applyMigrations(connection, () => NOW);
    await operation(connection);
  } finally {
    await connection.close();
  }
}

async function seedPublicReturn(
  connection: SqliteConnectionPort,
): Promise<void> {
  await connection.run(
    `INSERT INTO local_orders (
      order_guid, local_sequence, store_code, device_code,
      cashier_id, cashier_name, sold_at_iso, state,
      total_cents, discount_cents, actual_amount_cents,
      original_order_guid, created_at_iso, updated_at_iso
    ) VALUES (
      'return-order-1', 1, 'S1', 'IPAD-1', 'cashier-1', 'Cashier',
      ?, 'PendingSync', -500, 0, -500, 'original-order-1', ?, ?
    )`,
    [NOW, NOW, NOW],
  );
  await connection.run(
    `INSERT INTO local_order_lines (
      line_id, order_guid, line_sequence, product_code, item_number,
      lookup_code, display_name, quantity, unit_price_cents,
      discount_cents, actual_amount_cents, price_source, line_kind,
      return_source_key, original_order_guid, original_order_detail_guid,
      reference_code, sync_price_source
    ) VALUES (
      'return-line-1', 'return-order-1', 1, 'P1', NULL,
      'P1', 'Returned product', '1', 500, 0, -500,
      'catalog', 'return', 'return-source-1',
      'original-order-1', 'original-detail-1', 'REF-P1', 2
    )`,
  );
  await connection.run(
    `INSERT INTO payment_attempts (
      attempt_id, idempotency_key, order_guid, provider, operation,
      amount_cents, state, checkout_id, payment_id, session_id,
      txn_ref, rfn, provider_payload_ciphertext,
      provider_receipt_ciphertext, provider_response_code,
      created_at_iso, updated_at_iso, last_error_code
    ) VALUES (
      'voucher-attempt-1', 'voucher-idem-1', 'return-order-1',
      'voucher', 'refund', -500, 'Approved',
      NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, ?, ?, NULL
    )`,
    [NOW, NOW],
  );
  await connection.run(
    `INSERT INTO order_tenders (
      tender_guid, order_guid, method, amount_cents,
      payment_attempt_id, created_at_iso
    ) VALUES (
      'voucher-tender-1', 'return-order-1', 'voucher', -500,
      'voucher-attempt-1', ?
    )`,
    [NOW],
  );
}

async function seedCrossBoundFulfilmentPlan(
  connection: SqliteConnectionPort,
): Promise<void> {
  await connection.run(
    `INSERT INTO return_tender_capacities (
      capacity_id, original_order_guid, method,
      original_amount_cents, remaining_amount_cents,
      protected_context_ciphertext, observed_at_iso,
      created_at_iso, updated_at_iso
    ) VALUES (
      'voucher-capacity-1', 'original-order-1', 'voucher',
      500, 0, ?, ?, ?, ?
    )`,
    [new Uint8Array([1]), NOW, NOW, NOW],
  );
  await connection.run(
    `INSERT INTO return_actions (
      action_id, request_fingerprint, return_order_guid,
      action_recovery_token, source_kind, total_refund_cents, online,
      store_code, device_code, cashier_id, cashier_name, session_epoch,
      supervisor_grant_id, plan_json, state, created_at_iso,
      completed_at_iso, updated_at_iso
    ) VALUES (
      'return-action-1', 'fingerprint-1', 'different-return-order',
      'recovery-1', 'receipt', 500, 1,
      'S1', 'IPAD-1', 'cashier-1', 'Cashier', 'session-1',
      NULL, '{}', 'completed', ?, ?, ?
    )`,
    [NOW, NOW, NOW],
  );
  await connection.run(
    `INSERT INTO return_action_allocations (
      action_id, allocation_id, allocation_index, execution_kind,
      method, signed_amount_cents, capacity_id, original_order_guid,
      offline_evidence_id, offline_evidence_remaining_cents,
      external_attempt_id, external_attempt_kind, external_action_id,
      durable_attempt_id, status, protected_recovery_ciphertext,
      capacity_reservation_state, created_at_iso, updated_at_iso
    ) VALUES (
      'return-action-1', 'voucher-allocation-1', 0, 'online-refund',
      'voucher', -500, 'voucher-capacity-1', 'original-order-1',
      NULL, NULL, 'voucher-external-1', 'payment-provider',
      'voucher-external-1', 'voucher-attempt-1', 'completed', NULL,
      'Committed', ?, ?
    )`,
    [NOW, NOW],
  );
  await connection.run(
    `INSERT INTO return_tender_attempt_bindings (
      tender_guid, action_id, allocation_id, external_attempt_kind,
      external_action_id, durable_attempt_id, created_at_iso
    ) VALUES (
      'voucher-tender-1', 'return-action-1', 'voucher-allocation-1',
      'payment-provider', 'voucher-external-1', 'voucher-attempt-1', ?
    )`,
    [NOW],
  );
  // 仅用于模拟升级前已经存在的损坏行；生产写入由 M16 触发器直接拒绝。
  await connection.exec(
    "DROP TRIGGER trg_return_fulfilment_plan_action_order_insert;",
  );
  await connection.run(
    `INSERT INTO return_fulfilment_plans (
      action_id, return_order_guid, print_job_id, drawer_event_id,
      receipt_kind, print_receipt, drawer_required,
      materialized_at_iso, created_at_iso
    ) VALUES (
      'return-action-1', 'return-order-1', 'print-return-action-1', NULL,
      'refund-voucher', 1, 0, NULL, ?
    )`,
    [NOW],
  );
}

type CapacitySeed = Readonly<{
  method: "voucher" | "card" | "cash" | "installment";
  context: boolean;
}>;

async function seedValidReturnIdentity(
  connection: SqliteConnectionPort,
  capacity: CapacitySeed = { method: "voucher", context: true },
): Promise<void> {
  await connection.run(
    `INSERT INTO return_tender_capacities (
      capacity_id, original_order_guid, method,
      original_amount_cents, remaining_amount_cents,
      protected_context_ciphertext, observed_at_iso,
      created_at_iso, updated_at_iso
    ) VALUES (
      'voucher-capacity-1', 'original-order-1', ?,
      500, 0, ?, ?, ?, ?
    )`,
    [capacity.method, capacity.context ? new Uint8Array([1]) : null, NOW, NOW, NOW],
  );
  await connection.run(
    `INSERT INTO return_actions (
      action_id, request_fingerprint, return_order_guid,
      action_recovery_token, source_kind, total_refund_cents, online,
      store_code, device_code, cashier_id, cashier_name, session_epoch,
      supervisor_grant_id, plan_json, state, created_at_iso,
      completed_at_iso, updated_at_iso
    ) VALUES (
      'return-action-1', 'fingerprint-1', 'return-order-1',
      'recovery-1', 'receipt', 500, 1,
      'S1', 'IPAD-1', 'cashier-1', 'Cashier', 'session-1',
      NULL, '{}', 'completed', ?, ?, ?
    )`,
    [NOW, NOW, NOW],
  );
  await connection.run(
    `INSERT INTO return_action_allocations (
      action_id, allocation_id, allocation_index, execution_kind,
      method, signed_amount_cents, capacity_id, original_order_guid,
      offline_evidence_id, offline_evidence_remaining_cents,
      external_attempt_id, external_attempt_kind, external_action_id,
      durable_attempt_id, status, protected_recovery_ciphertext,
      capacity_reservation_state, created_at_iso, updated_at_iso
    ) VALUES (
      'return-action-1', 'voucher-allocation-1', 0, 'online-refund',
      'voucher', -500, 'voucher-capacity-1', 'original-order-1',
      NULL, NULL, 'voucher-external-1', 'payment-provider',
      'voucher-external-1', 'voucher-attempt-1', 'completed', NULL,
      'Committed', ?, ?
    )`,
    [NOW, NOW],
  );
  await connection.run(
    `INSERT INTO return_tender_attempt_bindings (
      tender_guid, action_id, allocation_id, external_attempt_kind,
      external_action_id, durable_attempt_id, created_at_iso
    ) VALUES (
      'voucher-tender-1', 'return-action-1', 'voucher-allocation-1',
      'payment-provider', 'voucher-external-1', 'voucher-attempt-1', ?
    )`,
    [NOW],
  );
  await connection.run(
    `INSERT INTO return_fulfilment_plans (
      action_id, return_order_guid, print_job_id, drawer_event_id,
      receipt_kind, print_receipt, drawer_required,
      materialized_at_iso, created_at_iso
    ) VALUES (
      'return-action-1', 'return-order-1', 'print-return-action-1', NULL,
      'refund-voucher', 1, 0, NULL, ?
    )`,
    [NOW],
  );
}

function protectedState(
  overrides: Partial<VoucherProtectedAttemptState> = {},
): VoucherProtectedAttemptState {
  return {
    protectedReference: "vpr_abcdefghijklmnop",
    attemptId: "voucher-attempt-1",
    idempotencyKey: "voucher-idem-1",
    orderGuid: "return-order-1",
    operation: "refund",
    phase: "approved",
    storeCode: "S1",
    cashierId: "cashier-1",
    voucherCode: "REFUND-VOUCHER-001",
    reservationToken: null,
    amountCents: -500,
    expiresAtIso: EXPIRES,
    reason: "RETURN_REFUND",
    ...overrides,
  };
}

async function replaceProtectedState(
  connection: SqliteConnectionPort,
  overrides: Partial<VoucherProtectedAttemptState>,
): Promise<void> {
  const { protectedReference: _protectedReference, ...state } =
    protectedState(overrides);
  await connection.run(
    `UPDATE voucher_protected_attempt_states
     SET state_ciphertext = ?
     WHERE attempt_id = ?`,
    [
      await encryptor.encrypt(JSON.stringify({ version: 1, state })),
      "voucher-attempt-1",
    ],
  );
}

class NodeSqliteConnection implements SqliteConnectionPort {
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
    const result = this.database
      .prepare(sql)
      .run(...parameters.map(toSqlInput));
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
      (this.database
        .prepare(sql)
        .get(...parameters.map(toSqlInput)) as T | undefined) ?? null
    );
  }

  public async getAll<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<readonly T[]> {
    return this.database
      .prepare(sql)
      .all(...parameters.map(toSqlInput)) as T[];
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

function toSqlInput(value: SqlValue): SQLInputValue {
  return value instanceof Uint8Array ? Buffer.from(value) : value;
}

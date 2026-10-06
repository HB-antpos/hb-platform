import assert from "node:assert/strict";
import test from "node:test";

import type {
  MaterializeReturnFulfilmentInput,
  StoredReturnFulfilmentPlan,
} from "../db/sqlite-return-fulfilment-plan-store";

import {
  RETURN_CASH_DRAWER_REASON,
  renderRefundReceiptWithVouchers,
  ReturnFulfilmentRuntime,
} from "./return-fulfilment-runtime";

const T0 = "2026-07-28T00:00:00.000Z";
const T1 = "2026-07-28T00:01:00.000Z";

class InMemoryReturnFulfilmentPlanStore {
  public readonly materializeCalls: MaterializeReturnFulfilmentInput[] = [];
  public readonly failMaterialization = new Set<string>();
  private readonly plans = new Map<string, StoredReturnFulfilmentPlan>();

  public constructor(plans: readonly StoredReturnFulfilmentPlan[]) {
    for (const plan of plans) this.plans.set(plan.actionId, plan);
  }

  public async get(
    actionId: string,
  ): Promise<StoredReturnFulfilmentPlan | null> {
    return this.plans.get(actionId) ?? null;
  }

  public async listPending(
    limit = 50,
  ): Promise<readonly StoredReturnFulfilmentPlan[]> {
    return [...this.plans.values()]
      .filter((plan) => plan.materializedAtIso === null)
      .slice(0, limit);
  }

  public async materialize(
    input: MaterializeReturnFulfilmentInput,
  ): Promise<StoredReturnFulfilmentPlan> {
    this.materializeCalls.push({
      ...input,
      receiptBytes:
        input.receiptBytes === null
          ? null
          : Uint8Array.from(input.receiptBytes),
    });
    if (this.failMaterialization.has(input.actionId)) {
      throw new Error(`materialize failed: ${input.actionId}`);
    }
    const plan = this.plans.get(input.actionId);
    if (!plan) throw new Error("plan missing");
    assert.equal(input.expectedReturnOrderGuid, plan.returnOrderGuid);
    assert.equal(input.expectedPrintJobId, plan.printJobId);
    assert.equal(input.expectedDrawerEventId, plan.drawerEventId);
    if (plan.materializedAtIso !== null) return plan;
    const materialized = Object.freeze({
      ...plan,
      materializedAtIso: T1,
    });
    this.plans.set(plan.actionId, materialized);
    return materialized;
  }
}

test("完成后渲染失败保持 plan pending，不产生任何物化写入", async () => {
  const store = new InMemoryReturnFulfilmentPlanStore([plan("render-fails")]);
  const runtime = new ReturnFulfilmentRuntime({
    plans: store,
    async renderReceipt() {
      throw new Error("receipt renderer unavailable");
    },
  });

  await assert.rejects(
    () => runtime.materializeAction("render-fails"),
    /receipt renderer unavailable/,
  );
  assert.equal(store.materializeCalls.length, 0);
  assert.equal((await store.get("render-fails"))?.materializedAtIso, null);
});

test("进程重启后 drainPending 恢复同一冻结 plan，且已物化重放不再渲染", async () => {
  const store = new InMemoryReturnFulfilmentPlanStore([plan("restart")]);
  const failedRuntime = new ReturnFulfilmentRuntime({
    plans: store,
    async renderReceipt() {
      throw new Error("killed before materialization");
    },
  });
  await assert.rejects(
    () => failedRuntime.materializeAction("restart"),
    /killed before materialization/,
  );

  let renderCount = 0;
  const recoveredRuntime = new ReturnFulfilmentRuntime({
    plans: store,
    async renderReceipt(identity) {
      renderCount += 1;
      assert.deepEqual(identity, {
        actionId: "restart",
        returnOrderGuid: "return-order-restart",
        receiptKind: "refund-receipt",
      });
      return {
        printerId: "XP-RESTART",
        receiptBytes: new Uint8Array([0x1b, 0x40]),
      };
    },
  });
  assert.deepEqual(await recoveredRuntime.drainPending(), {
    materialized: 1,
    failed: 0,
    materializedActionIds: ["restart"],
    failedActionIds: [],
  });
  assert.deepEqual(await recoveredRuntime.materializeAction("restart"), {
    actionId: "restart",
    status: "already-materialized",
  });
  assert.equal(renderCount, 1);
  assert.equal(store.materializeCalls.length, 1);
});

test("现金与非现金计划使用冻结 identity，只有现金传固定 drawerReason", async () => {
  const store = new InMemoryReturnFulfilmentPlanStore([
    plan("cash", true, "none"),
    plan("card", false),
  ]);
  let renderCount = 0;
  const runtime = new ReturnFulfilmentRuntime({
    plans: store,
    async resolveDrawerPrinterId() {
      return "XP-CASH";
    },
    async renderReceipt(identity) {
      renderCount += 1;
      return {
        printerId: `printer-${identity.returnOrderGuid}`,
        receiptBytes: new Uint8Array([1, 2, 3]),
      };
    },
  });

  assert.deepEqual(await runtime.drainPending(2), {
    materialized: 2,
    failed: 0,
    materializedActionIds: ["cash", "card"],
    failedActionIds: [],
  });
  assert.equal(
    store.materializeCalls[0]?.drawerReason,
    RETURN_CASH_DRAWER_REASON,
  );
  assert.equal(store.materializeCalls[0]?.expectedDrawerEventId, "drawer-cash");
  assert.equal(store.materializeCalls[1]?.drawerReason, null);
  assert.equal(store.materializeCalls[1]?.expectedDrawerEventId, null);
  assert.equal(store.materializeCalls[0]?.expectedPrintJobId, null);
  assert.equal(store.materializeCalls[0]?.receiptBytes, null);
  assert.equal(store.materializeCalls[0]?.printerId, "XP-CASH");
  assert.equal(
    store.materializeCalls[1]?.expectedReturnOrderGuid,
    "return-order-card",
  );
  assert.equal(renderCount, 1);
});

test("打印计划把冻结的 voucher 或 receipt kind 显式传给 renderer", async () => {
  const store = new InMemoryReturnFulfilmentPlanStore([
    plan("voucher", false, "refund-voucher"),
    plan("card-receipt", false, "refund-receipt"),
  ]);
  const rendered: unknown[] = [];
  const runtime = new ReturnFulfilmentRuntime({
    plans: store,
    async renderReceipt(identity) {
      rendered.push(identity);
      return {
        printerId: "printer-return",
        receiptBytes: new Uint8Array([1]),
      };
    },
  });

  assert.deepEqual(await runtime.drainPending(), {
    materialized: 2,
    failed: 0,
    materializedActionIds: ["voucher", "card-receipt"],
    failedActionIds: [],
  });
  assert.deepEqual(rendered, [
    {
      actionId: "voucher",
      returnOrderGuid: "return-order-voucher",
      receiptKind: "refund-voucher",
    },
    {
      actionId: "card-receipt",
      returnOrderGuid: "return-order-card-receipt",
      receiptKind: "refund-receipt",
    },
  ]);
});

test("非法 receipt kind 在 renderer 之前失败关闭", async () => {
  const invalidPlan = {
    ...plan("invalid-kind", false),
    receiptKind: "voucher",
  } as unknown as StoredReturnFulfilmentPlan;
  const store = new InMemoryReturnFulfilmentPlanStore([invalidPlan]);
  let renderCount = 0;
  const runtime = new ReturnFulfilmentRuntime({
    plans: store,
    async renderReceipt() {
      renderCount += 1;
      return {
        printerId: "XP-UNUSED",
        receiptBytes: new Uint8Array([1]),
      };
    },
  });

  await assert.rejects(
    () => runtime.materializeAction("invalid-kind"),
    /plan flags are invalid/,
  );
  assert.equal(renderCount, 0);
  assert.equal(store.materializeCalls.length, 0);
});

test("drainPending 隔离渲染和 DB 单项失败，后续计划仍可完成", async () => {
  const store = new InMemoryReturnFulfilmentPlanStore([
    plan("render-error"),
    plan("store-error"),
    plan("succeeds"),
  ]);
  store.failMaterialization.add("store-error");
  const runtime = new ReturnFulfilmentRuntime({
    plans: store,
    async renderReceipt(identity) {
      if (identity.returnOrderGuid === "return-order-render-error") {
        throw new Error("render failed");
      }
      return {
        printerId: "XP-ISOLATED",
        receiptBytes: new Uint8Array([9]),
      };
    },
  });

  assert.deepEqual(await runtime.drainPending(), {
    materialized: 1,
    failed: 2,
    materializedActionIds: ["succeeds"],
    failedActionIds: ["render-error", "store-error"],
  });
  assert.equal((await store.get("render-error"))?.materializedAtIso, null);
  assert.equal((await store.get("store-error"))?.materializedAtIso, null);
  assert.equal((await store.get("succeeds"))?.materializedAtIso, T1);
});

test("空打印机、空或异常 receipt 全部 fail closed", async (context) => {
  const cases = [
    {
      name: "blank printer",
      rendered: {
        printerId: "   ",
        receiptBytes: new Uint8Array([1]),
      },
      pattern: /printer id/,
    },
    {
      name: "empty receipt",
      rendered: {
        printerId: "XP-VALID",
        receiptBytes: new Uint8Array(),
      },
      pattern: /receipt bytes/,
    },
    {
      name: "invalid receipt",
      rendered: {
        printerId: "XP-VALID",
        receiptBytes: "not-bytes",
      },
      pattern: /receipt bytes/,
    },
  ] as const;

  for (const item of cases) {
    await context.test(item.name, async () => {
      const store = new InMemoryReturnFulfilmentPlanStore([
        plan(`invalid-${item.name}`),
      ]);
      const runtime = new ReturnFulfilmentRuntime({
        plans: store,
        async renderReceipt() {
          return item.rendered as never;
        },
      });
      await assert.rejects(
        () => runtime.materializeAction(`invalid-${item.name}`),
        item.pattern,
      );
      assert.equal(store.materializeCalls.length, 0);
    });
  }
});

test("缺失 action 与非法 actionId 在渲染前拒绝", async () => {
  const store = new InMemoryReturnFulfilmentPlanStore([]);
  let renderCount = 0;
  const runtime = new ReturnFulfilmentRuntime({
    plans: store,
    async renderReceipt() {
      renderCount += 1;
      return {
        printerId: "XP-UNUSED",
        receiptBytes: new Uint8Array([1]),
      };
    },
  });

  await assert.rejects(
    () => runtime.materializeAction("missing"),
    /plan is missing/,
  );
  await assert.rejects(
    () => runtime.materializeAction("   "),
    /action id/,
  );
  assert.equal(renderCount, 0);
});

function plan(
  actionId: string,
  drawerRequired = true,
  receiptKind:
    | "none"
    | "refund-voucher"
    | "refund-receipt" = "refund-receipt",
): StoredReturnFulfilmentPlan {
  const printReceipt = receiptKind !== "none";
  return Object.freeze({
    actionId,
    returnOrderGuid: `return-order-${actionId}`,
    printJobId: printReceipt ? `print-${actionId}` : null,
    drawerEventId: drawerRequired ? `drawer-${actionId}` : null,
    receiptKind,
    printReceipt,
    drawerRequired,
    materializedAtIso: null,
    createdAtIso: T0,
  });
}

test("退货小票含退款券时把券面追加到同一打印字节；无券时只打小票；券面失败或打印机不一致整体失败", async () => {
  const receipt = { printerId: "printer-1", receiptBytes: new Uint8Array([1, 2]) };
  const vouchers = { printerId: "printer-1", receiptBytes: new Uint8Array([3, 4, 5]) };
  let voucherRenders = 0;

  const combined = await renderRefundReceiptWithVouchers({
    renderReceipt: async () => receipt,
    hasRefundVoucherTender: async () => true,
    renderRefundVouchers: async () => {
      voucherRenders += 1;
      return vouchers;
    },
  });
  assert.equal(combined.printerId, "printer-1");
  assert.deepEqual([...combined.receiptBytes], [1, 2, 3, 4, 5]);

  const receiptOnly = await renderRefundReceiptWithVouchers({
    renderReceipt: async () => receipt,
    hasRefundVoucherTender: async () => false,
    renderRefundVouchers: async () => {
      voucherRenders += 1;
      return vouchers;
    },
  });
  assert.equal(receiptOnly, receipt);
  assert.equal(voucherRenders, 1);

  await assert.rejects(
    () => renderRefundReceiptWithVouchers({
      renderReceipt: async () => receipt,
      hasRefundVoucherTender: async () => true,
      renderRefundVouchers: async () => {
        throw new Error("REFUND_VOUCHER_MATERIAL_INVALID");
      },
    }),
    /REFUND_VOUCHER_MATERIAL_INVALID/u,
  );
  await assert.rejects(
    () => renderRefundReceiptWithVouchers({
      renderReceipt: async () => receipt,
      hasRefundVoucherTender: async () => true,
      renderRefundVouchers: async () => ({ ...vouchers, printerId: "printer-2" }),
    }),
    /printer identity has diverged/u,
  );
});

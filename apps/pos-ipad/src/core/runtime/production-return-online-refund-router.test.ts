import assert from "node:assert/strict";
import test from "node:test";

import {
  ProductionReturnCashRefundAdapter,
  ProductionReturnOnlineRefundRouter,
  ReturnOnlineRefundRouterError,
} from "./production-return-online-refund-router";

import type {
  DurableOnlineReturnRefundPort,
  OnlineReturnRefundInput,
} from "@hb/pos-domain/features/returns/adapters/durable-return-execution-orchestrator";
import type {
  DurableReturnApiAttempt,
  PrepareReturnApiAttempt,
  ReturnApiAttemptState,
} from "@/core/db/sqlite-return-api-attempt-store";


const cashPreparation = Object.freeze({
  actionId: "return-action-1",
  allocationId: "return-allocation-1",
  externalAttemptId: "cash-attempt-1",
  returnOrderGuid: "return-order-1",
  actor: {
    cashierId: "cashier-alice",
    cashierName: "Alice",
    userGuid: "user-alice",
  },
  method: "cash" as const,
  signedAmountCents: -1_001,
  capacityId: null,
  originalOrderGuid: null,
});

const cashAttempt: OnlineReturnRefundInput = Object.freeze({
  ...cashPreparation,
  attemptKind: "hbpos-api",
  externalActionId: cashPreparation.externalAttemptId,
  durableAttemptId: cashPreparation.externalAttemptId,
});

test("在线现金退款以既有 externalAttemptId 建立耐久本地绑定，提交和恢复都不调用 provider", async () => {
  let providerCalls = 0;
  const router = new ProductionReturnOnlineRefundRouter({
    ...cashRouterOptions(),
    providerRefund: providerPort(() => {
      providerCalls += 1;
    }),
  });

  assert.deepEqual(await router.prepareAttempt(cashPreparation), {
    attemptKind: "hbpos-api",
    externalActionId: "cash-attempt-1",
    durableAttemptId: "cash-attempt-1",
  });
  assert.deepEqual(await router.submit(cashAttempt), {
    status: "completed",
  });
  assert.deepEqual(
    await router.recover({
      ...cashAttempt,
      protectedRecoveryKey: null,
    }),
    { status: "completed" },
  );
  assert.equal(providerCalls, 0);
});

test("现金退款拒绝伪造 provider 绑定、非负金额和受保护恢复键", async () => {
  const router = new ProductionReturnOnlineRefundRouter({
    ...cashRouterOptions(),
    providerRefund: null,
  });

  await assert.rejects(
    router.submit({
      ...cashAttempt,
      durableAttemptId: "different-attempt",
    }),
    isRouterError("RETURN_CASH_ATTEMPT_MISMATCH"),
  );
  await assert.rejects(
    router.prepareAttempt({
      ...cashPreparation,
      signedAmountCents: 1,
    }),
    isRouterError("RETURN_CASH_AMOUNT_INVALID"),
  );
  await assert.rejects(
    router.recover({
      ...cashAttempt,
      protectedRecoveryKey: "must-not-exist",
    }),
    isRouterError("RETURN_CASH_RECOVERY_KEY_INVALID"),
  );
});

test("卡与券退款逐项委托既有 provider bridge，缺少 bridge 时失败关闭", async () => {
  const trace: string[] = [];
  const providerRefund = providerPort((operation) => {
    trace.push(operation);
  });
  const router = new ProductionReturnOnlineRefundRouter({
    ...cashRouterOptions(),
    providerRefund,
  });
  const cardPreparation = {
    ...cashPreparation,
    method: "card" as const,
    capacityId: "capacity-1",
    originalOrderGuid: "original-order-1",
  };
  const prepared = await router.prepareAttempt(cardPreparation);
  const cardAttempt = {
    ...cardPreparation,
    ...prepared,
  };

  assert.deepEqual(await router.submit(cardAttempt), {
    status: "completed",
  });
  assert.deepEqual(
    await router.recover({
      ...cardAttempt,
      protectedRecoveryKey: null,
    }),
    { status: "completed" },
  );
  assert.deepEqual(trace, ["prepare", "submit", "recover"]);

  await assert.rejects(
    new ProductionReturnOnlineRefundRouter({
      ...cashRouterOptions(),
      providerRefund: null,
    }).prepareAttempt(cardPreparation),
    isRouterError("RETURN_PROVIDER_REFUND_UNAVAILABLE"),
  );
});

test("离线现金退款只确认已冻结容量证明，不开钱箱且只接受空恢复键", async () => {
  const adapter = new ProductionReturnCashRefundAdapter();
  const input = {
    actionId: "return-action-1",
    allocationId: "return-allocation-1",
    returnOrderGuid: "return-order-1",
    signedAmountCents: -500,
    originalOrderGuid: "original-order-1",
    capacityId: "capacity-1",
    offlineCashProof: {
      evidenceId: "cash-evidence-1",
      capacityId: "capacity-1",
      originalOrderGuid: "original-order-1",
      remainingCents: 500,
    },
  } as const;

  assert.deepEqual(await adapter.submit(input), {
    status: "completed",
  });
  assert.deepEqual(
    await adapter.recover({ ...input, protectedRecoveryKey: null }),
    { status: "completed" },
  );
  await assert.rejects(
    adapter.submit({
      ...input,
      capacityId: "substituted-capacity",
    }),
    isRouterError("RETURN_OFFLINE_CASH_PROOF_MISMATCH"),
  );
  await assert.rejects(
    adapter.recover({
      ...input,
      protectedRecoveryKey: "must-not-exist",
    }),
    isRouterError("RETURN_CASH_RECOVERY_KEY_INVALID"),
  );
});

function providerPort(
  onCall: (operation: "prepare" | "submit" | "recover") => void,
): DurableOnlineReturnRefundPort {
  return {
    async prepareAttempt(input) {
      onCall("prepare");
      return {
        attemptKind: "payment-provider",
        externalActionId: input.externalAttemptId,
        durableAttemptId: `provider-${input.externalAttemptId}`,
      };
    },
    async submit() {
      onCall("submit");
      return { status: "completed" };
    },
    async recover() {
      onCall("recover");
      return { status: "completed" };
    },
  };
}

function isRouterError(code: string) {
  return (error: unknown): boolean =>
    error instanceof ReturnOnlineRefundRouterError &&
    error.code === code;
}

/** 内存版现金 attempt 存储：只实现路由用到的面，状态转换与生产存储一致。 */
class FakeCashAttempts {
  public readonly rows = new Map<string, DurableReturnApiAttempt>();
  public async get(id: string): Promise<DurableReturnApiAttempt | null> {
    return this.rows.get(id) ?? null;
  }
  public async prepareOrLoad(input: PrepareReturnApiAttempt): Promise<DurableReturnApiAttempt> {
    const existing = this.rows.get(input.durableAttemptId);
    if (existing) {
      assert.equal(existing.createdAtIso, input.createdAtIso, "重放必须沿用首次创建时间");
      return existing;
    }
    const { protectedContext: _ignored, ...rest } = input;
    const row = { ...rest, state: "Created" as const, updatedAtIso: input.createdAtIso };
    this.rows.set(input.durableAttemptId, row);
    return row;
  }
  public async compareAndSetState(input: Readonly<{
    durableAttemptId: string;
    expected: ReturnApiAttemptState;
    next: ReturnApiAttemptState;
    updatedAtIso: string;
  }>): Promise<boolean> {
    const row = this.rows.get(input.durableAttemptId);
    if (!row || row.state !== input.expected) return false;
    this.rows.set(input.durableAttemptId, { ...row, state: input.next, updatedAtIso: input.updatedAtIso });
    return true;
  }
}

function cashRouterOptions(attempts = new FakeCashAttempts()) {
  return { cashAttempts: attempts, nowIso: () => "2026-09-30T00:00:00.000Z" };
}

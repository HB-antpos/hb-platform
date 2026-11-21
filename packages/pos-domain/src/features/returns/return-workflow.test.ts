import assert from "node:assert/strict";
import test from "node:test";

import {
  ReturnFeatureError,
  type NoReceiptReturnItem,
  type ReceiptReturnContext,
} from "./return-domain";
import {
  ReturnWorkflow,
  type ReturnExecutionCommand,
  type ReturnExecutionOutcome,
  type ReturnExecutionPort,
  type ReturnLookupPort,
  type ReturnWorkflowOptions,
} from "./return-workflow";

test("重复确认共享同一执行且只生成一个退款 action", async () => {
  const pending = deferred<ReturnExecutionOutcome>();
  const execution = new FakeExecution();
  execution.executeImpl = async () => pending.promise;
  const workflow = createWorkflow({ execution });
  await selectOneReceiptLine(workflow);

  const first = workflow.confirm();
  await waitUntil(() => execution.executeCalls.length === 1);
  const second = workflow.confirm();
  assert.strictEqual(first, second);
  pending.resolve({
    status: "completed",
    returnOrderGuid: "return-order-1",
  });

  assert.equal((await first).status, "completed");
  assert.equal((await second).status, "completed");
  assert.equal(execution.executeCalls.length, 1);
  assert.equal(execution.executeCalls[0]?.actionId, "return-action-1");
  assert.equal(execution.executeCalls[0]?.plan.lines[0]?.signedAmountCents, -1_000);
});

test("Unknown 只允许恢复原 action，禁止再次确认或切换退款计划", async () => {
  const execution = new FakeExecution();
  execution.executeImpl = async () => ({
    status: "unknown",
    recoveryKey: "private-recovery-key",
  });
  execution.recoverImpl = async () => ({
    status: "completed",
    returnOrderGuid: "return-order-recovered",
  });
  const workflow = createWorkflow({ execution });
  await selectOneReceiptLine(workflow);

  const unknown = await workflow.confirm();
  assert.equal(unknown.status, "unknown");
  assert.equal(workflow.getSnapshot().status, "unknown");
  await assert.rejects(
    workflow.confirm(),
    hasCode("RETURN_UNKNOWN_RECOVERY_REQUIRED"),
  );
  assert.throws(
    () => workflow.setPreferredMethod("card"),
    hasCode("RETURN_UNKNOWN_RECOVERY_REQUIRED"),
  );
  assert.equal(execution.executeCalls.length, 1);

  const recovered = await workflow.recoverUnknown();
  assert.equal(recovered.status, "completed");
  assert.deepEqual(execution.recoverCalls, [
    {
      actionId: "return-action-1",
      recoveryKey: null,
    },
  ]);
  assert.equal(execution.executeCalls.length, 1);
});

test("跨进程恢复 hydrate 冻结原 action，只能显式 recover 且不再 execute", async () => {
  const execution = new FakeExecution();
  execution.recoverImpl = async () => ({
    status: "completed",
    returnOrderGuid: "same-return-order-guid",
  });
  const workflow = createWorkflow({ execution });

  const hydrated = workflow.hydrateRecovery({
    actionId: "frozen-return-action",
    sourceKind: "receipt",
    totalRefundCents: 1_001,
    lines: [
      {
        sourceKind: "receipt",
        itemNumber: "1001",
        displayName: "Recovered Product",
        quantity: 3,
        unitRefundCents: 334,
        signedAmountCents: -1_001,
        syncProvenance: {
          referenceCode: "RECOVERY-REF",
          priceSource: 0,
        },
      },
    ],
  });

  assert.equal(hydrated.status, "unknown");
  assert.equal(hydrated.selectedTotalCents, 1_001);
  assert.equal(hydrated.lines[0]?.selectedQuantity, 3);
  assert.throws(
    () => workflow.setQuantity(hydrated.lines[0]!.selectionKey, 1),
    hasCode("RETURN_UNKNOWN_RECOVERY_REQUIRED"),
  );
  assert.throws(
    () => workflow.reset(),
    hasCode("RETURN_UNKNOWN_RECOVERY_REQUIRED"),
  );
  await assert.rejects(
    workflow.confirm(),
    hasCode("RETURN_UNKNOWN_RECOVERY_REQUIRED"),
  );

  assert.equal((await workflow.recoverUnknown()).status, "completed");
  assert.deepEqual(execution.recoverCalls, [
    {
      actionId: "frozen-return-action",
      recoveryKey: null,
    },
  ]);
  assert.equal(execution.executeCalls.length, 0);
});

test("执行适配器异常后冻结 action，不把歧义失败当成可安全重试", async () => {
  const execution = new FakeExecution();
  execution.executeImpl = async () => {
    throw new Error("transport lost");
  };
  execution.recoverImpl = async () => ({
    status: "completed",
    returnOrderGuid: "return-order-after-action-recovery",
  });
  const workflow = createWorkflow({ execution });
  await selectOneReceiptLine(workflow);

  await assert.rejects(
    workflow.confirm(),
    hasCode("RETURN_EXECUTION_FAILED"),
  );
  assert.equal(workflow.getSnapshot().status, "unknown");
  await assert.rejects(
    workflow.confirm(),
    hasCode("RETURN_UNKNOWN_RECOVERY_REQUIRED"),
  );
  assert.throws(
    () => workflow.reset(),
    hasCode("RETURN_UNKNOWN_RECOVERY_REQUIRED"),
  );
  assert.equal(execution.executeCalls.length, 1);
  assert.equal((await workflow.recoverUnknown()).status, "completed");
  assert.deepEqual(execution.recoverCalls, [
    {
      actionId: "return-action-1",
      recoveryKey: null,
    },
  ]);
});

test("无小票商品和 OPENITEM 先检查在线，再执行主管授权", async () => {
  let online = false;
  let authorizationCalls = 0;
  let productLookupCalls = 0;
  const execution = new FakeExecution();
  const lookup = new FakeLookup();
  lookup.lookupNoReceiptProduct = async () => {
    productLookupCalls += 1;
    return noReceiptItem("no-receipt-product");
  };
  const workflow = createWorkflow({
    lookup,
    connectivity: { isOnline: async () => online },
    supervisorAuthorization: {
      authorizeNoReceiptReturn: async () => {
        authorizationCalls += 1;
        return { authorizationKey: "supervisor-grant-1" };
      },
    },
    execution,
  });
  workflow.beginNoReceipt();

  await assert.rejects(
    workflow.addNoReceiptProduct("9320001"),
    hasCode("RETURN_ONLINE_REQUIRED"),
  );
  assert.equal(authorizationCalls, 0);
  assert.equal(productLookupCalls, 0);

  online = true;
  await workflow.addNoReceiptProduct("9320001");
  await workflow.addNoReceiptOpenItem({
    displayName: "Loose item",
    unitRefundCents: 450,
  });
  assert.equal(authorizationCalls, 1);
  assert.equal(productLookupCalls, 1);
  assert.equal(workflow.getSnapshot().lines.length, 2);
  await workflow.confirm();
  assert.equal(
    execution.executeCalls[0]?.noReceiptAuthorizationKey,
    "supervisor-grant-1",
  );
});

test("无小票退货不能伪装为本地容量，断网确认必定拒绝", async () => {
  let online = true;
  const workflow = createWorkflow({
    connectivity: { isOnline: async () => online },
  });
  workflow.beginNoReceipt();
  await workflow.addNoReceiptProduct("9320001");
  workflow.setPreferredMethod("cash");
  online = false;

  await assert.rejects(
    workflow.confirm(),
    hasCode("RETURN_ONLINE_REQUIRED"),
  );
  // 断网只是计划阶段的校验失败：没有 actionId、没有写账本，恢复在线后同一页面可直接重试。
  assert.equal(workflow.getSnapshot().status, "draft");
  online = true;
  assert.equal((await workflow.confirm()).status, "completed");
});

test("无小票退货选银行卡 / 礼券：确认时在进入执行层之前就失败，不会冻结成 Unknown", async () => {
  for (const method of ["card", "voucher"] as const) {
    const execution = new FakeExecution();
    const workflow = createWorkflow({ execution });
    workflow.beginNoReceipt();
    await workflow.addNoReceiptProduct("9320001");
    workflow.setPreferredMethod(method);

    await assert.rejects(
      workflow.confirm(),
      hasCode("RETURN_NO_RECEIPT_CASH_ONLY"),
    );
    // 没有进入执行层 = 没有写退货账本，更不会留下需要恢复的 Unknown 动作。
    assert.equal(execution.executeCalls.length, 0);
    // 计划阶段的校验错误回到草稿（而不是冻结成 failed）：收银员按提示改选现金即可继续。
    assert.equal(workflow.getSnapshot().status, "draft");
    assert.equal(workflow.getSnapshot().lastErrorCode, "RETURN_NO_RECEIPT_CASH_ONLY");
    workflow.setPreferredMethod("cash");
    const outcome = await workflow.confirm();
    assert.equal(outcome.status, "completed");
    assert.equal(execution.executeCalls.length, 1);
    assert.equal(execution.executeCalls[0]?.plan.allocations[0]?.method, "cash");
  }
});

test("旧收银员 lease 在查询和执行异步边界后均不能更新页面或重复退款", async () => {
  const guard = new RotatingSessionGuard();
  const lookupPending = deferred<ReceiptReturnContext | null>();
  const lookup = new FakeLookup();
  lookup.lookupReceipt = async () => lookupPending.promise;
  const workflow = createWorkflow({ lookup, sessionGuard: guard });

  const loading = workflow.loadReceipt("HB-1001");
  guard.rotate();
  lookupPending.resolve(receiptContext());
  await assert.rejects(loading, hasCode("RETURN_SESSION_EXPIRED"));
  assert.equal(workflow.getSnapshot().lines.length, 0);

  const executeGuard = new RotatingSessionGuard();
  const executePending = deferred<ReturnExecutionOutcome>();
  const execution = new FakeExecution();
  execution.executeImpl = async () => executePending.promise;
  const executingWorkflow = createWorkflow({
    execution,
    sessionGuard: executeGuard,
  });
  await selectOneReceiptLine(executingWorkflow);
  const completing = executingWorkflow.confirm();
  await waitUntil(() => execution.executeCalls.length === 1);
  executeGuard.rotate();
  executePending.resolve({
    status: "completed",
    returnOrderGuid: "return-order-durable",
  });
  await assert.rejects(completing, hasCode("RETURN_SESSION_EXPIRED"));
  await assert.rejects(
    executingWorkflow.confirm(),
    hasCode("RETURN_SESSION_EXPIRED"),
  );
  assert.equal(execution.executeCalls.length, 1);
  assert.equal(executingWorkflow.getSnapshot().status, "completed");
});

test("主管拒绝被映射为稳定码且不会调用无小票查询", async () => {
  let lookupCalls = 0;
  const lookup = new FakeLookup();
  lookup.lookupNoReceiptProduct = async () => {
    lookupCalls += 1;
    return noReceiptItem("no-receipt-product");
  };
  const workflow = createWorkflow({
    lookup,
    supervisorAuthorization: {
      authorizeNoReceiptReturn: async () => {
        throw new Error("denied by supervisor");
      },
    },
  });
  workflow.beginNoReceipt();

  await assert.rejects(
    workflow.addNoReceiptProduct("9320001"),
    hasCode("RETURN_SUPERVISOR_REQUIRED"),
  );
  assert.equal(lookupCalls, 0);
});

async function selectOneReceiptLine(workflow: ReturnWorkflow): Promise<void> {
  await workflow.loadReceipt("HB-1001");
  workflow.setQuantity("line-a", 1);
  workflow.setPreferredMethod("cash");
}

function createWorkflow(
  overrides: Partial<ReturnWorkflowOptions> = {},
): ReturnWorkflow {
  return new ReturnWorkflow({
    lookup: new FakeLookup(),
    connectivity: { isOnline: async () => true },
    supervisorAuthorization: {
      authorizeNoReceiptReturn: async () => ({
        authorizationKey: "supervisor-grant-default",
      }),
    },
    sessionGuard: new RotatingSessionGuard(),
    execution: new FakeExecution(),
    createActionId: () => "return-action-1",
    ...overrides,
  });
}

class FakeLookup implements ReturnLookupPort {
  public async lookupReceipt(): Promise<ReceiptReturnContext | null> {
    return receiptContext();
  }

  public async lookupNoReceiptProduct(): Promise<NoReceiptReturnItem | null> {
    return noReceiptItem("no-receipt-product");
  }

  public async createNoReceiptOpenItem(input: Readonly<{
    displayName: string;
    unitRefundCents: number;
  }>): Promise<NoReceiptReturnItem | null> {
    return {
      ...noReceiptItem("no-receipt-open-item"),
      displayName: input.displayName,
      unitRefundCents: input.unitRefundCents,
      lookupCode: "OPENITEM",
      selectionKey: "open-line",
      returnSourceKey: "noreceipt-open:BNE:1",
    };
  }
}

class FakeExecution implements ReturnExecutionPort {
  public readonly executeCalls: ReturnExecutionCommand[] = [];
  public readonly recoverCalls: {
    actionId: string;
    recoveryKey: string | null;
  }[] = [];
  public executeImpl: (
    command: ReturnExecutionCommand,
  ) => Promise<ReturnExecutionOutcome> = async () => ({
    status: "completed",
    returnOrderGuid: "return-order-default",
  });
  public recoverImpl: (input: Readonly<{
    actionId: string;
    recoveryKey: string | null;
  }>) => Promise<ReturnExecutionOutcome> = async () => ({
    status: "completed",
    returnOrderGuid: "return-order-default",
  });

  public execute(
    command: ReturnExecutionCommand,
  ): Promise<ReturnExecutionOutcome> {
    this.executeCalls.push(command);
    return this.executeImpl(command);
  }

  public recover(input: Readonly<{
    actionId: string;
    recoveryKey: string | null;
  }>): Promise<ReturnExecutionOutcome> {
    this.recoverCalls.push(input);
    return this.recoverImpl(input);
  }
}

class RotatingSessionGuard {
  private epoch = 1;

  public captureLease(): string {
    return String(this.epoch);
  }

  public assertActive(lease: string): void {
    if (lease !== String(this.epoch)) throw new Error("stale");
  }

  public rotate(): void {
    this.epoch += 1;
  }
}

function receiptContext(): ReceiptReturnContext {
  return {
    originalOrderGuid: "order-a",
    receiptLabel: "HB-1001",
    loadedFrom: "remote",
    returnRecordsMayBeStale: false,
    lines: [
      {
        selectionKey: "line-a",
        originalOrderGuid: "order-a",
        originalOrderDetailGuid: "detail-a",
        returnSourceKey: "return:order-a:detail-a",
        productCode: "P-1",
        itemNumber: "1001",
        lookupCode: "1001",
        displayName: "Product",
        availableQuantity: 2,
        unitRefundCents: 1_000,
        remainingAmountCents: 2_000,
        syncProvenance: {
          referenceCode: "RECEIPT-REF",
          priceSource: 0,
        },
      },
    ],
    tenderCapacities: [
      {
        capacityId: "cash-capacity",
        originalOrderGuid: "order-a",
        method: "cash",
        remainingCents: 2_000,
        offlineCashProof: {
          evidenceId: "cash-proof",
          capacityId: "cash-capacity",
          originalOrderGuid: "order-a",
          remainingCents: 2_000,
        },
      },
    ],
  };
}

function noReceiptItem(
  sourceKind: NoReceiptReturnItem["sourceKind"],
): NoReceiptReturnItem {
  return {
    sourceKind,
    selectionKey: "no-receipt-line",
    returnSourceKey: "noreceipt:BNE:1",
    productCode: "P-2",
    itemNumber: "2002",
    lookupCode:
      sourceKind === "no-receipt-open-item" ? "OPENITEM" : "9320001",
    displayName: "No receipt product",
    unitRefundCents: 500,
    syncProvenance: {
      referenceCode: "CATALOG-REF",
      priceSource: 1,
    },
  };
}

function deferred<T>(): {
  promise: Promise<T>;
  resolve(value: T): void;
} {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((next) => {
    resolve = next;
  });
  return { promise, resolve };
}

async function waitUntil(predicate: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 20; attempt += 1) {
    if (predicate()) return;
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  }
  throw new Error("condition not reached");
}

function hasCode(code: string): (error: unknown) => boolean {
  return (error) =>
    error instanceof ReturnFeatureError && error.code === code;
}

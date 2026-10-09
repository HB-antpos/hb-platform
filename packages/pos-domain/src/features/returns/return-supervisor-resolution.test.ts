import assert from "node:assert/strict";
import test from "node:test";

import { ReturnFeatureError, type ReceiptReturnContext } from "./return-domain";
import { ReturnPresenter } from "./return-presenter";
import {
  ReturnWorkflow,
  type ReturnExecutionCommand,
  type ReturnExecutionOutcome,
  type ReturnExecutionPort,
  type ReturnSupervisorResolutionAuthorization,
  type ReturnSupervisorResolutionCommand,
  type ReturnWorkflowOptions,
} from "./return-workflow";

const SUPERVISOR = {
  authorizationId: "auth-1",
  supervisorActor: { cashierId: "supervisor-1", cashierName: "Supervisor", userGuid: "u-sup" },
  requestingActor: { cashierId: "cashier-1", cashierName: "Cashier", userGuid: "u-cash" },
} as const satisfies ReturnSupervisorResolutionAuthorization;

class FakeExecution implements ReturnExecutionPort {
  public readonly resolveCalls: ReturnSupervisorResolutionCommand[] = [];
  public executeImpl: (c: ReturnExecutionCommand) => Promise<ReturnExecutionOutcome> =
    async () => ({ status: "unknown", recoveryKey: "k" });
  public resolveImpl: (c: ReturnSupervisorResolutionCommand) => Promise<ReturnExecutionOutcome> =
    async () => ({ status: "declined" });
  public execute(command: ReturnExecutionCommand) { return this.executeImpl(command); }
  public async recover(): Promise<ReturnExecutionOutcome> { return { status: "unknown", recoveryKey: "k" }; }
  public resolveUnknown(command: ReturnSupervisorResolutionCommand) {
    this.resolveCalls.push(command);
    return this.resolveImpl(command);
  }
}

function createWorkflow(
  execution: FakeExecution,
  authorizeUnknownResolution: (() => Promise<ReturnSupervisorResolutionAuthorization>) | null =
    async () => SUPERVISOR,
): ReturnWorkflow {
  const options: ReturnWorkflowOptions = {
    lookup: {
      async lookupReceipt() { return receiptContext(); },
      async lookupNoReceiptProduct() { return null; },
      async createNoReceiptOpenItem() { return null; },
    },
    connectivity: { isOnline: async () => true },
    supervisorAuthorization: {
      authorizeNoReceiptReturn: async () => ({ authorizationKey: "grant" }),
      ...(authorizeUnknownResolution ? { authorizeUnknownResolution } : {}),
    },
    sessionGuard: { captureLease: () => "1", assertActive: () => undefined },
    execution,
    createActionId: () => "return-action-1",
  };
  return new ReturnWorkflow(options);
}

async function intoUnknown(workflow: ReturnWorkflow): Promise<void> {
  await workflow.loadReceipt("HB-1001");
  workflow.setQuantity("line-a", 1);
  workflow.setPreferredMethod("card");
  assert.equal((await workflow.confirm()).status, "unknown");
}

const evidence = { evidenceReference: "terminal receipt 8841", note: "Checked terminal and Linkly portal" };

test("H9：Unknown 退款经主管确认未退款后作废并解除锁定", async () => {
  const execution = new FakeExecution();
  const workflow = createWorkflow(execution);
  await intoUnknown(workflow);

  const outcome = await workflow.resolveUnknownBySupervisor({ finding: "not-refunded", ...evidence });
  assert.equal(outcome.status, "declined");
  assert.equal(workflow.getSnapshot().status, "declined");
  assert.deepEqual(execution.resolveCalls, [{
    actionId: "return-action-1",
    finding: "not-refunded",
    evidenceReference: "terminal receipt 8841",
    note: "Checked terminal and Linkly portal",
    authorization: SUPERVISOR,
  }]);
  // 解除后可以开始新的退货。
  assert.doesNotThrow(() => workflow.reset());
});

test("H9：继续等待只留审计，Unknown 锁定保持，仍只能恢复原 action", async () => {
  const execution = new FakeExecution();
  execution.resolveImpl = async () => ({ status: "unknown", recoveryKey: "k" });
  const workflow = createWorkflow(execution);
  await intoUnknown(workflow);

  const outcome = await workflow.resolveUnknownBySupervisor({ finding: "keep-waiting", ...evidence });
  assert.equal(outcome.status, "unknown");
  assert.equal(workflow.getSnapshot().status, "unknown");
  assert.throws(() => workflow.reset(), (e: unknown) => e instanceof ReturnFeatureError && e.code === "RETURN_UNKNOWN_RECOVERY_REQUIRED");
});

test("H9：恢复时发现 provider 已有明确结果则以真实结果为准（主管结论不覆盖）", async () => {
  const execution = new FakeExecution();
  execution.resolveImpl = async () => ({ status: "completed", returnOrderGuid: "return-order-late-approved" });
  const workflow = createWorkflow(execution);
  await intoUnknown(workflow);

  const outcome = await workflow.resolveUnknownBySupervisor({ finding: "not-refunded", ...evidence });
  assert.equal(outcome.status, "completed");
  assert.equal(workflow.getSnapshot().status, "completed");
});

test("H9：授权被拒/不是另一名主管/缺凭据时不调用结案，Unknown 锁定不变", async () => {
  const denied = new FakeExecution();
  const deniedWorkflow = createWorkflow(denied, async () => { throw new Error("cancelled"); });
  await intoUnknown(deniedWorkflow);
  await assert.rejects(
    deniedWorkflow.resolveUnknownBySupervisor({ finding: "not-refunded", ...evidence }),
    (e: unknown) => e instanceof ReturnFeatureError && e.code === "RETURN_SUPERVISOR_REQUIRED",
  );
  assert.equal(denied.resolveCalls.length, 0);
  assert.equal(deniedWorkflow.getSnapshot().status, "unknown");

  const self = new FakeExecution();
  const selfWorkflow = createWorkflow(self, async () => ({
    ...SUPERVISOR,
    supervisorActor: { ...SUPERVISOR.requestingActor },
  }));
  await intoUnknown(selfWorkflow);
  await assert.rejects(
    selfWorkflow.resolveUnknownBySupervisor({ finding: "not-refunded", ...evidence }),
    (e: unknown) => e instanceof ReturnFeatureError && e.code === "RETURN_SUPERVISOR_REQUIRED",
  );
  assert.equal(self.resolveCalls.length, 0);

  const missing = new FakeExecution();
  const missingWorkflow = createWorkflow(missing);
  await intoUnknown(missingWorkflow);
  for (const bad of [
    { evidenceReference: "  ", note: "x" },
    { evidenceReference: "x", note: "" },
    { evidenceReference: "bad\u0000ref", note: "x" },
    { evidenceReference: "x".repeat(257), note: "x" },
  ]) {
    await assert.rejects(
      missingWorkflow.resolveUnknownBySupervisor({ finding: "not-refunded", ...bad }),
      (e: unknown) => e instanceof ReturnFeatureError && e.code === "RETURN_RESOLUTION_EVIDENCE_REQUIRED",
    );
  }
  assert.equal(missing.resolveCalls.length, 0);
});

test("H9：非 Unknown 状态、未接线端口、执行失败都失败关闭", async () => {
  const draft = createWorkflow(new FakeExecution());
  await draft.loadReceipt("HB-1001");
  await assert.rejects(
    draft.resolveUnknownBySupervisor({ finding: "not-refunded", ...evidence }),
    (e: unknown) => e instanceof ReturnFeatureError && e.code === "RETURN_UNKNOWN_RECOVERY_REQUIRED",
  );

  const unsupported = createWorkflow(new FakeExecution(), null);
  await intoUnknown(unsupported);
  assert.equal(unsupported.supportsSupervisorResolution(), false);
  await assert.rejects(
    unsupported.resolveUnknownBySupervisor({ finding: "not-refunded", ...evidence }),
    (e: unknown) => e instanceof ReturnFeatureError && e.code === "RETURN_SUPERVISOR_RESOLUTION_UNSUPPORTED",
  );

  const failing = new FakeExecution();
  failing.resolveImpl = async () => { throw new ReturnFeatureError("RETURN_SUPERVISOR_RESOLUTION_UNSUPPORTED"); };
  const failingWorkflow = createWorkflow(failing);
  await intoUnknown(failingWorkflow);
  await assert.rejects(
    failingWorkflow.resolveUnknownBySupervisor({ finding: "not-refunded", ...evidence }),
    (e: unknown) => e instanceof ReturnFeatureError && e.code === "RETURN_SUPERVISOR_RESOLUTION_UNSUPPORTED",
  );
  assert.equal(failingWorkflow.getSnapshot().status, "unknown");
});

test("H9：presenter 暴露结案入口、记录继续等待并在未退款后给出稳定码", async () => {
  const execution = new FakeExecution();
  execution.resolveImpl = async (command) => command.finding === "keep-waiting"
    ? { status: "unknown", recoveryKey: "k" }
    : { status: "declined" };
  const workflow = createWorkflow(execution);
  await intoUnknown(workflow);
  const presenter = new ReturnPresenter(workflow);
  assert.equal(presenter.getState().phase, "unknown");
  assert.equal(presenter.getState().supervisorResolutionAvailable, true);

  assert.equal(await presenter.resolveBySupervisor({ finding: "keep-waiting", ...evidence }), false);
  assert.equal(presenter.getState().phase, "unknown");
  assert.equal(presenter.getState().supervisorWaitingRecorded, true);

  assert.equal(await presenter.resolveBySupervisor({ finding: "not-refunded", ...evidence }), true);
  assert.equal(presenter.getState().phase, "failed");
  assert.equal(presenter.getState().errorCode, "RETURN_SUPERVISOR_NOT_REFUNDED");

  const unsupported = createWorkflow(new FakeExecution(), null);
  await intoUnknown(unsupported);
  assert.equal(new ReturnPresenter(unsupported).getState().supervisorResolutionAvailable, false);
});

function receiptContext(): ReceiptReturnContext {
  return {
    originalOrderGuid: "order-a",
    receiptLabel: "HB-1001",
    loadedFrom: "remote",
    returnRecordsMayBeStale: false,
    lines: [{
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
      syncProvenance: { referenceCode: "RECEIPT-REF", priceSource: 0 },
    }],
    tenderCapacities: [{
      capacityId: "card-capacity",
      originalOrderGuid: "order-a",
      method: "card",
      remainingCents: 2_000,
      offlineCashProof: null,
    }],
  };
}

import assert from "node:assert/strict";
import test from "node:test";

import {
  ReturnFeatureError,
  type ReturnRefundPlan,
} from "../return-domain";
import type { ReturnExecutionCommand } from "../return-workflow";

import {
  DurableReturnExecutionOrchestrator,
  type CompleteDurableReturnAction,
  type DurableOfflineCashRefundPort,
  type DurableReturnAction,
  type DurableReturnAllocation,
  type DurableReturnExecutionOptions,
  type DurableReturnLine,
  type DurableOnlineReturnRefundPort,
  type PrepareDurableReturnAction,
  type ReturnAllocationExternalOutcome,
  type ReturnExecutionLedgerPort,
  type TrustedReturnIdentity,
} from "./durable-return-execution-orchestrator";

test("离线现金先耐久 prepare，成功后一次事务完成退货记录、outbox、打印和钱箱", async () => {
  const harness = createHarness();
  const command = receiptCommand(offlineCashPlan());

  const outcome = await harness.orchestrator.execute(command);

  assert.equal(outcome.status, "completed");
  assert.equal(harness.cash.submitCalls.length, 1);
  assert.equal(harness.online.submitCalls.length, 0);
  assert.equal(harness.ledger.prepareInputs.length, 1);
  const prepared = harness.ledger.prepareInputs[0]!;
  assert.equal(prepared.identity.sessionEpoch, "session-epoch-1");
  assert.equal(prepared.lines[0]?.displayName, "Milk 2L");
  assert.equal(prepared.lines[0]?.unitRefundCents, 1_000);
  assert.equal(prepared.allocations[0]?.status, "created");

  const completed = harness.ledger.completeInputs[0]!;
  assert.equal(completed.returnOrderGuid, prepared.returnOrderGuid);
  assert.equal(completed.returnRecords[0]?.returnAmountCents, 1_000);
  assert.equal(completed.outbox.idempotencyKey, prepared.returnOrderGuid);
  assert.equal(completed.fulfilment.drawerRequired, true);
  assert.ok(completed.fulfilment.drawerEventId);
  assert.equal(
    harness.events.indexOf("ledger:prepare") <
      harness.events.indexOf("cash:submit"),
    true,
  );
  assert.equal(
    harness.events.indexOf("cash:submit") <
      harness.events.indexOf("ledger:complete"),
    true,
  );
});

test("完成事务按 WPF 规则冻结现金、券、卡及混合退款履约策略", async () => {
  const cases = [
    {
      name: "现金",
      plan: planForMethods(["cash"]),
      receiptKind: "none",
      drawerRequired: true,
    },
    {
      name: "纯券签发",
      plan: planForMethods(["voucher"]),
      receiptKind: "refund-voucher",
      drawerRequired: false,
    },
    {
      // 每张退款券逐张出券面，不再因多券放弃打印。
      name: "多券逐张打印",
      plan: planForMethods(["voucher", "voucher"]),
      receiptKind: "refund-voucher",
      drawerRequired: false,
    },
    {
      name: "卡",
      plan: planForMethods(["card"]),
      receiptKind: "refund-receipt",
      drawerRequired: false,
    },
    {
      name: "卡加券",
      plan: planForMethods(["card", "voucher"]),
      receiptKind: "refund-receipt",
      drawerRequired: false,
    },
    {
      name: "现金加卡",
      plan: planForMethods(["cash", "card"]),
      receiptKind: "refund-receipt",
      drawerRequired: true,
    },
    {
      // 与 WPF 对齐：现金+退款券的混合退款同样打印退款券并开钱箱。
      name: "现金加券",
      plan: planForMethods(["cash", "voucher"]),
      receiptKind: "refund-voucher",
      drawerRequired: true,
    },
  ] as const;

  for (const item of cases) {
    const harness = createHarness();
    const outcome = await harness.orchestrator.execute(
      receiptCommand(item.plan),
    );
    assert.equal(outcome.status, "completed", item.name);
    const fulfilment = harness.ledger.completeInputs[0]!.fulfilment;
    assert.equal(fulfilment.receiptKind, item.receiptKind, item.name);
    assert.equal(
      fulfilment.printJobId !== null,
      item.receiptKind !== "none",
      item.name,
    );
    assert.equal(fulfilment.drawerRequired, item.drawerRequired, item.name);
    assert.equal(
      fulfilment.drawerEventId !== null,
      item.drawerRequired,
      item.name,
    );
    assert.equal(
      Object.hasOwn(fulfilment, "printReceipt"),
      false,
      item.name,
    );
  }
});

test("混合现金、卡和券严格按 allocation 顺序执行，provider 只收到 opaque capacityId", async () => {
  const harness = createHarness();
  const command = receiptCommand(mixedPlan());

  const outcome = await harness.orchestrator.execute(command);

  assert.equal(outcome.status, "completed");
  assert.deepEqual(harness.events, [
    "ledger:prepare",
    "ledger:submitted:0",
    "cash:submit",
    "ledger:completed:0",
    "ledger:submitted:1",
    "online:prepare:card",
    "ledger:bound:1",
    "online:submit:card",
    "ledger:completed:1",
    "ledger:submitted:2",
    "online:prepare:voucher",
    "ledger:bound:2",
    "online:submit:voucher",
    "ledger:completed:2",
    "ledger:complete",
  ]);
  assert.deepEqual(
    harness.online.submitCalls.map((call) => ({
      method: call.method,
      amount: call.signedAmountCents,
      capacityId: call.capacityId,
    })),
    [
      { method: "card", amount: -500, capacityId: "capacity-card" },
      {
        method: "voucher",
        amount: -300,
        capacityId: "capacity-voucher",
      },
    ],
  );
  assert.equal(
    JSON.stringify(harness.online.submitCalls).includes("provider-reference"),
    false,
  );
});

test("首笔卡退款 ACK pending 时暂停后续 allocation，恢复先确认原退款再提交第二笔", async () => {
  let allowFirstAcknowledgement = false;
  const acknowledgements: string[] = [];
  const harness = createHarness({
    async onAllocationFinalized(input) {
      acknowledgements.push(input.allocation.durableAttemptId ?? "missing");
      return input.allocation.index !== 0 || allowFirstAcknowledgement;
    },
  });
  const command = receiptCommand(planForMethods(["card", "card"]));

  const first = await harness.orchestrator.execute(command);

  assert.equal(first.status, "unknown");
  assert.equal(harness.online.submitCalls.length, 1);
  assert.deepEqual(
    (await harness.ledger.load(command.actionId))?.allocations.map(
      (allocation) => allocation.status,
    ),
    ["completed", "created"],
  );

  allowFirstAcknowledgement = true;
  const recovered = await harness.orchestrator.recover({
    actionId: command.actionId,
    recoveryKey: first.status === "unknown" ? first.recoveryKey : null,
  });

  assert.equal(recovered.status, "completed");
  assert.equal(harness.online.submitCalls.length, 2);
  assert.equal(harness.online.recoverCalls.length, 0);
  assert.ok(acknowledgements.length >= 2);
});

test("同 actionId 并发重复点击共享同一执行，且后续重放只返回原 returnOrderGuid", async () => {
  const harness = createHarness();
  const command = receiptCommand(offlineCashPlan());

  const [first, second] = await Promise.all([
    harness.orchestrator.execute(command),
    harness.orchestrator.execute(command),
  ]);
  const replay = await harness.orchestrator.execute(command);

  assert.deepEqual(first, second);
  assert.deepEqual(replay, first);
  assert.equal(harness.cash.submitCalls.length, 1);
  assert.equal(harness.ledger.completeInputs.length, 1);
  assert.equal(harness.ledger.prepareInputs.length, 2);
  assert.equal(
    (first.status === "completed" && first.returnOrderGuid) ||
      "not-completed",
    harness.ledger.prepareInputs[0]?.returnOrderGuid,
  );
});

test("provider 批准后记录结果崩溃时返回 Unknown；恢复只 query 原 attempt，不重新 submit", async () => {
  const harness = createHarness();
  harness.ledger.failRecordOutcomeOnce = true;
  const command = receiptCommand(onlineCardPlan());

  const first = await harness.orchestrator.execute(command);
  assert.equal(first.status, "unknown");
  assert.equal(harness.online.submitCalls.length, 1);
  const prepared = harness.ledger.prepareInputs[0]!;
  const storedAfterCrash = await harness.ledger.load(command.actionId);
  assert.equal(storedAfterCrash?.allocations[0]?.status, "submitted");

  const recovered = await harness.orchestrator.recover({
    actionId: command.actionId,
    recoveryKey: first.status === "unknown" ? first.recoveryKey : null,
  });

  assert.equal(recovered.status, "completed");
  assert.equal(harness.online.submitCalls.length, 1);
  assert.equal(harness.online.recoverCalls.length, 1);
  assert.equal(
    harness.online.recoverCalls[0]?.externalAttemptId,
    prepared.allocations[0]?.externalAttemptId,
  );
  assert.equal(
    recovered.status === "completed" && recovered.returnOrderGuid,
    prepared.returnOrderGuid,
  );
});

test("submitted 标记响应丢失时不调用 provider；恢复仍只检查原 attempt", async () => {
  const harness = createHarness();
  harness.ledger.loseSubmittedResponseOnce = true;
  const command = receiptCommand(onlineCardPlan());

  const first = await harness.orchestrator.execute(command);

  assert.equal(first.status, "unknown");
  assert.equal(harness.online.submitCalls.length, 0);
  assert.equal(
    (await harness.ledger.load(command.actionId))?.allocations[0]?.status,
    "submitted",
  );

  const recovered = await harness.orchestrator.recover({
    actionId: command.actionId,
    recoveryKey: first.status === "unknown" ? first.recoveryKey : null,
  });
  assert.equal(recovered.status, "completed");
  assert.equal(harness.online.submitCalls.length, 0);
  assert.equal(harness.online.recoverCalls.length, 1);
});

test("provider Unknown 会冻结 action；仅原 action recovery token 可恢复", async () => {
  const harness = createHarness();
  harness.online.submitOutcomes = [
    {
      status: "unknown",
      protectedRecoveryKey: "provider-secret-recovery",
    },
  ];
  harness.online.recoverOutcomes = [{ status: "completed" }];
  const command = receiptCommand(onlineCardPlan());

  const unknown = await harness.orchestrator.execute(command);
  assert.equal(unknown.status, "unknown");
  assert.equal(
    unknown.status === "unknown" && unknown.recoveryKey,
    harness.ledger.prepareInputs[0]?.actionRecoveryToken,
  );
  assert.notEqual(
    unknown.status === "unknown" && unknown.recoveryKey,
    "provider-secret-recovery",
  );

  await assert.rejects(
    () =>
      harness.orchestrator.recover({
        actionId: command.actionId,
        recoveryKey: "wrong-action-token",
      }),
    hasReturnCode("RETURN_RECOVERY_FAILED"),
  );

  const recovered = await harness.orchestrator.recover({
    actionId: command.actionId,
    recoveryKey: unknown.status === "unknown" ? unknown.recoveryKey : null,
  });
  assert.equal(recovered.status, "completed");
  assert.equal(
    harness.online.recoverCalls[0]?.protectedRecoveryKey,
    "provider-secret-recovery",
  );
  assert.equal(harness.online.submitCalls.length, 1);
});

test("最终事务已提交但响应丢失时读取同一 action，绝不生成第二 returnOrderGuid", async () => {
  const harness = createHarness();
  harness.ledger.loseCompleteResponseOnce = true;
  const command = receiptCommand(offlineCashPlan());

  const outcome = await harness.orchestrator.execute(command);

  assert.equal(outcome.status, "completed");
  assert.equal(harness.ledger.completeInputs.length, 1);
  assert.equal(harness.cash.submitCalls.length, 1);
  assert.equal(harness.ledger.completeInputs[0]?.fulfilment.printJobId, null);
  assert.equal(
    harness.ledger.completeInputs[0]?.fulfilment.receiptKind,
    "none",
  );
  assert.equal(
    harness.ledger.completeInputs[0]?.fulfilment.drawerRequired,
    true,
  );
  assert.ok(harness.ledger.completeInputs[0]?.fulfilment.drawerEventId);
  assert.equal(
    outcome.status === "completed" && outcome.returnOrderGuid,
    harness.ledger.prepareInputs[0]?.returnOrderGuid,
  );
});

test("无小票 action 在任何 provider 调用前保存完整行和一次性主管授权", async () => {
  const harness = createHarness();
  const command: ReturnExecutionCommand = {
    actionId: "action-no-receipt",
    plan: {
      sourceKind: "no-receipt",
      totalRefundCents: 750,
      online: true,
      lines: [
        {
          sourceKind: "no-receipt-open-item",
          returnSourceKey: "open-item-source",
          originalOrderGuid: null,
          originalOrderDetailGuid: null,
          productCode: "OPENITEM",
          quantity: 1,
          signedAmountCents: -750,
          syncProvenance: {
            referenceCode: "OPEN-REF",
            priceSource: 0,
          },
        },
      ],
      allocations: [
        {
          method: "cash",
          signedAmountCents: -750,
          originalCapacityId: null,
          originalOrderGuid: null,
          offlineCashProof: null,
        },
      ],
    },
    noReceiptAuthorizationKey: "opaque-supervisor-grant",
  };
  harness.lineMaterial.override = [
    {
      lineId: "line-open",
      selectionKey: "selection-open",
      sourceKind: "no-receipt-open-item",
      returnSourceKey: "open-item-source",
      originalOrderGuid: null,
      originalOrderDetailGuid: null,
      productCode: "OPENITEM",
      itemNumber: null,
      lookupCode: "OPENITEM",
      displayName: "Damaged assorted goods",
      quantity: 1,
      unitRefundCents: 750,
      signedAmountCents: -750,
      availableQuantity: null,
      remainingAmountCents: null,
      syncProvenance: {
        referenceCode: "OPEN-REF",
        priceSource: 0,
      },
    },
  ];

  const outcome = await harness.orchestrator.execute(command);

  assert.equal(outcome.status, "completed");
  assert.equal(
    harness.ledger.prepareInputs[0]?.supervisorGrantKey,
    "opaque-supervisor-grant",
  );
  assert.equal(
    harness.ledger.prepareInputs[0]?.lines[0]?.displayName,
    "Damaged assorted goods",
  );
  assert.equal(harness.cash.submitCalls.length, 0);
  assert.equal(harness.online.submitCalls[0]?.method, "cash");
  assert.equal(harness.online.submitCalls[0]?.attemptKind, "hbpos-api");
  assert.match(
    harness.online.submitCalls[0]?.durableAttemptId ?? "",
    /^attempt-/u,
  );
});

test("在线现金和分期绑定 Hbpos API attempt，不伪造银行卡 PaymentAttempt", async () => {
  for (const method of ["cash", "installment"] as const) {
    const harness = createHarness();
    const plan: ReturnRefundPlan = {
      sourceKind: "receipt",
      totalRefundCents: 1_000,
      online: true,
      lines: [refundLine(-1_000)],
      allocations: [
        {
          method,
          signedAmountCents: -1_000,
          originalCapacityId: `capacity-${method}`,
          originalOrderGuid: "order-original",
          offlineCashProof: null,
        },
      ],
    };

    const outcome = await harness.orchestrator.execute(receiptCommand(plan));

    assert.equal(outcome.status, "completed");
    assert.equal(harness.online.submitCalls[0]?.attemptKind, "hbpos-api");
    assert.equal(
      harness.ledger.prepareInputs[0]?.allocations[0]?.externalAttemptKind,
      null,
    );
    const stored = await harness.ledger.load("action-1");
    assert.equal(
      stored?.allocations[0]?.externalAttemptKind,
      "hbpos-api",
    );
  }
});

test("代替刷卡：现金代替走 Hbpos API 在线现金退款，代金券代替走券退款，均绑定原卡容量", async () => {
  // 现金代替：原卡 capacity + cash 退款方式（无离线证明）。
  const cashHarness = createHarness();
  const cashPlan: ReturnRefundPlan = {
    sourceKind: "receipt",
    totalRefundCents: 1_000,
    online: true,
    lines: [refundLine(-1_000)],
    allocations: [
      {
        method: "cash",
        signedAmountCents: -1_000,
        originalCapacityId: "capacity-card",
        originalOrderGuid: "order-original",
        offlineCashProof: null,
      },
    ],
  };
  const cashOutcome = await cashHarness.orchestrator.execute(
    receiptCommand(cashPlan),
  );
  assert.equal(cashOutcome.status, "completed");
  assert.equal(cashHarness.cash.submitCalls.length, 0);
  assert.equal(cashHarness.online.submitCalls[0]?.method, "cash");
  assert.equal(cashHarness.online.submitCalls[0]?.attemptKind, "hbpos-api");
  assert.equal(
    cashHarness.online.submitCalls[0]?.capacityId,
    "capacity-card",
  );

  // 代金券代替：原卡 capacity + voucher 退款方式。
  const voucherHarness = createHarness();
  const voucherPlan: ReturnRefundPlan = {
    sourceKind: "receipt",
    totalRefundCents: 1_000,
    online: true,
    lines: [refundLine(-1_000)],
    allocations: [
      {
        method: "voucher",
        signedAmountCents: -1_000,
        originalCapacityId: "capacity-card",
        originalOrderGuid: "order-original",
        offlineCashProof: null,
      },
    ],
  };
  const voucherOutcome = await voucherHarness.orchestrator.execute(
    receiptCommand(voucherPlan),
  );
  assert.equal(voucherOutcome.status, "completed");
  assert.equal(voucherHarness.online.submitCalls[0]?.method, "voucher");
  assert.equal(
    voucherHarness.online.submitCalls[0]?.capacityId,
    "capacity-card",
  );
});

test("同一门店设备收银员可在新 session 与更名后恢复原 action，且不二次 prepare/submit", async () => {
  const harness = createHarness();
  harness.online.submitOutcomes = [
    { status: "unknown", protectedRecoveryKey: null },
  ];
  const command = receiptCommand(onlineCardPlan());
  const unknown = await harness.orchestrator.execute(command);
  harness.identity.current = {
    ...harness.identity.current,
    cashierName: "Alice Renamed",
    sessionEpoch: "different-session",
  };

  const recovered = await harness.orchestrator.recover({
    actionId: command.actionId,
    recoveryKey: null,
  });

  assert.equal(unknown.status, "unknown");
  assert.equal(recovered.status, "completed");
  assert.equal(harness.ledger.prepareInputs.length, 1);
  assert.equal(harness.online.prepareCalls.length, 1);
  assert.equal(harness.online.submitCalls.length, 1);
  assert.equal(harness.online.recoverCalls.length, 1);
});

test("不同门店、设备或收银员均不能恢复原 action", async () => {
  const changedIdentities: readonly Partial<TrustedReturnIdentity>[] = [
    { storeCode: "S02" },
    { deviceCode: "IPAD-2" },
    { cashierId: "cashier-2" },
  ];

  for (const changed of changedIdentities) {
    const harness = createHarness();
    harness.online.submitOutcomes = [
      { status: "unknown", protectedRecoveryKey: null },
    ];
    const command = receiptCommand(onlineCardPlan());
    await harness.orchestrator.execute(command);
    harness.identity.current = {
      ...harness.identity.current,
      ...changed,
      sessionEpoch: "different-session",
    };

  await assert.rejects(
    () =>
      harness.orchestrator.recover({
        actionId: command.actionId,
          recoveryKey: null,
      }),
    hasReturnCode("RETURN_SESSION_EXPIRED"),
  );
  assert.equal(harness.online.recoverCalls.length, 0);
  }
});

function createHarness(input: Readonly<{
  onAllocationFinalized?(input: Readonly<{
    action: DurableReturnAction;
    allocation: DurableReturnAllocation;
  }>): Promise<boolean>;
}> = {}): Readonly<{
  orchestrator: DurableReturnExecutionOrchestrator;
  ledger: MemoryReturnLedger;
  cash: ScriptedCashRefund;
  online: ScriptedOnlineRefund;
  identity: MutableIdentity;
  lineMaterial: ScriptedLineMaterial;
  events: string[];
}> {
  const events: string[] = [];
  const ledger = new MemoryReturnLedger(events);
  const cash = new ScriptedCashRefund(events);
  const online = new ScriptedOnlineRefund(events);
  const identity = new MutableIdentity();
  const lineMaterial = new ScriptedLineMaterial();
  let nextId = 0;
  const options: DurableReturnExecutionOptions = {
    ledger,
    cashRefund: cash,
    onlineRefund: online,
    trustedIdentity: identity,
    lineMaterial,
    fingerprint: {
      digest: async ({ command, identity: trusted }) =>
        `fingerprint:${command.actionId}:${trusted.sessionEpoch}`,
    },
    createOpaqueId: (kind) => `${kind}-${++nextId}`,
    nowIso: () => "2026-07-28T01:02:03.000Z",
    onAllocationFinalized: input.onAllocationFinalized,
  };
  return {
    orchestrator: new DurableReturnExecutionOrchestrator(options),
    ledger,
    cash,
    online,
    identity,
    lineMaterial,
    events,
  };
}

class MutableIdentity {
  public current: TrustedReturnIdentity = {
    storeCode: "S01",
    deviceCode: "IPAD-1",
    cashierId: "cashier-1",
    cashierName: "Alice",
    sessionEpoch: "session-epoch-1",
  };

  public async getTrustedIdentity(): Promise<TrustedReturnIdentity> {
    return this.current;
  }
}

class ScriptedLineMaterial {
  public override: readonly DurableReturnLine[] | null = null;

  public async resolveForAction(input: Readonly<{
    plan: ReturnRefundPlan;
  }>): Promise<readonly DurableReturnLine[]> {
    return (
      this.override ??
      input.plan.lines.map((line, index) => ({
        lineId: `line-${index}`,
        selectionKey: `selection-${index}`,
        sourceKind: line.sourceKind,
        returnSourceKey: line.returnSourceKey,
        originalOrderGuid: line.originalOrderGuid,
        originalOrderDetailGuid: line.originalOrderDetailGuid,
        productCode: line.productCode,
        itemNumber: "100",
        lookupCode: "9300001",
        displayName: "Milk 2L",
        quantity: line.quantity,
        unitRefundCents: -line.signedAmountCents / line.quantity,
        signedAmountCents: line.signedAmountCents,
        availableQuantity:
          line.sourceKind === "receipt" ? line.quantity : null,
        remainingAmountCents:
          line.sourceKind === "receipt" ? -line.signedAmountCents : null,
        syncProvenance: line.syncProvenance,
      }))
    );
  }
}

class ScriptedCashRefund implements DurableOfflineCashRefundPort {
  public submitOutcomes: ReturnAllocationExternalOutcome[] = [
    { status: "completed" },
  ];
  public recoverOutcomes: ReturnAllocationExternalOutcome[] = [
    { status: "completed" },
  ];
  public readonly submitCalls: Parameters<
    DurableOfflineCashRefundPort["submit"]
  >[0][] = [];
  public readonly recoverCalls: Parameters<
    DurableOfflineCashRefundPort["recover"]
  >[0][] = [];

  public constructor(private readonly events: string[]) {}

  public async submit(
    input: Parameters<DurableOfflineCashRefundPort["submit"]>[0],
  ): Promise<ReturnAllocationExternalOutcome> {
    this.events.push("cash:submit");
    this.submitCalls.push(input);
    return this.submitOutcomes.shift() ?? { status: "completed" };
  }

  public async recover(
    input: Parameters<DurableOfflineCashRefundPort["recover"]>[0],
  ): Promise<ReturnAllocationExternalOutcome> {
    this.events.push("cash:recover");
    this.recoverCalls.push(input);
    return this.recoverOutcomes.shift() ?? { status: "completed" };
  }
}

class ScriptedOnlineRefund implements DurableOnlineReturnRefundPort {
  public submitOutcomes: ReturnAllocationExternalOutcome[] = [
    { status: "completed" },
  ];
  public recoverOutcomes: ReturnAllocationExternalOutcome[] = [
    { status: "completed" },
  ];
  public readonly submitCalls: Parameters<
    DurableOnlineReturnRefundPort["submit"]
  >[0][] = [];
  public readonly recoverCalls: Parameters<
    DurableOnlineReturnRefundPort["recover"]
  >[0][] = [];
  public readonly prepareCalls: Parameters<
    DurableOnlineReturnRefundPort["prepareAttempt"]
  >[0][] = [];

  public constructor(private readonly events: string[]) {}

  public async prepareAttempt(
    input: Parameters<DurableOnlineReturnRefundPort["prepareAttempt"]>[0],
  ): Promise<
    Awaited<ReturnType<DurableOnlineReturnRefundPort["prepareAttempt"]>>
  > {
    this.events.push(`online:prepare:${input.method}`);
    this.prepareCalls.push(input);
    const attemptKind =
      input.method === "cash" || input.method === "installment"
        ? "hbpos-api"
        : "payment-provider";
    return {
      attemptKind,
      externalActionId: `external-${input.externalAttemptId}`,
      durableAttemptId: `attempt-${input.externalAttemptId}`,
    };
  }

  public async submit(
    input: Parameters<DurableOnlineReturnRefundPort["submit"]>[0],
  ): Promise<ReturnAllocationExternalOutcome> {
    this.events.push(`online:submit:${input.method}`);
    this.submitCalls.push(input);
    return this.submitOutcomes.shift() ?? { status: "completed" };
  }

  public async recover(
    input: Parameters<DurableOnlineReturnRefundPort["recover"]>[0],
  ): Promise<ReturnAllocationExternalOutcome> {
    this.events.push(`online:recover:${input.method}`);
    this.recoverCalls.push(input);
    return this.recoverOutcomes.shift() ?? { status: "completed" };
  }
}

class MemoryReturnLedger implements ReturnExecutionLedgerPort {
  public readonly prepareInputs: PrepareDurableReturnAction[] = [];
  public readonly completeInputs: CompleteDurableReturnAction[] = [];
  public failRecordOutcomeOnce = false;
  public loseSubmittedResponseOnce = false;
  public loseCompleteResponseOnce = false;
  private readonly records = new Map<string, DurableReturnAction>();

  public constructor(private readonly events: string[]) {}

  public async prepareOrLoad(
    draft: PrepareDurableReturnAction,
  ): Promise<DurableReturnAction> {
    this.prepareInputs.push(clone(draft));
    const existing = this.records.get(draft.actionId);
    if (existing) return clone(existing);
    this.events.push("ledger:prepare");
    const created: DurableReturnAction = {
      ...clone(draft),
      status: "processing",
      completedAtIso: null,
    };
    this.records.set(draft.actionId, created);
    return clone(created);
  }

  public async load(actionId: string): Promise<DurableReturnAction | null> {
    const action = this.records.get(actionId);
    return action ? clone(action) : null;
  }

  public async markAllocationSubmitted(input: Readonly<{
    actionId: string;
    allocationId: string;
  }>): Promise<boolean> {
    const changed = this.changeAllocation(
      input.actionId,
      input.allocationId,
      (allocation) =>
        allocation.status === "created"
          ? { ...allocation, status: "submitted" }
          : null,
    );
    if (changed) {
      const allocation = this.findAllocation(
        input.actionId,
        input.allocationId,
      );
      this.events.push(`ledger:submitted:${allocation.index}`);
    }
    if (changed && this.loseSubmittedResponseOnce) {
      this.loseSubmittedResponseOnce = false;
      throw new Error("response lost after submitted commit");
    }
    return changed;
  }

  public async bindAllocationAttempt(input: Readonly<{
    actionId: string;
    allocationId: string;
    attemptKind: "payment-provider" | "hbpos-api";
    externalActionId: string;
    durableAttemptId: string;
  }>): Promise<boolean> {
    const changed = this.changeAllocation(
      input.actionId,
      input.allocationId,
      (allocation) => {
        if (
          allocation.externalAttemptKind === input.attemptKind &&
          allocation.externalActionId === input.externalActionId &&
          allocation.durableAttemptId === input.durableAttemptId
        ) {
          return null;
        }
        if (
          allocation.externalAttemptKind !== null ||
          allocation.externalActionId !== null ||
          allocation.durableAttemptId !== null
        ) {
          throw new Error("attempt binding collision");
        }
        return {
          ...allocation,
          externalAttemptKind: input.attemptKind,
          externalActionId: input.externalActionId,
          durableAttemptId: input.durableAttemptId,
        };
      },
    );
    if (changed) {
      const allocation = this.findAllocation(
        input.actionId,
        input.allocationId,
      );
      this.events.push(`ledger:bound:${allocation.index}`);
    }
    return changed;
  }

  public async recordAllocationOutcome(input: Readonly<{
    actionId: string;
    allocationId: string;
    expectedStatuses: readonly ("submitted" | "unknown")[];
    status: "completed" | "declined" | "unknown";
    protectedRecoveryKey: string | null;
  }>): Promise<boolean> {
    if (this.failRecordOutcomeOnce) {
      this.failRecordOutcomeOnce = false;
      throw new Error("crash after provider response");
    }
    const changed = this.changeAllocation(
      input.actionId,
      input.allocationId,
      (allocation) =>
        input.expectedStatuses.includes(
          allocation.status as "submitted" | "unknown",
        )
          ? {
              ...allocation,
              status: input.status,
              protectedRecoveryKey: input.protectedRecoveryKey,
            }
          : null,
    );
    if (changed) {
      const allocation = this.findAllocation(
        input.actionId,
        input.allocationId,
      );
      this.events.push(`ledger:${input.status}:${allocation.index}`);
    }
    return changed;
  }

  public async markActionUnknown(input: Readonly<{
    actionId: string;
  }>): Promise<void> {
    this.changeAction(input.actionId, (action) => ({
      ...action,
      status: "unknown",
    }));
  }

  public async resumeUnknownAction(input: Readonly<{
    actionId: string;
  }>): Promise<boolean> {
    const action = this.require(input.actionId);
    if (action.status !== "unknown") return false;
    this.records.set(input.actionId, {
      ...action,
      status: "processing",
    });
    return true;
  }

  public async markActionDeclined(input: Readonly<{
    actionId: string;
  }>): Promise<void> {
    this.changeAction(input.actionId, (action) => ({
      ...action,
      status: "declined",
    }));
  }

  public async completeAtomically(
    input: CompleteDurableReturnAction,
  ): Promise<DurableReturnAction> {
    this.completeInputs.push(clone(input));
    const action = this.require(input.actionId);
    if (
      action.returnOrderGuid !== input.returnOrderGuid ||
      action.allocations.some(
        (allocation) => allocation.status !== "completed",
      )
    ) {
      throw new Error("completion invariant failed");
    }
    this.events.push("ledger:complete");
    const completed: DurableReturnAction = {
      ...action,
      status: "completed",
      completedAtIso: input.completedAtIso,
    };
    this.records.set(input.actionId, completed);
    if (this.loseCompleteResponseOnce) {
      this.loseCompleteResponseOnce = false;
      throw new Error("response lost after atomic completion");
    }
    return clone(completed);
  }

  private changeAction(
    actionId: string,
    change: (action: DurableReturnAction) => DurableReturnAction,
  ): void {
    this.records.set(actionId, change(this.require(actionId)));
  }

  private changeAllocation(
    actionId: string,
    allocationId: string,
    change: (
      allocation: DurableReturnAllocation,
    ) => DurableReturnAllocation | null,
  ): boolean {
    const action = this.require(actionId);
    let changed = false;
    const allocations = action.allocations.map((allocation) => {
      if (allocation.allocationId !== allocationId) return allocation;
      const next = change(allocation);
      if (!next) return allocation;
      changed = true;
      return next;
    });
    if (changed) this.records.set(actionId, { ...action, allocations });
    return changed;
  }

  private findAllocation(
    actionId: string,
    allocationId: string,
  ): DurableReturnAllocation {
    const allocation = this.require(actionId).allocations.find(
      (candidate) => candidate.allocationId === allocationId,
    );
    if (!allocation) throw new Error("allocation missing");
    return allocation;
  }

  private require(actionId: string): DurableReturnAction {
    const action = this.records.get(actionId);
    if (!action) throw new Error("action missing");
    return action;
  }
}

function receiptCommand(plan: ReturnRefundPlan): ReturnExecutionCommand {
  return {
    actionId: "action-1",
    plan,
    noReceiptAuthorizationKey: null,
  };
}

function offlineCashPlan(): ReturnRefundPlan {
  return {
    sourceKind: "receipt",
    totalRefundCents: 1_000,
    online: false,
    lines: [refundLine(-1_000)],
    allocations: [
      {
        method: "cash",
        signedAmountCents: -1_000,
        originalCapacityId: "capacity-cash",
        originalOrderGuid: "order-original",
        offlineCashProof: {
          evidenceId: "proof-cash",
          capacityId: "capacity-cash",
          originalOrderGuid: "order-original",
          remainingCents: 1_000,
        },
      },
    ],
  };
}

function onlineCardPlan(): ReturnRefundPlan {
  return {
    sourceKind: "receipt",
    totalRefundCents: 1_000,
    online: true,
    lines: [refundLine(-1_000)],
    allocations: [
      {
        method: "card",
        signedAmountCents: -1_000,
        originalCapacityId: "capacity-card",
        originalOrderGuid: "order-original",
        offlineCashProof: null,
      },
    ],
  };
}

function mixedPlan(): ReturnRefundPlan {
  return {
    sourceKind: "receipt",
    totalRefundCents: 1_000,
    online: true,
    lines: [refundLine(-1_000)],
    allocations: [
      {
        method: "cash",
        signedAmountCents: -200,
        originalCapacityId: "capacity-cash",
        originalOrderGuid: "order-original",
        offlineCashProof: {
          evidenceId: "proof-cash",
          capacityId: "capacity-cash",
          originalOrderGuid: "order-original",
          remainingCents: 200,
        },
      },
      {
        method: "card",
        signedAmountCents: -500,
        originalCapacityId: "capacity-card",
        originalOrderGuid: "order-original",
        offlineCashProof: null,
      },
      {
        method: "voucher",
        signedAmountCents: -300,
        originalCapacityId: "capacity-voucher",
        originalOrderGuid: "order-original",
        offlineCashProof: null,
      },
    ],
  };
}

function planForMethods(
  methods: readonly ("cash" | "card" | "voucher")[],
): ReturnRefundPlan {
  const amountPerMethod = 100;
  const totalRefundCents = amountPerMethod * methods.length;
  return {
    sourceKind: "receipt",
    totalRefundCents,
    online: methods.some((method) => method !== "cash"),
    lines: [refundLine(-totalRefundCents)],
    allocations: methods.map((method, index) => ({
      method,
      signedAmountCents: -amountPerMethod,
      originalCapacityId: `capacity-${method}-${index}`,
      originalOrderGuid: "order-original",
      offlineCashProof:
        method === "cash"
          ? {
              evidenceId: `proof-${index}`,
              capacityId: `capacity-${method}-${index}`,
              originalOrderGuid: "order-original",
              remainingCents: amountPerMethod,
            }
          : null,
    })),
  };
}

function refundLine(signedAmountCents: number) {
  return {
    sourceKind: "receipt" as const,
    returnSourceKey: "return-source-1",
    originalOrderGuid: "order-original",
    originalOrderDetailGuid: "detail-original",
    productCode: "P100",
    quantity: 1,
    signedAmountCents,
    syncProvenance: {
      referenceCode: "RECEIPT-REF",
      priceSource: 0 as const,
    },
  };
}

function clone<T>(value: T): T {
  return structuredClone(value);
}

function hasReturnCode(
  code: ReturnFeatureError["code"],
): (error: unknown) => boolean {
  return (error) =>
    error instanceof ReturnFeatureError && error.code === code;
}

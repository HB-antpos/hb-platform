import assert from "node:assert/strict";
import test from "node:test";

import {
  PaymentActionBindingConflictError,
  PaymentAttemptBlockedError,
  PaymentAttemptDurabilityError,
  PaymentAttemptOfflineError,
  PaymentAttemptReferenceSeedError,
  PaymentAttemptService,
  PaymentAttemptStateError,
  type PaymentActionBinding,
  type PaymentActionBindingPort,
  type PaymentAttemptLedgerPort,
  type LegacyPaymentRecoveryEnvironmentResolver,
  type PersistedOrderDraftPort,
  type TrustedRefundReferenceSeed,
  type TrustedRefundReferenceSeedHook,
  type TrustedRefundReferenceSeedInput,
} from "./payment-attempt-service";
import { createLinklyAttemptTxnRef } from "./linkly-attempt-txn-ref";

import type { CardSyncEvidenceV1, OnlinePaymentPort, PaymentAttempt, PaymentProvider, PaymentProviderReferences, PaymentProviderResult } from "@hb/pos-domain/core/contracts/payment";
import type { Money } from "@hb/pos-domain/core/contracts/money";

const amount: Money = { currency: "AUD", cents: 1_250 };

test("订单草稿、Created attempt 与 Submitted 状态未持久化前绝不调用 provider", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("square");
  const drafts = new DraftGuard();
  const service = createService({ ledger, provider, drafts });

  drafts.fail = true;
  await assert.rejects(() => service.startAttempt(input()), /draft/i);
  assert.equal(ledger.attempts.size, 0);
  assert.equal(provider.submitCalls, 0);

  drafts.fail = false;
  ledger.failNextInsert = true;
  await assert.rejects(() => service.startAttempt(input()), /insert failed/i);
  assert.equal(provider.submitCalls, 0);

  ledger.failNextUpdate = true;
  await assert.rejects(() => service.startAttempt(input()), /update failed/i);
  assert.equal(provider.submitCalls, 0);
  assert.equal([...ledger.attempts.values()][0]?.state, "Created");
});

test("退款 attempt 与不可变签名保留负数账本金额，并只调用 refund provider", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("square");
  provider.refundResult = async (value) => {
    assert.equal(value.operation, "refund");
    assert.equal(value.amount.cents, -1_250);
    return result("Approved", { paymentId: "payment-original" });
  };
  const service = createService({ ledger, provider });

  const completed = await service.startAttempt({
    ...input(),
    actionId: "action-refund-1",
    operation: "refund",
    amount: { currency: "AUD", cents: -1_250 },
  });

  assert.equal(completed.attempt.operation, "refund");
  assert.equal(completed.attempt.amount.cents, -1_250);
  assert.equal(
    [...ledger.attempts.values()][0]?.amount.cents,
    -1_250,
  );
  assert.equal(provider.submitCalls, 0);
  assert.equal(provider.refundCalls, 1);
});

test("Approved 卡证据与状态使用同一 CAS 加密交付，公开 attempt 不携带受保护材料", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("square");
  provider.submitResult = async () =>
    result("Approved", { paymentId: "payment-attempt-1" });
  const service = createService({ ledger, provider });

  const completed = await service.startAttempt(input());
  const protectedEvidence = ledger.protectedEvidence.get(
    completed.attempt.attemptId,
  );

  assert.deepEqual(
    protectedEvidence,
    cardEvidence(completed.attempt, "payment-attempt-1"),
  );
  assert.equal("protectedSyncEvidence" in completed.attempt, false);
  assert.equal("protectedSyncEvidence" in completed, false);
});

test("Linkly Created 在 provider 边界冻结环境；后续配置变化的 recover 仍使用原环境", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("linkly-cloud");
  provider.providerEnvironment = "Sandbox";
  provider.submitResult = async (value) => {
    assert.equal(value.providerEnvironment, "Sandbox");
    return result("Pending", { sessionId: "linkly-session-1" });
  };
  const service = createService({ ledger, provider });
  const pending = await service.startAttempt({ ...input(), provider: "linkly-cloud" });
  assert.equal(pending.attempt.providerEnvironment, "Sandbox");

  provider.providerEnvironment = "Production";
  provider.recoverResult = async (value) => {
    assert.equal(value.providerEnvironment, "Sandbox");
    return result("Declined", { sessionId: "linkly-session-1" });
  };
  await service.recoverAttempt(pending.attempt.attemptId);
  assert.equal(provider.recoverCalls, 1);
});

test("Linkly 缺少可冻结环境时在 Created/provider 调用前失败关闭", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("linkly-cloud");
  delete provider.providerEnvironment;
  const service = createService({ ledger, provider });

  await assert.rejects(
    () => service.startAttempt({ ...input(), provider: "linkly-cloud" }),
    PaymentAttemptStateError,
  );
  assert.equal(ledger.attempts.size, 0);
  assert.equal(provider.submitCalls, 0);
});

test("Linkly 在 Created→Submitted 的同一次 CAS 预置由 attemptId 派生的 TxnRef，早于 provider 边界", async () => {
  const guid = "3b241101-e2bb-4255-8caf-4136c566a962";
  for (const operation of ["purchase", "refund"] as const) {
    const ledger = new MemoryLedger();
    const provider = new FakeProvider("linkly-cloud");
    const expected = createLinklyAttemptTxnRef(operation === "refund" ? "R" : "P", guid);
    let persistedAtBoundary: string | null | undefined;
    const run = async (value: PaymentAttempt) => {
      // provider 看到的 attempt 与已落库的 Submitted 行必须带同一个引用。
      persistedAtBoundary = ledger.attempts.get(value.attemptId)?.references.txnRef;
      assert.equal(value.references.txnRef, expected);
      return result("Pending", { sessionId: "linkly-session-1", txnRef: expected });
    };
    provider.submitResult = run;
    provider.refundResult = run;
    const service = createService({
      ledger,
      provider,
      createAttemptId: () => guid,
      ...(operation === "refund"
        ? { trustedRefundReferenceSeed: async () => ({ provider: "linkly-cloud" as const, rfn: "RFN-ORIGINAL" }) }
        : {}),
    });

    const started = await service.startAttempt({
      ...input(),
      provider: "linkly-cloud",
      operation,
      amount: operation === "refund" ? { currency: "AUD", cents: -1_250 } : amount,
      ...(operation === "refund" ? { refundCapacityId: "capacity-1" } : {}),
    });

    assert.equal(persistedAtBoundary, expected);
    assert.equal(started.attempt.references.txnRef, expected);
    assert.equal(ledger.attempts.get(guid)?.references.txnRef, expected);
  }
});

test("Linkly attemptId 不是标准 GUID 时不预置 TxnRef（沿用 UID 认领路径），其他 provider 不受影响", async () => {
  const linkly = new FakeProvider("linkly-cloud");
  linkly.submitResult = async (value) => {
    assert.equal(value.references.txnRef, null);
    return result("Pending", { sessionId: "linkly-session-1" });
  };
  await createService({ ledger: new MemoryLedger(), provider: linkly }).startAttempt({ ...input(), provider: "linkly-cloud" });
  assert.equal(linkly.submitCalls, 1);

  const square = new FakeProvider("square");
  square.submitResult = async (value) => {
    assert.equal(value.references.txnRef, null);
    return result("Pending", { paymentId: "payment-1" });
  };
  await createService({
    ledger: new MemoryLedger(),
    provider: square,
    createAttemptId: () => "3b241101-e2bb-4255-8caf-4136c566a962",
  }).startAttempt(input());
  assert.equal(square.submitCalls, 1);
});

test("旧 Hbpos.Api 忽略 attemptGuid 返回随机 TxnRef 时，创建响应带回的引用取代派生值，不触发引用冲突丢 SessionId", async () => {
  const guid = "3b241101-e2bb-4255-8caf-4136c566a962";
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("linkly-cloud");
  provider.submitResult = async () => result("Pending", { sessionId: "linkly-session-1", txnRef: "260921123456AB12" });
  const service = createService({ ledger, provider, createAttemptId: () => guid });

  const started = await service.startAttempt({ ...input(), provider: "linkly-cloud" });

  assert.equal(started.attempt.state, "Pending");
  assert.equal(started.attempt.references.sessionId, "linkly-session-1");
  assert.equal(started.attempt.references.txnRef, "260921123456AB12");

  // 已绑定会话之后，任何与已落库 TxnRef 不一致的响应仍是引用冲突，必须失败关闭。
  provider.recoverResult = async () => result("Pending", { sessionId: "linkly-session-1", txnRef: "PAAAAAAAAAAAAAAA" });
  const conflicted = await service.recoverAttempt(guid);
  assert.equal(conflicted.attempt.state, "Unknown");
  assert.equal(conflicted.attempt.lastErrorCode, "PROVIDER_REFERENCE_CONFLICT");
  assert.equal(conflicted.attempt.references.txnRef, "260921123456AB12");
});

test("旧 Linkly Submitted 先强匹配冻结环境，再只恢复原 session；不匹配时不访问 provider", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("linkly-cloud");
  provider.recoverResult = async (value) => {
    assert.equal(value.providerEnvironment, "Sandbox");
    assert.equal(value.references.sessionId, "legacy-session");
    return result("Declined", { sessionId: "legacy-session" });
  };
  const submitted = attempt({
    attemptId: "legacy-submitted",
    idempotencyKey: "legacy-key",
    provider: "linkly-cloud",
    state: "Submitted",
    references: references({ sessionId: "legacy-session" }),
  });
  ledger.seed(submitted, true);
  const service = createService({
    ledger,
    provider,
    legacyPaymentRecoveryEnvironment: async (value) => {
      assert.equal(value.attemptId, submitted.attemptId);
      return "Sandbox";
    },
  });

  const recovered = await service.recoverAttempt(submitted.attemptId);
  assert.equal(recovered.attempt.state, "Declined");
  assert.equal(recovered.attempt.providerEnvironment, "Sandbox");
  assert.equal(provider.recoverCalls, 1);

  const mismatchLedger = new MemoryLedger();
  const mismatchProvider = new FakeProvider("linkly-cloud");
  const mismatch = { ...submitted, attemptId: "legacy-mismatch" };
  mismatchLedger.seed(mismatch, true);
  const mismatchService = createService({
    ledger: mismatchLedger,
    provider: mismatchProvider,
    legacyPaymentRecoveryEnvironment: async () => null,
  });
  const blocked = await mismatchService.recoverAttempt(mismatch.attemptId);
  assert.equal(blocked.attempt.state, "Unknown");
  assert.equal(blocked.attempt.lastErrorCode, "LINKLY_RECOVERY_ENVIRONMENT_REQUIRED");
  assert.equal(mismatchProvider.recoverCalls, 0);
});

test("旧 Linkly 无 session 的 Submitted attempt 只进入明确 Unknown，不猜当前环境或 create", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("linkly-cloud");
  const submitted = attempt({
    attemptId: "legacy-no-session",
    provider: "linkly-cloud",
    state: "Submitted",
    references: references(),
  });
  ledger.seed(submitted, true);
  const service = createService({ ledger, provider });
  const blocked = await service.recoverAttempt(submitted.attemptId);
  assert.equal(blocked.attempt.state, "Unknown");
  assert.equal(blocked.attempt.lastErrorCode, "LINKLY_RECOVERY_ENVIRONMENT_REQUIRED");
  assert.equal(provider.submitCalls, 0);
  assert.equal(provider.recoverCalls, 0);
});

test("旧 Linkly reconciliation 被停止时保留原 Submitted，不因 resolver 异常写 Unknown", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("linkly-cloud");
  const submitted = attempt({
    attemptId: "legacy-aborted",
    provider: "linkly-cloud",
    state: "Submitted",
    references: references({ sessionId: "legacy-session" }),
  });
  ledger.seed(submitted, true);
  const controller = new AbortController();
  const service = createService({
    ledger,
    provider,
    legacyPaymentRecoveryEnvironment: async (value, control) => {
      assert.equal(value.attemptId, submitted.attemptId);
      assert.equal(control?.signal, controller.signal);
      controller.abort();
      return "Sandbox";
    },
  });

  const recovered = await service.recoverAttempt(submitted.attemptId, {
    signal: controller.signal,
    deadlineAtMs: Date.now() + 180_000,
  });
  assert.equal(recovered.attempt.state, "Submitted");
  assert.equal(recovered.attempt.providerEnvironment, undefined);
  assert.equal(provider.recoverCalls, 0);
});

test("Approved 卡响应缺失或换绑证据时进入 Unknown，绝不持久化或宣称成功", async () => {
  for (const [name, evidence] of [
    ["missing", null],
    [
      "amount-mismatch",
      {
        ...cardEvidence(attempt({ state: "Submitted" }), "payment-attempt-1"),
        amountCents: amount.cents + 1,
      },
    ],
  ] as const) {
    const ledger = new MemoryLedger();
    const provider = new FakeProvider("square");
    provider.submitResult = async () => ({
      ...result("Approved", { paymentId: "payment-attempt-1" }),
      protectedSyncEvidence: evidence,
    });
    const service = createService({ ledger, provider });

    const completed = await service.startAttempt({
      ...input(),
      actionId: `action-${name}`,
      orderGuid: `order-${name}`,
    });

    assert.equal(completed.attempt.state, "Unknown");
    assert.equal(
      completed.attempt.lastErrorCode,
      "PROVIDER_SYNC_EVIDENCE_INVALID",
    );
    assert.equal(
      ledger.protectedEvidence.has(completed.attempt.attemptId),
      false,
    );
  }
});

test("purchase 不调用 trusted refund seed hook，且首次 provider 调用从空 references 开始", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("square");
  let hookCalls = 0;
  provider.submitResult = async (value) => {
    assert.deepEqual(value.references, references());
    return result("Approved");
  };
  const service = createService({
    ledger,
    provider,
    trustedRefundReferenceSeed: async (seedInput) => {
      hookCalls += 1;
      return trustedRefundSeed(seedInput, "must-not-be-used");
    },
  });

  const completed = await service.startAttempt(input());

  assert.equal(hookCalls, 0);
  assert.deepEqual(completed.attempt.references, references());
  assert.equal(provider.submitCalls, 1);
  assert.equal(provider.refundCalls, 0);
});

test("purchase 携带 refundCapacityId 必须在绑定和 provider 前拒绝", async () => {
  const ledger = new MemoryLedger();
  const bindings = new MemoryActionBindings();
  const provider = new FakeProvider("square");
  let hookCalls = 0;
  const service = createService({
    ledger,
    bindings,
    provider,
    trustedRefundReferenceSeed: async (seedInput) => {
      hookCalls += 1;
      return trustedRefundSeed(seedInput, "must-not-be-used");
    },
  });

  await assert.rejects(
    () =>
      service.startAttempt({
        ...input(),
        refundCapacityId: "capacity-not-valid-for-purchase",
      }),
    /refundCapacityId.*refund/i,
  );
  assert.equal(bindings.bindings.size, 0);
  assert.equal(ledger.attempts.size, 0);
  assert.equal(hookCalls, 0);
  assert.equal(provider.submitCalls, 0);
  assert.equal(provider.refundCalls, 0);
});

test("配置 trusted hook 的 refund 缺少 capacityId 时 fail closed，且不创建 attempt", async () => {
  const ledger = new MemoryLedger();
  const bindings = new MemoryActionBindings();
  const provider = new FakeProvider("square");
  let hookCalls = 0;
  const service = createService({
    ledger,
    bindings,
    provider,
    trustedRefundReferenceSeed: async (seedInput) => {
      hookCalls += 1;
      return trustedRefundSeed(seedInput, "must-not-be-used");
    },
  });

  await assert.rejects(
    () =>
      service.startAttempt({
        ...input(),
        actionId: "action-refund-without-capacity",
        orderGuid: "order-refund-without-capacity",
        operation: "refund",
        amount: { currency: "AUD", cents: -1_250 },
      }),
    PaymentAttemptReferenceSeedError,
  );
  assert.equal(bindings.bindings.size, 1);
  assert.equal(ledger.attempts.size, 0);
  assert.equal(hookCalls, 0);
  assert.equal(provider.submitCalls, 0);
  assert.equal(provider.refundCalls, 0);
});

test("refund 在 action 绑定与在线门禁后、Created 持久化前注入各 provider 的唯一受保护引用", async () => {
  const cases = [
    {
      providerName: "square",
      protectedReference: "payment-original",
      expectedReferences: references({ paymentId: "payment-original" }),
    },
    {
      providerName: "linkly-cloud",
      protectedReference: "rfn-original",
      expectedReferences: references({ rfn: "rfn-original" }),
    },
  ] as const;

  for (const [index, current] of cases.entries()) {
    const ledger = new MemoryLedger();
    const bindings = new MemoryActionBindings();
    const provider = new FakeProvider(current.providerName);
    let hookCalls = 0;
    provider.refundResult = async (value) => {
      assert.deepEqual(value.references, current.expectedReferences);
      return {
        ...result("Approved"),
        references: current.expectedReferences,
      };
    };
    const service = createService({
      ledger,
      bindings,
      provider,
      trustedRefundReferenceSeed: async (seedInput) => {
        hookCalls += 1;
        assert.equal(bindings.bindings.size, 1);
        assert.equal(ledger.attempts.size, 0);
        assert.equal(seedInput.identity.attemptId, "attempt-1");
        assert.equal(seedInput.identity.idempotencyKey, "key-1");
        assert.equal(seedInput.identity.orderGuid, `order-seeded-${index}`);
        assert.equal(Object.isFrozen(seedInput), true);
        assert.equal(Object.isFrozen(seedInput.identity), true);
        assert.equal(Object.isFrozen(seedInput.action), true);
        assert.equal(Object.isFrozen(seedInput.capacity), true);
        assert.equal(Object.isFrozen(seedInput.capacity.amount), true);
        assert.equal(seedInput.provider, current.providerName);
        assert.equal(seedInput.operation, "refund");
        assert.equal(seedInput.action.actionId, `action-seeded-${index}`);
        assert.equal(
          seedInput.action.requestSignature,
          JSON.stringify([
            current.providerName,
            "refund",
            "AUD",
            -1_250,
            `capacity-seeded-${index}`,
          ]),
        );
        assert.equal(seedInput.capacity.actionId, seedInput.action.actionId);
        assert.equal(seedInput.capacity.orderGuid, seedInput.action.orderGuid);
        assert.equal(seedInput.capacity.provider, current.providerName);
        assert.equal(seedInput.capacity.operation, "refund");
        assert.equal(seedInput.capacity.amount.cents, -1_250);
        assert.equal(
          seedInput.capacity.capacityId,
          `capacity-seeded-${index}`,
        );
        return trustedRefundSeed(seedInput, current.protectedReference);
      },
    });

    const completed = await service.startAttempt(
      refundInput(index, current.providerName),
    );

    assert.equal(hookCalls, 1);
    assert.equal(provider.submitCalls, 0);
    assert.equal(provider.refundCalls, 1);
    assert.deepEqual(completed.attempt.references, current.expectedReferences);
    assert.deepEqual(
      ledger.attempts.get(completed.attempt.attemptId)?.references,
      current.expectedReferences,
    );
  }
});

test("voucher refund 即使配置 trusted seed hook 也从空 references 开始", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("voucher");
  let hookCalls = 0;
  provider.refundResult = async (value) => {
    assert.deepEqual(value.references, references());
    return result("Approved", {
      voucherReservationToken: "voucher-new-refund-context",
    });
  };
  const service = createService({
    ledger,
    provider,
    trustedRefundReferenceSeed: async (seedInput) => {
      hookCalls += 1;
      return trustedRefundSeed(seedInput, "must-not-be-used");
    },
  });

  const completed = await service.startAttempt(refundInput(29, "voucher"));

  assert.equal(hookCalls, 0);
  assert.equal(provider.refundCalls, 1);
  assert.equal(
    completed.attempt.references.voucherReservationToken,
    "voucher-new-refund-context",
  );
});

test("prepare Square/Linkly refund 只耐久 Created 与受保护引用，随后 start 同 action 才调用一次 provider", async () => {
  const cases = [
    {
      providerName: "square",
      protectedReference: "payment-prepare-original",
      expectedReferences: references({
        paymentId: "payment-prepare-original",
      }),
    },
    {
      providerName: "linkly-cloud",
      protectedReference: "rfn-prepare-original",
      expectedReferences: references({
        rfn: "rfn-prepare-original",
      }),
    },
  ] as const;

  for (const [index, current] of cases.entries()) {
    const ledger = new MemoryLedger();
    const provider = new FakeProvider(current.providerName);
    let hookCalls = 0;
    provider.refundResult = async (value) => ({
      ...result("Approved"),
      references: value.references,
    });
    const service = createService({
      ledger,
      provider,
      trustedRefundReferenceSeed: async (seedInput) => {
        hookCalls += 1;
        return trustedRefundSeed(
          seedInput,
          current.protectedReference,
        );
      },
    });
    const startInput = refundInput(40 + index, current.providerName);

    const prepared = await service.prepareAttempt(startInput);

    assert.equal(prepared.attempt.state, "Created");
    assert.deepEqual(
      prepared.attempt.references,
      current.expectedReferences,
    );
    assert.deepEqual(
      ledger.attempts.get(prepared.attempt.attemptId)?.references,
      current.expectedReferences,
    );
    assert.equal(hookCalls, 1);
    assertProviderCalls(provider, {
      submit: 0,
      recover: 0,
      cancel: 0,
      refund: 0,
    });

    const replayed = await service.prepareAttempt(startInput);

    assert.equal(replayed.attempt.state, "Created");
    assert.equal(
      replayed.attempt.attemptId,
      prepared.attempt.attemptId,
    );
    assert.equal(
      replayed.attempt.idempotencyKey,
      prepared.attempt.idempotencyKey,
    );
    assert.equal(hookCalls, 1);
    assertProviderCalls(provider, {
      submit: 0,
      recover: 0,
      cancel: 0,
      refund: 0,
    });

    const started = await service.startAttempt(startInput);

    assert.equal(started.attempt.state, "Approved");
    assert.equal(started.attempt.attemptId, prepared.attempt.attemptId);
    assert.equal(
      started.attempt.idempotencyKey,
      prepared.attempt.idempotencyKey,
    );
    assert.equal(provider.refundCalls, 1);

    const observed = await service.prepareAttempt(startInput);

    assert.equal(observed.attempt.state, "Approved");
    assert.equal(observed.attempt.attemptId, prepared.attempt.attemptId);
    assert.equal(provider.refundCalls, 1);
    assert.equal(provider.recoverCalls, 0);
  }
});

test("prepare purchase 保持空 references；模拟崩溃重建 service 后 start 复用同一 attempt 并只 submit 一次", async () => {
  const ledger = new MemoryLedger();
  const bindings = new MemoryActionBindings();
  const preparingProvider = new FakeProvider("square");
  const preparingService = createService({
    ledger,
    bindings,
    provider: preparingProvider,
  });

  const prepared = await preparingService.prepareAttempt(input());

  assert.equal(prepared.attempt.state, "Created");
  assert.deepEqual(prepared.attempt.references, references());
  assertProviderCalls(preparingProvider, {
    submit: 0,
    recover: 0,
    cancel: 0,
    refund: 0,
  });

  const restartedProvider = new FakeProvider("square");
  const restartedService = createService({
    ledger,
    bindings,
    provider: restartedProvider,
  });
  const started = await restartedService.startAttempt(input());

  assert.equal(started.attempt.state, "Approved");
  assert.equal(started.attempt.attemptId, prepared.attempt.attemptId);
  assert.equal(
    started.attempt.idempotencyKey,
    prepared.attempt.idempotencyKey,
  );
  assert.equal(restartedProvider.submitCalls, 1);
  assert.equal(restartedProvider.recoverCalls, 0);
  assert.equal(ledger.attempts.size, 1);
});

test("prepare 已存在的非 Created attempt 只返回真实现状，不恢复或改变状态", async () => {
  for (const state of [
    "Submitted",
    "Pending",
    "Unknown",
    "Approved",
    "Declined",
    "Cancelled",
  ] as const) {
    const ledger = new MemoryLedger();
    const bindings = new MemoryActionBindings();
    const provider = new FakeProvider("square");
    const binding: PaymentActionBinding = {
      orderGuid: "order-1",
      actionId: "action-1",
      requestSignature: JSON.stringify([
        "square",
        "purchase",
        "AUD",
        amount.cents,
      ]),
      attemptId: `attempt-prepare-${state}`,
      idempotencyKey: `key-prepare-${state}`,
      createdAtIso: "2026-07-28T00:00:00.000Z",
      actor: auditActor(),
    };
    await bindings.bindOrGet(binding);
    ledger.seed(
      attempt({
        attemptId: binding.attemptId,
        idempotencyKey: binding.idempotencyKey,
        state,
        createdAtIso: binding.createdAtIso,
        updatedAtIso: "2026-07-28T00:00:10.000Z",
      }),
      true,
    );
    const service = createService({
      ledger,
      bindings,
      provider,
      online: false,
    });

    const observed = await service.prepareAttempt(input());

    assert.equal(observed.attempt.state, state);
    assert.equal(
      observed.attempt.updatedAtIso,
      "2026-07-28T00:00:10.000Z",
    );
    assert.equal(
      ledger.attempts.get(binding.attemptId)?.state,
      state,
    );
    assertProviderCalls(provider, {
      submit: 0,
      recover: 0,
      cancel: 0,
      refund: 0,
    });
  }
});

test("prepare 离线时只保留 action binding；恢复在线后复用原 IDs 创建 Created", async () => {
  const ledger = new MemoryLedger();
  const bindings = new MemoryActionBindings();
  const offlineProvider = new FakeProvider("square");
  const offline = createService({
    ledger,
    bindings,
    provider: offlineProvider,
    online: false,
  });

  await assert.rejects(
    () => offline.prepareAttempt(input()),
    PaymentAttemptOfflineError,
  );

  const bound = [...bindings.bindings.values()][0];
  assert.ok(bound);
  assert.equal(ledger.attempts.size, 0);
  assertProviderCalls(offlineProvider, {
    submit: 0,
    recover: 0,
    cancel: 0,
    refund: 0,
  });

  const onlineProvider = new FakeProvider("square");
  const online = createService({
    ledger,
    bindings,
    provider: onlineProvider,
  });
  const prepared = await online.prepareAttempt(input());

  assert.equal(prepared.attempt.state, "Created");
  assert.equal(prepared.attempt.attemptId, bound.attemptId);
  assert.equal(prepared.attempt.idempotencyKey, bound.idempotencyKey);
  assert.equal(ledger.attempts.size, 1);
  assertProviderCalls(onlineProvider, {
    submit: 0,
    recover: 0,
    cancel: 0,
    refund: 0,
  });
});

test("prepare 的 request/capacity 签名冲突在 hook 与 provider 前拒绝", async () => {
  const ledger = new MemoryLedger();
  const bindings = new MemoryActionBindings();
  const square = new FakeProvider("square");
  const linkly = new FakeProvider("linkly-cloud");
  let hookCalls = 0;
  const service = createService({
    ledger,
    bindings,
    providers: [square, linkly],
    trustedRefundReferenceSeed: async (seedInput) => {
      hookCalls += 1;
      return trustedRefundSeed(seedInput, "payment-original");
    },
  });
  const original = refundInput(50, "square");
  await service.prepareAttempt(original);

  await assert.rejects(
    () =>
      service.prepareAttempt({
        ...original,
        refundCapacityId: "capacity-other",
      }),
    PaymentActionBindingConflictError,
  );
  await assert.rejects(
    () =>
      service.prepareAttempt({
        ...original,
        provider: "linkly-cloud",
      }),
    PaymentActionBindingConflictError,
  );

  assert.equal(hookCalls, 1);
  assert.equal(ledger.attempts.size, 1);
  assertProviderCalls(square, {
    submit: 0,
    recover: 0,
    cancel: 0,
    refund: 0,
  });
  assertProviderCalls(linkly, {
    submit: 0,
    recover: 0,
    cancel: 0,
    refund: 0,
  });
});

test("并发 prepare/start 同订单 fail fast，不会双 attempt 或双 provider；重试 start 只执行一次", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("square");
  const seedDeferred = createDeferred<TrustedRefundReferenceSeed>();
  let hookCalls = 0;
  const service = createService({
    ledger,
    provider,
    trustedRefundReferenceSeed: async () => {
      hookCalls += 1;
      return seedDeferred.promise;
    },
  });
  const startInput = refundInput(60, "square");

  const preparing = service.prepareAttempt(startInput);
  await waitUntil(() => hookCalls === 1);

  await assert.rejects(
    () => service.startAttempt(startInput),
    PaymentAttemptStateError,
  );
  assert.equal(ledger.attempts.size, 0);
  assert.equal(provider.refundCalls, 0);

  seedDeferred.resolve({
    provider: "square",
    paymentId: "payment-concurrent",
  });
  const prepared = await preparing;

  assert.equal(prepared.attempt.state, "Created");
  assert.equal(ledger.attempts.size, 1);
  assert.equal(provider.refundCalls, 0);

  const started = await service.startAttempt(startInput);

  assert.equal(started.attempt.state, "Approved");
  assert.equal(started.attempt.attemptId, prepared.attempt.attemptId);
  assert.equal(hookCalls, 1);
  assert.equal(provider.refundCalls, 1);
  assert.equal(ledger.attempts.size, 1);
});

test("同一退款 action 更换 capacityId 命中耐久签名冲突，不能调用 hook 或 provider", async () => {
  const ledger = new MemoryLedger();
  const bindings = new MemoryActionBindings();
  const firstProvider = new FakeProvider("square");
  let firstHookCalls = 0;
  firstProvider.refundResult = async (value) =>
    result("Declined", { paymentId: value.references.paymentId });
  const first = createService({
    ledger,
    bindings,
    provider: firstProvider,
    trustedRefundReferenceSeed: async (seedInput) => {
      firstHookCalls += 1;
      return trustedRefundSeed(seedInput, "payment-original");
    },
  });
  const originalInput = refundInput(30, "square");

  await first.startAttempt(originalInput);

  const replayProvider = new FakeProvider("square");
  let replayHookCalls = 0;
  const replay = createService({
    ledger,
    bindings,
    provider: replayProvider,
    trustedRefundReferenceSeed: async (seedInput) => {
      replayHookCalls += 1;
      return trustedRefundSeed(seedInput, "payment-other");
    },
  });

  await assert.rejects(
    () =>
      replay.startAttempt({
        ...originalInput,
        refundCapacityId: "capacity-different",
      }),
    PaymentActionBindingConflictError,
  );
  assert.equal(firstHookCalls, 1);
  assert.equal(replayHookCalls, 0);
  assert.equal(firstProvider.refundCalls, 1);
  assert.equal(replayProvider.refundCalls, 0);
  assert.equal(ledger.attempts.size, 1);
});

test("trusted refund seed 的 provider 或 capacity 解析不匹配时 fail closed", async () => {
  const invalidSeeds = [
    {
      name: "wrong provider",
      hook: (seedInput: TrustedRefundReferenceSeedInput) => {
        const seed = trustedRefundSeed(seedInput, "payment-original");
        return {
          ...seed,
          provider: "linkly-cloud",
          rfn: "rfn-wrong-provider",
        } as unknown as TrustedRefundReferenceSeed;
      },
    },
    {
      name: "wrong capacity",
      hook: (seedInput: TrustedRefundReferenceSeedInput) => {
        assert.notEqual(
          seedInput.capacity.capacityId,
          "capacity-authorized",
        );
        throw new Error("capacity does not belong to this action");
      },
    },
  ] as const;

  for (const [index, current] of invalidSeeds.entries()) {
    const ledger = new MemoryLedger();
    const bindings = new MemoryActionBindings();
    const provider = new FakeProvider("square");
    const service = createService({
      ledger,
      bindings,
      provider,
      trustedRefundReferenceSeed: async (seedInput) =>
        current.hook(seedInput),
    });

    await assert.rejects(
      () => service.startAttempt(refundInput(index, "square")),
      (error: unknown) => {
        assert.ok(
          error instanceof PaymentAttemptReferenceSeedError,
          current.name,
        );
        assert.equal(
          error.code,
          current.name === "wrong capacity"
            ? "TRUSTED_REFUND_REFERENCE_SEED_FAILED"
            : "TRUSTED_REFUND_REFERENCE_SEED_INVALID",
        );
        return true;
      },
    );
    assert.equal(bindings.bindings.size, 1);
    assert.equal(ledger.attempts.size, 0);
    assert.equal(provider.submitCalls, 0);
    assert.equal(provider.refundCalls, 0);
  }
});

test("trusted refund seed 拒绝额外/冲突引用，且已有 Created 引用不会被 hook 覆盖", async () => {
  const conflictedLedger = new MemoryLedger();
  const conflictedProvider = new FakeProvider("square");
  const conflicted = createService({
    ledger: conflictedLedger,
    provider: conflictedProvider,
    trustedRefundReferenceSeed: async (seedInput) =>
      ({
        ...trustedRefundSeed(seedInput, "payment-original"),
        rfn: "forbidden-rfn",
      }) as unknown as TrustedRefundReferenceSeed,
  });

  await assert.rejects(
    () => conflicted.startAttempt(refundInput(10, "square")),
    PaymentAttemptReferenceSeedError,
  );
  assert.equal(conflictedLedger.attempts.size, 0);
  assert.equal(conflictedProvider.refundCalls, 0);

  const existingLedger = new MemoryLedger();
  const existingBindings = new MemoryActionBindings();
  const existingProvider = new FakeProvider("square");
  const existingInput = refundInput(11, "square");
  const binding: PaymentActionBinding = {
    orderGuid: existingInput.orderGuid,
    actionId: existingInput.actionId,
    requestSignature: JSON.stringify([
      "square",
      "refund",
      "AUD",
      -1_250,
      "capacity-seeded-11",
    ]),
    attemptId: "attempt-existing-refund",
    idempotencyKey: "key-existing-refund",
    createdAtIso: "2026-07-28T00:00:00.000Z",
    actor: auditActor(),
  };
  await existingBindings.bindOrGet(binding);
  existingLedger.seed(
    attempt({
      attemptId: binding.attemptId,
      idempotencyKey: binding.idempotencyKey,
      orderGuid: binding.orderGuid,
      provider: "square",
      operation: "refund",
      amount: { currency: "AUD", cents: -1_250 },
      state: "Created",
      references: references({ paymentId: "payment-existing" }),
      createdAtIso: binding.createdAtIso,
      updatedAtIso: binding.createdAtIso,
    }),
    true,
  );
  let hookCalls = 0;
  existingProvider.refundResult = async (value) => {
    assert.equal(value.references.paymentId, "payment-existing");
    return result("Approved", { paymentId: "payment-existing" });
  };
  const existingService = createService({
    ledger: existingLedger,
    bindings: existingBindings,
    provider: existingProvider,
    trustedRefundReferenceSeed: async (seedInput) => {
      hookCalls += 1;
      return trustedRefundSeed(seedInput, "payment-overwrite");
    },
  });

  const completed = await existingService.startAttempt(existingInput);

  assert.equal(hookCalls, 0);
  assert.equal(completed.attempt.references.paymentId, "payment-existing");
  assert.equal(existingProvider.refundCalls, 1);
});

test("trusted refund seed hook 抛错或已知离线时不调用 provider，也不留下新 attempt", async () => {
  const thrownLedger = new MemoryLedger();
  const thrownBindings = new MemoryActionBindings();
  const thrownProvider = new FakeProvider("square");
  const thrownService = createService({
    ledger: thrownLedger,
    bindings: thrownBindings,
    provider: thrownProvider,
    trustedRefundReferenceSeed: async () => {
      throw new Error("vault unavailable");
    },
  });

  await assert.rejects(
    () => thrownService.startAttempt(refundInput(20, "square")),
    (error: unknown) => {
      assert.ok(error instanceof PaymentAttemptReferenceSeedError);
      assert.equal(error.code, "TRUSTED_REFUND_REFERENCE_SEED_FAILED");
      assert.equal(error.message.includes("vault unavailable"), false);
      return true;
    },
  );
  assert.equal(thrownBindings.bindings.size, 1);
  assert.equal(thrownLedger.attempts.size, 0);
  assert.equal(thrownProvider.refundCalls, 0);

  const offlineLedger = new MemoryLedger();
  const offlineProvider = new FakeProvider("square");
  let offlineHookCalls = 0;
  const offlineService = createService({
    ledger: offlineLedger,
    provider: offlineProvider,
    online: false,
    trustedRefundReferenceSeed: async (seedInput) => {
      offlineHookCalls += 1;
      return trustedRefundSeed(seedInput, "payment-offline");
    },
  });

  await assert.rejects(
    () => offlineService.startAttempt(refundInput(21, "square")),
    PaymentAttemptOfflineError,
  );
  assert.equal(offlineHookCalls, 0);
  assert.equal(offlineLedger.attempts.size, 0);
  assert.equal(offlineProvider.refundCalls, 0);
});

test("退款拒绝零、正数、MIN_SAFE 与非整数金额，且绝不创建 attempt 或调用 provider", async () => {
  const invalidCents = [
    0,
    1_250,
    Number.MIN_SAFE_INTEGER,
    -12.5,
  ];

  for (const [index, cents] of invalidCents.entries()) {
    const ledger = new MemoryLedger();
    const provider = new FakeProvider("square");
    const service = createService({ ledger, provider });

    await assert.rejects(
      () =>
        service.startAttempt({
          ...input(),
          actionId: `action-invalid-refund-${index}`,
          orderGuid: `order-invalid-refund-${index}`,
          operation: "refund",
          amount: { currency: "AUD", cents },
        }),
      /refund amount/i,
    );
    assert.equal(ledger.attempts.size, 0);
    assert.equal(provider.submitCalls, 0);
    assert.equal(provider.refundCalls, 0);
  }
});

test("同一订单的并发重复点击共享一次 provider 调用和同一 attempt", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("square");
  const deferred = createDeferred<PaymentProviderResult>();
  provider.submitResult = () => deferred.promise;
  const service = createService({ ledger, provider });

  const first = service.startAttempt(input());
  const second = service.startAttempt(input());

  await waitUntil(() => provider.submitCalls === 1);
  deferred.resolve(result("Pending", { checkoutId: "checkout-1" }));

  const [left, right] = await Promise.all([first, second]);
  assert.equal(provider.submitCalls, 1);
  assert.equal(left.attempt.attemptId, right.attempt.attemptId);
  assert.equal(left.attempt.idempotencyKey, right.attempt.idempotencyKey);
  assert.equal(left.attempt.references.checkoutId, "checkout-1");
});

test("Pending 受控恢复在卸载 abort 后保留原状态和身份，不追加 Unknown CAS", async () => {
  const ledger = new MemoryLedger();
  const provider = new AbortableFakeProvider("square");
  const pending = attempt({
    attemptId: "attempt-abortable-recovery",
    idempotencyKey: "key-abortable-recovery",
    state: "Pending",
    references: references({ checkoutId: "checkout-abortable-recovery" }),
  });
  ledger.seed(pending, true);
  const service = createService({ ledger, provider });
  const controller = new AbortController();
  const deadlineAtMs = Date.parse(pending.createdAtIso) + 90_000;
  ledger.failUpdateWhenState = "Unknown";
  provider.recoverWithControlResult = async () => {
    controller.abort();
    return {
      ...result("Unknown", { checkoutId: "checkout-should-not-merge" }),
      responseCode: "REQUEST_ABORTED",
    };
  };

  const recovered = await service.recoverAttempt(pending.attemptId, {
    signal: controller.signal,
    deadlineAtMs,
  });

  assert.equal(provider.recoverCalls, 0);
  assert.equal(provider.recoverWithControlCalls, 1);
  assert.strictEqual(provider.recoveryControls[0]?.signal, controller.signal);
  assert.equal(provider.recoveryControls[0]?.deadlineAtMs, deadlineAtMs);
  assert.equal(recovered.attempt.state, "Pending");
  assert.equal(recovered.attempt.attemptId, pending.attemptId);
  assert.equal(recovered.attempt.idempotencyKey, pending.idempotencyKey);
  assert.equal(recovered.attempt.references.checkoutId, pending.references.checkoutId);
  assert.equal((await ledger.get(pending.attemptId))?.state, "Pending");
  assert.equal(ledger.attempts.size, 1);
});

test("Created 受控恢复在 Submitted CAS 后 abort 时保留 Submitted，不追加 Unknown CAS", async () => {
  const ledger = new MemoryLedger();
  const provider = new AbortableFakeProvider("square");
  const created = attempt({
    attemptId: "attempt-created-abort-neutral",
    idempotencyKey: "key-created-abort-neutral",
    state: "Created",
  });
  ledger.seed(created, true);
  const controller = new AbortController();
  ledger.failUpdateWhenState = "Unknown";
  provider.recoverWithControlResult = async () => {
    controller.abort();
    return {
      ...result("Unknown", { checkoutId: "checkout-should-not-merge" }),
      responseCode: "SQUARE_RECOVERY_ABORTED",
    };
  };

  const recovered = await createService({ ledger, provider }).recoverAttempt(
    created.attemptId,
    {
      signal: controller.signal,
      deadlineAtMs: Date.parse(created.createdAtIso) + 90_000,
    },
  );

  assert.equal(recovered.attempt.state, "Submitted");
  assert.equal(recovered.attempt.attemptId, created.attemptId);
  assert.equal(recovered.attempt.idempotencyKey, created.idempotencyKey);
  assert.equal(recovered.attempt.references.checkoutId, null);
  assert.equal((await ledger.get(created.attemptId))?.state, "Submitted");
  assert.equal(provider.submitCalls, 0);
  assert.equal(provider.recoverWithControlCalls, 1);
});

test("受控恢复 deadline exceeded 即使 signal 尚未 abort 也保留 Pending", async () => {
  const ledger = new MemoryLedger();
  const provider = new AbortableFakeProvider("square");
  const pending = attempt({
    attemptId: "attempt-deadline-neutral",
    state: "Pending",
    references: references({ checkoutId: "checkout-deadline-neutral" }),
  });
  ledger.seed(pending, true);
  const controller = new AbortController();
  ledger.failUpdateWhenState = "Unknown";
  provider.recoverWithControlResult = async () => ({
    ...result("Unknown", { checkoutId: "checkout-should-not-merge" }),
    responseCode: "SQUARE_RECOVERY_DEADLINE_EXCEEDED",
  });

  const recovered = await createService({ ledger, provider }).recoverAttempt(
    pending.attemptId,
    {
      signal: controller.signal,
      deadlineAtMs: Date.parse(pending.createdAtIso) + 90_000,
    },
  );

  assert.equal(controller.signal.aborted, false);
  assert.equal(recovered.attempt.state, "Pending");
  assert.equal(recovered.attempt.attemptId, pending.attemptId);
  assert.equal(recovered.attempt.idempotencyKey, pending.idempotencyKey);
  assert.equal(recovered.attempt.references.checkoutId, pending.references.checkoutId);
});

test("Linkly 受控恢复 abort/deadline 只停止查询，不把 Pending 写成 Unknown", async () => {
  for (const [responseCode, abort] of [
    ["LINKLY_RECOVERY_ABORTED", true],
    ["LINKLY_RECOVERY_DEADLINE_EXCEEDED", false],
  ] as const) {
    const ledger = new MemoryLedger();
    const provider = new AbortableFakeProvider("linkly-cloud");
    const pending = attempt({
      attemptId: `attempt-linkly-${responseCode.toLowerCase()}`,
      provider: "linkly-cloud",
      state: "Pending",
    });
    ledger.seed(pending, true);
    ledger.failUpdateWhenState = "Unknown";
    const controller = new AbortController();
    provider.recoverWithControlResult = async () => {
      if (abort) controller.abort();
      return {
        ...result("Unknown"),
        responseCode,
      };
    };

    const recovered = await createService({ ledger, provider }).recoverAttempt(
      pending.attemptId,
      {
        signal: controller.signal,
        deadlineAtMs: Date.parse(pending.createdAtIso) + 180_000,
      },
    );

    assert.equal(recovered.attempt.state, "Pending");
    assert.equal((await ledger.get(pending.attemptId))?.state, "Pending");
  }
});

test("受控恢复的其他 Unknown（包括未 abort 或短码缺失）仍按原逻辑持久化", async () => {
  const ledger = new MemoryLedger();
  const provider = new AbortableFakeProvider("square");
  const pending = attempt({
    attemptId: "attempt-uncontrolled-unknown",
    state: "Pending",
  });
  ledger.seed(pending, true);
  const controller = new AbortController();
  provider.recoverWithControlResult = async () => ({
    ...result("Unknown"),
    responseCode: "REQUEST_ABORTED",
  });

  const recovered = await createService({ ledger, provider }).recoverAttempt(
    pending.attemptId,
    {
      signal: controller.signal,
      deadlineAtMs: Date.parse(pending.createdAtIso) + 90_000,
    },
  );

  assert.equal(controller.signal.aborted, false);
  assert.equal(recovered.attempt.state, "Unknown");
  assert.equal(recovered.attempt.lastErrorCode, "REQUEST_ABORTED");
  assert.equal((await ledger.get(pending.attemptId))?.state, "Unknown");

  const missingCodeLedger = new MemoryLedger();
  const missingCodeProvider = new AbortableFakeProvider("square");
  const missingCodeAttempt = attempt({
    attemptId: "attempt-unknown-missing-code",
    state: "Pending",
  });
  missingCodeLedger.seed(missingCodeAttempt, true);
  missingCodeProvider.recoverWithControlResult = async () => result("Unknown");

  const missingCodeRecovered = await createService({
    ledger: missingCodeLedger,
    provider: missingCodeProvider,
  }).recoverAttempt(missingCodeAttempt.attemptId, {
    signal: new AbortController().signal,
    deadlineAtMs: Date.parse(missingCodeAttempt.createdAtIso) + 90_000,
  });

  assert.equal(missingCodeRecovered.attempt.state, "Unknown");
  assert.equal(missingCodeRecovered.attempt.lastErrorCode, null);
  assert.equal(
    (await missingCodeLedger.get(missingCodeAttempt.attemptId))?.state,
    "Unknown",
  );
});

test("受控恢复在 provider 已返回真实状态后即使 signal 迟到 abort 也持久化真实结果", async () => {
  const ledger = new MemoryLedger();
  const provider = new AbortableFakeProvider("square");
  const pending = attempt({
    attemptId: "attempt-late-abort-real-result",
    state: "Pending",
  });
  ledger.seed(pending, true);
  const controller = new AbortController();
  provider.recoverWithControlResult = async () => {
    controller.abort();
    return {
      ...result("Declined"),
      responseCode: "CARD_DECLINED",
    };
  };

  const recovered = await createService({ ledger, provider }).recoverAttempt(
    pending.attemptId,
    {
      signal: controller.signal,
      deadlineAtMs: Date.parse(pending.createdAtIso) + 90_000,
    },
  );

  assert.equal(controller.signal.aborted, true);
  assert.equal(recovered.attempt.state, "Declined");
  assert.equal(recovered.attempt.lastErrorCode, "CARD_DECLINED");
  assert.equal((await ledger.get(pending.attemptId))?.state, "Declined");
});

test("Created Square purchase 带 deadline 恢复时先 CAS Submitted，再调用私有恢复 capability", async () => {
  const ledger = new MemoryLedger();
  const provider = new AbortableFakeProvider("square");
  const created = attempt({
    attemptId: "attempt-created-abortable-purchase",
    idempotencyKey: "key-created-abortable-purchase",
    state: "Created",
  });
  ledger.seed(created, true);
  const controller = new AbortController();
  const deadlineAtMs = Date.parse(created.createdAtIso) + 90_000;
  provider.recoverWithControlResult = async (submitted, control) => {
    const persisted = await ledger.get(submitted.attemptId);
    assert.equal(submitted.state, "Submitted");
    assert.equal(persisted?.state, "Submitted");
    assert.equal(control.signal, controller.signal);
    assert.equal(control.deadlineAtMs, deadlineAtMs);
    return result("Pending", { checkoutId: "checkout-created-abortable" });
  };
  const service = createService({ ledger, provider });

  const recovered = await service.recoverAttempt(created.attemptId, {
    signal: controller.signal,
    deadlineAtMs,
  });

  assert.equal(provider.recoverWithControlCalls, 1);
  assert.equal(provider.submitCalls, 0);
  assert.equal(provider.refundCalls, 0);
  assert.equal(provider.recoverCalls, 0);
  assert.equal(recovered.attempt.state, "Pending");
  assert.equal(recovered.attempt.attemptId, created.attemptId);
  assert.equal(recovered.attempt.idempotencyKey, created.idempotencyKey);
  assert.equal(ledger.attempts.size, 1);
});

test("Created Square refund 带 deadline 恢复时同样走私有恢复 capability，不绕过取消能力", async () => {
  const ledger = new MemoryLedger();
  const provider = new AbortableFakeProvider("square");
  const created = attempt({
    attemptId: "attempt-created-abortable-refund",
    idempotencyKey: "key-created-abortable-refund",
    operation: "refund",
    amount: { currency: "AUD", cents: -500 },
    state: "Created",
    references: references({ paymentId: "payment-original" }),
  });
  ledger.seed(created, true);
  const controller = new AbortController();
  const deadlineAtMs = Date.parse(created.createdAtIso) + 90_000;
  provider.recoverWithControlResult = async (submitted, control) => {
    const persisted = await ledger.get(submitted.attemptId);
    assert.equal(submitted.state, "Submitted");
    assert.equal(persisted?.state, "Submitted");
    assert.equal(submitted.operation, "refund");
    assert.equal(control.signal, controller.signal);
    assert.equal(control.deadlineAtMs, deadlineAtMs);
    return result("Pending", { paymentId: "payment-original" });
  };
  const service = createService({ ledger, provider });

  const recovered = await service.recoverAttempt(created.attemptId, {
    signal: controller.signal,
    deadlineAtMs,
  });

  assert.equal(provider.recoverWithControlCalls, 1);
  assert.equal(provider.submitCalls, 0);
  assert.equal(provider.refundCalls, 0);
  assert.equal(provider.recoverCalls, 0);
  assert.equal(recovered.attempt.state, "Pending");
  assert.equal(recovered.attempt.attemptId, created.attemptId);
  assert.equal(recovered.attempt.idempotencyKey, created.idempotencyKey);
  assert.equal(ledger.attempts.size, 1);
});

test("Created 恢复在无 signal 或 provider 不支持取消时保持原 submit/refund 行为", async () => {
  const noSignalLedger = new MemoryLedger();
  const abortableProvider = new AbortableFakeProvider("square");
  const purchase = attempt({
    attemptId: "attempt-created-no-signal",
    state: "Created",
  });
  noSignalLedger.seed(purchase, true);

  await createService({
    ledger: noSignalLedger,
    provider: abortableProvider,
  }).recoverAttempt(purchase.attemptId);

  assert.equal(abortableProvider.submitCalls, 1);
  assert.equal(abortableProvider.recoverWithControlCalls, 0);
  assert.equal(abortableProvider.recoverCalls, 0);

  const nonAbortableLedger = new MemoryLedger();
  const nonAbortableProvider = new FakeProvider("square");
  const refund = attempt({
    attemptId: "attempt-created-non-abortable-refund",
    operation: "refund",
    amount: { currency: "AUD", cents: -500 },
    state: "Created",
    references: references({ paymentId: "payment-original" }),
  });
  nonAbortableLedger.seed(refund, true);

  await createService({
    ledger: nonAbortableLedger,
    provider: nonAbortableProvider,
  }).recoverAttempt(refund.attemptId, {
    signal: new AbortController().signal,
    deadlineAtMs: Date.parse(refund.createdAtIso) + 90_000,
  });

  assert.equal(nonAbortableProvider.refundCalls, 1);
  assert.equal(nonAbortableProvider.submitCalls, 0);
  assert.equal(nonAbortableProvider.recoverCalls, 0);
});

test("跨 service 的不同 signal 或 manual 不 join，只有同一 signal 可复用", async () => {
  const ledger = new MemoryLedger();
  const provider = new AbortableFakeProvider("square");
  const pending = attempt({
    attemptId: "attempt-owned-recovery-flight",
    idempotencyKey: "key-owned-recovery-flight",
    state: "Pending",
    references: references({ checkoutId: "checkout-owned-recovery-flight" }),
  });
  ledger.seed(pending, true);
  const gate = createDeferred<PaymentProviderResult>();
  provider.recoverWithControlResult = () => gate.promise;
  const firstService = createService({ ledger, provider });
  const secondService = createService({ ledger, provider });
  const firstController = new AbortController();
  const secondController = new AbortController();
  const deadlineAtMs = Date.parse(pending.createdAtIso) + 90_000;

  const first = firstService.recoverAttempt(pending.attemptId, {
    signal: firstController.signal,
    deadlineAtMs,
  });
  await waitUntil(() => provider.recoverWithControlCalls === 1);
  const sameOwner = secondService.recoverAttempt(pending.attemptId, {
    signal: firstController.signal,
    deadlineAtMs,
  });
  const differentOwner = secondService.recoverAttempt(pending.attemptId, {
    signal: secondController.signal,
    deadlineAtMs,
  });
  const manual = secondService.recoverAttempt(pending.attemptId);

  gate.resolve(result("Unknown", { checkoutId: "checkout-owned-recovery-flight" }));
  const settled = await Promise.allSettled([
    first,
    sameOwner,
    differentOwner,
    manual,
  ]);
  assert.notStrictEqual(differentOwner, first);
  assert.notStrictEqual(manual, first);
  assert.equal(settled[0]?.status, "fulfilled");
  assert.equal(settled[1]?.status, "fulfilled");
  assert.equal(settled[2]?.status, "rejected");
  assert.equal(settled[3]?.status, "rejected");
  assert.equal(provider.recoverWithControlCalls, 1);
});

test("两个 service 实例的并发 start 共享模块级订单 single-flight", async () => {
  const ledger = new MemoryLedger();
  const firstProvider = new FakeProvider("square");
  const secondProvider = new FakeProvider("square");
  const deferred = createDeferred<PaymentProviderResult>();
  firstProvider.submitResult = () => deferred.promise;
  const firstService = createService({ ledger, provider: firstProvider });
  const secondService = createService({ ledger, provider: secondProvider });

  const first = firstService.startAttempt(input());
  const second = secondService.startAttempt(input());

  await waitUntil(() => firstProvider.submitCalls === 1);
  deferred.resolve(result("Pending", { checkoutId: "checkout-cross-instance" }));

  const [left, right] = await Promise.all([first, second]);
  assert.equal(firstProvider.submitCalls, 1);
  assert.equal(secondProvider.submitCalls, 0);
  assert.equal(left.attempt.attemptId, right.attempt.attemptId);
  assert.equal(ledger.attempts.size, 1);
});

test("相同订单并发切换 provider 会被拒绝，不能生成第二笔扣款", async () => {
  const ledger = new MemoryLedger();
  const square = new FakeProvider("square");
  const linkly = new FakeProvider("linkly-cloud");
  const deferred = createDeferred<PaymentProviderResult>();
  square.submitResult = () => deferred.promise;
  const service = createService({ ledger, providers: [square, linkly] });

  const active = service.startAttempt(input());
  await waitUntil(() => square.submitCalls === 1);

  await assert.rejects(
    () => service.startAttempt({ ...input(), provider: "linkly-cloud" }),
    PaymentAttemptStateError,
  );
  assert.equal(linkly.submitCalls, 0);

  deferred.resolve(result("Declined"));
  await active;
});

test("跨实例 start 与 cancel 共用订单互斥，不会同时触发第二个 provider 动作", async () => {
  const ledger = new MemoryLedger();
  const cancelProvider = new FakeProvider("square");
  const startProvider = new FakeProvider("square");
  const deferred = createDeferred<PaymentProviderResult>();
  cancelProvider.cancelResult = () => deferred.promise;
  const pending = attempt({ attemptId: "attempt-pending", state: "Pending" });
  ledger.seed(pending, true);
  const cancelService = createService({ ledger, provider: cancelProvider });
  const startService = createService({ ledger, provider: startProvider });

  const cancelling = cancelService.cancelAttempt(pending.attemptId);
  await waitUntil(() => cancelProvider.cancelCalls === 1);

  await assert.rejects(() => startService.startAttempt(input()), PaymentAttemptStateError);
  assert.equal(startProvider.submitCalls, 0);
  assert.equal(cancelProvider.cancelCalls, 1);

  deferred.resolve(result("Cancelled"));
  const cancelled = await cancelling;
  assert.equal(cancelled.attempt.state, "Cancelled");
});

test("provider 异常或响应丢失持久化为 Unknown，并阻止新扣款、退款与换 provider", async () => {
  const ledger = new MemoryLedger();
  const square = new FakeProvider("square");
  const linkly = new FakeProvider("linkly-cloud");
  square.submitResult = async () => {
    const error = new Error("response lost") as Error & { code: string };
    error.code = "NETWORK_RESPONSE_LOST";
    throw error;
  };
  const service = createService({ ledger, providers: [square, linkly] });

  const unknown = await service.startAttempt(input());
  assert.equal(unknown.attempt.state, "Unknown");
  assert.equal(unknown.attempt.lastErrorCode, "NETWORK_RESPONSE_LOST");

  await assert.rejects(
    () => service.startAttempt({ ...input(), actionId: "action-new-after-unknown" }),
    PaymentAttemptBlockedError,
  );
  await assert.rejects(
    () =>
      service.startAttempt({
        ...input(),
        actionId: "action-switch-after-unknown",
        provider: "linkly-cloud",
      }),
    PaymentAttemptBlockedError,
  );
  await assert.rejects(
    () =>
      service.startAttempt({
        ...input(),
        actionId: "action-refund-after-unknown",
        operation: "refund",
        amount: { currency: "AUD", cents: -amount.cents },
      }),
    PaymentAttemptBlockedError,
  );
  await assert.rejects(
    () => service.cancelAttempt(unknown.attempt.attemptId),
    PaymentAttemptStateError,
  );
  assert.equal(square.submitCalls, 1);
  assert.equal(square.cancelCalls, 0);
  assert.equal(square.refundCalls, 0);
  assert.equal(linkly.submitCalls, 0);
});

test("Unknown 恢复复用原 attempt、OrderGuid 和幂等键，并合并所有 provider references", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("linkly-cloud");
  const service = createService({ ledger, provider });
  const original = attempt({
    provider: "linkly-cloud",
    providerEnvironment: "Sandbox",
    state: "Unknown",
    references: references({
      sessionId: "session-1",
      txnRef: "txn-1",
    }),
  });
  ledger.seed(original, true);
  provider.recoverResult = async (value) => {
    assert.equal(value.attemptId, original.attemptId);
    assert.equal(value.orderGuid, original.orderGuid);
    assert.equal(value.idempotencyKey, original.idempotencyKey);
    return result("Approved", {
      sessionId: null,
      txnRef: null,
      rfn: "rfn-1",
    });
  };

  const recovered = await service.recoverAttempt(original.attemptId);

  assert.equal(provider.recoverCalls, 1);
  assert.equal(recovered.attempt.state, "Approved");
  assert.deepEqual(
    recovered.attempt.references,
    references({
      sessionId: "session-1",
      txnRef: "txn-1",
      rfn: "rfn-1",
    }),
  );
  assert.equal(recovered.attempt.orderGuid, original.orderGuid);
  assert.equal(recovered.attempt.idempotencyKey, original.idempotencyKey);
});

test("批准响应落库失败模拟批准后崩溃：原 Submitted attempt 阻塞重扣并可恢复同一订单", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("square");
  const service = createService({ ledger, provider });
  provider.submitResult = async () =>
    result("Approved", {
      checkoutId: "checkout-1",
      paymentId: "payment-1",
    });
  ledger.failUpdateWhenState = "Approved";

  await assert.rejects(() => service.startAttempt(input()), /update failed/i);
  assert.equal(provider.submitCalls, 1);

  const stored = [...ledger.attempts.values()][0];
  assert.equal(stored?.state, "Submitted");
  assert.ok(stored);

  await assert.rejects(
    () => service.startAttempt({ ...input(), actionId: "new-action-after-crash" }),
    PaymentAttemptBlockedError,
  );
  assert.equal(provider.submitCalls, 1);

  ledger.failUpdateWhenState = null;
  provider.recoverResult = async (value) => {
    assert.equal(value.attemptId, stored.attemptId);
    assert.equal(value.orderGuid, "order-1");
    return result("Approved", {
      checkoutId: "checkout-1",
      paymentId: "payment-1",
    });
  };
  const recovered = await service.recoverAttempt(stored.attemptId);
  assert.equal(recovered.attempt.state, "Approved");
  assert.equal(recovered.attempt.orderGuid, "order-1");
  assert.equal(provider.recoverCalls, 1);
});

test("CAS false 必须抛 durability/recovery-required，绝不宣称 Approved 或 Cancelled", async () => {
  const approvedLedger = new MemoryLedger();
  const approvedProvider = new FakeProvider("square");
  approvedProvider.submitResult = async () => result("Approved", { paymentId: "payment-1" });
  approvedLedger.returnFalseWhenNextState = "Approved";
  const approvalService = createService({ ledger: approvedLedger, provider: approvedProvider });

  await assert.rejects(
    () => approvalService.startAttempt(input()),
    (error: unknown) => {
      assert.ok(error instanceof PaymentAttemptDurabilityError);
      assert.equal(error.recoveryRequired, true);
      assert.equal(error.attemptId, "attempt-1");
      return true;
    },
  );
  assert.equal([...approvedLedger.attempts.values()][0]?.state, "Submitted");

  const cancelledLedger = new MemoryLedger();
  const created = attempt({ attemptId: "created-cas", state: "Created" });
  cancelledLedger.seed(created, true);
  cancelledLedger.returnFalseWhenNextState = "Cancelled";
  const cancelService = createService({
    ledger: cancelledLedger,
    provider: new FakeProvider("square"),
  });

  await assert.rejects(
    () => cancelService.cancelAttempt(created.attemptId),
    PaymentAttemptDurabilityError,
  );
  assert.equal(cancelledLedger.attempts.get(created.attemptId)?.state, "Created");
});

test("Approved 未完成在新 service 实例重启后同 action 幂等返回，且阻塞新 action", async () => {
  const ledger = new MemoryLedger();
  const bindings = new MemoryActionBindings();
  const firstProvider = new FakeProvider("square");
  firstProvider.submitResult = async () =>
    result("Approved", { checkoutId: "checkout-1", paymentId: "payment-1" });
  const firstService = createService({ ledger, bindings, provider: firstProvider });
  const approved = (await firstService.startAttempt(input())).attempt;

  const restartedProvider = new FakeProvider("square");
  const restartedService = createService({
    ledger,
    bindings,
    provider: restartedProvider,
  });

  const replayed = await restartedService.startAttempt(input());
  assert.equal(replayed.attempt.attemptId, approved.attemptId);
  assert.equal(replayed.attempt.idempotencyKey, approved.idempotencyKey);
  assert.equal(restartedProvider.submitCalls, 0);
  assert.equal(restartedProvider.recoverCalls, 0);
  await assert.rejects(
    async () =>
      restartedService.startAttempt({
        ...input(),
        actionId: "action-new-after-approved",
      }),
    (error: unknown) => {
      assert.ok(error instanceof PaymentAttemptBlockedError);
      assert.equal(error.blockingAttempt.attemptId, approved.attemptId);
      assert.equal(error.blockingAttempt.orderGuid, approved.orderGuid);
      return true;
    },
  );
  assert.equal(firstProvider.submitCalls, 1);
});

test("Approved 回单和规范化响应码先持久化，重建 service 后恢复仍返回同一回单", async () => {
  const ledger = new MemoryLedger();
  const firstProvider = new FakeProvider("linkly-cloud");
  firstProvider.submitResult = async () => ({
    state: "Approved",
    references: references({
      sessionId: "session-receipt",
      txnRef: "txn-receipt",
      rfn: "rfn-receipt",
    }),
    receiptText: "MERCHANT RECEIPT\nAPPROVED",
    responseCode: " approved ",
  });
  const firstService = createService({ ledger, provider: firstProvider });

  const approved = await firstService.startAttempt({
    ...input(),
    provider: "linkly-cloud",
  });

  assert.equal(approved.attempt.receiptText, "MERCHANT RECEIPT\nAPPROVED");
  assert.equal(approved.attempt.responseCode, "APPROVED");
  assert.equal(approved.receiptText, "MERCHANT RECEIPT\nAPPROVED");
  assert.equal(approved.responseCode, "APPROVED");
  assert.equal(
    ledger.attempts.get(approved.attempt.attemptId)?.receiptText,
    "MERCHANT RECEIPT\nAPPROVED",
  );

  const restartedProvider = new FakeProvider("linkly-cloud");
  const restartedService = createService({ ledger, provider: restartedProvider });
  const recovered = await restartedService.recoverAttempt(approved.attempt.attemptId);

  assert.equal(recovered.attempt.orderGuid, approved.attempt.orderGuid);
  assert.equal(recovered.receiptText, "MERCHANT RECEIPT\nAPPROVED");
  assert.equal(recovered.responseCode, "APPROVED");
  assert.equal(restartedProvider.recoverCalls, 0);
});

test("取消边界：Created 本地取消；Submitted/Pending 显式调用 provider；Unknown 与终态禁止取消", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("square");
  const service = createService({ ledger, provider });

  const created = attempt({ attemptId: "created", state: "Created" });
  ledger.seed(created, true);
  const local = await service.cancelAttempt(created.attemptId);
  assert.equal(local.attempt.state, "Cancelled");
  assert.equal(provider.cancelCalls, 0);

  const submitted = attempt({ attemptId: "submitted", state: "Submitted" });
  ledger.seed(submitted, true);
  provider.cancelResult = async () => result("Cancelled");
  const remote = await service.cancelAttempt(submitted.attemptId);
  assert.equal(remote.attempt.state, "Cancelled");
  assert.equal(provider.cancelCalls, 1);

  for (const state of ["Unknown", "Approved", "Declined", "Cancelled"] as const) {
    const value = attempt({ attemptId: state, state });
    ledger.seed(value, state === "Unknown" || state === "Approved");
    await assert.rejects(() => service.cancelAttempt(value.attemptId), PaymentAttemptStateError);
  }
  assert.equal(provider.cancelCalls, 1);
});

test("reference 合并不会用 null 擦除已有值，且冲突引用进入 Unknown 而不换绑交易", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("square");
  const service = createService({ ledger, provider });
  const pending = attempt({
    state: "Pending",
    references: references({ checkoutId: "checkout-1" }),
  });
  ledger.seed(pending, true);
  provider.recoverResult = async () =>
    result("Pending", {
      checkoutId: null,
      paymentId: "payment-1",
    });

  const merged = await service.recoverAttempt(pending.attemptId);
  assert.equal(merged.attempt.references.checkoutId, "checkout-1");
  assert.equal(merged.attempt.references.paymentId, "payment-1");

  provider.recoverResult = async () =>
    result("Approved", {
      checkoutId: "checkout-other",
      paymentId: "payment-1",
    });
  const conflicted = await service.recoverAttempt(pending.attemptId);
  assert.equal(conflicted.attempt.state, "Unknown");
  assert.equal(conflicted.attempt.references.checkoutId, "checkout-1");
  assert.equal(conflicted.attempt.lastErrorCode, "PROVIDER_REFERENCE_CONFLICT");
});

test("已知离线时不创建 attempt 且不调用 provider", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("square");
  const service = createService({ ledger, provider, online: false });

  await assert.rejects(() => service.startAttempt(input()), PaymentAttemptOfflineError);
  assert.equal(ledger.attempts.size, 0);
  assert.equal(provider.submitCalls, 0);
});

test("同一 action 跨新 service 重放复用 IDs，Submitted 只能 recover 不能再次 submit", async () => {
  const ledger = new MemoryLedger();
  const bindings = new MemoryActionBindings();
  const firstProvider = new FakeProvider("square");
  firstProvider.submitResult = async () =>
    result("Pending", { checkoutId: "checkout-replay" });
  const firstService = createService({ ledger, bindings, provider: firstProvider });
  const pending = await firstService.startAttempt(input());

  const restartedProvider = new FakeProvider("square");
  restartedProvider.recoverResult = async (attemptValue) => {
    assert.equal(attemptValue.attemptId, pending.attempt.attemptId);
    assert.equal(attemptValue.idempotencyKey, pending.attempt.idempotencyKey);
    return result("Approved", {
      checkoutId: "checkout-replay",
      paymentId: "payment-replay",
    });
  };
  const restartedService = createService({
    ledger,
    bindings,
    provider: restartedProvider,
  });

  const replayed = await restartedService.startAttempt(input());

  assert.equal(replayed.attempt.attemptId, pending.attempt.attemptId);
  assert.equal(replayed.attempt.idempotencyKey, pending.attempt.idempotencyKey);
  assert.equal(restartedProvider.submitCalls, 0);
  assert.equal(restartedProvider.recoverCalls, 1);
});

test("同一 action 改变 provider、operation 或金额签名必须拒绝且不创建新 attempt", async () => {
  for (const changed of [
    { ...input(), provider: "linkly-cloud" as const },
    {
      ...input(),
      operation: "refund" as const,
      amount: { currency: "AUD" as const, cents: -amount.cents },
    },
    { ...input(), amount: { currency: "AUD" as const, cents: amount.cents + 1 } },
  ]) {
    const ledger = new MemoryLedger();
    const bindings = new MemoryActionBindings();
    const square = new FakeProvider("square");
    square.submitResult = async () => result("Declined");
    const linkly = new FakeProvider("linkly-cloud");
    const first = createService({
      ledger,
      bindings,
      providers: [square, linkly],
    });
    await first.startAttempt(input());

    const restarted = createService({
      ledger,
      bindings,
      providers: [square, linkly],
    });
    await assert.rejects(
      () => restarted.startAttempt(changed),
      PaymentActionBindingConflictError,
    );
    assert.equal(ledger.attempts.size, 1);
    assert.equal(square.submitCalls, 1);
    assert.equal(square.refundCalls, 0);
    assert.equal(linkly.submitCalls, 0);
  }
});

test("绑定已提交但 attempt 尚不存在时只能用绑定的 attemptId 和幂等键创建", async () => {
  const ledger = new MemoryLedger();
  const bindings = new MemoryActionBindings();
  const persisted: PaymentActionBinding = {
    orderGuid: "order-1",
    actionId: "action-1",
    requestSignature: JSON.stringify(["square", "purchase", "AUD", amount.cents]),
    attemptId: "attempt-from-binding",
    idempotencyKey: "key-from-binding",
    createdAtIso: "2026-07-28T00:00:00.000Z",
    actor: auditActor(),
  };
  await bindings.bindOrGet(persisted);
  const provider = new FakeProvider("square");
  const service = createService({ ledger, bindings, provider });

  const execution = await service.startAttempt(input());

  assert.equal(execution.attempt.attemptId, "attempt-from-binding");
  assert.equal(execution.attempt.idempotencyKey, "key-from-binding");
  assert.equal(provider.submitCalls, 1);
  assert.equal(ledger.attempts.size, 1);
});

test("Created 崩溃后同 action 重启从 Created→Submitted，只调用一次 submit", async () => {
  const ledger = new MemoryLedger();
  const bindings = new MemoryActionBindings();
  const crashingProvider = new FakeProvider("square");
  ledger.failNextUpdate = true;
  const first = createService({
    ledger,
    bindings,
    provider: crashingProvider,
  });

  await assert.rejects(() => first.startAttempt(input()), PaymentAttemptDurabilityError);
  const created = [...ledger.attempts.values()][0];
  assert.equal(created?.state, "Created");
  assert.equal(crashingProvider.submitCalls, 0);

  const restartedProvider = new FakeProvider("square");
  const restarted = createService({
    ledger,
    bindings,
    provider: restartedProvider,
  });
  const recovered = await restarted.startAttempt(input());

  assert.equal(recovered.attempt.attemptId, created?.attemptId);
  assert.equal(recovered.attempt.state, "Approved");
  assert.equal(restartedProvider.submitCalls, 1);
  assert.equal(restartedProvider.recoverCalls, 0);
});

test("显式 recover 可安全推进 Created，终态 recover 直接返回且不访问 provider", async () => {
  const ledger = new MemoryLedger();
  const provider = new FakeProvider("square");
  const service = createService({ ledger, provider });
  const created = attempt({ attemptId: "attempt-created-recover", state: "Created" });
  ledger.seed(created, true);

  const recovered = await service.recoverAttempt(created.attemptId);
  assert.equal(recovered.attempt.state, "Approved");
  assert.equal(provider.submitCalls, 1);
  assert.equal(provider.recoverCalls, 0);

  for (const state of ["Declined", "Cancelled"] as const) {
    const terminal = attempt({
      attemptId: `attempt-${state}-recover`,
      state,
    });
    ledger.seed(terminal, false);
    const direct = await service.recoverAttempt(terminal.attemptId);
    assert.equal(direct.attempt.state, state);
  }
  assert.equal(provider.submitCalls, 1);
  assert.equal(provider.recoverCalls, 0);
});

test("action 绑定写入结果不明抛 durability 且保留提议 attemptId", async () => {
  const ledger = new MemoryLedger();
  const bindings = new MemoryActionBindings();
  bindings.failNextBind = true;
  const provider = new FakeProvider("square");
  const service = createService({ ledger, bindings, provider });

  await assert.rejects(
    () => service.startAttempt(input()),
    (error: unknown) => {
      assert.ok(error instanceof PaymentAttemptDurabilityError);
      assert.equal(error.attemptId, "attempt-1");
      assert.equal(error.orderGuid, "order-1");
      return true;
    },
  );
  assert.equal(provider.submitCalls, 0);
  assert.equal(ledger.attempts.size, 0);
});

test("prepare/start 遇到绑定 attempt 的不可变身份变化都进入 durability recovery，绝不触碰 provider", async () => {
  const ledger = new MemoryLedger();
  const bindings = new MemoryActionBindings();
  const binding: PaymentActionBinding = {
    orderGuid: "order-1",
    actionId: "action-1",
    requestSignature: JSON.stringify(["square", "purchase", "AUD", amount.cents]),
    attemptId: "attempt-bound",
    idempotencyKey: "key-bound",
    createdAtIso: "2026-07-28T00:00:00.000Z",
    actor: auditActor(),
  };
  await bindings.bindOrGet(binding);
  ledger.seed(
    attempt({
      attemptId: binding.attemptId,
      idempotencyKey: "key-tampered",
      state: "Created",
    }),
    true,
  );
  const provider = new FakeProvider("square");
  const service = createService({ ledger, bindings, provider });

  await assert.rejects(
    () => service.prepareAttempt(input()),
    (error: unknown) => {
      assert.ok(error instanceof PaymentAttemptDurabilityError);
      assert.equal(error.attemptId, binding.attemptId);
      return true;
    },
  );
  await assert.rejects(
    () => service.startAttempt(input()),
    PaymentAttemptDurabilityError,
  );
  assert.equal(provider.submitCalls, 0);
  assert.equal(provider.recoverCalls, 0);
});

class MemoryLedger implements PaymentAttemptLedgerPort {
  public readonly attempts = new Map<string, PaymentAttempt>();
  public readonly protectedEvidence = new Map<string, CardSyncEvidenceV1>();
  public failNextInsert = false;
  public failNextUpdate = false;
  public failUpdateWhenState: PaymentAttempt["state"] | null = null;
  public returnFalseWhenNextState: PaymentAttempt["state"] | null = null;

  public async insertIfUnblocked(value: PaymentAttempt): Promise<PaymentAttempt | null> {
    if (this.failNextInsert) {
      this.failNextInsert = false;
      throw new Error("insert failed");
    }
    const blocking = this.blockingFor(value.orderGuid);
    if (blocking) return clone(blocking);
    if (this.attempts.has(value.attemptId)) throw new Error("duplicate attempt");
    this.attempts.set(value.attemptId, clone(value));
    return null;
  }

  public async compareAndUpdate(
    expected: PaymentAttempt,
    next: PaymentAttempt,
    protectedSyncEvidence?: CardSyncEvidenceV1,
  ): Promise<boolean> {
    if (this.failNextUpdate || this.failUpdateWhenState === next.state) {
      this.failNextUpdate = false;
      throw new Error("update failed");
    }
    if (this.returnFalseWhenNextState === next.state) {
      this.returnFalseWhenNextState = null;
      return false;
    }
    const current = this.attempts.get(expected.attemptId);
    if (
      !current ||
      current.state !== expected.state ||
      current.updatedAtIso !== expected.updatedAtIso
    ) {
      return false;
    }
    this.attempts.set(next.attemptId, clone(next));
    if (protectedSyncEvidence) {
      this.protectedEvidence.set(next.attemptId, protectedSyncEvidence);
    }
    return true;
  }

  public async get(attemptId: string): Promise<PaymentAttempt | null> {
    const value = this.attempts.get(attemptId);
    return value ? clone(value) : null;
  }

  public async verifyProviderEnvironment(
    expected: PaymentAttempt,
    environment: string,
  ): Promise<boolean> {
    const current = this.attempts.get(expected.attemptId);
    if (
      !current ||
      current.state === "Created" ||
      current.providerEnvironment ||
      current.provider !== expected.provider ||
      current.operation !== expected.operation ||
      current.amount.cents !== expected.amount.cents
    ) return false;
    this.attempts.set(
      expected.attemptId,
      clone({ ...current, providerEnvironment: environment }),
    );
    return true;
  }

  public async findBlocking(orderGuid: string): Promise<PaymentAttempt | null> {
    const blocking = this.blockingFor(orderGuid);
    return blocking ? clone(blocking) : null;
  }

  public seed(value: PaymentAttempt, _blocking: boolean): void {
    this.attempts.set(value.attemptId, clone(value));
  }

  private blockingFor(orderGuid: string): PaymentAttempt | null {
    const candidates = [...this.attempts.values()].filter(
      (value) => value.orderGuid === orderGuid && isBlockingState(value.state),
    );
    return candidates.length ? candidates[candidates.length - 1]! : null;
  }
}

class MemoryActionBindings implements PaymentActionBindingPort {
  public readonly bindings = new Map<string, PaymentActionBinding>();
  public failNextBind = false;

  public async bindOrGet(
    proposed: PaymentActionBinding,
  ): Promise<PaymentActionBinding> {
    if (this.failNextBind) {
      this.failNextBind = false;
      throw new Error("binding commit outcome unknown");
    }
    const key = `${proposed.orderGuid}\u0000${proposed.actionId}`;
    const existing = this.bindings.get(key);
    if (existing) return { ...existing };
    this.bindings.set(key, { ...proposed });
    return { ...proposed };
  }

  public async getByAttempt(
    attemptId: string,
  ): Promise<PaymentActionBinding | null> {
    const binding = [...this.bindings.values()].find(
      (candidate) => candidate.attemptId === attemptId,
    );
    return binding ? { ...binding, actor: { ...binding.actor } } : null;
  }
}

class DraftGuard implements PersistedOrderDraftPort {
  public fail = false;
  public calls = 0;

  public async assertPersisted(orderGuid: string): Promise<void> {
    this.calls += 1;
    if (this.fail) throw new Error(`draft missing: ${orderGuid}`);
  }
}

class FakeProvider implements OnlinePaymentPort {
  public submitCalls = 0;
  public recoverCalls = 0;
  public cancelCalls = 0;
  public refundCalls = 0;
  public submitResult: (attempt: PaymentAttempt) => Promise<PaymentProviderResult> = async () =>
    result("Approved");
  public recoverResult: (attempt: PaymentAttempt) => Promise<PaymentProviderResult> = async () =>
    result("Approved");
  public cancelResult: (attempt: PaymentAttempt) => Promise<PaymentProviderResult> = async () =>
    result("Cancelled");
  public refundResult: (attempt: PaymentAttempt) => Promise<PaymentProviderResult> = async () =>
    result("Approved");
  /** Linkly 新 attempt 必须在 provider 边界冻结环境。 */
  public providerEnvironment?: string;

  public constructor(public readonly provider: PaymentProvider) {
    // 测试 provider 也必须提供创建前冻结的环境；生产实现对 Square/Linkly 一致执行该门禁。
    if (provider === "linkly-cloud" || provider === "square") {
      this.providerEnvironment = "test-environment";
    }
  }

  public async submit(value: PaymentAttempt): Promise<PaymentProviderResult> {
    this.submitCalls += 1;
    return withDefaultProtectedEvidence(await this.submitResult(value), value);
  }

  public async recover(value: PaymentAttempt): Promise<PaymentProviderResult> {
    this.recoverCalls += 1;
    return withDefaultProtectedEvidence(await this.recoverResult(value), value);
  }

  public async cancel(value: PaymentAttempt): Promise<PaymentProviderResult> {
    this.cancelCalls += 1;
    return this.cancelResult(value);
  }

  public async refund(value: PaymentAttempt): Promise<PaymentProviderResult> {
    this.refundCalls += 1;
    return withDefaultProtectedEvidence(await this.refundResult(value), value);
  }
}

class AbortableFakeProvider extends FakeProvider {
  public recoverWithControlCalls = 0;
  public readonly recoveryControls: {
    signal: AbortSignal;
    deadlineAtMs: number;
  }[] = [];
  public recoverWithControlResult: (
    attempt: PaymentAttempt,
    control: { signal: AbortSignal; deadlineAtMs: number },
  ) => Promise<PaymentProviderResult> = async () => result("Unknown");

  public async recoverWithControl(
    value: PaymentAttempt,
    control: { signal: AbortSignal; deadlineAtMs: number },
  ): Promise<PaymentProviderResult> {
    this.recoverWithControlCalls += 1;
    this.recoveryControls.push(control);
    return withDefaultProtectedEvidence(
      await this.recoverWithControlResult(value, control),
      value,
    );
  }
}

function createService(options: {
  ledger: MemoryLedger;
  bindings?: MemoryActionBindings;
  provider?: FakeProvider;
  providers?: readonly FakeProvider[];
  drafts?: DraftGuard;
  online?: boolean;
  legacyPaymentRecoveryEnvironment?: LegacyPaymentRecoveryEnvironmentResolver;
  trustedRefundReferenceSeed?: TrustedRefundReferenceSeedHook;
  createAttemptId?: () => string;
}): PaymentAttemptService {
  const providers = options.providers ?? [options.provider ?? new FakeProvider("square")];
  let id = 0;
  return new PaymentAttemptService({
    ledger: options.ledger,
    actionBindings:
      options.bindings ??
      bindingsByLedger.get(options.ledger) ??
      rememberBindings(options.ledger),
    drafts: options.drafts ?? new DraftGuard(),
    connectivity: { isOnline: async () => options.online ?? true },
    providers: {
      get(provider) {
        const match = providers.find((candidate) => candidate.provider === provider);
        if (!match) throw new Error(`missing provider: ${provider}`);
        return match;
      },
    },
    createAttemptId: options.createAttemptId ?? (() => `attempt-${++id}`),
    createIdempotencyKey: () => `key-${id}`,
    nowIso: () => "2026-07-28T00:00:00.000Z",
    ...(options.legacyPaymentRecoveryEnvironment
      ? { legacyPaymentRecoveryEnvironment: options.legacyPaymentRecoveryEnvironment }
      : {}),
    ...(options.trustedRefundReferenceSeed
      ? {
          trustedRefundReferenceSeed:
            options.trustedRefundReferenceSeed,
        }
      : {}),
  });
}

function input() {
  return {
    actionId: "action-1",
    orderGuid: "order-1",
    provider: "square" as const,
    operation: "purchase" as const,
    amount,
    actor: auditActor(),
  };
}

function refundInput(index: number, provider: PaymentProvider) {
  return {
    ...input(),
    actionId: `action-seeded-${index}`,
    orderGuid: `order-seeded-${index}`,
    provider,
    operation: "refund" as const,
    amount: { currency: "AUD" as const, cents: -1_250 },
    refundCapacityId: `capacity-seeded-${index}`,
  };
}

function auditActor() {
  return {
    cashierId: "cashier-alice",
    cashierName: "Alice",
    userGuid: "user-alice",
  } as const;
}

function trustedRefundSeed(
  inputValue: TrustedRefundReferenceSeedInput,
  protectedReference: string,
): TrustedRefundReferenceSeed {
  switch (inputValue.provider) {
    case "square":
      return {
        provider: "square",
        paymentId: protectedReference,
      };
    case "linkly-cloud":
      return {
        provider: "linkly-cloud",
        rfn: protectedReference,
      };
  }
}

const bindingsByLedger = new WeakMap<MemoryLedger, MemoryActionBindings>();

function rememberBindings(ledger: MemoryLedger): MemoryActionBindings {
  const bindings = new MemoryActionBindings();
  bindingsByLedger.set(ledger, bindings);
  return bindings;
}

function attempt(
  overrides: Partial<PaymentAttempt> & Pick<Partial<PaymentAttempt>, "state"> = {},
): PaymentAttempt {
  return {
    attemptId: overrides.attemptId ?? "attempt-existing",
    idempotencyKey: overrides.idempotencyKey ?? "key-existing",
    orderGuid: overrides.orderGuid ?? "order-1",
    provider: overrides.provider ?? "square",
    operation: overrides.operation ?? "purchase",
    amount: overrides.amount ?? amount,
    state: overrides.state ?? "Created",
    references: overrides.references ?? references(),
    createdAtIso: overrides.createdAtIso ?? "2026-07-28T00:00:00.000Z",
    updatedAtIso: overrides.updatedAtIso ?? "2026-07-28T00:00:00.000Z",
    lastErrorCode: overrides.lastErrorCode ?? null,
    ...(overrides.providerEnvironment !== undefined
      ? { providerEnvironment: overrides.providerEnvironment }
      : {}),
  };
}

function references(
  overrides: Partial<PaymentProviderReferences> = {},
): PaymentProviderReferences {
  return {
    checkoutId: overrides.checkoutId ?? null,
    paymentId: overrides.paymentId ?? null,
    sessionId: overrides.sessionId ?? null,
    txnRef: overrides.txnRef ?? null,
    rfn: overrides.rfn ?? null,
    voucherReservationToken: overrides.voucherReservationToken ?? null,
  };
}

function result(
  state: PaymentProviderResult["state"],
  referenceOverrides: Partial<PaymentProviderReferences> = {},
): PaymentProviderResult {
  return {
    state,
    references: references(referenceOverrides),
    receiptText: null,
    responseCode: null,
  };
}

function withDefaultProtectedEvidence(
  providerResult: PaymentProviderResult,
  current: PaymentAttempt,
): PaymentProviderResult {
  if (
    providerResult.state !== "Approved" ||
    current.provider === "voucher" ||
    providerResult.protectedSyncEvidence !== undefined
  ) {
    return providerResult;
  }
  const transactionReference =
    providerResult.references.paymentId ??
    providerResult.references.txnRef ??
    `${current.provider}-approved`;
  return {
    ...providerResult,
    protectedSyncEvidence: cardEvidence(current, transactionReference),
  };
}

function cardEvidence(
  current: Pick<PaymentAttempt, "provider" | "operation" | "amount">,
  transactionReference: string,
): CardSyncEvidenceV1 {
  if (current.provider === "voucher") {
    throw new Error("voucher cannot create card evidence");
  }
  return {
    version: 1,
    provider: current.provider,
    operation: current.operation,
    processor: current.provider === "square" ? "Square" : "ANZ",
    txnRef: transactionReference,
    authCode: null,
    cardType: null,
    cardBin: null,
    maskedCardNumber: null,
    merchantId: null,
    responseCode: null,
    responseText: null,
    stan: null,
    bankDateTimeIso: null,
    amountCents: Math.abs(current.amount.cents),
    refundReference: null,
  };
}

function assertProviderCalls(
  provider: FakeProvider,
  expected: Readonly<{
    submit: number;
    recover: number;
    cancel: number;
    refund: number;
  }>,
): void {
  assert.equal(provider.submitCalls, expected.submit);
  assert.equal(provider.recoverCalls, expected.recover);
  assert.equal(provider.cancelCalls, expected.cancel);
  assert.equal(provider.refundCalls, expected.refund);
}

function clone(value: PaymentAttempt): PaymentAttempt {
  return {
    ...value,
    amount: { ...value.amount },
    references: { ...value.references },
  };
}

function isBlockingState(state: PaymentAttempt["state"]): boolean {
  return ["Created", "Submitted", "Pending", "Unknown", "Approved"].includes(state);
}

function createDeferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((promiseResolve, promiseReject) => {
    resolve = promiseResolve;
    reject = promiseReject;
  });
  return { promise, resolve, reject };
}

async function waitUntil(predicate: () => boolean): Promise<void> {
  for (let index = 0; index < 100; index += 1) {
    if (predicate()) return;
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  }
  throw new Error("condition not reached");
}

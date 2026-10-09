import assert from "node:assert/strict";
import test from "node:test";

import type { PaymentAttempt } from "@hb/pos-domain/core/contracts/payment";

import {
  LinklySupervisorAckSessionNotFoundError,
  SupervisorResolutionAcknowledgementService,
  type SupervisorResolutionAckQueuePort,
} from "./supervisor-resolution-acknowledgement-service";

function attempt(overrides: Partial<PaymentAttempt> = {}): PaymentAttempt {
  return {
    attemptId: "attempt-1",
    idempotencyKey: "idempotency-1",
    orderGuid: "order-1",
    provider: "linkly-cloud",
    operation: "purchase",
    amount: { currency: "AUD", cents: 500 },
    state: "Unknown",
    references: { checkoutId: null, paymentId: null, sessionId: "session-1", txnRef: null, rfn: null, voucherReservationToken: null },
    createdAtIso: "2026-09-09T00:00:00.000Z",
    updatedAtIso: "2026-09-09T00:00:01.000Z",
    lastErrorCode: null,
    providerEnvironment: "production",
    providerAcknowledgedAtIso: null,
    ...overrides,
  };
}

function setup(current: PaymentAttempt | null, acknowledge: (a: PaymentAttempt) => Promise<void>) {
  const state = { acknowledgedAtIso: null as string | null, failures: [] as string[] };
  const queue: SupervisorResolutionAckQueuePort = {
    async listPending() { return state.acknowledgedAtIso ? [] : [{ attemptId: "attempt-1" }]; },
    async markAcknowledged(_id, iso) {
      if (state.acknowledgedAtIso) return false;
      state.acknowledgedAtIso = iso;
      return true;
    },
    async recordFailure(_id, code) { state.failures.push(code); },
  };
  const calls = { acknowledge: 0 };
  const service = new SupervisorResolutionAcknowledgementService({
    queue,
    ledger: { async get() { return current; } },
    acknowledger: {
      async acknowledgeSupervisorResolved(source) { calls.acknowledge += 1; await acknowledge(source); },
    },
    nowIso: () => "2026-09-09T00:00:02.000Z",
  });
  return { service, state, calls };
}

test("ACK 成功后才标记队列；失败保留队列并记录错误码，下次 drain 重试", async () => {
  let failing = true;
  const { service, state, calls } = setup(attempt(), async () => {
    if (failing) throw Object.assign(new Error("net"), { code: "LINKLY_NETWORK_DOWN" });
  });
  assert.deepEqual(await service.drain(), { acknowledged: 0, pending: 1 });
  assert.equal(state.acknowledgedAtIso, null);
  assert.deepEqual(state.failures, ["LINKLY_NETWORK_DOWN"]);

  failing = false;
  assert.deepEqual(await service.drain(), { acknowledged: 1, pending: 0 });
  assert.equal(state.acknowledgedAtIso, "2026-09-09T00:00:02.000Z");
  assert.equal(calls.acknowledge, 2);
  assert.deepEqual(await service.drain(), { acknowledged: 0, pending: 0 });
  assert.equal(calls.acknowledge, 2);
});

test("同一时刻只有一个 drain，避免并发重复 POST", async () => {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const { service, calls } = setup(attempt(), () => gate);
  const first = service.drain();
  const second = service.drain();
  assert.equal(first, second);
  release();
  await first;
  assert.equal(calls.acknowledge, 1);
});

test("后端无此会话视为无需确认；缺失 attempt 保持待办", async () => {
  const gone = setup(attempt(), async () => { throw new LinklySupervisorAckSessionNotFoundError(); });
  assert.deepEqual(await gone.service.drain(), { acknowledged: 1, pending: 0 });

  const missing = setup(null, async () => undefined);
  assert.deepEqual(await missing.service.drain(), { acknowledged: 0, pending: 1 });
  assert.deepEqual(missing.state.failures, ["SUPERVISOR_ACK_ATTEMPT_NOT_FOUND"]);
  assert.equal(missing.calls.acknowledge, 0);
});

test("迟到的 provider 终态已落账时不再用主管标记覆盖，交给既有终态 ACK 管线", async () => {
  for (const state of ["Approved", "Declined", "Cancelled"] as const) {
    const { service, calls, state: queueState } = setup(attempt({ state }), async () => undefined);
    assert.deepEqual(await service.drain(), { acknowledged: 1, pending: 0 });
    assert.equal(calls.acknowledge, 0);
    assert.notEqual(queueState.acknowledgedAtIso, null);
  }
});

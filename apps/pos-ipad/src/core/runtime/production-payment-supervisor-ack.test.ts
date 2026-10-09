import assert from "node:assert/strict";
import test from "node:test";

import { CurrentCashierSession } from "./current-cashier-session";
import { createProductionPaymentRuntime } from "./production-payment-runtime";

import type { PaymentAttempt, PaymentProvider } from "@/core/contracts";
import type { PosDatabase } from "@/core/db/pos-database";
import type {
  ManualPaymentRecoveryFindingInput,
  PaymentRecoveryCenterRecord,
} from "@/core/db/sqlite-payment-recovery-center-store";
import type {
  PosRepositoryBundle,
  SensitivePayloadEncryptor,
} from "@/core/db/sqlite-repositories";
import type { PaymentProviderRuntimeBootstrap } from "@/core/runtime/payment-provider-runtime-bootstrap";
import { PricingCart } from "@/features/sales/domain";
import { ActivePricingCartSession } from "@/features/sales/runtime";

const encryptor: SensitivePayloadEncryptor = {
  async encrypt(value) { return new TextEncoder().encode(value); },
  async decrypt(value) { return new TextDecoder().decode(value); },
};

const ALL_PERMISSIONS = [
  "Permissions.PosTerminal.Payment.View",
  "Permissions.PosTerminal.Payment.TakeCash",
  "Permissions.PosTerminal.Payment.TakeCard",
  "Permissions.PosTerminal.Payment.TakeVoucher",
  "Permissions.PosTerminal.Payment.RemoveTender",
  "Permissions.PosTerminal.Payment.Confirm",
];

function linklyAttempt(state: PaymentAttempt["state"] = "Unknown"): PaymentAttempt {
  return {
    attemptId: "linkly-attempt-1",
    idempotencyKey: "linkly-idempotency-1",
    orderGuid: "order-1",
    provider: "linkly-cloud",
    providerEnvironment: "Sandbox",
    operation: "purchase",
    amount: { currency: "AUD", cents: 1_000 },
    state,
    references: {
      checkoutId: null, paymentId: null, sessionId: "session-1",
      txnRef: null, rfn: null, voucherReservationToken: null,
    },
    createdAtIso: "2026-07-28T00:00:00.000Z",
    updatedAtIso: "2026-07-28T00:01:00.000Z",
    lastErrorCode: null,
  };
}

function record(): PaymentRecoveryCenterRecord {
  return {
    recordId: "recovery-record-1",
    checkoutIntentId: "checkout-1",
    orderGuid: "order-1",
    attemptId: "linkly-attempt-1",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    terminalName: null,
    occurredAtIso: "2026-07-28T00:00:00.000Z",
    amountCents: 1_000,
    provider: "linkly-cloud",
    attemptState: "Unknown",
    orderState: "Draft",
    isParked: true,
    status: "result-unknown",
    transactionReference: null,
    receiptReference: null,
    terminalAmountMismatchCents: null,
    lines: [],
    events: [],
  };
}

type QueueRow = { attemptId: string; acknowledgedAtIso: string | null; failures: string[] };

function harness(options: Readonly<{
  failAckTimes?: number;
  attemptState?: PaymentAttempt["state"];
  queued?: boolean;
  withSupervisorAck?: boolean;
}> = {}) {
  const attempt = linklyAttempt(options.attemptState ?? "Unknown");
  const rows = new Map<string, QueueRow>();
  if (options.queued !== false) {
    rows.set(attempt.attemptId, { attemptId: attempt.attemptId, acknowledgedAtIso: null, failures: [] });
  }
  let supervisorAckCalls = 0;
  let plainAckCalls = 0;
  let failuresLeft = options.failAckTimes ?? 0;
  const findings: ManualPaymentRecoveryFindingInput[] = [];
  const provider: Record<string, unknown> = {
    provider: "linkly-cloud",
    async acknowledge() { plainAckCalls += 1; },
    async submit() { throw new Error("must not submit"); },
    async recover() { throw new Error("must not recover"); },
    async cancel() { throw new Error("must not cancel"); },
    async refund() { throw new Error("must not refund"); },
  };
  if (options.withSupervisorAck !== false) {
    provider.acknowledgeSupervisorResolved = async (source: PaymentAttempt) => {
      supervisorAckCalls += 1;
      assert.equal(source.attemptId, attempt.attemptId);
      if (failuresLeft > 0) {
        failuresLeft -= 1;
        throw new Error("LINKLY_NETWORK_DOWN");
      }
    };
  }
  const queue = {
    async listPending() {
      return [...rows.values()].filter((row) => row.acknowledgedAtIso === null);
    },
    async markAcknowledged(attemptId: string, iso: string) {
      const row = rows.get(attemptId);
      if (!row || row.acknowledgedAtIso) return false;
      row.acknowledgedAtIso = iso;
      return true;
    },
    async recordFailure(attemptId: string, code: string) {
      rows.get(attemptId)?.failures.push(code);
    },
  };
  const current = record();
  const database = {
    paymentDraftRecovery: () => ({
      async assertPersisted() {},
      async findBlockingRecovery() { return null; },
      async readDraft() { return null; },
      async hasLegacyLinklyRecovery() { return false; },
      async findPendingLinklyAcknowledgement() { return null; },
    }),
    paymentRecoveryCenter: () => ({
      async list() { return [current]; },
      async findCurrentCandidate() { return null; },
      async getExact() { return current; },
      async recordManualFinding(input: ManualPaymentRecoveryFindingInput) {
        findings.push(input);
        return { record: current, actionId: input.actionId, authorizationId: input.authorizationId, replayed: false };
      },
    }),
    manualPaymentOrderCommitter: () => ({ async completeManualPaymentOrder() { return { replayed: false }; } }),
    paymentActionBindings: () => ({}),
    paymentSupervisorAckQueue: () => queue,
    voucherPreparationStore: () => ({ async prepare() { return "x"; }, async bindToAttempt() { return null; } }),
    settings: () => ({ async getReceiptPrinterSettings() { return {}; } }),
    paymentOrderCommitter: () => ({}),
    mixedPaymentOrderTruth: () => ({ async getPaymentTruth() { return null; } }),
    mixedPaymentTenders: () => ({}),
    voucherTenderReversals: () => ({ async findBlocking() { return null; } }),
    returnCapacityVault: () => ({}),
  } as unknown as PosDatabase;
  const repositories = {
    orders: { async nextLocalSequence() { return 1; }, async getByGuid() { return null; }, async listLocal() { return []; } },
    payments: {
      async insertIfUnblocked() { return null; },
      async compareAndUpdate() { return true; },
      async get(id: string) { return id === attempt.attemptId ? attempt : null; },
      async findBlocking() { return null; },
    },
  } as unknown as PosRepositoryBundle;
  const cashier = new CurrentCashierSession();
  cashier.activate(
    cashier.beginAuthentication(),
    { source: "online", session: {
      cashierId: "cashier-1", cashierName: "Cashier", storeCode: "S1", deviceCode: "IPAD-1",
      permissionCodes: ALL_PERMISSIONS,
    } },
    { storeCode: "S1", deviceCode: "IPAD-1" },
  );
  let idCounter = 0;
  const runtime = createProductionPaymentRuntime({
    database,
    repositories,
    encryptor,
    activeCart: new ActivePricingCartSession(new PricingCart(), () => new PricingCart()),
    currentCashier: cashier,
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: { now: () => new Date("2026-07-28T00:02:00.000Z"), nowIso: () => "2026-07-28T00:02:00.000Z" },
    createId: () => `id-${++idCounter}`,
    connectivity: { async isOnline() { return true; } },
    bootstrap: {
      providers: {
        get() { return provider; },
        getAvailability(name: PaymentProvider) { return { provider: name, available: name === "linkly-cloud", blocker: null }; },
        listAvailability() { return []; },
        listAvailableProviders() { return ["linkly-cloud"]; },
      } as unknown as PaymentProviderRuntimeBootstrap["providers"],
      configurationAvailability: {} as PaymentProviderRuntimeBootstrap["configurationAvailability"],
      linklyTerminals: { environment: "Sandbox", port: {} as never },
      bindVoucherContextProvider() {},
      createLinklyOperator() { return null; },
    },
    authorizeRecovery: async (_request, run) => run({
      authorizationId: "authorization-1",
      authorizingActor: { cashierId: "supervisor-2", cashierName: "Supervisor", userGuid: "supervisor-user-2" },
    }),
    async drainFulfilment() {},
  });
  return {
    runtime, rows, findings,
    calls: () => ({ supervisorAckCalls, plainAckCalls }),
  };
}

const unpaid = {
  recordId: "recovery-record-1",
  finding: "unpaid" as const,
  verifiedAmountCents: null,
  evidenceReference: "terminal-history",
  note: "No charge found on the terminal",
};

async function settle(): Promise<void> {
  for (let index = 0; index < 20; index += 1) await new Promise((resolve) => setImmediate(resolve));
}

test("M17：主管确认未收款并持久化结论后，向后端补发 supervisorResolved ACK 并标记队列", async () => {
  const h = harness();
  const center = h.runtime.service.recoveryCenter;
  assert.ok(center);
  await center.submitManualVerification(unpaid);
  assert.equal(h.findings.length, 1, "人工结论必须先落库");
  assert.deepEqual(h.calls(), { supervisorAckCalls: 1, plainAckCalls: 0 });
  assert.notEqual(h.rows.get("linkly-attempt-1")?.acknowledgedAtIso, null);
});

test("M17：ACK 失败不回滚人工结论、只记录错误并保留队列；打开恢复中心时自动重试成功", async () => {
  const h = harness({ failAckTimes: 1 });
  const center = h.runtime.service.recoveryCenter;
  assert.ok(center);
  await center.submitManualVerification(unpaid);
  assert.equal(h.findings.length, 1);
  assert.equal(h.rows.get("linkly-attempt-1")?.acknowledgedAtIso, null);
  assert.deepEqual(h.rows.get("linkly-attempt-1")?.failures, ["LINKLY_NETWORK_DOWN"]);

  await center.list();
  await settle();
  assert.equal(h.calls().supervisorAckCalls, 2);
  assert.notEqual(h.rows.get("linkly-attempt-1")?.acknowledgedAtIso, null);

  await center.list();
  await settle();
  assert.equal(h.calls().supervisorAckCalls, 2, "已确认的队列行不再重复 POST");
});

test("M17：冷启动 initializeRecovery 会补发上次崩溃遗留的 ACK", async () => {
  const h = harness();
  await h.runtime.initializeRecovery();
  await settle();
  assert.equal(h.calls().supervisorAckCalls, 1);
  assert.notEqual(h.rows.get("linkly-attempt-1")?.acknowledgedAtIso, null);
});

test("M17：没有入队（仍未知结论）时不发 ACK；迟到的 provider 终态由既有终态 ACK 管线负责", async () => {
  const none = harness({ queued: false });
  await none.runtime.initializeRecovery();
  await none.runtime.service.recoveryCenter?.list();
  await settle();
  assert.deepEqual(none.calls(), { supervisorAckCalls: 0, plainAckCalls: 0 });

  const late = harness({ attemptState: "Approved" });
  await late.runtime.initializeRecovery();
  await settle();
  assert.deepEqual(late.calls(), { supervisorAckCalls: 0, plainAckCalls: 0 });
  assert.notEqual(late.rows.get("linkly-attempt-1")?.acknowledgedAtIso, null);
});

test("M17：provider 不支持 supervisorResolved ACK 时人工结论照常保存，队列行保留", async () => {
  const h = harness({ withSupervisorAck: false });
  const center = h.runtime.service.recoveryCenter;
  assert.ok(center);
  await center.submitManualVerification(unpaid);
  assert.equal(h.findings.length, 1);
  assert.equal(h.rows.get("linkly-attempt-1")?.acknowledgedAtIso, null);
});

import assert from "node:assert/strict";
import test from "node:test";

import { CurrentCashierSession } from "./current-cashier-session";
import { createProductionPaymentRuntime } from "./production-payment-runtime";

import type { PaymentAttempt, PaymentProvider } from "@/core/contracts";
import type { PosDatabase } from "@/core/db/pos-database";
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
  const database = {
    paymentDraftRecovery: () => ({
      async assertPersisted() {},
      async findBlockingRecovery() { return null; },
      async readDraft() { return null; },
      async hasLegacyLinklyRecovery() { return false; },
      async findPendingLinklyAcknowledgement() { return null; },
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
    async drainFulfilment() {},
  });
  return {
    runtime, rows,
    calls: () => ({ supervisorAckCalls, plainAckCalls }),
  };
}


async function settle(): Promise<void> {
  for (let index = 0; index < 20; index += 1) await new Promise((resolve) => setImmediate(resolve));
}

test("M17（手持）：冷启动 initializeRecovery 会补发上次遗留的主管结案 ACK", async () => {
  const h = harness({ failAckTimes: 1 });
  await h.runtime.initializeRecovery();
  await settle();
  assert.equal(h.calls().supervisorAckCalls, 1);
  assert.equal(h.rows.get("linkly-attempt-1")?.acknowledgedAtIso, null, "失败保留队列");
  assert.deepEqual(h.rows.get("linkly-attempt-1")?.failures, ["LINKLY_NETWORK_DOWN"]);

  await h.runtime.supervisorAcknowledgements?.drain();
  assert.equal(h.calls().supervisorAckCalls, 2);
  assert.notEqual(h.rows.get("linkly-attempt-1")?.acknowledgedAtIso, null);
  await h.runtime.supervisorAcknowledgements?.drain();
  assert.equal(h.calls().supervisorAckCalls, 2, "已确认的队列行不再重复 POST");
});

test("M17（手持）：没有入队或 provider 已有终态时不发主管结案 ACK；provider 不支持时不暴露入口", async () => {
  const none = harness({ queued: false });
  await none.runtime.initializeRecovery();
  await settle();
  assert.deepEqual(none.calls(), { supervisorAckCalls: 0, plainAckCalls: 0 });

  const late = harness({ attemptState: "Approved" });
  await late.runtime.initializeRecovery();
  await settle();
  assert.deepEqual(late.calls(), { supervisorAckCalls: 0, plainAckCalls: 0 });

  const unsupported = harness({ withSupervisorAck: false });
  assert.equal(unsupported.runtime.supervisorAcknowledgements, null);
});

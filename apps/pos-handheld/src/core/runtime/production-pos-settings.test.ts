import assert from "node:assert/strict";
import test from "node:test";

import type { AuditEventDraft } from "@hb/pos-domain/core/contracts/order";
import {
  PosSettingsService,
  POS_SETTING_KEYS,
  type PosSettingsApiPort,
  type PosSettingsLocalState,
  type PosSettingsLocalStorePort,
  type PosSettingsSyncResponse,
} from "@hb/pos-sync/features/pos-settings/pos-settings-service";

import {
  createPosSettingBindings,
  createPosSettingsAudit,
  guardPaymentMethodsSave,
  POS_SETTING_MANAGED_ERROR_CODE,
} from "./production-pos-settings";

/** 模拟 expo-pos-runtime：repo 落盘 + 内存快照（支付 bootstrap 读的那份）。 */
function createPaymentMethods(initial = false) {
  const state = { stored: initial, memorySnapshot: initial, saves: [] as boolean[] };
  return {
    state,
    port: {
      load: async () => ({ useManualCard: state.stored }),
      save: async (settings: { useManualCard: boolean }) => {
        state.saves.push(settings.useManualCard);
        state.stored = settings.useManualCard;
        // 与 expo-pos-runtime 一致：落盘后刷新内存快照。
        state.memorySnapshot = settings.useManualCard;
      },
    },
  };
}

function createCashMovementStore(initial = false) {
  const state = { enabled: initial };
  return {
    state,
    store: {
      readCashMovementEnabled: async () => state.enabled,
      writeCashMovementEnabled: async (value: boolean) => {
        state.enabled = value;
      },
    },
  };
}

function createLocalStore(): PosSettingsLocalStorePort {
  let saved: PosSettingsLocalState | null = null;
  return {
    load: async () => saved,
    save: async (state) => {
      saved = state;
    },
  };
}

function createApi(response: PosSettingsSyncResponse): PosSettingsApiPort {
  return {
    sync: async () => response,
    ack: async () => undefined,
  };
}

const MANAGED_ON: PosSettingsSyncResponse = {
  changed: true,
  hash: "hash-1",
  settings: { "payment.manualCard": { value: true, source: "store" } },
};

test("bindings：有付款方式存取时包含 cashMovement 与 manualCard；没有时只剩 cashMovement", () => {
  const cash = createCashMovementStore();
  const payments = createPaymentMethods();
  assert.deepEqual(
    createPosSettingBindings({ store: cash.store, paymentMethods: payments.port }).map((b) => b.key),
    ["cashMovement.enabled", "payment.manualCard"],
  );
  assert.deepEqual(
    createPosSettingBindings({ store: cash.store }).map((b) => b.key),
    ["cashMovement.enabled"],
  );
});

test("manualCard binding 写穿走原始 save，内存快照同步刷新；读取取落盘值", async () => {
  const cash = createCashMovementStore();
  const payments = createPaymentMethods(false);
  const manual = createPosSettingBindings({ store: cash.store, paymentMethods: payments.port })[1]!;
  assert.equal(await manual.read(), false);
  await manual.write(true);
  assert.deepEqual(payments.state.saves, [true]);
  assert.equal(payments.state.memorySnapshot, true);
  assert.equal(await manual.read(), true);
});

test("cashMovement binding 读写本机键", async () => {
  const cash = createCashMovementStore();
  const binding = createPosSettingBindings({ store: cash.store })[0]!;
  assert.equal(await binding.read(), false);
  await binding.write(true);
  assert.equal(cash.state.enabled, true);
  assert.equal(await binding.read(), true);
});

test("审计适配：事件类型、payload 键值与 actor 三字段；键名只在 action 的值里", async () => {
  const events: AuditEventDraft[] = [];
  const audit = createPosSettingsAudit({
    append: async (batch) => {
      events.push(...batch);
    },
    nowIso: () => "2026-11-14T01:00:00.000Z",
    createId: (() => {
      let n = 0;
      return () => `id-${++n}`;
    })(),
    readActor: () => ({ cashierId: "C1", cashierName: "Alice", userGuid: "U1" }),
  });
  await audit.recordOfflineOverride({
    key: "payment.manualCard",
    previous: true,
    current: false,
    managedValue: true,
    managedSource: "store",
  });
  assert.equal(events.length, 1);
  const event = events[0]!;
  assert.equal(event.eventType, "POS_SETTING_OFFLINE_OVERRIDE");
  assert.equal(event.orderGuid, null);
  assert.equal(event.occurredAtIso, "2026-11-14T01:00:00.000Z");
  assert.deepEqual(event.payload, {
    source: "pos-settings",
    action: "payment.manualCard",
    status: "off",
    reason: "OFFLINE",
    requestingCashierId: "C1",
    requestingCashierName: "Alice",
    requestingUserGuid: "U1",
  });
  // 键本身不能含 payment/card 等敏感字样（assertSafe 会拒绝）。
  assert.equal(Object.keys(event.payload).some((key) => /payment|card|token|voucher/i.test(key)), false);
});

test("审计适配：取不到收银员时省略 actor 仍然写入；开启状态记为 on", async () => {
  const events: AuditEventDraft[] = [];
  const audit = createPosSettingsAudit({
    append: async (batch) => {
      events.push(...batch);
    },
    nowIso: () => "2026-11-14T01:00:00.000Z",
    createId: () => "id",
    readActor: () => {
      throw new Error("CURRENT_CASHIER_REQUIRED");
    },
  });
  await audit.recordOfflineOverride({
    key: "cashMovement.enabled",
    previous: false,
    current: true,
    managedValue: false,
    managedSource: "global",
  });
  assert.deepEqual(events[0]!.payload, {
    source: "pos-settings",
    action: "cashMovement.enabled",
    status: "on",
    reason: "OFFLINE",
  });
});

async function createGuardHarness(options: Readonly<{ online: boolean; managed: boolean }>) {
  const cash = createCashMovementStore();
  const payments = createPaymentMethods(false);
  const audits: AuditEventDraft[] = [];
  const service = new PosSettingsService({
    api: createApi(options.managed ? MANAGED_ON : { changed: true, hash: "h0", settings: {} }),
    store: createLocalStore(),
    bindings: createPosSettingBindings({ store: cash.store, paymentMethods: payments.port }),
    boundStoreCode: () => "S001",
    appVersion: () => "1.0.0",
    now: () => Date.parse("2026-11-14T00:00:00Z"),
    audit: createPosSettingsAudit({
      append: async (batch) => {
        audits.push(...batch);
      },
      nowIso: () => "2026-11-14T00:00:00.000Z",
      createId: () => "audit-id",
      readActor: () => ({ cashierId: "C1", cashierName: "Alice", userGuid: null }),
    }),
  });
  await service.load();
  await service.syncNow();
  service.reportConnectivity(options.online);
  // 守卫包装的是「原始 save」；trySet 内部的 binding 也用同一个原始 save，不会递归进守卫。
  const guarded = guardPaymentMethodsSave({
    service,
    load: payments.port.load,
    save: payments.port.save,
  });
  return { service, payments, audits, guarded, cash };
}

test("守卫：在线且被总部管理 → 抛 POS_SETTING_MANAGED 且不写任何内容", async () => {
  const { payments, guarded, service } = await createGuardHarness({ online: true, managed: true });
  // 同步已把总部值 true 写穿；此时用户试图改回 false。
  assert.equal(payments.state.stored, true);
  payments.state.saves.length = 0;
  await assert.rejects(() => guarded({ useManualCard: false }), (error: unknown) => {
    assert.equal((error as { code?: string }).code, POS_SETTING_MANAGED_ERROR_CODE);
    return true;
  });
  assert.deepEqual(payments.state.saves, []);
  assert.equal(payments.state.stored, true);
  assert.equal(payments.state.memorySnapshot, true);
  assert.equal(service.isLocked(POS_SETTING_KEYS.manualCard), true);
});

test("守卫：没有总部管理时经 trySet 写穿，内存快照一并刷新，且只写一次", async () => {
  const { payments, guarded } = await createGuardHarness({ online: true, managed: false });
  payments.state.saves.length = 0;
  await guarded({ useManualCard: true });
  assert.deepEqual(payments.state.saves, [true]);
  assert.equal(payments.state.memorySnapshot, true);
});

test("守卫：离线时放行总部管理项并写审计与覆盖次数", async () => {
  const { payments, guarded, audits, service } = await createGuardHarness({ online: false, managed: true });
  payments.state.saves.length = 0;
  await guarded({ useManualCard: false });
  assert.deepEqual(payments.state.saves, [false]);
  assert.equal(payments.state.memorySnapshot, false);
  assert.equal(audits.length, 1);
  assert.equal(audits[0]!.eventType, "POS_SETTING_OFFLINE_OVERRIDE");
  const item = service.getView().items.find((entry) => entry.key === POS_SETTING_KEYS.manualCard)!;
  assert.equal(item.isOfflineOverride, true);
});

test("守卫：无变更直接放行到原始 save，不调用 trySet", async () => {
  const payments = createPaymentMethods(true);
  let trySetCalls = 0;
  const guarded = guardPaymentMethodsSave({
    service: {
      trySet: async () => {
        trySetCalls += 1;
        return { outcome: "applied" } as const;
      },
    },
    load: payments.port.load,
    save: payments.port.save,
  });
  await guarded({ useManualCard: true });
  assert.equal(trySetCalls, 0);
  assert.deepEqual(payments.state.saves, [true]);
});

test("守卫：有变更必须调用 trySet 并带上 payment.manualCard 键", async () => {
  const payments = createPaymentMethods(false);
  const seen: unknown[] = [];
  const guarded = guardPaymentMethodsSave({
    service: {
      trySet: async (changes) => {
        seen.push(changes);
        return { outcome: "applied" } as const;
      },
    },
    load: payments.port.load,
    save: payments.port.save,
  });
  await guarded({ useManualCard: true });
  assert.deepEqual(seen, [[{ key: "payment.manualCard", value: true }]]);
  // applied 表示已由 binding 写穿：守卫自己不能再 save 一遍。
  assert.deepEqual(payments.state.saves, []);
});

test("守卫：trySet 返回 locked 抛带码错误；failed / unknown-key 抛通用错误；都不写入", async () => {
  for (const outcome of [
    { outcome: "locked", key: "payment.manualCard" },
    { outcome: "failed" },
    { outcome: "unknown-key", key: "x" },
  ] as const) {
    const payments = createPaymentMethods(false);
    const guarded = guardPaymentMethodsSave({
      service: { trySet: async () => outcome },
      load: payments.port.load,
      save: payments.port.save,
    });
    await assert.rejects(() => guarded({ useManualCard: true }), (error: unknown) => {
      const code = (error as { code?: string }).code;
      if (outcome.outcome === "locked") assert.equal(code, POS_SETTING_MANAGED_ERROR_CODE);
      else assert.notEqual(code, POS_SETTING_MANAGED_ERROR_CODE);
      return true;
    });
    assert.deepEqual(payments.state.saves, []);
  }
});

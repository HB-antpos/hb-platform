import assert from "node:assert/strict";
import test from "node:test";

import {
  PosSettingsService,
  type PosSettingsLocalState,
  type PosSettingsSyncResponse,
} from "@hb/pos-sync/features/pos-settings/pos-settings-service";

import {
  createGuardedPaymentMethodsSave,
  createPosSettingsAuditPort,
  createPosSettingsBindings,
  PosSettingManagedError,
} from "./production-pos-settings";

import type { AuditEventDraft } from "@/core/contracts";
import {
  DEFAULT_PAYMENT_METHOD_SETTINGS,
  type PaymentMethodSettings,
} from "@/features/settings/payment-method-settings";

const CASH = "cashMovement.enabled";
const CARD = "payment.manualCard";
const VOUCHER = "payment.voucher";

/**
 * 模拟 expo-pos-runtime 的付款方式存储：repository（落盘值）+ 闭包内存快照（支付页读它）。
 * 写穿必须经 save 回调同时刷新两者，直接写 repository 会让 memory 落后。
 */
function paymentStore(initial: PaymentMethodSettings = DEFAULT_PAYMENT_METHOD_SETTINGS) {
  const store = {
    persisted: initial,
    memory: initial,
    saves: [] as PaymentMethodSettings[],
    load: async () => store.persisted,
    save: async (next: PaymentMethodSettings) => {
      store.saves.push(next);
      store.persisted = next;
      store.memory = next;
    },
  };
  return store;
}

function cashStore(initial = false) {
  const store = {
    value: initial,
    writes: [] as boolean[],
    load: async () => store.value,
    save: async (enabled: boolean) => {
      store.writes.push(enabled);
      store.value = enabled;
    },
  };
  return store;
}

function build(options: {
  payment?: ReturnType<typeof paymentStore> | null;
  cash?: ReturnType<typeof cashStore>;
  sync?: () => Promise<PosSettingsSyncResponse>;
  initialState?: PosSettingsLocalState | null;
} = {}) {
  const payment = options.payment === null ? null : (options.payment ?? paymentStore());
  const cash = options.cash ?? cashStore();
  const audits: AuditEventDraft[] = [];
  let ids = 0;
  const service = new PosSettingsService({
    api: {
      sync: options.sync ?? (async () => ({ changed: false, hash: "", settings: null })),
      ack: async () => undefined,
    },
    store: {
      load: async () => options.initialState ?? null,
      save: async () => undefined,
    },
    bindings: createPosSettingsBindings({
      cashMovement: cash,
      paymentMethods: payment ?? undefined,
    }),
    boundStoreCode: () => "S001",
    appVersion: () => "1.0.0",
    now: () => Date.parse("2026-11-14T00:00:00Z"),
    audit: createPosSettingsAuditPort({
      audit: { append: async (events: readonly AuditEventDraft[]) => { audits.push(...events); } } as never,
      nowIso: () => "2026-11-14T00:00:00.000Z",
      createId: () => `id-${++ids}`,
      actor: () => ({ cashierId: "C1", cashierName: "Amy", userGuid: "U1" }),
    }),
  });
  const guarded = payment
    ? createGuardedPaymentMethodsSave({
        load: payment.load,
        save: payment.save,
        trySet: (changes) => service.trySet(changes),
      })
    : null;
  return { service, payment, cash, audits, guarded };
}

function managed(settings: Record<string, { value: boolean; source: string }>): PosSettingsSyncResponse {
  return { changed: true, hash: "h1", settings };
}

test("绑定：没有付款方式存储时只暴露 cashMovement 一个 binding", async () => {
  const { service } = build({ payment: null });
  const view = await service.load();
  assert.deepEqual(view.items.map((item) => item.key), [CASH]);
});

test("绑定：有付款方式存储时键顺序为 cashMovement、manualCard、voucher", async () => {
  const { service } = build();
  const view = await service.load();
  assert.deepEqual(view.items.map((item) => item.key), [CASH, CARD, VOUCHER]);
});

test("写穿 manualCard：读后合并另一个键，经原始 save 同时刷新内存快照", async () => {
  const payment = paymentStore({ useManualCard: false, giftCardEnabled: true });
  const { service } = build({
    payment,
    sync: async () => managed({ [CARD]: { value: true, source: "store" } }),
  });
  await service.load();
  const result = await service.requestSync("startup");
  assert.equal(result.status, "updated");
  // 恰好两个键、voucher 保持本机原值，且支付页读的内存快照已刷新。
  assert.deepEqual(payment.saves, [{ useManualCard: true, giftCardEnabled: true }]);
  assert.deepEqual(payment.memory, { useManualCard: true, giftCardEnabled: true });
});

test("写穿 voucher 与 cashMovement：各写各的存储，互不影响", async () => {
  const payment = paymentStore({ useManualCard: true, giftCardEnabled: false });
  const cash = cashStore(false);
  const { service } = build({
    payment,
    cash,
    sync: async () =>
      managed({
        [VOUCHER]: { value: true, source: "global" },
        [CASH]: { value: true, source: "device" },
      }),
  });
  await service.load();
  await service.requestSync("startup");
  assert.deepEqual(payment.memory, { useManualCard: true, giftCardEnabled: true });
  assert.equal(cash.value, true);
  assert.deepEqual(cash.writes, [true]);
});

test("守卫：无变更直接放行给原始保存，不经过服务", async () => {
  const payment = paymentStore({ useManualCard: true, giftCardEnabled: false });
  let trySetCalls = 0;
  const guarded = createGuardedPaymentMethodsSave({
    load: payment.load,
    save: payment.save,
    trySet: async () => {
      trySetCalls += 1;
      return { outcome: "applied" };
    },
  });
  await guarded({ useManualCard: true, giftCardEnabled: false });
  assert.equal(trySetCalls, 0);
  assert.equal(payment.saves.length, 1);
});

test("守卫：有变更必须交给服务 trySet，键映射为 payment.manualCard / payment.voucher，applied 后不再重复保存", async () => {
  const payment = paymentStore({ useManualCard: false, giftCardEnabled: false });
  const seen: unknown[] = [];
  const guarded = createGuardedPaymentMethodsSave({
    load: payment.load,
    save: payment.save,
    trySet: async (changes) => {
      seen.push(changes);
      return { outcome: "applied" };
    },
  });
  await guarded({ useManualCard: true, giftCardEnabled: true });
  assert.deepEqual(seen, [[
    { key: CARD, value: true },
    { key: VOUCHER, value: true },
  ]]);
  // 写穿由服务经 binding 完成；守卫自己不再 save 一遍。
  assert.equal(payment.saves.length, 0);
});

test("守卫：总部管理且在线时抛 POS_SETTING_MANAGED，不写任何内容（含同一次保存里未锁定的另一项）", async () => {
  const payment = paymentStore({ useManualCard: false, giftCardEnabled: false });
  const { service, guarded } = build({
    payment,
    sync: async () => managed({ [CARD]: { value: false, source: "store" } }),
  });
  await service.load();
  await service.requestSync("startup");
  payment.saves.length = 0;

  await assert.rejects(
    () => guarded!({ useManualCard: true, giftCardEnabled: true }),
    (error: unknown) =>
      error instanceof PosSettingManagedError &&
      error.code === "POS_SETTING_MANAGED" &&
      error.message === "POS_SETTING_MANAGED" &&
      error.key === CARD,
  );
  assert.deepEqual(payment.saves, []);
  assert.deepEqual(payment.memory, { useManualCard: false, giftCardEnabled: false });
});

test("守卫：没被总部管理的项在线时照常可改（写穿到原始存储）", async () => {
  const payment = paymentStore({ useManualCard: false, giftCardEnabled: false });
  const { service, guarded } = build({
    payment,
    sync: async () => managed({ [CASH]: { value: true, source: "store" } }),
  });
  await service.load();
  await service.requestSync("startup");
  await guarded!({ useManualCard: true, giftCardEnabled: false });
  assert.deepEqual(payment.memory, { useManualCard: true, giftCardEnabled: false });
});

test("离线时总部管理项可临时修改：写入本机、登记审计（payload 只含安全键，键名在 action 值里，带收银员身份）", async () => {
  const payment = paymentStore({ useManualCard: false, giftCardEnabled: false });
  const { service, guarded, audits } = build({
    payment,
    sync: async () => managed({ [CARD]: { value: false, source: "store" } }),
  });
  await service.load();
  await service.requestSync("startup");
  service.reportConnectivity(false);

  await guarded!({ useManualCard: true, giftCardEnabled: false });
  assert.deepEqual(payment.memory, { useManualCard: true, giftCardEnabled: false });
  const view = service.getView();
  const item = view.items.find((candidate) => candidate.key === CARD);
  assert.equal(item?.isOfflineOverride, true);
  assert.equal(item?.isLocked, false);

  assert.equal(audits.length, 1);
  const event = audits[0]!;
  assert.equal(event.eventType, "POS_SETTING_OFFLINE_OVERRIDE");
  assert.equal(event.orderGuid, null);
  assert.deepEqual(event.payload, {
    source: "pos-settings",
    action: CARD,
    status: "on",
    reason: "OFFLINE",
    requestingCashierId: "C1",
    requestingCashierName: "Amy",
    requestingUserGuid: "U1",
  });
  // 本地 assertSafe 拒绝含 authorization / token / card / voucher / payment / session / reference 的 payload 键。
  for (const key of Object.keys(event.payload)) {
    assert.doesNotMatch(key, /authorization|token|card|voucher|payment|session|reference/iu);
  }
});

test("审计端口：status 随新值变化；取不到收银员身份时仍落审计但不带身份；追加失败向上抛由服务吞掉", async () => {
  const events: AuditEventDraft[] = [];
  const port = createPosSettingsAuditPort({
    audit: { append: async (batch: readonly AuditEventDraft[]) => { events.push(...batch); } } as never,
    nowIso: () => "2026-11-14T00:00:00.000Z",
    createId: () => "id",
    actor: () => { throw new Error("no cashier"); },
  });
  await port.recordOfflineOverride({
    key: VOUCHER, previous: true, current: false, managedValue: true, managedSource: "global",
  });
  assert.deepEqual(events[0]?.payload, {
    source: "pos-settings", action: VOUCHER, status: "off", reason: "OFFLINE",
  });

  const failing = createPosSettingsAuditPort({
    audit: { append: async () => { throw new Error("disk"); } } as never,
    nowIso: () => "2026-11-14T00:00:00.000Z",
    createId: () => "id",
    actor: () => null,
  });
  await assert.rejects(() =>
    failing.recordOfflineOverride({
      key: CARD, previous: false, current: true, managedValue: false, managedSource: "store",
    }),
  );
});

test("守卫：服务写失败 → 通用错误（不是 POS_SETTING_MANAGED）", async () => {
  const payment = paymentStore();
  const guarded = createGuardedPaymentMethodsSave({
    load: payment.load,
    save: payment.save,
    trySet: async () => ({ outcome: "failed" }),
  });
  await assert.rejects(
    () => guarded({ useManualCard: true, giftCardEnabled: false }),
    (error: unknown) => error instanceof Error && !(error instanceof PosSettingManagedError),
  );
});

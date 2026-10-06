import assert from "node:assert/strict";
import test from "node:test";

import {
  deriveNewTransactionGate,
  normalizePosIpadUpdatePolicy,
} from "./app-updates";
import {
  AUD_CASH_DENOMINATIONS_CENTS,
  normalizeDailyCloseCounts,
} from "@hb/pos-domain/core/contracts/daily-close";
import { evaluateDeviceReregistrationPreflight } from "./device-reregistration";
import {
  canCancelInstallmentWithRefund,
  canTransitionInstallment,
} from "@hb/pos-domain/core/contracts/installments";
import { normalizeRemoteHistoryQuery } from "@hb/pos-domain/core/contracts/remote-history";
import { normalizeSpecialProductOrder } from "@hb/pos-domain/core/contracts/special-products";

test("远程历史固定当前门店、WPF 首批 100 条并规范化可选筛选", () => {
  assert.deepEqual(
    normalizeRemoteHistoryQuery(
      {
        storeCode: " OTHER ",
        deviceCode: " IPAD-1 ",
        soldFromIso: "2026-07-27T14:00:00.000Z",
        soldToIso: "2026-07-28T13:59:59.999Z",
        keyword: " 931234 ",
        take: 200,
      },
      " STORE-1 ",
    ),
    {
      storeCode: "STORE-1",
      deviceCode: "IPAD-1",
      soldFromIso: "2026-07-27T14:00:00.000Z",
      soldToIso: "2026-07-28T13:59:59.999Z",
      keyword: "931234",
      take: 100,
    },
  );
  assert.throws(
    () =>
      normalizeRemoteHistoryQuery(
        {
          storeCode: "STORE-1",
          deviceCode: null,
          soldFromIso: "bad",
          soldToIso: "2026-07-28T13:59:59.999Z",
          keyword: null,
        },
        "STORE-1",
      ),
    /date range/i,
  );
});

test("特殊商品本机排序必须完整、唯一且只能包含当前列表商品", () => {
  assert.deepEqual(
    normalizeSpecialProductOrder(
      [" P-2 ", "P-1"],
      new Set(["P-1", "P-2"]),
    ),
    ["P-2", "P-1"],
  );
  assert.throws(
    () =>
      normalizeSpecialProductOrder(
        ["P-1", "P-1"],
        new Set(["P-1", "P-2"]),
      ),
    /complete unique permutation/i,
  );
});

test("日结点钞固定完整 11 种 AUD 面额并使用整数分币汇总", () => {
  const counts = normalizeDailyCloseCounts([
    { denominationCents: 10_000, quantity: 2 },
    { denominationCents: 5, quantity: 3 },
  ]);

  assert.deepEqual(
    counts.map((entry) => entry.denominationCents),
    AUD_CASH_DENOMINATIONS_CENTS,
  );
  assert.equal(
    counts.reduce((total, entry) => total + entry.subtotalCents, 0),
    20_015,
  );
  assert.throws(
    () =>
      normalizeDailyCloseCounts([
        { denominationCents: 500, quantity: 1.5 },
      ]),
    /non-negative integer/i,
  );
});

test("分期只允许 WPF 的单向业务状态迁移", () => {
  assert.equal(canTransitionInstallment("Active", "PaidOff"), true);
  assert.equal(canTransitionInstallment("Active", "Cancelled"), true);
  assert.equal(canTransitionInstallment("PaidOff", "PickedUp"), true);
  // 已付清未提货可取消并全额退款（与服务端 InstallmentLifecycleRules 同口径）。
  assert.equal(canTransitionInstallment("PaidOff", "Cancelled"), true);
  assert.equal(canTransitionInstallment("PickedUp", "Cancelled"), false);
  assert.equal(canTransitionInstallment("Cancelled", "Active"), false);
});

test("分期取消退款只限进行中有余额或已付清未提货", () => {
  assert.equal(canCancelInstallmentWithRefund("Active", 100), true);
  assert.equal(canCancelInstallmentWithRefund("Active", 0), false);
  assert.equal(canCancelInstallmentWithRefund("PaidOff", 0), true);
  assert.equal(canCancelInstallmentWithRefund("PaidOff", 100), false);
  assert.equal(canCancelInstallmentWithRefund("PickedUp", 0), false);
  assert.equal(canCancelInstallmentWithRefund("Cancelled", 0), false);
});

test("iPad 更新策略在本地同时门禁离线现金，但始终开放恢复与补传", () => {
  const policy = normalizePosIpadUpdatePolicy({
    enabled: true,
    minimumSupportedVersion: "1.2.0",
    latestVersion: "1.3.0",
    forceUpdate: true,
    appStoreUrl: "https://apps.apple.com/app/id123456789",
    releaseMessage: "Please update",
  });
  assert.deepEqual(deriveNewTransactionGate(policy), {
    state: "force-update",
    canStartNewTransaction: false,
    canContinueRecovery: true,
  });
  assert.throws(
    () =>
      normalizePosIpadUpdatePolicy({
        ...policy,
        appStoreUrl: "https://example.com/fake-store",
      }),
    /Apple App Store URL/i,
  );
});

test("enabled 关闭时阻止新交易，未完成检查时默认允许", () => {
  assert.deepEqual(
    deriveNewTransactionGate({
      enabled: false,
      minimumSupportedVersion: null,
      latestVersion: null,
      forceUpdate: false,
      appStoreUrl: null,
      releaseMessage: null,
    }),
    {
      state: "disabled",
      canStartNewTransaction: false,
      canContinueRecovery: true,
    },
  );
  assert.deepEqual(deriveNewTransactionGate(null), {
    state: "unchecked",
    canStartNewTransaction: true,
    canContinueRecovery: true,
  });
});

test("iPad 更新策略必须显式拥有六个字段，nullable 字段只能用 null 表示空值", () => {
  const complete: Record<string, unknown> = {
    enabled: true,
    minimumSupportedVersion: "1.2.0",
    latestVersion: "1.3.0",
    forceUpdate: false,
    appStoreUrl: "https://apps.apple.com/app/id123456789",
    releaseMessage: "Please update",
  };
  for (const field of Object.keys(complete)) {
    const incomplete = { ...complete };
    delete incomplete[field];
    assert.throws(() => normalizePosIpadUpdatePolicy(incomplete));
  }
  assert.throws(() =>
    normalizePosIpadUpdatePolicy(Object.create(complete) as unknown),
  );

  for (const field of [
    "minimumSupportedVersion",
    "latestVersion",
    "appStoreUrl",
    "releaseMessage",
  ]) {
    assert.throws(() =>
      normalizePosIpadUpdatePolicy({ ...complete, [field]: undefined }),
    );
    assert.throws(() =>
      normalizePosIpadUpdatePolicy({ ...complete, [field]: "   " }),
    );
  }

  const explicitNulls = {
    ...complete,
    minimumSupportedVersion: null,
    latestVersion: null,
    appStoreUrl: null,
    releaseMessage: null,
  };
  assert.deepEqual(normalizePosIpadUpdatePolicy(explicitNulls), explicitNulls);
});

test("设备重注册在旧 scope 未决事实归零前失败关闭但不删除本地数据", () => {
  assert.deepEqual(
    evaluateDeviceReregistrationPreflight({
      activeCartLineCount: 0,
      unresolvedPaymentCount: 0,
      pendingOrderCount: 2,
      pendingAuditCount: 0,
      supportExportReady: true,
    }),
    {
      allowed: false,
      code: "PENDING_OLD_SCOPE_DATA",
      preserveLocalDatabase: true,
    },
  );
  assert.equal(
    evaluateDeviceReregistrationPreflight({
      activeCartLineCount: 0,
      unresolvedPaymentCount: 0,
      pendingOrderCount: 0,
      pendingAuditCount: 0,
      supportExportReady: false,
    }).allowed,
    true,
  );
});

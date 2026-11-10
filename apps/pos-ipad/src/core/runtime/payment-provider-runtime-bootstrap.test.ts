import assert from "node:assert/strict";
import test from "node:test";

import type { VoucherProtectedTokenPort } from "../../features/payments/voucher";
import type { HbposTransport } from "../api";

import { createPaymentProviderRuntimeBootstrap } from "./payment-provider-runtime-bootstrap";

const transport = {} as HbposTransport;
const voucherTokens = {} as VoucherProtectedTokenPort;

test("持久化成功后更新共享快照，原 provider registry 即时切换且仍可恢复旧卡支付", async () => {
  let methods = { useManualCard: false, giftCardEnabled: false };
  const bootstrap = await createPaymentProviderRuntimeBootstrap({
    transport, voucherProtectedTokens: voucherTokens, readPaymentMethods: () => methods,
    extra: { provider: "square", square: { environment: "Sandbox", deviceId: "SQ", locationId: "LOC" } },
  });
  assert.equal(bootstrap.providers.getAvailability("square").available, true);
  methods = { useManualCard: true, giftCardEnabled: true };
  assert.equal(bootstrap.providers.getAvailability("square").available, false);
  assert.equal(bootstrap.providers.getAvailability("manual-card").available, true);
  assert.equal(bootstrap.providers.getAvailability("voucher").available, true);
  assert.equal(bootstrap.providers.get("square").provider, "square");
  methods = { useManualCard: false, giftCardEnabled: false };
  assert.equal(bootstrap.providers.getAvailability("square").available, true);
  assert.equal(bootstrap.providers.getAvailability("manual-card").available, false);
});

test("手动刷卡替换已配置信用卡，购物券独立启用；分期禁止手动刷卡", async () => {
  for (const giftCardEnabled of [false, true]) {
    for (const allowManualCard of [false, true]) {
      const bootstrap = await createPaymentProviderRuntimeBootstrap({
        transport, voucherProtectedTokens: voucherTokens,
        paymentMethods: { useManualCard: true, giftCardEnabled },
        allowManualCard,
        extra: { provider: "square", square: { environment: "Sandbox", deviceId: "SQ", locationId: "LOC" }, linkly: { environment: "Sandbox" } },
      });
      assert.equal(bootstrap.providers.getAvailability("square").available, false);
      assert.equal(bootstrap.providers.getAvailability("linkly-cloud").available, false);
      assert.equal(bootstrap.providers.getAvailability("manual-card").available, allowManualCard);
      assert.equal(bootstrap.providers.getAvailability("voucher").available, giftCardEnabled);
      assert.equal(bootstrap.providers.get("square").provider, "square");
    }
  }
});

test("provider bootstrap 对缺失公开配置返回稳定 unavailable，不触碰网络", async () => {
  const bootstrap = await createPaymentProviderRuntimeBootstrap({
    transport,
    extra: undefined,
    voucherProtectedTokens: voucherTokens,
  });

  assert.deepEqual(bootstrap.providers.listAvailability(), [
    {
      provider: "square",
      available: false,
      blocker: "SQUARE_CONFIGURATION_MISSING",
    },
    {
      provider: "linkly-cloud",
      available: false,
      blocker: "LINKLY_CONFIGURATION_MISSING",
    },
    {
      provider: "voucher",
      available: false,
      blocker: "VOUCHER_CONFIGURATION_DISABLED",
    },
    { provider: "manual-card", available: false, blocker: "MANUAL_CARD_CONFIGURATION_DISABLED" },
    // Zeller 适配器总是注册着（供恢复已有交易），但设置里没有选中它，新付款不可用。
    { provider: "zeller", available: false, blocker: "PAYMENT_PROVIDER_UNKNOWN" },
  ]);
  assert.equal(
    bootstrap.createLinklyOperator({
      attempts: {} as never,
      acknowledgements: {} as never,
      trustedSession: {} as never,
      permissions: {} as never,
    }),
    null,
  );
  // 开关只控制新支付，已收购物券仍须保留恢复与撤销能力。
  assert.equal(bootstrap.voucherApprovedPurchaseRelease.status, "available");
  assert.equal(bootstrap.providers.get("voucher").provider, "voucher");
  assert.equal(bootstrap.providers.get("manual-card").provider, "manual-card");
});

test("bootstrap 仅在合法且可用的 Linkly 环境创建 operator，并一次性绑定 Voucher 上下文", async () => {
  const bootstrap = await createPaymentProviderRuntimeBootstrap({
    paymentMethods: { useManualCard: false, giftCardEnabled: true },
    transport,
    extra: {
      provider: "square",
      square: {
        environment: "Sandbox",
        deviceId: "square-device",
        locationId: "square-location",
      },
      linkly: { environment: "Sandbox" },
      voucher: { enabled: true },
    },
    voucherProtectedTokens: voucherTokens,
  });

  assert.deepEqual(
    bootstrap.providers
      .listAvailability()
      .map(({ provider, available }) => ({ provider, available })),
    [
      { provider: "square", available: true },
      { provider: "linkly-cloud", available: false },
      { provider: "voucher", available: true },
      { provider: "manual-card", available: false },
      { provider: "zeller", available: false },
    ],
  );
  assert.deepEqual(
    bootstrap.configurationAvailability
      .listAvailability()
      .map(({ provider, available }) => ({ provider, available })),
    [
      { provider: "square", available: true },
      { provider: "linkly-cloud", available: true },
      { provider: "voucher", available: true },
      { provider: "manual-card", available: true },
      // 配置层面 Zeller 恒为可用（它没有要读取的公开配置）；是否放行新付款由上面的通道选择决定。
      { provider: "zeller", available: true },
    ],
  );
  assert.ok(
    bootstrap.createLinklyOperator({
      attempts: {} as never,
      acknowledgements: {} as never,
      trustedSession: {
        assertActive() {},
      },
      permissions: {
        assert() {},
      },
    }),
  );
  assert.ok(bootstrap.linklyPaymentSelection);
  assert.equal(
    bootstrap.linklyTerminals?.port,
    bootstrap.linklyPaymentSelection,
  );
  assert.equal(
    bootstrap.voucherApprovedPurchaseRelease.status,
    "available",
  );
  assert.deepEqual(
    Object.keys(bootstrap.voucherApprovedPurchaseRelease).sort(),
    ["release", "status"],
  );

  bootstrap.bindVoucherContextProvider(async () => ({
    storeCode: "S001",
    cashierId: "cashier-1",
    voucherCode: "private",
    refundReason: null,
  }));
  assert.throws(
    () =>
      bootstrap.bindVoucherContextProvider(async () => ({
        storeCode: "S001",
        cashierId: "cashier-1",
        voucherCode: "other",
        refundReason: null,
      })),
    /already bound/i,
  );
});

test("双卡终端均已配置但未显式选择时，新支付全部失败关闭，旧 provider 仍保留恢复能力", async () => {
  const bootstrap = await createPaymentProviderRuntimeBootstrap({
    transport,
    extra: {
      square: {
        environment: "Sandbox",
        deviceId: "square-device",
        locationId: "square-location",
      },
      linkly: { environment: "Sandbox" },
    },
    voucherProtectedTokens: voucherTokens,
  });

  assert.deepEqual(
    bootstrap.providers.listAvailability().slice(0, 2),
    [
      {
        provider: "square",
        available: false,
        blocker: "PAYMENT_PROVIDER_UNKNOWN",
      },
      {
        provider: "linkly-cloud",
        available: false,
        blocker: "PAYMENT_PROVIDER_UNKNOWN",
      },
    ],
  );
  assert.equal(
    bootstrap.configurationAvailability.getAvailability("square")
      .available,
    true,
  );
  assert.equal(
    bootstrap.configurationAvailability.getAvailability("linkly-cloud")
      .available,
    true,
  );
  assert.equal(bootstrap.providers.get("square").provider, "square");
  assert.equal(
    bootstrap.providers.get("linkly-cloud").provider,
    "linkly-cloud",
  );
});

test("设置里选中 Zeller：只有 Zeller 可以收新的刷卡，启动时就请求加载 Zeller SDK", async () => {
  let requested = 0;
  const bootstrap = await createPaymentProviderRuntimeBootstrap({
    paymentMethods: { useManualCard: false, giftCardEnabled: false },
    transport,
    extra: { provider: "zeller", linkly: { environment: "Sandbox" } },
    voucherProtectedTokens: voucherTokens,
    zellerTerminals: { current: () => null, request: () => { requested += 1; } },
  });

  assert.equal(requested, 1);
  assert.deepEqual(bootstrap.providers.getAvailability("zeller"), {
    provider: "zeller",
    available: true,
    blocker: null,
  });
  // 同一时间只有一家刷卡通道：Linkly 即使配置完好也不接新付款。
  assert.deepEqual(bootstrap.providers.getAvailability("linkly-cloud"), {
    provider: "linkly-cloud",
    available: false,
    blocker: "PAYMENT_PROVIDER_UNKNOWN",
  });
  // 旧付款的恢复不受选择影响：原通道的适配器始终取得到。
  assert.equal(bootstrap.providers.get("linkly-cloud").provider, "linkly-cloud");
  assert.equal(bootstrap.providers.get("zeller").provider, "zeller");
});

test("开启手动刷卡后 Zeller 与其他刷卡机一样让位；没选中 Zeller 时不请求加载 SDK", async () => {
  let requested = 0;
  const terminals = { current: () => null, request: () => { requested += 1; } };
  const manual = await createPaymentProviderRuntimeBootstrap({
    paymentMethods: { useManualCard: true, giftCardEnabled: false },
    transport,
    extra: { provider: "zeller" },
    voucherProtectedTokens: voucherTokens,
    zellerTerminals: terminals,
  });
  assert.equal(manual.providers.getAvailability("zeller").available, false);
  assert.equal(manual.providers.getAvailability("manual-card").available, true);

  requested = 0;
  const square = await createPaymentProviderRuntimeBootstrap({
    transport,
    extra: {
      provider: "square",
      square: { environment: "Sandbox", deviceId: "square-device", locationId: "square-location" },
    },
    voucherProtectedTokens: voucherTokens,
    zellerTerminals: terminals,
  });
  assert.equal(requested, 0);
  assert.equal(square.providers.getAvailability("zeller").available, false);
});

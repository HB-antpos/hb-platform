import assert from "node:assert/strict";
import test from "node:test";

import type { VoucherProtectedTokenPort } from "../../features/payments/voucher";
import type { HbposTransport } from "../api";

import { createPaymentProviderRuntimeBootstrap } from "./payment-provider-runtime-bootstrap";

const transport = {} as HbposTransport;
const voucherTokens = {} as VoucherProtectedTokenPort;

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
    {
      provider: "manual-card",
      available: false,
      blocker: "MANUAL_CARD_CONFIGURATION_DISABLED",
    },
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
  assert.deepEqual(bootstrap.voucherApprovedPurchaseRelease, {
    status: "unavailable",
    reason: "VOUCHER_CONFIGURATION_DISABLED",
  });
});

test("bootstrap 仅在合法且可用的 Linkly 环境创建 operator，并一次性绑定 Voucher 上下文", async () => {
  const bootstrap = await createPaymentProviderRuntimeBootstrap({
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

test("手动刷卡保存后即时替代联机卡，关闭后仍保留原付款恢复通道", async () => {
  let methods = { useManualCard: false };
  const bootstrap = await createPaymentProviderRuntimeBootstrap({
    transport,
    extra: {
      provider: "square",
      square: { environment: "Sandbox", deviceId: "square-device", locationId: "square-location" },
      voucher: { enabled: true },
    },
    voucherProtectedTokens: voucherTokens,
    readPaymentMethods: () => methods,
  });
  assert.equal(bootstrap.providers.getAvailability("manual-card").available, false);
  assert.equal(bootstrap.providers.getAvailability("square").available, true);
  methods = { useManualCard: true };
  assert.equal(bootstrap.providers.getAvailability("manual-card").available, true);
  assert.equal(bootstrap.providers.getAvailability("square").available, false);
  assert.equal(bootstrap.providers.getAvailability("voucher").available, true);
  methods = { useManualCard: false };
  assert.equal(bootstrap.providers.getAvailability("manual-card").available, false);
  assert.equal(bootstrap.providers.getAvailability("square").available, true);
  assert.equal(bootstrap.providers.get("manual-card").provider, "manual-card");

  const installment = await createPaymentProviderRuntimeBootstrap({
    transport,
    extra: {
      provider: "square",
      square: { environment: "Sandbox", deviceId: "square-device", locationId: "square-location" },
    },
    voucherProtectedTokens: voucherTokens,
    paymentMethods: { useManualCard: true },
    allowManualCard: false,
  });
  assert.equal(installment.providers.getAvailability("manual-card").available, false);
  assert.equal(installment.providers.getAvailability("square").available, true);
});

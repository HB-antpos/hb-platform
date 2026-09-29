import assert from "node:assert/strict";
import test from "node:test";

import {
  PaymentAttemptService,
  type PaymentActionBinding,
  type PaymentActionBindingPort,
  type PaymentAttemptLedgerPort,
} from "@hb/pos-payments-core/features/payments/payment-attempt-service";

import {
  PaymentProviderUnavailableError,
  createConfiguredPaymentProviderRegistry,
  type LinklyRuntimeConfiguration,
  type LinklyRuntimeConfigurationPort,
  type SquareRuntimeConfiguration,
  type SquareRuntimeConfigurationPort,
  type VoucherRuntimeConfiguration,
  type VoucherRuntimeConfigurationPort,
} from "./payment-provider-registry";

import type {
  HbposTransport,
  HbposTransportRequest,
  HbposTransportResponse,
} from "@/core/api";
import type {
  PaymentAttempt,
  PaymentProviderReferences,
} from "@/core/contracts";
import type {
  VoucherProtectedAttemptState,
  VoucherProtectedAttemptStateDraft,
  VoucherProtectedTokenPort,
} from "@/features/payments/voucher/voucher-payment-adapter";

test("class 配置仓储保留 this 绑定，且只声明真实配置有效的 provider", async () => {
  const registry = await createConfiguredPaymentProviderRegistry({
    transport: new NeverTransport(),
    squareConfiguration: new ClassSquareConfiguration("device-1"),
    linklyConfiguration: new ClassLinklyConfiguration(null),
    voucherConfiguration: new ClassVoucherConfiguration(true),
    voucherProtectedTokens: new EmptyVoucherTokens(),
    voucherContextProvider: async () => ({
      storeCode: "S1",
      cashierId: "C1",
      voucherCode: "not-called",
      refundReason: null,
    }),
  });

  assert.deepEqual(registry.listAvailableProviders(), ["square", "voucher"]);
  assert.equal(registry.get("square").provider, "square");
  assert.equal(registry.get("square").providerEnvironment, "Sandbox");
  assert.equal(registry.get("voucher").provider, "voucher");
  assert.deepEqual(registry.getAvailability("linkly-cloud"), {
    provider: "linkly-cloud",
    available: false,
    blocker: "LINKLY_CONFIGURATION_MISSING",
  });
  assert.throws(
    () => registry.get("linkly-cloud"),
    (error: unknown) => {
      assert.ok(error instanceof PaymentProviderUnavailableError);
      assert.equal(error.code, "LINKLY_CONFIGURATION_MISSING");
      return true;
    },
  );
});

test("真实 Square registry 让 PaymentAttemptService 在首次提交前冻结并使用 Sandbox", async () => {
  const transport = new RecordingTransport([
    {
      status: 200,
      data: {
        success: true,
        data: {
          checkoutId: "checkout-service-1",
          environment: "Sandbox",
          status: "PENDING",
          paymentIds: [],
        },
      },
    },
  ]);
  const registry = await createConfiguredPaymentProviderRegistry({
    transport,
    squareConfiguration: new ClassSquareConfiguration("device-service-1"),
    linklyConfiguration: new ClassLinklyConfiguration(null),
    voucherConfiguration: new ClassVoucherConfiguration(false),
    voucherProtectedTokens: new EmptyVoucherTokens(),
    voucherContextProvider: async () => {
      throw new Error("not called");
    },
  });
  const ledger = new SingleAttemptLedger();
  const service = new PaymentAttemptService({
    ledger,
    actionBindings: new SingleActionBindings(),
    drafts: { assertPersisted: async () => undefined },
    providers: registry,
    connectivity: { isOnline: async () => true },
    createAttemptId: () => "attempt-service-1",
    createIdempotencyKey: () => "key-service-1",
    nowIso: () => "2026-09-13T00:00:00.000Z",
  });

  const result = await service.startAttempt({
    actionId: "action-service-1",
    orderGuid: "order-service-1",
    provider: "square",
    operation: "purchase",
    amount: { currency: "AUD", cents: 100 },
    actor: {
      cashierId: "cashier-1",
      cashierName: "Cashier One",
      userGuid: "user-1",
    },
  });

  assert.equal(result.attempt.providerEnvironment, "Sandbox");
  assert.equal(result.attempt.state, "Pending");
  assert.equal(
    (transport.calls[0]?.data as { environment?: string } | undefined)
      ?.environment,
    "Sandbox",
  );
});

test("缺失、非法、load 失败和运行时未知 provider 均 fail closed 为稳定 blocker", async () => {
  const registry = await createConfiguredPaymentProviderRegistry({
    transport: new NeverTransport(),
    squareConfiguration: {
      async load() {
        return {
          environment: "Sandbox",
          deviceId: " ",
          locationId: "location-1",
        };
      },
    },
    linklyConfiguration: {
      async load() {
        throw new Error("secure settings unavailable");
      },
    },
    voucherConfiguration: {
      async load() {
        return { enabled: false };
      },
    },
    voucherProtectedTokens: new EmptyVoucherTokens(),
    voucherContextProvider: async () => {
      throw new Error("not called");
    },
  });

  assert.deepEqual(
    registry.listAvailability().map((entry) => entry.blocker),
    [
      "SQUARE_CONFIGURATION_INVALID",
      "LINKLY_CONFIGURATION_LOAD_FAILED",
      "VOUCHER_CONFIGURATION_DISABLED",
      "MANUAL_CARD_CONFIGURATION_DISABLED",
    ],
  );
  assert.throws(
    () => registry.get("untrusted-provider" as "square"),
    (error: unknown) => {
      assert.ok(error instanceof PaymentProviderUnavailableError);
      assert.equal(error.code, "PAYMENT_PROVIDER_UNKNOWN");
      return true;
    },
  );
});

test("配置与 registry 的公开 JSON 不存在 secret/token/credential 字段", async () => {
  const registry = await createConfiguredPaymentProviderRegistry({
    transport: new NeverTransport(),
    squareConfiguration: new ClassSquareConfiguration("device-safe"),
    linklyConfiguration: new ClassLinklyConfiguration({
      environment: "Production",
    }),
    voucherConfiguration: new ClassVoucherConfiguration(true),
    voucherProtectedTokens: new EmptyVoucherTokens(),
    voucherContextProvider: async () => ({
      storeCode: "S1",
      cashierId: "C1",
      voucherCode: "internal-only",
      refundReason: null,
    }),
  });

  const json = JSON.stringify(registry.listAvailability()).toLowerCase();
  assert.equal(json.includes("secret"), false);
  assert.equal(json.includes("token"), false);
  assert.equal(json.includes("credential"), false);
  assert.equal(json.includes("internal-only"), false);
});

test("registry 只提供 Approved voucher purchase 的窄 release capability", async () => {
  const protectedState: VoucherProtectedAttemptState = {
    protectedReference: "vpr_registry_release",
    attemptId: "attempt-release",
    idempotencyKey: "idempotency-release",
    orderGuid: "order-release",
    operation: "purchase",
    phase: "approved",
    storeCode: "S1",
    cashierId: "C1",
    voucherCode: "VC-PRIVATE",
    reservationToken: "reservation-private",
    amountCents: 500,
    expiresAtIso: "2026-07-28T00:05:00.000Z",
    reason: null,
  };
  const tokens = new PresetVoucherTokens(protectedState);
  const transport = new RecordingTransport([
    {
      status: 200,
      data: {
        success: true,
        data: {
          voucherCode: "VC-PRIVATE",
          reservationToken: "reservation-private",
          released: true,
        },
      },
    },
  ]);
  const registry = await createConfiguredPaymentProviderRegistry({
    transport,
    squareConfiguration: new ClassSquareConfiguration("device-1"),
    linklyConfiguration: new ClassLinklyConfiguration(null),
    voucherConfiguration: new ClassVoucherConfiguration(true),
    voucherProtectedTokens: tokens,
    voucherContextProvider: async () => {
      throw new Error("not called");
    },
  });

  const releaseCapability =
    registry.getVoucherApprovedPurchaseReleasePort();
  assert.equal(releaseCapability.status, "available");
  if (releaseCapability.status !== "available") {
    assert.fail("voucher release capability should be available");
  }
  assert.deepEqual(
    Object.keys(releaseCapability).sort(),
    ["release", "status"],
  );

  for (const [invalid, expectedCode] of [
    [paymentAttempt({ provider: "square" }), "VOUCHER_PROVIDER_MISMATCH"],
    [
      paymentAttempt({ operation: "refund" }),
      "VOUCHER_PURCHASE_OPERATION_REQUIRED",
    ],
    [
      paymentAttempt({ state: "Unknown" }),
      "VOUCHER_APPROVED_ATTEMPT_REQUIRED",
    ],
  ] as const) {
    const rejected = await releaseCapability.release(invalid);
    assert.equal(rejected.state, "Unknown");
    assert.equal(rejected.responseCode, expectedCode);
  }
  assert.equal(transport.calls.length, 0);

  const released = await releaseCapability.release(paymentAttempt());
  assert.equal(released.state, "Cancelled");
  assert.equal(released.responseCode, "VOUCHER_RELEASED");
  assert.equal(transport.calls.length, 1);
  assert.equal(tokens.state.phase, "released");
});

test("registry 对不可用 Voucher 返回显式 unavailable，且没有 release 函数", async () => {
  const registry = await createConfiguredPaymentProviderRegistry({
    transport: new NeverTransport(),
    squareConfiguration: new ClassSquareConfiguration("device-1"),
    linklyConfiguration: new ClassLinklyConfiguration(null),
    voucherConfiguration: new ClassVoucherConfiguration(false),
    voucherProtectedTokens: new EmptyVoucherTokens(),
    voucherContextProvider: async () => {
      throw new Error("not called");
    },
  });

  const releaseCapability =
    registry.getVoucherApprovedPurchaseReleasePort();
  assert.deepEqual(releaseCapability, {
    status: "unavailable",
    reason: "VOUCHER_CONFIGURATION_DISABLED",
  });
  assert.equal("release" in releaseCapability, false);
});

class ClassSquareConfiguration implements SquareRuntimeConfigurationPort {
  private readonly environment = "Sandbox" as const;
  private readonly locationId = "location-1";

  public constructor(private readonly deviceId: string) {}

  public async load(): Promise<SquareRuntimeConfiguration> {
    return {
      environment: this.environment,
      deviceId: this.deviceId,
      locationId: this.locationId,
    };
  }
}

class ClassLinklyConfiguration implements LinklyRuntimeConfigurationPort {
  public constructor(
    private readonly configuration: LinklyRuntimeConfiguration | null,
  ) {}

  public async load(): Promise<LinklyRuntimeConfiguration | null> {
    return this.configuration;
  }
}

class ClassVoucherConfiguration implements VoucherRuntimeConfigurationPort {
  public constructor(private readonly enabled: boolean) {}

  public async load(): Promise<VoucherRuntimeConfiguration> {
    return { enabled: this.enabled };
  }
}

class NeverTransport implements HbposTransport {
  public async request<T>(
    _request: HbposTransportRequest,
  ): Promise<HbposTransportResponse<T>> {
    throw new Error("transport should not be called");
  }
}

class RecordingTransport implements HbposTransport {
  public readonly calls: HbposTransportRequest[] = [];

  public constructor(
    private readonly steps: HbposTransportResponse<unknown>[],
  ) {}

  public async request<T>(
    request: HbposTransportRequest,
  ): Promise<HbposTransportResponse<T>> {
    this.calls.push(request);
    const response = this.steps.shift();
    if (!response) throw new Error("Unexpected transport request");
    return response as HbposTransportResponse<T>;
  }
}

class EmptyVoucherTokens implements VoucherProtectedTokenPort {
  public async save(
    _state: VoucherProtectedAttemptStateDraft,
  ): Promise<string> {
    return "vpr-test";
  }

  public async getByAttempt(
    _attemptId: string,
  ): Promise<VoucherProtectedAttemptState | null> {
    return null;
  }

  public async resolve(
    _protectedReference: string,
  ): Promise<VoucherProtectedAttemptState | null> {
    return null;
  }
}

class SingleAttemptLedger implements PaymentAttemptLedgerPort {
  private current: PaymentAttempt | null = null;

  public async insertIfUnblocked(
    value: PaymentAttempt,
  ): Promise<PaymentAttempt | null> {
    if (this.current) return this.current;
    this.current = value;
    return null;
  }

  public async compareAndUpdate(
    expected: PaymentAttempt,
    next: PaymentAttempt,
  ): Promise<boolean> {
    if (
      !this.current ||
      this.current.attemptId !== expected.attemptId ||
      this.current.updatedAtIso !== expected.updatedAtIso
    ) {
      return false;
    }
    this.current = next;
    return true;
  }

  public async get(attemptId: string): Promise<PaymentAttempt | null> {
    return this.current?.attemptId === attemptId ? this.current : null;
  }

  public async findBlocking(orderGuid: string): Promise<PaymentAttempt | null> {
    return this.current?.orderGuid === orderGuid ? this.current : null;
  }

  public async verifyProviderEnvironment(): Promise<boolean> {
    return false;
  }
}

class SingleActionBindings implements PaymentActionBindingPort {
  private current: PaymentActionBinding | null = null;

  public async bindOrGet(
    proposed: PaymentActionBinding,
  ): Promise<PaymentActionBinding> {
    this.current ??= proposed;
    return this.current;
  }

  public async getByAttempt(
    attemptId: string,
  ): Promise<PaymentActionBinding | null> {
    return this.current?.attemptId === attemptId ? this.current : null;
  }
}

class PresetVoucherTokens implements VoucherProtectedTokenPort {
  public constructor(public state: VoucherProtectedAttemptState) {}

  public async save(
    state: VoucherProtectedAttemptStateDraft,
  ): Promise<string> {
    this.state = {
      ...state,
      protectedReference: this.state.protectedReference,
    };
    return this.state.protectedReference;
  }

  public async getByAttempt(
    attemptId: string,
  ): Promise<VoucherProtectedAttemptState | null> {
    return this.state.attemptId === attemptId ? this.state : null;
  }

  public async resolve(
    protectedReference: string,
  ): Promise<VoucherProtectedAttemptState | null> {
    return this.state.protectedReference === protectedReference
      ? this.state
      : null;
  }
}

function paymentAttempt(
  overrides: Partial<PaymentAttempt> = {},
): PaymentAttempt {
  return {
    attemptId: "attempt-release",
    idempotencyKey: "idempotency-release",
    orderGuid: "order-release",
    provider: "voucher",
    operation: "purchase",
    amount: { currency: "AUD", cents: 500 },
    state: "Approved",
    references: paymentReferences({
      voucherReservationToken: "vpr_registry_release",
    }),
    createdAtIso: "2026-07-28T00:00:00.000Z",
    updatedAtIso: "2026-07-28T00:00:00.001Z",
    lastErrorCode: null,
    ...overrides,
  };
}

function paymentReferences(
  overrides: Partial<PaymentProviderReferences> = {},
): PaymentProviderReferences {
  return {
    checkoutId: null,
    paymentId: null,
    sessionId: null,
    txnRef: null,
    rfn: null,
    voucherReservationToken: null,
    ...overrides,
  };
}

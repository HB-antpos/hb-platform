import assert from "node:assert/strict";
import test from "node:test";

import {
  VoucherHbposApi,
  VoucherPaymentAdapter,
  type VoucherPaymentContext,
  type VoucherProtectedAttemptState,
  type VoucherProtectedAttemptStateDraft,
  type VoucherProtectedTokenPort,
} from "./voucher-payment-adapter";

import {
  HbposApiError,
  type HbposEnvelope,
  type HbposTransport,
  type HbposTransportRequest,
  type HbposTransportResponse,
} from "@/core/api";
import type {
  PaymentAttempt,
  PaymentProviderReferences,
} from "@/core/contracts";

test("query 与 direct issue 只调用 Hbpos.Api 的现有路由和生成 DTO", async () => {
  const transport = new ScriptedTransport([
    ok(queryResponse({ voucherCode: "VC/100" })),
    ok({
      voucherCode: "VC-ISSUED",
      amount: 20,
      remainingAmount: 20,
      status: "1",
      expiredAt: "2027-07-28T00:00:00.000Z",
      storeCode: "S001",
      customerCode: "CUS-1",
    }),
  ]);
  const api = new VoucherHbposApi(transport);

  const queried = await api.query("S001", "VC/100");
  const issued = await api.issue({
    storeCode: "S001",
    amount: 20,
    cashierId: "C001",
    idempotencyKey: "issue-key-1",
    customerCode: "CUS-1",
    reason: "Manual",
  });

  assert.equal(queried.voucher?.voucherCode, "VC/100");
  assert.equal(issued.voucherCode, "VC-ISSUED");
  assert.deepEqual(transport.calls, [
    {
      method: "GET",
      url: "/api/v1/vouchers/VC%2F100",
      params: { storeCode: "S001" },
    },
    {
      method: "POST",
      url: "/api/v1/vouchers/issue",
      data: {
        storeCode: "S001",
        amount: 20,
        cashierId: "C001",
        idempotencyKey: "issue-key-1",
        customerCode: "CUS-1",
        reason: "Manual",
      },
    },
  ]);
});

test("purchase 查询余额后精确锁定，attempt 只得到受保护句柄而不是券码或 reservation token", async () => {
  const transport = new ScriptedTransport([
    ok(queryResponse()),
    ok({
      voucherCode: "VC100",
      lockedAmount: 12.5,
      reservationToken: "reservation-secret-1",
      expiresAt: "2026-07-28T00:05:00.000Z",
      remainingAmountAfterLock: 7.5,
    }),
  ]);
  const secrets = new MemoryProtectedTokenPort();
  const adapter = createAdapter(transport, secrets);

  const completed = await adapter.submit(attempt());

  assert.equal(completed.state, "Approved");
  assert.equal(
    completed.references.voucherReservationToken,
    "vpr_attempt_1",
  );
  assert.equal(
    completed.references.voucherReservationToken?.includes("VC100"),
    false,
  );
  assert.equal(
    completed.references.voucherReservationToken?.includes("reservation-secret-1"),
    false,
  );
  assert.deepEqual(transport.calls, [
    {
      method: "GET",
      url: "/api/v1/vouchers/VC100",
      params: { storeCode: "S001" },
    },
    {
      method: "POST",
      url: "/api/v1/vouchers/lock",
      data: {
        storeCode: "S001",
        voucherCode: "VC100",
        requestedAmount: 12.5,
      },
    },
  ]);
  assert.deepEqual(secrets.states.get("attempt-1"), {
    protectedReference: "vpr_attempt_1",
    attemptId: "attempt-1",
    idempotencyKey: "idempotency-1",
    orderGuid: "order-1",
    operation: "purchase",
    phase: "approved",
    storeCode: "S001",
    cashierId: "C001",
    voucherCode: "VC100",
    reservationToken: "reservation-secret-1",
    amountCents: 1_250,
    expiresAtIso: "2026-07-28T00:05:00.000Z",
    reason: null,
  });
});

test("余额不足或 lock 返回非精确金额时不把 attempt 误报 Approved", async () => {
  const insufficientTransport = new ScriptedTransport([
    ok(queryResponse({ remainingAmount: 10 })),
  ]);
  const insufficient = await createAdapter(
    insufficientTransport,
    new MemoryProtectedTokenPort(),
  ).submit(attempt());

  assert.equal(insufficient.state, "Declined");
  assert.equal(insufficient.responseCode, "VOUCHER_INSUFFICIENT_BALANCE");
  assert.equal(insufficientTransport.calls.length, 1);

  const mismatchTransport = new ScriptedTransport([
    ok(queryResponse()),
    ok({
      voucherCode: "VC100",
      lockedAmount: 12.49,
      reservationToken: "reservation-mismatch",
      expiresAt: "2026-07-28T00:05:00.000Z",
      remainingAmountAfterLock: 7.51,
    }),
  ]);
  const mismatch = await createAdapter(
    mismatchTransport,
    new MemoryProtectedTokenPort(),
  ).submit(attempt());

  assert.equal(mismatch.state, "Unknown");
  assert.equal(mismatch.responseCode, "VOUCHER_LOCK_AMOUNT_MISMATCH");
  assert.equal(mismatch.references.voucherReservationToken, null);
});

test("显式 release 校验受保护引用后调用后端；Unknown cancel 绝不自动 release", async () => {
  const secrets = new MemoryProtectedTokenPort();
  const protectedReference = await secrets.save(
    protectedState({
      phase: "approved",
      reservationToken: "reservation-release",
    }),
  );
  let phaseAtRequest: string | undefined;
  const calls: HbposTransportRequest[] = [];
  const transport: HbposTransport = {
    async request<T>(
      request: HbposTransportRequest,
    ): Promise<HbposTransportResponse<T>> {
      calls.push(request);
      phaseAtRequest = secrets.states.get("attempt-1")?.phase;
      return ok({
        voucherCode: "VC100",
        reservationToken: "reservation-release",
        released: true,
      }) as HbposTransportResponse<T>;
    },
  };
  const adapter = createAdapter(transport, secrets);
  const approvedAttempt = attempt({
    state: "Approved",
    references: references({ voucherReservationToken: protectedReference }),
  });

  const released = await adapter.releaseReservation(approvedAttempt);

  assert.equal(released.state, "Cancelled");
  assert.equal(phaseAtRequest, "release-submitted");
  assert.deepEqual(calls[0], {
    method: "POST",
    url: "/api/v1/vouchers/release",
    data: {
      storeCode: "S001",
      voucherCode: "VC100",
      reservationToken: "reservation-release",
    },
  });
  assert.equal(secrets.states.get("attempt-1")?.phase, "released");

  const callCount = calls.length;
  const unknown = await adapter.cancel({
    ...approvedAttempt,
    state: "Unknown",
  });
  assert.equal(unknown.state, "Unknown");
  assert.equal(unknown.responseCode, "VOUCHER_UNKNOWN_REQUIRES_RECOVERY");
  assert.equal(calls.length, callCount);
});

test("release-submitted 精确重放原受保护三元组，released 幂等收敛且不再访问网络", async () => {
  const secrets = new MemoryProtectedTokenPort();
  const protectedReference = await secrets.save(
    protectedState({
      phase: "release-submitted",
      storeCode: "S-REPLAY",
      voucherCode: "VC-REPLAY",
      reservationToken: "reservation-replay",
    }),
  );
  const transport = new ScriptedTransport([
    ok({
      voucherCode: "VC-REPLAY",
      reservationToken: "reservation-replay",
      released: true,
    }),
  ]);
  const adapter = createAdapter(transport, secrets);
  const approvedAttempt = attempt({
    state: "Approved",
    references: references({ voucherReservationToken: protectedReference }),
  });

  const replayed = await adapter.releaseReservation(approvedAttempt);
  const converged = await adapter.releaseReservation(approvedAttempt);

  assert.equal(replayed.state, "Cancelled");
  assert.equal(replayed.responseCode, "VOUCHER_RELEASED");
  assert.equal(converged.state, "Cancelled");
  assert.equal(converged.responseCode, "VOUCHER_RELEASED");
  assert.deepEqual(transport.calls, [
    {
      method: "POST",
      url: "/api/v1/vouchers/release",
      data: {
        storeCode: "S-REPLAY",
        voucherCode: "VC-REPLAY",
        reservationToken: "reservation-replay",
      },
    },
  ]);
  assert.equal(secrets.states.get("attempt-1")?.phase, "released");
});

test("专用 release 对其他 phase 或损坏绑定均 fail closed 且不访问网络", async () => {
  for (const phase of [
    "purchase-prepared",
    "lock-submitted",
    "refund-submitted",
  ] as const) {
    const secrets = new MemoryProtectedTokenPort();
    const protectedReference = await secrets.save(
      protectedState({
        phase,
        reservationToken: "reservation-not-releasable",
      }),
    );
    const transport = new ScriptedTransport([]);
    const result = await createAdapter(transport, secrets).releaseReservation(
      attempt({
        state: "Approved",
        references: references({
          voucherReservationToken: protectedReference,
        }),
      }),
    );

    assert.equal(result.state, "Unknown");
    assert.equal(result.responseCode, "VOUCHER_RESERVATION_REQUIRED");
    assert.equal(transport.calls.length, 0);
  }

  const damagedSecrets = new MemoryProtectedTokenPort();
  const reboundReference = await damagedSecrets.save(
    protectedState({
      attemptId: "attempt-other",
      reservationToken: "reservation-other",
    }),
  );
  const damagedTransport = new ScriptedTransport([]);
  const damaged = await createAdapter(
    damagedTransport,
    damagedSecrets,
  ).releaseReservation(
    attempt({
      state: "Approved",
      references: references({
        voucherReservationToken: reboundReference,
      }),
    }),
  );

  assert.equal(damaged.state, "Unknown");
  assert.equal(
    damaged.responseCode,
    "VOUCHER_PROTECTED_REFERENCE_CONFLICT",
  );
  assert.equal(damagedTransport.calls.length, 0);
});

test("release 返回 false 或响应丢失均保持 Unknown，recover 不自动再次 release", async () => {
  for (const releaseStep of [
    ok({
      voucherCode: "VC100",
      reservationToken: "reservation-release",
      released: false,
    }),
    transportFailure(),
  ]) {
    const secrets = new MemoryProtectedTokenPort();
    const protectedReference = await secrets.save(
      protectedState({
        phase: "approved",
        reservationToken: "reservation-release",
      }),
    );
    const transport = new ScriptedTransport([releaseStep]);
    const adapter = createAdapter(transport, secrets);
    const current = attempt({
      state: "Pending",
      references: references({ voucherReservationToken: protectedReference }),
    });

    const result = await adapter.cancel(current);
    const recovered = await adapter.recover({ ...current, state: "Unknown" });

    assert.equal(result.state, "Unknown");
    assert.equal(recovered.state, "Unknown");
    assert.equal(transport.calls.length, 1);
    assert.equal(secrets.states.get("attempt-1")?.phase, "release-submitted");
  }
});

test("transport、5xx 与响应缺失均保留 release-submitted，下一次专用调用精确重放", async () => {
  const failureSteps: readonly (
    | HbposTransportResponse<unknown>
    | Error
  )[] = [
    transportFailure(),
    {
      status: 503,
      data: { success: false, message: "temporarily unavailable" },
    },
    {
      status: 200,
      data: { success: true },
    },
  ];

  for (const failureStep of failureSteps) {
    const secrets = new MemoryProtectedTokenPort();
    const protectedReference = await secrets.save(
      protectedState({
        phase: "approved",
        reservationToken: "reservation-retry",
      }),
    );
    const transport = new ScriptedTransport([
      failureStep,
      ok({
        voucherCode: "VC100",
        reservationToken: "reservation-retry",
        released: true,
      }),
    ]);
    const adapter = createAdapter(transport, secrets);
    const approvedAttempt = attempt({
      state: "Approved",
      references: references({
        voucherReservationToken: protectedReference,
      }),
    });

    const first = await adapter.releaseReservation(approvedAttempt);
    assert.equal(first.state, "Unknown");
    assert.equal(
      secrets.states.get("attempt-1")?.phase,
      "release-submitted",
    );

    const replayed = await adapter.releaseReservation(approvedAttempt);
    assert.equal(replayed.state, "Cancelled");
    assert.equal(replayed.responseCode, "VOUCHER_RELEASED");
    assert.equal(transport.calls.length, 2);
    assert.deepEqual(transport.calls[0], transport.calls[1]);
  }
});

test("lock 响应丢失后重复 recover 保持 Unknown 且绝不再次 query/lock", async () => {
  const transport = new ScriptedTransport([
    ok(queryResponse()),
    transportFailure(),
  ]);
  const secrets = new MemoryProtectedTokenPort();
  const adapter = createAdapter(transport, secrets);

  const first = await adapter.submit(attempt());
  const firstRecovery = await adapter.recover(
    attempt({ state: "Unknown" }),
  );
  const secondRecovery = await adapter.recover(
    attempt({ state: "Unknown" }),
  );

  assert.equal(first.state, "Unknown");
  assert.equal(firstRecovery.state, "Unknown");
  assert.equal(secondRecovery.state, "Unknown");
  assert.equal(first.responseCode, "VOUCHER_TRANSPORT_ERROR");
  assert.equal(firstRecovery.responseCode, "VOUCHER_LOCK_RESULT_UNRESOLVED");
  assert.equal(transport.calls.length, 2);
  assert.equal(secrets.states.get("attempt-1")?.phase, "lock-submitted");
});

test("lock 已写受保护 Port 但 attempt 尚未 CAS 时，recover 复用原订单并恢复 Approved", async () => {
  const secrets = new MemoryProtectedTokenPort();
  await secrets.save(
    protectedState({
      phase: "approved",
      reservationToken: "reservation-after-crash",
    }),
  );
  const transport = new ScriptedTransport([]);
  const adapter = createAdapter(transport, secrets);

  const recovered = await adapter.recover(
    attempt({
      state: "Unknown",
      references: references(),
    }),
  );

  assert.equal(recovered.state, "Approved");
  assert.equal(recovered.references.voucherReservationToken, "vpr_attempt_1");
  assert.equal(transport.calls.length, 0);
});

test("refund 响应丢失后只用同一 attempt 幂等键重放，随后恢复不再发券", async () => {
  const transport = new ScriptedTransport([
    transportFailure(),
    ok({
      voucherCode: "RF100",
      amount: 12.5,
      remainingAmount: 12.5,
      status: "1",
      expiredAt: "2027-07-28T00:00:00.000Z",
    }),
  ]);
  const secrets = new MemoryProtectedTokenPort();
  const adapter = createAdapter(transport, secrets);
  const refundAttempt = attempt({
    operation: "refund",
    amount: { currency: "AUD", cents: -1_250 },
    state: "Submitted",
  });

  const first = await adapter.refund(refundAttempt);
  const recovered = await adapter.recover({
    ...refundAttempt,
    state: "Unknown",
  });
  const recoveredAgain = await adapter.recover({
    ...refundAttempt,
    state: "Approved",
    references: recovered.references,
  });

  assert.equal(first.state, "Unknown");
  assert.equal(recovered.state, "Approved");
  assert.equal(recoveredAgain.state, "Approved");
  assert.equal(transport.calls.length, 2);
  assert.deepEqual(transport.calls[0], transport.calls[1]);
  assert.deepEqual(transport.calls[0], {
    method: "POST",
    url: "/api/v1/vouchers/refund",
    data: {
      storeCode: "S001",
      amount: 12.5,
      cashierId: "C001",
      idempotencyKey: "idempotency-1",
      orderReference: "order-1",
      reason: "Refund",
    },
  });
  assert.equal(recovered.references.voucherReservationToken, "vpr_attempt_1");
  assert.equal(secrets.states.get("attempt-1")?.voucherCode, "RF100");
  assert.equal(secrets.states.get("attempt-1")?.amountCents, -1_250);
});

test("受保护引用换绑 attempt/order 或混入其他 provider 引用时保持 Unknown 且不调用 API", async () => {
  const secrets = new MemoryProtectedTokenPort();
  const protectedReference = await secrets.save(
    protectedState({
      attemptId: "attempt-other",
      orderGuid: "order-other",
      phase: "approved",
      reservationToken: "reservation-other",
    }),
  );
  const transport = new ScriptedTransport([]);
  const adapter = createAdapter(transport, secrets);

  const rebound = await adapter.recover(
    attempt({
      state: "Unknown",
      references: references({ voucherReservationToken: protectedReference }),
    }),
  );
  const mixed = await adapter.submit(
    attempt({
      references: references({
        checkoutId: "square-checkout",
      }),
    }),
  );

  assert.equal(rebound.state, "Unknown");
  assert.equal(rebound.responseCode, "VOUCHER_PROTECTED_REFERENCE_CONFLICT");
  assert.equal(mixed.state, "Unknown");
  assert.equal(mixed.responseCode, "VOUCHER_REFERENCE_CONFLICT");
  assert.equal(transport.calls.length, 0);
});

test("受保护状态绑定原 idempotencyKey，恢复时不得换键", async () => {
  const secrets = new MemoryProtectedTokenPort();
  const protectedReference = await secrets.save(
    protectedState({
      phase: "approved",
      reservationToken: "reservation-bound-key",
    }),
  );
  const transport = new ScriptedTransport([]);
  const adapter = createAdapter(transport, secrets);

  const recovered = await adapter.recover(
    attempt({
      state: "Unknown",
      idempotencyKey: "different-idempotency-key",
      references: references({
        voucherReservationToken: protectedReference,
      }),
    }),
  );

  assert.equal(recovered.state, "Unknown");
  assert.equal(
    recovered.responseCode,
    "VOUCHER_PROTECTED_REFERENCE_CONFLICT",
  );
  assert.equal(transport.calls.length, 0);
});

test("provider、AUD 整数分、正金额、OrderGuid 和幂等键均在调用前验证", async () => {
  const invalidAttempts: PaymentAttempt[] = [
    attempt({ provider: "square" }),
    attempt({ amount: { currency: "USD" as "AUD", cents: 1_250 } }),
    attempt({ amount: { currency: "AUD", cents: 12.5 } }),
    attempt({ amount: { currency: "AUD", cents: 0 } }),
    attempt({ orderGuid: " " }),
    attempt({ idempotencyKey: " " }),
  ];

  for (const invalid of invalidAttempts) {
    const transport = new ScriptedTransport([]);
    const result = await createAdapter(
      transport,
      new MemoryProtectedTokenPort(),
    ).submit(invalid);
    assert.equal(result.state, "Unknown");
    assert.equal(transport.calls.length, 0);
  }
});

test("refund 零、正数和 MIN_SAFE 金额均在 Voucher 请求及受保护状态写入前 fail closed", async () => {
  for (const cents of [0, 1_250, Number.MIN_SAFE_INTEGER]) {
    const transport = new ScriptedTransport([]);
    const secrets = new MemoryProtectedTokenPort();
    const result = await createAdapter(transport, secrets).refund(
      attempt({
        operation: "refund",
        amount: { currency: "AUD", cents },
      }),
    );

    assert.equal(result.state, "Unknown");
    assert.equal(result.responseCode, "VOUCHER_AMOUNT_INVALID");
    assert.equal(transport.calls.length, 0);
    assert.equal(secrets.states.size, 0);
  }
});

// 服务端（ASP.NET Core + System.Text.Json 默认序列化 DateTimeOffset）真实会返回 "+00:00" 形态，
// 而存储层只接受 toISOString() 规范形态，所以适配器必须在落库前规范化。
const EXPIRY_WIRE_FORMS: readonly (readonly [label: string, wire: string, canonical: string])[] = [
  ["服务端真实形态（无毫秒 +00:00）", "2027-01-05T12:59:59+00:00", "2027-01-05T12:59:59.000Z"],
  ["带毫秒 +00:00", "2027-01-05T12:59:59.123+00:00", "2027-01-05T12:59:59.123Z"],
  ["Z 无毫秒", "2027-01-05T12:59:59Z", "2027-01-05T12:59:59.000Z"],
  ["门店偏移 +11:00", "2027-01-05T23:59:59+11:00", "2027-01-05T12:59:59.000Z"],
  ["已是规范形态（输出不变）", "2027-01-05T12:59:59.000Z", "2027-01-05T12:59:59.000Z"],
];

test("refund 响应的 expiredAt 无论哪种线上形态，落库状态里的 expiresAtIso 都是规范形态", async () => {
  for (const [label, wire, canonical] of EXPIRY_WIRE_FORMS) {
    const transport = new ScriptedTransport([
      ok({
        voucherCode: "RF100",
        amount: 12.5,
        remainingAmount: 12.5,
        status: "1",
        expiredAt: wire,
      }),
    ]);
    const secrets = new MemoryProtectedTokenPort();
    const adapter = createAdapter(transport, secrets);

    const result = await adapter.refund(
      attempt({
        operation: "refund",
        amount: { currency: "AUD", cents: -1_250 },
        state: "Submitted",
      }),
    );

    assert.equal(result.state, "Approved", label);
    const saved = secrets.states.get("attempt-1");
    assert.equal(saved?.phase, "approved", label);
    assert.equal(saved?.voucherCode, "RF100", label);
    assert.equal(saved?.expiresAtIso, canonical, label);
  }
});

test("purchase 锁券响应的 expiresAt 同样规范化后落库（与 refund 共用同一校验）", async () => {
  for (const [label, wire, canonical] of EXPIRY_WIRE_FORMS) {
    const transport = new ScriptedTransport([
      ok(queryResponse()),
      ok({
        voucherCode: "VC100",
        lockedAmount: 12.5,
        reservationToken: "reservation-secret-1",
        expiresAt: wire,
        remainingAmountAfterLock: 7.5,
      }),
    ]);
    const secrets = new MemoryProtectedTokenPort();
    const adapter = createAdapter(transport, secrets);

    const result = await adapter.submit(attempt());

    assert.equal(result.state, "Approved", label);
    assert.equal(secrets.states.get("attempt-1")?.expiresAtIso, canonical, label);
  }
});

test("expiredAt/expiresAt 不是日期时保持原有错误码，不落 approved 状态", async () => {
  for (const bad of ["not-a-date", "", "2027-13-45T99:00:00Z"]) {
    const refundSecrets = new MemoryProtectedTokenPort();
    const refund = await createAdapter(
      new ScriptedTransport([
        ok({
          voucherCode: "RF100",
          amount: 12.5,
          remainingAmount: 12.5,
          status: "1",
          expiredAt: bad,
        }),
      ]),
      refundSecrets,
    ).refund(
      attempt({
        operation: "refund",
        amount: { currency: "AUD", cents: -1_250 },
        state: "Submitted",
      }),
    );
    assert.equal(refund.state, "Unknown", JSON.stringify(bad));
    assert.equal(refund.responseCode, "VOUCHER_REFUND_EXPIRY_INVALID");
    assert.equal(refundSecrets.states.get("attempt-1")?.phase, "refund-submitted");

    const lockSecrets = new MemoryProtectedTokenPort();
    const lock = await createAdapter(
      new ScriptedTransport([
        ok(queryResponse()),
        ok({
          voucherCode: "VC100",
          lockedAmount: 12.5,
          reservationToken: "reservation-secret-1",
          expiresAt: bad,
          remainingAmountAfterLock: 7.5,
        }),
      ]),
      lockSecrets,
    ).submit(attempt());
    assert.equal(lock.state, "Unknown", JSON.stringify(bad));
    assert.equal(lock.responseCode, "VOUCHER_LOCK_EXPIRY_INVALID");
  }
});

class ScriptedTransport implements HbposTransport {
  public readonly calls: HbposTransportRequest[] = [];

  public constructor(
    private readonly steps: (HbposTransportResponse<unknown> | Error)[],
  ) {}

  public async request<T>(
    request: HbposTransportRequest,
  ): Promise<HbposTransportResponse<T>> {
    this.calls.push(request);
    const step = this.steps.shift();
    if (!step) throw new Error("Unexpected transport request.");
    if (step instanceof Error) throw step;
    return step as HbposTransportResponse<T>;
  }
}

class MemoryProtectedTokenPort implements VoucherProtectedTokenPort {
  public readonly states = new Map<string, VoucherProtectedAttemptState>();
  private readonly references = new Map<string, VoucherProtectedAttemptState>();

  public async save(state: VoucherProtectedAttemptStateDraft): Promise<string> {
    // 与真实 SqliteVoucherProtectedTokenStore.validateDraft 一致：落库的到期时刻必须是
    // toISOString() 规范形态；否则存储层会抛 TypeError，已出券的退款卡在 refund-submitted。
    if (
      state.expiresAtIso !== null &&
      new Date(Date.parse(state.expiresAtIso)).toISOString() !== state.expiresAtIso
    ) {
      throw new TypeError("voucher expiry must be canonical ISO UTC.");
    }
    const existing = this.states.get(state.attemptId);
    const protectedReference =
      existing?.protectedReference ??
      `vpr_${state.attemptId.replaceAll("-", "_")}`;
    const saved = { ...state, protectedReference };
    this.states.set(state.attemptId, saved);
    this.references.set(protectedReference, saved);
    return protectedReference;
  }

  public async getByAttempt(
    attemptId: string,
  ): Promise<VoucherProtectedAttemptState | null> {
    return this.states.get(attemptId) ?? null;
  }

  public async resolve(
    protectedReference: string,
  ): Promise<VoucherProtectedAttemptState | null> {
    return this.references.get(protectedReference) ?? null;
  }
}

function createAdapter(
  transport: HbposTransport,
  secrets: VoucherProtectedTokenPort,
  contextOverrides: Partial<VoucherPaymentContext> = {},
): VoucherPaymentAdapter {
  return new VoucherPaymentAdapter(
    new VoucherHbposApi(transport),
    secrets,
    async () => ({
      storeCode: contextOverrides.storeCode ?? "S001",
      cashierId: contextOverrides.cashierId ?? "C001",
      voucherCode:
        contextOverrides.voucherCode === undefined
          ? "VC100"
          : contextOverrides.voucherCode,
      refundReason:
        contextOverrides.refundReason === undefined
          ? "Refund"
          : contextOverrides.refundReason,
    }),
  );
}

function ok<T>(
  data: T,
): HbposTransportResponse<HbposEnvelope<T>> {
  return {
    status: 200,
    data: { success: true, data },
  };
}

function transportFailure(): HbposApiError {
  return new HbposApiError("response lost", { kind: "transport" });
}

function queryResponse(
  overrides: Readonly<Record<string, unknown>> = {},
) {
  return {
    found: true,
    voucher: {
      voucherCode: "VC100",
      storeCode: "S001",
      voucherType: 3,
      amount: 20,
      remainingAmount: 20,
      status: "1",
      expiredAt: "2027-07-28T00:00:00.000Z",
      customerCode: null,
      discountRate: 0,
      remark: null,
      ...overrides,
    },
    message: null,
  };
}

function attempt(overrides: Partial<PaymentAttempt> = {}): PaymentAttempt {
  return {
    attemptId: "attempt-1",
    idempotencyKey: "idempotency-1",
    orderGuid: "order-1",
    provider: "voucher",
    operation: "purchase",
    amount: { currency: "AUD", cents: 1_250 },
    state: "Submitted",
    references: references(),
    createdAtIso: "2026-07-28T00:00:00.000Z",
    updatedAtIso: "2026-07-28T00:00:00.001Z",
    lastErrorCode: null,
    ...overrides,
  };
}

function references(
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

function protectedState(
  overrides: Partial<VoucherProtectedAttemptStateDraft> = {},
): VoucherProtectedAttemptStateDraft {
  return {
    attemptId: "attempt-1",
    idempotencyKey: "idempotency-1",
    orderGuid: "order-1",
    operation: "purchase",
    phase: "approved",
    storeCode: "S001",
    cashierId: "C001",
    voucherCode: "VC100",
    reservationToken: null,
    amountCents: 1_250,
    expiresAtIso: "2026-07-28T00:05:00.000Z",
    ...overrides,
  };
}

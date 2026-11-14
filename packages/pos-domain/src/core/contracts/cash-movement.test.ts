import assert from "node:assert/strict";
import test from "node:test";

import {
  CASH_MOVEMENT_MAXIMUM_AMOUNT_CENTS,
  CashMovementValidationError,
  buildCashMovement,
  canVoidCashMovement,
  cashMovementBusinessDate,
  formatCashMovementDisplayNumber,
  summarizeCashMovements,
  validateCashMovementInput,
  type BuildCashMovementInput,
  type CashMovementDirection,
  type CashMovementFact,
  type CashMovementReasonCode,
} from "./cash-movement";

const GUID_A = "11111111-1111-4111-8111-111111111111";
const GUID_B = "22222222-2222-4222-8222-222222222222";
const AUDIT_A = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

function buildInput(overrides: Partial<BuildCashMovementInput> = {}): BuildCashMovementInput {
  return {
    movementGuid: GUID_A,
    auditEventId: AUDIT_A,
    entry: { kind: "movement", reasonCode: "ChangeTopUp", amountCents: 5_000 },
    note: null,
    occurredAtIso: "2026-11-14T02:30:00.000Z",
    businessTimeZone: "Australia/Brisbane",
    storeCode: "S001",
    deviceCode: "IPAD-1",
    cashier: { cashierId: "c1", cashierName: "Alice", userGuid: null },
    ...overrides,
  };
}

function summarizable(
  reasonCode: CashMovementReasonCode,
  direction: CashMovementDirection,
  amountCents: number,
) {
  return { reasonCode, direction, amountCents };
}

// ---------- validateCashMovementInput ----------

test("金额边界：0 / 负数 / 小数分 / NaN 拒绝；1 分与 100,000,000 分通过；超限拒绝", () => {
  const ok = (amountCents: unknown) =>
    validateCashMovementInput({ reasonCode: "ChangeTopUp", amountCents });
  assert.deepEqual(ok(0), { ok: false, code: "AMOUNT_INVALID" });
  assert.deepEqual(ok(-1), { ok: false, code: "AMOUNT_INVALID" });
  assert.deepEqual(ok(1.5), { ok: false, code: "AMOUNT_INVALID" });
  assert.deepEqual(ok(Number.NaN), { ok: false, code: "AMOUNT_INVALID" });
  assert.deepEqual(ok("100"), { ok: false, code: "AMOUNT_INVALID" });
  assert.equal(ok(1).ok, true);
  assert.equal(ok(CASH_MOVEMENT_MAXIMUM_AMOUNT_CENTS).ok, true);
  assert.deepEqual(ok(CASH_MOVEMENT_MAXIMUM_AMOUNT_CENTS + 1), {
    ok: false,
    code: "AMOUNT_TOO_LARGE",
  });
});

test("备注边界：OtherIn / PaidOut 备注 1 字拒绝、2 字通过；trim 后计数；200 通过、201 拒绝且不截断", () => {
  for (const reasonCode of ["OtherIn", "PaidOut"] as const) {
    const check = (note: string | null) =>
      validateCashMovementInput({ reasonCode, amountCents: 100, note });
    assert.deepEqual(check(null), { ok: false, code: "NOTE_REQUIRED" });
    assert.deepEqual(check("   "), { ok: false, code: "NOTE_REQUIRED" });
    assert.deepEqual(check("a"), { ok: false, code: "NOTE_REQUIRED" });
    assert.deepEqual(check(" a "), { ok: false, code: "NOTE_REQUIRED" });
    const two = check(" ab ");
    assert.equal(two.ok && two.note, "ab");
    assert.equal(check("x".repeat(200)).ok, true);
    assert.deepEqual(check("x".repeat(201)), { ok: false, code: "NOTE_TOO_LONG" });
    // trim 之后 200 字通过（前后空白不计）。
    assert.equal(check(` ${"x".repeat(200)} `).ok, true);
  }
});

test("ChangeTopUp / SafeDrop 备注可选：空白备注归一为 null，201 字仍拒绝", () => {
  for (const reasonCode of ["ChangeTopUp", "SafeDrop"] as const) {
    const blank = validateCashMovementInput({ reasonCode, amountCents: 100, note: "  " });
    assert.equal(blank.ok && blank.note, null);
    assert.deepEqual(
      validateCashMovementInput({ reasonCode, amountCents: 100, note: "x".repeat(201) }),
      { ok: false, code: "NOTE_TOO_LONG" },
    );
  }
});

test("冲正（isVoid）备注界面要求至少 2 字，即使原因码本身备注可选", () => {
  assert.deepEqual(
    validateCashMovementInput({ reasonCode: "SafeDrop", amountCents: 100, note: "x", isVoid: true }),
    { ok: false, code: "NOTE_REQUIRED" },
  );
  assert.equal(
    validateCashMovementInput({ reasonCode: "SafeDrop", amountCents: 100, note: "xx", isVoid: true }).ok,
    true,
  );
});

test("原因码必须是四个之一（区分大小写）", () => {
  for (const reasonCode of ["changetopup", "Other", "", null, undefined, 1]) {
    assert.deepEqual(validateCashMovementInput({ reasonCode, amountCents: 100 }), {
      ok: false,
      code: "REASON_INVALID",
    });
  }
});

// ---------- buildCashMovement ----------

test("原因码自然方向：ChangeTopUp / OtherIn = In，SafeDrop / PaidOut = Out", () => {
  const direction = (reasonCode: string) =>
    buildCashMovement(
      buildInput({
        entry: { kind: "movement", reasonCode, amountCents: 100 },
        note: "memo",
      }),
    ).movement.direction;
  assert.equal(direction("ChangeTopUp"), "In");
  assert.equal(direction("OtherIn"), "In");
  assert.equal(direction("SafeDrop"), "Out");
  assert.equal(direction("PaidOut"), "Out");
});

test("冲正：原因码与金额同原记录、方向相反，voidsMovementGuid 指向原记录", () => {
  const commit = buildCashMovement(
    buildInput({
      movementGuid: GUID_B,
      note: "wrong amount",
      entry: {
        kind: "void",
        original: {
          movementGuid: GUID_A,
          reasonCode: "SafeDrop",
          amountCents: 12_345,
          direction: "Out",
          voidsMovementGuid: null,
        },
      },
    }),
  );
  assert.equal(commit.movement.direction, "In");
  assert.equal(commit.movement.reasonCode, "SafeDrop");
  assert.equal(commit.movement.amountCents, 12_345);
  assert.equal(commit.movement.voidsMovementGuid, GUID_A);
  assert.equal(commit.audit.eventType, "CASH_MOVEMENT_VOID");
  assert.equal(commit.audit.payload.amountDeltaCents, 12_345);
  assert.equal(commit.audit.payload.voidsMovementGuid, GUID_A);
});

test("冲正不能冲冲正、不能冲自己、冲正备注不足 2 字拒绝", () => {
  const original = {
    movementGuid: GUID_A,
    reasonCode: "PaidOut" as const,
    amountCents: 500,
    direction: "In" as const, // 冲正记录的方向
    voidsMovementGuid: GUID_B,
  };
  assert.throws(
    () =>
      buildCashMovement(buildInput({ movementGuid: GUID_B, note: "ok ok", entry: { kind: "void", original } })),
    (error) => error instanceof CashMovementValidationError && error.code === "VOID_TARGET_INVALID",
  );
  assert.throws(
    () =>
      buildCashMovement(
        buildInput({
          movementGuid: GUID_A,
          note: "ok ok",
          entry: {
            kind: "void",
            original: { ...original, voidsMovementGuid: null, direction: "Out" },
          },
        }),
      ),
    (error) => error instanceof CashMovementValidationError && error.code === "VOID_TARGET_INVALID",
  );
  assert.throws(
    () =>
      buildCashMovement(
        buildInput({
          movementGuid: GUID_B,
          note: "x",
          entry: {
            kind: "void",
            original: { ...original, voidsMovementGuid: null, direction: "Out" },
          },
        }),
      ),
    (error) => error instanceof CashMovementValidationError && error.code === "NOTE_REQUIRED",
  );
});

test("构造失败直接抛带 code 的错误：不截断备注、不修正金额", () => {
  const code = (overrides: Partial<BuildCashMovementInput>) => {
    try {
      buildCashMovement(buildInput(overrides));
      return "ok";
    } catch (error) {
      return error instanceof CashMovementValidationError ? error.code : "other";
    }
  };
  assert.equal(code({ note: "x".repeat(201) }), "NOTE_TOO_LONG");
  assert.equal(code({ entry: { kind: "movement", reasonCode: "ChangeTopUp", amountCents: 0 } }), "AMOUNT_INVALID");
  assert.equal(code({ entry: { kind: "movement", reasonCode: "PaidOut", amountCents: 100 }, note: "a" }), "NOTE_REQUIRED");
  assert.equal(code({ movementGuid: "not-a-guid" }), "MOVEMENT_GUID_INVALID");
  assert.equal(code({ auditEventId: "x" }), "AUDIT_EVENT_ID_INVALID");
  assert.equal(code({ occurredAtIso: "garbage" }), "OCCURRED_AT_INVALID");
  assert.equal(code({ businessTimeZone: "Not/AZone" }), "BUSINESS_TIME_ZONE_INVALID");
  assert.equal(code({ cashier: { cashierId: "x".repeat(65), cashierName: "A", userGuid: null } }), "CASHIER_INVALID");
  assert.equal(code({ cashier: { cashierId: "c", cashierName: "n".repeat(129), userGuid: null } }), "CASHIER_INVALID");
  assert.equal(code({ authorizedBy: { id: "m", name: "" } }), "AUTHORIZER_INVALID");
  assert.equal(code({ storeCode: " " }), "SCOPE_INVALID");
  assert.equal(code({}), "ok");
  // 边界：cashierId 64、cashierName 128 通过。
  assert.equal(
    code({ cashier: { cashierId: "c".repeat(64), cashierName: "n".repeat(128), userGuid: null } }),
    "ok",
  );
});

test("business_date 用门店营业时区而不是设备时区：UTC 14:30 在 Brisbane 已是次日", () => {
  // 2026-11-13T14:30Z = Brisbane 2026-11-14 00:30（UTC+10）。
  assert.equal(
    cashMovementBusinessDate("2026-11-13T14:30:00.000Z", "Australia/Brisbane"),
    "2026-11-14",
  );
  assert.equal(cashMovementBusinessDate("2026-11-13T14:30:00.000Z", "UTC"), "2026-11-13");
  assert.equal(
    cashMovementBusinessDate("2026-11-13T14:30:00.000Z", "America/Los_Angeles"),
    "2026-11-13",
  );
  const commit = buildCashMovement(
    buildInput({ occurredAtIso: "2026-11-13T14:30:00.000Z", businessTimeZone: "Australia/Brisbane" }),
  );
  assert.equal(commit.movement.businessDate, "2026-11-14");
  // 营业日与发生时刻 UTC 日期相差 1 天，在服务端 ±1 天容许范围内。
  const utcDay = Date.parse(`${commit.movement.occurredAtIso.slice(0, 10)}T00:00:00Z`);
  const businessDay = Date.parse(`${commit.movement.businessDate}T00:00:00Z`);
  assert.ok(Math.abs(businessDay - utcDay) <= 86_400_000);
});

test("审计事件：类型 CASH_IN / CASH_OUT，payload 带符号 amountDeltaCents，correlationId = movementGuid，orderGuid = null", () => {
  const cashIn = buildCashMovement(buildInput());
  assert.equal(cashIn.audit.eventType, "CASH_IN");
  assert.equal(cashIn.audit.orderGuid, null);
  assert.equal(cashIn.audit.correlationId, GUID_A);
  assert.equal(cashIn.audit.eventId, AUDIT_A);
  assert.equal(cashIn.audit.occurredAtIso, cashIn.movement.occurredAtIso);
  assert.deepEqual(
    { ...cashIn.audit.payload },
    {
      source: "cash-movement",
      action: "in",
      status: "completed",
      reason: "ChangeTopUp",
      paymentMethod: "Cash",
      amountDeltaCents: 5_000,
      requestingCashierId: "c1",
      requestingCashierName: "Alice",
      requestingUserGuid: null,
    },
  );
  const cashOut = buildCashMovement(
    buildInput({
      entry: { kind: "movement", reasonCode: "SafeDrop", amountCents: 7_000 },
      authorizedBy: { id: "m1", name: "Manager" },
      cashier: { cashierId: "c1", cashierName: "Alice", userGuid: GUID_B },
    }),
  );
  assert.equal(cashOut.audit.eventType, "CASH_OUT");
  assert.equal(cashOut.audit.payload.amountDeltaCents, -7_000);
  assert.equal(cashOut.audit.payload.authorizingCashierId, "m1");
  assert.equal(cashOut.audit.payload.requestingUserGuid, GUID_B);
  assert.equal(cashOut.movement.authorizedById, "m1");
  assert.equal(cashOut.movement.authorizedByName, "Manager");
});

test("occurredAt 规范化为 UTC ISO，备注 trim", () => {
  const commit = buildCashMovement(
    buildInput({ occurredAtIso: "2026-11-14T12:30:00+10:00", note: "  hello  " }),
  );
  assert.equal(commit.movement.occurredAtIso, "2026-11-14T02:30:00.000Z");
  assert.equal(commit.movement.note, "hello");
});

test("流水号 CM-0001 递增，超过 4 位自然扩展，≤ 32 字符；非法序号拒绝", () => {
  assert.equal(formatCashMovementDisplayNumber(1), "CM-0001");
  assert.equal(formatCashMovementDisplayNumber(9), "CM-0009");
  assert.equal(formatCashMovementDisplayNumber(10_000), "CM-10000");
  assert.ok(formatCashMovementDisplayNumber(Number.MAX_SAFE_INTEGER).length <= 32);
  assert.throws(() => formatCashMovementDisplayNumber(0));
  assert.throws(() => formatCashMovementDisplayNumber(1.5));
});

// ---------- summarizeCashMovements ----------

test("汇总：cashIn = ChangeTopUp + OtherIn，cashOut = SafeDrop + PaidOut，byReason 分别累加", () => {
  const summary = summarizeCashMovements([
    summarizable("ChangeTopUp", "In", 10_000),
    summarizable("OtherIn", "In", 2_500),
    summarizable("SafeDrop", "Out", 30_000),
    summarizable("PaidOut", "Out", 1_250),
    summarizable("ChangeTopUp", "In", 500),
  ]);
  assert.equal(summary.cashInCents, 13_000);
  assert.equal(summary.cashOutCents, 31_250);
  assert.deepEqual(
    { ...summary.byReason },
    { ChangeTopUp: 10_500, OtherIn: 2_500, SafeDrop: 30_000, PaidOut: 1_250 },
  );
});

test("汇总：一对「原记录 + 冲正」抵消为 0，只抵消对应原因码", () => {
  const summary = summarizeCashMovements([
    summarizable("SafeDrop", "Out", 5_000),
    summarizable("SafeDrop", "In", 5_000), // 冲正
    summarizable("PaidOut", "Out", 800),
    summarizable("ChangeTopUp", "In", 200),
    summarizable("ChangeTopUp", "Out", 200), // 冲正
  ]);
  assert.equal(summary.cashOutCents, 800);
  assert.equal(summary.cashInCents, 0);
  assert.equal(summary.byReason.SafeDrop, 0);
});

test("汇总：孤立冲正导致负数时失败关闭；空列表为 0；非法记录拒绝", () => {
  assert.throws(
    () => summarizeCashMovements([summarizable("SafeDrop", "In", 100)]),
    RangeError,
  );
  assert.deepEqual(
    { ...summarizeCashMovements([]) , byReason: undefined },
    { cashInCents: 0, cashOutCents: 0, byReason: undefined },
  );
  assert.throws(() => summarizeCashMovements([summarizable("SafeDrop", "Out", 0)]), TypeError);
  assert.throws(
    () => summarizeCashMovements([{ reasonCode: "X" as CashMovementReasonCode, direction: "In", amountCents: 1 }]),
    TypeError,
  );
});

// ---------- canVoidCashMovement ----------

const original: Pick<CashMovementFact, "voidsMovementGuid" | "businessDate" | "occurredAtIso"> = {
  voidsMovementGuid: null,
  businessDate: "2026-11-14",
  occurredAtIso: "2026-11-14T02:00:00.000Z",
};

test("冲正资格：全部满足才允许", () => {
  assert.deepEqual(
    canVoidCashMovement({
      movement: original,
      alreadyVoided: false,
      todayBusinessDate: "2026-11-14",
      latestDailyCloseSavedAtIso: null,
    }),
    { ok: true },
  );
  // 日结保存在原记录之前（更早的一次日结）不影响。
  assert.deepEqual(
    canVoidCashMovement({
      movement: original,
      alreadyVoided: false,
      todayBusinessDate: "2026-11-14",
      latestDailyCloseSavedAtIso: "2026-11-14T01:59:59.999Z",
    }),
    { ok: true },
  );
});

test("冲正资格：原记录是冲正 / 不是今天 / 已被冲正 / 之后保存过日结（含同一时刻）各自拒绝", () => {
  const base = {
    movement: original,
    alreadyVoided: false,
    todayBusinessDate: "2026-11-14",
    latestDailyCloseSavedAtIso: null,
  };
  assert.deepEqual(
    canVoidCashMovement({ ...base, movement: { ...original, voidsMovementGuid: GUID_A } }),
    { ok: false, reason: "IS_VOID_RECORD" },
  );
  assert.deepEqual(canVoidCashMovement({ ...base, todayBusinessDate: "2026-11-15" }), {
    ok: false,
    reason: "NOT_TODAY",
  });
  assert.deepEqual(canVoidCashMovement({ ...base, alreadyVoided: true }), {
    ok: false,
    reason: "ALREADY_VOIDED",
  });
  assert.deepEqual(
    canVoidCashMovement({ ...base, latestDailyCloseSavedAtIso: "2026-11-14T02:00:00.001Z" }),
    { ok: false, reason: "DAILY_CLOSE_SAVED" },
  );
  assert.deepEqual(
    canVoidCashMovement({ ...base, latestDailyCloseSavedAtIso: "2026-11-14T02:00:00.000Z" }),
    { ok: false, reason: "DAILY_CLOSE_SAVED" },
  );
  // 日结时间无法解析时失败关闭。
  assert.deepEqual(canVoidCashMovement({ ...base, latestDailyCloseSavedAtIso: "garbage" }), {
    ok: false,
    reason: "DAILY_CLOSE_SAVED",
  });
});

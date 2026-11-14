import assert from "node:assert/strict";
import test from "node:test";

import type {
  CashMovementSummary,
  LocalCashMovement,
} from "@hb/pos-domain/core/contracts/cash-movement";

import { CashMovementPresenter } from "./cash-movement-presenter";
import type {
  CashMovementReprintOutcome,
  CashMovementServicePort,
  CashMovementSubmitInput,
  CashMovementSubmitOutcome,
  CashMovementTodaySnapshot,
  CashMovementVoidInput,
  CashMovementVoidOutcome,
} from "./cash-movement-runtime";

const TODAY = "2026-11-14";

function movement(overrides: Partial<LocalCashMovement> = {}): LocalCashMovement {
  return {
    movementGuid: "00000000-0000-4000-8000-000000000001",
    direction: "In",
    reasonCode: "ChangeTopUp",
    amountCents: 5_000,
    note: null,
    voidsMovementGuid: null,
    businessDate: TODAY,
    occurredAtIso: "2026-11-14T02:00:00.000Z",
    cashierId: "c1",
    cashierName: "Cashier One",
    authorizedById: null,
    authorizedByName: null,
    storeCode: "S1",
    deviceCode: "D1",
    displayNumber: "CM-0001",
    voidedByMovementGuid: null,
    auditEventId: "00000000-0000-4000-8000-0000000000a1",
    uploadState: "pending",
    uploadAttempts: 0,
    uploadErrorCode: null,
    uploadedAtIso: null,
    createdAtIso: "2026-11-14T02:00:00.000Z",
    ...overrides,
  };
}

class FakeService implements CashMovementServicePort {
  public movements: LocalCashMovement[] = [];
  public latestDailyCloseSavedAtIso: string | null = null;
  public submitCalls: CashMovementSubmitInput[] = [];
  public voidCalls: CashMovementVoidInput[] = [];
  public reprintCalls: string[] = [];
  public submitOutcome: CashMovementSubmitOutcome | Error = {
    kind: "saved",
    movement: movement(),
    drawer: "ok",
    print: "ok",
  };
  public voidOutcome: CashMovementVoidOutcome | Error = {
    kind: "saved",
    movement: movement(),
    drawer: "ok",
    print: "ok",
  };
  public reprintOutcome: CashMovementReprintOutcome | Error = { kind: "printed" };
  public listError = false;
  public releaseSubmit: (() => void) | null = null;
  public holdSubmit = false;

  public async listToday(): Promise<CashMovementTodaySnapshot> {
    if (this.listError) throw new Error("list failed");
    return {
      businessDate: TODAY,
      businessTimeZone: "Australia/Brisbane",
      movements: this.movements,
      latestDailyCloseSavedAtIso: this.latestDailyCloseSavedAtIso,
    };
  }
  public async summary(): Promise<CashMovementSummary> {
    return {
      cashInCents: 0,
      cashOutCents: 0,
      byReason: { ChangeTopUp: 0, OtherIn: 0, SafeDrop: 0, PaidOut: 0 },
    };
  }
  public async submit(input: CashMovementSubmitInput) {
    this.submitCalls.push(input);
    if (this.holdSubmit) {
      await new Promise<void>((resolve) => {
        this.releaseSubmit = resolve;
      });
    }
    if (this.submitOutcome instanceof Error) throw this.submitOutcome;
    return this.submitOutcome;
  }
  public async void(input: CashMovementVoidInput) {
    this.voidCalls.push(input);
    if (this.voidOutcome instanceof Error) throw this.voidOutcome;
    return this.voidOutcome;
  }
  public async reprint(guid: string) {
    this.reprintCalls.push(guid);
    if (this.reprintOutcome instanceof Error) throw this.reprintOutcome;
    return this.reprintOutcome;
  }
}

function setup() {
  const service = new FakeService();
  const presenter = new CashMovementPresenter({ service });
  return { service, presenter };
}

function typeAmount(presenter: CashMovementPresenter, text: string): void {
  for (const char of text) {
    presenter.pressAmountKey(char as "1");
  }
}

test("默认：存入页签，第一项原因补零钱；取出页签默认交店长；切换会复位表单", () => {
  const { presenter } = setup();
  assert.equal(presenter.getState().tab, "in");
  assert.equal(presenter.getState().reasonCode, "ChangeTopUp");
  presenter.selectReason("OtherIn");
  typeAmount(presenter, "12.5");
  presenter.setNote("abc");
  presenter.setTab("out");
  const state = presenter.getState();
  assert.equal(state.reasonCode, "SafeDrop");
  assert.equal(state.amountText, "");
  assert.equal(state.note, "");
  // 取出页签不能选存入的原因。
  presenter.selectReason("OtherIn");
  assert.equal(presenter.getState().reasonCode, "SafeDrop");
  presenter.selectReason("PaidOut");
  assert.equal(presenter.getState().reasonCode, "PaidOut");
});

test("金额输入：小数位 / 9 位 / 快捷键；amountCents 与文本同步", () => {
  const { presenter } = setup();
  typeAmount(presenter, "12.345");
  assert.equal(presenter.getState().amountText, "12.34");
  assert.equal(presenter.getState().amountCents, 1234);
  presenter.pressAmountKey("clear");
  typeAmount(presenter, "1234567890");
  assert.equal(presenter.getState().amountText, "123456789");
  presenter.setQuickAmount(5_000);
  assert.equal(presenter.getState().amountText, "50");
  assert.equal(presenter.getState().amountCents, 5_000);
  // 不在快捷列表里的金额被忽略。
  presenter.setQuickAmount(1_234);
  assert.equal(presenter.getState().amountCents, 5_000);
});

test("校验：未输入金额 / 金额为 0 都提示金额无效，且不调用服务", async () => {
  const { presenter, service } = setup();
  await presenter.submit();
  assert.equal(presenter.getState().statusCode, "amount-invalid");
  typeAmount(presenter, "0.00");
  await presenter.submit();
  assert.equal(presenter.getState().statusCode, "amount-invalid");
  assert.equal(service.submitCalls.length, 0);
});

test("校验：金额超出上限", async () => {
  const { presenter, service } = setup();
  typeAmount(presenter, "9999999");
  await presenter.submit();
  assert.equal(presenter.getState().statusCode, "amount-too-large");
  assert.equal(service.submitCalls.length, 0);
});

test("校验：其他存入与小额现金支出必须写 >= 2 字备注（空白不算），补零钱 / 交店长不需要", async () => {
  const { presenter, service } = setup();
  presenter.selectReason("OtherIn");
  typeAmount(presenter, "10");
  await presenter.submit();
  assert.equal(presenter.getState().statusCode, "note-required");
  presenter.setNote(" a ");
  await presenter.submit();
  assert.equal(presenter.getState().statusCode, "note-required");
  assert.equal(service.submitCalls.length, 0);
  presenter.setNote("找零");
  await presenter.submit();
  assert.equal(service.submitCalls.length, 1);

  presenter.setTab("out");
  presenter.selectReason("PaidOut");
  typeAmount(presenter, "10");
  await presenter.submit();
  assert.equal(presenter.getState().statusCode, "note-required");
  presenter.selectReason("SafeDrop");
  await presenter.submit();
  assert.equal(service.submitCalls.length, 2);
  assert.equal(service.submitCalls[1]?.reasonCode, "SafeDrop");
});

test("备注超过 200 字：报错而不是静默截断", async () => {
  const { presenter, service } = setup();
  typeAmount(presenter, "10");
  presenter.setNote("x".repeat(201));
  assert.equal(presenter.getState().note.length, 201);
  await presenter.submit();
  assert.equal(presenter.getState().statusCode, "note-too-long");
  assert.equal(service.submitCalls.length, 0);
});

test("保存成功：清空表单、刷新记录并提示已保存；服务收到整数分金额与原样备注", async () => {
  const { presenter, service } = setup();
  service.movements = [movement()];
  typeAmount(presenter, "20.5");
  presenter.setNote("note");
  await presenter.submit();
  assert.deepEqual(service.submitCalls, [
    { reasonCode: "ChangeTopUp", amountCents: 2_050, note: "note" },
  ]);
  const state = presenter.getState();
  assert.equal(state.statusCode, "saved");
  assert.equal(state.amountText, "");
  assert.equal(state.note, "");
  assert.equal(state.records.length, 1);
  assert.equal(state.busy, false);
});

test("保存成功但开钱箱 / 打印失败：仍算记录成功，只改提示", async () => {
  for (const [drawer, print, expected] of [
    ["failed", "ok", "saved-drawer-failed"],
    ["ok", "failed", "saved-print-failed"],
    ["failed", "failed", "saved-drawer-print-failed"],
    ["skipped", "skipped", "saved"],
  ] as const) {
    const { presenter, service } = setup();
    service.submitOutcome = { kind: "saved", movement: movement(), drawer, print };
    typeAmount(presenter, "10");
    await presenter.submit();
    assert.equal(presenter.getState().statusCode, expected, `${drawer}/${print}`);
    // 记录已入库：表单已清空，不会让收银员误以为没存而重复提交。
    assert.equal(presenter.getState().amountText, "");
  }
});

test("保存成功后刷新记录失败，不覆盖已保存的提示", async () => {
  const { presenter, service } = setup();
  service.listError = true;
  typeAmount(presenter, "10");
  await presenter.submit();
  assert.equal(presenter.getState().statusCode, "saved");
});

test("授权被拒 / 取消 / 保存失败 / 服务抛错：表单保留，提示对应原因", async () => {
  const cases: readonly [CashMovementSubmitOutcome | Error, string][] = [
    [{ kind: "denied", reason: "denied" }, "authorization-denied"],
    [{ kind: "denied", reason: "cancelled" }, "authorization-cancelled"],
    [{ kind: "failed" }, "save-failed"],
    [new Error("boom"), "save-failed"],
    [{ kind: "invalid", code: "AMOUNT_TOO_LARGE" }, "amount-too-large"],
  ];
  for (const [outcome, expected] of cases) {
    const { presenter, service } = setup();
    service.submitOutcome = outcome;
    typeAmount(presenter, "10");
    await presenter.submit();
    assert.equal(presenter.getState().statusCode, expected);
    assert.equal(presenter.getState().amountText, "10", "失败时保留表单");
    assert.equal(presenter.getState().busy, false);
  }
});

test("重复点击防抖：在途期间再次提交拿到同一个 Promise，服务只被调用一次", async () => {
  const { presenter, service } = setup();
  service.holdSubmit = true;
  typeAmount(presenter, "10");
  const first = presenter.submit();
  const second = presenter.submit();
  const third = presenter.submit();
  assert.equal(first, second);
  assert.equal(second, third);
  assert.equal(presenter.getState().busy, true);
  // 在途期间表单被锁定：改金额 / 切页签都无效。
  presenter.pressAmountKey("5");
  presenter.setTab("out");
  assert.equal(presenter.getState().amountText, "10");
  assert.equal(presenter.getState().tab, "in");
  await new Promise((resolve) => setImmediate(resolve));
  service.releaseSubmit?.();
  await first;
  assert.equal(service.submitCalls.length, 1);
  assert.equal(presenter.getState().busy, false);
});

test("今日记录：最新在上，冲正资格与被拒横幅计数来自记录", async () => {
  const { presenter, service } = setup();
  const g = (n: number) => `00000000-0000-4000-8000-0000000001${String(n).padStart(2, "0")}`;
  const original = movement({ movementGuid: g(1), displayNumber: "CM-0001", voidedByMovementGuid: g(2) });
  const voidRecord = movement({
    movementGuid: g(2),
    displayNumber: "CM-0002",
    direction: "Out",
    voidsMovementGuid: g(1),
    occurredAtIso: "2026-11-14T03:00:00.000Z",
    uploadState: "rejected",
  });
  const plain = movement({
    movementGuid: g(3),
    displayNumber: "CM-0003",
    direction: "Out",
    reasonCode: "SafeDrop",
    occurredAtIso: "2026-11-14T04:00:00.000Z",
    uploadState: "rejected",
  });
  const yesterday = movement({
    movementGuid: g(4),
    displayNumber: "CM-0004",
    businessDate: "2026-11-13",
    occurredAtIso: "2026-11-13T04:00:00.000Z",
  });
  service.movements = [yesterday, original, voidRecord, plain];
  await presenter.load();
  const state = presenter.getState();
  assert.deepEqual(
    state.records.map((record) => record.movement.displayNumber),
    ["CM-0003", "CM-0002", "CM-0001", "CM-0004"],
  );
  assert.deepEqual(
    state.records.map((record) => record.kind),
    ["out", "void", "in", "in"],
  );
  const byNumber = Object.fromEntries(
    state.records.map((record) => [record.movement.displayNumber, record]),
  );
  assert.deepEqual(byNumber["CM-0003"]?.voidEligibility, { ok: true });
  assert.deepEqual(byNumber["CM-0002"]?.voidEligibility, { ok: false, reason: "IS_VOID_RECORD" });
  assert.deepEqual(byNumber["CM-0001"]?.voidEligibility, { ok: false, reason: "ALREADY_VOIDED" });
  assert.equal(byNumber["CM-0001"]?.isVoided, true);
  assert.deepEqual(byNumber["CM-0004"]?.voidEligibility, { ok: false, reason: "NOT_TODAY" });
  assert.equal(state.rejectedCount, 2);
  assert.equal(state.businessTimeZone, "Australia/Brisbane");
});

test("冲正资格：该记录之后保存过日结则不可冲正（之前保存的不影响）", async () => {
  const { presenter, service } = setup();
  service.movements = [movement({ occurredAtIso: "2026-11-14T02:00:00.000Z" })];
  service.latestDailyCloseSavedAtIso = "2026-11-14T08:00:00.000Z";
  await presenter.load();
  assert.deepEqual(presenter.getState().records[0]?.voidEligibility, {
    ok: false,
    reason: "DAILY_CLOSE_SAVED",
  });
  service.latestDailyCloseSavedAtIso = "2026-11-14T01:00:00.000Z";
  await presenter.load();
  assert.deepEqual(presenter.getState().records[0]?.voidEligibility, { ok: true });
});

test("冲正流程：不满足资格不能开始；备注 < 2 字拒绝；成功后收起表单并刷新", async () => {
  const { presenter, service } = setup();
  const eligible = movement();
  const closed = movement({
    movementGuid: "00000000-0000-4000-8000-000000000009",
    displayNumber: "CM-0009",
    businessDate: "2026-11-13",
  });
  service.movements = [closed, eligible];
  await presenter.load();
  presenter.beginVoid(closed.movementGuid);
  assert.equal(presenter.getState().voidDraft, null);
  presenter.beginVoid(eligible.movementGuid);
  assert.equal(presenter.getState().voidDraft?.movementGuid, eligible.movementGuid);

  presenter.setVoidNote(" x ");
  await presenter.confirmVoid();
  assert.equal(presenter.getState().statusCode, "void-note-required");
  assert.equal(service.voidCalls.length, 0);

  presenter.setVoidNote("输错金额");
  await presenter.confirmVoid();
  assert.deepEqual(service.voidCalls, [
    { movementGuid: eligible.movementGuid, note: "输错金额" },
  ]);
  assert.equal(presenter.getState().statusCode, "void-saved");
  assert.equal(presenter.getState().voidDraft, null);
});

test("冲正的各种失败结果：授权被拒、资格已变、保存失败、打印失败、开钱箱失败", async () => {
  const cases: readonly [CashMovementVoidOutcome | Error, string, boolean][] = [
    [{ kind: "denied", reason: "denied" }, "authorization-denied", true],
    [{ kind: "denied", reason: "cancelled" }, "authorization-cancelled", true],
    [{ kind: "not-allowed", reason: "DAILY_CLOSE_SAVED" }, "void-not-allowed", false],
    [{ kind: "failed" }, "void-failed", true],
    [new Error("boom"), "void-failed", true],
    [
      { kind: "saved", movement: movement(), drawer: "ok", print: "failed" },
      "void-saved-print-failed",
      false,
    ],
    [
      { kind: "saved", movement: movement(), drawer: "failed", print: "ok" },
      "void-saved-drawer-failed",
      false,
    ],
    [
      { kind: "saved", movement: movement(), drawer: "failed", print: "failed" },
      "void-saved-drawer-print-failed",
      false,
    ],
  ];
  for (const [outcome, expected, draftKept] of cases) {
    const { presenter, service } = setup();
    service.movements = [movement()];
    await presenter.load();
    presenter.beginVoid(movement().movementGuid);
    presenter.setVoidNote("输错金额");
    service.voidOutcome = outcome;
    await presenter.confirmVoid();
    assert.equal(presenter.getState().statusCode, expected);
    assert.equal(presenter.getState().voidDraft !== null, draftKept);
  }
});

test("补打：成功与失败只改提示；同一时刻只允许一个在途动作", async () => {
  const { presenter, service } = setup();
  const first = presenter.reprint("g1");
  const second = presenter.reprint("g2");
  assert.equal(first, second);
  await first;
  assert.deepEqual(service.reprintCalls, ["g1"]);
  assert.equal(presenter.getState().statusCode, "reprint-printed");

  service.reprintOutcome = { kind: "failed" };
  await presenter.reprint("g1");
  assert.equal(presenter.getState().statusCode, "reprint-failed");
  service.reprintOutcome = new Error("boom");
  await presenter.reprint("g1");
  assert.equal(presenter.getState().statusCode, "reprint-failed");
});

test("加载失败进入 failed 并提示；销毁后不再通知也不再改状态", async () => {
  const { presenter, service } = setup();
  service.listError = true;
  await presenter.load();
  assert.equal(presenter.getState().kind, "failed");
  assert.equal(presenter.getState().statusCode, "load-failed");

  let notified = 0;
  presenter.subscribe(() => {
    notified += 1;
  });
  presenter.destroy();
  presenter.pressAmountKey("1");
  await presenter.submit();
  assert.equal(notified, 0);
  assert.equal(presenter.getState().amountText, "");
  assert.equal(service.submitCalls.length, 0);
});

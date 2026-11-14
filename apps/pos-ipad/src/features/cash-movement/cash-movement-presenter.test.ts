import assert from "node:assert/strict";
import test from "node:test";

import type {
  CashMovementReasonCode,
  LocalCashMovement,
} from "@hb/pos-domain/core/contracts/cash-movement";

import {
  CASH_AMOUNT_MAXIMUM_DIGITS,
  CashMovementPresenter,
  applyCashAmountKey,
  parseCashAmountText,
  type CashAmountKey,
} from "./cash-movement-presenter";
import type {
  CashMovementReprintResult,
  CashMovementRuntime,
  CashMovementTodayView,
  CashMovementWriteResult,
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
    occurredAtIso: "2026-11-14T01:00:00.000Z",
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
    createdAtIso: "2026-11-14T01:00:00.000Z",
    ...overrides,
  };
}

/** 脚本化的运行时替身：记录每次调用，结果可逐次指定。 */
class FakeRuntime implements CashMovementRuntime {
  public movements: LocalCashMovement[] = [];
  public latestDailyCloseSavedAtIso: string | null = null;
  public submitCalls: Parameters<CashMovementRuntime["submit"]>[0][] = [];
  public voidCalls: Parameters<CashMovementRuntime["voidMovement"]>[0][] = [];
  public reprintCalls: string[] = [];
  public submitResult: () => Promise<CashMovementWriteResult> = async () => ({
    kind: "saved",
    movement: movement({ displayNumber: "CM-0009" }),
    drawer: "opened",
    print: "printed",
  });
  public voidResult: () => Promise<CashMovementWriteResult> = async () => ({
    kind: "saved",
    movement: movement({ displayNumber: "CM-0010", direction: "Out", voidsMovementGuid: "x" }),
    drawer: "opened",
    print: "printed",
  });
  public reprintResult: CashMovementReprintResult = { kind: "printed" };
  public listFails = false;

  public async listToday(): Promise<CashMovementTodayView> {
    if (this.listFails) throw new Error("list failed");
    return {
      businessDate: TODAY,
      businessTimeZone: "Australia/Brisbane",
      movements: this.movements,
      latestDailyCloseSavedAtIso: this.latestDailyCloseSavedAtIso,
      cashInCents: 0,
      cashOutCents: 0,
    };
  }
  public async submit(input: Parameters<CashMovementRuntime["submit"]>[0]) {
    this.submitCalls.push(input);
    return this.submitResult();
  }
  public async voidMovement(input: Parameters<CashMovementRuntime["voidMovement"]>[0]) {
    this.voidCalls.push(input);
    return this.voidResult();
  }
  public async reprint(movementGuid: string) {
    this.reprintCalls.push(movementGuid);
    return this.reprintResult;
  }
}

function setup() {
  const runtime = new FakeRuntime();
  let counter = 0;
  const ids: string[] = [];
  const presenter = new CashMovementPresenter({
    runtime,
    createId: () => {
      counter += 1;
      const id = `10000000-0000-4000-8000-${String(counter).padStart(12, "0")}`;
      ids.push(id);
      return id;
    },
  });
  return { runtime, presenter, ids };
}

function typeAmount(presenter: CashMovementPresenter, keys: readonly CashAmountKey[]): void {
  for (const key of keys) presenter.pressKey(key);
}

// ───────────────────────── 金额输入 ─────────────────────────

test("金额输入：最多 2 位小数、最多 9 位数字、前导 0 与小数点规则", () => {
  const press = (keys: readonly CashAmountKey[]) =>
    keys.reduce((text, key) => applyCashAmountKey(text, key), "");
  assert.equal(press(["1", "2", "decimal", "5"]), "12.5");
  // 第三位小数被忽略（不截断已有内容）。
  assert.equal(press(["1", "decimal", "2", "5", "9"]), "1.25");
  // 第二个小数点被忽略；以小数点开头自动补 0。
  assert.equal(press(["decimal", "5", "decimal"]), "0.5");
  // "0" 后再按数字是替换，不出现 "05"。
  assert.equal(press(["0", "5"]), "5");
  assert.equal(press(["0", "0"]), "0");
  // 9 位数字封顶：整数位与小数位合计。
  assert.equal(
    press(["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"]).length,
    CASH_AMOUNT_MAXIMUM_DIGITS,
  );
  assert.equal(press(["1", "2", "3", "4", "5", "6", "7", "decimal", "8", "9", "1"]), "1234567.89");
  assert.equal(press(["1", "2", "decimal", "3", "4"]), "12.34");
  assert.equal(press(["1", "2", "3", "backspace"]), "12");
  assert.equal(press(["1", "2", "3", "clear"]), "");
  assert.equal(applyCashAmountKey("", "backspace"), "");
});

test("金额文本换算整数分：不经浮点数，0.1 / 0.07 / 末尾小数点都精确", () => {
  assert.equal(parseCashAmountText("0.1"), 10);
  assert.equal(parseCashAmountText("0.07"), 7);
  assert.equal(parseCashAmountText("20."), 2_000);
  assert.equal(parseCashAmountText("19.99"), 1_999);
  assert.equal(parseCashAmountText(""), null);
  assert.equal(parseCashAmountText("."), null);
  assert.equal(parseCashAmountText("1.234"), null);
});

test("快捷金额直接替换当前金额；只接受 20 / 50 / 100 / 200 / 500", () => {
  const { presenter } = setup();
  typeAmount(presenter, ["7"]);
  presenter.quickAmount(100);
  assert.equal(presenter.getState().amountText, "100");
  presenter.quickAmount(37);
  assert.equal(presenter.getState().amountText, "100");
  for (const value of [20, 50, 200, 500]) {
    presenter.quickAmount(value);
    assert.equal(presenter.getState().amountText, String(value));
  }
});

// ───────────────────────── 表单校验 ─────────────────────────

test("默认存入页选中补零钱，取出页选中交店长；原因只能在本页范围内选", () => {
  const { presenter } = setup();
  assert.equal(presenter.getState().tab, "in");
  assert.equal(presenter.getState().reasonCode, "ChangeTopUp");
  assert.equal(presenter.selectReason("SafeDrop"), false);
  assert.equal(presenter.selectReason("OtherIn"), true);
  presenter.setTab("out");
  assert.equal(presenter.getState().reasonCode, "SafeDrop");
  assert.equal(presenter.selectReason("OtherIn"), false);
  assert.equal(presenter.selectReason("PaidOut"), true);
  // 切换页签清空金额与备注，不把存入的内容带到取出。
  presenter.setTab("in");
  typeAmount(presenter, ["5"]);
  presenter.setNote("abc");
  presenter.setTab("out");
  assert.equal(presenter.getState().amountText, "");
  assert.equal(presenter.getState().note, "");
});

test("金额为空 / 0 / 超限不放行，也不会调用运行时、不生成 guid", async () => {
  const { runtime, presenter, ids } = setup();
  await presenter.submit();
  assert.equal(presenter.getState().validationCode, "AMOUNT_INVALID");
  typeAmount(presenter, ["0"]);
  await presenter.submit();
  assert.equal(presenter.getState().validationCode, "AMOUNT_INVALID");
  presenter.pressKey("clear");
  typeAmount(presenter, ["decimal", "0", "0"]);
  await presenter.submit();
  assert.equal(presenter.getState().validationCode, "AMOUNT_INVALID");
  presenter.pressKey("clear");
  typeAmount(presenter, ["1", "0", "0", "0", "0", "0", "0", "decimal", "0", "1"]);
  await presenter.submit();
  assert.equal(presenter.getState().validationCode, "AMOUNT_TOO_LARGE");
  assert.equal(runtime.submitCalls.length, 0);
  assert.equal(ids.length, 0);
  // 恰好 1,000,000.00 放行。
  presenter.pressKey("clear");
  typeAmount(presenter, ["1", "0", "0", "0", "0", "0", "0"]);
  await presenter.submit();
  assert.equal(runtime.submitCalls[0]?.amountCents, 100_000_000);
});

test("其他存入 / 小额现金支出必须写备注（trim 后至少 2 字），补零钱 / 交店长不必", async () => {
  const { runtime, presenter } = setup();
  presenter.selectReason("OtherIn");
  typeAmount(presenter, ["5"]);
  await presenter.submit();
  assert.equal(presenter.getState().validationCode, "NOTE_REQUIRED");
  presenter.setNote(" a ");
  await presenter.submit();
  assert.equal(presenter.getState().validationCode, "NOTE_REQUIRED");
  assert.equal(runtime.submitCalls.length, 0);
  presenter.setNote("  ab  ");
  assert.equal(presenter.getState().validationCode, null, "编辑会清掉旧的校验提示");
  await presenter.submit();
  assert.equal(runtime.submitCalls.length, 1);
  assert.equal(runtime.submitCalls[0]?.note, "ab", "备注 trim 后提交");
  assert.equal(runtime.submitCalls[0]?.reasonCode, "OtherIn");

  presenter.setTab("out");
  presenter.selectReason("PaidOut");
  typeAmount(presenter, ["3"]);
  await presenter.submit();
  assert.equal(presenter.getState().validationCode, "NOTE_REQUIRED");

  presenter.selectReason("SafeDrop");
  await presenter.submit();
  assert.equal(runtime.submitCalls.length, 2);
  assert.equal(runtime.submitCalls[1]?.note, null);
});

test("备注限长 200：超长输入被忽略，不会静默截断后保存", async () => {
  const { runtime, presenter } = setup();
  const exact = "x".repeat(200);
  presenter.setNote(exact);
  assert.equal(presenter.getState().note, exact);
  presenter.setNote(`${exact}y`);
  assert.equal(presenter.getState().note, exact, "201 字的输入被整体忽略");
  typeAmount(presenter, ["5"]);
  await presenter.submit();
  assert.equal(runtime.submitCalls[0]?.note, exact);
  // 原来是短备注时，粘贴超长内容同样整体忽略（不是截成 200 字写进去）。
  presenter.setNote("abc");
  presenter.setNote("z".repeat(250));
  assert.equal(presenter.getState().note, "abc");
});

// ───────────────────────── 保存结果 ─────────────────────────

test("保存成功：清空表单、提示流水号并刷新今日记录；钱箱与凭条正常时没有警告", async () => {
  const { runtime, presenter } = setup();
  runtime.movements = [movement({ displayNumber: "CM-0009" })];
  typeAmount(presenter, ["2", "0"]);
  presenter.setNote("note");
  await presenter.submit();
  const state = presenter.getState();
  assert.equal(state.statusCode, "saved");
  assert.equal(state.statusNumber, "CM-0009");
  assert.deepEqual(state.hardwareWarnings, []);
  assert.equal(state.amountText, "");
  assert.equal(state.note, "");
  assert.equal(state.busy, false);
  assert.equal(state.records.length, 1, "保存后重新读取了今日记录");
});

test("保存成功但钱箱 / 打印失败：仍算记录成功，只追加提示，表单照常清空", async () => {
  for (const scenario of [
    { drawer: "failed", print: "printed", warnings: ["drawer"] },
    { drawer: "opened", print: "failed", warnings: ["print"] },
    { drawer: "failed", print: "failed", warnings: ["drawer", "print"] },
    // 打印机未启用同样按「没打印出来」提示；钱箱未配置不提示。
    { drawer: "skipped", print: "skipped", warnings: ["print"] },
  ] as const) {
    const { runtime, presenter } = setup();
    runtime.submitResult = async () => ({
      kind: "saved",
      movement: movement(),
      drawer: scenario.drawer,
      print: scenario.print,
    });
    typeAmount(presenter, ["5"]);
    await presenter.submit();
    const state = presenter.getState();
    assert.equal(state.statusCode, "saved", JSON.stringify(scenario));
    assert.deepEqual([...state.hardwareWarnings], scenario.warnings, JSON.stringify(scenario));
    assert.equal(state.amountText, "");
  }
});

test("授权被拒 / 取消：不记录、保留表单内容以便重试；落库失败与运行时抛错都按保存失败处理", async () => {
  const { runtime, presenter } = setup();
  typeAmount(presenter, ["5"]);
  presenter.setNote("keep");
  runtime.submitResult = async () => ({ kind: "denied", reason: "denied" });
  await presenter.submit();
  assert.equal(presenter.getState().statusCode, "denied");
  assert.equal(presenter.getState().amountText, "5");
  assert.equal(presenter.getState().note, "keep");

  runtime.submitResult = async () => ({ kind: "denied", reason: "cancelled" });
  await presenter.submit();
  assert.equal(presenter.getState().statusCode, "cancelled");

  runtime.submitResult = async () => ({ kind: "failed" });
  await presenter.submit();
  assert.equal(presenter.getState().statusCode, "save-failed");

  runtime.submitResult = async () => {
    throw new Error("boom");
  };
  await presenter.submit();
  assert.equal(presenter.getState().statusCode, "save-failed");
  assert.equal(presenter.getState().busy, false, "失败后不会卡在处理中");
  assert.equal(presenter.getState().amountText, "5");
});

test("重复点击防抖：授权等待期间连点只调用一次运行时，同一次提交只生成一个 guid", async () => {
  const { runtime, presenter, ids } = setup();
  let release: (value: CashMovementWriteResult) => void = () => undefined;
  runtime.submitResult = () =>
    new Promise<CashMovementWriteResult>((resolve) => {
      release = resolve;
    });
  typeAmount(presenter, ["5"]);
  const first = presenter.submit();
  const second = presenter.submit();
  const third = presenter.submit();
  assert.equal(presenter.getState().busy, true);
  // 处理中不接受任何编辑与切换。
  presenter.pressKey("9");
  presenter.setTab("out");
  presenter.setNote("zzz");
  assert.equal(presenter.getState().amountText, "5");
  assert.equal(presenter.getState().tab, "in");
  assert.equal(presenter.getState().note, "");
  release({ kind: "saved", movement: movement(), drawer: "opened", print: "printed" });
  await Promise.all([first, second, third]);
  assert.equal(runtime.submitCalls.length, 1);
  assert.equal(ids.length, 1, "只生成了一个 movementGuid");
  assert.equal(runtime.submitCalls[0]?.movementGuid, ids[0]);
  assert.equal(presenter.getState().busy, false);
  // 下一次（用户再点）才会生成新的 guid。
  typeAmount(presenter, ["6"]);
  runtime.submitResult = async () => ({ kind: "failed" });
  await presenter.submit();
  assert.equal(ids.length, 2);
  assert.notEqual(runtime.submitCalls[1]?.movementGuid, ids[0]);
});

// ───────────────────────── 今日记录 / 冲正资格 ─────────────────────────

test("今日记录最新在上；冲正记录符号与方向与原记录相反；已冲正的原记录带标记", async () => {
  const { runtime, presenter } = setup();
  const original = movement({
    movementGuid: "00000000-0000-4000-8000-0000000000b1",
    displayNumber: "CM-0001",
    direction: "Out",
    reasonCode: "SafeDrop",
    amountCents: 20_000,
    occurredAtIso: "2026-11-14T01:00:00.000Z",
    voidedByMovementGuid: "00000000-0000-4000-8000-0000000000b2",
  });
  const voidRecord = movement({
    movementGuid: "00000000-0000-4000-8000-0000000000b2",
    displayNumber: "CM-0002",
    direction: "In",
    reasonCode: "SafeDrop",
    amountCents: 20_000,
    voidsMovementGuid: original.movementGuid,
    occurredAtIso: "2026-11-14T01:05:00.000Z",
  });
  runtime.movements = [original, voidRecord];
  await presenter.load();
  const records = presenter.getState().records;
  assert.deepEqual(
    records.map((record) => record.movement.displayNumber),
    ["CM-0002", "CM-0001"],
  );
  assert.equal(records[0]?.kind, "void");
  assert.equal(records[0]?.signedCents, 20_000);
  assert.equal(records[1]?.kind, "out");
  assert.equal(records[1]?.signedCents, -20_000);
  assert.equal(records[1]?.voided, true);
});

test("冲正资格：冲正记录 / 已冲正 / 非今天 / 其后已日结 都不可冲正且给出原因，其余可冲正", async () => {
  const { runtime, presenter } = setup();
  const guid = (n: number) => `00000000-0000-4000-8000-0000000000c${n}`;
  runtime.latestDailyCloseSavedAtIso = "2026-11-14T03:00:00.000Z";
  runtime.movements = [
    movement({ movementGuid: guid(1), displayNumber: "CM-0001", occurredAtIso: "2026-11-14T01:00:00.000Z" }), // 其后已日结
    movement({ movementGuid: guid(2), displayNumber: "CM-0002", occurredAtIso: "2026-11-14T04:00:00.000Z" }), // 可冲正
    movement({ movementGuid: guid(3), displayNumber: "CM-0003", occurredAtIso: "2026-11-14T05:00:00.000Z", voidedByMovementGuid: guid(4) }),
    movement({
      movementGuid: guid(4),
      displayNumber: "CM-0004",
      direction: "Out",
      voidsMovementGuid: guid(3),
      occurredAtIso: "2026-11-14T05:10:00.000Z",
    }),
    movement({ movementGuid: guid(5), displayNumber: "CM-0005", businessDate: "2026-11-13", occurredAtIso: "2026-11-13T05:00:00.000Z" }),
  ];
  await presenter.load();
  const byNumber = new Map(
    presenter.getState().records.map((record) => [record.movement.displayNumber, record.voidEligibility]),
  );
  assert.deepEqual(byNumber.get("CM-0001"), { ok: false, reason: "DAILY_CLOSE_SAVED" });
  assert.deepEqual(byNumber.get("CM-0002"), { ok: true });
  assert.deepEqual(byNumber.get("CM-0003"), { ok: false, reason: "ALREADY_VOIDED" });
  assert.deepEqual(byNumber.get("CM-0004"), { ok: false, reason: "IS_VOID_RECORD" });
  assert.deepEqual(byNumber.get("CM-0005"), { ok: false, reason: "NOT_TODAY" });
  // 不满足资格的记录不能进入冲正流程。
  assert.equal(presenter.startVoid(guid(1)), false);
  assert.equal(presenter.getState().voidDraft, null);
  assert.equal(presenter.startVoid(guid(2)), true);
});

test("冲正：备注至少 2 字才提交；成功后关闭面板并刷新；传给运行时的是原记录 guid 与新 guid", async () => {
  const { runtime, presenter, ids } = setup();
  const original = movement({ movementGuid: "00000000-0000-4000-8000-0000000000d1" });
  runtime.movements = [original];
  await presenter.load();
  assert.equal(presenter.startVoid(original.movementGuid), true);
  await presenter.confirmVoid();
  assert.equal(presenter.getState().voidDraft?.validationCode, "VOID_NOTE_REQUIRED");
  presenter.setVoidNote(" x ");
  await presenter.confirmVoid();
  assert.equal(presenter.getState().voidDraft?.validationCode, "VOID_NOTE_REQUIRED");
  assert.equal(runtime.voidCalls.length, 0);
  assert.equal(ids.length, 0);

  presenter.setVoidNote("wrong amount");
  await presenter.confirmVoid();
  assert.equal(runtime.voidCalls.length, 1);
  assert.equal(runtime.voidCalls[0]?.originalMovementGuid, original.movementGuid);
  assert.equal(runtime.voidCalls[0]?.note, "wrong amount");
  assert.equal(runtime.voidCalls[0]?.movementGuid, ids[0]);
  assert.equal(presenter.getState().voidDraft, null);
  assert.equal(presenter.getState().statusCode, "void-saved");
});

test("冲正被拒（库内规则）：提示不可冲正、关闭面板并刷新列表；授权被拒保留面板", async () => {
  const { runtime, presenter } = setup();
  const original = movement({ movementGuid: "00000000-0000-4000-8000-0000000000e1" });
  runtime.movements = [original];
  await presenter.load();
  presenter.startVoid(original.movementGuid);
  presenter.setVoidNote("ok note");
  runtime.voidResult = async () => ({ kind: "denied", reason: "denied" });
  await presenter.confirmVoid();
  assert.equal(presenter.getState().statusCode, "denied");
  assert.notEqual(presenter.getState().voidDraft, null);

  runtime.voidResult = async () => ({ kind: "void-not-allowed", denial: "DAILY_CLOSE_SAVED" });
  await presenter.confirmVoid();
  assert.equal(presenter.getState().statusCode, "void-not-allowed");
  assert.equal(presenter.getState().voidDraft, null);
});

test("冲正重复点击防抖：同一次冲正只调用一次运行时", async () => {
  const { runtime, presenter, ids } = setup();
  const original = movement({ movementGuid: "00000000-0000-4000-8000-0000000000f1" });
  runtime.movements = [original];
  await presenter.load();
  presenter.startVoid(original.movementGuid);
  presenter.setVoidNote("dup tap");
  let release: (value: CashMovementWriteResult) => void = () => undefined;
  runtime.voidResult = () =>
    new Promise<CashMovementWriteResult>((resolve) => {
      release = resolve;
    });
  const tasks = [presenter.confirmVoid(), presenter.confirmVoid()];
  release({ kind: "saved", movement: movement(), drawer: "opened", print: "printed" });
  await Promise.all(tasks);
  assert.equal(runtime.voidCalls.length, 1);
  assert.equal(ids.length, 1);
});

// ───────────────────────── 被拒横幅 / 补打 / 读取失败 ─────────────────────────

test("被拒记录数进入状态（横幅与徽标用）；不被拒时为 0", async () => {
  const { runtime, presenter } = setup();
  runtime.movements = [
    movement({ movementGuid: "00000000-0000-4000-8000-000000000a01", uploadState: "rejected" }),
    movement({ movementGuid: "00000000-0000-4000-8000-000000000a02", uploadState: "rejected" }),
    movement({ movementGuid: "00000000-0000-4000-8000-000000000a03", uploadState: "synced" }),
  ];
  await presenter.load();
  assert.equal(presenter.getState().rejectedCount, 2);
  runtime.movements = [movement({ uploadState: "pending" })];
  await presenter.load();
  assert.equal(presenter.getState().rejectedCount, 0);
});

test("补打：成功提示已发送，失败提示失败；处理中重复点击只补打一次", async () => {
  const { runtime, presenter } = setup();
  await presenter.reprint("g1");
  assert.equal(presenter.getState().statusCode, "reprint-printed");
  runtime.reprintResult = { kind: "failed" };
  await presenter.reprint("g1");
  assert.equal(presenter.getState().statusCode, "reprint-failed");
  runtime.reprintResult = { kind: "not-found" };
  await presenter.reprint("g1");
  assert.equal(presenter.getState().statusCode, "reprint-failed");

  let release: () => void = () => undefined;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  const before = runtime.reprintCalls.length;
  runtime.reprint = async (guid: string) => {
    runtime.reprintCalls.push(guid);
    await gate;
    return { kind: "printed" };
  };
  const tasks = [presenter.reprint("g2"), presenter.reprint("g2")];
  release();
  await Promise.all(tasks);
  assert.equal(runtime.reprintCalls.length - before, 1);
});

test("今日记录读取失败进入 failed 状态，重新读取后恢复；销毁后不再更新", async () => {
  const { runtime, presenter } = setup();
  runtime.listFails = true;
  await presenter.load();
  assert.equal(presenter.getState().loadState, "failed");
  runtime.listFails = false;
  runtime.movements = [movement()];
  await presenter.load();
  assert.equal(presenter.getState().loadState, "ready");
  assert.equal(presenter.getState().records.length, 1);

  let notifications = 0;
  presenter.subscribe(() => {
    notifications += 1;
  });
  presenter.destroy();
  presenter.pressKey("5");
  await presenter.load();
  assert.equal(notifications, 0);
});

test("保存成功后刷新记录失败也不改写成保存失败", async () => {
  const { runtime, presenter } = setup();
  typeAmount(presenter, ["5"]);
  runtime.listFails = true;
  await presenter.submit();
  assert.equal(presenter.getState().statusCode, "saved");
  assert.equal(presenter.getState().loadState, "failed");
});

test("原因码列表对齐共享层：存入两项、取出两项", () => {
  const reasons: readonly CashMovementReasonCode[] = ["ChangeTopUp", "OtherIn", "SafeDrop", "PaidOut"];
  const { presenter } = setup();
  const seen = new Set<string>();
  for (const reason of reasons) {
    presenter.setTab(["ChangeTopUp", "OtherIn"].includes(reason) ? "in" : "out");
    if (presenter.selectReason(reason)) seen.add(reason);
  }
  assert.deepEqual([...seen].sort(), [...reasons].sort());
});

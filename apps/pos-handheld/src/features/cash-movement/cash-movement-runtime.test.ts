import assert from "node:assert/strict";
import test from "node:test";

import {
  CashMovementRepositoryError,
  type CashMovementCommit,
  type CashMovementRepositoryPort,
} from "@hb/pos-domain/core/contracts/cash-movement";

import { CashMovementPresenter } from "./cash-movement-presenter";
import {
  CASH_MOVEMENT_CASH_IN_PERMISSION,
  CASH_MOVEMENT_CASH_OUT_PERMISSION,
  CASH_MOVEMENT_VOID_PERMISSION,
  createCashMovementRuntimeService,
  type CashMovementAuthorizationPort,
  type CashMovementHardwarePort,
  type CashMovementSideEffectResult,
  type CashMovementTrustedSession,
} from "./cash-movement-runtime";
import {
  createMemoryCashMovementDatabase,
  type MemoryCashMovementDatabase,
} from "./cash-movement-test-support";

const NOW = "2026-11-14T02:30:00.000Z"; // 布里斯班 2026-11-14 12:30
const TZ = "Australia/Brisbane";

const ALL_PERMISSIONS = [
  CASH_MOVEMENT_CASH_IN_PERMISSION,
  CASH_MOVEMENT_CASH_OUT_PERMISSION,
  CASH_MOVEMENT_VOID_PERMISSION,
];

type Harness = Awaited<ReturnType<typeof createHarness>>;

async function createHarness(
  options: Readonly<{
    permissions?: readonly string[];
    authorization?: CashMovementAuthorizationPort | null;
    repository?: (real: MemoryCashMovementDatabase["repository"]) => CashMovementRepositoryPort;
    drawer?: () => Promise<CashMovementSideEffectResult> | CashMovementSideEffectResult;
    print?: () => Promise<CashMovementSideEffectResult> | CashMovementSideEffectResult;
  }> = {},
) {
  const database = await createMemoryCashMovementDatabase();
  const calls: string[] = [];
  const printed: Uint8Array[] = [];
  let id = 0;
  let nowIso = NOW;
  let session: CashMovementTrustedSession = {
    cashierId: "c1",
    cashierName: "Cashier One",
    userGuid: null,
    permissionCodes: options.permissions ?? ALL_PERMISSIONS,
    storeCode: "S1",
    deviceCode: "D1",
  };
  const hardware: CashMovementHardwarePort = {
    async openDrawer() {
      calls.push("drawer");
      return options.drawer ? options.drawer() : "ok";
    },
    async printSlip(_jobId, bytes) {
      calls.push("print");
      printed.push(bytes);
      return options.print ? options.print() : "ok";
    },
    async receiptContext() {
      return { paper: "80mm", brandName: "Hot Bargain", storeName: "Brisbane" };
    },
  };
  const real = database.repository;
  const repository = options.repository ? options.repository(real) : real;
  const service = createCashMovementRuntimeService({
    repository,
    authorization: options.authorization === undefined ? passThroughAuthorization(() => session) : options.authorization,
    session: () => session,
    businessTimeZone: TZ,
    createId: () => `00000000-0000-4000-8000-${String(++id).padStart(12, "0")}`,
    now: () => new Date(nowIso),
    requestDrain: () => {
      calls.push("drain");
    },
    hardware,
  });
  return {
    database,
    service,
    calls,
    printed,
    setSession(next: Partial<CashMovementTrustedSession>) {
      session = { ...session, ...next };
    },
    setNow(iso: string) {
      nowIso = iso;
    },
    idCount: () => id,
    text: (bytes: Uint8Array) => Buffer.from(bytes).toString("latin1"),
  };
}

/** 与真实 OperationAuthorizationService 同语义的替身：有权限直接执行，否则交给 supervisor 回调。 */
function passThroughAuthorization(
  getSession: () => CashMovementTrustedSession,
  supervisor?: Readonly<{
    cashierId: string;
    cashierName: string;
    outcome?: "authorized" | "cancelled" | "denied";
  }>,
  seen?: string[],
): CashMovementAuthorizationPort {
  return {
    async authorizeAndRun(request, operation) {
      seen?.push(`${request.permissionCode}|${request.action}|${request.screen}`);
      if (getSession().permissionCodes.includes(request.permissionCode)) {
        return { authorized: true, value: await operation({
          authorizationMode: "current-cashier",
          requestingCashierId: getSession().cashierId,
          authorizingCashierId: null,
          permissionCode: request.permissionCode,
        }) };
      }
      if (!supervisor || supervisor.outcome === "denied") {
        return { authorized: false, reason: "PERMISSION_DENIED" };
      }
      if (supervisor.outcome === "cancelled") {
        return { authorized: false, reason: "CANCELLED" };
      }
      return {
        authorized: true,
        value: await operation({
          authorizationMode: "online",
          requestingCashierId: getSession().cashierId,
          authorizingCashierId: supervisor.cashierId,
          permissionCode: request.permissionCode,
          authorizingActor: {
            cashierId: supervisor.cashierId,
            cashierName: supervisor.cashierName,
            userGuid: null,
          },
        }),
      };
    },
  };
}

async function rows(harness: Harness) {
  return harness.database.connection.getAll<{
    movement_guid: string;
    authorized_by_id: string | null;
    authorized_by_name: string | null;
    direction: string;
    reason_code: string;
    note: string | null;
    voids_movement_guid: string | null;
  }>("SELECT * FROM local_cash_movements ORDER BY rowid");
}

test("本人有权限：落库、授权人为空；顺序为 落库 → 唤醒上传 → 开钱箱 → 打印；凭条是存入凭条", async () => {
  const h = await createHarness();
  try {
    const outcome = await h.service.submit({ reasonCode: "ChangeTopUp", amountCents: 5_000, note: "" });
    assert.equal(outcome.kind, "saved");
    const stored = await rows(h);
    assert.equal(stored.length, 1);
    assert.equal(stored[0]?.authorized_by_id, null);
    assert.equal(stored[0]?.direction, "In");
    assert.deepEqual(h.calls, ["drain", "drawer", "print"]);
    const slip = h.text(h.printed[0]!);
    assert.match(slip, /CASH IN/u);
    assert.doesNotMatch(slip, /REPRINT/u);
    // 审计与记录同一事务：CASH_IN 审计已落库。
    const audit = await h.database.connection.getAll<{ event_type: string }>(
      "SELECT event_type FROM audit_events WHERE event_type = 'CASH_IN'",
    );
    assert.equal(audit.length, 1);
  } finally {
    await h.database.close();
  }
});

test("没有权限、主管扫码授权：授权人写入记录与列表；请求用对应权限码", async () => {
  const seen: string[] = [];
  const withSupervisor = await createHarness({
    permissions: [],
    authorization: passThroughAuthorization(
      () => ({
        cashierId: "c1",
        cashierName: "Cashier One",
        userGuid: null,
        permissionCodes: [],
        storeCode: "S1",
        deviceCode: "D1",
      }),
      { cashierId: "boss", cashierName: "Boss" },
      seen,
    ),
  });
  try {
    const outcome = await withSupervisor.service.submit({ reasonCode: "SafeDrop", amountCents: 10_000, note: "" });
    assert.equal(outcome.kind, "saved");
    const stored = await rows(withSupervisor);
    assert.equal(stored[0]?.authorized_by_id, "boss");
    assert.equal(stored[0]?.authorized_by_name, "Boss");
    assert.deepEqual(seen, [`${CASH_MOVEMENT_CASH_OUT_PERMISSION}|cash-out|cash-movement`]);
    const listed = await withSupervisor.service.listToday();
    assert.equal(listed.movements[0]?.authorizedByName, "Boss");
  } finally {
    await withSupervisor.database.close();
  }
});

test("存入用 CashIn 权限码，取出用 CashOut 权限码", async () => {
  const seen: string[] = [];
  const h = await createHarness({
    authorization: passThroughAuthorization(
      () => ({ cashierId: "c1", cashierName: "C", userGuid: null, permissionCodes: ALL_PERMISSIONS, storeCode: "S1", deviceCode: "D1" }),
      undefined,
      seen,
    ),
  });
  try {
    await h.service.submit({ reasonCode: "ChangeTopUp", amountCents: 100, note: "" });
    await h.service.submit({ reasonCode: "OtherIn", amountCents: 100, note: "找零" });
    await h.service.submit({ reasonCode: "SafeDrop", amountCents: 100, note: "" });
    await h.service.submit({ reasonCode: "PaidOut", amountCents: 100, note: "买胶带" });
    assert.deepEqual(seen.map((entry) => entry.split("|")[0]), [
      CASH_MOVEMENT_CASH_IN_PERMISSION,
      CASH_MOVEMENT_CASH_IN_PERMISSION,
      CASH_MOVEMENT_CASH_OUT_PERMISSION,
      CASH_MOVEMENT_CASH_OUT_PERMISSION,
    ]);
  } finally {
    await h.database.close();
  }
});

test("没有主管授权能力且本人没有权限：直接拒绝，不落库、不动硬件", async () => {
  const h = await createHarness({ permissions: [], authorization: null });
  try {
    const outcome = await h.service.submit({ reasonCode: "ChangeTopUp", amountCents: 5_000, note: "" });
    assert.deepEqual(outcome, { kind: "denied", reason: "denied" });
    assert.equal((await rows(h)).length, 0);
    assert.deepEqual(h.calls, []);
    // 没有授权能力但本人有权限：走当前收银员路径。
    h.setSession({ permissionCodes: ALL_PERMISSIONS });
    assert.equal((await h.service.submit({ reasonCode: "ChangeTopUp", amountCents: 5_000, note: "" })).kind, "saved");
  } finally {
    await h.database.close();
  }
});

test("主管授权被拒 / 取消：不落库、不动硬件、不唤醒上传", async () => {
  for (const outcome of ["denied", "cancelled"] as const) {
    const h = await createHarness({
      permissions: [],
      authorization: passThroughAuthorization(
        () => ({ cashierId: "c1", cashierName: "C", userGuid: null, permissionCodes: [], storeCode: "S1", deviceCode: "D1" }),
        { cashierId: "boss", cashierName: "Boss", outcome },
      ),
    });
    try {
      const result = await h.service.submit({ reasonCode: "ChangeTopUp", amountCents: 5_000, note: "" });
      assert.deepEqual(result, { kind: "denied", reason: outcome === "cancelled" ? "cancelled" : "denied" });
      assert.equal((await rows(h)).length, 0);
      assert.deepEqual(h.calls, []);
    } finally {
      await h.database.close();
    }
  }
});

test("校验在授权之前：备注必填被绕过的请求、金额 0 都被挡住，授权端口不会被调用", async () => {
  const seen: string[] = [];
  const h = await createHarness({
    authorization: passThroughAuthorization(
      () => ({ cashierId: "c1", cashierName: "C", userGuid: null, permissionCodes: ALL_PERMISSIONS, storeCode: "S1", deviceCode: "D1" }),
      undefined,
      seen,
    ),
  });
  try {
    assert.deepEqual(
      await h.service.submit({ reasonCode: "OtherIn", amountCents: 100, note: " " }),
      { kind: "invalid", code: "NOTE_REQUIRED" },
    );
    assert.deepEqual(
      await h.service.submit({ reasonCode: "PaidOut", amountCents: 100, note: "x" }),
      { kind: "invalid", code: "NOTE_REQUIRED" },
    );
    assert.deepEqual(
      await h.service.submit({ reasonCode: "ChangeTopUp", amountCents: 0, note: "" }),
      { kind: "invalid", code: "AMOUNT_INVALID" },
    );
    assert.deepEqual(
      await h.service.submit({ reasonCode: "ChangeTopUp", amountCents: 100_000_001, note: "" }),
      { kind: "invalid", code: "AMOUNT_TOO_LARGE" },
    );
    assert.deepEqual(
      await h.service.submit({ reasonCode: "Bogus", amountCents: 100, note: "" }),
      { kind: "invalid", code: "REASON_INVALID" },
    );
    assert.deepEqual(seen, []);
    assert.equal((await rows(h)).length, 0);
  } finally {
    await h.database.close();
  }
});

test("落库失败：不开钱箱、不打印、不唤醒上传", async () => {
  const h = await createHarness({
    repository: (real) => ({
      append: async () => {
        throw new Error("disk full");
      },
      getByGuid: (guid) => real.getByGuid(guid),
      listToday: (scope) => real.listToday(scope),
      summarizePeriod: (scope) => real.summarizePeriod(scope),
      latestDailyCloseSavedAtIso: (scope) => real.latestDailyCloseSavedAtIso(scope),
    }),
  });
  try {
    const outcome = await h.service.submit({ reasonCode: "ChangeTopUp", amountCents: 5_000, note: "" });
    assert.deepEqual(outcome, { kind: "failed" });
    assert.deepEqual(h.calls, []);
    assert.equal((await rows(h)).length, 0);
  } finally {
    await h.database.close();
  }
});

test("开钱箱失败 / 抛错：记录保留，仍继续打印", async () => {
  for (const drawer of [() => "failed" as const, () => { throw new Error("pulse"); }]) {
    const h = await createHarness({ drawer });
    try {
      const outcome = await h.service.submit({ reasonCode: "ChangeTopUp", amountCents: 5_000, note: "" });
      assert.equal(outcome.kind === "saved" && outcome.drawer, "failed");
      assert.equal(outcome.kind === "saved" && outcome.print, "ok");
      assert.equal((await rows(h)).length, 1);
      assert.deepEqual(h.calls, ["drain", "drawer", "print"]);
    } finally {
      await h.database.close();
    }
  }
});

test("打印失败 / 抛错：记录不回滚，其后仍可补打", async () => {
  for (const print of [() => "failed" as const, () => { throw new Error("paper out"); }]) {
    const h = await createHarness({ print });
    try {
      const outcome = await h.service.submit({ reasonCode: "ChangeTopUp", amountCents: 5_000, note: "" });
      assert.equal(outcome.kind === "saved" && outcome.print, "failed");
      assert.equal((await rows(h)).length, 1);
      assert.equal((await h.service.summary()).cashInCents, 5_000);
    } finally {
      await h.database.close();
    }
  }
});

test("钱箱 / 打印设置关闭（skipped）不算失败", async () => {
  const h = await createHarness({ drawer: () => "skipped", print: () => "skipped" });
  try {
    const outcome = await h.service.submit({ reasonCode: "ChangeTopUp", amountCents: 5_000, note: "" });
    assert.equal(outcome.kind === "saved" && outcome.drawer, "skipped");
    assert.equal(outcome.kind === "saved" && outcome.print, "skipped");
  } finally {
    await h.database.close();
  }
});

test("授权等待期间会话被替换：失败关闭，不落库", async () => {
  let swap: (() => void) | null = null;
  const h = await createHarness({
    authorization: {
      async authorizeAndRun(_request, operation) {
        swap?.();
        return { authorized: true, value: await operation({
          authorizationMode: "current-cashier",
          requestingCashierId: "c1",
          authorizingCashierId: null,
          permissionCode: "x",
        }) };
      },
    },
  });
  swap = () => h.setSession({ cashierId: "someone-else" });
  try {
    const outcome = await h.service.submit({ reasonCode: "ChangeTopUp", amountCents: 5_000, note: "" });
    assert.deepEqual(outcome, { kind: "failed" });
    assert.equal((await rows(h)).length, 0);
  } finally {
    await h.database.close();
  }
});

test("同一次提交只生成一个流水 guid：presenter 连点三次，库里只有一条记录", async () => {
  const h = await createHarness();
  try {
    const presenter = new CashMovementPresenter({ service: h.service });
    for (const key of ["5", "0"] as const) presenter.pressAmountKey(key);
    const first = presenter.submit();
    presenter.submit();
    presenter.submit();
    await first;
    assert.equal((await rows(h)).length, 1);
    // guid + auditEventId + 授权动作 id + 凭条任务 id，共 4 次，没有为连点多生成。
    assert.equal(h.idCount(), 4);
  } finally {
    await h.database.close();
  }
});

async function seed(h: Harness, reasonCode = "SafeDrop", amountCents = 7_000) {
  const outcome = await h.service.submit({ reasonCode, amountCents, note: reasonCode === "PaidOut" ? "买胶带" : "" });
  assert.equal(outcome.kind, "saved");
  if (outcome.kind !== "saved") throw new Error("seed failed");
  h.calls.length = 0;
  h.printed.length = 0;
  return outcome.movement;
}

test("冲正：原因码与金额取自原记录、方向相反；先开钱箱再打印冲正凭条（带原记录，与 WPF 一致）；原记录显示已冲正", async () => {
  const h = await createHarness();
  try {
    const original = await seed(h);
    const outcome = await h.service.void({ movementGuid: original.movementGuid, note: "输错金额" });
    assert.equal(outcome.kind, "saved");
    const stored = await rows(h);
    assert.equal(stored.length, 2);
    assert.equal(stored[1]?.voids_movement_guid, original.movementGuid);
    assert.equal(stored[1]?.direction, "In");
    assert.equal(stored[1]?.reason_code, "SafeDrop");
    assert.equal(stored[1]?.note, "输错金额");
    assert.deepEqual(h.calls, ["drain", "drawer", "print"]);
    const slip = h.text(h.printed[0]!);
    assert.match(slip, /CASH MOVEMENT VOID/u);
    assert.match(slip, new RegExp(original.displayNumber, "u"));
    const listed = await h.service.listToday();
    assert.notEqual(listed.movements[0]?.voidedByMovementGuid, null);
    assert.deepEqual(await h.service.summary(), {
      cashInCents: 0,
      cashOutCents: 0,
      byReason: { ChangeTopUp: 0, OtherIn: 0, SafeDrop: 0, PaidOut: 0 },
    });
  } finally {
    await h.database.close();
  }
});

test("冲正：开钱箱失败只提示，冲正记录仍已落库且仍打印凭条", async () => {
  const h = await createHarness({ drawer: () => "failed" });
  try {
    const original = await seed(h);
    h.calls.length = 0;
    const outcome = await h.service.void({ movementGuid: original.movementGuid, note: "输错金额" });
    assert.equal(outcome.kind, "saved");
    if (outcome.kind === "saved") {
      assert.equal(outcome.drawer, "failed");
      assert.equal(outcome.print, "ok");
    }
    assert.equal((await rows(h)).length, 2);
    assert.deepEqual(h.calls, ["drain", "drawer", "print"]);
  } finally {
    await h.database.close();
  }
});

test("冲正资格：已冲正 / 冲正记录 / 非今天 / 日结之后 / 找不到都被拒绝，且不落库", async () => {
  const h = await createHarness({
    repository: (real) => ({
      append: (commit: CashMovementCommit) => real.append(commit),
      getByGuid: (guid) => real.getByGuid(guid),
      listToday: (scope) => real.listToday(scope),
      summarizePeriod: (scope) => real.summarizePeriod(scope),
      latestDailyCloseSavedAtIso: async () => closeSavedAt,
    }),
  });
  let closeSavedAt: string | null = null;
  try {
    const original = await seed(h);
    // 日结之后
    closeSavedAt = "2026-11-14T02:45:00.000Z";
    assert.deepEqual(await h.service.void({ movementGuid: original.movementGuid, note: "输错金额" }), {
      kind: "not-allowed",
      reason: "DAILY_CLOSE_SAVED",
    });
    closeSavedAt = null;
    // 找不到
    assert.deepEqual(await h.service.void({ movementGuid: "00000000-0000-4000-8000-00000000ffff", note: "输错金额" }), {
      kind: "not-allowed",
      reason: "NOT_FOUND",
    });
    // 成功冲正一次，再冲正：已冲正；冲正记录本身：不可冲正
    const voided = await h.service.void({ movementGuid: original.movementGuid, note: "输错金额" });
    assert.equal(voided.kind, "saved");
    assert.deepEqual(await h.service.void({ movementGuid: original.movementGuid, note: "再来一次" }), {
      kind: "not-allowed",
      reason: "ALREADY_VOIDED",
    });
    const voidRecord = voided.kind === "saved" ? voided.movement : null;
    assert.deepEqual(await h.service.void({ movementGuid: voidRecord!.movementGuid, note: "冲正的冲正" }), {
      kind: "not-allowed",
      reason: "IS_VOID_RECORD",
    });
    // 非今天：时间推到次日营业日
    const second = await seed(h, "ChangeTopUp", 1_000);
    h.setNow("2026-11-14T20:00:00.000Z"); // 布里斯班 11-15 06:00
    assert.deepEqual(await h.service.void({ movementGuid: second.movementGuid, note: "输错金额" }), {
      kind: "not-allowed",
      reason: "NOT_TODAY",
    });
    assert.equal((await rows(h)).length, 3);
  } finally {
    await h.database.close();
  }
});

test("冲正备注 < 2 字拒绝，且在授权之前；库触发器兜底的拒绝映射为 not-allowed", async () => {
  const h = await createHarness({
    repository: (real) => ({
      append: async (commit: CashMovementCommit) => {
        if (commit.movement.voidsMovementGuid && forceDenial) {
          throw new CashMovementRepositoryError("VOID_NOT_ALLOWED", "DAILY_CLOSE_SAVED");
        }
        return real.append(commit);
      },
      getByGuid: (guid) => real.getByGuid(guid),
      listToday: (scope) => real.listToday(scope),
      summarizePeriod: (scope) => real.summarizePeriod(scope),
      latestDailyCloseSavedAtIso: (scope) => real.latestDailyCloseSavedAtIso(scope),
    }),
  });
  let forceDenial = false;
  try {
    const original = await seed(h);
    assert.deepEqual(await h.service.void({ movementGuid: original.movementGuid, note: " x " }), {
      kind: "invalid",
      code: "NOTE_REQUIRED",
    });
    forceDenial = true;
    assert.deepEqual(await h.service.void({ movementGuid: original.movementGuid, note: "输错金额" }), {
      kind: "not-allowed",
      reason: "DAILY_CLOSE_SAVED",
    });
    assert.equal((await rows(h)).length, 1);
  } finally {
    await h.database.close();
  }
});

test("冲正权限：持有 Void 的收银员可直接冲；没有则走主管授权并写入授权人", async () => {
  const seen: string[] = [];
  const h = await createHarness({
    permissions: [CASH_MOVEMENT_CASH_OUT_PERMISSION],
    authorization: passThroughAuthorization(
      () => ({
        cashierId: "c1",
        cashierName: "Cashier One",
        userGuid: null,
        permissionCodes: [CASH_MOVEMENT_CASH_OUT_PERMISSION],
        storeCode: "S1",
        deviceCode: "D1",
      }),
      { cashierId: "boss", cashierName: "Boss" },
      seen,
    ),
  });
  try {
    const original = await seed(h);
    seen.length = 0;
    const outcome = await h.service.void({ movementGuid: original.movementGuid, note: "输错金额" });
    assert.equal(outcome.kind, "saved");
    assert.deepEqual(seen, [`${CASH_MOVEMENT_VOID_PERMISSION}|cash-movement-void|cash-movement`]);
    const stored = await rows(h);
    assert.equal(stored[1]?.authorized_by_id, "boss");
  } finally {
    await h.database.close();
  }
});

test("补打：凭条带 REPRINT；冲正记录补打带原记录；找不到 / 打印失败分别返回 not-found / failed", async () => {
  const h = await createHarness();
  try {
    const original = await seed(h);
    assert.deepEqual(await h.service.reprint(original.movementGuid), { kind: "printed" });
    assert.match(h.text(h.printed[0]!), /REPRINT/u);
    const voided = await h.service.void({ movementGuid: original.movementGuid, note: "输错金额" });
    h.printed.length = 0;
    if (voided.kind !== "saved") throw new Error("void failed");
    assert.deepEqual(await h.service.reprint(voided.movement.movementGuid), { kind: "printed" });
    const slip = h.text(h.printed[0]!);
    assert.match(slip, /CASH MOVEMENT VOID/u);
    assert.match(slip, /REPRINT/u);
    assert.deepEqual(await h.service.reprint("00000000-0000-4000-8000-00000000ffff"), { kind: "not-found" });
  } finally {
    await h.database.close();
  }
  const failing = await createHarness({ print: () => "failed" });
  try {
    const original = await seed(failing);
    assert.deepEqual(await failing.service.reprint(original.movementGuid), { kind: "failed" });
  } finally {
    await failing.database.close();
  }
});

test("会话失效：所有入口失败关闭，不触碰库与硬件", async () => {
  const h = await createHarness();
  try {
    const original = await seed(h);
    h.setSession({});
    // 模拟 deps.session() 抛错：用新的服务实例。
    const broken = createCashMovementRuntimeService({
      repository: h.database.repository,
      authorization: null,
      session: () => {
        throw new Error("no cashier");
      },
      businessTimeZone: TZ,
      createId: () => "x",
      now: () => new Date(NOW),
      requestDrain: () => undefined,
      hardware: {
        openDrawer: async () => "ok",
        printSlip: async () => "ok",
        receiptContext: async () => ({ paper: "80mm", brandName: "", storeName: "" }),
      },
    });
    assert.deepEqual(await broken.submit({ reasonCode: "ChangeTopUp", amountCents: 100, note: "" }), { kind: "failed" });
    assert.deepEqual(await broken.void({ movementGuid: original.movementGuid, note: "输错金额" }), { kind: "failed" });
    assert.deepEqual(await broken.reprint(original.movementGuid), { kind: "failed" });
    await assert.rejects(broken.listToday());
  } finally {
    await h.database.close();
  }
});

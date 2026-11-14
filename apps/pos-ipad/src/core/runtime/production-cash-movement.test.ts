import assert from "node:assert/strict";
import test from "node:test";

import {
  CashMovementRepositoryError,
  type LocalCashMovement,
} from "@hb/pos-domain/core/contracts/cash-movement";

import {
  OperationAuthorizationService,
  type AuthorizedOperationContext,
  type OperationAuthorizationRequest,
  type OperationAuthorizationResult,
} from "../../features/operation-authorization/operation-authorization-service";

import {
  CASH_MOVEMENT_PERMISSIONS,
  createCashMovementRuntime,
  type CashMovementHardwareSettings,
  type CashMovementSession,
  type ProductionCashMovementDependencies,
} from "./production-cash-movement";
import { MemoryCashMovementStore } from "./test-support/memory-cash-movement-store";

const TZ = "Australia/Brisbane";
// 悉尼 / 布里斯班营业日 2026-11-14：UTC 2026-11-13T14:00 ～ 2026-11-14T14:00。
const NOW = "2026-11-14T02:00:00.000Z";

function session(overrides: Partial<CashMovementSession> = {}): CashMovementSession {
  return {
    cashierId: "cashier-1",
    cashierName: "Cashier One",
    userGuid: null,
    storeCode: "S001",
    deviceCode: "IPAD-1",
    permissionCodes: [
      CASH_MOVEMENT_PERMISSIONS.cashIn,
      CASH_MOVEMENT_PERMISSIONS.cashOut,
      CASH_MOVEMENT_PERMISSIONS.void,
    ],
    ...overrides,
  };
}

type AuthMode = "own" | "supervisor" | "deny" | "cancel" | "revoked";

/** 可编排的授权替身：记录每次请求，按模式决定是否执行回调以及授权人。 */
class FakeAuthorization {
  public requests: OperationAuthorizationRequest[] = [];
  public mode: AuthMode = "own";
  public async authorizeAndRun<T>(
    input: OperationAuthorizationRequest,
    operation: (context: AuthorizedOperationContext) => T | Promise<T>,
  ): Promise<OperationAuthorizationResult<T>> {
    this.requests.push(input);
    if (this.mode === "deny") return { authorized: false, reason: "AUTHENTICATION_FAILED" };
    if (this.mode === "cancel") return { authorized: false, reason: "CANCELLED" };
    if (this.mode === "revoked") return { authorized: false, reason: "REVOKED" };
    const context: AuthorizedOperationContext =
      this.mode === "supervisor"
        ? {
            authorizationMode: "online",
            requestingCashierId: "cashier-1",
            authorizingCashierId: "boss-1",
            authorizingActor: { cashierId: "boss-1", cashierName: "Boss", userGuid: "guid-boss" },
            permissionCode: input.permissionCode,
          }
        : {
            authorizationMode: "current-cashier",
            requestingCashierId: "cashier-1",
            authorizingCashierId: null,
            permissionCode: input.permissionCode,
          };
    return { authorized: true, value: await operation(context) };
  }
}

type Harness = ReturnType<typeof harness>;

function harness(
  options: Readonly<{
    settings?: Partial<CashMovementHardwareSettings>;
    session?: () => CashMovementSession;
    withAuthorization?: boolean;
    drain?: () => unknown;
  }> = {},
) {
  const store = new MemoryCashMovementStore();
  const authorization = new FakeAuthorization();
  const events: string[] = [];
  const printed: { jobId: string; text: string }[] = [];
  const hardware = {
    connectFails: false,
    drawerResult: "completed" as "completed" | "failed" | "throw",
    printResult: "printed" as "printed" | "failed" | "throw",
  };
  let counter = 0;
  let clock = Date.parse(NOW);
  const deps: ProductionCashMovementDependencies = {
    repository: store,
    authorization: options.withAuthorization === false ? null : authorization,
    session: options.session ?? (() => session()),
    businessTimeZone: TZ,
    now: () => new Date((clock += 1_000)),
    createId: () => {
      counter += 1;
      return `20000000-0000-4000-8000-${String(counter).padStart(12, "0")}`;
    },
    readHardwareSettings: async () => ({
      printEnabled: true,
      drawerEnabled: true,
      peripheralId: "printer-1",
      paper: "80mm",
      brandName: "Hot Bargain",
      storeName: "Brisbane",
      ...options.settings,
    }),
    hardware: {
      async connect() {
        events.push("connect");
        if (hardware.connectFails) throw new Error("connect failed");
      },
      async open() {
        events.push("drawer");
        if (hardware.drawerResult === "throw") throw new Error("drawer boom");
        return { status: hardware.drawerResult, errorCode: null };
      },
      async print(jobId: string, bytes: Uint8Array) {
        events.push("print");
        if (hardware.printResult === "throw") throw new Error("print boom");
        printed.push({ jobId, text: new TextDecoder().decode(bytes) });
        return { status: hardware.printResult, errorCode: null };
      },
    },
    requestDrain: () => {
      events.push("drain");
      return options.drain ? options.drain() : Promise.resolve();
    },
  };
  return {
    store,
    authorization,
    events,
    printed,
    hardware,
    runtime: createCashMovementRuntime(deps),
  };
}

let guidCounter = 0;
const guid = () => `30000000-0000-4000-8000-${String(++guidCounter).padStart(12, "0")}`;

async function save(
  h: Harness,
  reasonCode: "ChangeTopUp" | "OtherIn" | "SafeDrop" | "PaidOut" = "ChangeTopUp",
  amountCents = 5_000,
  note: string | null = null,
) {
  return h.runtime.submit({ movementGuid: guid(), reasonCode, amountCents, note });
}

// ───────────────────────── 保存顺序与授权 ─────────────────────────

test("本人有权限：落库 → 唤醒上传 → 开钱箱 → 打印；授权人为空，审计与记录字段正确", async () => {
  const h = harness();
  const result = await save(h, "ChangeTopUp", 5_000);
  assert.equal(result.kind, "saved");
  if (result.kind !== "saved") return;
  assert.equal(result.drawer, "opened");
  assert.equal(result.print, "printed");
  assert.deepEqual(h.events, ["drain", "connect", "drawer", "print"]);

  const row = h.store.rows[0]!;
  assert.equal(row.direction, "In");
  assert.equal(row.reasonCode, "ChangeTopUp");
  assert.equal(row.amountCents, 5_000);
  assert.equal(row.businessDate, "2026-11-14");
  assert.equal(row.storeCode, "S001");
  assert.equal(row.deviceCode, "IPAD-1");
  assert.equal(row.cashierId, "cashier-1");
  assert.equal(row.authorizedById, null);
  assert.equal(row.authorizedByName, null);
  assert.equal(h.store.audits[0]?.eventType, "CASH_IN");
  assert.equal(h.store.audits[0]?.payload.authorizingCashierId, undefined);

  // 授权请求：存入用 CashIn，页面 / 动作用于主管授权弹窗的文案。
  assert.deepEqual(
    h.authorization.requests.map(({ permissionCode, screen, action }) => ({ permissionCode, screen, action })),
    [{ permissionCode: "Permissions.PosTerminal.CashMovement.CashIn", screen: "cash-movement", action: "cash-in" }],
  );
  assert.equal(h.printed.length, 1);
  assert.match(h.printed[0]!.text, /CASH IN/u);
  assert.match(h.printed[0]!.text, /CM-0001/u);
});

test("取出用 CashOut 权限；交店长与小额支出分别落成 CASH_OUT 审计", async () => {
  const h = harness();
  await save(h, "SafeDrop", 20_000);
  await save(h, "PaidOut", 1_250, "milk");
  assert.deepEqual(
    h.authorization.requests.map((request) => request.permissionCode),
    [CASH_MOVEMENT_PERMISSIONS.cashOut, CASH_MOVEMENT_PERMISSIONS.cashOut],
  );
  assert.deepEqual(h.store.audits.map((audit) => audit.eventType), ["CASH_OUT", "CASH_OUT"]);
  assert.equal(h.store.rows[1]?.note, "milk");
  assert.match(h.printed[0]!.text, /CASH OUT/u);
  assert.match(h.printed[0]!.text, /SafeDrop|Safe drop/iu);
});

test("主管授权：授权人写入记录（authorizedBy）与审计，收银员仍是发起人", async () => {
  const h = harness();
  h.authorization.mode = "supervisor";
  const result = await save(h, "OtherIn", 700, "float");
  assert.equal(result.kind, "saved");
  const row = h.store.rows[0]!;
  assert.equal(row.cashierId, "cashier-1");
  assert.equal(row.authorizedById, "boss-1");
  assert.equal(row.authorizedByName, "Boss");
  assert.equal(h.store.audits[0]?.payload.authorizingCashierId, "boss-1");
  // 凭条带授权人。
  assert.match(h.printed[0]!.text, /Boss/u);
});

test("授权被拒 / 取消 / 撤销：不落库、不唤醒上传、不开钱箱、不打印", async () => {
  for (const [mode, expected] of [
    ["deny", "denied"],
    ["cancel", "cancelled"],
    ["revoked", "cancelled"],
  ] as const) {
    const h = harness();
    h.authorization.mode = mode;
    const result = await save(h);
    assert.deepEqual(result, { kind: "denied", reason: expected }, mode);
    assert.equal(h.store.rows.length, 0);
    assert.equal(h.store.appendCalls, 0);
    assert.deepEqual(h.events, []);
  }
});

test("没有配置主管授权时只认收银员自己的权限：有权限直接保存，没权限直接拒绝", async () => {
  const allowed = harness({ withAuthorization: false });
  assert.equal((await save(allowed)).kind, "saved");
  assert.equal(allowed.store.rows[0]?.authorizedById, null);

  const missing = harness({
    withAuthorization: false,
    session: () => session({ permissionCodes: [CASH_MOVEMENT_PERMISSIONS.cashOut] }),
  });
  assert.deepEqual(await save(missing, "ChangeTopUp"), { kind: "denied", reason: "denied" });
  assert.equal(missing.store.appendCalls, 0);
  assert.equal((await save(missing, "SafeDrop")).kind, "saved");
});

test("输入不合法时先拒绝、不弹出授权：金额 0 / 超限 / 备注必填被绕过的情况都挡在授权之前", async () => {
  const h = harness();
  assert.deepEqual(await save(h, "ChangeTopUp", 0), { kind: "invalid", code: "AMOUNT_INVALID" });
  assert.deepEqual(await save(h, "ChangeTopUp", 100_000_001), { kind: "invalid", code: "AMOUNT_TOO_LARGE" });
  assert.deepEqual(await save(h, "OtherIn", 500, " "), { kind: "invalid", code: "NOTE_REQUIRED" });
  assert.deepEqual(await save(h, "PaidOut", 500, "a"), { kind: "invalid", code: "NOTE_REQUIRED" });
  assert.deepEqual(
    await h.runtime.submit({ movementGuid: guid(), reasonCode: "Bogus" as never, amountCents: 100, note: null }),
    { kind: "invalid", code: "REASON_INVALID" },
  );
  assert.equal(h.authorization.requests.length, 0);
  assert.equal(h.store.appendCalls, 0);
});

test("授权通过后会话已变化（锁屏 / 换人）：不落库", async () => {
  let active = session();
  const h = harness({ session: () => active });
  const original = h.authorization.authorizeAndRun.bind(h.authorization);
  h.authorization.authorizeAndRun = (async (input, operation) => {
    // 主管扫码等待期间换了收银员。
    active = session({ cashierId: "cashier-2" });
    return original(input, operation);
  }) as typeof h.authorization.authorizeAndRun;
  assert.deepEqual(await save(h), { kind: "failed" });
  assert.equal(h.store.appendCalls, 0);
  assert.deepEqual(h.events, []);
});

// ───────────────────────── 落库之后的失败不回滚 ─────────────────────────

test("落库失败：返回失败，不唤醒上传、不开钱箱、不打印", async () => {
  const h = harness();
  h.store.appendError = new Error("disk full");
  assert.deepEqual(await save(h), { kind: "failed" });
  assert.equal(h.store.rows.length, 0);
  assert.deepEqual(h.events, []);
});

test("打印失败 / 打印抛错：记录保留并返回 saved，只标记打印失败", async () => {
  for (const printResult of ["failed", "throw"] as const) {
    const h = harness();
    h.hardware.printResult = printResult;
    const result = await save(h);
    assert.equal(result.kind, "saved", printResult);
    if (result.kind !== "saved") return;
    assert.equal(result.print, "failed");
    assert.equal(result.drawer, "opened");
    assert.equal(h.store.rows.length, 1, "记录不回滚");
    assert.equal(h.store.audits.length, 1);
  }
});

test("钱箱失败：记录保留，凭条照常打印", async () => {
  for (const drawerResult of ["failed", "throw"] as const) {
    const h = harness();
    h.hardware.drawerResult = drawerResult;
    const result = await save(h);
    assert.equal(result.kind, "saved");
    if (result.kind !== "saved") return;
    assert.equal(result.drawer, "failed");
    assert.equal(result.print, "printed");
    assert.equal(h.store.rows.length, 1);
  }
});

test("打印机连接失败：钱箱与打印都标记失败，记录保留", async () => {
  const h = harness();
  h.hardware.connectFails = true;
  const result = await save(h);
  assert.equal(result.kind, "saved");
  if (result.kind !== "saved") return;
  assert.equal(result.drawer, "failed");
  assert.equal(result.print, "failed");
  assert.equal(h.store.rows.length, 1);
});

test("本机设置里钱箱 / 打印未启用：对应步骤跳过，不连接、不报钱箱失败；读设置失败只影响硬件", async () => {
  const off = harness({ settings: { drawerEnabled: false, printEnabled: false } });
  const result = await save(off);
  assert.equal(result.kind === "saved" && result.drawer, "skipped");
  assert.equal(result.kind === "saved" && result.print, "skipped");
  assert.deepEqual(off.events, ["drain"]);

  const noPeripheral = harness({ settings: { peripheralId: null } });
  const second = await save(noPeripheral);
  assert.equal(second.kind === "saved" && second.drawer, "skipped");
  assert.equal(second.kind === "saved" && second.print, "skipped");

  const drawerOnly = harness({ settings: { printEnabled: false } });
  const third = await save(drawerOnly);
  assert.equal(third.kind === "saved" && third.drawer, "opened");
  assert.equal(third.kind === "saved" && third.print, "skipped");
});

test("唤醒上传同步抛出或异步拒绝：保存结果不受影响", async () => {
  for (const drain of [
    () => {
      throw new Error("drain unavailable");
    },
    () => Promise.reject(new Error("drain rejected")),
  ]) {
    const h = harness({ drain });
    const result = await save(h);
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.equal(result.kind, "saved");
    assert.equal(h.store.rows.length, 1);
    assert.equal(h.printed.length, 1);
  }
});

// ───────────────────────── 冲正 ─────────────────────────

async function saveOriginal(h: Harness): Promise<LocalCashMovement> {
  const result = await save(h, "SafeDrop", 20_000);
  assert.equal(result.kind, "saved");
  h.events.length = 0;
  h.printed.length = 0;
  h.authorization.requests.length = 0;
  return h.store.rows[0]!;
}

test("冲正：用 Void 权限，原因码与金额取自库里的原记录，凭条带原记录信息并开钱箱", async () => {
  const h = harness();
  const original = await saveOriginal(h);
  const result = await h.runtime.voidMovement({
    movementGuid: guid(),
    originalMovementGuid: original.movementGuid,
    note: "wrong amount",
  });
  assert.equal(result.kind, "saved");
  const voidRow = h.store.rows[1]!;
  assert.equal(voidRow.voidsMovementGuid, original.movementGuid);
  assert.equal(voidRow.reasonCode, "SafeDrop");
  assert.equal(voidRow.amountCents, 20_000);
  assert.equal(voidRow.direction, "In", "方向与原记录相反");
  assert.equal(voidRow.note, "wrong amount");
  assert.equal(h.store.audits[1]?.eventType, "CASH_MOVEMENT_VOID");
  assert.deepEqual(
    h.authorization.requests.map(({ permissionCode, action }) => ({ permissionCode, action })),
    [{ permissionCode: "Permissions.PosTerminal.CashMovement.Void", action: "cash-movement-void" }],
  );
  assert.deepEqual(h.events, ["drain", "connect", "drawer", "print"]);
  assert.match(h.printed[0]!.text, /VOID/u);
  assert.match(h.printed[0]!.text, new RegExp(original.displayNumber, "u"));
  assert.equal(h.store.rows[0]?.voidedByMovementGuid, voidRow.movementGuid);
});

test("冲正：无权限走主管授权，授权人写入冲正记录", async () => {
  const h = harness();
  const original = await saveOriginal(h);
  h.authorization.mode = "supervisor";
  await h.runtime.voidMovement({
    movementGuid: guid(),
    originalMovementGuid: original.movementGuid,
    note: "boss says",
  });
  assert.equal(h.store.rows[1]?.authorizedById, "boss-1");
});

test("冲正：备注少于 2 字 → invalid 且不落库；原记录不存在 / 非本机 → 不可冲正", async () => {
  const h = harness();
  const original = await saveOriginal(h);
  assert.deepEqual(
    await h.runtime.voidMovement({ movementGuid: guid(), originalMovementGuid: original.movementGuid, note: " x " }),
    { kind: "invalid", code: "NOTE_REQUIRED" },
  );
  assert.equal(h.store.rows.length, 1);

  assert.deepEqual(
    await h.runtime.voidMovement({ movementGuid: guid(), originalMovementGuid: guid(), note: "no such" }),
    { kind: "void-not-allowed", denial: null },
  );
  h.store.patchRow(original.movementGuid, { deviceCode: "OTHER" });
  assert.deepEqual(
    await h.runtime.voidMovement({ movementGuid: guid(), originalMovementGuid: original.movementGuid, note: "other dev" }),
    { kind: "void-not-allowed", denial: null },
  );
});

test("冲正被库内规则拒绝（已冲正 / 其后已日结）：返回带原因的 void-not-allowed，不开钱箱不打印", async () => {
  const h = harness();
  const original = await saveOriginal(h);
  await h.runtime.voidMovement({ movementGuid: guid(), originalMovementGuid: original.movementGuid, note: "first" });
  h.events.length = 0;
  const again = await h.runtime.voidMovement({
    movementGuid: guid(),
    originalMovementGuid: original.movementGuid,
    note: "second",
  });
  assert.deepEqual(again, { kind: "void-not-allowed", denial: "ALREADY_VOIDED" });
  assert.deepEqual(h.events, []);

  const daily = harness();
  const target = await saveOriginal(daily);
  daily.store.appendError = new CashMovementRepositoryError("VOID_NOT_ALLOWED", "DAILY_CLOSE_SAVED");
  assert.deepEqual(
    await daily.runtime.voidMovement({ movementGuid: guid(), originalMovementGuid: target.movementGuid, note: "late" }),
    { kind: "void-not-allowed", denial: "DAILY_CLOSE_SAVED" },
  );
});

// ───────────────────────── 补打 / 今日记录 ─────────────────────────

test("补打：不开钱箱，凭条带 REPRINT；冲正记录补打带原记录；他机记录与失败都返回对应结果", async () => {
  const h = harness();
  const original = await saveOriginal(h);
  assert.deepEqual(await h.runtime.reprint(original.movementGuid), { kind: "printed" });
  assert.deepEqual(h.events, ["connect", "print"]);
  assert.match(h.printed[0]!.text, /REPRINT/u);

  await h.runtime.voidMovement({ movementGuid: guid(), originalMovementGuid: original.movementGuid, note: "void it" });
  h.printed.length = 0;
  const voidGuid = h.store.rows[1]!.movementGuid;
  assert.deepEqual(await h.runtime.reprint(voidGuid), { kind: "printed" });
  assert.match(h.printed[0]!.text, /VOID/u);
  assert.match(h.printed[0]!.text, /REPRINT/u);

  assert.deepEqual(await h.runtime.reprint(guid()), { kind: "not-found" });
  h.store.patchRow(original.movementGuid, { storeCode: "OTHER" });
  assert.deepEqual(await h.runtime.reprint(original.movementGuid), { kind: "not-found" });

  h.hardware.printResult = "failed";
  assert.deepEqual(await h.runtime.reprint(voidGuid), { kind: "failed" });
  const disabled = harness({ settings: { printEnabled: false } });
  const row = await saveOriginal(disabled);
  assert.deepEqual(await disabled.runtime.reprint(row.movementGuid), { kind: "failed" });
});

test("今日记录：按门店营业时区取营业日，只含本终端；合计冲正抵消；带最近日结时间", async () => {
  const h = harness();
  const first = await save(h, "ChangeTopUp", 5_000);
  const second = await save(h, "SafeDrop", 2_000);
  assert.equal(first.kind, "saved");
  assert.equal(second.kind, "saved");
  // 另一台设备的记录不计入。
  h.store.rows.push({ ...h.store.rows[0]!, movementGuid: guid(), deviceCode: "IPAD-2", displayNumber: "CM-0099" });
  h.store.latestDailyCloseSavedAt = "2026-11-14T05:00:00.000Z";
  const view = await h.runtime.listToday();
  assert.equal(view.businessDate, "2026-11-14");
  assert.equal(view.businessTimeZone, TZ);
  assert.equal(view.movements.length, 2);
  assert.equal(view.cashInCents, 5_000);
  assert.equal(view.cashOutCents, 2_000);
  assert.equal(view.latestDailyCloseSavedAtIso, "2026-11-14T05:00:00.000Z");
});

test("今日记录与补打在会话失效时拒绝读取 / 返回失败", async () => {
  let valid = true;
  const h = harness({
    session: () => {
      if (!valid) throw new Error("CURRENT_CASHIER_REQUIRED");
      return session();
    },
  });
  const original = await saveOriginal(h);
  valid = false;
  await assert.rejects(h.runtime.listToday(), /CURRENT_CASHIER_REQUIRED/u);
  assert.deepEqual(await h.runtime.reprint(original.movementGuid), { kind: "failed" });
  assert.deepEqual(await save(h), { kind: "failed" });
});

// ───────────────────────── 真实授权服务：本人有权限 / 取消 ─────────────────────────

function realAuthorization(permissions: readonly string[]) {
  const service = new OperationAuthorizationService({
    cashierAuthentication: {
      login: async () => {
        throw new Error("supervisor login is not used in this test");
      },
    },
    audit: { append: async () => undefined },
    createId: (() => {
      let n = 0;
      return () => `40000000-0000-4000-8000-${String(++n).padStart(12, "0")}`;
    })(),
    nowIso: () => NOW,
  });
  service.activateRequestingCashier({
    cashierId: "cashier-1",
    cashierName: "Cashier One",
    userGuid: null,
    storeCode: "S001",
    deviceCode: "IPAD-1",
    permissions,
  });
  return service;
}

test("接真实授权服务：本人有权限时 authorizingActor 为空 → 授权人为空；无权限时进入主管授权等待，取消后不落库", async () => {
  const own = realAuthorization([CASH_MOVEMENT_PERMISSIONS.cashIn]);
  const h = harness();
  const runtime = createCashMovementRuntime({
    repository: h.store,
    authorization: own,
    session: () => session(),
    businessTimeZone: TZ,
    now: () => new Date(NOW),
    createId: () => guid(),
    readHardwareSettings: async () => ({
      printEnabled: false,
      drawerEnabled: false,
      peripheralId: null,
      paper: "80mm",
      brandName: "",
      storeName: "",
    }),
    hardware: {
      connect: async () => undefined,
      open: async () => ({ status: "completed", errorCode: null }),
      print: async () => ({ status: "printed", errorCode: null }),
    },
    requestDrain: () => undefined,
  });
  const saved = await runtime.submit({ movementGuid: guid(), reasonCode: "ChangeTopUp", amountCents: 100, note: null });
  assert.equal(saved.kind, "saved");
  assert.equal(h.store.rows[0]?.authorizedById, null);

  // 没有 CashOut 权限：进入「等待主管扫码」，此时取消 → cancelled，且没有落库。
  const pending = runtime.submit({ movementGuid: guid(), reasonCode: "SafeDrop", amountCents: 100, note: null });
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.equal(own.getState().kind, "awaiting-supervisor");
  assert.equal(own.cancel(), true);
  assert.deepEqual(await pending, { kind: "denied", reason: "cancelled" });
  assert.equal(h.store.rows.length, 1);
});

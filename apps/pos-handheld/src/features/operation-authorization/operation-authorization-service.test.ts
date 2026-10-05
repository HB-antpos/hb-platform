import assert from "node:assert/strict";
import test from "node:test";

import {
  OperationAuthorizationService,
  type OperationAuthorizationRequest,
  type RequestingCashierAuthorizationIdentity,
} from "./operation-authorization-service";

import type { CashierSessionDto } from "@/core/api/hbpos-api";
import type { AuditEventDraft } from "@/core/contracts";
import type { CashierLoginResult } from "@/core/security/cashier-authentication";

const NOW_ISO = "2026-07-28T06:00:00.000Z";
const PERMISSION = "Permissions.PosTerminal.Sales.ChangePrice";

function cashier(overrides: Partial<RequestingCashierAuthorizationIdentity> = {}): RequestingCashierAuthorizationIdentity {
  return {
    cashierId: "REQUESTER",
    cashierName: "Requester",
    userGuid: "requester-user-guid",
    storeCode: "STORE-1",
    deviceCode: "IPAD-1",
    permissions: [],
    ...overrides,
  };
}

function request(overrides: Partial<OperationAuthorizationRequest> = {}): OperationAuthorizationRequest {
  return {
    actionId: "00000000-0000-4000-8000-000000000101",
    permissionCode: PERMISSION,
    screen: "PosTerminal",
    action: "change-price",
    ...overrides,
  };
}

function supervisor(overrides: Partial<CashierSessionDto> = {}): CashierSessionDto {
  return {
    cashierId: "SUPERVISOR",
    userGuid: "supervisor-user-guid",
    cashierName: "Supervisor",
    storeCode: "STORE-1",
    deviceCode: "IPAD-1",
    permissionCodes: [PERMISSION],
    isEmergencyOverride: false,
    authorizationToken: "supervisor-secret-ticket",
    authorizationExpiresAtUtc: "2026-07-28T07:00:00.000Z",
    ...overrides,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function harness(
  login: () => Promise<CashierLoginResult> = async () => ({ source: "online", session: supervisor() }),
  activeCashier: RequestingCashierAuthorizationIdentity | null = cashier(),
) {
  const audits: AuditEventDraft[] = [];
  const loginInputs: Readonly<{ storeCode: string; deviceCode: string; userBarcode: string; }>[] = [];
  let nextId = 0;
  const service = new OperationAuthorizationService({
    cashierAuthentication: {
      async login(input) {
        loginInputs.push(input);
        return login();
      },
    },
    audit: { async append(events) { audits.push(...events); } },
    nowIso: () => NOW_ISO,
    createId: () => `00000000-0000-4000-8000-${String(++nextId).padStart(12, "0")}`,
  });
  if (activeCashier) service.activateRequestingCashier(activeCashier);
  return { service, audits, loginInputs };
}

async function flush(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

test("请求不再携带收银员身份；组合根激活的冻结会话决定精确权限", async () => {
  const active = { ...cashier(), permissions: [] as string[] };
  const { service, loginInputs } = harness(undefined, active);
  active.permissions.push(PERMISSION);
  let calls = 0;
  const forged = { ...request(), requestingCashier: cashier({ permissions: [PERMISSION] }) } as OperationAuthorizationRequest;
  const pending = service.authorizeAndRun(forged, () => { calls += 1; return "done"; });

  assert.equal(calls, 0, "伪造 request 字段不能绕过已激活会话");
  assert.deepEqual(await service.submitSupervisorBarcode("supervisor"), { consumed: true, outcome: "authorized" });
  assert.deepEqual(await pending, { authorized: true, value: "done" });
  assert.equal(calls, 1);
  assert.equal(loginInputs.length, 1);
});

test("当前收银员直通且重复 action 只执行一次，回调上下文没有主管票据", async () => {
  const { service, audits, loginInputs } = harness(undefined, cashier({ permissions: [PERMISSION] }));
  let calls = 0;
  const operation = (context: unknown) => {
    calls += 1;
    assert.deepEqual(context, {
      authorizationMode: "current-cashier",
      requestingCashierId: "REQUESTER",
      authorizingCashierId: null,
      permissionCode: PERMISSION,
    });
    assert.equal(JSON.stringify(context).includes("secret"), false);
    return { completed: true };
  };
  const first = service.authorizeAndRun(request(), operation);
  const duplicate = service.authorizeAndRun(request(), () => { calls += 100; return { completed: false }; });
  assert.equal(first, duplicate);
  assert.deepEqual(await first, { authorized: true, value: { completed: true } });
  assert.equal(calls, 1);
  assert.deepEqual(loginInputs, []);
  assert.deepEqual(audits, []);
});

test("强制主管授权时，即使当前收银员已有权限也必须等待第二人扫码并冻结主管身份", async () => {
  const { service, loginInputs } = harness(undefined, cashier({ permissions: [PERMISSION] }));
  let calls = 0;
  const action = service.authorizeAndRun(request({ forceSupervisor: true }), (context) => {
    calls += 1;
    return context.authorizingActor;
  });

  assert.equal(calls, 0);
  assert.equal(service.getState().kind, "awaiting-supervisor");
  assert.deepEqual(await service.submitSupervisorBarcode("supervisor"), { consumed: true, outcome: "authorized" });
  const result = await action;
  assert.deepEqual(result, {
    authorized: true,
    value: { cashierId: "SUPERVISOR", cashierName: "Supervisor", userGuid: "supervisor-user-guid" },
  });
  assert.equal(result.authorized && Object.isFrozen(result.value), true);
  assert.equal(calls, 1);
  assert.equal(loginInputs.length, 1);
});

test("强制主管授权拒绝当前收银员用 cashierId 或 userGuid 给自己授权", async () => {
  const cases: readonly CashierSessionDto[] = [
    supervisor({ cashierId: " requester ", userGuid: "different-user-guid" }),
    supervisor({ cashierId: "OTHER", userGuid: " REQUESTER-USER-GUID " }),
  ];
  for (const [index, session] of cases.entries()) {
    const { service } = harness(async () => ({ source: "online", session }), cashier({ permissions: [PERMISSION] }));
    const action = service.authorizeAndRun(
      request({ actionId: `forced-self-${index}`, forceSupervisor: true }),
      () => "must-not-run",
    );
    assert.deepEqual(await service.submitSupervisorBarcode("requester"), {
      consumed: true,
      outcome: "denied",
      reason: "AUTHORIZER_IDENTITY_INVALID",
    });
    assert.equal(service.cancel(), true);
    assert.deepEqual(await action, { authorized: false, reason: "CANCELLED" });
  }
});

test("主管授权审计脱敏，票据既不在回调也不留在可 JSON 化服务状态", async () => {
  const { service, audits } = harness();
  const authorization = service.authorizeAndRun(request(), (context) => context);
  await service.submitSupervisorBarcode(" supervisor-barcode ");
  const result = await authorization;
  assert.deepEqual(result, {
    authorized: true,
    value: {
      authorizationMode: "online",
      requestingCashierId: "REQUESTER",
      authorizingCashierId: "SUPERVISOR",
      permissionCode: PERMISSION,
      authorizingActor: {
        cashierId: "SUPERVISOR",
        cashierName: "Supervisor",
        userGuid: "supervisor-user-guid",
      },
    },
  });
  await flush();
  const serialized = JSON.stringify({ audits, service, result });
  assert.equal(serialized.includes("supervisor-secret-ticket"), false);
  assert.equal(serialized.includes("supervisor-barcode"), false);
  assert.equal(audits[0]?.payload.outcome, "Succeeded");
  assert.deepEqual(
    {
      source: audits[0]?.payload.source,
      requestingCashierId: audits[0]?.payload.requestingCashierId,
      requestingCashierName: audits[0]?.payload.requestingCashierName,
      requestingUserGuid: audits[0]?.payload.requestingUserGuid,
    },
    {
      source: "pos-handheld",
      requestingCashierId: "REQUESTER",
      requestingCashierName: "Requester",
      requestingUserGuid: "requester-user-guid",
    },
  );
});

test("主管必须同店同设备、非紧急、票据未过期且精确拥有权限", async () => {
  const cases: readonly Readonly<{ session: CashierSessionDto; reason: string }>[] = [
    { session: supervisor({ isEmergencyOverride: true }), reason: "EMERGENCY_OVERRIDE_DENIED" },
    { session: supervisor({ storeCode: "OTHER" }), reason: "STORE_OR_DEVICE_MISMATCH" },
    { session: supervisor({ deviceCode: "OTHER" }), reason: "STORE_OR_DEVICE_MISMATCH" },
    { session: supervisor({ authorizationToken: " " }), reason: "AUTHORIZATION_TICKET_INVALID" },
    { session: supervisor({ authorizationExpiresAtUtc: NOW_ISO }), reason: "AUTHORIZATION_TICKET_INVALID" },
    { session: supervisor({ permissionCodes: [PERMISSION.toLowerCase()] }), reason: "PERMISSION_DENIED" },
  ];
  for (const { session, reason } of cases) {
    const { service } = harness(async () => ({ source: "offline-cache", session }));
    const action = service.authorizeAndRun(request({ actionId: `action-${reason}` }), () => "must-not-run");
    assert.deepEqual(await service.submitSupervisorBarcode("supervisor"), { consumed: true, outcome: "denied", reason });
    assert.equal(service.cancel(), true);
    assert.deepEqual(await action, { authorized: false, reason: "CANCELLED" });
  }
});

test("可信时钟无效时 fail-closed，并且不悬挂待授权动作", async () => {
  const { service } = harness(async () => ({ source: "online", session: supervisor() }));
  const invalidClock = new OperationAuthorizationService({
    cashierAuthentication: { login: async () => ({ source: "online", session: supervisor() }) },
    audit: { append: async () => {} },
    nowIso: () => "invalid-clock",
    createId: () => "audit-id",
  });
  invalidClock.activateRequestingCashier(cashier());
  const action = invalidClock.authorizeAndRun(request(), () => "must-not-run");
  assert.deepEqual(await invalidClock.submitSupervisorBarcode("supervisor"), { consumed: true, outcome: "denied", reason: "AUTHORIZATION_VALIDATION_FAILED" });
  assert.equal(invalidClock.cancel(), true);
  assert.deepEqual(await action, { authorized: false, reason: "CANCELLED" });
  service.clearRequestingCashier();
});

test("cancel/revoke 与永不返回登录 race，扫描 Promise 立即 cancelled，迟到 rejection 被吸收", async () => {
  const login = deferred<CashierLoginResult>();
  const { service } = harness(() => login.promise);
  const action = service.authorizeAndRun(request(), () => "must-not-run");
  const scan = service.submitSupervisorBarcode("supervisor");
  assert.equal(service.cancel(), true);
  assert.deepEqual(await Promise.race([
    scan,
    new Promise<"timeout">((resolve) => setTimeout(() => resolve("timeout"), 25)),
  ]), { consumed: true, outcome: "cancelled" });
  assert.deepEqual(await action, { authorized: false, reason: "CANCELLED" });
  login.reject(new Error("late rejection"));
  await flush();

  const secondLogin = deferred<CashierLoginResult>();
  const second = harness(() => secondLogin.promise);
  const secondAction = second.service.authorizeAndRun(request(), () => "must-not-run");
  const secondScan = second.service.submitSupervisorBarcode("supervisor");
  second.service.revokeAll();
  assert.deepEqual(await secondScan, { consumed: true, outcome: "cancelled" });
  assert.deepEqual(await secondAction, { authorized: false, reason: "REVOKED" });
  secondLogin.resolve({ source: "online", session: supervisor() });
});

test("状态订阅只公布动作元数据与 verifying，取消和成功均发布状态变化", async () => {
  const login = deferred<CashierLoginResult>();
  const { service } = harness(() => login.promise);
  const states: unknown[] = [];
  const unsubscribe = service.subscribe((state) => states.push(state));
  service.subscribe(() => { throw new Error("broken modal subscriber"); });
  const action = service.authorizeAndRun(request(), () => "ok");
  assert.deepEqual(service.getState(), {
    kind: "awaiting-supervisor", actionId: request().actionId, permissionCode: PERMISSION,
    screen: "PosTerminal", action: "change-price", verifying: false,
  });
  const scan = service.submitSupervisorBarcode("supervisor");
  assert.equal((service.getState() as { verifying: boolean }).verifying, true);
  service.cancel();
  assert.deepEqual(await scan, { consumed: true, outcome: "cancelled" });
  assert.deepEqual(await action, { authorized: false, reason: "CANCELLED" });
  unsubscribe();
  const serialized = JSON.stringify(states);
  assert.equal(serialized.includes("REQUESTER"), false);
  assert.equal(serialized.includes("supervisor-secret-ticket"), false);
  assert.equal(serialized.includes("secret"), false);
  assert.deepEqual(states.at(-1), { kind: "idle" });
});

test("业务同步异常和 Promise rejection 都原样拒绝且相同 action 不会重放", async () => {
  const { service } = harness(undefined, cashier({ permissions: [PERMISSION] }));
  let syncCalls = 0;
  const sync = service.authorizeAndRun(request(), () => { syncCalls += 1; throw new Error("fail"); });
  await assert.rejects(sync, /fail/);
  await assert.rejects(
    service.authorizeAndRun(request(), () => { syncCalls += 100; return "bad"; }),
    /fail/,
  );
  assert.equal(syncCalls, 1);

  let asyncCalls = 0;
  const asyncRequest = request({ actionId: "00000000-0000-4000-8000-000000000202" });
  await assert.rejects(
    service.authorizeAndRun(asyncRequest, async () => { asyncCalls += 1; throw new Error("async fail"); }),
    /async fail/,
  );
  await assert.rejects(
    service.authorizeAndRun(asyncRequest, () => { asyncCalls += 100; return "bad"; }),
    /async fail/,
  );
  assert.equal(asyncCalls, 1);
});

test("清除/切换收银员同步撤销记录；没有激活可信会话时 fail-closed", async () => {
  const { service } = harness(undefined, cashier({ permissions: [PERMISSION] }));
  const delayed = deferred<string>();
  let calls = 0;
  const running = service.authorizeAndRun(request(), () => { calls += 1; return delayed.promise; });
  service.clearRequestingCashier();
  assert.deepEqual(await running, { authorized: false, reason: "REVOKED" });
  assert.equal(calls, 0, "lock 发生在微任务前时，业务副作用不能启动");
  assert.deepEqual(await service.authorizeAndRun(request({ actionId: "new" }), () => "bad"), { authorized: false, reason: "NO_ACTIVE_CASHIER" });
  delayed.resolve("late");
  service.activateRequestingCashier(cashier({ cashierId: "NEXT", permissions: [PERMISSION] }));
  assert.deepEqual(await service.authorizeAndRun(request(), (context) => context.requestingCashierId), { authorized: true, value: "NEXT" });
  assert.deepEqual(await service.authorizeAndRun(request({ actionId: "new-session" }), (context) => context.requestingCashierId), { authorized: true, value: "NEXT" });
});

test("revokeAll 在业务微任务开始前阻断回调，不能把未开始动作伪装成已执行", async () => {
  const { service } = harness(undefined, cashier({ permissions: [PERMISSION] }));
  let calls = 0;
  const action = service.authorizeAndRun(
    request({ actionId: "revoke-before-operation" }),
    () => { calls += 1; return "must-not-run"; },
  );
  service.revokeAll();
  assert.deepEqual(await action, { authorized: false, reason: "REVOKED" });
  await flush();
  assert.equal(calls, 0);
});

test("业务回调已进入时 revokeAll 不伪装取消，等待其真实耐久结果", async () => {
  const { service } = harness(undefined, cashier({ permissions: [PERMISSION] }));
  const completion = deferred<string>();
  let calls = 0;
  const action = service.authorizeAndRun(
    request({ actionId: "revoke-after-operation" }),
    () => { calls += 1; return completion.promise; },
  );
  await Promise.resolve();
  assert.equal(calls, 1);
  service.revokeAll();
  completion.resolve("durable-result");
  assert.deepEqual(await action, { authorized: true, value: "durable-result" });
});

test("终态重放 tombstone 有 2048 上限；近端 action 在超过上限后仍不得重放", async () => {
  const { service } = harness(undefined, cashier({ permissions: [PERMISSION] }));
  let calls = 0;
  for (let index = 0; index < 2052; index += 1) {
    const actionId = `bounded-${index}`;
    const result = await service.authorizeAndRun(request({ actionId }), () => { calls += 1; return index; });
    assert.deepEqual(result, { authorized: true, value: index });
  }
  assert.equal(calls, 2052);
  assert.deepEqual(await service.authorizeAndRun(request({ actionId: "bounded-2051" }), () => { calls += 1000; return "replay"; }), { authorized: true, value: 2051 });
  assert.equal(calls, 2052, "近端终态 tombstone 不能被重放");
});

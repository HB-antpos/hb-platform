// 位置请求待办消费流程：资格、筛选、一次取点共用、终态 / 可重试分类、in-flight 锁、15 秒节流、会话内去重、
// 非交互不弹权限框 / 交互走权限申请、账号切换中途停止、提示与日志不含坐标。
import assert from "node:assert/strict";
import { test } from "node:test";
import type { CapturedLocationPayload } from "@/modules/attendance/required-location";
import type { LocationCaptureOutcome } from "./location-request-capture";
import { createLocationRequestConsumer } from "./location-request-consumer";
import {
  LOCATION_REQUEST_FETCH_MIN_INTERVAL_MS,
  LocationRequestResponseError,
  TERMINAL_RESPOND_ERROR_CODES,
  type AttendanceLocationRequest,
  type LocationRequestNotice,
  type RespondLocationRequestBody,
} from "./location-request-logic";

const START = Date.parse("2026-11-19T01:10:00Z");
const USER = "user-a";
const LAT = -32.928311;
const LNG = 151.781742;

const location = (overrides: Partial<CapturedLocationPayload> = {}): CapturedLocationPayload => ({
  locationLatitude: LAT,
  locationLongitude: LNG,
  locationAccuracy: 9,
  locationPermissionStatus: "granted",
  locationCapturedAtUtc: "2026-11-19T01:10:05.000Z",
  ...overrides,
});

const req = (guid: string, overrides: Partial<AttendanceLocationRequest> = {}): AttendanceLocationRequest => ({
  requestGuid: guid,
  storeCode: "1042",
  requestedByName: "Alex Manager",
  status: "Pending",
  createdAtUtc: "2026-11-19T01:05:00Z",
  expiresAtUtc: "2026-11-19T01:20:00Z",
  alreadyResponded: false,
  ...overrides,
});

const businessError = (code: string) => Object.assign(new Error("failed"), { apiBusinessError: true, code });
const httpError = (status: number | undefined) =>
  Object.assign(new Error("http"), status === undefined ? { code: "ERR_NETWORK" } : { response: { status, data: {} } });

interface Deferred<T> {
  promise: Promise<T>;
  resolve(value: T): void;
  reject(error: unknown): void;
}
function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

interface HarnessOptions {
  pending?: () => Promise<AttendanceLocationRequest[]>;
  respond?: (guid: string) => Promise<AttendanceLocationRequest>;
  capture?: (interactive: boolean) => Promise<LocationCaptureOutcome>;
  device?: () => Promise<{ hardwareId?: string; systemDeviceNumber?: string; deviceSystem?: string }>;
}

function harness(options: HarnessOptions = {}) {
  const clock = { nowMs: START };
  const state = { user: USER as string | null, pending: [req("r1")] };
  const log = {
    lists: 0,
    captures: [] as boolean[],
    responds: [] as { userGuid: string; guid: string; body: RespondLocationRequestBody }[],
    notices: [] as LocationRequestNotice[],
    warnings: [] as { message: string; detail?: Record<string, unknown> }[],
  };
  const consumer = createLocationRequestConsumer({
    now: () => clock.nowMs,
    resolveUserGuid: () => state.user,
    listPending: async () => {
      log.lists += 1;
      return options.pending ? options.pending() : state.pending.map((item) => ({ ...item }));
    },
    respond: async (userGuid, guid, body) => {
      log.responds.push({ userGuid, guid, body });
      return options.respond ? options.respond(guid) : req(guid, { status: "Responded" });
    },
    captureLocation: async (interactive) => {
      log.captures.push(interactive);
      return options.capture ? options.capture(interactive) : { status: "ok", location: location() };
    },
    getDeviceContext: options.device ?? (async () => ({ hardwareId: "hw-1", systemDeviceNumber: "SYS-1", deviceSystem: "Android" })),
    showNotice: (notice) => {
      log.notices.push(notice);
    },
    warn: (message, detail) => {
      log.warnings.push({ message, detail });
    },
  });
  return { consumer, clock, state, log };
}

/** 让出事件循环，等已启动的异步流程推进到下一个 await。 */
const tick = () => new Promise<void>((resolve) => setImmediate(resolve));

// ───────── 资格 ─────────

test("没有资格（未登录 / 非个人账号 / 无权限 / 审核态）：不发任何请求", async () => {
  const h = harness();
  h.state.user = null;
  for (const trigger of ["launch", "foreground", "push-received", "push-tap", "inbox-tap"] as const) {
    assert.deepEqual(await h.consumer.consume({ trigger }), { kind: "skipped", reason: "not-eligible" }, trigger);
  }
  assert.equal(h.log.lists, 0);
  assert.deepEqual(h.log.captures, []);
  assert.equal(h.log.responds.length, 0);
});

// ───────── 主流程 ─────────

test("没有待办：只拉一次列表，不取位置、不上报、不提示", async () => {
  const h = harness();
  h.state.pending = [];
  assert.deepEqual(await h.consumer.consume({ trigger: "launch" }), { kind: "idle" });
  assert.equal(h.log.lists, 1);
  assert.deepEqual(h.log.captures, []);
  assert.deepEqual(h.log.notices, []);
});

test("有一条待办：非交互取一次位置并上报，请求体字段齐全，提示带请求人姓名", async () => {
  const h = harness();
  assert.deepEqual(await h.consumer.consume({ trigger: "foreground" }), { kind: "done", responded: 1, discarded: 0, retryLater: 0 });
  assert.deepEqual(h.log.captures, [false], "foreground 是非交互");
  assert.equal(h.log.responds.length, 1);
  assert.equal(h.log.responds[0].userGuid, USER);
  assert.equal(h.log.responds[0].guid, "r1");
  assert.deepEqual(h.log.responds[0].body, {
    hardwareId: "hw-1",
    systemDeviceNumber: "SYS-1",
    deviceSystem: "Android",
    locationLatitude: LAT,
    locationLongitude: LNG,
    locationAccuracy: 9,
    locationPermissionStatus: "granted",
    locationCapturedAtUtc: "2026-11-19T01:10:05.000Z",
  });
  assert.deepEqual(h.log.notices, [{ kind: "reported-named", name: "Alex Manager" }]);
  assert.deepEqual(h.log.warnings, []);
});

test("多条待办共用一个点：只取一次位置，按创建时间从早到晚逐条上报，提示合并条数", async () => {
  const h = harness();
  h.state.pending = [
    req("late", { createdAtUtc: "2026-11-19T01:07:00Z" }),
    req("early", { createdAtUtc: "2026-11-19T01:06:00Z" }),
  ];
  const outcome = await h.consumer.consume({ trigger: "launch" });
  assert.deepEqual(outcome, { kind: "done", responded: 2, discarded: 0, retryLater: 0 });
  assert.equal(h.log.captures.length, 1, "所有待办共用一个点");
  assert.deepEqual(h.log.responds.map((item) => item.guid), ["early", "late"]);
  assert.deepEqual(h.log.responds[0].body, h.log.responds[1].body, "两条请求用同一个点");
  assert.deepEqual(h.log.notices, [{ kind: "reported-many", count: 2 }]);
});

test("过滤：非 Pending / 已响应重放 / 设备时钟已过期的请求不会触发取位置", async () => {
  const h = harness();
  h.state.pending = [
    req("responded", { status: "Responded" }),
    req("replay", { alreadyResponded: true }),
    req("expired", { expiresAtUtc: "2026-11-19T01:09:00Z" }),
  ];
  assert.deepEqual(await h.consumer.consume({ trigger: "launch" }), { kind: "idle" });
  assert.deepEqual(h.log.captures, []);
});

test("设备信息取不到也照常上报（只是可选补充）", async () => {
  const h = harness({
    device: async () => {
      throw new Error("storage unavailable");
    },
  });
  assert.equal((await h.consumer.consume({ trigger: "launch" })).kind, "done");
  assert.equal("hardwareId" in h.log.responds[0].body, false);
  assert.equal(h.log.responds[0].body.locationLatitude, LAT);
});

// ───────── 会话内去重 ─────────

test("同一 requestGuid 成功响应后，本会话不再重复上报（即使服务端列表里还带着它）", async () => {
  const h = harness();
  assert.equal((await h.consumer.consume({ trigger: "launch" })).kind, "done");
  h.clock.nowMs += LOCATION_REQUEST_FETCH_MIN_INTERVAL_MS + 1;
  assert.deepEqual(await h.consumer.consume({ trigger: "foreground" }), { kind: "idle" });
  assert.equal(h.log.responds.length, 1);
  assert.equal(h.log.captures.length, 1, "已处理的请求不再触发取位置");
  // 新的请求照常处理
  h.state.pending = [req("r1"), req("r2")];
  h.clock.nowMs += LOCATION_REQUEST_FETCH_MIN_INTERVAL_MS + 1;
  assert.equal((await h.consumer.consume({ trigger: "foreground" })).kind, "done");
  assert.deepEqual(h.log.responds.map((item) => item.guid), ["r1", "r2"]);
});

test("reset（登出）清掉去重与节流记录", async () => {
  const h = harness();
  await h.consumer.consume({ trigger: "launch" });
  h.consumer.reset();
  assert.equal((await h.consumer.consume({ trigger: "launch" })).kind, "done", "reset 后不受 15 秒节流限制，也不再记得已处理的请求");
  assert.equal(h.log.responds.length, 2);
});

test("换账号：丢弃上一个账号的节流与去重记录", async () => {
  const h = harness();
  await h.consumer.consume({ trigger: "launch" });
  h.state.user = "user-b";
  assert.equal((await h.consumer.consume({ trigger: "launch" })).kind, "done", "新账号不受上一个账号 15 秒节流的影响");
  assert.equal(h.log.responds[1].userGuid, "user-b");
});

// ───────── 节流 ─────────

test("自动触发（冷启动 / 回前台）拉待办至少间隔 15 秒；到点后放行", async () => {
  const h = harness();
  h.state.pending = [];
  assert.equal((await h.consumer.consume({ trigger: "launch" })).kind, "idle");
  assert.deepEqual(await h.consumer.consume({ trigger: "foreground" }), { kind: "skipped", reason: "throttled" });
  h.clock.nowMs += LOCATION_REQUEST_FETCH_MIN_INTERVAL_MS - 1;
  assert.deepEqual(await h.consumer.consume({ trigger: "foreground" }), { kind: "skipped", reason: "throttled" });
  assert.equal(h.log.lists, 1, "被节流的触发不发任何请求");
  h.clock.nowMs += 1;
  assert.equal((await h.consumer.consume({ trigger: "foreground" })).kind, "idle");
  assert.equal(h.log.lists, 2);
});

test("刚收到位置请求推送与交互触发不受 15 秒限制（请求很可能是刚创建的）", async () => {
  const h = harness();
  h.state.pending = [];
  await h.consumer.consume({ trigger: "launch" });
  h.state.pending = [req("fresh")];
  const received = await h.consumer.consume({ trigger: "push-received" });
  assert.equal(received.kind, "done", "刚 launch 过，收到推送仍然立即检查");
  h.state.pending = [req("tapped")];
  assert.equal((await h.consumer.consume({ trigger: "push-tap" })).kind, "done");
  h.state.pending = [req("inbox")];
  assert.equal((await h.consumer.consume({ trigger: "inbox-tap" })).kind, "done");
  assert.deepEqual(h.log.captures, [false, true, true], "只有点推送 / 点收件箱通知是交互模式");
});

// ───────── in-flight 锁 ─────────

test("同一时刻只允许一轮在跑：在跑时的自动触发直接放弃，不重复请求", async () => {
  const gate = deferred<AttendanceLocationRequest[]>();
  const h = harness({ pending: () => gate.promise });
  const first = h.consumer.consume({ trigger: "launch" });
  await tick();
  assert.deepEqual(await h.consumer.consume({ trigger: "foreground" }), { kind: "skipped", reason: "in-flight" });
  assert.deepEqual(await h.consumer.consume({ trigger: "launch" }), { kind: "skipped", reason: "in-flight" });
  assert.equal(h.log.lists, 1);
  gate.resolve([]);
  assert.equal((await first).kind, "idle");
  await tick();
  assert.equal(h.log.lists, 1, "自动触发不会排队补跑");
});

test("在跑期间收到推送 / 员工点通知：不能被吞掉，这一轮结束后补跑（交互优先于非交互）", async () => {
  const gate = deferred<AttendanceLocationRequest[]>();
  let call = 0;
  const h = harness({
    pending: () => {
      call += 1;
      return call === 1 ? gate.promise : Promise.resolve([req("new-request")]);
    },
    capture: async (interactive) => (interactive ? { status: "ok", location: location() } : { status: "permission-not-granted" }),
  });
  const first = h.consumer.consume({ trigger: "launch" });
  await tick();
  // 在跑期间：先收到推送（非交互），再点了通知（交互）——补跑必须按交互来
  assert.deepEqual(await h.consumer.consume({ trigger: "push-received" }), { kind: "skipped", reason: "in-flight" });
  assert.deepEqual(await h.consumer.consume({ trigger: "push-tap" }), { kind: "skipped", reason: "in-flight" });
  gate.resolve([]);
  await first;
  // 补跑是即发即弃：等它走完
  for (let i = 0; i < 20 && h.log.responds.length === 0; i += 1) await tick();
  assert.equal(h.log.lists, 2, "补跑了一次");
  assert.deepEqual(h.log.captures, [true], "补跑按交互模式取位置（允许走权限申请）");
  assert.deepEqual(h.log.responds.map((item) => item.guid), ["new-request"]);
});

test("在跑期间只收到非交互的推送：补跑按非交互；补跑只发生一次", async () => {
  const gate = deferred<AttendanceLocationRequest[]>();
  let call = 0;
  const h = harness({
    pending: () => {
      call += 1;
      return call === 1 ? gate.promise : Promise.resolve([]);
    },
  });
  const first = h.consumer.consume({ trigger: "launch" });
  await tick();
  await h.consumer.consume({ trigger: "push-received" });
  await h.consumer.consume({ trigger: "push-received" });
  gate.resolve([]);
  await first;
  await tick();
  await tick();
  assert.equal(h.log.lists, 2, "多次推送只补跑一轮");
});

test("拉取待办抛错也会释放锁，下次触发还能再跑", async () => {
  let fail = true;
  const h = harness({
    pending: async () => {
      if (fail) throw httpError(503);
      return [req("r1")];
    },
  });
  assert.deepEqual(await h.consumer.consume({ trigger: "launch" }), { kind: "failed", stage: "list" });
  fail = false;
  assert.equal((await h.consumer.consume({ trigger: "push-received" })).kind, "done");
});

// ───────── 权限：非交互静默 / 交互申请 ─────────

test("非交互 + 权限未授予：静默跳过（不弹框、不上报、不提示），请求仍留待办；之后点通知走交互路径", async () => {
  const h = harness({
    capture: async (interactive) => (interactive ? { status: "ok", location: location() } : { status: "permission-not-granted" }),
  });
  assert.deepEqual(await h.consumer.consume({ trigger: "launch" }), { kind: "skipped", reason: "permission-not-granted" });
  assert.deepEqual(h.log.captures, [false]);
  assert.equal(h.log.responds.length, 0);
  assert.deepEqual(h.log.notices, []);
  assert.deepEqual(h.log.warnings, []);

  h.clock.nowMs += 1000;
  assert.equal((await h.consumer.consume({ trigger: "push-tap" })).kind, "done", "员工点了推送：交互模式，不受节流限制");
  assert.deepEqual(h.log.captures, [false, true]);
  assert.equal(h.log.responds.length, 1);
});

test("交互模式没取到位置（拒绝权限 / 定位不可用 / 超时）：提示员工；自动触发同样失败则不打扰", async () => {
  for (const status of [{ status: "permission-denied" }, { status: "unavailable", reason: "error" }, { status: "unavailable", reason: "timeout" }] as LocationCaptureOutcome[]) {
    const h = harness({ capture: async () => status });
    assert.deepEqual(await h.consumer.consume({ trigger: "push-tap" }), { kind: "failed", stage: "location" });
    assert.deepEqual(h.log.notices, [{ kind: "location-unavailable" }], JSON.stringify(status));
    assert.equal(h.log.responds.length, 0);

    const quiet = harness({ capture: async () => status });
    assert.deepEqual(await quiet.consumer.consume({ trigger: "foreground" }), { kind: "failed", stage: "location" });
    assert.deepEqual(quiet.log.notices, [], "自动触发取位置失败不打扰员工");
    // 失败不记入已处理：请求仍是待办，下次触发再试
    assert.equal(quiet.log.warnings.length, 1);
  }
});

test("审核态取位置结果 review-skipped：不上报", async () => {
  const h = harness({ capture: async () => ({ status: "review-skipped" }) });
  assert.deepEqual(await h.consumer.consume({ trigger: "launch" }), { kind: "skipped", reason: "not-eligible" });
  assert.equal(h.log.responds.length, 0);
});

// ───────── 采集时间复核 ─────────

test("取到的点早于请求创建时间 30 秒以上（缓存旧点）：不上报、不消耗请求，留给下次触发重新取点", async () => {
  let stale = true;
  const h = harness({
    capture: async () => ({
      status: "ok",
      location: location({ locationCapturedAtUtc: stale ? "2026-11-19T01:04:00.000Z" : "2026-11-19T01:10:05.000Z" }),
    }),
  });
  assert.deepEqual(await h.consumer.consume({ trigger: "launch" }), { kind: "failed", stage: "location" });
  assert.equal(h.log.responds.length, 0);
  stale = false;
  h.clock.nowMs += LOCATION_REQUEST_FETCH_MIN_INTERVAL_MS + 1;
  assert.equal((await h.consumer.consume({ trigger: "foreground" })).kind, "done", "请求没有被消耗掉，重新取到新点后可以响应");
});

test("多条待办里只有一部分点够新：够新的照常上报，其余留待下次", async () => {
  const h = harness({ capture: async () => ({ status: "ok", location: location({ locationCapturedAtUtc: "2026-11-19T01:06:00.000Z" }) }) });
  h.state.pending = [
    req("old-request", { createdAtUtc: "2026-11-19T01:00:00Z" }),
    req("new-request", { createdAtUtc: "2026-11-19T01:08:00Z" }),
  ];
  const outcome = await h.consumer.consume({ trigger: "launch" });
  assert.deepEqual(outcome, { kind: "done", responded: 1, discarded: 0, retryLater: 1 });
  assert.deepEqual(h.log.responds.map((item) => item.guid), ["old-request"]);
});

// ───────── 终态 / 可重试 ─────────

test("终态错误码：静默丢弃（不提示员工），本会话不再重试，日志只有短码", async () => {
  for (const code of TERMINAL_RESPOND_ERROR_CODES) {
    const h = harness({
      respond: async () => {
        throw businessError(code);
      },
    });
    assert.deepEqual(await h.consumer.consume({ trigger: "launch" }), { kind: "done", responded: 0, discarded: 1, retryLater: 0 }, code);
    assert.deepEqual(h.log.notices, [], `${code} 不提示员工`);
    assert.equal(h.log.warnings.length, 1);
    assert.equal(h.log.warnings[0].detail?.code, code);

    // 下次触发：同一请求不再响应（服务端列表即使还带着它）
    h.clock.nowMs += LOCATION_REQUEST_FETCH_MIN_INTERVAL_MS + 1;
    assert.deepEqual(await h.consumer.consume({ trigger: "foreground" }), { kind: "idle" }, code);
    assert.equal(h.log.responds.length, 1, `${code} 没有重试`);
  }
});

test("网络错误 / 5xx / 响应格式异常：不记为已处理，下次触发时重试；本轮不自己循环重试", async () => {
  for (const failure of [httpError(undefined), httpError(500), httpError(503), new LocationRequestResponseError("bad")]) {
    let fail = true;
    const h = harness({
      respond: async (guid) => {
        if (fail) throw failure;
        return req(guid, { status: "Responded" });
      },
    });
    const first = await h.consumer.consume({ trigger: "launch" });
    assert.deepEqual(first, { kind: "done", responded: 0, discarded: 0, retryLater: 1 });
    assert.equal(h.log.responds.length, 1, "本轮只尝试一次，没有自己循环重试");
    assert.deepEqual(h.log.notices, []);

    fail = false;
    h.clock.nowMs += LOCATION_REQUEST_FETCH_MIN_INTERVAL_MS + 1;
    assert.equal((await h.consumer.consume({ trigger: "foreground" })).kind, "done");
    assert.equal(h.log.responds.length, 2, "下次触发重试成功");
    assert.deepEqual(h.log.notices.length, 1);
  }
});

test("多条待办中间一条被判终态：丢弃它，后面的照常上报；遇到网络错误则停止后面的", async () => {
  const mixed = harness({
    respond: async (guid) => {
      if (guid === "b") throw businessError("LOCATION_REQUEST_EXPIRED");
      return req(guid, { status: "Responded" });
    },
  });
  mixed.state.pending = [
    req("a", { createdAtUtc: "2026-11-19T01:01:00Z" }),
    req("b", { createdAtUtc: "2026-11-19T01:02:00Z" }),
    req("c", { createdAtUtc: "2026-11-19T01:03:00Z" }),
  ];
  assert.deepEqual(await mixed.consumer.consume({ trigger: "launch" }), { kind: "done", responded: 2, discarded: 1, retryLater: 0 });
  assert.deepEqual(mixed.log.notices, [{ kind: "reported-many", count: 2 }]);

  const offline = harness({
    respond: async (guid) => {
      if (guid === "b") throw httpError(undefined);
      return req(guid, { status: "Responded" });
    },
  });
  offline.state.pending = [
    req("a", { createdAtUtc: "2026-11-19T01:01:00Z" }),
    req("b", { createdAtUtc: "2026-11-19T01:02:00Z" }),
    req("c", { createdAtUtc: "2026-11-19T01:03:00Z" }),
  ];
  assert.deepEqual(await offline.consumer.consume({ trigger: "launch" }), { kind: "done", responded: 1, discarded: 0, retryLater: 2 });
  assert.deepEqual(offline.log.responds.map((item) => item.guid), ["a", "b"], "网络中断后不再尝试 c");
});

test("幂等重放（alreadyResponded）按成功处理并提示", async () => {
  const h = harness({ respond: async (guid) => req(guid, { status: "Responded", alreadyResponded: true }) });
  assert.equal((await h.consumer.consume({ trigger: "launch" })).kind, "done");
  assert.equal(h.log.notices.length, 1);
});

test("待办拉取失败：按失败处理（不当成没有待办），不取位置；账号切换打断不记日志", async () => {
  const net = harness({
    pending: async () => {
      throw httpError(undefined);
    },
  });
  assert.deepEqual(await net.consumer.consume({ trigger: "launch" }), { kind: "failed", stage: "list" });
  assert.deepEqual(net.log.captures, []);
  assert.equal(net.log.warnings.length, 1);

  const malformed = harness({
    pending: async () => {
      throw new LocationRequestResponseError("not a list");
    },
  });
  assert.deepEqual(await malformed.consumer.consume({ trigger: "launch" }), { kind: "failed", stage: "list" });

  const switched = harness({
    pending: async () => {
      throw Object.assign(new Error("ACCOUNT_SESSION_CHANGED"), { code: "ACCOUNT_SESSION_CHANGED" });
    },
  });
  assert.deepEqual(await switched.consumer.consume({ trigger: "launch" }), { kind: "skipped", reason: "not-eligible" });
  assert.deepEqual(switched.log.warnings, []);
});

// ───────── 账号切换中途 ─────────

test("取位置期间账号被切换 / 登出：不再上报", async () => {
  const gate = deferred<LocationCaptureOutcome>();
  const h = harness({ capture: () => gate.promise });
  const run = h.consumer.consume({ trigger: "push-tap" });
  await tick();
  await tick();
  h.state.user = null;
  gate.resolve({ status: "ok", location: location() });
  assert.deepEqual(await run, { kind: "skipped", reason: "not-eligible" });
  assert.equal(h.log.responds.length, 0);
  assert.deepEqual(h.log.notices, []);
});

test("逐条上报期间账号被切换：停止后面的，不提示", async () => {
  let responds = 0;
  const h = harness({
    respond: async (guid) => {
      responds += 1;
      h.state.user = null;
      return req(guid, { status: "Responded" });
    },
  });
  h.state.pending = [
    req("a", { createdAtUtc: "2026-11-19T01:01:00Z" }),
    req("b", { createdAtUtc: "2026-11-19T01:02:00Z" }),
  ];
  const outcome = await h.consumer.consume({ trigger: "launch" });
  assert.equal(responds, 1, "第一条之后账号已变，不再上报第二条");
  assert.equal(outcome.kind, "done");
  assert.deepEqual(h.log.notices, [], "账号已变不再提示");
});

// ───────── 隐私 ─────────

test("日志与提示不含坐标", async () => {
  const h = harness({
    respond: async () => {
      throw businessError("LOCATION_STALE");
    },
  });
  await h.consumer.consume({ trigger: "launch" });
  const h2 = harness();
  await h2.consumer.consume({ trigger: "launch" });
  const serialized = JSON.stringify([h.log.warnings, h.log.notices, h2.log.warnings, h2.log.notices]);
  assert.equal(serialized.includes(String(LAT)), false);
  assert.equal(serialized.includes(String(LNG)), false);
  assert.equal(/latitude|longitude/i.test(serialized), false);
});

test("消费永不向外抛错：依赖意外抛异常也只返回失败结果", async () => {
  const h = harness({
    capture: async () => {
      throw new Error("native crash");
    },
  });
  const outcome = await h.consumer.consume({ trigger: "push-tap" });
  assert.equal(outcome.kind, "failed");
  // 锁已释放
  const again = await h.consumer.consume({ trigger: "push-tap" });
  assert.notDeepEqual(again, { kind: "skipped", reason: "in-flight" });
});

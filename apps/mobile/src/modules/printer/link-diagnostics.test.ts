import assert from "node:assert/strict";
import { test } from "node:test";
import {
  appendPendingLinkLog,
  buildAppStartFields,
  createPrinterLinkRecorder,
  describeLinkError,
  EMPTY_LINK_LAST_SEEN,
  fitLinkEventsToBudget,
  installPrinterLinkRecorder,
  LINK_CONTEXT_MAX,
  LINK_DIAGNOSTICS_STORAGE_KEY,
  LINK_EVENTS_BUDGET_CHARS,
  LINK_EVENTS_MIN_KEPT,
  LINK_HEAD_CAP,
  LINK_SNAPSHOT_TAIL,
  MAX_PENDING_LINK_LOGS,
  nextLinkLastSeen,
  parseLinkLastSeen,
  parsePendingLinkLogs,
  pickLinkContext,
  recordPrinterLink,
  recordPrinterNativeStatus,
  recordPrinterPrintSuccess,
  uploadPendingPrinterLinkLogs,
  type PrinterLinkEvent,
  type PrinterLinkLogItem,
  type PrinterLinkRecorder,
  type PrinterLinkStorage,
} from "./link-diagnostics";

type LoggedEvent = { t: number; kind: string; [key: string]: unknown };

function setup() {
  const emitted: PrinterLinkLogItem[] = [];
  let clock = 5_000_000;
  let ids = 0;
  const recorder = createPrinterLinkRecorder({
    now: () => clock,
    newId: () => `00000000-0000-4000-8000-${String((ids += 1)).padStart(12, "0")}`,
    emit: (item) => emitted.push(item),
    context: () => ({ environment: "test", appVersion: "1.0.10+63", properties: { platform: "android" } }),
  });
  return { emitted, recorder, advance: (ms: number) => { clock += ms; } };
}

function fail(recorder: PrinterLinkRecorder, message = "read failed", elapsedMs = 10_000) {
  recorder.record("connect.start", { role: "label", trigger: "auto", address: "AA:BB" });
  recorder.record("connect.fail", {
    role: "label", trigger: "auto", address: "AA:BB", transport: "classic", code: "CONNECT_ERROR", message, elapsedMs,
  });
}

const eventsOf = (item: PrinterLinkLogItem) => item.properties.events as LoggedEvent[];

test("连续失败只在 3/10/30 次里程碑各上报一次，错误按文本聚合并记录耗时范围", () => {
  const { emitted, recorder, advance } = setup();
  for (let attempt = 1; attempt <= 12; attempt += 1) {
    fail(recorder, attempt === 2 ? "Service discovery failed" : "read failed", attempt * 1000);
    advance(5_000);
  }
  assert.equal(emitted.length, 2, "第 3、10 次各一条；中间连续失败不重复上报");
  assert.deepEqual(emitted.map((item) => item.properties.failures), [3, 10]);
  assert.ok(emitted.every((item) => item.level === "Warning" && item.category === "printer.link"));
  assert.ok(emitted.every((item) => item.sourceType === "Mobile" && item.serviceName === "HbwebExpoApp"));
  assert.equal(emitted[0].appVersion, "1.0.10+63");
  assert.equal(emitted[0].properties.platform, "android");
  assert.equal(emitted[0].properties.address, "AA:BB");
  assert.equal(emitted[0].properties.transport, "classic");
  // 第 10 次失败时的快照只统计前 10 次：第 2 次是另一种错误，其余 9 次耗时 1s、3s…10s。
  assert.deepEqual(emitted[1].properties.errors, [
    { code: "CONNECT_ERROR", message: "read failed", count: 9, minElapsedMs: 1000, maxElapsedMs: 10_000 },
    { code: "CONNECT_ERROR", message: "Service discovery failed", count: 1, minElapsedMs: 2000, maxElapsedMs: 2000 },
  ]);
  assert.equal(new Set(emitted.map((item) => item.clientEventId)).size, 2, "每条日志的 ClientEventId 唯一");
});

test("断线后恢复：失败不足 2 次且很快恢复不上报，失败 2 次或超过 60 秒才上报", () => {
  const quick = setup();
  quick.recorder.record("link.lost", { source: "native" });
  fail(quick.recorder);
  quick.advance(3_000);
  quick.recorder.record("connect.ok", { role: "label", address: "AA:BB", elapsedMs: 800 });
  assert.equal(quick.emitted.length, 0, "一次失败后即恢复属于正常抖动，不上报");

  const twice = setup();
  fail(twice.recorder);
  fail(twice.recorder);
  twice.recorder.record("connect.ok", { role: "label", address: "AA:BB", elapsedMs: 900 });
  assert.equal(twice.emitted.length, 1);
  assert.equal(twice.emitted[0].level, "Information", "失败不足 3 次的恢复只是信息级");
  assert.equal(twice.emitted[0].properties.phase, "recovered");
  assert.equal(twice.emitted[0].properties.failures, 2);

  const slow = setup();
  slow.recorder.record("link.lost", { source: "print", code: "PRINT_ERROR" });
  slow.advance(61_000);
  slow.recorder.record("connect.ok", { role: "label", address: "AA:BB", elapsedMs: 700 });
  assert.equal(slow.emitted.length, 1, "断线超过 60 秒才恢复，即使没有失败也要留痕");
  assert.equal(slow.emitted[0].properties.failures, 0);
  assert.equal(slow.emitted[0].properties.durationMs, 61_000);
});

test("恢复后故障清零，下一次断线重新计数并可再次上报", () => {
  const { emitted, recorder } = setup();
  for (let i = 0; i < 3; i += 1) fail(recorder);
  recorder.record("connect.ok", { role: "label", address: "AA:BB" });
  assert.deepEqual(emitted.map((item) => item.properties.phase), ["failing", "recovered"]);
  for (let i = 0; i < 3; i += 1) fail(recorder);
  assert.equal(emitted.length, 3, "新一轮故障重新从第 3 次开始上报");
  assert.notEqual(emitted[0].properties.episodeId, emitted[2].properties.episodeId);
});

test("用户暂停自动重连：暂停期间的断线不算故障，暂停也终止正在追踪的故障", () => {
  const { emitted, recorder } = setup();
  fail(recorder);
  fail(recorder);
  recorder.record("auto.reconnect", { paused: true });
  recorder.record("connect.ok", { role: "label", address: "AA:BB" });
  assert.equal(emitted.length, 0, "暂停后不再把这次故障上报为恢复");

  recorder.record("link.lost", { source: "native" });
  for (let i = 0; i < 3; i += 1) recorder.record("connect.ok", { role: "label" });
  assert.equal(emitted.length, 0, "暂停期间的断线不开启故障");

  recorder.record("auto.reconnect", { paused: false });
  recorder.record("link.lost", { source: "native" });
  for (let i = 0; i < 2; i += 1) fail(recorder);
  recorder.record("connect.ok", { role: "label" });
  assert.equal(emitted.length, 1, "恢复自动重连后的断线照常追踪");
});

test("小票机的连接事件只作上下文，不计入标签打印机故障", () => {
  const { emitted, recorder } = setup();
  for (let i = 0; i < 5; i += 1) {
    recorder.record("connect.fail", { role: "receipt", trigger: "receipt-test", code: "CONNECT_ERROR", message: "x" });
  }
  assert.equal(emitted.length, 0);
  for (let i = 0; i < 3; i += 1) fail(recorder);
  assert.equal(emitted.length, 1);
  assert.ok(
    eventsOf(emitted[0]).some((event) => event.role === "receipt"),
    "小票机事件仍保留在上下文里，便于看出连接被占用",
  );
  assert.equal(emitted[0].properties.failures, 3);
});

test("原生状态去重：重复轮询不产生事件，标签打印机已连接变为断开记为断线", () => {
  const { emitted, recorder, advance } = setup();
  const connected = { supported: true, enabled: true, connected: true, address: "AA:BB" };
  recorder.recordNativeStatus(connected, "AA:BB");
  recorder.recordNativeStatus(connected, "AA:BB");
  recorder.recordNativeStatus({ ...connected }, "AA:BB");
  // 蓝牙关闭 + 断线：原生层先报 connected=false。
  recorder.recordNativeStatus({ supported: true, enabled: false, connected: false, address: null }, "AA:BB");
  recorder.recordNativeStatus({ supported: true, enabled: false, connected: false, address: null }, "AA:BB");
  advance(70_000);
  recorder.record("connect.ok", { role: "label", address: "AA:BB" });

  assert.equal(emitted.length, 1);
  const kinds = eventsOf(emitted[0]).map((event) => event.kind);
  assert.deepEqual(kinds.filter((kind) => kind === "native.status").length, 2, "相同状态不重复记录");
  assert.ok(kinds.includes("link.lost"));
  const nativeEvents = eventsOf(emitted[0]).filter((event) => event.kind === "native.status");
  assert.equal(nativeEvents[1].enabled, false, "蓝牙被关闭要能从日志看出");
  assert.equal(nativeEvents[1].labelConnected, false);
});

test("小票测试临时占用 socket：先暂停自动重连，标签连接被替换不算故障", () => {
  const { emitted, recorder, advance } = setup();
  recorder.recordNativeStatus({ supported: true, enabled: true, connected: true, address: "AA:BB" }, "AA:BB");
  // 与真实流程一致：testReceiptPrinterConnection 先暂停标签自动重连，再连接小票机。
  recorder.record("auto.reconnect", { paused: true });
  recorder.recordNativeStatus({ supported: true, enabled: true, connected: true, address: "RECEIPT" }, "AA:BB");
  recorder.recordNativeStatus({ supported: true, enabled: true, connected: false, address: null }, "AA:BB");
  recorder.record("auto.reconnect", { paused: false });
  advance(120_000);
  recorder.record("connect.ok", { role: "label", address: "AA:BB" });
  assert.equal(emitted.length, 0);
});

test("没有保存标签打印机时，原生连接状态变化不算标签断线", () => {
  const { emitted, recorder, advance } = setup();
  recorder.recordNativeStatus({ supported: true, enabled: true, connected: true, address: "RECEIPT" }, null);
  recorder.recordNativeStatus({ supported: true, enabled: true, connected: false, address: null }, null);
  advance(120_000);
  recorder.record("connect.ok", { role: "label" });
  assert.equal(emitted.length, 0);
});

test("环形缓冲有上限：长时间失败的快照保留开头与最近事件，序号升序且不重复", () => {
  const { emitted, recorder, advance } = setup();
  recorder.record("link.lost", { source: "native", marker: "first-event" });
  for (let i = 0; i < 120; i += 1) {
    fail(recorder, "read failed", 1000 + i);
    advance(5_000);
  }
  const last = emitted[emitted.length - 1];
  assert.equal((last.properties.failures as number) >= 100, true);
  const events = eventsOf(last);
  // 开头 = 故障前上下文（最多 LINK_CONTEXT_MAX + 1 条，含触发事件）+ LINK_HEAD_CAP 条，再加最近 LINK_SNAPSHOT_TAIL 条。
  assert.ok(events.length <= LINK_CONTEXT_MAX + 1 + LINK_HEAD_CAP + LINK_SNAPSHOT_TAIL);
  assert.equal(events[0].marker, "first-event", "开头事件（最初是怎么断的）必须保留");
  assert.equal(events[0].t, 0);
  const times = events.map((event) => event.t);
  assert.deepEqual(times, [...times].sort((a, b) => a - b));
  assert.equal(new Set(events.map((event) => JSON.stringify(event))).size, events.length);
});

test("原生事件带原始时间戳进入快照：按真实发生时间排序，不开启故障也不计入失败", () => {
  const { emitted, recorder, advance } = setup();
  recorder.record("link.lost", { source: "native" });
  advance(6_000);
  fail(recorder);
  fail(recorder);
  // 原生事件在失败之后才被取走（JS 取缓冲），但发生得更早：ACL 断开在 link.lost 之前 300 毫秒。
  recorder.record("native.diag", { ev: "acl.disconnected", address: "AA:BB", tracked: true, socketConnected: true }, 4_999_700);
  recorder.record("native.diag", { ev: "connect.error", elapsedMs: 5_450, error: "IOException: read failed" }, 5_005_900);
  fail(recorder);

  assert.equal(emitted.length, 1);
  assert.equal(emitted[0].properties.failures, 3, "原生事件不计入失败次数");
  const events = eventsOf(emitted[0]);
  const times = events.map((event) => event.t);
  assert.deepEqual(times, [...times].sort((a, b) => a - b), "必须按真实发生时间升序");
  assert.equal(events[0].kind, "native.diag");
  assert.equal(events[0].ev, "acl.disconnected");
  assert.equal(events[0].t, -300, "相对故障开始（link.lost）的毫秒，早于它所以为负");
  assert.equal(events[0].socketConnected, true);
  assert.equal("atMs" in events[0], false, "时间戳不作为字段重复输出");
  const connectError = events.find((event) => event.ev === "connect.error");
  assert.equal(connectError?.elapsedMs, 5_450);
});

test("原生事件时间戳非法时退回当前时间，仍能记录", () => {
  const { emitted, recorder } = setup();
  recorder.record("link.lost", { source: "native" });
  recorder.record("native.diag", { ev: "adapter.state", state: "off" }, Number.NaN);
  for (let i = 0; i < 3; i += 1) fail(recorder);
  const adapter = eventsOf(emitted[0]).find((event) => event.ev === "adapter.state");
  assert.ok(adapter, "非法时间戳不能让事件丢失");
  assert.equal(adapter.t, 0, "退回当前（时钟未走）时间");
  assert.equal(adapter.state, "off");
});

test("字段清洗：丢弃 undefined 与保留键，截断超长文本；emit 抛错不外溢", () => {
  const emitted: PrinterLinkLogItem[] = [];
  const recorder = createPrinterLinkRecorder({
    now: () => 1,
    newId: () => "00000000-0000-4000-8000-000000000001",
    emit: (item) => { emitted.push(item); throw new Error("upload layer exploded"); },
    context: () => ({ environment: "test" }),
  });
  const longMessage = "x".repeat(500);
  for (let i = 0; i < 3; i += 1) {
    assert.doesNotThrow(() =>
      recorder.record("connect.fail", {
        role: "label", message: longMessage, code: "E", elapsedMs: 5, t: 999, kind: "forged", dropped: undefined,
      }));
  }
  assert.equal(emitted.length, 1);
  const failEvent = eventsOf(emitted[0]).find((event) => event.kind === "connect.fail");
  assert.ok(failEvent);
  assert.equal((failEvent.message as string).length, 161, "截断到 160 字符并带省略号");
  assert.equal("dropped" in failEvent, false);
  assert.notEqual(failEvent.t, 999, "t 是相对故障开始的毫秒，字段里同名键会被丢弃");
});

test("未安装记录器时全局打点是空操作；安装后生效、卸载后恢复空操作", () => {
  assert.doesNotThrow(() => recordPrinterLink("connect.start", { role: "label" }));
  assert.doesNotThrow(() => recordPrinterNativeStatus({ supported: true, enabled: true, connected: false }, null));
  const emitted: PrinterLinkLogItem[] = [];
  installPrinterLinkRecorder({
    now: () => 1,
    newId: () => "00000000-0000-4000-8000-000000000002",
    emit: (item) => emitted.push(item),
    context: () => ({ environment: "test" }),
  });
  for (let i = 0; i < 3; i += 1) recordPrinterLink("connect.fail", { role: "label", code: "E", message: "m" });
  assert.equal(emitted.length, 1);
  installPrinterLinkRecorder(null);
  for (let i = 0; i < 3; i += 1) recordPrinterLink("connect.fail", { role: "label", code: "E", message: "m" });
  assert.equal(emitted.length, 1);
});

test("describeLinkError 兼容带 code 的原生错误、普通 Error 与非错误值", () => {
  assert.deepEqual(describeLinkError(Object.assign(new Error("boom"), { code: "CONNECT_ERROR" })), { code: "CONNECT_ERROR", message: "boom" });
  assert.deepEqual(describeLinkError(new Error("plain")), { code: null, message: "plain" });
  assert.deepEqual(describeLinkError("text"), { code: null, message: "text" });
});

// ---------------------------------------------------------------------------
// 本机暂存与补传
// ---------------------------------------------------------------------------

function item(id: number): PrinterLinkLogItem {
  return {
    clientEventId: `00000000-0000-4000-8000-${String(id).padStart(12, "0")}`,
    level: "Warning",
    message: `m${id}`,
    timestampUtc: new Date(id * 1000).toISOString(),
    environment: "test",
    sourceType: "Mobile",
    serviceName: "HbwebExpoApp",
    category: "printer.link",
    properties: { id },
  };
}

function memoryStorage(initial: PrinterLinkLogItem[]): PrinterLinkStorage & { value: string | null } {
  const storage = {
    value: JSON.stringify(initial) as string | null,
    async getString() { return storage.value; },
    async setString(_key: string, value: string) { storage.value = value; },
  };
  return storage;
}

test("暂存解析容错，追加去重并限制在后端单次上限内（丢最旧）", () => {
  assert.deepEqual(parsePendingLinkLogs(null), []);
  assert.deepEqual(parsePendingLinkLogs("not json"), []);
  assert.deepEqual(parsePendingLinkLogs(JSON.stringify([{ foo: 1 }, item(1)])).map((entry) => entry.message), ["m1"]);

  let pending: PrinterLinkLogItem[] = [];
  for (let i = 1; i <= MAX_PENDING_LINK_LOGS + 5; i += 1) pending = appendPendingLinkLog(pending, item(i));
  assert.equal(pending.length, MAX_PENDING_LINK_LOGS);
  assert.equal(pending[0].message, "m6", "超限时丢最旧的");
  assert.equal(appendPendingLinkLog(pending, item(MAX_PENDING_LINK_LOGS + 5)).length, MAX_PENDING_LINK_LOGS, "重复入队不会写两条");
});

test("补传成功清掉已发送的，网络类失败保留，服务端明确拒收才丢弃", async () => {
  const uploaded: string[][] = [];
  const storage = memoryStorage([item(1), item(2)]);
  assert.equal(
    await uploadPendingPrinterLinkLogs({ storage, upload: async (logs) => { uploaded.push(logs.map((entry) => entry.message)); } }),
    "uploaded",
  );
  assert.deepEqual(uploaded, [["m1", "m2"]]);
  assert.deepEqual(parsePendingLinkLogs(storage.value), []);
  assert.equal(await uploadPendingPrinterLinkLogs({ storage, upload: async () => { throw new Error("must not be called"); } }), "skipped");

  for (const status of [undefined, 401, 404, 429, 500, 503]) {
    const kept = memoryStorage([item(1)]);
    const before = kept.value;
    const result = await uploadPendingPrinterLinkLogs({
      storage: kept,
      upload: async () => { throw status === undefined ? new Error("Network Error") : Object.assign(new Error("http"), { response: { status } }); },
    });
    assert.equal(result, "kept", `status=${status} 应保留下次再传`);
    assert.equal(kept.value, before);
  }

  for (const status of [400, 403]) {
    const dropped = memoryStorage([item(1)]);
    const result = await uploadPendingPrinterLinkLogs({
      storage: dropped,
      upload: async () => { throw Object.assign(new Error("http"), { response: { status } }); },
    });
    assert.equal(result, "dropped", `status=${status} 服务端明确拒收`);
    assert.deepEqual(parsePendingLinkLogs(dropped.value), []);
  }
});

test("暂存里的日志按批次补传，只清除本批已发送的", async () => {
  const many = Array.from({ length: MAX_PENDING_LINK_LOGS }, (_, index) => item(index + 1));
  const storage = memoryStorage(many);
  let batchSize = 0;
  await uploadPendingPrinterLinkLogs({ storage, upload: async (logs) => { batchSize = logs.length; } });
  assert.equal(batchSize, MAX_PENDING_LINK_LOGS);
  assert.deepEqual(parsePendingLinkLogs(storage.value), []);
  assert.equal(typeof LINK_DIAGNOSTICS_STORAGE_KEY, "string");
});

// ---------------------------------------------------------------------------
// 补断线前的日志（C1～C6）：会话摘要、断线原因、上下文、上报量、冷启动标记
// ---------------------------------------------------------------------------

function triggerThreeFailures(recorder: PrinterLinkRecorder, advance: (ms: number) => void) {
  for (let i = 0; i < 3; i += 1) {
    fail(recorder);
    advance(5_000);
  }
}

test("断线时补上会话摘要：连了多久、距上次打印多久、断开原因，故障日志带 session", () => {
  const { emitted, recorder, advance } = setup();
  const start = 5_000_000;
  recorder.record("connect.ok", { role: "label", address: "AA:BB" });
  advance(60_000);
  recorder.recordPrintSuccess();
  advance(60_000);
  recorder.recordPrintSuccess();
  advance(180_000); // 最后一次打印后空闲 3 分钟，打印机可能已自动休眠
  recorder.record("app.state", { state: "background" });
  advance(10_000);
  recorder.record("native.diag", { ev: "acl.disconnected", tracked: true });
  recorder.record("native.diag", { ev: "connection.cleared", reason: "acl_disconnected" });
  recorder.record("link.lost", { source: "native", bluetoothEnabled: true });
  triggerThreeFailures(recorder, advance);

  assert.equal(emitted.length, 1);
  const lost = eventsOf(emitted[0]).find((event) => event.kind === "link.lost")!;
  assert.equal(lost.connectedForMs, 310_000);
  assert.equal(lost.idleSincePrintMs, 190_000);
  assert.equal(lost.printsInSession, 2);
  assert.equal(lost.endReason, "acl_disconnected");
  assert.equal(lost.appState, "background", "断线时 App 在后台（息屏 / 切走）");
  assert.equal(lost.appStateForMs, 10_000);
  assert.deepEqual(emitted[0].properties.session, {
    endedAtUtc: new Date(start + 310_000).toISOString(),
    connectedForMs: 310_000,
    idleSincePrintMs: 190_000,
    printsInSession: 2,
    endReason: "acl_disconnected",
    appState: "background",
    appStateForMs: 10_000,
  });
});

test("会话摘要：没打印过则 idleSincePrintMs 为 null；冷启动没有会话则各项为 null；连上后清掉不带进下一次故障", () => {
  const { emitted, recorder, advance } = setup();
  // 没有已连接的会话就断线（如冷启动后一直没连上）：不能编造「连了多久」。
  recorder.record("link.lost", { source: "native", bluetoothEnabled: true });
  triggerThreeFailures(recorder, advance);
  assert.deepEqual(
    (emitted[0].properties.session as Record<string, unknown>),
    {
      endedAtUtc: new Date(5_000_000).toISOString(),
      connectedForMs: null,
      idleSincePrintMs: null,
      printsInSession: null,
      endReason: "unknown",
      appState: null,
      appStateForMs: null,
    },
  );

  // 连上（恢复日志带上旧摘要）→ 新会话没打印过就断线：idleSincePrintMs 为 null，printsInSession 为 0。
  recorder.record("connect.ok", { role: "label" });
  assert.ok(emitted[1].properties.session, "恢复日志仍带断线时的会话摘要");
  advance(30_000);
  recorder.record("link.lost", { source: "native", bluetoothEnabled: true });
  const second = emitted.length;
  triggerThreeFailures(recorder, advance);
  const session = emitted[second].properties.session as Record<string, unknown>;
  assert.equal(session.connectedForMs, 30_000);
  assert.equal(session.idleSincePrintMs, null);
  assert.equal(session.printsInSession, 0);

  // 再次连上并正常恢复后，下一次「没有会话」的故障不能带着上一次的摘要。
  recorder.record("connect.ok", { role: "label" });
  const afterRecover = emitted.length;
  fail(recorder);
  fail(recorder);
  fail(recorder);
  assert.equal(emitted.length, afterRecover + 1);
  assert.equal(emitted[afterRecover].properties.session, undefined, "没有断线事件时不带 session");
});

test("断线原因：打印失败 / 蓝牙关闭 / GATT 断开 / 连接被清理的原因各自识别，优先级 蓝牙关闭 > 清理原因", () => {
  const reasonOf = (setupEvents: (recorder: PrinterLinkRecorder) => void, lost: Record<string, string | boolean> = { source: "native", bluetoothEnabled: true }) => {
    const { emitted, recorder, advance } = setup();
    recorder.record("connect.ok", { role: "label" });
    advance(1_000);
    setupEvents(recorder);
    recorder.record("link.lost", lost);
    triggerThreeFailures(recorder, advance);
    return (emitted[0].properties.session as Record<string, unknown>).endReason;
  };
  assert.equal(reasonOf(() => undefined, { source: "print" }), "print_failed");
  assert.equal(reasonOf(() => undefined, { source: "native", bluetoothEnabled: false }), "bluetooth_off");
  assert.equal(reasonOf((r) => r.record("native.diag", { ev: "adapter.state", state: "turning_off" })), "bluetooth_off");
  assert.equal(reasonOf((r) => r.record("native.diag", { ev: "connection.cleared", reason: "ble_lost" })), "ble_lost");
  assert.equal(reasonOf((r) => r.record("native.diag", { ev: "acl.disconnected" })), "acl_disconnected");
  assert.equal(reasonOf((r) => r.record("native.diag", { ev: "ble.gatt.state", status: 8, newState: 0 })), "gatt_disconnected");
  assert.equal(reasonOf((r) => {
    r.record("native.diag", { ev: "connection.cleared", reason: "acl_disconnected" });
    r.record("native.diag", { ev: "adapter.state", state: "off" });
  }), "bluetooth_off");
  assert.equal(reasonOf(() => undefined), "unknown", "原生没有任何线索时如实记 unknown，不猜");
});

test("故障前上下文：自上一次连接成功之后的全部事件，超过上限时优先保留断线类事件，且不带更早的事件", () => {
  const { emitted, recorder, advance } = setup();
  recorder.record("app.state", { state: "active", marker: "before-ok" });
  recorder.record("connect.ok", { role: "label", marker: "session-start" });
  recorder.record("native.diag", { ev: "acl.disconnected", marker: "early-acl" });
  for (let i = 0; i < 40; i += 1) recorder.record("native.status", { supported: true, enabled: true, connected: true, filler: i });
  recorder.record("link.lost", { source: "native", bluetoothEnabled: true });
  triggerThreeFailures(recorder, advance);

  const events = eventsOf(emitted[0]);
  const markers = events.map((event) => event.marker);
  assert.ok(!markers.includes("before-ok"), "上一次连接成功之前的事件不属于这次故障的上下文");
  assert.ok(markers.includes("session-start") && markers.includes("early-acl"), "早期的连接成功与 ACL 断开属于断线类事件，要保留");
  const fillers = events.filter((event) => typeof event.filler === "number");
  assert.ok(fillers.length < 40, "普通状态事件被截断");
  assert.ok(events.length <= LINK_CONTEXT_MAX + 1 + LINK_HEAD_CAP + LINK_SNAPSHOT_TAIL);
});

test("没有上一次连接成功（如冷启动）时，上下文仍只带最近几条，行为与以前一致", () => {
  const { emitted, recorder, advance } = setup();
  for (let i = 0; i < 12; i += 1) recorder.record("app.state", { state: "active", seq: i });
  triggerThreeFailures(recorder, advance);
  const seqs = eventsOf(emitted[0]).filter((event) => event.kind === "app.state").map((event) => event.seq);
  // 开头上下文 = 失败前最近 LINK_CONTEXT_BEFORE 条 + 触发事件；更早的 app.state 不进来（会被 head 以外的环形缓冲尾部覆盖，尾部 30 条内仍有）
  assert.ok(seqs.length > 0);
  assert.equal(eventsOf(emitted[0])[0].t <= 0, true);
});

test("上报量控制：events 超过字符预算时从中间丢事件，保留开头与最近的，并标记 eventsTrimmed", () => {
  const { emitted, recorder, advance } = setup();
  const wide = (tag: string) => Object.fromEntries(
    Array.from({ length: 4 }, (_, index) => [`f${index}`, `${tag}-${"x".repeat(150)}`]),
  );
  recorder.record("link.lost", { source: "native", marker: "first-event", ...wide("a") });
  for (let i = 0; i < 40; i += 1) {
    recorder.record("native.diag", { ev: "connect.begin", marker: `m${i}`, ...wide("b") });
  }
  triggerThreeFailures(recorder, advance);
  const item = emitted[0];
  const events = eventsOf(item);
  assert.ok(JSON.stringify(events).length <= LINK_EVENTS_BUDGET_CHARS, "序列化后不超过预算");
  assert.ok((item.properties.eventsTrimmed as number) > 0, "被丢弃的条数要如实标出");
  assert.equal(events[0].marker, "first-event", "开头保留");
  assert.ok(events.length >= LINK_EVENTS_MIN_KEPT);
  assert.equal(events[events.length - 1].kind, "connect.fail", "最近的事件保留");
});

test("fitLinkEventsToBudget 与 pickLinkContext：纯函数边界", () => {
  const small = [{ a: 1 }, { a: 2 }];
  assert.deepEqual(fitLinkEventsToBudget(small, 1_000), { events: small, dropped: 0 });
  const big = Array.from({ length: 30 }, (_, index) => ({ index, pad: "y".repeat(100) }));
  const fitted = fitLinkEventsToBudget(big, 800, 5);
  assert.ok(JSON.stringify(fitted.events).length <= 800 || fitted.events.length === 5);
  assert.equal(fitted.events[0].index, 0);
  assert.equal(fitted.events[fitted.events.length - 1].index, 29);
  assert.equal(fitted.dropped, 30 - fitted.events.length);

  const mk = (seq: number, kind: PrinterLinkEvent["kind"], fields: PrinterLinkEvent["fields"] = {}): PrinterLinkEvent => ({ seq, atMs: seq, kind, fields });
  const events = [
    mk(1, "connect.ok"),
    ...Array.from({ length: 10 }, (_, i) => mk(2 + i, "native.status")),
    mk(12, "native.diag", { ev: "acl.disconnected" }),
    ...Array.from({ length: 10 }, (_, i) => mk(13 + i, "native.status")),
  ];
  const picked = pickLinkContext(events, 10);
  assert.ok(picked.length <= 10);
  assert.ok(picked.some((event) => event.seq === 1) || picked.some((event) => event.seq === 12), "更早的断线类事件优先保留");
  assert.equal(picked[picked.length - 1].seq, 22, "最近的事件一定保留");
  assert.equal(pickLinkContext(events.slice(0, 3), 10).length, 3, "未超上限时原样返回");
});

test("自动重连阶段事件（phase）不改写暂停状态：暂停期间仍不开故障；不产生额外日志条数", () => {
  const { emitted, recorder, advance } = setup();
  recorder.record("auto.reconnect", { paused: true });
  recorder.record("auto.reconnect", { phase: "probe_fast", failures: 10, nextInMs: 60_000 });
  recorder.record("link.lost", { source: "native", bluetoothEnabled: true });
  advance(100_000);
  // 暂停期间的断线不开故障：阶段事件没有把暂停状态改回 false。若被改回，故障会从上面那条 link.lost 起算。
  recorder.record("auto.reconnect", { paused: false });
  triggerThreeFailures(recorder, advance);
  assert.equal(emitted.length, 1);
  assert.equal(
    emitted[0].properties.startedAtUtc,
    new Date(5_000_000 + 100_000).toISOString(),
    "故障从恢复后的第一次连接失败起算，而不是暂停期间的断线",
  );

  const before = emitted.length;
  recorder.record("auto.reconnect", { phase: "probe_slow", failures: 20, nextInMs: 300_000 });
  recorder.record("auto.reconnect", { phase: "resumed", failures: 20, resumeReason: "foreground" });
  assert.equal(emitted.length, before, "阶段事件本身不单独生成日志，只作为上下文");
});

test("故障日志带最近一次冷启动信息与阶段事件（作为上下文随里程碑上报）", () => {
  const { emitted, recorder, advance } = setup();
  recorder.record("app.start", buildAppStartFields({ lastConnectedAtMs: 4_990_000, lastSeenAtMs: 4_999_000, wasConnected: true }, 5_000_000));
  recorder.record("auto.reconnect", { phase: "probe_fast", failures: 10, nextInMs: 60_000 });
  triggerThreeFailures(recorder, advance);
  assert.deepEqual(emitted[0].properties.appStart, {
    coldStart: true,
    sinceLastConnectedMs: 10_000,
    sinceLastSeenMs: 1_000,
    wasConnectedAtLastSeen: true,
  });
  assert.ok(eventsOf(emitted[0]).some((event) => event.kind === "auto.reconnect" && event.phase === "probe_fast"));
});

test("冷启动标记：解析容错、落盘更新规则、app.start 字段", () => {
  assert.equal(parseLinkLastSeen(null), null);
  assert.equal(parseLinkLastSeen("not json"), null);
  assert.equal(parseLinkLastSeen("[1,2]")?.lastSeenAtMs ?? null, null);
  assert.deepEqual(
    parseLinkLastSeen(JSON.stringify({ lastConnectedAtMs: 100, lastSeenAtMs: "bad", wasConnected: true })),
    { lastConnectedAtMs: 100, lastSeenAtMs: null, wasConnected: true },
  );
  // 已连接时刷新最后连接时间；未连接时保留旧值，只更新「最后落盘」与「落盘时是否连着」。
  const connected = nextLinkLastSeen(EMPTY_LINK_LAST_SEEN, true, 1_000);
  assert.deepEqual(connected, { lastConnectedAtMs: 1_000, lastSeenAtMs: 1_000, wasConnected: true });
  assert.deepEqual(nextLinkLastSeen(connected, false, 5_000), { lastConnectedAtMs: 1_000, lastSeenAtMs: 5_000, wasConnected: false });
  assert.deepEqual(buildAppStartFields(null, 9_000), {
    coldStart: true,
    sinceLastConnectedMs: null,
    sinceLastSeenMs: null,
    wasConnectedAtLastSeen: null,
  });
  assert.deepEqual(buildAppStartFields({ lastConnectedAtMs: 4_000, lastSeenAtMs: 8_000, wasConnected: false }, 9_000), {
    coldStart: true,
    sinceLastConnectedMs: 5_000,
    sinceLastSeenMs: 1_000,
    wasConnectedAtLastSeen: false,
  });
  // 时钟被调回（落盘时间在未来）不能产生负数。
  assert.equal(buildAppStartFields({ lastConnectedAtMs: 20_000, lastSeenAtMs: 20_000, wasConnected: true }, 9_000).sinceLastSeenMs, 0);
});

test("全局 recordPrinterPrintSuccess：未安装记录器时是空操作，安装后计入会话", () => {
  recordPrinterPrintSuccess(); // 未安装：不抛错
  const emitted: PrinterLinkLogItem[] = [];
  let clock = 1_000_000;
  installPrinterLinkRecorder({
    now: () => clock,
    newId: () => "00000000-0000-4000-8000-000000000001",
    emit: (item) => emitted.push(item),
    context: () => ({ environment: "test" }),
  });
  try {
    recordPrinterLink("connect.ok", { role: "label" });
    clock += 1_000;
    recordPrinterPrintSuccess();
    clock += 9_000;
    recordPrinterLink("link.lost", { source: "native", bluetoothEnabled: true });
    for (let i = 0; i < 3; i += 1) {
      recordPrinterLink("connect.fail", { role: "label", code: "CONNECT_ERROR", message: "x", elapsedMs: 1 });
    }
    const session = emitted[0].properties.session as Record<string, unknown>;
    assert.equal(session.printsInSession, 1);
    assert.equal(session.idleSincePrintMs, 9_000);
  } finally {
    installPrinterLinkRecorder(null);
  }
});

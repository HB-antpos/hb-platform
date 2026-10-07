import assert from "node:assert/strict";
import { test } from "node:test";
import {
  appendPendingLinkLog,
  createPrinterLinkRecorder,
  describeLinkError,
  installPrinterLinkRecorder,
  LINK_DIAGNOSTICS_STORAGE_KEY,
  LINK_HEAD_CAP,
  LINK_SNAPSHOT_TAIL,
  MAX_PENDING_LINK_LOGS,
  parsePendingLinkLogs,
  recordPrinterLink,
  recordPrinterNativeStatus,
  uploadPendingPrinterLinkLogs,
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
  assert.ok(events.length <= LINK_HEAD_CAP + LINK_SNAPSHOT_TAIL);
  assert.equal(events[0].marker, "first-event", "开头事件（最初是怎么断的）必须保留");
  assert.equal(events[0].t, 0);
  const times = events.map((event) => event.t);
  assert.deepEqual(times, [...times].sort((a, b) => a - b));
  assert.equal(new Set(events.map((event) => JSON.stringify(event))).size, events.length);
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

import assert from "node:assert/strict";
import {
  createHidBarcodeKeyBuffer,
  createHidTextInputCapture,
  isHidNativeTextInputFallbackEvent,
} from "./hid-barcode-buffer";

class FakeTimers {
  private nextId = 1;
  private tasks = new Map<number, () => void>();

  setTimeout = (callback: () => void) => {
    const id = this.nextId;
    this.nextId += 1;
    this.tasks.set(id, callback);
    return id;
  };

  clearTimeout = (id: number) => {
    this.tasks.delete(id);
  };

  runAll() {
    const tasks = Array.from(this.tasks.values());
    this.tasks.clear();
    for (const task of tasks) {
      task();
    }
  }
}

function createBuffer(options?: {
  minLength?: number;
  now?: () => number;
  onFallbackToTextInput?: () => void;
}) {
  const timers = new FakeTimers();
  const scans: string[] = [];
  const buffer = createHidBarcodeKeyBuffer({
    idleMs: 50,
    minLength: options?.minLength ?? 3,
    now: options?.now,
    onBarcode: (barcode) => {
      scans.push(barcode);
    },
    onFallbackToTextInput: options?.onFallbackToTextInput,
    setTimeoutFn: timers.setTimeout as unknown as typeof setTimeout,
    clearTimeoutFn: timers.clearTimeout as unknown as typeof clearTimeout,
  });

  return { buffer, scans, timers };
}

{
  const { buffer, scans, timers } = createBuffer();
  for (const character of "9525813130129") {
    buffer.handleKeyPress({ key: character, character });
  }

  timers.runAll();
  assert.deepEqual(scans, ["9525813130129"], "连续字符应在空闲后提交一次扫码结果");
}

{
  const { buffer, scans, timers } = createBuffer();
  for (const character of "ABC123") {
    buffer.handleKeyPress({ key: character, character });
  }
  buffer.handleKeyPress({ key: "Enter" });
  timers.runAll();

  assert.deepEqual(scans, ["ABC123"], "Enter 应立即提交并清理空闲定时器");
}

{
  const { buffer, scans, timers } = createBuffer();
  for (const character of "2606150113355") {
    buffer.handleKeyPress({ key: character, character });
  }
  buffer.handleKeyPress({ key: "Tab" });
  timers.runAll();

  assert.deepEqual(scans, ["2606150113355"], "Tab 应作为扫码枪结束符立即提交");
}

{
  const { buffer, scans, timers } = createBuffer({ minLength: 4 });
  buffer.handleKeyPress({ key: "A", character: "A" });
  buffer.handleKeyPress({ key: "B", character: "B" });
  buffer.handleKeyPress({ key: "Enter" });
  timers.runAll();

  assert.deepEqual(scans, [], "短条码不应触发查询");
}

{
  let currentTime = 1000;
  const { buffer, scans } = createBuffer({ now: () => currentTime });
  for (const character of "HB313129") {
    buffer.handleKeyPress({ key: character, character });
  }
  buffer.handleKeyPress({ key: "Enter" });

  currentTime += 50;
  for (const character of "HB313129") {
    buffer.handleKeyPress({ key: character, character });
  }
  buffer.handleKeyPress({ key: "Enter" });

  assert.deepEqual(scans, ["HB313129"], "极短时间内重复同条码不应重复触发");
}

{
  let currentTime = 1000;
  const { buffer, scans } = createBuffer({ now: () => currentTime });
  for (const character of "HB313129") {
    buffer.handleKeyPress({ key: character, character });
  }
  buffer.handleKeyPress({ key: "Enter" });

  currentTime += 150;
  for (const character of "HB313129") {
    buffer.handleKeyPress({ key: character, character });
  }
  buffer.handleKeyPress({ key: "Enter" });

  assert.deepEqual(scans, ["HB313129", "HB313129"], "真实连续扫同条码应允许累计加购");
}

{
  let currentTime = 1000;
  const { buffer, scans } = createBuffer({ now: () => currentTime });
  for (const character of "HB-DISABLE") {
    buffer.handleKeyPress({ key: character, character });
  }
  buffer.handleKeyPress({ key: "Enter" });

  buffer.dispose();
  for (const character of "HB-DISABLE") {
    buffer.handleKeyPress({ key: character, character });
  }
  buffer.handleKeyPress({ key: "Enter" });

  assert.deepEqual(scans, ["HB-DISABLE", "HB-DISABLE"], "禁用清理后不得保留旧扫码缓存");
}

/**
 * 模拟非受控的原生 TextInput：扫码枪逐字符写入并触发 onChangeText；
 * JS 调 clear() 时可设定为「被原生丢弃」（新架构事件计数对不上）或「成功清空但不回传变化事件」。
 */
function createTextInputHarness(options?: { dropClear?: () => boolean; captureSubmission?: () => () => boolean }) {
  const timers = new FakeTimers();
  const scans: string[] = [];
  let nativeText = "";
  let clearCalls = 0;
  const capture = createHidTextInputCapture({
    idleMs: 50,
    onText: (pendingText) => {
      scans.push(pendingText.replace(/[\r\n\t]/g, "").trim());
    },
    clearNative: () => {
      clearCalls += 1;
      if (!options?.dropClear?.()) {
        nativeText = "";
      }
    },
    captureSubmission: options?.captureSubmission,
    setTimeoutFn: timers.setTimeout as unknown as typeof setTimeout,
    clearTimeoutFn: timers.clearTimeout as unknown as typeof clearTimeout,
  });

  const type = (code: string) => {
    for (const character of code) {
      nativeText += character;
      capture.handleChangeText(nativeText);
    }
  };

  return {
    capture,
    scans,
    timers,
    getNativeText: () => nativeText,
    getClearCalls: () => clearCalls,
    type,
    /** 带回车后缀的扫码：逐字符输入后触发 submitEditing（带原生全文）。 */
    scanWithEnter(code: string) {
      type(code);
      capture.handleSubmitEditing(nativeText);
      timers.runAll();
    },
    /** 不带回车后缀的扫码：逐字符输入后靠停顿自动提交。 */
    scanWithIdle(code: string) {
      type(code);
      timers.runAll();
    },
  };
}

{
  const harness = createTextInputHarness();
  harness.scanWithEnter("9300000000001");
  assert.deepEqual(harness.scans, ["9300000000001"], "回车路径应以原生全文提交一次，且不再被停顿定时器重放");
  assert.equal(harness.getNativeText(), "", "提交后应调用 ref.clear() 清空原生输入框");
}

{
  const harness = createTextInputHarness();
  harness.scanWithIdle("9525813130129");
  harness.scanWithIdle("12345678");
  assert.deepEqual(harness.scans, ["9525813130129", "12345678"], "停顿路径：原生清空成功时每枪整段提交");
}

for (const path of ["enter", "idle"] as const) {
  // 13 位 EAN 后原生清空被丢弃，再扫更短的 8 位码：旧逻辑会把 21 位拼接串当条码提交。
  const harness = createTextInputHarness({ dropClear: () => true });
  const scan = path === "enter" ? harness.scanWithEnter : harness.scanWithIdle;
  scan("9300000000001");
  scan("12345678");
  assert.equal(harness.getNativeText(), "930000000000112345678", "前置：清空被丢弃时原生文本保留旧码残留");
  assert.deepEqual(harness.scans, ["9300000000001", "12345678"], `${path} 路径：新码比旧码短 + 清空被丢弃时只提交新增部分`);
}

{
  // TC26 历史现象：下一次扫码追加到旧值后面（同长度）。
  const harness = createTextInputHarness({ dropClear: () => true });
  harness.scanWithIdle("9300000000001");
  harness.scanWithIdle("9300000000002");
  harness.scanWithIdle("ABC");
  assert.deepEqual(harness.scans, ["9300000000001", "9300000000002", "ABC"], "多枪连续追加时每枪只提交新增条码段");
}

for (const dropClear of [true, false]) {
  const harness = createTextInputHarness({ dropClear: () => dropClear });
  harness.scanWithEnter("9300000000001");
  harness.scanWithEnter("9300000000001");
  assert.deepEqual(
    harness.scans,
    ["9300000000001", "9300000000001"],
    `同码重扫（清空${dropClear ? "被丢弃" : "成功"}）不应被当成旧前缀吃掉`,
  );
}

{
  // 原生清空成功后，新码恰好以前一枪为前缀：不能截成尾段。
  const harness = createTextInputHarness();
  harness.scanWithEnter("9300000000001");
  harness.scanWithEnter("93000000000012");
  assert.deepEqual(harness.scans, ["9300000000001", "93000000000012"], "新条码以前条码为前缀时应整段提交");
}

{
  // 清空时好时坏：第一次被丢弃、第二次成功。
  let clearIndex = 0;
  const harness = createTextInputHarness({ dropClear: () => clearIndex++ === 0 });
  harness.scanWithIdle("9300000000001");
  harness.scanWithIdle("4711");
  harness.scanWithIdle("9300000000001");
  assert.deepEqual(harness.scans, ["9300000000001", "4711", "9300000000001"], "清空成功与被丢弃交替时都不串码");
}

{
  // 停顿已提交后，迟到的 submitEditing 带回同一段全文：不得重放。
  const harness = createTextInputHarness({ dropClear: () => true });
  harness.scanWithIdle("9300000000001");
  harness.capture.handleSubmitEditing(harness.getNativeText());
  assert.deepEqual(harness.scans, ["9300000000001"], "同一枪的停顿提交与迟到回车只能提交一次");
}

{
  // 文本变化时捕获的提交许可在停顿前失效（页面停用过）：只丢弃，之后的新码照常按差量提交。
  let revision = 0;
  const harness = createTextInputHarness({
    dropClear: () => true,
    captureSubmission: () => {
      const captured = revision;
      return () => captured === revision;
    },
  });
  harness.type("STALE01");
  revision += 1;
  harness.timers.runAll();
  assert.deepEqual(harness.scans, [], "许可失效后停顿不得提交");
  assert.equal(harness.getClearCalls(), 1, "许可失效时仍要尽力清空原生残段");
  harness.scanWithIdle("9300000000001");
  assert.deepEqual(harness.scans, ["9300000000001"], "被丢弃的残段之后，新码照常按差量提交");
}

{
  let fallbackCount = 0;
  const { buffer, scans, timers } = createBuffer({
    onFallbackToTextInput: () => {
      fallbackCount += 1;
    },
  });

  assert.equal(isHidNativeTextInputFallbackEvent({ key: "12" }), true, "数字 key 且无 character 应判定为 fallback");
  buffer.handleKeyPress({ key: "12" });
  timers.runAll();

  assert.equal(fallbackCount, 1, "数字 key 且无 character 时应切换到隐藏 TextInput 模式");
  assert.deepEqual(scans, [], "fallback 事件不应提交空条码");
}

console.log("hid-barcode-buffer.test.ts: ok");

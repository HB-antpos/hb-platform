import assert from "node:assert/strict";

import {
  BleScanFrameAssembler,
  type BleScanFrame,
  CrossSourceScanDeduper,
  bleDeviceSuffix,
  classifyBleScanValue,
  decodeBase64,
  decodeScanBytes,
  guessBarcodeSymbology,
  matchBleScannerProfile,
  normalizeBleUuid,
  parseBatteryLevel,
  parseSavedBleScanner,
  scanValueTail,
} from "./ble-scanner-core";

/* 用手动计时器驱动拼帧器，保证测试不依赖真实时间。 */
function createHarness(options: { idleMs?: number; staleMs?: number; maxLength?: number } = {}) {
  let now = 0;
  const timers = new Map<number, { at: number; callback: () => void }>();
  let nextId = 1;
  const frames: BleScanFrame[] = [];
  const diagnostics: string[] = [];
  const assembler = new BleScanFrameAssembler({
    ...options,
    now: () => now,
    onFrame: (frame) => frames.push(frame),
    onDiagnostic: (diagnostic) => diagnostics.push(diagnostic),
    setTimer: (callback, ms) => {
      const id = nextId++;
      timers.set(id, { at: now + ms, callback });
      return id;
    },
    clearTimer: (handle) => {
      timers.delete(handle as number);
    },
  });
  const advance = (ms: number) => {
    now += ms;
    for (const [id, timer] of [...timers.entries()]) {
      if (timer.at <= now) {
        timers.delete(id);
        timer.callback();
      }
    }
  };
  const push = (text: string | number[]) => {
    const bytes = typeof text === "string" ? Array.from(text, (c) => c.charCodeAt(0)) : text;
    assembler.push(bytes, now);
  };
  return { assembler, frames, diagnostics, advance, push };
}

// 1. 背夹实测：EAN-13 一包到齐，结尾 CR CR —— 只出一个码，第二个 CR 的空码丢弃。
{
  const h = createHarness();
  h.push([...Array.from("9528503822012", (c) => c.charCodeAt(0)), 0x0d, 0x0d]);
  assert.equal(h.frames.length, 1);
  assert.equal(h.frames[0]?.value, "9528503822012");
  assert.equal(h.frames[0]?.terminator, "CR");
  assert.equal(h.frames[0]?.packetCount, 1);
  h.advance(1000);
  assert.equal(h.frames.length, 1, "CR CR 不应多出一次空扫码");
}

// 2. 背夹实测：109 字符二维码拆 8 包，包间最长 62ms，结尾 CR CR —— 不能被切成两段。
{
  const h = createHarness();
  const url =
    "https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/wpf-releases/production/1.0.115/Hbpos.Client.Wpf-1.0.115-x64.exe";
  const packets = [
    url.slice(0, 20),
    url.slice(20, 40),
    url.slice(40, 60),
    url.slice(60, 64),
    url.slice(64, 84),
    url.slice(84, 104),
    url.slice(104, 124),
    `${url.slice(124)}\r\r`,
  ];
  const gaps = [0, 0, 0, 27, 62, 1, 0, 28];
  packets.forEach((packet, index) => {
    h.advance(gaps[index] ?? 0);
    h.push(packet);
  });
  assert.equal(h.frames.length, 1);
  assert.equal(h.frames[0]?.value, url);
  assert.equal(h.frames[0]?.packetCount, 8);
  assert.equal(h.frames[0]?.durationMs, 118);
}

// 3. 版本回复结尾 LF CR：也只出一个码，并被归类为设备回复。
{
  const h = createHarness();
  h.push("HW:V1.00;SW:HD210_V2");
  h.push("Nx_V1.8.7.0;Date:202");
  h.push("5.12.29;\n\r");
  assert.equal(h.frames.length, 1);
  assert.equal(h.frames[0]?.terminator, "LF");
  assert.equal(classifyBleScanValue(h.frames[0]?.value ?? ""), "device-reply");
}

// 4. 从没见过结束符的设备：靠 120ms 空闲超时结束；超时之前的包间空档不切断。
{
  const h = createHarness();
  h.push("12345");
  h.advance(100);
  h.push("678");
  h.advance(119);
  assert.equal(h.frames.length, 0);
  h.advance(1);
  assert.equal(h.frames.length, 1);
  assert.equal(h.frames[0]?.value, "12345678");
  assert.equal(h.frames[0]?.terminator, "idle");
}

// 5. 见过结束符之后只认结束符：低速传输 300ms 空档也不切断；残码 500ms 后清掉不发。
{
  const h = createHarness();
  h.push("A1\r");
  h.push("B2");
  h.advance(300);
  h.push("C3\r");
  assert.deepEqual(
    h.frames.map((frame) => frame.value),
    ["A1", "B2C3"],
  );
  h.push("LEFTOVER");
  h.advance(500);
  assert.equal(h.frames.length, 2, "残码不能被当成一个码发出去");
  assert.deepEqual(h.diagnostics, ["stale-partial"]);
}

// 6. 一包里有两个码 + CRLF、Tab 结束符、控制字符剔除。
{
  const h = createHarness();
  h.push("AAA\r\nBBB\tC\u0001C\r");
  assert.deepEqual(
    h.frames.map((frame) => [frame.value, frame.terminator]),
    [
      ["AAA", "CR"],
      ["BBB", "TAB"],
      ["CC", "CR"],
    ],
  );
}

// 7. 超长丢弃直到下一个结束符，下一个码不受污染。
{
  const h = createHarness({ maxLength: 8 });
  h.push("123456789012345678901234567890123456789\r");
  h.push("OK\r");
  assert.deepEqual(
    h.frames.map((frame) => frame.value),
    ["OK"],
  );
  assert.ok(h.diagnostics.includes("max-length"));
}

// 8. UTF-8 解码；非法序列退回 Latin-1。
assert.equal(decodeScanBytes([0xe4, 0xb8, 0xad, 0x41]), "中A");
assert.equal(decodeScanBytes([0xff, 0x41]), "ÿA");

// 9. base64：蓝牙库交付的特征值。
assert.deepEqual(Array.from(decodeBase64("OTUyODUwMzgyMjAxMg0N")), [
  ...Array.from("9528503822012", (c) => c.charCodeAt(0)),
  0x0d,
  0x0d,
]);
assert.deepEqual(Array.from(decodeBase64("Rg==")), [0x46]);

// 10. 电量：背夹读到 0x46 = 70%。
assert.equal(parseBatteryLevel([0x46]), 70);
assert.equal(parseBatteryLevel([0xff]), null);
assert.equal(parseBatteryLevel([]), null);

// 11. 型号识别：广播服务 FEEA → 怡芯档案；只有名字也能认出。
assert.equal(normalizeBleUuid("feea"), "0000FEEA-0000-1000-8000-00805F9B34FB");
assert.equal(matchBleScannerProfile(["FEEA"], null)?.id, "yichip-feea");
assert.equal(matchBleScannerProfile([], "BarCode Scanner BLE")?.id, "yichip-feea");
assert.equal(matchBleScannerProfile(["FFE0"], "HMSoft")?.id, "hm10-ffe0");
assert.equal(matchBleScannerProfile(["180D"], "Heart"), null);
assert.equal(bleDeviceSuffix("D2DA1459-BCE1-0B6C-B7A7-8DE157AAD5CF"), "D5CF");
assert.equal(bleDeviceSuffix("AA:BB:CC:DD:EE:0F"), "EE0F");

// 12. 设置码、设备回复不进业务页面。
assert.equal(classifyBleScanValue("%%SpecCodeAC"), "setting-code");
assert.equal(classifyBleScanValue(">RUNMOD0<"), "setting-code");
assert.equal(classifyBleScanValue(">J606101<"), "setting-code");
assert.equal(classifyBleScanValue("9528503822012"), "barcode");
assert.equal(classifyBleScanValue("https://example.com/a<b>"), "barcode");

// 13. 码型推测。
assert.equal(guessBarcodeSymbology("4006381333931"), "EAN-13");
assert.equal(guessBarcodeSymbology("9528503822012"), "other", "背夹实测的店内码校验位不符，不能硬标 EAN-13");
assert.equal(guessBarcodeSymbology("4006381333932"), "other");
assert.equal(guessBarcodeSymbology("036000291452"), "UPC-A");
assert.equal(guessBarcodeSymbology("96385074"), "EAN-8");
assert.equal(guessBarcodeSymbology("VD028"), "other");
assert.equal(scanValueTail("9528503822012"), "…822012");
assert.equal(scanValueTail("VD028"), "VD028");

// 14. 跨来源去重：不同来源 300ms 内同码只算一次；同来源连续重复照常放行。
{
  const deduper = new CrossSourceScanDeduper();
  assert.equal(deduper.accept("A", "ble", 0), true);
  assert.equal(deduper.accept("A", "hid", 120), false, "同一把枪两种模式都开，只算一次");
  assert.equal(deduper.accept("A", "ble", 200), true, "同来源连扫两件同款不能吞");
  assert.equal(deduper.accept("A", "hid", 900), true, "超过窗口放行");
  assert.equal(deduper.accept("B", "camera", 950), true);
}

// 15. 保存的配置：字段白名单，坏数据整条作废。
{
  const good = {
    version: 1,
    peripheralId: "D2DA1459-BCE1-0B6C-B7A7-8DE157AAD5CF",
    name: "BarCode Scanner BLE",
    alias: "1 号背夹",
    profileId: "yichip-feea",
    serviceUuid: "feea",
    notifyUuid: "2aa1",
    autoReconnect: true,
    savedAt: "2026-11-21T05:00:00.000Z",
  };
  const parsed = parseSavedBleScanner(JSON.stringify(good));
  assert.equal(parsed?.serviceUuid, "0000FEEA-0000-1000-8000-00805F9B34FB");
  assert.equal(parsed?.alias, "1 号背夹");
  assert.equal(parseSavedBleScanner({ ...good, version: 2 }), null);
  assert.equal(parseSavedBleScanner({ ...good, autoReconnect: "yes" }), null);
  assert.equal(parseSavedBleScanner({ ...good, peripheralId: "" }), null);
  assert.equal(parseSavedBleScanner({ ...good, alias: "x".repeat(200) }), null);
  assert.equal(parseSavedBleScanner({ ...good, profileId: "unknown" })?.profileId, null);
  assert.equal(parseSavedBleScanner("{not json"), null);
}

console.log("ble-scanner-core: ok");

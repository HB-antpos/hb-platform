import assert from "node:assert/strict";
import test from "node:test";

import {
  appendScannerTestEntry,
  createScannerTestEntry,
  SCANNER_TEST_HISTORY_LIMIT,
  type ScannerTestEntry,
} from "./ble-scanner-test-session";

const frame = {
  value: "9313559558795",
  rawHex: "39 33 31 33 35 35 39 35 35 38 37 39 35 0D",
  packetCount: 1,
  durationMs: 0,
  terminator: "CR" as const,
  receivedAt: 1_000,
};

test("BLE 记录保留拼帧细节，新记录在最前", () => {
  let entries: readonly ScannerTestEntry[] = [];
  entries = appendScannerTestEntry(
    entries,
    createScannerTestEntry({ id: 1, source: "ble", value: "VD028", receivedAt: 100 }),
  ).entries;
  entries = appendScannerTestEntry(
    entries,
    createScannerTestEntry({
      id: 2,
      source: "ble",
      value: frame.value,
      receivedAt: 5_000,
      frame,
    }),
  ).entries;
  assert.deepEqual(
    entries.map((entry) => entry.id),
    [2, 1],
  );
  assert.equal(entries[0]?.packetCount, 1);
  assert.equal(entries[0]?.terminator, "CR");
  assert.equal(entries[0]?.length, 13);
  assert.equal(entries[1]?.packetCount, null, "无拼帧细节时为空");
});

test("键盘与蓝牙 1 秒内同码互相标记为重复；同来源连扫不算", () => {
  let entries: readonly ScannerTestEntry[] = [];
  entries = appendScannerTestEntry(
    entries,
    createScannerTestEntry({ id: 1, source: "ble", value: "SKU-1", receivedAt: 1_000 }),
  ).entries;
  const sameSource = appendScannerTestEntry(
    entries,
    createScannerTestEntry({ id: 2, source: "ble", value: "SKU-1", receivedAt: 1_100 }),
  );
  assert.equal(sameSource.duplicate, false, "连扫两件同款不是重复输入");
  const crossSource = appendScannerTestEntry(
    sameSource.entries,
    createScannerTestEntry({ id: 3, source: "hid", value: "SKU-1", receivedAt: 1_200 }),
  );
  assert.equal(crossSource.duplicate, true);
  assert.deepEqual(
    crossSource.entries.map((entry) => entry.duplicate),
    [true, true, true],
  );
  const late = appendScannerTestEntry(
    crossSource.entries,
    createScannerTestEntry({ id: 4, source: "camera", value: "SKU-1", receivedAt: 5_000 }),
  );
  assert.equal(late.duplicate, false, "超出窗口的同码是新的一次扫码");
});

test("开通码屏蔽显示、不保留原始字节、不参与重复比对", () => {
  const entry = createScannerTestEntry({
    id: 1,
    source: "ble",
    value: "HBDEV1-SECRET-CODE",
    receivedAt: 1_000,
    frame: { ...frame, value: "HBDEV1-SECRET-CODE", rawHex: "48 42" },
  });
  assert.equal(entry.masked, true);
  assert.equal(entry.value.includes("SECRET"), false);
  assert.equal(entry.rawHex, null);
  assert.equal(entry.matchKey, null);
  const result = appendScannerTestEntry(
    [entry],
    createScannerTestEntry({ id: 2, source: "hid", value: "HBDEV1-SECRET-CODE", receivedAt: 1_100 }),
  );
  assert.equal(result.duplicate, false);
});

test("只保留最近 20 条", () => {
  let entries: readonly ScannerTestEntry[] = [];
  for (let index = 0; index < 30; index += 1) {
    entries = appendScannerTestEntry(
      entries,
      createScannerTestEntry({ id: index, source: "ble", value: `C${index}`, receivedAt: index * 2_000 }),
    ).entries;
  }
  assert.equal(entries.length, SCANNER_TEST_HISTORY_LIMIT);
  assert.equal(entries[0]?.value, "C29");
});

import assert from "node:assert/strict";

import {
  createPersonalCodeSymbologyController,
  DEFAULT_PERSONAL_CODE_SYMBOLOGY,
  normalizePersonalCodeSymbology,
  PERSONAL_CODE_SYMBOLOGY_STORAGE_KEY,
} from "./personal-code-symbology";

function memoryStorage(initial?: string | null, options: { failRead?: boolean; failWrite?: boolean } = {}) {
  const data = new Map<string, string>();
  if (initial != null) data.set(PERSONAL_CODE_SYMBOLOGY_STORAGE_KEY, initial);
  return {
    data,
    async getString(key: string) {
      if (options.failRead) throw new Error("read failed");
      return data.get(key) ?? null;
    },
    async setString(key: string, value: string) {
      if (options.failWrite) throw new Error("write failed");
      data.set(key, value);
    },
  };
}

async function main() {
  assert.equal(DEFAULT_PERSONAL_CODE_SYMBOLOGY, "qr", "默认必须是二维码");
  assert.equal(normalizePersonalCodeSymbology("barcode"), "barcode");
  for (const value of ["qr", "", "BARCODE", "code128", null, undefined, 1]) {
    assert.equal(normalizePersonalCodeSymbology(value), "qr", `未知值 ${String(value)} 回落二维码`);
  }

  // 没存过偏好 → 默认二维码，且不会往存储里写。
  const empty = memoryStorage();
  const fresh = createPersonalCodeSymbologyController(empty);
  assert.equal(fresh.getSnapshot(), "qr");
  assert.equal(await fresh.hydrate(), "qr");
  assert.equal(empty.data.size, 0, "读取不产生写入");

  // 切换 → 通知订阅者并持久化；新实例读回同一值。
  const storage = memoryStorage();
  const first = createPersonalCodeSymbologyController(storage);
  let notified = 0;
  const unsubscribe = first.subscribe(() => { notified += 1; });
  await first.set("barcode");
  assert.equal(first.getSnapshot(), "barcode");
  assert.equal(notified, 1);
  await first.set("barcode");
  assert.equal(notified, 1, "值未变化不重复通知");
  unsubscribe();
  await first.set("qr");
  assert.equal(notified, 1, "取消订阅后不再通知");
  await first.set("barcode");
  assert.equal(await createPersonalCodeSymbologyController(storage).hydrate(), "barcode", "重启后读回偏好");

  // 存储里是损坏值 → 回落二维码。
  assert.equal(await createPersonalCodeSymbologyController(memoryStorage("???")).hydrate(), "qr");

  // 读取失败 → 保持默认，不抛错。
  assert.equal(await createPersonalCodeSymbologyController(memoryStorage(null, { failRead: true })).hydrate(), "qr");

  // 写入失败 → 本次会话仍按用户选择显示和打印，不抛错。
  const readonly = createPersonalCodeSymbologyController(memoryStorage(null, { failWrite: true }));
  await readonly.set("barcode");
  assert.equal(readonly.getSnapshot(), "barcode");
  assert.equal(await readonly.hydrate(), "barcode", "写失败也不被后续读取覆盖");

  // hydrate 进行中用户先切换 → 以用户操作为准，不被迟到的存储值覆盖。
  let release: (value: string | null) => void = () => undefined;
  const slow = createPersonalCodeSymbologyController({
    getString: () => new Promise<string | null>((resolve) => { release = resolve; }),
    setString: async () => undefined,
  });
  const pending = slow.hydrate();
  await slow.set("barcode");
  release("qr");
  assert.equal(await pending, "barcode");
  assert.equal(slow.getSnapshot(), "barcode");

  // hydrate 幂等：重复调用只读一次存储。
  let reads = 0;
  const counted = createPersonalCodeSymbologyController({
    getString: async () => { reads += 1; return "barcode"; },
    setString: async () => undefined,
  });
  await Promise.all([counted.hydrate(), counted.hydrate()]);
  await counted.hydrate();
  assert.equal(reads, 1);
  assert.equal(counted.getSnapshot(), "barcode");

  console.log("personal-code-symbology tests passed");
}

void main();

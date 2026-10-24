import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";

import {
  API_ENTRY_MODE_STORAGE_KEY,
  type AsyncKeyValueStorage,
} from "@hb/pos-api-client/api-entry-mode-coordinator";
import { sharedApiTransportSelector } from "@hb/pos-api-client/transport-failover";

import { createApiEntryModeCoordinatorFor } from "./api-entry-mode-store";

function memoryStorage(initial: Record<string, string> = {}) {
  const values = new Map(Object.entries(initial));
  const storage: AsyncKeyValueStorage = {
    getItem: async (key) => values.get(key) ?? null,
    setItem: async (key, value) => {
      values.set(key, value);
    },
    removeItem: async (key) => {
      values.delete(key);
    },
  };
  return { storage, values };
}

test("薄封装接收注入的 AsyncStorage 形状存储：恢复 → 应用 → 落盘（用共享选择器，测完复位）", async () => {
  const { storage, values } = memoryStorage({ [API_ENTRY_MODE_STORAGE_KEY]: "aws" });
  try {
    const coordinator = createApiEntryModeCoordinatorFor(storage);
    await coordinator.restore();
    assert.equal(sharedApiTransportSelector.getPreferredMode(), "aws");

    assert.equal(coordinator.apply("direct"), "applied");
    assert.equal(coordinator.apply("unknown-mode"), "ignored");
    await coordinator.flush();
    assert.equal(sharedApiTransportSelector.getPreferredMode(), "direct");
    assert.equal(values.get(API_ENTRY_MODE_STORAGE_KEY), "direct");

    assert.equal(coordinator.apply(null), "applied");
    await coordinator.flush();
    assert.equal(sharedApiTransportSelector.getPreferredMode(), "edge");
    assert.equal(values.has(API_ENTRY_MODE_STORAGE_KEY), false);
  } finally {
    sharedApiTransportSelector.setPreferredMode(null);
  }
});

test("构造协调器本身不触碰原生 AsyncStorage（tsx 里不传存储也不会抛）", async () => {
  const coordinator = createApiEntryModeCoordinatorFor();
  assert.equal(typeof coordinator.apply, "function");
});

test("源码契约：存储用 AsyncStorage 且延迟 require，不再使用 SecureStore；依赖版本与员工移动端一致", () => {
  const source = readFileSync(join(process.cwd(), "src/core/api/api-entry-mode-store.ts"), "utf8");
  assert.match(source, /require\("@react-native-async-storage\/async-storage"\)/u);
  assert.doesNotMatch(source, /secure/iu);
  assert.doesNotMatch(source, /^import .*async-storage/mu, "不得顶层 import 原生模块");
  const manifest = JSON.parse(readFileSync(join(process.cwd(), "package.json"), "utf8")) as {
    dependencies: Record<string, string>;
  };
  assert.equal(manifest.dependencies["@react-native-async-storage/async-storage"], "2.2.0");
  const mobile = JSON.parse(
    readFileSync(join(process.cwd(), "../mobile/package.json"), "utf8"),
  ) as { dependencies: Record<string, string> };
  assert.equal(
    manifest.dependencies["@react-native-async-storage/async-storage"],
    mobile.dependencies["@react-native-async-storage/async-storage"],
  );
});

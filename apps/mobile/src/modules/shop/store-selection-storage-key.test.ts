import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { resolveScopedStoreSelection } from "./store-scope";
import { STORE_SELECTION_STORAGE_KEY, buildStoreSelectionStorageKey } from "./types";

const directory = dirname(fileURLToPath(import.meta.url));

test("分店选择 key 按账号区分，且大小写/空白不影响同一账号", () => {
  const a = buildStoreSelectionStorageKey("AAAA-1111");
  const b = buildStoreSelectionStorageKey("bbbb-2222");
  assert.notEqual(a, b);
  assert.equal(a, buildStoreSelectionStorageKey("  aaaa-1111 "));
  assert.ok(a.startsWith(`${STORE_SELECTION_STORAGE_KEY}:`));
});

test("A→B→A：内存态被重置后，按账号记住的分店仍能恢复；失效则回退默认", () => {
  const stores = [{ storeCode: "S1", storeName: "One" }, { storeCode: "S2", storeName: "Two" }];
  // 切回 A：内存里没有当前分店，持久化值 S2 可用 → 恢复为 S2（非自动）
  assert.deepEqual(
    resolveScopedStoreSelection({ persistedStoreCode: "S2", isDeviceMode: false, stores }),
    { storeCode: "S2", isAuto: false },
  );
  // 记住的分店已不在可选列表 → 回退到第一个（自动默认）
  assert.deepEqual(
    resolveScopedStoreSelection({ persistedStoreCode: "S9", isDeviceMode: false, stores }),
    { storeCode: "S1", isAuto: true },
  );
});

test("use-stores：读写走按账号 key；自动回退不清除记忆；放弃已切走账号的同步任务", () => {
  const source = readFileSync(resolve(directory, "use-stores.ts"), "utf8");
  assert.match(source, /AppAsyncStorage\.getString\(selectionStorageKey\)/);
  assert.match(source, /useAuthStore\.getState\(\)\.user\?\.userGUID !== syncUserGuid/);
  // 账号模式下不再读写全局 key（设备模式仍用旧全局 key）
  const syncBody = source.slice(source.indexOf("async function syncStores"), source.indexOf("void syncStores()"));
  assert.doesNotMatch(syncBody, /removeItem/);
  assert.doesNotMatch(syncBody, /STORE_SELECTION_STORAGE_KEY/);
});

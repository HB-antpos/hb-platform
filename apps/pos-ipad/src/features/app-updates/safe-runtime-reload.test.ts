import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
  SAFE_RUNTIME_RELOAD_MARKER,
  hasSafeRuntimeReload,
} from "./safe-runtime-reload";

const here = dirname(fileURLToPath(import.meta.url));
const patchPath = resolve(
  here,
  "../../../../../patches/expo-modules-core+3.0.30.patch",
);

test("只有原生层在 global.expo 上写了 true 标记才认为可在 App 内重启", () => {
  assert.equal(
    hasSafeRuntimeReload({ expo: { [SAFE_RUNTIME_RELOAD_MARKER]: true } }),
    true,
  );
  for (const globalObject of [
    {},
    null,
    undefined,
    { expo: null },
    { expo: "x" },
    { expo: {} },
    { expo: { [SAFE_RUNTIME_RELOAD_MARKER]: "true" } },
    { expo: { [SAFE_RUNTIME_RELOAD_MARKER]: 1 } },
  ]) {
    assert.equal(hasSafeRuntimeReload(globalObject), false);
  }
  const throwing = Object.defineProperty({}, "expo", {
    get() {
      throw new Error("boom");
    },
  });
  assert.equal(hasSafeRuntimeReload(throwing), false);
});

test("expo-modules-core 补丁同时包含同步清空修复与同名标记，二者不能只留其一", () => {
  const patch = readFileSync(patchPath, "utf8");
  // 修复：clear() 由 lockQueue.async 改为 lockQueue.sync（否则 App 内 reload 必闪退）。
  assert.match(
    patch,
    /internal func clear\(\) \{\n(?:\+.*\n)*-\s+Self\.lockQueue\.async \{\n(?:\+.*\n)*\+\s+Self\.lockQueue\.sync \{/u,
  );
  // 标记：JS 只认这个名字，名字对不上「立即重启」就永远不会出现。
  assert.ok(
    patch.includes(
      `+    coreObject.defineProperty("${SAFE_RUNTIME_RELOAD_MARKER}", value: true, options: [])`,
    ),
  );
});

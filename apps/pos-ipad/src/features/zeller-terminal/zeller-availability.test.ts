import assert from "node:assert/strict";
import test from "node:test";

import { isZellerSettingsAvailable } from "./zeller-availability";

test("非 production 构建一直开放 Zeller 设置页签", () => {
  for (const buildProfile of ["development", "preview", undefined]) {
    assert.equal(
      isZellerSettingsAvailable({ buildProfile, enableZeller: undefined }),
      true,
      `档位 ${String(buildProfile)}`,
    );
  }
});

test("production 构建默认不开放 Zeller", () => {
  assert.equal(
    isZellerSettingsAvailable({
      buildProfile: "production",
      enableZeller: undefined,
    }),
    false,
  );
});

test("production 构建只有显式开关为字面量 1 才开放（production-zeller 档位）", () => {
  assert.equal(
    isZellerSettingsAvailable({
      buildProfile: "production",
      enableZeller: "1",
    }),
    true,
  );
  assert.equal(
    isZellerSettingsAvailable({
      buildProfile: "production",
      enableZeller: " 1 ",
    }),
    true,
    "首尾空白按 EAS 变量常见手滑容忍",
  );
});

test("production 构建里其他开关值一律不开放，避免配错", () => {
  for (const enableZeller of ["", "0", "true", "yes", "on", "11"]) {
    assert.equal(
      isZellerSettingsAvailable({ buildProfile: "production", enableZeller }),
      false,
      `开关值 ${JSON.stringify(enableZeller)}`,
    );
  }
});

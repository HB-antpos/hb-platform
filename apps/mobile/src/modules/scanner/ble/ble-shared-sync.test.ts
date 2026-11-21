import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/**
 * 移动端不在根 npm workspaces 里，无法引用 packages/*；BLE 扫码枪的纯逻辑、控制器和
 * ble-plx 适配层因此在 iPad POS 与移动端各放一份。本测试保证两份逐字相同，
 * 改一端忘了改另一端时 CI 直接红。
 */
const SHARED_FILES = ["ble-scanner-core.ts", "ble-scanner-controller.ts", "ble-plx-transport.ts"] as const;

const mobileDirectory = __dirname;
const ipadDirectory = resolve(__dirname, "../../../../../pos-ipad/src/core/peripherals/scanner/ble");

for (const file of SHARED_FILES) {
  const mobile = readFileSync(resolve(mobileDirectory, file), "utf8");
  const ipad = readFileSync(resolve(ipadDirectory, file), "utf8");
  assert.equal(mobile, ipad, `${file} 两端拷贝不一致：改动须同步到 apps/pos-ipad 与 apps/mobile`);
}

console.log("ble-shared-sync: ok");

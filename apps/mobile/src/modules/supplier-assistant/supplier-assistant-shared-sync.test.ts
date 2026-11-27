import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/**
 * 「订货助手」在大屏收银 App（apps/pos-ipad）与员工移动端各有一份网页注入逻辑。
 *
 * 中文注释：
 * 1. 移动端不在根 npm workspaces 里，无法引用 packages/*（与 BLE 扫码枪同一个原因），所以逐字拷贝；
 *    本测试保证两份完全一致，改一端忘了改另一端时 CI 直接红。
 * 2. 生成文件（插件 list.js + lib/* 打包结果）只在 iPad 端生成，iPad 的 supplier-assistant-core.test.ts
 *    负责校验它与插件源码的哈希一致；移动端这里只要求与 iPad 那份逐字相同。
 * 3. 同步办法：先跑 node apps/pos-ipad/scripts/build-supplier-assistant-content.mjs，
 *    再跑 node apps/mobile/scripts/sync-supplier-assistant-content.mjs。
 */
const SHARED_FILES = [
  "supplier-assistant-background.ts",
  "content/chrome-shim.js",
  "content/entry.js",
  "generated/supplier-assistant-content.generated.ts",
] as const;

const mobileDirectory = __dirname;
const ipadDirectory = resolve(__dirname, "../../../../pos-ipad/src/features/supplier-assistant");

for (const file of SHARED_FILES) {
  const mobile = readFileSync(resolve(mobileDirectory, file), "utf8");
  const ipad = readFileSync(resolve(ipadDirectory, file), "utf8");
  assert.equal(
    mobile,
    ipad,
    `${file} 两端拷贝不一致：跑 node apps/mobile/scripts/sync-supplier-assistant-content.mjs 同步`,
  );
}

console.log("supplier-assistant-shared-sync: ok");

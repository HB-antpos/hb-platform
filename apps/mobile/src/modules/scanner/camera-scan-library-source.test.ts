import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";

const read = (relative: string) => readFileSync(join(__dirname, "../../..", relative), "utf8");
const hook = read("src/modules/scanner/use-camera-scan.ts");
const sheet = read("src/components/ui/CameraScanSheet.tsx");
const runtime = read("src/modules/scanner/local-image-barcode-runtime.ts");

// 相册结果必须走相机扫码同一入口（gate、冷却、重复抑制），不能绕开直接回调业务。
assert.match(hook, /await handleBarcodeScanned\(outcome\.hit\)/, "相册结果必须复用 handleBarcodeScanned");
assert.match(hook, /if \(pickingImageRef\.current\) \{\s*return;/, "选图期间必须拦住实时相机帧");
assert.match(hook, /libraryPicker: canScanLocalImage\(\)/, "不支持的平台 / 原生包必须隐藏入口");
assert.match(sheet, /libraryPicker\?: /, "共享面板必须提供相册入口 prop");

// iOS 的 scanFromURLAsync 只认二维码，商品一维码必须走自带 Vision 模块。
assert.match(runtime, /Platform\.OS === "ios"[\s\S]*HBImageBarcode\.scanImage/, "iOS 必须走 Vision 原生模块");
assert.match(runtime, /scanFromURLAsync\(uri, ALL_BARCODE_TYPES\)/, "Android 必须显式传全码制");

// 所有使用共享面板的页面都要接入入口。
for (const file of [
  "app/(shell)/home.tsx",
  "app/(shell)/product-query.tsx",
  "app/(shell)/warehouse.tsx",
  "src/modules/promotions/promotion-ui.tsx",
  "src/modules/warehouse-picking/components/PickCameraSheet.tsx",
]) {
  assert.match(read(file), /libraryPicker=\{\w+\.libraryPicker\}/, `${file} 缺少相册入口接线`);
}

console.log("camera-scan-library-source.test.ts: ok");

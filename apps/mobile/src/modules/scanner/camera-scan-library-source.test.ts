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

// 创建商品 / 新增多码条码用的独立扫码弹层也要有相册入口，且与相机共用同一把「只回填一次」的锁。
const createScanner = read("src/components/product-maintenance/CreateBarcodeScanner.tsx");
assert.match(createScanner, /canScanLocalImage\(\) \?/, "不支持的平台 / 原生包必须隐藏相册入口");
assert.match(createScanner, /scanBarcodeFromImage\(picked\.uri, localImageScanner\)/, "必须复用相册识别核心逻辑");
assert.match(createScanner, /if \(pickingImageRef\.current\) return;/, "选图期间必须拦住实时相机帧");
assert.match(createScanner, /consumed\.current = true;\s*onScan\(barcode\);/, "相册结果必须与相机共用 consumed 锁");

// 新增多码弹层：扫码页必须嵌在同一个 Modal 里切换，不能再另开一个原生 Modal。
// iOS 在一个原生 Modal 还在收起时再弹另一个会被丢弃：相册选图返回后新增弹层看不见，
// 页面却因 codeAddModal 仍为真被锁住、搜索按钮一直转圈（真机上出现过）。
const codeAddSheet = read("src/components/product-maintenance/CodeAddSheet.tsx");
assert.match(createScanner, /export function BarcodeScanPanel\(/, "扫码内容必须是可嵌入的面板");
assert.match(codeAddSheet, /scanning \? <BarcodeScanPanel onScan=\{onScanned\} \/>/, "新增弹层必须把扫码面板嵌在自己的 Modal 里");
assert.match(codeAddSheet, /onBack=\{scanning \? onScanBack : undefined\}/, "扫码页必须有返回键回到表单");
const productQuery = read("app/(shell)/product-query.tsx");
assert.match(productQuery, /<CodeAddSheet\s+visible=\{Boolean\(codeAddModal\)\}/, "新增弹层的可见性只由 codeAddModal 决定，不能因扫码页被卸载");
assert.doesNotMatch(productQuery, /codeAddModal && codeAddScannerVisible \?/, "不能再为新增多码另挂一个独立的扫码 Modal");
assert.match(productQuery, /if \(!codeAddModal\) \{\s*setCodeAddScannerVisible\(false\);/, "新增弹层关闭时必须复位扫码页状态");

console.log("camera-scan-library-source.test.ts: ok");

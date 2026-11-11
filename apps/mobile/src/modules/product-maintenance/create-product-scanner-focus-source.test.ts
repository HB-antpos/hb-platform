import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const currentDir = dirname(fileURLToPath(import.meta.url));
const productQuerySource = readFileSync(
  resolve(currentDir, "../../../app/(shell)/product-query.tsx"),
  "utf8",
);

// 商品查询页挂着一个隐藏的 HID 扫码输入框，启用时每 3 秒抢一次焦点。
// 创建商品弹层打开期间如果它仍然启用，用户正在输入的条码 / 货号 / 名称会被它抢走，
// 并被当成扫码结果触发查询（现象：光标自己跳走，输入内容跑到扫码流程里）。
// 所以创建流程的三个入口状态必须计入 isProductQueryBusy，让扫码器在其打开期间整体禁用。
const busySource =
  productQuerySource.match(
    /const isProductQueryBusy = useCallback\([\s\S]*?\n {2}\);\n/,
  )?.[0] ?? "";
assert.ok(busySource, "必须能定位 isProductQueryBusy");

const CREATE_FLOW_STATES = [
  "createProductVisible",
  "createBarcodeScannerVisible",
  "createSupplierPickerVisible",
];
for (const state of CREATE_FLOW_STATES) {
  const occurrences = busySource.match(new RegExp(`\\b${state}\\b`, "g")) ?? [];
  // 一次在布尔条件里，一次在 useCallback 依赖数组里；缺依赖会让回调读到过期值。
  assert.ok(
    occurrences.length >= 2,
    `${state} 必须同时出现在 isProductQueryBusy 的判断和依赖数组中`,
  );
}

// 扫码器启用条件必须由 isProductQueryBusy 驱动，否则上面的屏蔽不生效。
assert.match(
  productQuerySource,
  /const scannerInputBlocked = isProductQueryBusy\(\);/,
  "scannerInputBlocked 必须来自 isProductQueryBusy",
);
assert.match(
  productQuerySource,
  /enabled:\s*isFocused && !scannerInputBlocked,/,
  "HID 扫码器必须在 scannerInputBlocked 时禁用",
);
assert.match(
  productQuerySource,
  /isFocused &&\s*!scannerInputBlocked &&\s*hidScanner\.mode === "textInput"/,
  "隐藏扫码输入框必须在 scannerInputBlocked 时卸载",
);

console.log("create product scanner focus source contract passed");

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const sheet = readFileSync(resolve(directory, "BusinessSheet.tsx"), "utf8");
const productQuery = readFileSync(
  resolve(directory, "../../../app/(shell)/product-query.tsx"),
  "utf8",
);

// Android 边到边下 Modal 窗口不会随键盘缩放，behavior 为空会让键盘盖住底部弹层的输入框。
assert.match(
  sheet,
  /<KeyboardAvoidingView[^>]*behavior="padding"/,
  "BusinessSheet 在 Android 也必须用 padding 避让键盘",
);
assert.ok(
  !/Platform\.OS === "ios" \? "padding" : undefined/.test(sheet),
  "BusinessSheet 不能再让 Android 的键盘避让为空",
);
assert.match(sheet, /flexShrink: 1/, "键盘顶起后弹层要能收缩，内部 ScrollView 才能滚动");

// 页面 ScrollView 默认 never：键盘打开时点「添加」只会收键盘，按钮点不灵。
const pageScroll = productQuery.slice(
  productQuery.indexOf("<ScrollView\n        style={[styles.scrollArea"),
);
assert.ok(
  pageScroll.slice(0, pageScroll.indexOf(">\n")).includes('keyboardShouldPersistTaps="handled"'),
  "扫码查询页主 ScrollView 必须 keyboardShouldPersistTaps=handled",
);

console.log("business-sheet-keyboard-contract.test.ts: ok");

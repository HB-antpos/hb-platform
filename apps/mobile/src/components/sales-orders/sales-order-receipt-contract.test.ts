import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const read = (file: string) => readFileSync(resolve(directory, file), "utf8");
const sheet = read("SalesOrderDetailSheet.tsx");
const paper = read("ReceiptPaper.tsx");
const overlay = read("ReceiptQrOverlay.tsx");

// 默认停在「小票」页签；原商品明细保留为第二个页签。
assert.match(sheet, /useState<"receipt" \| "items">\("receipt"\)/, "默认页签是小票");
assert.match(sheet, /tab === "receipt" \? receiptContent : loading \?/, "商品明细仍走原有加载 / 错误 / 明细分支");

// 在线二维码只在店员点开时才生成链接，不在打开详情时白白生成。
assert.match(sheet, /if \(!receiptLink\) void ensureReceiptLink\(\)/, "首次打开二维码面板才生成链接");
// 分享用系统分享，同时给 message 与 url（Android 只认 message，iOS 认 url）。
assert.match(sheet, /Share\.share\(\{ message: link\.url, url: link\.url \}\)/, "系统分享同时带 message 与 url");
// 重新生成必须二次确认：旧链接会立即失效。
assert.match(sheet, /Alert\.alert\(t\("receipt\.regenerate"\), t\("receipt\.regenerateConfirm"\)/, "重新生成需确认");

// 二维码面板盖在详情面板里，不嵌套 Modal（iOS 上嵌套 Modal 的呈现不可靠）。
assert.ok(!/<Modal\b/.test(overlay), "二维码面板不得再嵌套 Modal");
assert.match(overlay, /StyleSheet\.absoluteFillObject/, "二维码面板用绝对定位盖在详情面板上");

// 纸面固定按版式换算字号，不跟随系统字体放大，否则 42 列会被撑出屏幕。
assert.match(paper, /allowFontScaling=\{false\}/, "纸面文字不随系统字号");
assert.match(paper, /numberOfLines=\{1\}/, "纸面每行不折行");

// 既有订单二维码（完整 orderGuid，收银台扫码退货）保持不变，见 sales-order-detail-qr-contract.test.ts。
assert.match(sheet, /<QrCodePanel[^>]*value=\{order\.orderGuid\}/, "订单二维码仍编码完整 orderGuid");

// 文案：页面里用到的 receipt.* 键在中英文里都必须存在。
const keys = new Set([...sheet.matchAll(/t\("receipt\.([A-Za-z]+)"/g)].map((match) => match[1]));
assert.ok(keys.size >= 15, `应使用到足够多的 receipt 文案，实际 ${keys.size}`);
for (const language of ["zh", "en"]) {
  const locale = JSON.parse(read(`../../locales/${language}/screens/salesOrders.json`));
  for (const key of keys) {
    assert.equal(typeof locale.receipt?.[key], "string", `${language} 缺少 receipt.${key}`);
  }
}

console.log("sales-order-receipt-contract.test.ts: ok");

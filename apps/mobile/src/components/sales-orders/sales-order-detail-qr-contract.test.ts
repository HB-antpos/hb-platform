import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const sheet = readFileSync(resolve(directory, "SalesOrderDetailSheet.tsx"), "utf8");
const panel = readFileSync(resolve(directory, "../ui/QrCodePanel.tsx"), "utf8");

// 载荷必须是完整 orderGuid：POS 小票上的机读码固定是它，收银台才能扫出同一笔订单。
assert.match(
  sheet,
  /<QrCodePanel[^>]*value=\{order\.orderGuid\}/,
  "订单详情的二维码必须编码完整 orderGuid",
);
// 订单头已显示 GUID 原文，二维码下方不再重复一遍。
assert.match(sheet, /<QrCodePanel[^>]*showValue=\{false\}/, "订单头已有 GUID，二维码不重复显示原文");
// 只在有订单号时渲染，避免出现空二维码占位。
assert.match(sheet, /order\?\.orderGuid \? \(\s*<QrCodePanel/, "没有订单号时不渲染二维码");
// showValue 默认 true，不能改变分期订单、代金券等其他页面已有的显示。
assert.match(panel, /showValue = true/, "QrCodePanel 的 showValue 默认必须为 true");

for (const language of ["zh", "en"]) {
  const locale = JSON.parse(
    readFileSync(resolve(directory, `../../locales/${language}/screens/salesOrders.json`), "utf8"),
  );
  assert.equal(locale.detail.orderQr, language === "zh" ? "订单二维码" : "Order QR code");
}

console.log("sales-order-detail-qr-contract.test.ts: ok");

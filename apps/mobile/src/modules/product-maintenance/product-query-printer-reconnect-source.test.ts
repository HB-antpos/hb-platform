import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

/**
 * 商品查询页「打印机重新连接」入口的源码契约（门店反馈：打印机一早上断开 6、7 次，每次要连点好几层设置才能重连）：
 * 1. 打印行状态图标与打印设置弹窗里的摘要都接上一键重连；
 * 2. 打印失败发生在「连接打印机」这一步（还没发出任何数据）时，提示条带「重新连接并重试」；
 *    写入途中失败、或本次已经打出过标签时，只给「重新连接」，绝不自动重印；
 * 3. 提示条的操作按钮只挂在它对应的那条提示上，不会串到别的提示。
 */
const currentDir = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(resolve(currentDir, "../../../app/(shell)/product-query.tsx"), "utf8");
const api = readFileSync(resolve(currentDir, "../printer/api.ts"), "utf8");

function readLocale(language: "zh" | "en") {
  return JSON.parse(
    readFileSync(resolve(currentDir, `../../locales/${language}/screens/productQuery.json`), "utf8"),
  ) as { messages: Record<string, unknown>; print: { printerStatus: Record<string, unknown> } };
}

// 1. 一键重连入口：图标与弹窗摘要都接上同一个处理函数。
assert.match(source, /const handleReconnectPrinter = useCallback\(async \(\) => \{[\s\S]*?await requestPrinterReconnect\(\);/, "重连处理必须走 requestPrinterReconnect");
assert.match(source, /<PrinterConnectionChip[\s\S]*?onReconnect=\{pendingScanPresentation \? undefined : \(\) => void handleReconnectPrinter\(\)\}/, "打印行状态图标要接一键重连（扫码打印进行中不可点）");
assert.match(source, /<PrinterConnectionSummary[\s\S]*?reconnectInfo=\{printerReconnectInfo\}[\s\S]*?onReconnect=\{\(\) => void handleReconnectPrinter\(\)\}/, "弹窗摘要要接重连按钮与自动重连进度");
assert.match(source, /const printerReconnectInfo = usePrinterStore\(\(state\) => state\.reconnectInfo\);/);

// 2. 打印失败提示：只有「连接这一步失败」才可能带重试；已经打出标签就不重试。
assert.match(
  source,
  /isPrinterConnectFailureBeforeSend\(error\)[\s\S]*?getPrintedLabelCount\(\) === options\.printedBefore \? options\.retry : null/,
  "重试必须同时满足：连接阶段失败 + 本次打印期间没有打出过标签",
);
assert.match(source, /messages\.reconnectAndRetryAction[\s\S]*?messages\.reconnectAction/, "有重试时显示「重新连接并重试」，否则只显示「重新连接」");
assert.match(
  source,
  /if \(!\(await handleReconnectPrinter\(\)\)\) return;\s*await retry\?\.\(\);/,
  "必须先重连成功才重试；重连失败不能硬打",
);
// 两个打印入口（sendProductLabel / handlePrint）失败都走统一提示，并在打印开始前记下已打出的标签数。
const printedBeforeCount = source.match(/const printedBefore = getPrintedLabelCount\(\);/g)?.length ?? 0;
assert.equal(printedBeforeCount, 2, "sendProductLabel 与 handlePrint 都要在打印前记录已打标签数");
assert.equal(source.match(/showPrintFailure\(error, "messages\.printFailed", \{/g)?.length, 2, "两处打印失败都走 showPrintFailure");
assert.match(source, /retry: \(\) => sendProductLabelRef\.current\?\.\(targetDetail, options\) \?\? Promise\.resolve\(false\)/);
assert.match(source, /retry: \(\) => handlePrintRef\.current\(kind, targetDetail\)/);
// 暂停自动重连时直接给「重新连接」，不再让人去设置页。
assert.match(source, /if \(printerAutoReconnectPaused\) \{[\s\S]*?setSnackbarAction\(\{[\s\S]*?message: pausedMessage/);

// 3. 提示条：操作按钮只在提示文字仍是当时那条时才显示，带按钮时留够 10 秒。
assert.match(source, /snackbarAction && snackbarAction\.message === snackbarMessage\s*\? \{ label: snackbarAction\.label, onPress: snackbarAction\.onPress \}\s*: undefined/);
assert.match(source, /snackbarAction && snackbarAction\.message === snackbarMessage\s*\? 10000/);
assert.match(source, /onDismiss=\{\(\) => \{\s*setSnackbarMessage\(""\);\s*setSnackbarAction\(null\);\s*\}\}/, "关闭提示时一并清掉操作按钮");

// 4. api 层：重连后必须通知自动重连重新开始；打印前连接失败要有标记；写入途中失败不标记。
assert.match(api, /export async function requestPrinterReconnect\(\)[\s\S]*?finally \{\s*usePrinterStore\.getState\(\)\.bumpRetryNonce\(\);/);
assert.match(api, /try \{\s*await ensureConnectedPrinter\(\{ preferHotWrite: true \}\);\s*\} catch \(error\) \{[\s\S]*?connectFailedBeforeSendErrors\.add\(error\);/, "只有补连接阶段的失败才标记为「还没发出任何数据」");

// 5. 文案：中英文都要有页面用到的键（parity 脚本只比对两边一致，这里确认键本身存在）。
for (const language of ["zh", "en"] as const) {
  const locale = readLocale(language);
  for (const key of ["printerReconnected", "printerReconnectFailed", "reconnectAction", "reconnectAndRetryAction"]) {
    assert.equal(typeof locale.messages[key], "string", `${language} messages.${key}`);
  }
  for (const key of ["reconnect", "reconnectHint", "errorHint"]) {
    assert.equal(typeof locale.print.printerStatus[key], "string", `${language} print.printerStatus.${key}`);
  }
  const retry = locale.print.printerStatus.retry as Record<string, unknown>;
  for (const key of ["backoff", "probeFast", "probeSlow"]) {
    assert.equal(typeof retry[key], "string", `${language} print.printerStatus.retry.${key}`);
  }
  assert.match(String(retry.backoff), /\{\{count\}\}/, "backoff 文案要带失败次数占位符");
}

console.log("product-query-printer-reconnect-source.test.ts: ok");

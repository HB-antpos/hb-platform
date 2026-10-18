import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { createScanTraceId, logScanPerformance } from "./scan-performance";

const source = readFileSync(join(__dirname, "use-camera-scan.ts"), "utf8");
const scanPerformanceSource = readFileSync(join(__dirname, "scan-performance.ts"), "utf8");
const productMaintenanceApiSource = readFileSync(
  join(__dirname, "../product-maintenance/api.ts"),
  "utf8"
);
const productQuerySource = readFileSync(
  join(__dirname, "../../../app/(shell)/product-query.tsx"),
  "utf8"
);

function extractConsoleCalls(targetSource: string): string[] {
  const calls: string[] = [];
  const consoleCallPattern = /console\.(?:log|debug|info|warn|error)\s*\(/g;

  for (const match of targetSource.matchAll(consoleCallPattern)) {
    const start = match.index;
    let depth = 1;
    let quote: "'" | '"' | "`" | null = null;
    let escaped = false;

    for (let index = start + match[0].length; index < targetSource.length; index += 1) {
      const character = targetSource[index];
      if (escaped) {
        escaped = false;
        continue;
      }
      if (quote && character === "\\") {
        escaped = true;
        continue;
      }
      if (quote) {
        if (character === quote) {
          quote = null;
        }
        continue;
      }
      if (character === "'" || character === '"' || character === "`") {
        quote = character;
        continue;
      }
      if (character === "(") {
        depth += 1;
      } else if (character === ")") {
        depth -= 1;
        if (depth === 0) {
          calls.push(targetSource.slice(start, index + 1));
          break;
        }
      }
    }
  }

  return calls;
}

assert.doesNotMatch(source, /console\.(?:log|debug|info|warn|error)/, "扫码 hook 不得输出扫描事件");
assert.doesNotMatch(source, /prefix|firstPart|summarizeBarcodeForLog/, "扫码 hook 不得保留条码片段摘要");
assert.match(
  source,
  /const barcode = restoreCameraBarcodeData\(type, normalizeBarcode\(data\)\);/,
  "扫码归一化（trim + 补回相机层丢掉的前导 0）步骤必须保留"
);
assert.match(source, /scanGateController\.tryStart/, "扫码 gate 必须保留");
assert.match(source, /await onBarcode\(barcode\);/, "扫码回调时序必须保留");
assert.match(source, /finally \{\s*scanGateController\.finish\(lease\);/s, "扫码 lease 必须继续在 finally 中释放");

const productQueryConsoleCalls = extractConsoleCalls(productQuerySource);
assert.ok(productQueryConsoleCalls.length > 0, "Product Query 日志检查必须命中现有 console 调用");
for (const call of productQueryConsoleCalls) {
  assert.doesNotMatch(
    call,
    /\b(?:barcode|keyword|productCode)\b/i,
    "Product Query 日志不得以任何参数形式引用条码、查询词或商品编码"
  );
  assert.doesNotMatch(call, /\.\.\.context\b/, "Product Query 日志不得展开可能包含商品编码的上下文");
}

assert.deepEqual(
  extractConsoleCalls(productMaintenanceApiSource),
  [],
  "商品维护 API 不得直接记录请求、响应、商品编码或查询词"
);
assert.doesNotMatch(
  scanPerformanceSource,
  /barcodeTail|slice\(\s*-\d+\s*\)/,
  "扫码性能日志不得保留条码尾号或其他片段"
);

const barcode = "BARCODE_SECRET_0123456789";
const productCode = "PRODUCT_SECRET_9876543210";
const keyword = "KEYWORD_SECRET_24680";
const traceId = createScanTraceId("camera", barcode);
assert.doesNotMatch(traceId, /SECRET|0123456789|23456789/, "traceId 不得包含扫码内容或其片段");

const capturedInfoCalls: unknown[][] = [];
const originalConsoleInfo = console.info;
console.info = (...args: unknown[]) => {
  capturedInfoCalls.push(args);
};
try {
  logScanPerformance("scan.test", {
    barcode,
    productCode,
    keyword,
    nested: {
      Barcode: barcode,
      ProductCode: productCode,
      Keyword: keyword,
    },
    status: "ok",
    traceId,
  });
} finally {
  console.info = originalConsoleInfo;
}

const serializedInfoCalls = JSON.stringify(capturedInfoCalls);
assert.doesNotMatch(
  serializedInfoCalls,
  /BARCODE_SECRET|0123456789|23456789|PRODUCT_SECRET|9876543210|KEYWORD_SECRET|24680/,
  "真实扫码日志出口不得包含查询词、商品编码、条码或任意片段"
);
assert.match(serializedInfoCalls, /scan\.test/, "隐私过滤后必须保留性能阶段");
assert.match(serializedInfoCalls, /\"status\":\"ok\"/, "隐私过滤后必须保留非敏感诊断字段");

console.log("camera-scan-privacy-source.test.ts: ok");

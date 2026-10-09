import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { join, relative } from "node:path";
import test from "node:test";

import ts from "typescript";

const projectRoot = new URL("../", import.meta.url);
const sourceRoots = ["src", "app"];
const placeholderPattern = /\{\{([A-Za-z][A-Za-z0-9_]*)\}\}/gu;

// 新增页面必须在这里登记其实际承载可见文案的 source，避免路由已注册而未纳入双语审计。
const registeredPageCopySurfaces = {
  "app/attendance-audit.tsx": "src/features/attendance-audit/attendance-audit-copy.ts",
  "app/catalog-maintenance.tsx": "../../packages/pos-domain/src/features/catalog/maintenance/catalog-maintenance-copy.ts",
  "app/daily-close.tsx": "src/features/daily-close/daily-close-copy.ts",
  "app/held-orders.tsx": "src/features/held-orders/held-orders-copy.ts",
  "app/index.tsx": "src/ui/screens/bootstrap-screen.tsx",
  "app/installments.tsx": "src/features/installments/installment-copy.ts",
  "app/local-history.tsx": "src/features/local-history/local-history-copy.ts",
  "app/login.tsx": "src/features/cashier-login/cashier-login-screen.tsx",
  "app/payment-recovery.tsx": "../../packages/pos-domain/src/features/payment-recovery/payment-recovery-copy.ts",
  "app/payment.tsx": "src/features/payments/ui/payment-copy.ts",
  "app/registration.tsx": "src/features/device-registration/device-registration-screen.tsx",
  "app/remote-history.tsx": "src/features/remote-history/remote-history-copy.ts",
  "app/returns.tsx": "src/features/returns/return-copy.ts",
  "app/sales.tsx": "src/features/sales/ui/sales-copy.ts",
  "app/settings.tsx": "src/features/settings/settings-copy.ts",
  "app/special-products.tsx": "src/features/special-products/special-products-copy.ts",
  "app/sync-history.tsx": "src/features/sync-history/sync-history-copy.ts",
  "app/update-recovery.tsx": "src/features/app-updates/app-update-recovery-screen.tsx",
};

test("全局 i18n 中英文键及插值参数完全一致", async () => {
  const [english, chinese] = await Promise.all([
    readJson("src/i18n/locales/en.json"),
    readJson("src/i18n/locales/zh.json"),
  ]);

  assertCopyParity("src/i18n/locales", english, chinese);
  assertNoBilingualCopy("src/i18n/locales/en.json", english);
  assertNoBilingualCopy("src/i18n/locales/zh.json", chinese);
});

test("手持端全局文案不再暴露 iPad 或客显身份", async () => {
  const [englishSource, chineseSource] = await Promise.all([
    readFile(new URL("src/i18n/locales/en.json", projectRoot), "utf8"),
    readFile(new URL("src/i18n/locales/zh.json", projectRoot), "utf8"),
  ]);
  for (const [path, source] of [
    ["src/i18n/locales/en.json", englishSource],
    ["src/i18n/locales/zh.json", chineseSource],
  ]) {
    assert.doesNotMatch(
      source,
      /\bipad\b|customer[-_ ]?display|external[-_ ]?display|客显/iu,
      `${path} 只能使用 handheld/device 或 手持终端 身份`,
    );
  }
});

test("所有显式双语 copy 表保持键和插值参数一致", async () => {
  const files = await copyAuditFiles();
  let checkedPairs = 0;
  for (const file of files) {
    const sourceText = await readFile(file, "utf8");
    const source = ts.createSourceFile(
      file,
      sourceText,
      ts.ScriptTarget.Latest,
      true,
      file.endsWith(".tsx") ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
    );
    const namedCopies = collectNamedCopyObjects(source);
    for (const [prefix, english] of namedCopies.english) {
      const chinese = namedCopies.chinese.get(prefix);
      if (!chinese) continue;
      assertCopyParity(relativePath(file), english, chinese);
      checkedPairs += 1;
    }
    visit(source, (node) => {
      if (!ts.isObjectLiteralExpression(node)) return;
      const englishNode = objectProperty(node, "en");
      const chineseNode = objectProperty(node, "zh");
      if (!englishNode || !chineseNode) return;
      const english = literalCopy(englishNode);
      const chinese = literalCopy(chineseNode);
      if (!english || !chinese) return;
      assertCopyParity(relativePath(file), english, chinese);
      checkedPairs += 1;
    });
  }
  assert.ok(checkedPairs >= 6, "应至少核对六组功能双语 copy 表");
});

test("使用 react-i18next 的字面量键都存在于两种全局语言", async () => {
  const [english, chinese, files] = await Promise.all([
    readJson("src/i18n/locales/en.json"),
    readJson("src/i18n/locales/zh.json"),
    sourceFiles(),
  ]);
  for (const file of files) {
    const sourceText = await readFile(file, "utf8");
    if (
      !sourceText.includes("react-i18next") ||
      !/const\s*\{[^}]*\bt\b[^}]*\}\s*=\s*useTranslation\s*\(/u.test(
        sourceText,
      )
    ) {
      continue;
    }
    const source = ts.createSourceFile(
      file,
      sourceText,
      ts.ScriptTarget.Latest,
      true,
      file.endsWith(".tsx") ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
    );
    visit(source, (node) => {
      if (
        !ts.isCallExpression(node) ||
        !ts.isIdentifier(node.expression) ||
        node.expression.text !== "t"
      ) {
        return;
      }
      const key = node.arguments[0];
      if (!key || !ts.isStringLiteralLike(key)) return;
      assert.ok(
        hasGlobalKey(english, key.text),
        `${relativePath(file)} 缺少英文全局键 ${key.text}`,
      );
      assert.ok(
        hasGlobalKey(chinese, key.text),
        `${relativePath(file)} 缺少中文全局键 ${key.text}`,
      );
    });
  }
});

test("所有已注册 POS 页面均登记到可审计的可见文案 source", async () => {
  const routeFiles = (await sourceFiles())
    .map(relativePath)
    .filter((file) => file.startsWith("app/") && file.endsWith(".tsx"))
    .filter((file) => !/\.(?:test|spec|rntl)\.tsx$/u.test(file))
    .filter((file) => file !== "app/_layout.tsx")
    .sort();
  const registeredRoutes = Object.keys(registeredPageCopySurfaces).sort();

  assert.deepEqual(
    routeFiles,
    registeredRoutes,
    "新增或移除 POS 路由时必须同步更新双语文案覆盖登记",
  );

  for (const [route, surface] of Object.entries(registeredPageCopySurfaces)) {
    const source = await readFile(new URL(surface, projectRoot), "utf8");
    assert.ok(
      source.trim().length > 0,
      `${route} 的可见文案 source 不得为空: ${surface}`,
    );
  }
});

test("页面、弹窗与无障碍文案不得把中英文翻译拼在同一字面量", async () => {
  const files = await copyAuditFiles();
  const uiFiles = files.filter((file) => {
    const path = relativePath(file);
    return (
      path.endsWith(".tsx") &&
      !/\.(?:test|spec|rntl)\.tsx$/u.test(path)
    );
  });
  // 弹窗和共享组件不一定对应路由，统一扫描所有 typed copy，避免登记遗漏。
  const copyFiles = files.filter((file) =>
    relativePath(file).endsWith("-copy.ts"),
  );
  const failures = [];

  for (const file of new Set([...uiFiles, ...copyFiles])) {
    const sourceText = await readFile(file, "utf8");
    const source = ts.createSourceFile(
      file,
      sourceText,
      ts.ScriptTarget.Latest,
      true,
      file.endsWith(".tsx") ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
    );
    visit(source, (node) => {
      const text = visibleLiteralText(node, source);
      if (text === null || !isBilingualTranslationPair(text)) return;
      const { line } = source.getLineAndCharacterOfPosition(
        node.getStart(source),
      );
      failures.push(
        `${relativePath(file)}:${line + 1} ${JSON.stringify(
          text.replace(/\s+/gu, " ").trim(),
        )}`,
      );
    });
  }

  assert.deepEqual(
    failures,
    [],
    `操作员界面仍存在中英文同屏文案：\n${failures.join("\n")}`,
  );
});

test("双语同屏识别覆盖常见分隔形式，并允许品牌与协议缩写", () => {
  for (const value of [
    "中文 / English",
    "中文/English",
    "中文（English）",
    "English (中文)",
    "中文\nEnglish",
    "中文 English",
    "English 中文",
  ]) {
    assert.equal(isBilingualTranslationPair(value), true, value);
  }

  for (const value of [
    "请在 iPad 设置中允许相机",
    "此设备或 Development Build 未提供相机能力",
    "门店 / 设备",
    "支付方式：Square",
  ]) {
    assert.equal(isBilingualTranslationPair(value), false, value);
  }
});

test("稳定错误码与票据文案均保持可审计的双语覆盖", async () => {
  const errorCopySources = [
    ["src/features/payments/ui/payment-copy.ts", 40],
    ["src/features/returns/return-copy.ts", 15],
  ];
  for (const [path, minimumKeyCount] of errorCopySources) {
    const keys = await literalKeysWithPrefix(path, "error.");
    assert.ok(
      keys.size >= minimumKeyCount,
      `${path} 应保留稳定、面向操作员的错误码文案`,
    );
    for (const key of keys) {
      assert.doesNotMatch(
        key,
        /(?:token|secret|password|stack|exception)/iu,
        `${path} 不得把内部敏感信息作为可见错误键`,
      );
    }
  }

  const receiptSources = [
    "../../packages/pos-receipt-core/src/features/receipts/receipt-document.ts",
    "../../packages/pos-receipt-core/src/features/receipts/refund-voucher-receipt-renderer.ts",
    "../../packages/pos-receipt-core/src/features/receipts/return-receipt-renderer.ts",
  ];
  for (const path of receiptSources) {
    const source = await readFile(new URL(path, projectRoot), "utf8");
    assert.match(source, /zh-CN/u, `${path} 必须保留中文票据分支`);
    assert.match(source, /en/u, `${path} 必须保留英文票据分支`);
  }

  const receiptDocument = await readFile(
    new URL(
      "../../packages/pos-receipt-core/src/features/receipts/receipt-document.ts",
      projectRoot,
    ),
    "utf8",
  );
  assert.match(
    receiptDocument,
    /b\.wrap\(sanitizeBankText\(raw\)\)/u,
    "银行卡回单必须先脱敏再打印原始银行文本",
  );
});

async function readJson(path) {
  return JSON.parse(await readFile(new URL(path, projectRoot), "utf8"));
}

async function sourceFiles() {
  const files = [];
  for (const root of sourceRoots) {
    await walk(new URL(`${root}/`, projectRoot), files);
  }
  return files.filter((file) => /\.(?:ts|tsx)$/u.test(file));
}

async function copyAuditFiles() {
  const files = await sourceFiles();
  const registeredSurfaces = Object.values(registeredPageCopySurfaces).map(
    (surface) => new URL(surface, projectRoot).pathname,
  );
  return [...new Set([...files, ...registeredSurfaces])];
}

async function walk(directoryUrl, files) {
  const entries = await readdir(directoryUrl, { withFileTypes: true });
  for (const entry of entries) {
    const path = join(directoryUrl.pathname, entry.name);
    if (entry.isDirectory()) {
      await walk(new URL(`${path}/`, "file://"), files);
    } else {
      files.push(path);
    }
  }
}

function collectNamedCopyObjects(source) {
  const english = new Map();
  const chinese = new Map();
  visit(source, (node) => {
    if (!ts.isVariableDeclaration(node) || !ts.isIdentifier(node.name)) {
      return;
    }
    const match = /^(.*)(English|Chinese)Copy$/u.exec(node.name.text);
    if (!match || !node.initializer) return;
    const copy = literalCopy(node.initializer);
    if (!copy) return;
    (match[2] === "English" ? english : chinese).set(match[1], copy);
  });
  return { english, chinese };
}

function literalCopy(expression) {
  const value = unwrapExpression(expression);
  if (!ts.isObjectLiteralExpression(value)) return null;
  const copy = {};
  for (const property of value.properties) {
    if (!ts.isPropertyAssignment(property)) return null;
    const key = propertyName(property.name);
    const text = literalText(property.initializer);
    if (key === null || text === null) return null;
    copy[key] = text;
  }
  return copy;
}

function unwrapExpression(expression) {
  let current = expression;
  while (
    ts.isAsExpression(current) ||
    ts.isSatisfiesExpression(current) ||
    ts.isParenthesizedExpression(current)
  ) {
    current = current.expression;
  }
  return current;
}

function literalText(expression) {
  const value = unwrapExpression(expression);
  return ts.isStringLiteralLike(value) ||
    ts.isNoSubstitutionTemplateLiteral(value)
    ? value.text
    : null;
}

function visibleLiteralText(node, source) {
  if (ts.isJsxText(node)) {
    return node.getText(source);
  }
  if (ts.isTemplateExpression(node)) {
    return node.getText(source);
  }
  if (
    ts.isStringLiteralLike(node) &&
    !ts.isImportDeclaration(node.parent) &&
    !ts.isExportDeclaration(node.parent)
  ) {
    return node.text;
  }
  return null;
}

function isBilingualTranslationPair(value) {
  const text = String(value)
    .replace(/\{\{[^}]*\}\}/gu, "")
    .replace(/\$\{[^}]*\}/gu, "")
    .trim();
  const languageText = text
    .replace(
      /\b(?:API|AUD|App Store|Development Build|ETag|HB|HID|ID|iPadOS|iPad|iOS|Linkly|OPENITEM|PAN|Permissions(?:\.[A-Za-z]+)+|POS|QR|SQLCipher|Square|token|UUID|UTC|WPF|YYYY-MM-DD)\b/gu,
      "",
    )
    .trim();
  if (!hasHan(languageText) || !hasLatin(languageText)) {
    return false;
  }

  const separated = languageText.split(
    /(?<!:)\s*\/\s*(?!\/)|\s*\|\s*|\r?\n/gu,
  );
  if (hasOpposingLanguageSegments(separated)) return true;

  for (const match of languageText.matchAll(
    /([^()（）]+)[(（]([^()（）]+)[)）]/gu,
  )) {
    if (hasOpposingLanguageSegments([match[1], match[2]])) return true;
  }

  const collapsed = languageText.replace(/\s+/gu, " ").trim();
  return (
    /^[\p{Script=Han}\p{N}\p{P}\p{S}\s]+ [A-Za-z][A-Za-z0-9 &'’.-]*$/u.test(
      collapsed,
    ) ||
    /^[A-Za-z][A-Za-z0-9 &'’.-]* [\p{Script=Han}\p{N}\p{P}\p{S}\s]+$/u.test(
      collapsed,
    )
  );
}

function hasOpposingLanguageSegments(segments) {
  return segments.some(
    (segment, index) =>
      hasHan(segment) &&
      segments.some(
        (candidate, candidateIndex) =>
          candidateIndex !== index && hasLatin(candidate) && !hasHan(candidate),
      ),
  );
}

function hasHan(value) {
  return /\p{Script=Han}/u.test(value);
}

function hasLatin(value) {
  return /[A-Za-z]/u.test(value);
}

function objectProperty(object, name) {
  const property = object.properties.find(
    (candidate) =>
      ts.isPropertyAssignment(candidate) &&
      propertyName(candidate.name) === name,
  );
  return property?.initializer ?? null;
}

function propertyName(name) {
  if (
    ts.isIdentifier(name) ||
    ts.isStringLiteralLike(name) ||
    ts.isNumericLiteral(name)
  ) {
    return name.text;
  }
  return null;
}

function assertCopyParity(label, english, chinese) {
  const englishKeys = Object.keys(english).sort();
  const chineseKeys = Object.keys(chinese).sort();
  assert.deepEqual(
    chineseKeys,
    englishKeys,
    `${label} 的中英文键不一致`,
  );
  for (const key of englishKeys) {
    assert.deepEqual(
      placeholders(chinese[key]),
      placeholders(english[key]),
      `${label} 的 ${key} 插值参数不一致`,
    );
  }
}

function assertNoBilingualCopy(label, copy) {
  const failures = Object.entries(copy)
    .filter(([, value]) => isBilingualTranslationPair(value))
    .map(([key, value]) => `${key}: ${JSON.stringify(value)}`);
  assert.deepEqual(
    failures,
    [],
    `${label} 仍存在中英文同屏文案：\n${failures.join("\n")}`,
  );
}

function placeholders(value) {
  return [...String(value).matchAll(placeholderPattern)]
    .map((match) => match[1])
    .sort();
}

function hasGlobalKey(copy, key) {
  return (
    Object.hasOwn(copy, key) ||
    (Object.hasOwn(copy, `${key}_one`) &&
      Object.hasOwn(copy, `${key}_other`))
  );
}

async function literalKeysWithPrefix(path, prefix) {
  const sourceText = await readFile(new URL(path, projectRoot), "utf8");
  const source = ts.createSourceFile(
    path,
    sourceText,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TS,
  );
  const keys = new Set();
  visit(source, (node) => {
    if (!ts.isPropertyAssignment(node)) return;
    const key = propertyName(node.name);
    if (key?.startsWith(prefix)) keys.add(key);
  });
  return keys;
}

function visit(node, listener) {
  listener(node);
  node.forEachChild((child) => visit(child, listener));
}

function relativePath(file) {
  return relative(projectRoot.pathname, file);
}

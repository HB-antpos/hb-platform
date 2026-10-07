// 现金模块的源码契约测试：
// 1. 任何文件里都不得出现被禁用的 T2 别名（界面文案、i18n、代码标识符、注释一律只叫 T2）；
// 2. 页面与组件（.tsx）里不得硬编码中文文案，业务文案必须走 i18n（运行时日志等开发者可见文本不在此限）；
// 3. 代码里用到的每个文案键，中英文都必须有；
// 4. 路由薄壳只做转发。
import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { CASH_ERROR_CODES, CASH_EXPENSE_CATEGORIES } from "./constants";
import { BALANCE_ISSUE_CODES, DEPOSIT_ISSUE_CODES, EXPENSE_ISSUE_CODES, MONEY_ISSUES } from "./issue-messages";

const moduleDir = dirname(fileURLToPath(import.meta.url));
const mobileRoot = resolve(moduleDir, "../../..");

// 被禁用的叫法用转义拼出来，避免本测试文件自己违反规则
const FORBIDDEN_TERMS = [String.fromCharCode(0x5206, 0x7ea2), ["divi", "dend"].join("")];

function listFiles(directory: string, predicate: (name: string) => boolean): string[] {
  return readdirSync(directory).flatMap((name) => {
    const full = join(directory, name);
    if (statSync(full).isDirectory()) return listFiles(full, predicate);
    return predicate(name) ? [full] : [];
  });
}

const moduleFiles = listFiles(moduleDir, () => true);
const routeDir = join(mobileRoot, "app/(shell)/store-cash");
const routeFiles = listFiles(routeDir, () => true);
const localeFiles = ["zh", "en"].map((lang) => join(mobileRoot, `src/locales/${lang}/screens/storeCash.json`));

// ───────── 1. 禁用叫法 ─────────
for (const file of [...moduleFiles, ...routeFiles, ...localeFiles]) {
  const source = readFileSync(file, "utf8").toLowerCase();
  for (const term of FORBIDDEN_TERMS) {
    assert.equal(source.includes(term.toLowerCase()), false, `${file} 含有被禁用的叫法`);
  }
}

// ───────── 2. 组件里不硬编码中文 ─────────
function stripComments(source: string): string {
  return source.replace(/\/\*[\s\S]*?\*\//g, "").replace(/(^|[^:])\/\/.*$/gm, "$1");
}
const CJK = /[㐀-鿿＀-￯]/;
const testFilePattern = /\.test\.tsx?$/;
for (const file of [...moduleFiles, ...routeFiles].filter((name) => /\.tsx$/.test(name) && !testFilePattern.test(name))) {
  const code = stripComments(readFileSync(file, "utf8"));
  const offending = code.split("\n").find((line) => CJK.test(line));
  assert.equal(offending, undefined, `${file} 硬编码了中文：${offending?.trim()}`);
}

// ───────── 3. 文案键中英文齐全 ─────────
type Tree = { [key: string]: string | Tree };
const zh = JSON.parse(readFileSync(localeFiles[0], "utf8")) as Tree;
const en = JSON.parse(readFileSync(localeFiles[1], "utf8")) as Tree;
const common = JSON.parse(readFileSync(join(mobileRoot, "src/locales/zh/common.json"), "utf8")) as Tree;

function lookup(tree: Tree, key: string): string | Tree | undefined {
  return key.split(".").reduce<string | Tree | undefined>(
    (node, part) => (node && typeof node === "object" ? node[part] : undefined),
    tree,
  );
}

const staticKeys = new Set<string>();
for (const file of moduleFiles.filter((name) => /\.tsx?$/.test(name) && !testFilePattern.test(name))) {
  const code = stripComments(readFileSync(file, "utf8"));
  for (const match of code.matchAll(/\bt\(\s*"([^"]+)"/g)) staticKeys.add(match[1]);
  for (const match of code.matchAll(/fallbackKey: "([^"]+)"/g)) staticKeys.add(match[1]);
}
assert.ok(staticKeys.size > 100, "应当扫描到大量文案键");
for (const key of staticKeys) {
  if (key.startsWith("common:")) {
    assert.equal(typeof lookup(common, key.slice("common:".length)), "string", `common 缺少 ${key}`);
  } else if (key.startsWith("storeCash:")) {
    const inner = key.slice("storeCash:".length);
    assert.equal(typeof lookup(zh, inner), "string", `zh 缺少 ${key}`);
    assert.equal(typeof lookup(en, inner), "string", `en 缺少 ${key}`);
  } else if (/^actions\./.test(key)) {
    // ui.tsx 通过 common 命名空间取通用按钮文案
    assert.equal(typeof lookup(common, key), "string", `common 缺少 ${key}`);
  } else {
    assert.equal(typeof lookup(zh, key), "string", `zh 缺少 ${key}`);
    assert.equal(typeof lookup(en, key), "string", `en 缺少 ${key}`);
  }
}

// 动态拼接的键族：逐个枚举核对
const dynamicKeys: string[] = [
  ...CASH_EXPENSE_CATEGORIES.map((category) => `categories.${category}`),
  ...[0, 1, 2, 3, 4, 5, 6].map((day) => `weekdays.${day}`),
  ...["deposits", "expenses", "entries"].map((kind) => `records.kinds.${kind}`),
  ...["last30", "last90", "all"].map((range) => `records.ranges.${range}`),
  ...["Opening", "Count"].map((type) => `records.entryTypes.${type}`),
  ...["balanced", "over", "short"].map((kind) => `balance.outcome.${kind}`),
  ...["decode_failed", "blob_read_failed", "file_too_large"].map((code) => `photo.errors.${code}`),
  ...CASH_ERROR_CODES.map((code) => `errors.codes.${code}`),
  ...MONEY_ISSUES.map((issue) => `errors.money.${issue}`),
  ...DEPOSIT_ISSUE_CODES.map((code) => `errors.deposit.${code}`),
  ...EXPENSE_ISSUE_CODES.map((code) => `errors.expense.${code}`),
  ...BALANCE_ISSUE_CODES.map((code) => `errors.balance.${code}`),
];
for (const key of dynamicKeys) {
  assert.equal(typeof lookup(zh, key), "string", `zh 缺少 ${key}`);
  assert.equal(typeof lookup(en, key), "string", `en 缺少 ${key}`);
}

// 类别文案固定：「现金工资」「现金购物」「T2」「其他」
assert.deepEqual(
  CASH_EXPENSE_CATEGORIES.map((category) => lookup(zh, `categories.${category}`)),
  ["现金工资", "现金购物", "T2", "其他"],
);
assert.equal(lookup(en, "categories.T2"), "T2", "英文界面 T2 同样只叫 T2");

// ───────── 4. 路由薄壳 ─────────
const expectedRoutes = ["_layout.tsx", "index.tsx", "deposit-new.tsx", "expense-new.tsx", "balance-new.tsx", "record-detail.tsx"];
for (const name of expectedRoutes) {
  const file = join(routeDir, name);
  assert.equal(existsSync(file), true, `缺少路由文件 ${name}`);
  const source = readFileSync(file, "utf8");
  assert.ok(source.split("\n").length <= 15, `${name} 应只做转发`);
  if (name !== "_layout.tsx") {
    assert.match(source, /from "@\/modules\/store-cash\//, `${name} 应转发到 store-cash 模块`);
  }
}

console.log("source-contract.test.ts: ok");

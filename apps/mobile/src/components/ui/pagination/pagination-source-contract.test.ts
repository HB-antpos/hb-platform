import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";

// 源码契约：分页条必须由 props 渲染每页条数选项，并同时提供上一页 / 下一页 / 跳页入口；
// 跳页面板含页码输入与页码格子。组件依赖 RN，这里只做源码级断言。
const read = (name: string) => readFileSync(join(__dirname, name), "utf8");
const bar = read("PaginationBar.tsx");
const sheet = read("PageJumpSheet.tsx");

assert.match(bar, /pageSizeOptions\.map\(/, "每页条数选项必须来自 pageSizeOptions prop");
assert.doesNotMatch(bar, /\[\s*50\s*,\s*100/, "分页条内不得写死 50/100/200");
assert.match(bar, /onPageChange\(current - 1\)/, "缺少上一页");
assert.match(bar, /onPageChange\(current \+ 1\)/, "缺少下一页");
assert.match(bar, /-jump`/, "缺少页码选择（跳页）入口");
assert.match(bar, /<PageJumpSheet/, "点页码选择器应打开跳页面板");
assert.match(bar, /pagination\.summary/, "缺少「共 N 条 · 显示 a–b」说明");
assert.match(bar, /pagination\.perPage/, "缺少「每页」标签");
assert.match(bar, /disabled=\{locked \|\| current <= 1\}/, "首页必须禁用上一页");
assert.match(bar, /disabled=\{locked \|\| current >= pageCount\}/, "末页必须禁用下一页");
assert.match(bar, /CONTROL_SIZE = 36/, "触控目标不得小于 36");

assert.match(sheet, /pageSizeOptions\.map\(/, "跳页面板的每页条数也来自 props");
assert.match(sheet, /parsePageInput\(input, pageCount\)/, "跳页输入必须校验范围");
assert.match(sheet, /getPageGridNumbers\(pageCount\)/, "页数不多时应有页码格子");

// 国际化键必须在公共 common 命名空间里中英文都有
for (const locale of ["zh", "en"]) {
  const common = JSON.parse(readFileSync(join(__dirname, `../../../locales/${locale}/common.json`), "utf8")) as { pagination?: Record<string, string> };
  for (const key of ["previous", "next", "pageIndicator", "pageIndicatorLabel", "summary", "summaryEmpty", "perPage", "perPageOption", "jumpTitle", "jumpSubtitle", "jumpPlaceholder", "jumpOf", "confirm", "invalidPage", "pageSizeTitle", "pageSizeHint"]) {
    assert.ok(common.pagination?.[key], `${locale}/common.json 缺少 pagination.${key}`);
  }
}

console.log("pagination source contract tests passed");

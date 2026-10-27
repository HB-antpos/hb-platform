import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// 页面源码契约：自动定价开关开启、关闭都必须即时写库。
// 关闭只改本地状态时，保存前的重新扫码/刷新会把库里仍为「开」的值读回来，开关就会自己弹回去。
const currentDir = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(resolve(currentDir, "../../../app/(shell)/product-query.tsx"), "utf8");
const start = source.indexOf("const handleToggleAutoPricing = useCallback(");
assert.notEqual(start, -1, "自动定价开关处理函数必须存在");
const handler = source.slice(start, source.indexOf("const handleChangeStorePurchasePrice = useCallback(", start));

const offBranchStart = handler.indexOf("if (!value) {");
const onCallIndex = handler.indexOf("maybeHandleAutoPricing(detail, { forceAutoPricing: true })");
assert.ok(offBranchStart !== -1 && onCallIndex > offBranchStart, "必须区分关闭与开启两条分支");
const offBranch = handler.slice(offBranchStart, onCallIndex);

assert.match(offBranch, /persistStorePrice\(detail, \{ isAutoPricing: false \}\)/, "关闭必须立即调用保存接口写库");
assert.doesNotMatch(offBranch, /handleChangeStorePrice\(/, "关闭不能只改本地状态");
assert.match(offBranch, /if \(saving\)\s*\{\s*return;\s*\}/, "保存进行中要忽略重复点击");
assert.match(offBranch, /finally\s*\{\s*setSaving\(false\);/, "无论成败都要释放保存锁");

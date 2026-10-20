import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { getRetailPriceEditBlock } from "./retail-price-edit-guard";

// 自动定价开启：零售价由规则重算，手改会被覆盖，必须先关闭自动定价。
assert.equal(getRetailPriceEditBlock({ isAutoPricing: true }), "autoPricingOn", "自动定价开启时应拦截");

// 自动定价关闭 / 没有分店价：可以手改。
assert.equal(getRetailPriceEditBlock({ isAutoPricing: false }), null, "自动定价关闭时放行");
assert.equal(getRetailPriceEditBlock({ isAutoPricing: null }), null, "开关未知（null）按关闭处理，不误拦");
assert.equal(getRetailPriceEditBlock({}), null, "缺少开关字段按关闭处理");
assert.equal(getRetailPriceEditBlock(null), null, "没有分店价时不拦截（由页面本身的空状态处理）");
assert.equal(getRetailPriceEditBlock(undefined), null, "没有分店价时不拦截（由页面本身的空状态处理）");

// 页面源码契约：门禁必须在打开零售价输入框之前，并且给出提示、不能悄悄关掉开关。
const currentDir = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(resolve(currentDir, "../../../app/(shell)/product-query.tsx"), "utf8");
const editorStart = source.indexOf("const openStoreRetailPriceEditor = useCallback(");
assert.notEqual(editorStart, -1, "零售价编辑入口必须存在");
const editorSource = source.slice(editorStart, source.indexOf("const openStoreDiscountPercentEditor = useCallback(", editorStart));
const guardIndex = editorSource.indexOf("getRetailPriceEditBlock(detail?.storePrice)");
const modalIndex = editorSource.indexOf("openNumericInputModal({");
assert.ok(guardIndex !== -1 && modalIndex !== -1 && guardIndex < modalIndex, "必须先判断自动定价，再打开输入框");
assert.match(editorSource, /setSnackbarMessage\(t\("storePrice\.autoPricingBlocksRetailEdit"\)\)/, "拦截时必须给出提示");
assert.doesNotMatch(editorSource, /isAutoPricing: false/, "不能替用户悄悄关闭自动定价");

// 其它改价入口不受影响：进货价、折扣率、折后价不改零售价，不应被这条门禁拦截。
assert.equal((source.match(/getRetailPriceEditBlock\(/g) ?? []).length, 1, "门禁只用于零售价编辑入口");

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const currentDir = dirname(fileURLToPath(import.meta.url));
const productQuerySource = readFileSync(
  resolve(currentDir, "../../../app/(shell)/product-query.tsx"),
  "utf8"
);
const dialogSource = readFileSync(
  resolve(currentDir, "../../components/product-maintenance/CreateProductDialog.tsx"),
  "utf8"
);

// 创建弹层是 Paper Modal，页面级 Snackbar 渲染在它下面，弹层打开时根本看不到。
// 弹层内发生的校验失败、创建失败、条码生成失败、供应商加载失败，必须显示在弹层里。
// 2026-10-14 门店把货号填成 4 位，提示被盖住，用户只看到「点了没反应」。
const FAILURE_KEYS = [
  "createProduct.messages.createFailed",
  "createProduct.messages.barcodeGenerateFailed",
  "createProduct.messages.suppliersLoadFailed",
];
for (const key of FAILURE_KEYS) {
  assert.doesNotMatch(
    productQuerySource,
    new RegExp(`setSnackbarMessage\\(\\s*(?:\\n\\s*)?getErrorMessage\\([^)]*${key.replace(/\./g, "\\.")}`),
    `${key} 发生在创建弹层打开期间，不能再走会被弹层盖住的页面 Snackbar`
  );
}

const submitSource = productQuerySource.match(
  /const handleCreateProductSubmit = useCallback\([\s\S]*?\n  \]\);\n\n  const persistStorePrice/
)?.[0] ?? "";
assert.ok(submitSource, "必须能定位创建商品的提交处理函数");
assert.match(
  submitSource,
  /showCreateProductError\(\s*t\(`createProduct\.messages\.\$\{validation\.reason\}`\),\s*validation\.field,?\s*\)/,
  "校验失败必须把提示和出错字段一起交给弹层"
);
assert.doesNotMatch(
  submitSource.slice(0, submitSource.indexOf("createProductWithPrices(")),
  /setSnackbarMessage/,
  "提交前的校验失败不能走页面 Snackbar"
);
assert.match(
  submitSource,
  /resolveCreateProductServerError\(error\)/,
  "创建失败要先识别能指向输入框的服务端冲突（如货号已存在）"
);

// 用户一改表单，旧错误要撤掉；打开、关闭、创建成功也要清空，避免红字残留到下一次。
for (const marker of [
  /const updateCreateProductDraft = useCallback\([\s\S]*?setCreateProductError\(null\)/,
  /const closeCreateProductModal = useCallback\([\s\S]*?setCreateProductError\(null\)/,
  /const openCreateProductModal = useCallback\([\s\S]*?setCreateProductError\(null\)/,
]) {
  assert.match(productQuerySource, marker, "弹层错误必须在编辑、关闭、打开时清空");
}
assert.match(productQuerySource, /error=\{createProductError\}/, "页面必须把错误传给创建弹层");

// 弹层本身：底栏固定位置展示错误（alert 角色），并为六个字段全部记录位置、支持标红。
assert.match(dialogSource, /props\.error \? \([\s\S]*?accessibilityRole="alert"/, "弹层底栏必须以 alert 角色展示错误");
assert.match(dialogSource, /styles\.errorText/, "错误文案必须使用独立的错误样式");
for (const field of ["supplier", "productName", "itemNumber", "barcode", "purchasePrice", "retailPrice"]) {
  assert.match(dialogSource, new RegExp(`trackY\\([^)]*"${field}"`), `${field} 必须记录纵坐标以便滚动定位`);
}
for (const field of ["productName", "itemNumber", "barcode", "purchasePrice", "retailPrice"]) {
  assert.match(dialogSource, new RegExp(`invalid=\\{errorField === "${field}"\\}`), `${field} 出错时必须标红`);
}
assert.match(dialogSource, /errorField === "supplier" && styles\.invalidBorder/, "供应商出错时必须标红");
assert.match(dialogSource, /scrollRef\.current\?\.scrollTo/, "出错时必须滚动到出错字段");

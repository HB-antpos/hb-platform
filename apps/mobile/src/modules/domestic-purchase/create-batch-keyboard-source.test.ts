import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const keyboard = readFileSync(resolve(directory, "CreateBatchNumberInput.tsx"), "utf8");
const modal = readFileSync(resolve(directory, "CreateBatchModal.tsx"), "utf8");
const editor = readFileSync(resolve(directory, "CreateBatchProductEditor.tsx"), "utf8");

assert.ok(keyboard.includes('focused && keyboardVisible'), "数字键盘可见时在弹层内显示操作栏");
assert.ok(keyboard.includes('Keyboard.addListener("keyboardDidShow"') && keyboard.includes('shown.remove(); hidden.remove();'), "键盘显示状态随系统事件更新且卸载时清理监听");
assert.ok(keyboard.includes('entry.input.focus()'), "下一步必须真正聚焦下一输入框");
assert.ok(keyboard.includes('entry.group === current.group'), "虚拟化列表中的数字焦点只在当前商品内切换");
assert.ok(keyboard.includes('returnKeyType={last ? "done" : "next"}'), "当前商品最后一个字段明确显示完成");
assert.ok(editor.includes('product.subItemsExpanded ?? true'), "滚动卸载后仍从草稿恢复套装折叠状态");
assert.ok(keyboard.includes('?.input.blur()') && keyboard.includes('Keyboard.dismiss()'), "完成先失焦再收起键盘");
assert.ok(keyboard.includes('minHeight: 44'), "键盘操作触控区至少 44 点");
assert.ok(keyboard.includes('textColor="#0958D9"'), "工具栏文字使用高对比颜色");
assert.ok(modal.includes('<KeyboardAvoidingView') && modal.includes('keyboardShouldPersistTaps="handled"'), "表单避让键盘且首次点击操作可达");
assert.ok(modal.includes('id="batch-count" order={0}') && modal.includes('id="batch-price" order={1}'), "批量添加的数量先于零售价");
assert.ok(editor.includes('CreateBatchNumberInput'), "套装及子项复用键盘操作");
assert.ok(modal.includes('onRequestClose={form.dismiss}'), "Android 系统返回遵守提交关闭保护");
assert.ok(modal.includes('form.creationUncertain') && modal.includes('form.returnToList'), "结果未知时只保留返回批次列表入口");
assert.ok(modal.includes('sortSuppliersByName(form.suppliers, language)') && modal.includes('sortedSuppliers.map('), "创建批次的供应商选择列表按名字排序");
assert.ok(readFileSync(resolve(directory, "DomesticProductList.tsx"), "utf8").includes('sortedSuppliers.map('), "商品列表的供应商筛选按名字排序");
for (const language of ["zh", "en"]) {
  const locale = JSON.parse(readFileSync(resolve(directory, `../../locales/${language}/screens/domesticPurchase.json`), "utf8"));
  assert.equal(locale.create.next, language === "zh" ? "下一步" : "Next");
  assert.equal(locale.create.done, language === "zh" ? "完成" : "Done");
}
console.log("create-batch-keyboard-source.test.ts: ok");

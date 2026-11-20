import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";

// 订单明细的货号搜索框放在 FlatList 列表头里。列表头若以函数（组件类型）传入，
// 每次输入都会换新引用，FlatList 会整段卸载重挂列表头，搜索框输入一个字就失焦、键盘收起。
// 因此列表头必须以 useMemo 生成的元素传入。
const screen = readFileSync(join(__dirname, "../../../app/(shell)/orders.tsx"), "utf8");

assert.match(screen, /const detailHeader = useMemo\(/, "订单明细列表头应由 useMemo 生成元素");
assert.match(screen, /ListHeaderComponent=\{detailHeader\}/, "订单明细 FlatList 应传入列表头元素");
assert.doesNotMatch(
  screen,
  /ListHeaderComponent=\{render[A-Z]\w*\}/,
  "不能把 render 函数当列表头传给 FlatList，否则搜索框每输入一个字就会被重挂而失焦"
);
assert.doesNotMatch(screen, /renderDetailHeader/, "旧的函数式列表头应已删除");

// 搜索框仍在列表头元素里，并直接受控于筛选词。
const headerStart = screen.indexOf("const detailHeader = useMemo(");
const headerEnd = screen.indexOf("const renderDetailEmpty", headerStart);
assert.ok(headerStart >= 0 && headerEnd > headerStart, "找不到订单明细列表头定义");
const header = screen.slice(headerStart, headerEnd);
assert.match(header, /<TextInput[\s\S]*value=\{itemNumberFilter\}[\s\S]*onChangeText=\{onItemNumberFilterChange\}/);

console.log("order detail search focus contract tests passed");

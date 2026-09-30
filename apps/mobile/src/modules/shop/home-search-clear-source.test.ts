import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const currentDirectory = dirname(fileURLToPath(import.meta.url));
const homeSource = readFileSync(
  resolve(currentDirectory, "../../../app/(shell)/home.tsx"),
  "utf8",
);

assert.match(
  homeSource,
  /const searchReturnPageRef = useRef<number \| null>\(null\);/,
  "首页应记录首次进入搜索前的页码",
);
assert.match(
  homeSource,
  /const handleSearchInputChange = useCallback\([\s\S]*?if \(!value\.trim\(\)\)[\s\S]*?type: "clear"[\s\S]*?\n  \},/,
  "手动删空或点击 Searchbar 清空按钮时都应退出已提交搜索",
);
assert.match(
  homeSource,
  /useVisibleSearchScannerInput<[\s\S]*?>\(\{[\s\S]*?onChangeText: handleSearchInputChange,[\s\S]*?onScannerInput: handleVisibleSearchScan,/,
  "扫码识别之外的输入必须回落到统一的关键词输入处理器",
);
assert.match(
  homeSource,
  /onChangeText=\{visibleSearchScanner\.handleChangeText\}/,
  "Searchbar 必须先经过可见框扫码识别，Zebra 聚焦搜索框时扫码才能自动查询",
);
assert.match(
  homeSource,
  /lastScan\?\.barcode === barcode && now - lastScan\.time < 100/,
  "原生按键监听与可见搜索框可能收到同一次扫码，必须去重以免重复加购",
);
assert.doesNotMatch(
  homeSource,
  /onClearIconPress=/,
  "Searchbar 已会触发 onChangeText，不能再绑定清空回调导致重复恢复",
);

// 搜索框右侧常驻相机扫码按钮：打开的是与底栏相机按钮同一个扫码弹层，并先收起键盘。
const searchbarRight = homeSource.match(/<Searchbar[\s\S]*?right=\{[\s\S]*?\n        \/>/)?.[0] ?? "";
assert.match(
  searchbarRight,
  /icon="barcode-scan"[\s\S]*?visibleSearchScanner\.blurSearchInput\(\);[\s\S]*?handleOpenCameraSheet\(\);/,
  "订货页搜索框右侧必须有相机扫码按钮，点击先收起键盘再打开扫码弹层",
);
// 自绘 right 会隐藏 Searchbar 自带清空按钮，自绘清空必须同样走可见框扫码识别回落到关键词处理器。
assert.match(
  searchbarRight,
  /icon="close"[\s\S]*?searchInputRef\.current\?\.clear\(\);[\s\S]*?visibleSearchScanner\.handleChangeText\(""\);/,
  "自绘清空按钮必须清空输入框并经 visibleSearchScanner.handleChangeText 退出已提交搜索",
);

const productContextReset = homeSource.match(
  /useEffect\(\(\) => \{[\s\S]*?searchReturnPageRef\.current = null;[\s\S]*?setPageNumber\(1\);[\s\S]*?\}, \[selectedCategoryGUID, selectedGrade, selectedStoreCode\]\);/,
);
assert.ok(
  productContextReset,
  "门店、分类或 Grade 改变后应让旧返回页码失效并回到第一页",
);

console.log("home-search-clear-source.test.ts: ok");

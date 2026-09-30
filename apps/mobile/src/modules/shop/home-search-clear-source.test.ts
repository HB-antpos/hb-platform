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

assert.match(
  homeSource,
  /if \(scanFeedback\.status !== "supply_paused"[\s\S]*?if \(appliedSupplyPausedFeedbackRef\.current === scanFeedback\) \{\s*return;\s*\}\s*appliedSupplyPausedFeedbackRef\.current = scanFeedback;[\s\S]*?setSearchInput\(scanFeedback\.barcode\);/,
  "暂停供货扫码回填只能按同一条反馈执行一次，否则清空搜索框后 effect 重跑会把旧条码写回",
);

const productContextReset = homeSource.match(
  /useEffect\(\(\) => \{[\s\S]*?searchReturnPageRef\.current = null;[\s\S]*?setPageNumber\(1\);[\s\S]*?\}, \[selectedCategoryGUID, selectedGrade, selectedStoreCode\]\);/,
);
assert.ok(
  productContextReset,
  "门店、分类或 Grade 改变后应让旧返回页码失效并回到第一页",
);

console.log("home-search-clear-source.test.ts: ok");

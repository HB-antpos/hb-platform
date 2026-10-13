import assert from "node:assert/strict";
import { getWarehouseDateColumn, getWarehouseDateText, normalizeWarehouseArrivalDate } from "./warehouse-date";

// ───────── 商品卡片右列：标签 + 大字日期 + 年份 ─────────
// 已到仓（actual）→「到仓库」+ 实际到货日期；在途（estimated）→「预计到仓库」+ 预计到岸日期
assert.deepEqual(getWarehouseDateColumn("actual", "2026-10-02"), { labelKey: "labels.warehouseDateLabel", day: "02/10", year: "2026" });
assert.deepEqual(getWarehouseDateColumn("estimated", "2026-10-02"), { labelKey: "labels.warehouseDateEstimatedLabel", day: "02/10", year: "2026" });
// 带时间部分只取日期，不经过 Date，所以不受设备时区影响
assert.deepEqual(getWarehouseDateColumn("actual", "2026-10-02T23:30:00"), { labelKey: "labels.warehouseDateLabel", day: "02/10", year: "2026" });
assert.deepEqual(getWarehouseDateColumn("actual", "2026-12-31T23:59:59+10:00"), { labelKey: "labels.warehouseDateLabel", day: "31/12", year: "2026" });
// 年份单独成字段（和原来单日期写法一致，不拼进日期里）
assert.deepEqual(getWarehouseDateColumn("estimated", "2027-01-03"), { labelKey: "labels.warehouseDateEstimatedLabel", day: "03/01", year: "2027" });
// 没有日期（旧版后端不返回 / null / 空串 / 格式不对）→ null，整列不渲染；不会回退成别的日期
assert.equal(getWarehouseDateColumn("actual", null), null);
assert.equal(getWarehouseDateColumn("estimated", undefined), null);
assert.equal(getWarehouseDateColumn("actual", ""), null);
assert.equal(getWarehouseDateColumn("actual", "  "), null);
assert.equal(getWarehouseDateColumn("actual", "not-a-date"), null);
assert.equal(getWarehouseDateColumn("actual", "2026-1-2"), null);

// ───────── 货柜选择面板：柜号下方一行文字 ─────────
assert.deepEqual(getWarehouseDateText("actual", "2026-10-02"), { key: "containerSheet.warehouseDate", date: "02/10" });
assert.deepEqual(getWarehouseDateText("estimated", "2026-10-02"), { key: "containerSheet.warehouseDateEstimated", date: "02/10" });
assert.equal(getWarehouseDateText("actual", null), null);
assert.equal(getWarehouseDateText("estimated", undefined), null);
assert.equal(getWarehouseDateText("actual", ""), null);
assert.equal(getWarehouseDateText("actual", "not-a-date"), null);
assert.deepEqual(getWarehouseDateText("actual", "2026-10-02T23:30:00"), { key: "containerSheet.warehouseDate", date: "02/10" });
// 年份：与参考日期（门店本地今天）同年不显示；跨年补上，避免 12 月底、1 月初同时出现时被误读
assert.equal(getWarehouseDateText("actual", "2026-10-02", "2026-10-05")?.date, "02/10");
assert.equal(getWarehouseDateText("actual", "2026-12-29", "2027-01-05")?.date, "29/12/2026");
assert.equal(getWarehouseDateText("estimated", "2027-01-03", "2026-12-29T08:00:00")?.date, "03/01/2027");
// 参考日期缺失 / 无效时按不带年份处理
assert.equal(getWarehouseDateText("actual", "2026-12-29", null)?.date, "29/12");
assert.equal(getWarehouseDateText("actual", "2026-12-29", "garbage")?.date, "29/12");

// 归一化：接口日期 → YYYY-MM-DD 或 null
assert.equal(normalizeWarehouseArrivalDate("2026-10-02"), "2026-10-02");
assert.equal(normalizeWarehouseArrivalDate(" 2026-10-02T00:00:00 "), "2026-10-02");
assert.equal(normalizeWarehouseArrivalDate(null), null);
assert.equal(normalizeWarehouseArrivalDate(undefined), null);
assert.equal(normalizeWarehouseArrivalDate("02/10/2026"), null);

console.log("container-new-products warehouse date tests passed");

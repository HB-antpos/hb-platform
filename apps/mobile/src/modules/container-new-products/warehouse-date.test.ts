import assert from "node:assert/strict";
import { getWarehouseDateText, normalizeWarehouseArrivalDate } from "./warehouse-date";

// 已到仓（actual）→「到仓库」，在途（estimated）→「预计到仓库」；两个文案分组各自取自己的 locale 键
assert.deepEqual(getWarehouseDateText("labels", "actual", "2026-10-02"), { key: "labels.warehouseDate", date: "02/10" });
assert.deepEqual(getWarehouseDateText("labels", "estimated", "2026-10-02"), { key: "labels.warehouseDateEstimated", date: "02/10" });
assert.deepEqual(getWarehouseDateText("containerSheet", "actual", "2026-10-02"), { key: "containerSheet.warehouseDate", date: "02/10" });
assert.deepEqual(getWarehouseDateText("containerSheet", "estimated", "2026-10-02"), { key: "containerSheet.warehouseDateEstimated", date: "02/10" });

// 没有日期（旧版后端 / 空串 / 格式不对）→ null，调用方不渲染；不会回退成别的日期
assert.equal(getWarehouseDateText("labels", "actual", null), null);
assert.equal(getWarehouseDateText("labels", "estimated", undefined), null);
assert.equal(getWarehouseDateText("labels", "actual", ""), null);
assert.equal(getWarehouseDateText("labels", "actual", "  "), null);
assert.equal(getWarehouseDateText("labels", "actual", "not-a-date"), null);
assert.equal(getWarehouseDateText("labels", "actual", "2026-1-2"), null);

// 带时间部分只取日期，不经过 Date，所以不受设备时区影响
assert.deepEqual(getWarehouseDateText("labels", "actual", "2026-10-02T23:30:00"), { key: "labels.warehouseDate", date: "02/10" });
assert.deepEqual(getWarehouseDateText("labels", "actual", "2026-12-31T23:59:59+10:00"), { key: "labels.warehouseDate", date: "31/12" });

// 年份：与参考日期（到店起始日）同年不显示；跨年补上，避免 12 月到仓、次年 1 月到店被误读
assert.equal(getWarehouseDateText("labels", "actual", "2026-10-02", "2026-10-05")?.date, "02/10");
assert.equal(getWarehouseDateText("labels", "actual", "2026-12-29", "2027-01-05")?.date, "29/12/2026");
assert.equal(getWarehouseDateText("labels", "estimated", "2026-12-29", "2027-01-05T00:00:00")?.date, "29/12/2026");
// 参考日期缺失 / 无效时按不带年份处理
assert.equal(getWarehouseDateText("labels", "actual", "2026-12-29", null)?.date, "29/12");
assert.equal(getWarehouseDateText("labels", "actual", "2026-12-29", "garbage")?.date, "29/12");

// 归一化：接口日期 → YYYY-MM-DD 或 null
assert.equal(normalizeWarehouseArrivalDate("2026-10-02"), "2026-10-02");
assert.equal(normalizeWarehouseArrivalDate(" 2026-10-02T00:00:00 "), "2026-10-02");
assert.equal(normalizeWarehouseArrivalDate(null), null);
assert.equal(normalizeWarehouseArrivalDate(undefined), null);
assert.equal(normalizeWarehouseArrivalDate("02/10/2026"), null);

console.log("container-new-products warehouse date tests passed");

import assert from "node:assert/strict";
import { buildHoursView, parsePayslipKeyParams } from "./detail-model";
import { FIXTURE_HOURS_RESPONSE } from "./fixtures";
import { normalizeHours } from "./normalize";

// 路由参数 → 定位键：三要素必填，periodStart 可选；数组参数取第一个；日期规整
assert.deepEqual(parsePayslipKeyParams({ connectionGuid: "c", employeeUid: "e", paymentDate: "2026-10-29", periodStart: "2026-10-19" }), {
  connectionGuid: "c",
  employeeUid: "e",
  paymentDate: "2026-10-29",
  periodStart: "2026-10-19",
});
assert.equal(parsePayslipKeyParams({ connectionGuid: "c", employeeUid: "e", paymentDate: "2026-10-29" })?.periodStart, null);
assert.equal(parsePayslipKeyParams({ connectionGuid: ["c", "x"], employeeUid: "e", paymentDate: "2026-10-29T00:00:00" })?.connectionGuid, "c");
assert.equal(parsePayslipKeyParams({ connectionGuid: "c", employeeUid: "", paymentDate: "2026-10-29" }), null);
assert.equal(parsePayslipKeyParams({ connectionGuid: "c", employeeUid: "e", paymentDate: "bad" }), null);
assert.equal(parsePayslipKeyParams({}), null);

// 我方考勤工时展示行
const view = buildHoursView(normalizeHours(FIXTURE_HOURS_RESPONSE));
assert.equal(view.periodFrom, "19/10/2026");
assert.equal(view.periodTo, "25/10/2026");
assert.deepEqual(view.rows, [{ key: "1014", label: "1014 Kawana", failed: false, workText: "36.00 h", leaveText: "0.00 h" }]);
assert.equal(view.totalWorkText, "36.00 h");
// 读取失败的分店标记 failed
const failed = buildHoursView(normalizeHours({ stores: [{ storeCode: "1", storeLabel: "1 A", failed: true }] }));
assert.equal(failed.rows[0].failed, true);
assert.equal(failed.totalWorkText, "—");

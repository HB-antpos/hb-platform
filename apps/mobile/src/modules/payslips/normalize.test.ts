import assert from "node:assert/strict";
import { FIXTURE_DETAIL_RESPONSE, FIXTURE_EMAIL_TARGET_RESPONSE, FIXTURE_HOURS_RESPONSE, FIXTURE_LIST_RESPONSE } from "./fixtures";
import {
  normalizeCompanies,
  normalizeDetail,
  normalizeEmailResult,
  normalizeEmailTarget,
  normalizeHours,
  normalizeList,
  normalizePlacement,
} from "./normalize";

// 列表：字段原样规整，日期只取 yyyy-MM-dd，未知归属归为 Unknown
const list = normalizeList(FIXTURE_LIST_RESPONSE);
assert.equal(list.rows.length, 5);
assert.equal(list.totalCount, 5);
assert.equal(list.canViewAllStores, true);
assert.equal(list.unassignedCount, 1);
assert.equal(list.rows[0].employeeName, "ALEX MORGAN");
assert.equal(list.rows[0].paymentDate, "2026-10-29");
assert.equal(list.rows[0].periodStart, "2026-10-19");
assert.deepEqual(list.rows[0].stores, [{ storeCode: "1014", storeLabel: "1014 Kawana" }]);
assert.equal(list.rows[3].placement, "Unbound");
assert.equal(normalizePlacement("whatever"), "Unknown");
assert.equal(normalizePlacement(undefined), "Unknown");

// 缺少定位三要素的行被丢弃；带时间的日期被截断；数字字符串可解析
const messy = normalizeList({
  rows: [
    { connectionGuid: "c", employeeUid: "e", paymentDate: "2026-10-29T00:00:00", netPay: "12.5", grossPay: null },
    { connectionGuid: "c", employeeUid: "", paymentDate: "2026-10-29" },
    { connectionGuid: "c", employeeUid: "e", paymentDate: "" },
    null,
  ],
});
assert.equal(messy.rows.length, 1);
assert.equal(messy.rows[0].paymentDate, "2026-10-29");
assert.equal(messy.rows[0].netPay, 12.5);
assert.equal(messy.rows[0].grossPay, null);
assert.equal(messy.totalCount, 1, "缺 totalCount 时用规整后的行数");
assert.equal(messy.canViewAllStores, false);
assert.throws(() => normalizeList(null), /invalid/);

// 公司列表
assert.deepEqual(normalizeCompanies([{ connectionGuid: "g", connectionName: "A" }, { connectionName: "no guid" }, null]), [{ connectionGuid: "g", connectionName: "A" }]);
assert.deepEqual(normalizeCompanies("x"), []);

// 公司读取状态：ok 缺省按成功，false 才算失败
const status = normalizeList({ rows: [], companies: [{ connectionGuid: "g", ok: false, count: 0, errorCode: "X", errorMessage: "boom" }, { connectionGuid: "h" }] });
assert.deepEqual(status.companies.map((item) => item.ok), [false, true]);

// 详情：明细保持原顺序；Entitlement 行 amount 为 null；权限位只有明确 true 才为 true
const detail = normalizeDetail(FIXTURE_DETAIL_RESPONSE);
assert.equal(detail.lines.length, 5);
assert.equal(detail.lines[4].amount, null);
assert.equal(detail.lines[4].hours, 1.15);
assert.equal(detail.hourlyRate, 24);
assert.equal(detail.referenceNumber, "1263");
assert.equal(detail.canDownloadPdf, true);
assert.equal(detail.canSendEmail, true);
const restricted = normalizeDetail({ ...FIXTURE_DETAIL_RESPONSE, canDownloadPdf: "true", canSendEmail: undefined, hourlyRate: null, referenceNumber: "" });
assert.equal(restricted.canDownloadPdf, false);
assert.equal(restricted.canSendEmail, false);
assert.equal(restricted.hourlyRate, null);
assert.equal(restricted.referenceNumber, null);
assert.equal(normalizeDetail({ ...FIXTURE_DETAIL_RESPONSE, samePaymentDateCount: 0 }).samePaymentDateCount, 1);
assert.throws(() => normalizeDetail({ companyName: "x" }), /invalid/);

// 工时 / 发信目标 / 发信结果
const hours = normalizeHours(FIXTURE_HOURS_RESPONSE);
assert.equal(hours.stores[0].workHours, 36);
assert.equal(hours.totalWorkHours, 36);
assert.equal(hours.stores[0].failed, false);
assert.equal(normalizeHours({ stores: [{ storeCode: "1", failed: true }] }).stores[0].failed, true);
const target = normalizeEmailTarget(FIXTURE_EMAIL_TARGET_RESPONSE);
assert.equal(target.source, "profile");
assert.equal(target.email, "alex.morgan@example.com");
assert.equal(normalizeEmailTarget({ source: "none", email: null }).source, "none");
assert.equal(normalizeEmailTarget({ source: "weird" }).source, "none");
assert.deepEqual(normalizeEmailResult({ sentTo: "a***@example.com", copiedToMe: true }), { sentTo: "a***@example.com", copiedToMe: true });
assert.deepEqual(normalizeEmailResult(null), { sentTo: "", copiedToMe: false });

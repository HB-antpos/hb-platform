import assert from "node:assert/strict";
import {
  classifyFaceMatchError,
  classifyReviewImageError,
  decodeBinaryJsonBody,
  describeFaceMatchError,
  extractFaceMatchErrorCode,
  extractHttpStatus,
  getDisabledReasonKey,
  getGateReportFromError,
  isSharedImageFailure,
} from "./errors";

// 假 t：直接回显键与参数，便于断言
const t = (key: string, options?: Record<string, unknown>) => (options ? `${key}${JSON.stringify(options)}` : key);

/** 模拟 axios 错误：HTTP 状态 + 响应体。 */
function httpError(status: number, body: unknown, message = "preserved-details-text") {
  return Object.assign(new Error(message), { response: { status, data: body } });
}

function bytesOf(text: string) {
  return new TextEncoder().encode(text).buffer as ArrayBuffer;
}

// ───────── 错误码提取 ─────────
assert.equal(extractFaceMatchErrorCode(httpError(409, { success: false, errorCode: "FACE_MATCH_GATE_NOT_MET" })), "FACE_MATCH_GATE_NOT_MET");
assert.equal(extractFaceMatchErrorCode(httpError(400, { code: "FACE_MATCH_FIELD_INVALID" })), "FACE_MATCH_FIELD_INVALID", "兼容 code 字段");
assert.equal(
  extractFaceMatchErrorCode(Object.assign(new Error("x"), { code: "FACE_MATCH_DISABLED" })),
  "FACE_MATCH_DISABLED",
  "unwrapApiEnvelope 抛出的业务错误把错误码放在 error.code",
);
assert.equal(extractFaceMatchErrorCode(new Error("plain")), null);
assert.equal(extractFaceMatchErrorCode(null), null);
assert.equal(extractHttpStatus(httpError(403, {})), 403);
assert.equal(extractHttpStatus(new Error("x")), null);

// ───────── 二进制错误体（图片接口 responseType=arraybuffer） ─────────
{
  const binary = httpError(404, bytesOf('{"success":false,"errorCode":"FACE_MATCH_IMAGE_GONE","message":"图片已清理"}'));
  assert.equal(extractFaceMatchErrorCode(binary), "FACE_MATCH_IMAGE_GONE", "ArrayBuffer 错误体也要能读出错误码");
  assert.equal(classifyReviewImageError(binary), "gone");
  // 真正的图片字节（成功响应不会进入这里）不会被当成 JSON
  assert.equal(decodeBinaryJsonBody(Uint8Array.from([0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10]).buffer), null);
  assert.equal(decodeBinaryJsonBody(bytesOf("   ")), null);
  assert.deepEqual(decodeBinaryJsonBody({ a: 1 }), { a: 1 });
  assert.equal(decodeBinaryJsonBody(bytesOf("[1,2]") ) !== null, true, "数组也是对象，调用方只读 errorCode 字段，不会误判");
}

// ───────── 文案键分类：已知码优先，其次状态码，最后通用 ─────────
assert.equal(classifyFaceMatchError(httpError(409, { errorCode: "FACE_MATCH_SAME_REVIEWER" })), "errors.codes.FACE_MATCH_SAME_REVIEWER");
assert.equal(classifyFaceMatchError(httpError(409, { errorCode: "FACE_MATCH_BRAND_NEW_CODE" })), "errors.status.409", "未登记的新错误码按 HTTP 状态码兜底");
assert.equal(classifyFaceMatchError(httpError(418, { errorCode: "UNKNOWN" })), "errors.generic");
assert.equal(classifyFaceMatchError(new Error("boom")), "errors.generic");
assert.equal(classifyFaceMatchError(httpError(403, {})), "errors.status.403");

// ───────── 完整提示：不使用被 preserveApiClientError 改写过的 error.message ─────────
{
  // details 是闸门报告：axios 拦截器会把 error.message 拼成一串字段键，绝不能当提示展示
  const gate = httpError(
    409,
    { success: false, errorCode: "FACE_MATCH_GATE_NOT_MET", details: { facets: [{ facet: "capture", requested: true, missing: ["allowedSubjects"], warnings: [] }], canSave: false } },
    "capture\nallowedSubjects",
  );
  const text = describeFaceMatchError(t, "zh", gate);
  assert.equal(text, "errors.codes.FACE_MATCH_GATE_NOT_MET");
  assert.equal(text.includes("allowedSubjects"), false);

  const disabled = httpError(503, { errorCode: "FACE_MATCH_DISABLED", details: { reason: "credentials-missing" } });
  assert.equal(describeFaceMatchError(t, "zh", disabled), "errors.codes.FACE_MATCH_DISABLED disabled.reasons.credentialsMissing");
  assert.equal(
    describeFaceMatchError(t, "zh", httpError(503, { errorCode: "FACE_MATCH_DISABLED", details: { reason: "x-weird" } })),
    "errors.codes.FACE_MATCH_DISABLED",
    "未知原因不追加",
  );

  const field = httpError(400, { errorCode: "FACE_MATCH_FIELD_INVALID", details: { field: "evidenceRef" } });
  assert.equal(describeFaceMatchError(t, "zh", field), 'errors.codes.FACE_MATCH_FIELD_INVALID' + 'errors.fieldSuffix{"field":"evidenceRef"}');
  const weirdField = httpError(400, { errorCode: "FACE_MATCH_FIELD_INVALID", details: { field: "<script>alert(1)</script>" } });
  assert.equal(describeFaceMatchError(t, "zh", weirdField), "errors.codes.FACE_MATCH_FIELD_INVALID", "字段名不是普通标识符时不展示");

  assert.equal(describeFaceMatchError(t, "zh", httpError(413, {})), "errors.status.413");
  // 网络错误交给通用本地化解析
  const network = Object.assign(new Error("Network Error"), { code: "ERR_NETWORK" });
  assert.equal(describeFaceMatchError(t, "zh", network), "common:errors.network");
  const server = httpError(500, { message: "NullReferenceException at X" });
  assert.equal(describeFaceMatchError(t, "zh", server), "common:errors.server", "5xx 不展示服务端消息文本");
}

// ───────── 闸门报告 ─────────
{
  const report = getGateReportFromError(
    httpError(409, {
      errorCode: "FACE_MATCH_GATE_NOT_MET",
      details: { facets: [{ facet: "capture", requested: true, missing: ["allowedSubjects"] }, { facet: "match", requested: false }], canSave: false },
    }),
  );
  assert.ok(report);
  assert.equal(report.canSave, false);
  assert.deepEqual(report.facets[0].missing, ["allowedSubjects"]);
  assert.deepEqual(report.facets[0].warnings, [], "老后端没有 warnings 字段时归一化为空数组");
  assert.equal(getGateReportFromError(httpError(409, { errorCode: "FACE_MATCH_ENTRY_CHANGED" })), null);
  assert.equal(getGateReportFromError(httpError(409, { errorCode: "FACE_MATCH_GATE_NOT_MET" })), null, "没有 details 时返回 null");
}

// ───────── 复核图片失败分类 ─────────
assert.equal(classifyReviewImageError(httpError(404, { errorCode: "FACE_MATCH_STORE_NOT_CONFIGURED" })), "notConfigured");
assert.equal(classifyReviewImageError(httpError(503, { errorCode: "FACE_MATCH_DISABLED" })), "notConfigured");
assert.equal(classifyReviewImageError(httpError(403, { errorCode: "FACE_MATCH_STORE_FORBIDDEN" })), "forbidden");
assert.equal(classifyReviewImageError(httpError(403, {})), "forbidden");
assert.equal(classifyReviewImageError(httpError(401, {})), "unauthorized");
assert.equal(classifyReviewImageError(httpError(410, {})), "gone");
assert.equal(classifyReviewImageError(httpError(404, { errorCode: "FACE_MATCH_NOT_FOUND" })), "missing");
assert.equal(classifyReviewImageError(httpError(404, {})), "missing");
assert.equal(classifyReviewImageError(httpError(502, { errorCode: "FACE_MATCH_STORE_ERROR" })), "failed");
assert.equal(classifyReviewImageError(new Error("net")), "failed");
assert.equal(isSharedImageFailure("forbidden"), true);
assert.equal(isSharedImageFailure("unauthorized"), true);
assert.equal(isSharedImageFailure("notConfigured"), true);
assert.equal(isSharedImageFailure("gone"), false, "单张被清理不代表其余图片也没了");
assert.equal(isSharedImageFailure("missing"), false);
assert.equal(isSharedImageFailure("failed"), false);

// ───────── 未开启原因 ─────────
assert.equal(getDisabledReasonKey("region-missing"), "disabled.reasons.regionMissing");
assert.equal(getDisabledReasonKey("nope"), "disabled.reasons.unknown");
assert.equal(getDisabledReasonKey(null), "disabled.reasons.unknown");

console.log("errors.test.ts: ok");

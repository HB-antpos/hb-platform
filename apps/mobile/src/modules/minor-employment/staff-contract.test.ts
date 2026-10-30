import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  GUARDIAN_LINK_ERROR_KEYS,
  MINOR_STAGE_TONES,
  buildRevokePayload,
  buildSendPayload,
  formatSydneyShort,
  hasDeliveryFailure,
  hasGuardianLinkFormErrors,
  isAustralianMobile,
  isForbiddenError,
  isResendStage,
  mapSendResult,
  mapStaffDetail,
  normalizeAustralianMobile,
  parseUtc,
  resolveGuardianLinkDefaults,
  resolveRelationshipOption,
  resolveSmsBlockReason,
  validateGuardianLinkForm,
} from "./staff-contract";

const directory = dirname(fileURLToPath(import.meta.url));

// ───────── GET staff/{userGuid}：camelCase 响应 ─────────
const detail = mapStaffDetail({
  item: {
    userGUID: "u-15",
    employeeName: "Mia Nguyen",
    username: "mia.n",
    storeCode: "1001",
    storeName: "示例门店 1001",
    stateCode: "NSW",
    formType: "NSW_COMPANY_CONSENT",
    birthday: "2011-05-01T00:00:00",
    age: 15,
    complianceId: 9,
    version: 2,
    revision: 4,
    complianceStatus: "awaiting_guardian_signature",
    stage: "awaiting_guardian",
    needsAction: false,
    canSend: true,
    canRevoke: true,
    guardianName: "Linh Nguyen",
    guardianRelationship: "Mother",
    guardianEmail: "linh.nguyen@gmail.com",
    guardianPhone: "0412 345 678",
    maskedGuardianEmail: "l***@gmail.com",
    maskedGuardianPhone: "0412 *** 678",
    linkExpiresAtUtc: "2026-11-02T05:20:00",
    lastSentAtUtc: "2026-10-30T05:20:00Z",
    lastSentEmail: true,
    lastSentSms: false,
    guardianSignedAtUtc: null,
    reviewComment: null,
    returnFields: [],
  },
  smsAvailable: true,
});
assert.equal(detail.smsAvailable, true);
assert.ok(detail.item);
assert.equal(detail.item.userGUID, "u-15");
assert.equal(detail.item.stage, "awaiting_guardian");
assert.equal(detail.item.version, 2);
assert.equal(detail.item.revision, 4);
assert.equal(detail.item.canRevoke, true);
assert.equal(detail.item.lastSentEmail, true);
assert.equal(detail.item.lastSentSms, false);
assert.equal(detail.item.guardianSignedAtUtc, undefined);
assert.deepEqual(detail.item.returnFields, []);

// item 为 null（成年 / 无生日 / 无主门店）→ 整节不显示
assert.deepEqual(mapStaffDetail({ item: null, smsAvailable: false }), { item: null, smsAvailable: false });
assert.equal(mapStaffDetail(undefined).item, null);

// PascalCase、未知阶段与渠道「没选」(null) 的兜底
const pascal = mapStaffDetail({
  Item: { UserGUID: "u-2", Stage: "weird", StateCode: "vic", FormType: "x", LastSentEmail: null, Age: "16" },
  SmsAvailable: false,
});
assert.equal(pascal.item?.userGUID, "u-2");
assert.equal(pascal.item?.stage, "not_sent");
assert.equal(pascal.item?.stateCode, "UNKNOWN");
assert.equal(pascal.item?.formType, "UNSUPPORTED_STATE");
assert.equal(pascal.item?.lastSentEmail, null);
assert.equal(pascal.item?.lastSentSms, null);
assert.equal(pascal.item?.age, 16);
assert.equal(pascal.item?.version, undefined);

// ───────── POST guardian-link：请求体 ─────────
const form = resolveGuardianLinkDefaults(detail.item, true);
assert.equal(form.sendEmail, true, "有邮箱默认开邮件");
assert.equal(form.sendSms, true, "短信可用且是澳洲手机默认开短信");
assert.equal(form.expiryDays, 3, "默认有效期 3 天");
assert.equal(form.guardianRelationship, "Mother");
assert.deepEqual(buildSendPayload(detail.item, { ...form, guardianName: "  Linh Nguyen ", guardianEmail: " " , sendEmail: false }), {
  guardianName: "Linh Nguyen",
  guardianRelationship: "Mother",
  guardianEmail: null,
  guardianPhone: "0412 345 678",
  sendEmail: false,
  sendSms: true,
  expiryDays: 3,
  expectedVersion: 2,
  expectedRevision: 4,
});
// 没有记录时版本号为 null（后端用它判断「是否被别人抢先建档」）
assert.equal(buildSendPayload({}, form).expectedVersion, null);
assert.equal(buildSendPayload({}, form).expectedRevision, null);
assert.deepEqual(buildRevokePayload(detail.item), { expectedVersion: 2, expectedRevision: 4 });

// 默认值：短信不可用 / 座机号 → 不开短信；没有邮箱 → 不开邮件
assert.equal(resolveGuardianLinkDefaults(detail.item, false).sendSms, false);
assert.equal(resolveGuardianLinkDefaults({ ...detail.item, guardianPhone: "02 9876 5432" }, true).sendSms, false);
assert.equal(resolveGuardianLinkDefaults({ guardianName: "A" }, true).sendEmail, false);

// ───────── 澳洲手机号：与后端 AustralianPhoneNumber 同规则 ─────────
assert.equal(normalizeAustralianMobile("0412 345 678"), "+61412345678");
assert.equal(normalizeAustralianMobile("+61 412 345 678"), "+61412345678");
assert.equal(normalizeAustralianMobile("+61 0412 345 678"), "+61412345678");
assert.equal(normalizeAustralianMobile("61412345678"), "+61412345678");
assert.equal(normalizeAustralianMobile("0061412345678"), "+61412345678");
assert.equal(isAustralianMobile("02 9876 5432"), false, "座机不是手机");
assert.equal(isAustralianMobile("0412 345 67"), false, "位数不足");
assert.equal(isAustralianMobile("0412ext345678"), false, "含字母");
assert.equal(isAustralianMobile(""), false);
assert.equal(resolveSmsBlockReason(false, "0412345678"), "serviceOff");
assert.equal(resolveSmsBlockReason(true, "0298765432"), "notMobile");
assert.equal(resolveSmsBlockReason(true, ""), null, "空号码由「电话必填」提示，不算短信原因");
assert.equal(resolveSmsBlockReason(true, "0412345678"), null);

// ───────── 校验 ─────────
assert.equal(hasGuardianLinkFormErrors(validateGuardianLinkForm(form, true)), false);
assert.deepEqual(
  validateGuardianLinkForm({ ...form, guardianName: " ", guardianPhone: "", sendEmail: false, sendSms: false }, true),
  { name: "nameRequired", phone: "phoneRequired", channel: "channelRequired" },
);
assert.equal(validateGuardianLinkForm({ ...form, guardianEmail: "", sendEmail: true }, true).email, "emailRequired");
assert.equal(validateGuardianLinkForm({ ...form, guardianEmail: "bad", sendEmail: false }, true).email, "emailInvalid");
// 开了短信但短信服务没开：等于没开任何方式
assert.equal(validateGuardianLinkForm({ ...form, sendEmail: false, sendSms: true }, false).channel, "channelRequired");
assert.equal(validateGuardianLinkForm({ ...form, guardianPhone: "123" }, true).phone, "phoneInvalid");

// ───────── 发送结果 ─────────
const partial = mapSendResult({
  item: { userGUID: "u-15", stage: "awaiting_guardian" },
  expiresAtUtc: "2026-11-02T05:20:00Z",
  emailRequested: true,
  emailSent: true,
  smsRequested: true,
  smsSent: false,
  smsError: "号码不是澳洲手机号",
  codeChannel: "email",
  signingUrl: "https://hotbargain.vip/minor-employment/sign#token=abc",
});
assert.equal(partial.item.stage, "awaiting_guardian");
assert.equal(partial.codeChannel, "email");
assert.equal(hasDeliveryFailure(partial), true);
assert.ok(partial.signingUrl?.includes("#token="));
const allOk = mapSendResult({ item: {}, emailRequested: true, emailSent: true, smsRequested: false, codeChannel: "SMS" });
assert.equal(allOk.codeChannel, "sms");
assert.equal(hasDeliveryFailure(allOk), false);
assert.equal(allOk.signingUrl, undefined);

// ───────── 展示规则 ─────────
assert.equal(MINOR_STAGE_TONES.not_sent, "amber");
assert.equal(MINOR_STAGE_TONES.awaiting_guardian, "blue");
assert.equal(MINOR_STAGE_TONES.returned, "red");
assert.equal(MINOR_STAGE_TONES.approved, "green");
assert.equal(isResendStage("awaiting_guardian"), true);
assert.equal(isResendStage("returned"), true);
assert.equal(isResendStage("link_expired"), true);
assert.equal(isResendStage("not_sent"), false);
assert.equal(isResendStage("not_sent"), false);
assert.equal(resolveRelationshipOption("legal guardian"), "Legal guardian");
assert.equal(resolveRelationshipOption("Aunt"), null, "不在列表里的关系原样保留");
// 不带时区后缀按 UTC 解析；悉尼 11 月是夏令时 UTC+11
assert.equal(parseUtc("2026-11-02T05:20:00")?.toISOString(), "2026-11-02T05:20:00.000Z");
assert.equal(formatSydneyShort("2026-11-02T05:20:00", "zh"), "11-02 16:20");
assert.equal(formatSydneyShort("2026-11-02T05:20:00Z", "en"), "02/11 16:20");
assert.equal(formatSydneyShort(undefined, "zh"), null);

// ───────── 错误码 ─────────
for (const code of [
  "MINOR_GUARDIAN_CHANNEL_REQUIRED",
  "MINOR_COMPLIANCE_GUARDIAN_CONTACT_REQUIRED",
  "GUARDIAN_SMS_NOT_CONFIGURED",
  "GUARDIAN_SMS_PHONE_INVALID",
  "MINOR_COMPLIANCE_VERSION_CONFLICT",
  "INVALID_STATUS",
  "DOB_REQUIRED",
  "NOT_MINOR",
  "FORBIDDEN",
  "MINOR_COMPLIANCE_STATE_UNCONFIRMED",
]) {
  assert.ok(GUARDIAN_LINK_ERROR_KEYS[code], `缺少错误码 ${code} 的文案键`);
}
assert.equal(isForbiddenError({ code: "FORBIDDEN" }), true);
assert.equal(isForbiddenError({ response: { status: 403 } }), true);
assert.equal(isForbiddenError({ response: { status: 500 } }), false);

// 每个错误键在中英文 locale 里都存在
for (const lang of ["zh", "en"]) {
  const locale = JSON.parse(readFileSync(resolve(directory, `../../locales/${lang}/screens/peopleDetail.json`), "utf8"));
  for (const key of Object.values(GUARDIAN_LINK_ERROR_KEYS)) {
    assert.ok(locale.minor?.errors?.[key], `${lang} 缺少 peopleDetail:minor.errors.${key}`);
  }
}

// ───────── 接口地址（源码契约）─────────
const api = readFileSync(resolve(directory, "staff-api.ts"), "utf8");
assert.match(api, /const BASE = "\/minor-employment\/staff"/);
assert.match(api, /`\$\{BASE\}\/\$\{encodeURIComponent\(userGuid\)\}`/);
assert.match(api, /\/guardian-link`,\s*buildSendPayload\(item, form\)/);
assert.match(api, /\/guardian-link\/revoke`,\s*buildRevokePayload\(item\)/);

console.log("minor employment staff contract passed");

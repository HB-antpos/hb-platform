/**
 * 新建 / 编辑人员时，后端以 USERNAME_EXISTS / EMAIL_EXISTS 表示用户名或邮箱已被其他账号占用。
 * 这里只负责把错误归到具体字段，文案由各表单按 peopleForm 语言包翻译后标在对应输入框下。
 *
 * 错误有两种形态：
 * - 接口返回 200 + success:false，经 unwrapApiEnvelope 转成带 `code` 的业务错误（店员接口与管理员接口都是这样）；
 * - 以后若改成 4xx，Axios 错误的 `response.data.errorCode` 里同样带错误码，这里一并兼容。
 */
export type IdentityConflictField = "username" | "email";

const FIELD_BY_CODE: Record<string, IdentityConflictField> = {
  USERNAME_EXISTS: "username",
  EMAIL_EXISTS: "email",
};

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === "object" && value !== null ? (value as Record<string, unknown>) : null;
}

function readCode(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value.trim().toUpperCase() : null;
}

export function resolveIdentityConflictField(error: unknown): IdentityConflictField | null {
  const record = asRecord(error);
  const data = asRecord(asRecord(record?.response)?.data);
  const candidates = [record?.code, data?.errorCode, data?.ErrorCode, data?.code, data?.Code];
  for (const candidate of candidates) {
    const code = readCode(candidate);
    if (code && FIELD_BY_CODE[code]) return FIELD_BY_CODE[code];
  }
  return null;
}

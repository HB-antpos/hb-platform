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

function codeCandidates(error: unknown) {
  const record = asRecord(error);
  const data = asRecord(asRecord(record?.response)?.data);
  return [record?.code, data?.errorCode, data?.ErrorCode, data?.code, data?.Code];
}

export function resolveIdentityConflictField(error: unknown): IdentityConflictField | null {
  for (const candidate of codeCandidates(error)) {
    const code = readCode(candidate);
    if (code && FIELD_BY_CODE[code]) return FIELD_BY_CODE[code];
  }
  return null;
}

/**
 * 读取服务端业务错误码（两种形态同上），用于「新建员工必填规则」等按错误码给文案的场景。
 * 优先返回以 `NEW_STAFF_` 开头的码：Axios 错误的 `code` 是传输层码（如 ERR_BAD_REQUEST），业务码在响应体里。
 */
export function readApiErrorCode(error: unknown): string | null {
  const codes = codeCandidates(error).map(readCode).filter((code): code is string => Boolean(code));
  return codes.find((code) => code.startsWith("NEW_STAFF_")) ?? codes[0] ?? null;
}

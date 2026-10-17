// 裸认证请求的响应解析（纯函数，便于单测）。
//
// 重要事实（后端 AuthController.RefreshToken）：刷新令牌无效 / 已过期 / 账号被限制时，返回的是
// 「HTTP 200 + { success:false, message }」而不是 401。如果只看 HTTP 状态，被服务器明确拒绝的凭据会被当成
// 「网络错误」永远重试、永远不会标记「需重新登录」。所以这里把业务失败信封显式转成带 401 状态的错误，
// 让 switch-flow 的 isRejectedByServer 能统一识别。

export interface TokenPair {
  accessToken: string;
  refreshToken: string;
}

/** 服务器明确拒绝了这份凭据（形状上等同 401，供 getHttpStatus 识别）。 */
export function createRejectedError(message: string): Error & { response: { status: number } } {
  return Object.assign(new Error(message || "Rejected by server"), { response: { status: 401 } });
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === "object" ? (value as Record<string, unknown>) : null;
}

function isExplicitFailure(envelope: Record<string, unknown>): boolean {
  return envelope.success === false || envelope.isSuccess === false;
}

function messageOf(envelope: Record<string, unknown>): string {
  const value = envelope.message ?? envelope.Message;
  return typeof value === "string" ? value : "";
}

/** 解析刷新响应：业务失败 / 缺令牌都按「被拒绝」处理。 */
export function parseRefreshResponse(body: unknown): TokenPair {
  const envelope = asRecord(body);
  if (!envelope) throw createRejectedError("Empty refresh response");
  if (isExplicitFailure(envelope)) throw createRejectedError(messageOf(envelope));
  const data = asRecord(envelope.data) ?? envelope;
  const accessToken = data.accessToken;
  const refreshToken = data.refreshToken;
  if (typeof accessToken !== "string" || !accessToken || typeof refreshToken !== "string" || !refreshToken) {
    throw createRejectedError(messageOf(envelope) || "Refresh response missing tokens");
  }
  return { accessToken, refreshToken };
}

/**
 * 解析「读取当前用户」的响应：认证类业务失败（success:false 且带未授权标记）视为被拒绝，
 * 其它形式的失败交给调用方原样抛出（可能只是服务端临时错误）。
 */
export function assertCurrentUserAuthorized(body: unknown, isUnauthenticatedPayload: (body: unknown) => boolean): void {
  if (isUnauthenticatedPayload(body)) {
    throw createRejectedError(messageOf(asRecord(body) ?? {}) || "Unauthorized");
  }
}

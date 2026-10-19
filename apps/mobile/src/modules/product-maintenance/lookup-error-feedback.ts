import { isAxiosError } from "axios";

/**
 * 商品查询（扫码 / 手输）请求失败时给员工看的提示。
 * retryable：网络不通、超时、服务器繁忙这类「再查一次可能就好」的失败，页面上给「重试」按钮；
 * 其余失败（4xx 等）重试也不会变好，不给按钮。
 */
export type LookupErrorFeedback = {
  key: string;
  values?: Record<string, string | number>;
  retryable: boolean;
};

/**
 * 只处理 axios 请求错误；非 axios 错误（本地快照读失败等）返回 null，由页面按原有方式取真实原因。
 */
export function describeLookupRequestError(error: unknown): LookupErrorFeedback | null {
  if (!isAxiosError(error)) {
    return null;
  }

  if (!error.response) {
    return error.code === "ECONNABORTED" || error.code === "ETIMEDOUT"
      ? { key: "messages.lookupTimeout", retryable: true }
      : { key: "messages.lookupNetworkError", retryable: true };
  }

  const status = error.response.status;
  if (status >= 500) {
    return { key: "messages.lookupServerError", values: { status }, retryable: true };
  }

  return { key: "messages.lookupFailed", retryable: false };
}

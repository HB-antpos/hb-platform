export function getContainerNewProductsErrorCode(error: unknown) {
  const responseData = (error as { response?: { data?: unknown } })?.response?.data;
  const responseRecord = responseData && typeof responseData === "object"
    ? responseData as Record<string, unknown>
    : null;
  // Axios 的 error.code 通常是 ERR_BAD_REQUEST，业务码必须优先读响应体。
  return responseRecord?.errorCode
    ?? responseRecord?.ErrorCode
    ?? responseRecord?.code
    ?? responseRecord?.Code
    ?? (error as { code?: unknown })?.code;
}

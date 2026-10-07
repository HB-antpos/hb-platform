// 提交流程编排：先把还没上传成功的照片传完，再用最新草稿构造请求并发送。
// 任一环节失败都不丢表单与已上传的附件，调用方保持同一个 clientRequestId 重试。
export type SubmitOutcome<TResult> =
  | { status: "uploadFailed"; failedCount: number }
  | { status: "invalid" }
  | { status: "sent"; result: TResult }
  | { status: "error"; error: unknown };

export async function runCashSubmit<TRequest, TResult>(options: {
  /** 上传全部未成功的照片；已成功的不会重复上传。 */
  uploadPhotos: () => Promise<{ allUploaded: boolean; failedKeys: string[] }>;
  /** 用上传后的最新草稿构造请求；校验未过或附件不全返回 null。 */
  buildRequest: () => TRequest | null;
  send: (request: TRequest) => Promise<TResult>;
}): Promise<SubmitOutcome<TResult>> {
  const upload = await options.uploadPhotos();
  if (!upload.allUploaded) return { status: "uploadFailed", failedCount: upload.failedKeys.length };
  const request = options.buildRequest();
  if (!request) return { status: "invalid" };
  try {
    return { status: "sent", result: await options.send(request) };
  } catch (error) {
    return { status: "error", error };
  }
}

// 照片直传流程：先对每张图申请 upload-signature → PUT 到返回的 url（带 headers 全部请求头）→ 记下 attachmentGuid。
// 依赖全部通过参数注入，便于纯逻辑测试；运行时实现见 photo-upload-runtime.ts。
import type { CashAttachmentUploadSignature } from "./types";
import type { PhotoDraft, PhotoFailureStage } from "./photo-drafts";
import { photosNeedingUpload } from "./photo-drafts";

export interface PhotoUploadDeps {
  /** 读取本地文件的真实字节大小；签名请求以实际字节为准，避免压缩结果与声明大小不一致。 */
  readFileSize(uri: string): Promise<number>;
  requestSignature(request: { contentType: string; fileSize: number }): Promise<CashAttachmentUploadSignature>;
  putFile(uri: string, signature: CashAttachmentUploadSignature): Promise<void>;
  contentType: string;
}

export type PhotoUploadPatch = Partial<Pick<PhotoDraft, "status" | "attachmentGuid" | "failureStage" | "fileSize">>;

export interface PhotoUploadResult {
  status: "uploaded" | "failed";
  attachmentGuid?: string;
  failureStage?: PhotoFailureStage;
  error?: unknown;
}

/** 上传单张照片，从不抛错：失败返回 failed 与出错阶段，由调用方就地展示「重试」。 */
export async function uploadSinglePhoto(photo: PhotoDraft, deps: PhotoUploadDeps): Promise<PhotoUploadResult> {
  let stage: PhotoFailureStage = "read";
  try {
    const fileSize = await deps.readFileSize(photo.uri);
    if (!(fileSize > 0)) throw new Error("empty image");
    stage = "signature";
    const signature = await deps.requestSignature({ contentType: deps.contentType, fileSize });
    stage = "upload";
    await deps.putFile(photo.uri, signature);
    return { status: "uploaded", attachmentGuid: signature.attachmentGuid };
  } catch (error) {
    return { status: "failed", failureStage: stage, error };
  }
}

/**
 * 上传一批照片中尚未成功的那些（ready / failed）。
 * - 已 uploaded 的绝不重复上传；uploading 的不重复触发。
 * - 每张的状态变化通过 onPatch 即时回调，UI 逐张显示进度。
 * - 失败的不影响其余照片继续上传；返回值 allUploaded 表示这一批是否全部成功。
 * concurrency 默认 2，弱网下不至于同时占满带宽。
 */
export async function uploadPendingPhotos(
  photos: readonly PhotoDraft[],
  deps: PhotoUploadDeps,
  onPatch: (key: string, patch: PhotoUploadPatch) => void,
  concurrency = 2,
): Promise<{ allUploaded: boolean; failedKeys: string[] }> {
  const queue = photosNeedingUpload(photos);
  const failedKeys: string[] = [];
  let cursor = 0;

  const worker = async () => {
    while (cursor < queue.length) {
      const photo = queue[cursor];
      cursor += 1;
      onPatch(photo.key, { status: "uploading", failureStage: undefined });
      const result = await uploadSinglePhoto(photo, deps);
      if (result.status === "uploaded") {
        onPatch(photo.key, { status: "uploaded", attachmentGuid: result.attachmentGuid, failureStage: undefined });
      } else {
        failedKeys.push(photo.key);
        onPatch(photo.key, { status: "failed", failureStage: result.failureStage });
      }
    }
  };

  const workerCount = Math.max(1, Math.min(Math.trunc(concurrency) || 1, queue.length));
  await Promise.all(Array.from({ length: workerCount }, () => worker()));

  const allKnownUploaded = photos.every(
    (photo) => photo.status === "uploaded" || queue.some((queued) => queued.key === photo.key),
  );
  return { allUploaded: allKnownUploaded && failedKeys.length === 0, failedKeys };
}

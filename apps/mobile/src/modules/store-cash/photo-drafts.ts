// 存单 / 收据照片的草稿状态：纯数据结构与增删规则，不依赖 React 与原生模块。
//
// 状态流转：ready（已压缩、待上传）→ uploading → uploaded（拿到 attachmentGuid）
//                                      ↘ failed（可就地重试）
// 已 uploaded 的照片在提交失败重试时不会再次上传。

export type PhotoStatus = "ready" | "uploading" | "uploaded" | "failed";

export type PhotoFailureStage = "read" | "signature" | "upload";

export interface PhotoDraft {
  /** 本地唯一键，仅用于列表渲染与状态更新。 */
  key: string;
  /** 压缩后的本地 JPEG（长边 ≤ 2048、≤ 5 MiB）。 */
  uri: string;
  width: number;
  height: number;
  fileSize: number;
  status: PhotoStatus;
  /** 上传成功后服务端返回的附件编号，提交单据时带上。 */
  attachmentGuid?: string;
  failureStage?: PhotoFailureStage;
}

export interface ProcessedPhotoInput {
  uri: string;
  width: number;
  height: number;
  fileSize: number;
}

let photoKeyCounter = 0;

/** 本地唯一键：时间戳 + 自增，足够在一次表单会话里区分每张照片。 */
export function createPhotoKey(): string {
  photoKeyCounter += 1;
  return `photo-${Date.now().toString(36)}-${photoKeyCounter}`;
}

export function createPhotoDraft(key: string, input: ProcessedPhotoInput): PhotoDraft {
  return {
    key,
    uri: input.uri,
    width: input.width,
    height: input.height,
    fileSize: input.fileSize,
    status: "ready",
  };
}

/** 还能再加几张（上限为 0 或已满返回 0）。 */
export function remainingPhotoSlots(currentCount: number, maxCount: number): number {
  return Math.max(0, Math.trunc(maxCount) - currentCount);
}

/**
 * 往照片列表追加新照片，超出上限的部分被丢弃并计入 rejected，
 * 调用方据此提示「最多 N 张」。保持传入顺序。
 */
export function appendPhotos(
  current: readonly PhotoDraft[],
  incoming: readonly PhotoDraft[],
  maxCount: number,
): { photos: PhotoDraft[]; accepted: number; rejected: number } {
  const room = remainingPhotoSlots(current.length, maxCount);
  const accepted = incoming.slice(0, room);
  return {
    photos: [...current, ...accepted],
    accepted: accepted.length,
    rejected: incoming.length - accepted.length,
  };
}

export function removePhoto(current: readonly PhotoDraft[], key: string): PhotoDraft[] {
  return current.filter((photo) => photo.key !== key);
}

export function patchPhoto(
  current: readonly PhotoDraft[],
  key: string,
  patch: Partial<Omit<PhotoDraft, "key">>,
): PhotoDraft[] {
  return current.map((photo) => (photo.key === key ? { ...photo, ...patch } : photo));
}

export function hasUploadingPhoto(photos: readonly PhotoDraft[]): boolean {
  return photos.some((photo) => photo.status === "uploading");
}

export function hasFailedPhoto(photos: readonly PhotoDraft[]): boolean {
  return photos.some((photo) => photo.status === "failed");
}

export function allPhotosUploaded(photos: readonly PhotoDraft[]): boolean {
  return photos.every((photo) => photo.status === "uploaded" && Boolean(photo.attachmentGuid));
}

/** 需要（再次）上传的照片：已上传成功的不重复上传，正在上传的也不重复触发。 */
export function photosNeedingUpload(photos: readonly PhotoDraft[]): PhotoDraft[] {
  return photos.filter((photo) => photo.status === "ready" || photo.status === "failed");
}

/** 按展示顺序取附件编号；任一张未上传完成返回 null，避免带着残缺附件提交。 */
export function collectAttachmentGuids(photos: readonly PhotoDraft[]): string[] | null {
  if (!allPhotosUploaded(photos)) return null;
  return photos.map((photo) => photo.attachmentGuid as string);
}

/**
 * 把所有照片退回「待上传」并丢掉旧附件编号。
 * 服务端以 CASH_ATTACHMENT_INVALID 拒绝（签名过期、待确认附件被清理等）时使用：
 * 本地压缩好的文件还在，下次提交会重新申请签名并上传，用户不必重新拍照。
 */
export function resetPhotosForReupload(photos: readonly PhotoDraft[]): PhotoDraft[] {
  return photos.map((photo) => ({
    ...photo,
    status: "ready",
    attachmentGuid: undefined,
    failureStage: undefined,
  }));
}

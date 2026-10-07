// 附件展示的纯逻辑：详情里的图片带几分钟有效的私有下载地址，每次进详情重新取，不缓存过期的。
import type { CashAttachment } from "./types";

export interface ViewerImage {
  key: string;
  uri: string;
}

/** 签名地址在过期前留出的余量，避免用户点开时刚好过期。 */
export const ATTACHMENT_URL_SAFETY_MARGIN_MS = 30_000;

/** 地址是否已过期或即将过期；无法解析过期时间时按「已过期」处理，宁可多取一次。 */
export function isAttachmentUrlStale(
  attachment: Pick<CashAttachment, "urlExpiresAtUtc">,
  nowMs: number,
  marginMs: number = ATTACHMENT_URL_SAFETY_MARGIN_MS,
): boolean {
  const expiresAt = Date.parse(attachment.urlExpiresAtUtc);
  return !Number.isFinite(expiresAt) || expiresAt - marginMs <= nowMs;
}

export function anyAttachmentUrlStale(
  attachments: readonly Pick<CashAttachment, "urlExpiresAtUtc">[],
  nowMs: number,
): boolean {
  return attachments.some((attachment) => isAttachmentUrlStale(attachment, nowMs));
}

export function toViewerImages(attachments: readonly Pick<CashAttachment, "attachmentGuid" | "url">[]): ViewerImage[] {
  return attachments.map((attachment) => ({ key: attachment.attachmentGuid, uri: attachment.url }));
}

/** 由横向滚动偏移量换算当前页码，夹在有效范围内。 */
export function resolveViewerPage(offsetX: number, pageWidth: number, pageCount: number): number {
  if (pageWidth <= 0 || pageCount <= 0) return 0;
  return Math.min(pageCount - 1, Math.max(0, Math.round(offsetX / pageWidth)));
}

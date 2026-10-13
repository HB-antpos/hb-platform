// 腾讯云 COS 默认域名：<bucket>-<appid>.cos.<region>.myqcloud.com，桶已开通图片处理。
const COS_HOST_PATTERN = /^https?:\/\/[a-z0-9-]+\.cos\.[a-z0-9-]+\.myqcloud\.com\//i;

/**
 * 商品缩略图地址：COS 原图追加 imageMogr2 由 COS 实时缩放并转 WebP（单张 100KB+ 降到约 2KB），
 * 与 Web 的 toProductThumbnailUrl 规则一致；非 COS 或已带查询参数的地址原样返回。
 *
 * 注意：COS 缩略图响应带 30 天强缓存，商品图在 COS 上按原文件名覆盖后，设备上可能继续显示旧缩略图
 * （Web 靠后端的图片版本号规避，移动端目前不接）。
 */
export function productThumbnailUri(url: string | null | undefined, size: number) {
  const trimmed = url?.trim();
  if (!trimmed) return null;
  if (trimmed.includes("?") || !COS_HOST_PATTERN.test(trimmed)) return trimmed;
  return `${trimmed}?imageMogr2/thumbnail/${size}x${size}/format/webp`;
}

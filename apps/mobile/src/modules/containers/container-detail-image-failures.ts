/**
 * 货柜明细表格商品图的「加载失败」记忆（纯函数 + 模块级 Map，无 React 依赖，便于单测）。
 *
 * 为什么需要：后端会给每个有货号的明细补默认图地址（…/YW200/<货号>.jpg），没上传过图片的商品这个地址是 404。
 * 表格是虚拟化的，行滚出窗口再滚回来会重新挂载，组件内 state 会丢，同一个 404 地址就会被反复请求。
 * 这里按地址记住失败，重新挂载的行直接显示占位，不再发请求。
 *
 * 失败记忆有有效期：onError 也可能只是网络抖动，永久记住会让图片在网络恢复后仍然不出现。
 * 过期后下一次挂载会重试一次；有效期内的重试被压住，所以不会形成请求风暴。
 */

/** 失败记忆有效期：10 分钟。 */
export const CONTAINER_DETAIL_IMAGE_FAILURE_TTL_MS = 10 * 60 * 1000;
/** 最多记住的失败地址数，超出后淘汰最早的（一页最多 500 行，翻几页也不会无限增长）。 */
export const CONTAINER_DETAIL_IMAGE_FAILURE_MAX_ENTRIES = 1000;

/** 地址 -> 失败时间戳；Map 按插入顺序迭代，第一个键就是最早记住的。 */
const failedAtByUri = new Map<string, number>();

export function hasContainerDetailImageFailed(uri: string, now: number = Date.now()): boolean {
  const failedAt = failedAtByUri.get(uri);
  if (failedAt === undefined) return false;
  if (now - failedAt >= CONTAINER_DETAIL_IMAGE_FAILURE_TTL_MS) {
    failedAtByUri.delete(uri);
    return false;
  }
  return true;
}

export function rememberContainerDetailImageFailure(uri: string, now: number = Date.now()): void {
  // 先删再放：已存在的地址刷新时间，并移到「最新」的位置
  failedAtByUri.delete(uri);
  failedAtByUri.set(uri, now);
  while (failedAtByUri.size > CONTAINER_DETAIL_IMAGE_FAILURE_MAX_ENTRIES) {
    const oldest = failedAtByUri.keys().next();
    if (oldest.done) break;
    failedAtByUri.delete(oldest.value);
  }
}

/** 仅供测试：清空记忆。 */
export function resetContainerDetailImageFailures(): void {
  failedAtByUri.clear();
}

/** 仅供测试：当前记住的失败地址数。 */
export function countContainerDetailImageFailures(): number {
  return failedAtByUri.size;
}

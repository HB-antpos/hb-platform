import type { CashStoreOption } from "./types";

/**
 * 当前操作的分店：路由参数指定的优先，其次 App 全局已选分店（若在可操作列表里），最后是列表第一项。
 * 可操作分店以 context.stores 为准——店长只有自己关联的分店，有全部分店权限者是全部分店。
 */
export function resolveActiveStore(
  stores: readonly CashStoreOption[],
  requestedCode: string | null | undefined,
  preferredCode?: string | null,
): CashStoreOption | null {
  const byCode = (code: string | null | undefined) =>
    code ? stores.find((store) => store.storeCode === code) ?? null : null;
  return byCode(requestedCode) ?? byCode(preferredCode) ?? stores[0] ?? null;
}

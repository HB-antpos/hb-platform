import { canDownloadCatalog } from "@hb/pos-domain/features/catalog/maintenance/catalog-maintenance-authorization";
import { DAILY_CLOSE_VIEW_PERMISSION } from "@hb/pos-domain/features/daily-close/daily-close-authorization";
import { INSTALLMENTS_VIEW_PERMISSION } from "@hb/pos-domain/features/installments/installment-authorization";
import { REMOTE_HISTORY_VIEW_PERMISSION } from "@hb/pos-domain/features/remote-history/remote-history-presenter";

import { LOCAL_HISTORY_VIEW_PERMISSION } from "@/features/local-history/local-history-presenter";
import { SETTINGS_VIEW_PERMISSION } from "@/features/settings/settings-authorization";
import {
  POS_DESTINATION_IDS,
  type PosDestinationId,
} from "@/ui/shell/pos-navigation";

/**
 * 当前收银员能看到哪些页面入口。
 *
 * 这里只决定入口显不显示；每个路由进入时仍会各自再校验一次权限，
 * 所以这里漏判不会放行，只会多显示或少显示一个按钮。
 */
export function resolvePosDestinationAccess(
  permissions: readonly string[] | null | undefined,
): ReadonlySet<PosDestinationId> {
  if (!permissions) return new Set();
  const has = (permission: string) => permissions.includes(permission);
  const visible: Record<PosDestinationId, boolean> = {
    "held-orders": true,
    returns: true,
    installments: has(INSTALLMENTS_VIEW_PERMISSION),
    "payment-recovery": true,
    "local-history": has(LOCAL_HISTORY_VIEW_PERMISSION),
    "remote-history": has(REMOTE_HISTORY_VIEW_PERMISSION),
    "sync-history": true,
    "daily-close": has(DAILY_CLOSE_VIEW_PERMISSION),
    "attendance-audit": true,
    "catalog-maintenance": canDownloadCatalog(permissions),
    settings: has(SETTINGS_VIEW_PERMISSION),
  };
  return new Set(POS_DESTINATION_IDS.filter((id) => visible[id]));
}

/** 各入口对应的路由路径。 */
export const POS_DESTINATION_ROUTES: Readonly<Record<PosDestinationId, string>> =
  Object.freeze({
    "held-orders": "/held-orders",
    returns: "/returns",
    installments: "/installments",
    "payment-recovery": "/payment-recovery",
    "local-history": "/local-history",
    "remote-history": "/remote-history",
    "sync-history": "/sync-history",
    "daily-close": "/daily-close",
    "attendance-audit": "/attendance-audit",
    "catalog-maintenance": "/catalog-maintenance",
    settings: "/settings",
  });

const guards = new Map<PosDestinationId, () => boolean>();

/**
 * 登记某个入口的进入前检查，返回取消登记的函数。
 *
 * 用途：收银页有「一码多商品待选」时不能去取单（取单会替换购物车）。
 * 左侧栏在所有页面都能点，所以这个检查不能只写在收银页的按钮上。
 */
export function registerPosDestinationGuard(
  destinationId: PosDestinationId,
  guard: () => boolean,
): () => void {
  guards.set(destinationId, guard);
  return () => {
    if (guards.get(destinationId) === guard) guards.delete(destinationId);
  };
}

/** 没有登记检查的入口一律放行。 */
export function passesPosDestinationGuard(
  destinationId: PosDestinationId,
): boolean {
  return guards.get(destinationId)?.() ?? true;
}

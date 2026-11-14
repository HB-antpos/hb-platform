import { PERMISSIONS } from "@/shared/utils/access";

/** 收银端设置两个权限码（后端 Permissions.PosSettings.*）。 */
export const POS_SETTINGS_PERMISSIONS = {
  view: PERMISSIONS.PosSettings.View,
  manage: PERMISSIONS.PosSettings.Manage,
} as const;

/** 适用端的展示顺序。 */
export const POS_CLIENT_KIND_ORDER = ["wpf", "ipad", "handheld"] as const;

/** 保存后收银机约 1 分钟内同步（文案里的数字，界面统一引用）。 */
export const POS_SETTINGS_SYNC_MINUTES = 1;

/**
 * 设置键里带「.」，而 i18next 的 keySeparator 也是「.」，所以文案键把「.」换成「_」。
 * 例：cashMovement.enabled → settings.cashMovement_enabled.name。
 */
export function settingCopyId(settingKey: string): string {
  return settingKey.replace(/\./g, "_");
}

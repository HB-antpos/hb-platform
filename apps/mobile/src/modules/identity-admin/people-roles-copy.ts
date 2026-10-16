import en from "@/locales/en/screens/peopleRoles.json";
import zh from "@/locales/zh/screens/peopleRoles.json";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

/**
 * 角色详情 / 权限详情改版新增的文案（命名空间 peopleRoles）。
 * 与同模块其他页面一致：直接导入 JSON、按语言取对象，键写错会在编译期报错。
 */
export type PeopleRolesCopy = typeof zh;

export function usePeopleRolesCopy(): PeopleRolesCopy {
  const { language } = useAppTranslation();
  return language.startsWith("zh") ? zh : en;
}

/** 简单的 {{name}} 占位替换（与其他 identity-admin 页面的 interpolate 同口径）。 */
export function fillCopy(template: string, params: Record<string, string | number> = {}) {
  return Object.entries(params).reduce((text, [key, value]) => text.split(`{{${key}}}`).join(String(value)), template);
}

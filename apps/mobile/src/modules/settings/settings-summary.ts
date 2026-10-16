// 设置页首屏用到的纯逻辑：连接状态汇总、名片类型与角色标签（不依赖 React，便于单测）。
import type { AppLanguage } from "@/shared/i18n/types";
import { localizeAccessRoleName } from "@/modules/users/access-permission-presentation";

export interface ConnectionAttentionInput {
  /** 有设备会话 / 绑定 / 登录用户时才需要关心设备是否就绪。 */
  canViewDeviceCard: boolean;
  deviceReady: boolean;
  labelPrinterError: boolean;
  receiptPrinterError: boolean;
}

/**
 * 「设备与打印」需要处理的项数：设备未就绪、标签机出错、小票机出错各算一项。
 * 首页分区标题右侧的汇总胶囊（「全部正常」/「N 项需处理」）就用它，取代原来 70px 高的汇总卡。
 */
export function countAttentionItems(input: ConnectionAttentionInput): number {
  return (
    (input.canViewDeviceCard && !input.deviceReady ? 1 : 0) +
    (input.labelPrinterError ? 1 : 0) +
    (input.receiptPrinterError ? 1 : 0)
  );
}

export type IdentityKind = "account" | "device" | "none";

/** 名片显示谁：有登录用户就是账号名片，只有设备授权就是设备名片，都没有则只显示「未登录」。 */
export function resolveIdentityKind(input: { hasUser: boolean; hasDeviceSession: boolean; hasAccountBinding: boolean }): IdentityKind {
  if (input.hasUser) return "account";
  if (input.hasDeviceSession || input.hasAccountBinding) return "device";
  return "none";
}

export interface RoleTag {
  label: string;
  /** 除了显示的这个角色，还有几个（显示成「+N」）。 */
  extra: number;
}

/** 名片上的角色标签：取第一个角色并本地化，其余折成「+N」；没有角色则不显示。 */
export function resolveRoleTag(roleNames: readonly string[] | undefined, language: AppLanguage): RoleTag | null {
  const names = (roleNames ?? []).map((name) => name.trim()).filter(Boolean);
  if (names.length === 0) return null;
  return { label: localizeAccessRoleName(names[0], language), extra: names.length - 1 };
}

/** 名片第二行：账号 · 分店；缺哪个就省略哪个，不留悬空的分隔符。 */
export function buildIdentityMeta(parts: readonly (string | null | undefined)[]): string {
  return parts
    .map((part) => part?.trim())
    .filter((part): part is string => Boolean(part))
    .join(" · ");
}

/** 名片头像里的字：姓名首字（英文取首字母大写），没有名字用 ?。 */
export function resolveAvatarInitial(name: string | null | undefined): string {
  const trimmed = (name ?? "").trim();
  if (!trimmed) return "?";
  const first = Array.from(trimmed)[0] ?? "?";
  return first.toUpperCase();
}

/** 「更多设置」页里「运行时版本」一行：没有就不显示这一行。 */
export function resolveRuntimeVersionLabel(runtimeVersion: string | null | undefined): string | null {
  const value = runtimeVersion?.trim();
  return value ? value : null;
}

import type { StoreUserListItem } from "@/modules/users/types";

/**
 * 员工管理列表的展示口径：店长本人置顶（只读）+ 其余店员。
 * 范围本身由后端收口（本店店员 + 本人，不含其他店长 / 管理员）；这里只做展示分组，
 * 不能当作权限边界——前端不据此判断「该不该看到某人」。
 */
export function splitSelfAndStaff(
  items: readonly StoreUserListItem[],
  actorGuid: string
): { self: StoreUserListItem | null; staff: StoreUserListItem[] } {
  const normalizedActor = actorGuid.trim().toLowerCase();
  if (!normalizedActor) {
    return { self: null, staff: [...items] };
  }
  const self = items.find((item) => item.userGUID.trim().toLowerCase() === normalizedActor) ?? null;
  return {
    self,
    staff: items.filter((item) => item !== self),
  };
}

/** 本人行：只读，不提供停用、重置密码、改权限、个人码等操作。 */
export function isSelfStaffRow(item: StoreUserListItem, actorGuid: string): boolean {
  const normalizedActor = actorGuid.trim().toLowerCase();
  return normalizedActor.length > 0 && item.userGUID.trim().toLowerCase() === normalizedActor;
}

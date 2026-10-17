/**
 * 新增页有两个版本，由路由参数与当前身份决定（跨块路由契约，见 /users/new）：
 * - 有 `storeCode`，或当前账号不是管理员 → 店长版（固定本店店员，走店员接口）；
 * - 无 `storeCode` 且是管理员账号会话（有创建权限）→ 管理员版（可选角色、多家分店、初始密码）。
 * 管理员接口需要 account 会话；设备模式 / iOS 审核会话不满足，会落到店长版（再由店长版自己按分店范围拦截）。
 */
export type CreateVariant = "staff" | "admin";

export function resolveCreateVariant(input: {
  storeCode: string;
  isAdmin: boolean;
  hasUsersCreate: boolean;
  /** isIdentitySessionAllowed 的结果：account 会话、已登录、非审核离线守卫、且有 Users.Create。 */
  adminSessionAllowed: boolean;
}): CreateVariant {
  if (input.storeCode.trim()) return "staff";
  return input.isAdmin && input.hasUsersCreate && input.adminSessionAllowed ? "admin" : "staff";
}

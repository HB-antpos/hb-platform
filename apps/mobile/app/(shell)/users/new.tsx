import { useLocalSearchParams } from "expo-router";

import { AdminCreateScreen } from "@/modules/people/form/admin-create-screen";
import { resolveCreateVariant } from "@/modules/people/form/create-variant";
import { StaffCreateScreen } from "@/modules/people/form/staff-create-screen";
import { useIdentitySession } from "@/modules/identity-admin/user-hooks";
import { useAuthStore } from "@/store/auth-store";

/**
 * 「人员管理」新增页，一个入口两个版本（路由契约：`/users/new?storeCode=`）：
 * - 有 `storeCode`，或当前账号不是管理员 → 店长版：固定本店店员，走店员接口（StaffCreateScreen）；
 * - 管理员且没带 `storeCode` → 管理员版：可选角色、多家分店与初始密码，走管理员接口（AdminCreateScreen）。
 * 两个版本成功后都在本页内展示整页「创建完成」结果，不是弹窗。
 */
export default function NewPersonScreen() {
  const params = useLocalSearchParams<{ storeCode?: string | string[] }>();
  const storeCode = (Array.isArray(params.storeCode) ? params.storeCode[0] : params.storeCode) ?? "";
  const access = useAuthStore((state) => state.access);
  const session = useIdentitySession("Users.Create");

  const variant = resolveCreateVariant({
    storeCode,
    isAdmin: access.isAdmin,
    hasUsersCreate: access.hasPermission("Users.Create"),
    adminSessionAllowed: session.allowed,
  });

  // 管理员版按账号隔离草稿：切换账号后不得沿用上一个账号填写的内容。
  return variant === "admin"
    ? <AdminCreateScreen key={session.actorKey} />
    : <StaffCreateScreen storeCode={storeCode} />;
}

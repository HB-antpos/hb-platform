import { Redirect } from "expo-router";
import { buildPeopleHubRoute } from "@/modules/people/people-hub-tabs";

// 旧入口兼容：「角色管理」已并入「人员管理」的「角色」页签；没有 Roles.View 的账号在 Hub 里会回落到「人员」页签。
export default function RolesRoute() {
  return <Redirect href={buildPeopleHubRoute("roles")} />;
}

import { PeopleHubScreen } from "@/modules/people/people-hub-screen";

// 「人员管理」Hub：人员 / 角色 / 权限三个页签，页签由 ?tab= 驱动（见 modules/people/people-hub-screen.tsx）。
export default function UsersRoute() {
  return <PeopleHubScreen />;
}

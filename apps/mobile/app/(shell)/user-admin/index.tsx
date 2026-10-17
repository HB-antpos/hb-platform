import { Redirect } from "expo-router";
import { buildPeopleHubRoute } from "@/modules/people/people-hub-tabs";

// 旧入口兼容：「用户管理」已并入「人员管理」的「人员」页签；旧版 App、推送与书签仍可能落到这里。
export default function UserAdminRoute() {
  return <Redirect href={buildPeopleHubRoute("people")} />;
}

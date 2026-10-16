// 「人员管理」Hub（路由 /users）：原「员工管理」（店长）与「用户管理 / 角色管理 / 权限管理」（管理员）合并成一个入口。
// 页签 = 人员 | 角色 | 权限；页签状态由 URL 参数 ?tab= 驱动，从详情页返回时仍停在原页签。
//
// 谁能看到什么：
// - 「人员」页签：管理员（全局用户查看 + account 会话）看全局用户列表（AdminUserList），其余人看本店店员列表（StoreStaffList）；
// - 「角色」「权限」页签：只有满足原 roles / permissions 入口门禁（identity-admin-access 的 Roles.View 判断：
//   account 会话、非设备模式、非 iOS 审核、持有 Roles.View）才显示；其余人只有人员列表，不画页签条。
//   iOS 审核模式下这两个页签不存在，也不会挂载任何管理员列表，因此不会发出管理员接口请求。
//
// 页签切换的取舍：访问过的页签保持挂载（隐藏不卸载），搜索词、筛选与滚动位置都保留；没访问过的页签不挂载，不提前取数据。
import { useCallback, useEffect, useMemo, useState } from "react";
import { StyleSheet, View } from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import { EmptyState } from "@/components/ui/EmptyState";
import { AdminUserList } from "@/modules/identity-admin/users-screen";
import { PermissionsList } from "@/modules/identity-admin/permissions-screen";
import { RolesList } from "@/modules/identity-admin/roles-screen";
import { useIdentitySession } from "@/modules/identity-admin/user-hooks";
import { StoreStaffList } from "@/modules/users/store-staff-list";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { PERMISSIONS } from "@/shared/utils/access";
import { useAuthStore } from "@/store/auth-store";
import { PeopleScreenFrame, type PeopleHubChrome } from "./people-frame";
import {
  normalizeHubTab,
  resolveHubTabs,
  resolvePeopleListMode,
  shouldShowHubTabBar,
  type PeopleHubTab,
} from "./people-hub-tabs";
import { SegmentTabs } from "./ui";

export function PeopleHubScreen() {
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  // 账号变化时重建页面状态，避免把上一个账号的筛选、搜索词和列表留在屏幕上。
  return <PeopleHubContent key={userGuid ?? "anonymous"} />;
}

function PeopleHubContent() {
  const router = useRouter();
  const { t } = useAppTranslation(["people", "common", "userManagement"]);
  const access = useAuthStore((state) => state.access);
  // 两个入口门禁：Users.View（管理员版人员列表）与 Roles.View（角色 / 权限页签），都要求 account 会话、非设备、非审核。
  const usersGate = useIdentitySession("Users.View");
  const rolesGate = useIdentitySession("Roles.View");
  const { tab } = useLocalSearchParams<{ tab?: string | string[] }>();

  // 员工管理入口认 Users.ViewStoreStaff；Users.View（全局用户查看）持有者同样放行。
  const canViewPeople =
    usersGate.allowed
    || access.isAdmin
    || access.canReadUser
    || access.hasPermission(PERMISSIONS.Users.ViewStoreStaff);
  const tabs = useMemo(
    () => resolveHubTabs({ canViewPeople, canViewRolesAndPermissions: rolesGate.allowed }),
    [canViewPeople, rolesGate.allowed]
  );
  const listMode = resolvePeopleListMode({
    identityUsersAllowed: usersGate.allowed,
    isStoreLevelManager: access.isStoreLevelManager,
  });

  const [requestedTab, setRequestedTab] = useState<PeopleHubTab>(() => normalizeHubTab(tab, tabs));
  // 路由参数变化（深链、重定向壳、同一页面被再次指向）时跟着切页签；非法 / 无权限的 tab 回落到人员。
  useEffect(() => {
    setRequestedTab(normalizeHubTab(tab, tabs));
  }, [tab, tabs]);
  // 权限在页面打开期间被收回时，当前页签也要重新规范化。
  const activeTab = normalizeHubTab(requestedTab, tabs);
  const [visited, setVisited] = useState<ReadonlySet<PeopleHubTab>>(() => new Set([activeTab]));
  useEffect(() => {
    setVisited((previous) => (previous.has(activeTab) ? previous : new Set(previous).add(activeTab)));
  }, [activeTab]);

  const selectTab = useCallback(
    (next: PeopleHubTab) => {
      setRequestedTab(next);
      // 把页签写进 URL：进详情页再返回时仍停在原页签。
      router.setParams({ tab: next });
    },
    [router]
  );
  const goBack = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.replace("/(shell)/workbench");
  }, [router]);

  const tabBar = shouldShowHubTabBar(tabs) ? (
    <SegmentTabs
      items={tabs.map((key) => ({ key, label: t(`people:hub.tabs.${key}`) }))}
      value={activeTab}
      onChange={selectTab}
    />
  ) : null;
  const chrome: PeopleHubChrome = { tabBar, onBack: goBack };

  if (!tabs.length) {
    return (
      <PeopleScreenFrame title={t("people:hub.title")} chrome={chrome}>
        <EmptyState title={t("people:hub.noAccessTitle")} description={t("people:hub.noAccessDescription")} />
      </PeopleScreenFrame>
    );
  }

  const renders = (key: PeopleHubTab) => tabs.includes(key) && (key === activeTab || visited.has(key));
  const panelStyle = (key: PeopleHubTab) => (key === activeTab ? styles.panel : styles.hidden);

  return (
    <View style={styles.root}>
      {renders("people") ? (
        <View style={panelStyle("people")}>
          {listMode === "admin" ? (
            <AdminUserList chrome={chrome} active={activeTab === "people"} />
          ) : (
            <StoreStaffList chrome={chrome} />
          )}
        </View>
      ) : null}
      {renders("roles") ? (
        <View style={panelStyle("roles")}>
          <RolesList chrome={chrome} />
        </View>
      ) : null}
      {renders("permissions") ? (
        <View style={panelStyle("permissions")}>
          <PermissionsList chrome={chrome} />
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  panel: { flex: 1 },
  // 隐藏而不卸载：保留面板内的搜索词、筛选和滚动位置。
  hidden: { display: "none" },
});

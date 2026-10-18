import { useCallback, useEffect, useMemo, useState } from "react";
import { Alert, ScrollView, StyleSheet, View } from "react-native";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useLocalSearchParams, useRouter } from "expo-router";
import { ActivityIndicator, Button, IconButton, Snackbar, Text } from "react-native-paper";
import { ActionRow, InfoCard, PeopleTag, SectionTitle, StickyActionBar } from "@/modules/people";
import { HB_COLORS as C } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { localizeAccessRoleName } from "@/modules/users/access-permission-presentation";
import {
  assignIdentityPermissionRoles,
  assignIdentityPermissionUsers,
  deleteIdentitySysPermission,
  fetchIdentityPermissionRoles,
  fetchIdentityPermissionUsers,
  fetchIdentityRoles,
  fetchIdentityUsers,
  getIdentityAdminErrorMeta,
} from "./api";
import { AdminDetailShell } from "./admin-detail-shell";
import { usePeopleRolesCopy } from "./people-roles-copy";
import { AssignRolesSheet, AssignUsersSheet } from "./permission-assign-sheets";
import type { IdentityRole, IdentityRoleUser } from "./types";
import { backToHubTab } from "./role-detail-logic";
import { isUncertainRoleWrite } from "./role-logic";
import {
  areRoleGuidsEqual,
  buildAssignRoleRows,
  countAssignedRoles,
  createRoleAssignmentDraft,
  findPermissionListItem,
  formatPermissionUserName,
  getRoleAssignmentDelta,
  isImplicitAllRoleName,
  isPermissionUserDeltaApplied,
  isRoleAssignmentDirty,
  toggleRoleAssignment,
  type RoleAssignmentDraft,
} from "./permission-logic";
import { interpolatePermissionCopy as interpolate, permissionQueryKeys, usePermissionCopy, usePermissionItems, usePermissionSession } from "./permission-hooks";
import { AdminEmpty, AdminError } from "./ui";
import { identityDate, useIdentitySearch } from "./user-hooks";
import { labelColon } from "@/shared/i18n/punctuation";

type PermissionUserLite = Pick<IdentityRoleUser, "userGUID" | "username" | "email" | "fullName" | "isActive">;

function toPermissionUserLite(user: PermissionUserLite): PermissionUserLite {
  return { userGUID: user.userGUID, username: user.username, email: user.email, isActive: user.isActive, ...(user.fullName ? { fullName: user.fullName } : {}) };
}

function matchesPermissionUser(user: PermissionUserLite, keyword: string) {
  return !keyword || [user.username, user.fullName ?? "", user.email].some((value) => value.toLocaleLowerCase().includes(keyword));
}

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value;
}

async function fetchAllRoles(actorGuid: string, signal?: AbortSignal) {
  const pageSize = 200;
  const roles: IdentityRole[] = [];
  for (let page = 1; ; page += 1) {
    const result = await fetchIdentityRoles({ page, pageSize }, actorGuid, signal);
    roles.push(...result.items);
    if (page >= result.totalPages) return roles;
  }
}

export default function PermissionDetailScreen() {
  const params = useLocalSearchParams<{ code?: string | string[] }>();
  const code = firstParam(params.code)?.trim() ?? "";
  const { actorKey } = usePermissionSession();
  // 账号或权限代码变化时强制重建状态，避免分配草稿跨身份残留。
  return <PermissionDetailContent key={`${actorKey}:${code.toLocaleLowerCase()}`} code={code} />;
}

function PermissionDetailContent({ code }: { code: string }) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const { copy, appLanguage } = usePermissionCopy();
  const p = usePeopleRolesCopy();
  const { allowed, actorKey, capabilities, access } = usePermissionSession();
  // 权限反查用户是管理员专属接口（服务端 ADMIN_REQUIRED），非管理员不发请求也不展示该区块。
  const canViewUsers = allowed && access.isAdmin;
  const [assignOpen, setAssignOpen] = useState(false);
  const [assignSearch, setAssignSearch] = useState("");
  const [onlySelected, setOnlySelected] = useState(false);
  const [assignment, setAssignment] = useState<RoleAssignmentDraft | null>(null);
  const [writeForbidden, setWriteForbidden] = useState(false);
  const [notice, setNotice] = useState("");
  // 分配弹层打开时 Snackbar 会被盖住：保存失败的原因放在各自弹层里就地显示。
  const [assignNotice, setAssignNotice] = useState("");
  const [userAssignNotice, setUserAssignNotice] = useState("");
  const [userAssignOpen, setUserAssignOpen] = useState(false);
  const [userSearch, setUserSearch] = useState("");
  const debouncedUserSearch = useIdentitySearch(userSearch);
  const [onlySelectedUsers, setOnlySelectedUsers] = useState(false);
  // 复用 GUID 集合草稿：baseline 为服务端直接授权用户，保存时只提交增量。
  const [userAssignment, setUserAssignment] = useState<RoleAssignmentDraft | null>(null);
  // 搜索结果按关键字分页，切换关键字后旧页不在当前数据里；这里记住见过的用户，「仅看已选」时仍能显示名称。
  const [seenUsers, setSeenUsers] = useState<Map<string, PermissionUserLite>>(() => new Map());

  const permissions = usePermissionItems({ actorKey, enabled: allowed, appLanguage });
  const item = useMemo(() => findPermissionListItem(permissions.items, code), [code, permissions.items]);
  const superAdminRoleNames = permissions.catalog?.superAdminRoleNames;

  const assignedQuery = useQuery({
    queryKey: permissionQueryKeys.permissionRoles(actorKey, code),
    enabled: allowed && Boolean(code),
    retry: false,
    queryFn: ({ signal }) => fetchIdentityPermissionRoles(code, actorKey, signal),
  });
  const allRolesQuery = useQuery({
    queryKey: permissionQueryKeys.allRoles(actorKey),
    enabled: allowed && Boolean(code),
    retry: false,
    queryFn: ({ signal }) => fetchAllRoles(actorKey, signal),
  });

  const assignedUsersQuery = useQuery({
    queryKey: permissionQueryKeys.permissionUsers(actorKey, code),
    enabled: canViewUsers && Boolean(code),
    retry: false,
    queryFn: ({ signal }) => fetchIdentityPermissionUsers(code, actorKey, signal),
  });
  const userCandidatesQuery = useInfiniteQuery({
    queryKey: permissionQueryKeys.permissionUserCandidates(actorKey, debouncedUserSearch),
    enabled: userAssignOpen && capabilities.canManage && !writeForbidden,
    retry: false,
    initialPageParam: 1,
    queryFn: ({ pageParam, signal }) => fetchIdentityUsers({ page: pageParam, pageSize: 50, search: debouncedUserSearch || undefined }, actorKey, signal),
    getNextPageParam: (lastPage) => lastPage.page < lastPage.totalPages ? lastPage.page + 1 : undefined,
  });

  const isLockedRole = useCallback(
    (role: Pick<IdentityRole, "roleName">) => isImplicitAllRoleName(role.roleName, superAdminRoleNames),
    [superAdminRoleNames],
  );
  // 服务端分配接口会跳过超级管理员角色，因此显式分配列表里不会出现它们；详情页单独补上「隐式」行。
  const implicitRoles = useMemo(() => (allRolesQuery.data ?? []).filter(isLockedRole), [allRolesQuery.data, isLockedRole]);
  const explicitRoles = useMemo(() => (assignedQuery.data ?? []).filter((role) => !isLockedRole(role)), [assignedQuery.data, isLockedRole]);
  const explicitGuids = useMemo(() => explicitRoles.map((role) => role.roleGUID), [explicitRoles]);

  useEffect(() => {
    if (!assignedQuery.data) return;
    // 后台刷新不能覆盖用户尚未保存的勾选；仅干净草稿接受服务器最新值。
    setAssignment((current) => current && isRoleAssignmentDirty(current) ? current : createRoleAssignmentDraft(explicitGuids));
  }, [assignedQuery.data, explicitGuids]);

  const assignedUsers = useMemo(() => assignedUsersQuery.data ?? [], [assignedUsersQuery.data]);
  const assignedUserGuids = useMemo(() => assignedUsers.map((user) => user.userGUID), [assignedUsers]);

  useEffect(() => {
    if (!assignedUsersQuery.data) return;
    // 与角色草稿相同：后台刷新只覆盖干净草稿，不吞掉尚未保存的勾选。
    setUserAssignment((current) => current && isRoleAssignmentDirty(current) ? current : createRoleAssignmentDraft(assignedUserGuids));
  }, [assignedUsersQuery.data, assignedUserGuids]);

  useEffect(() => {
    const incoming = [...assignedUsers, ...(userCandidatesQuery.data?.pages.flatMap((page) => page.items) ?? [])];
    if (incoming.length === 0) return;
    setSeenUsers((current) => {
      let changed = false;
      const next = new Map(current);
      for (const user of incoming) {
        const previous = next.get(user.userGUID);
        if (!previous || previous.username !== user.username || previous.fullName !== user.fullName || previous.email !== user.email || previous.isActive !== user.isActive) {
          next.set(user.userGUID, toPermissionUserLite(user));
          changed = true;
        }
      }
      return changed ? next : current;
    });
  }, [assignedUsers, userCandidatesQuery.data]);

  const assertCurrentSession = useCallback((permission: string) => {
    const state = useAuthStore.getState();
    const currentActorKey = state.user?.userGUID ?? "";
    if (
      !state.isAuthenticated
      || state.sessionKind !== "account"
      || state.iosReviewOfflineGuardActive
      || currentActorKey !== actorKey
      || !state.access.hasPermission(permission)
    ) {
      throw Object.assign(new Error("IDENTITY_PERMISSION_SESSION_CHANGED"), { status: 403 });
    }
  }, [actorKey]);
  const canApplyMutationResult = useCallback((permission: string) => {
    try {
      assertCurrentSession(permission);
      return true;
    } catch {
      return false;
    }
  }, [assertCurrentSession]);
  /** 写入失败：服务器拒绝或结果不明确时冻结后续写入，并返回应该给用户看的原因（由调用方放到正确的位置）。 */
  const handleMutationError = useCallback((error: unknown) => {
    const status = getIdentityAdminErrorMeta(error).status;
    if ([401, 403].includes(status ?? 0) || isUncertainRoleWrite(error)) {
      setWriteForbidden(true);
      return [401, 403].includes(status ?? 0) ? copy.forbidden : copy.uncertainWrite;
    }
    return copy.saveFailed;
  }, [copy.forbidden, copy.saveFailed, copy.uncertainWrite]);

  const assignMutation = useMutation({
    retry: false,
    mutationFn: async () => {
      if (!assignment) throw new Error("Assignment draft unavailable");
      assertCurrentSession("Roles.ManagePermissions");
      await assignIdentityPermissionRoles(code, assignment.selected, actorKey);
      assertCurrentSession("Roles.ManagePermissions");
      const verified = await fetchIdentityPermissionRoles(code, actorKey);
      assertCurrentSession("Roles.ManagePermissions");
      const verifiedGuids = verified.filter((role) => !isLockedRole(role)).map((role) => role.roleGUID);
      if (!areRoleGuidsEqual(assignment.selected, verifiedGuids)) throw new Error("PERMISSION_ROLE_READBACK_MISMATCH");
      return verified;
    },
    onSuccess: async (verified) => {
      if (!canApplyMutationResult("Roles.ManagePermissions")) return;
      queryClient.setQueryData(permissionQueryKeys.permissionRoles(actorKey, code), verified);
      setAssignment(createRoleAssignmentDraft(verified.filter((role) => !isLockedRole(role)).map((role) => role.roleGUID)));
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: permissionQueryKeys.roleCounts(actorKey) }),
        // 角色详情页的权限勾选与本次分配是同一份数据，需一并失效。
        queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "rolePermissionState"] }),
      ]);
      if (!canApplyMutationResult("Roles.ManagePermissions")) return;
      setAssignNotice("");
      setAssignOpen(false);
      setNotice(copy.assignmentSaved);
    },
    onError: (error) => { setAssignNotice(handleMutationError(error)); },
  });
  const deleteMutation = useMutation({
    retry: false,
    mutationFn: async () => {
      assertCurrentSession("Roles.ManagePermissions");
      await deleteIdentitySysPermission(code, actorKey);
      assertCurrentSession("Roles.ManagePermissions");
    },
    onSuccess: async () => {
      if (!canApplyMutationResult("Roles.ManagePermissions")) return;
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: permissionQueryKeys.catalog(actorKey) }),
        queryClient.invalidateQueries({ queryKey: permissionQueryKeys.sysPermissions(actorKey) }),
        queryClient.invalidateQueries({ queryKey: permissionQueryKeys.roleCounts(actorKey) }),
        queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "rolePermissionState"] }),
        queryClient.invalidateQueries({ queryKey: permissionQueryKeys.permissionUsers(actorKey, code) }),
      ]);
      backToHubTab(router, "permissions");
    },
    onError: (error) => { setNotice(handleMutationError(error)); },
  });

  const userAssignMutation = useMutation({
    retry: false,
    mutationFn: async () => {
      if (!userAssignment) throw new Error("User assignment draft unavailable");
      const delta = getRoleAssignmentDelta(userAssignment);
      assertCurrentSession("Roles.ManagePermissions");
      await assignIdentityPermissionUsers(code, { addUserGuids: delta.added, removeUserGuids: delta.removed }, actorKey);
      assertCurrentSession("Roles.ManagePermissions");
      const verified = await fetchIdentityPermissionUsers(code, actorKey);
      assertCurrentSession("Roles.ManagePermissions");
      if (!isPermissionUserDeltaApplied(delta, verified.map((user) => user.userGUID))) throw new Error("PERMISSION_USER_READBACK_MISMATCH");
      return verified;
    },
    onSuccess: async (verified) => {
      if (!canApplyMutationResult("Roles.ManagePermissions")) return;
      queryClient.setQueryData(permissionQueryKeys.permissionUsers(actorKey, code), verified);
      setUserAssignment(createRoleAssignmentDraft(verified.map((user) => user.userGUID)));
      await Promise.all([
        // 用户授权页与员工访问管理读取同一张直接权限表，需一并失效。
        queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "access-permissions"] }),
        queryClient.invalidateQueries({ queryKey: ["userAccessManagement"] }),
      ]);
      if (!canApplyMutationResult("Roles.ManagePermissions")) return;
      setUserAssignNotice("");
      setUserAssignOpen(false);
      setNotice(copy.userAssignmentSaved);
    },
    onError: (error) => {
      // 权限未入库是确定的业务拒绝，给出可操作的原因而不是冻结写入。
      if (getIdentityAdminErrorMeta(error).code === "PERMISSION_NOT_FOUND") {
        setUserAssignNotice(copy.permissionNotPersisted);
        return;
      }
      setUserAssignNotice(handleMutationError(error));
    },
  });

  const handleVerifyRefresh = useCallback(async () => {
    if (!canApplyMutationResult("Roles.View")) return;
    try {
      assertCurrentSession("Roles.View");
      const [verified, verifiedUsers] = await Promise.all([
        fetchIdentityPermissionRoles(code, actorKey),
        canViewUsers ? fetchIdentityPermissionUsers(code, actorKey) : Promise.resolve(null),
        permissions.refetch(),
      ]);
      assertCurrentSession("Roles.View");
      queryClient.setQueryData(permissionQueryKeys.permissionRoles(actorKey, code), verified);
      setAssignment(createRoleAssignmentDraft(verified.filter((role) => !isLockedRole(role)).map((role) => role.roleGUID)));
      if (verifiedUsers) {
        queryClient.setQueryData(permissionQueryKeys.permissionUsers(actorKey, code), verifiedUsers);
        setUserAssignment(createRoleAssignmentDraft(verifiedUsers.map((user) => user.userGUID)));
      }
      setWriteForbidden(false);
      setAssignNotice("");
      setUserAssignNotice("");
      setNotice(copy.verificationComplete);
    } catch {
      if (canApplyMutationResult("Roles.View")) setNotice(copy.verificationFailed);
    }
  }, [actorKey, assertCurrentSession, canApplyMutationResult, canViewUsers, code, copy.verificationComplete, copy.verificationFailed, isLockedRole, permissions, queryClient]);

  const saving = assignMutation.isPending || deleteMutation.isPending || userAssignMutation.isPending;
  const canWrite = capabilities.canManage && !writeForbidden && !saving;
  const delta = assignment ? getRoleAssignmentDelta(assignment) : { added: [], removed: [] };
  const assignmentDirty = Boolean(assignment && isRoleAssignmentDirty(assignment));
  const userDelta = userAssignment ? getRoleAssignmentDelta(userAssignment) : { added: [], removed: [] };
  const userAssignmentDirty = Boolean(userAssignment && isRoleAssignmentDirty(userAssignment));
  const resetUserAssignment = () => setUserAssignment(createRoleAssignmentDraft(assignedUserGuids));

  const sheetUsers = useMemo<PermissionUserLite[]>(() => {
    const keyword = userSearch.trim().toLocaleLowerCase();
    if (onlySelectedUsers) {
      // 已选名单可能跨多次搜索，改为本地过滤；未见过详情的 GUID 以 GUID 兜底展示，保证仍可取消勾选。
      return (userAssignment?.selected ?? [])
        .map((guid) => seenUsers.get(guid) ?? { userGUID: guid, username: guid, email: "", isActive: true })
        .filter((user) => matchesPermissionUser(user, keyword))
        .sort((a, b) => formatPermissionUserName(a).localeCompare(formatPermissionUserName(b)));
    }
    const unique = new Map<string, PermissionUserLite>();
    for (const user of userCandidatesQuery.data?.pages.flatMap((page) => page.items) ?? []) {
      if (!unique.has(user.userGUID)) unique.set(user.userGUID, toPermissionUserLite(user));
    }
    return Array.from(unique.values());
  }, [onlySelectedUsers, seenUsers, userAssignment?.selected, userCandidatesQuery.data, userSearch]);

  const localizedRoleName = useCallback((role: Pick<IdentityRole, "roleName">) => localizeAccessRoleName(role.roleName, appLanguage), [appLanguage]);
  const sheetRoles = useMemo(() => buildAssignRoleRows(allRolesQuery.data ?? [], {
    keyword: assignSearch,
    onlySelected,
    selectedGuids: assignment?.selected ?? [],
    isLocked: isLockedRole,
    localizedName: localizedRoleName,
  }), [allRolesQuery.data, assignSearch, assignment?.selected, isLockedRole, localizedRoleName, onlySelected]);
  const assignedCounts = useMemo(
    () => countAssignedRoles(allRolesQuery.data ?? [], assignment?.selected ?? [], isLockedRole),
    [allRolesQuery.data, assignment?.selected, isLockedRole],
  );

  const handleBack = useCallback(() => backToHubTab(router, "permissions"), [router]);
  const confirmDelete = () => {
    if (!item) return;
    Alert.alert(copy.deleteTitle, interpolate(copy.deleteMessage, { name: item.name }), [
      { text: copy.cancel, style: "cancel" },
      { text: copy.delete, style: "destructive", onPress: () => deleteMutation.mutate() },
    ]);
  };
  /** 关闭角色分配弹层：有未保存勾选时先确认，放弃则恢复为服务端状态。 */
  const closeAssignSheet = () => {
    if (assignmentDirty) {
      Alert.alert(copy.unsavedTitle, copy.unsavedMessage, [
        { text: copy.cancel, style: "cancel" },
        { text: copy.discard, style: "destructive", onPress: () => { setAssignment(createRoleAssignmentDraft(explicitGuids)); setAssignNotice(""); setAssignOpen(false); } },
      ]);
      return;
    }
    setAssignNotice("");
    setAssignOpen(false);
  };
  const closeUserAssignSheet = () => {
    if (userAssignmentDirty) {
      Alert.alert(copy.unsavedTitle, copy.unsavedMessage, [
        { text: copy.cancel, style: "cancel" },
        { text: copy.discard, style: "destructive", onPress: () => { resetUserAssignment(); setUserAssignNotice(""); setUserAssignOpen(false); } },
      ]);
      return;
    }
    setUserAssignNotice("");
    setUserAssignOpen(false);
  };

  if (!allowed) {
    return <AdminDetailShell title={copy.detailTitle} onBack={handleBack}><View style={styles.message}><Text>{copy.accessDenied}</Text></View></AdminDetailShell>;
  }
  if (permissions.isPending) return <AdminDetailShell title={copy.detailTitle} onBack={handleBack}><ActivityIndicator style={styles.loader} /></AdminDetailShell>;
  if (permissions.error) {
    return <AdminDetailShell title={copy.detailTitle} onBack={handleBack}><AdminError error={permissions.error} onRetry={() => void permissions.refetch()} /></AdminDetailShell>;
  }
  if (!item) return <AdminDetailShell title={copy.detailTitle} onBack={handleBack}><AdminEmpty text={copy.notFound} /></AdminDetailShell>;

  const deleteHint = item.isSystem ? copy.deleteSystemHint : !item.deletable ? copy.deleteUnsavedHint : "";
  const roleTotal = explicitRoles.length + implicitRoles.length;
  const footer = capabilities.canManage ? (
    <StickyActionBar>
      <Button
        mode="contained"
        style={styles.footerButton}
        disabled={!canWrite || assignedQuery.isPending || allRolesQuery.isPending}
        onPress={() => { setAssignSearch(""); setOnlySelected(false); setAssignNotice(""); setAssignOpen(true); }}
      >
        {copy.assignRoles}
      </Button>
      <Button
        mode="contained"
        style={styles.footerButton}
        disabled={!canWrite || !canViewUsers || assignedUsersQuery.isPending || Boolean(assignedUsersQuery.error)}
        onPress={() => { setUserSearch(""); setOnlySelectedUsers(false); setUserAssignNotice(""); setUserAssignOpen(true); }}
      >
        {copy.assignUsers}
      </Button>
    </StickyActionBar>
  ) : undefined;

  return (
    <AdminDetailShell
      title={item.name}
      subtitle={item.code}
      subtitleMono
      onBack={handleBack}
      action={writeForbidden ? <IconButton icon="refresh" accessibilityLabel={copy.retry} onPress={() => void handleVerifyRefresh()} /> : undefined}
      footer={footer}
    >
      <View style={styles.page}>
        <ScrollView contentContainerStyle={styles.content}>
          <View style={styles.tags}>
            <PeopleTag label={item.categoryName} tone="blue" />
            <PeopleTag label={item.isSystem ? copy.systemPermission : copy.customPermission} tone={item.isSystem ? "gray" : "blue"} icon={item.isSystem ? "lock-outline" : undefined} />
          </View>
          <InfoCard style={styles.descriptionCard}>
            <Text style={styles.label}>{copy.description}</Text>
            <Text style={item.description ? styles.body : styles.muted}>{item.description || copy.noDescription}</Text>
            {item.createdAt || item.updatedAt ? (
              <View style={styles.metaBlock}>
                {item.createdAt ? <Text style={styles.muted}>{`${copy.createdAt}${labelColon()}${identityDate(item.createdAt)}${item.createdBy ? ` · ${item.createdBy}` : ""}`}</Text> : null}
                {item.updatedAt ? <Text style={styles.muted}>{`${copy.updatedAt}${labelColon()}${identityDate(item.updatedAt)}${item.updatedBy ? ` · ${item.updatedBy}` : ""}`}</Text> : null}
              </View>
            ) : null}
          </InfoCard>

          <View style={styles.section}>
            <SectionTitle title={interpolate(copy.assignedRolesCount, { count: roleTotal })} />
            {assignedQuery.isPending || allRolesQuery.isPending ? <ActivityIndicator style={styles.sectionLoader} /> : assignedQuery.error || allRolesQuery.error ? (
              <AdminError error={assignedQuery.error ?? allRolesQuery.error} onRetry={() => { void assignedQuery.refetch(); void allRolesQuery.refetch(); }} />
            ) : roleTotal === 0 ? <AdminEmpty text={copy.noAssignedRoles} /> : (
              <InfoCard>
                {implicitRoles.map((role, index) => (
                  <ActionRow
                    key={role.roleGUID}
                    first={index === 0}
                    icon="shield-crown-outline"
                    tone="violet"
                    title={localizedRoleName(role)}
                    subtitle={interpolate(copy.membersCount, { count: role.userCount })}
                    right={<PeopleTag label={copy.implicitRole} tone="violet" icon="lock-outline" />}
                    onPress={() => router.push({ pathname: "/(shell)/roles/[roleGuid]", params: { roleGuid: role.roleGUID } })}
                  />
                ))}
                {explicitRoles.map((role, index) => (
                  <ActionRow
                    key={role.roleGUID}
                    first={implicitRoles.length === 0 && index === 0}
                    icon="shield-account-outline"
                    tone="blue"
                    title={localizedRoleName(role)}
                    subtitle={interpolate(copy.membersCount, { count: role.userCount })}
                    right={role.isActive ? undefined : <PeopleTag label={copy.disabled} tone="gray" />}
                    onPress={() => router.push({ pathname: "/(shell)/roles/[roleGuid]", params: { roleGuid: role.roleGUID } })}
                  />
                ))}
              </InfoCard>
            )}
          </View>

          {canViewUsers ? (
            <View style={styles.section}>
              <SectionTitle title={interpolate(copy.assignedUsersCount, { count: assignedUsers.length })} />
              <Text style={styles.muted}>{copy.assignedUsersHint}</Text>
              {assignedUsersQuery.isPending ? <ActivityIndicator style={styles.sectionLoader} /> : assignedUsersQuery.error ? (
                <AdminError error={assignedUsersQuery.error} onRetry={() => void assignedUsersQuery.refetch()} />
              ) : assignedUsers.length === 0 ? <AdminEmpty text={copy.noAssignedUsers} /> : (
                <InfoCard>
                  {assignedUsers.map((user, index) => (
                    <ActionRow
                      key={user.userGUID}
                      first={index === 0}
                      icon="account-outline"
                      tone="blue"
                      title={formatPermissionUserName(user)}
                      subtitle={[user.email, interpolate(copy.grantedAt, { time: identityDate(user.assignedAt) })].filter(Boolean).join(" · ")}
                      right={user.isActive ? undefined : <PeopleTag label={copy.disabled} tone="gray" />}
                      onPress={() => router.push({
                        pathname: "/users/[userGuid]",
                        params: { userGuid: user.userGUID, username: user.username },
                      } as unknown as Parameters<typeof router.push>[0])}
                    />
                  ))}
                </InfoCard>
              )}
            </View>
          ) : null}

          {capabilities.canManage ? (
            <View style={styles.section}>
              <InfoCard>
                <ActionRow
                  first
                  icon="delete-outline"
                  tone="red"
                  title={copy.deletePermission}
                  subtitle={deleteHint || undefined}
                  chevron={false}
                  disabled={!canWrite || !item.deletable || deleteMutation.isPending}
                  onPress={confirmDelete}
                />
              </InfoCard>
            </View>
          ) : null}
        </ScrollView>

        {assignOpen ? (
          <AssignRolesSheet
            p={p}
            copy={copy}
            permissionName={item.name}
            permissionCode={item.code}
            roles={sheetRoles}
            counts={assignedCounts}
            selectedGuids={assignment?.selected ?? []}
            isLocked={isLockedRole}
            localizedName={localizedRoleName}
            canWrite={canWrite}
            search={assignSearch}
            onSearch={setAssignSearch}
            onlySelected={onlySelected}
            onToggleOnlySelected={() => setOnlySelected((value) => !value)}
            onToggle={(roleGuid) => setAssignment((current) => current ? toggleRoleAssignment(current, roleGuid) : current)}
            delta={{ added: delta.added.length, removed: delta.removed.length }}
            saving={assignMutation.isPending}
            notice={assignNotice}
            onDismiss={closeAssignSheet}
            onCancel={closeAssignSheet}
            onSave={() => { setAssignNotice(""); assignMutation.mutate(); }}
          />
        ) : null}
        {userAssignOpen ? (
          <AssignUsersSheet
            p={p}
            copy={copy}
            permissionName={item.name}
            permissionCode={item.code}
            users={sheetUsers}
            selectedGuids={userAssignment?.selected ?? []}
            selectedCount={userAssignment?.selected.length ?? 0}
            canWrite={canWrite}
            search={userSearch}
            onSearch={setUserSearch}
            onlySelected={onlySelectedUsers}
            onToggleOnlySelected={() => setOnlySelectedUsers((value) => !value)}
            onToggle={(userGuid) => setUserAssignment((current) => current ? toggleRoleAssignment(current, userGuid) : current)}
            loading={!onlySelectedUsers && userCandidatesQuery.isPending}
            error={!onlySelectedUsers ? userCandidatesQuery.error : null}
            onRetry={() => void userCandidatesQuery.refetch()}
            hasMore={!onlySelectedUsers && Boolean(userCandidatesQuery.hasNextPage)}
            loadingMore={userCandidatesQuery.isFetchingNextPage}
            onLoadMore={() => void userCandidatesQuery.fetchNextPage()}
            delta={{ added: userDelta.added.length, removed: userDelta.removed.length }}
            saving={userAssignMutation.isPending}
            notice={userAssignNotice}
            onDismiss={closeUserAssignSheet}
            onCancel={closeUserAssignSheet}
            onSave={() => { setUserAssignNotice(""); userAssignMutation.mutate(); }}
          />
        ) : null}
        <Snackbar visible={Boolean(notice)} onDismiss={() => setNotice("")} duration={5000}>{notice}</Snackbar>
      </View>
    </AdminDetailShell>
  );
}

const styles = StyleSheet.create({
  page: { flex: 1 },
  message: { backgroundColor: C.surfaceMuted, borderRadius: 8, margin: 16, padding: 16 },
  loader: { marginTop: 32 },
  content: { gap: 14, padding: 16, paddingBottom: 24 },
  tags: { flexDirection: "row", flexWrap: "wrap", gap: 6 },
  descriptionCard: { gap: 6, padding: 14 },
  label: { color: C.textSecondary, fontSize: 13 },
  body: { color: C.textPrimary, fontSize: 14, lineHeight: 21 },
  muted: { color: C.textSecondary, fontSize: 12.5, lineHeight: 18 },
  metaBlock: { borderTopColor: C.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth, gap: 2, marginTop: 6, paddingTop: 8 },
  section: { gap: 6 },
  sectionLoader: { marginVertical: 16 },
  footerButton: { borderRadius: 8, flex: 1 },
});

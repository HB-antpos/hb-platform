import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Alert, StyleSheet, View } from "react-native";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { usePreventRemove, type NavigationAction } from "@react-navigation/native";
import { useLocalSearchParams, useNavigation, useRouter } from "expo-router";
import { ActivityIndicator, Button, IconButton, Snackbar, Text } from "react-native-paper";
import en from "@/locales/en/identityRoles.json";
import zh from "@/locales/zh/identityRoles.json";
import { NoticeBox, SegmentTabs, StickyActionBar, resolvePersonName } from "@/modules/people";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS as C } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { localizeAccessPermission, localizeAccessPermissionCategory } from "@/modules/users/access-permission-presentation";
import {
  addIdentityRoleUsers,
  createIdentityRole,
  fetchIdentityPermissionCatalog,
  fetchIdentityRoleDetail,
  fetchIdentityRolePermissionState,
  fetchIdentityRoleUsers,
  fetchIdentityUsers,
  removeIdentityRoleUser,
  saveIdentityRolePermissions,
  updateIdentityRole,
} from "./api";
import { AdminDetailShell } from "./admin-detail-shell";
import { fillCopy, usePeopleRolesCopy } from "./people-roles-copy";
import {
  backToHubTab,
  buildPermissionView,
  buildRoleSaveReport,
  canEditRoleMembers,
  canEditRoleName,
  classifyRole,
  classifyRoleWriteFailure,
  countProfileChanges,
  formatChangeBadge,
  getPermissionDelta,
  resolveFailureReasonKey,
  resolveSaveBarState,
  runRoleSaveSequence,
  summarizeRoleChanges,
  type RoleSaveReport,
  type RoleSaveStep,
  type RoleWriteFailure,
} from "./role-detail-logic";
import { RoleAddMembersSheet, RoleMembersSection } from "./role-detail-members";
import { RoleMenusSection, RolePermissionsSection, RoleProfileSection, type MenuFilter, type MenuPlatform } from "./role-detail-sections";
import type { IdentityRoleDetail, IdentityRoleUser } from "./types";
import {
  acceptSavedPermissionDraft,
  applyMenuPermissionChange,
  arePermissionCodesEqual,
  buildRoleMenuPreview,
  isImplicitAllRole,
  isPermissionDraftDirty,
  isRoleDraftVerified,
  reconcilePermissionDraft,
  resolveRolePreviewCodes,
  getRoleCapabilities,
  togglePermission,
  togglePermissionCategory,
  toRoleMutationInput,
  validateRoleDraft,
  type PermissionDraft,
  type RoleDraft,
  type RoleMenuPreviewItem,
} from "./role-logic";
import { getRoleMenuDefinitions } from "./role-menu-catalog";
import { AdminError } from "./ui";
import { useIdentitySession } from "./user-hooks";

type DetailTab = "profile" | "permissions" | "members" | "menus";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value;
}

function initialDraft(role?: IdentityRoleDetail | null): RoleDraft {
  return { roleName: role?.roleName ?? "", description: role?.description ?? "", isActive: role?.isActive ?? true };
}

async function fetchAllRoleUsers(roleGuid: string, actorGuid: string, signal?: AbortSignal) {
  const pageSize = 200;
  const users: IdentityRoleUser[] = [];
  for (let page = 1; ; page += 1) {
    const result = await fetchIdentityRoleUsers(roleGuid, { page, pageSize }, actorGuid, signal);
    users.push(...result.items);
    if (page >= result.totalPages) return users;
  }
}

export default function RoleDetailScreen() {
  const params = useLocalSearchParams<{ roleGuid?: string | string[] }>();
  const roleGuid = firstParam(params.roleGuid)?.trim() ?? "";
  const { actorKey } = useIdentitySession("Roles.View");
  // 账号或角色变化时强制重建详情状态，避免脏草稿跨身份或跨角色残留。
  return <RoleDetailScreenContent key={`${actorKey}:${roleGuid}`} roleGuid={roleGuid} />;
}

function RoleDetailScreenContent({ roleGuid }: { roleGuid: string }) {
  const router = useRouter();
  const navigation = useNavigation();
  const queryClient = useQueryClient();
  const isCreate = roleGuid === "new";
  const { language } = useAppTranslation();
  const copy = language.startsWith("zh") ? zh : en;
  const p = usePeopleRolesCopy();
  const appLanguage = language.startsWith("zh") ? "zh" : "en";
  const { access, allowed, actorKey, isAuthenticated } = useIdentitySession("Roles.View");
  const capabilities = useMemo(() => getRoleCapabilities({
    isDeviceMode: !allowed,
    isAdmin: access.isAdmin,
    hasPermission: access.hasPermission,
  }), [access.hasPermission, access.isAdmin, allowed]);
  const [tab, setTab] = useState<DetailTab>("profile");
  const [draft, setDraft] = useState<RoleDraft>(() => initialDraft());
  const [baseline, setBaseline] = useState<RoleDraft>(() => initialDraft());
  const [permissionDraft, setPermissionDraft] = useState<PermissionDraft | null>(null);
  const [permissionSearch, setPermissionSearch] = useState("");
  const [onlySelectedPermissions, setOnlySelectedPermissions] = useState(false);
  const [memberSearch, setMemberSearch] = useState("");
  const [addSearch, setAddSearch] = useState("");
  const [addOpen, setAddOpen] = useState(false);
  const [addNotice, setAddNotice] = useState("");
  const [selectedUsers, setSelectedUsers] = useState<string[]>([]);
  const [menuPlatform, setMenuPlatform] = useState<MenuPlatform>("web");
  const [menuFilter, setMenuFilter] = useState<MenuFilter>("all");
  const [writeForbidden, setWriteForbidden] = useState(false);
  const [allowRemove, setAllowRemove] = useState(false);
  const [notice, setNotice] = useState("");
  // 吸底保存的结论（哪些已保存、哪些没保存）；失败时常驻在页面上，直到下次保存或刷新核对。
  const [saveReport, setSaveReport] = useState<RoleSaveReport | null>(null);
  const [sequenceRunning, setSequenceRunning] = useState(false);
  const pendingActionRef = useRef<NavigationAction | null>(null);
  const createdRoleGuidRef = useRef<string | null>(null);
  const createPostAttemptedRef = useRef(false);
  const initializedRoleRef = useRef<string | null>(null);

  const canOpen = allowed && (!isCreate || capabilities.canCreate);
  const roleQuery = useQuery({
    queryKey: ["identity-admin", actorKey, "role", roleGuid],
    enabled: canOpen && !isCreate && Boolean(roleGuid),
    retry: false,
    queryFn: ({ signal }) => fetchIdentityRoleDetail(roleGuid, actorKey, signal),
  });
  const catalogQuery = useQuery({
    queryKey: ["identity-admin", actorKey, "permissionCatalog"],
    enabled: canOpen && !isCreate && (tab === "permissions" || tab === "menus"),
    retry: false,
    queryFn: ({ signal }) => fetchIdentityPermissionCatalog(actorKey, signal),
  });
  const permissionQuery = useQuery({
    queryKey: ["identity-admin", actorKey, "rolePermissionState", roleGuid],
    enabled: canOpen && !isCreate && Boolean(roleGuid) && (tab === "permissions" || tab === "menus"),
    retry: false,
    queryFn: ({ signal }) => fetchIdentityRolePermissionState(roleGuid, actorKey, signal),
  });
  const membersQuery = useInfiniteQuery({
    queryKey: ["identity-admin", actorKey, "roleMembers", roleGuid, memberSearch.trim()],
    enabled: canOpen && !isCreate && tab === "members",
    retry: false,
    initialPageParam: 1,
    queryFn: ({ pageParam, signal }) => fetchIdentityRoleUsers(roleGuid, { page: pageParam, pageSize: 100, searchKeyword: memberSearch.trim() || undefined }, actorKey, signal),
    getNextPageParam: (lastPage) => lastPage.page < lastPage.totalPages ? lastPage.page + 1 : undefined,
  });

  const roleKind = useMemo(() => classifyRole(roleQuery.data?.roleName ?? ""), [roleQuery.data?.roleName]);
  const membersEditable = canEditRoleMembers(roleKind);

  const allMemberIdsQuery = useQuery({
    queryKey: ["identity-admin", actorKey, "roleAllMemberIds", roleGuid],
    enabled: canOpen && !isCreate && addOpen && capabilities.canManageUsers && membersEditable && !writeForbidden,
    retry: false,
    queryFn: ({ signal }) => fetchAllRoleUsers(roleGuid, actorKey, signal),
  });
  const availableUsersQuery = useInfiniteQuery({
    queryKey: ["identity-admin", actorKey, "roleAvailableUsers", roleGuid, addSearch.trim()],
    enabled: canOpen && !isCreate && addOpen && capabilities.canManageUsers && membersEditable && !writeForbidden && allMemberIdsQuery.isSuccess,
    retry: false,
    initialPageParam: 1,
    queryFn: async ({ pageParam, signal }) => {
      const page = await fetchIdentityUsers({ page: pageParam, pageSize: 100, search: addSearch.trim() || undefined }, actorKey, signal);
      const assigned = new Set((allMemberIdsQuery.data ?? []).map((member) => member.userGUID));
      return { ...page, items: page.items.filter((user) => !assigned.has(user.userGUID)) };
    },
    getNextPageParam: (lastPage) => lastPage.page < lastPage.totalPages ? lastPage.page + 1 : undefined,
  });

  const profileDirty = draft.roleName.trim() !== baseline.roleName.trim()
    || draft.description.trim() !== baseline.description.trim()
    || draft.isActive !== baseline.isActive;
  const permissionsDirty = Boolean(permissionDraft && isPermissionDraftDirty(permissionDraft));
  const dirty = profileDirty || permissionsDirty;

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
      throw Object.assign(new Error("IDENTITY_ROLE_SESSION_CHANGED"), { status: 403 });
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

  useEffect(() => {
    if (isCreate || !roleQuery.data || initializedRoleRef.current === roleGuid) return;
    const next = initialDraft(roleQuery.data);
    setDraft(next);
    setBaseline(next);
    initializedRoleRef.current = roleGuid;
  }, [isCreate, roleGuid, roleQuery.data]);

  useEffect(() => {
    if (!permissionQuery.data) return;
    const codes = isImplicitAllRole(permissionQuery.data, catalogQuery.data?.superAdminRoleNames)
      ? permissionQuery.data.effectivePermissionCodes
      : permissionQuery.data.explicitPermissionCodes;
    setPermissionDraft((current) => reconcilePermissionDraft(current, codes));
  }, [catalogQuery.data?.superAdminRoleNames, permissionQuery.data]);

  /** 服务器拒绝或结果不明确时冻结后续写入，直到用户刷新核对（沿用原有语义）。 */
  const freezeWritesIfNeeded = useCallback((error: unknown): RoleWriteFailure => {
    const failure = classifyRoleWriteFailure(error);
    if (failure.kind !== "rejected") setWriteForbidden(true);
    return failure;
  }, []);
  const describeFailure = useCallback((failure: RoleWriteFailure) => {
    const key = resolveFailureReasonKey(failure);
    return key === "forbidden" ? copy.forbidden : key === "uncertainWrite" ? copy.uncertainWrite : key === "nameExists" ? p.nameExists : copy.saveFailed;
  }, [copy.forbidden, copy.saveFailed, copy.uncertainWrite, p.nameExists]);

  // 资料保存：创建与编辑共用。variables 是点击保存瞬间的草稿快照，避免按顺序保存时读到过期闭包。
  const profileMutation = useMutation({
    retry: false,
    mutationFn: async (input: RoleDraft) => {
      const payload = toRoleMutationInput(input);
      if (isCreate) {
        assertCurrentSession("Roles.Create");
        const createdRoleGuid = createdRoleGuidRef.current;
        if (!createdRoleGuid && createPostAttemptedRef.current) throw new Error("ROLE_CREATE_RETRY_BLOCKED");
        let guid = createdRoleGuid;
        if (!guid) {
          // 在 POST 前锁定本次创建；即使用户快速重复点击或响应不明确，也不能发送第二次创建。
          createPostAttemptedRef.current = true;
          try {
            guid = (await createIdentityRole(payload, actorKey)).roleGUID;
          } catch (error) {
            // 只有服务端明确拒绝（如角色名重复）才允许修正后重试；401/403 或结果不明确都保持锁定，防止重复创建。
            if (classifyRoleWriteFailure(error).kind === "rejected") createPostAttemptedRef.current = false;
            throw error;
          }
        }
        // POST 已返回 GUID 后只允许继续核对该角色，绝不再次创建同名角色。
        createdRoleGuidRef.current = guid;
        assertCurrentSession("Roles.Create");
        const verified = await fetchIdentityRoleDetail(guid, actorKey);
        assertCurrentSession("Roles.Create");
        if (!isRoleDraftVerified(input, verified)) throw new Error("ROLE_READBACK_MISMATCH");
        return verified;
      }
      assertCurrentSession("Roles.Edit");
      await updateIdentityRole(roleGuid, payload, actorKey);
      assertCurrentSession("Roles.Edit");
      const verified = await fetchIdentityRoleDetail(roleGuid, actorKey);
      assertCurrentSession("Roles.Edit");
      if (!isRoleDraftVerified(input, verified)) throw new Error("ROLE_READBACK_MISMATCH");
      return verified;
    },
    onSuccess: async (verified) => {
      if (!canApplyMutationResult(isCreate ? "Roles.Create" : "Roles.Edit")) return;
      queryClient.setQueryData(["identity-admin", actorKey, "role", verified.roleGUID], verified);
      await queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "roles"] });
      if (!canApplyMutationResult(isCreate ? "Roles.Create" : "Roles.Edit")) return;
      const next = initialDraft(verified);
      setDraft(next);
      setBaseline(next);
      if (isCreate) {
        setNotice(copy.created);
        createdRoleGuidRef.current = verified.roleGUID;
        setAllowRemove(true);
      }
    },
    onError: (error) => {
      const failure = freezeWritesIfNeeded(error);
      // 编辑时的结论统一由按顺序保存的汇总给出；创建是单步，直接提示。
      if (isCreate) {
        setNotice(describeFailure(failure));
        if (createdRoleGuidRef.current && canApplyMutationResult("Roles.View")) setAllowRemove(true);
      }
    },
  });
  const permissionsMutation = useMutation({
    retry: false,
    mutationFn: async (selected: string[]) => {
      assertCurrentSession("Roles.ManagePermissions");
      await saveIdentityRolePermissions(roleGuid, selected, actorKey);
      assertCurrentSession("Roles.ManagePermissions");
      const verified = await fetchIdentityRolePermissionState(roleGuid, actorKey);
      assertCurrentSession("Roles.ManagePermissions");
      if (!arePermissionCodesEqual(selected, verified.explicitPermissionCodes)) {
        throw new Error("ROLE_PERMISSION_READBACK_MISMATCH");
      }
      return verified;
    },
    onSuccess: async (verified) => {
      if (!canApplyMutationResult("Roles.ManagePermissions")) return;
      queryClient.setQueryData(["identity-admin", actorKey, "rolePermissionState", roleGuid], verified);
      const verifiedCodes = isImplicitAllRole(verified, catalogQuery.data?.superAdminRoleNames)
        ? verified.effectivePermissionCodes
        : verified.explicitPermissionCodes;
      setPermissionDraft(acceptSavedPermissionDraft({ selected: verifiedCodes, baseline: verifiedCodes }));
      await queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "role", roleGuid] });
    },
    onError: (error) => { freezeWritesIfNeeded(error); },
  });
  const addMembersMutation = useMutation({
    retry: false,
    mutationFn: async () => {
      assertCurrentSession("Roles.ManageUsers");
      await addIdentityRoleUsers(roleGuid, selectedUsers, actorKey);
      assertCurrentSession("Roles.ManageUsers");
      const [detail, allMembers] = await Promise.all([fetchIdentityRoleDetail(roleGuid, actorKey), fetchAllRoleUsers(roleGuid, actorKey)]);
      assertCurrentSession("Roles.ManageUsers");
      const memberGuids = new Set(allMembers.map((member) => member.userGUID));
      if (!selectedUsers.every((userGuid) => memberGuids.has(userGuid))) throw new Error("ROLE_MEMBER_READBACK_MISMATCH");
      return detail;
    },
    onSuccess: async (verified) => {
      if (!canApplyMutationResult("Roles.ManageUsers")) return;
      queryClient.setQueryData(["identity-admin", actorKey, "role", roleGuid], verified);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "roleMembers", roleGuid] }),
        queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "roleAllMemberIds", roleGuid] }),
        queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "roleAvailableUsers", roleGuid] }),
        queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "roles"] }),
      ]);
      if (!canApplyMutationResult("Roles.ManageUsers")) return;
      setSelectedUsers([]);
      setAddNotice("");
      setAddOpen(false);
      setNotice(copy.membersAdded);
    },
    // 添加弹层打开时 Snackbar 会被盖住，失败原因就地显示在弹层里。
    onError: (error) => { setAddNotice(describeFailure(freezeWritesIfNeeded(error))); },
  });
  const removeMemberMutation = useMutation({
    retry: false,
    mutationFn: async (userGuid: string) => {
      assertCurrentSession("Roles.ManageUsers");
      await removeIdentityRoleUser(roleGuid, userGuid, actorKey);
      assertCurrentSession("Roles.ManageUsers");
      const [detail, allMembers] = await Promise.all([fetchIdentityRoleDetail(roleGuid, actorKey), fetchAllRoleUsers(roleGuid, actorKey)]);
      assertCurrentSession("Roles.ManageUsers");
      if (allMembers.some((member) => member.userGUID === userGuid)) throw new Error("ROLE_MEMBER_READBACK_MISMATCH");
      return detail;
    },
    onSuccess: async (verified) => {
      if (!canApplyMutationResult("Roles.ManageUsers")) return;
      queryClient.setQueryData(["identity-admin", actorKey, "role", roleGuid], verified);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "roleMembers", roleGuid] }),
        queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "roleAllMemberIds", roleGuid] }),
        queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "roleAvailableUsers", roleGuid] }),
        queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "roles"] }),
      ]);
      if (!canApplyMutationResult("Roles.ManageUsers")) return;
      setNotice(copy.memberRemoved);
    },
    onError: (error) => { setNotice(describeFailure(freezeWritesIfNeeded(error))); },
  });
  const saving = sequenceRunning || profileMutation.isPending || permissionsMutation.isPending || addMembersMutation.isPending || removeMemberMutation.isPending;

  const handleVerifyRefresh = useCallback(async () => {
    if (!canApplyMutationResult("Roles.View")) return;
    if (isCreate) {
      await queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "roles"] });
      if (!canApplyMutationResult("Roles.View")) return;
      // 创建请求未返回 GUID 时无法安全判断是否已落库，因此刷新后仍保持冻结，禁止重复 POST。
      setNotice(copy.verificationFailed);
      return;
    }
    try {
      assertCurrentSession("Roles.View");
      const verified = await fetchIdentityRoleDetail(roleGuid, actorKey);
      assertCurrentSession("Roles.View");
      queryClient.setQueryData(["identity-admin", actorKey, "role", roleGuid], verified);
      const remoteDraft = initialDraft(verified);
      setBaseline(remoteDraft);
      if (isRoleDraftVerified(draft, verified)) setDraft(remoteDraft);

      if (tab === "permissions" || tab === "menus") {
        const state = await fetchIdentityRolePermissionState(roleGuid, actorKey);
        assertCurrentSession("Roles.View");
        queryClient.setQueryData(["identity-admin", actorKey, "rolePermissionState", roleGuid], state);
        const remoteCodes = isImplicitAllRole(state, catalogQuery.data?.superAdminRoleNames)
          ? state.effectivePermissionCodes
          : state.explicitPermissionCodes;
        setPermissionDraft((current) => current
          ? { selected: current.selected, baseline: remoteCodes }
          : reconcilePermissionDraft(null, remoteCodes));
      }
      if (tab === "members") {
        await fetchAllRoleUsers(roleGuid, actorKey);
        assertCurrentSession("Roles.View");
        await Promise.all([
          queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "roleMembers", roleGuid] }),
          queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "roleAllMemberIds", roleGuid] }),
        ]);
        assertCurrentSession("Roles.View");
      }
      setWriteForbidden(false);
      setSaveReport(null);
      setNotice(copy.verificationComplete);
    } catch {
      if (canApplyMutationResult("Roles.View")) setNotice(copy.verificationFailed);
    }
  }, [actorKey, assertCurrentSession, canApplyMutationResult, catalogQuery.data?.superAdminRoleNames, copy.verificationComplete, copy.verificationFailed, draft, isCreate, queryClient, roleGuid, tab]);

  usePreventRemove(isAuthenticated && (dirty || saving) && !allowRemove, ({ data }) => {
    const session = useAuthStore.getState();
    if (
      !session.isAuthenticated
      || session.sessionKind !== "account"
      || session.iosReviewOfflineGuardActive
      || (session.user?.userGUID ?? "") !== actorKey
    ) {
      // 登出或身份切换必须立即离开，不能被旧账号的脏草稿确认框拦住。
      pendingActionRef.current = data.action;
      setAllowRemove(true);
      return;
    }
    if (saving) {
      Alert.alert(copy.busyTitle, copy.busyMessage, [{ text: copy.confirm }]);
      return;
    }
    Alert.alert(copy.unsavedTitle, p.unsavedMessage, [
      { text: copy.cancel, style: "cancel" },
      { text: copy.discard, style: "destructive", onPress: () => { pendingActionRef.current = data.action; setAllowRemove(true); } },
    ]);
  });
  useEffect(() => {
    if (!allowRemove) return;
    if (pendingActionRef.current) {
      const action = pendingActionRef.current;
      pendingActionRef.current = null;
      navigation.dispatch(action);
      return;
    }
    if (isCreate && createdRoleGuidRef.current) {
      const createdRoleGuid = createdRoleGuidRef.current;
      router.replace({ pathname: "/roles/[roleGuid]", params: { roleGuid: createdRoleGuid } });
      // 同一动态路由可能复用组件，跳转完成后重新启用脏草稿保护。
      setAllowRemove(false);
    }
  }, [allowRemove, isCreate, navigation, router]);

  // ───────── 权限视图（分类计数 / 搜索 / 仅看已选） ─────────
  const presentedCategories = useMemo(() => (catalogQuery.data?.categories ?? []).map((category) => ({
    ...category,
    displayName: localizeAccessPermissionCategory(category.category || category.displayName, appLanguage),
    permissions: category.permissions.map((permission) => {
      const localized = localizeAccessPermission(permission, appLanguage);
      return { ...permission, displayName: localized.name, description: localized.description };
    }),
  })), [appLanguage, catalogQuery.data?.categories]);
  const selectedPermissionCodes = useMemo(() => permissionDraft?.selected ?? [], [permissionDraft?.selected]);
  const selectedPermissionSet = useMemo(() => new Set(selectedPermissionCodes), [selectedPermissionCodes]);
  const permissionView = useMemo(() => buildPermissionView(presentedCategories, {
    keyword: permissionSearch,
    onlySelected: onlySelectedPermissions,
    selected: selectedPermissionCodes,
  }), [onlySelectedPermissions, permissionSearch, presentedCategories, selectedPermissionCodes]);
  const handleTogglePermission = useCallback((code: string) => {
    setPermissionDraft((current) => current ? togglePermission(current, code) : current);
  }, []);
  const handleToggleGroup = useCallback((codes: string[]) => {
    setPermissionDraft((current) => current ? togglePermissionCategory(current, codes) : current);
  }, []);

  const members = useMemo(() => membersQuery.data?.pages.flatMap((page) => page.items) ?? [], [membersQuery.data?.pages]);
  const availableUsers = useMemo(() => availableUsersQuery.data?.pages.flatMap((page) => page.items) ?? [], [availableUsersQuery.data?.pages]);
  const implicitAllPermissions = Boolean(permissionQuery.data && isImplicitAllRole(
    permissionQuery.data,
    catalogQuery.data?.superAdminRoleNames,
  ));
  const actualSuperAdmin = permissionQuery.data?.isSuperAdmin ?? false;
  const assignablePermissionCodes = useMemo(
    () => catalogQuery.data?.categories.flatMap((category) => category.permissions.map((permission) => permission.name)) ?? [],
    [catalogQuery.data?.categories],
  );
  const previewPermissionCodes = useMemo(() => permissionQuery.data
    ? resolveRolePreviewCodes({
      selectedCodes: selectedPermissionCodes,
      aliases: catalogQuery.data?.permissionAliases ?? [],
    })
    : [], [catalogQuery.data?.permissionAliases, permissionQuery.data, selectedPermissionCodes]);
  const menuItems = useMemo(() => buildRoleMenuPreview(
    getRoleMenuDefinitions(language),
    previewPermissionCodes,
    { isSuperAdmin: actualSuperAdmin, implicitAllPermissions },
  ).filter((item) => item.platform === menuPlatform), [actualSuperAdmin, implicitAllPermissions, language, menuPlatform, previewPermissionCodes]);
  const handleMenuToggle = useCallback((item: RoleMenuPreviewItem, visible: boolean) => {
    setPermissionDraft((current) => current
      ? { ...current, selected: applyMenuPermissionChange(current.selected, item, visible, catalogQuery.data?.permissionAliases ?? [], assignablePermissionCodes) }
      : current);
  }, [assignablePermissionCodes, catalogQuery.data?.permissionAliases]);

  // ───────── 改动汇总与吸底保存栏 ─────────
  const profileErrors = validateRoleDraft(draft);
  const profileInvalid = Object.keys(profileErrors).length > 0;
  const permissionsLoading = catalogQuery.isPending || permissionQuery.isPending || (permissionQuery.isSuccess && !permissionDraft);
  const profileWritable = isCreate ? capabilities.canCreate : capabilities.canEdit;
  const permissionsWritable = capabilities.canManagePermissions
    && catalogQuery.isSuccess
    && permissionQuery.isSuccess
    && !implicitAllPermissions;
  const changeSummary = useMemo(() => summarizeRoleChanges({
    profileCount: isCreate ? 0 : countProfileChanges(draft, baseline),
    permission: getPermissionDelta(permissionDraft),
  }), [baseline, draft, isCreate, permissionDraft]);
  const canWriteAnything = isCreate ? capabilities.canCreate : (capabilities.canEdit || capabilities.canManagePermissions);
  const saveBar = resolveSaveBarState({
    hasChanges: isCreate ? !profileInvalid : changeSummary.hasChanges,
    saving,
    frozen: writeForbidden,
    profileInvalid: isCreate ? profileInvalid : profileInvalid && profileDirty,
    canWriteAnything,
  });

  const handleSave = async () => {
    if (!saveBar.enabled) return;
    if (isCreate) {
      profileMutation.mutate(draft);
      return;
    }
    const steps: RoleSaveStep[] = [];
    // 固定顺序：先资料、后权限；任何一步失败都停下，后面的不再提交。
    if (profileWritable && profileDirty) {
      const snapshot = draft;
      steps.push({ key: "profile", run: () => profileMutation.mutateAsync(snapshot) });
    }
    if (permissionsWritable && permissionDraft && permissionsDirty) {
      const selected = permissionDraft.selected;
      steps.push({ key: "permissions", run: () => permissionsMutation.mutateAsync(selected) });
    }
    if (steps.length === 0) return;
    setSaveReport(null);
    setSequenceRunning(true);
    try {
      const report = buildRoleSaveReport(await runRoleSaveSequence(steps));
      if (!canApplyMutationResult("Roles.View")) return;
      setSaveReport(report);
      if (report.tone === "success") setNotice(p[report.messageKey]);
    } finally {
      setSequenceRunning(false);
    }
  };

  const saveReportText = saveReport && saveReport.tone === "error" && saveReport.failure
    ? `${p[saveReport.messageKey]}\n${describeFailure(saveReport.failure)}`
    : "";

  const barHint = (() => {
    if (isCreate) return p.barCreateHint;
    if (saveBar.blocker === "saving") return p.barSaving;
    if (saveBar.blocker === "frozen") return p.barFrozen;
    if (saveBar.blocker === "invalid") return p.barInvalid;
    return [
      changeSummary.profileCount > 0 ? fillCopy(p.barHintProfile, { count: changeSummary.profileCount }) : "",
      changeSummary.permissionAdded + changeSummary.permissionRemoved > 0
        ? fillCopy(p.barHintPermissions, { badge: formatChangeBadge(changeSummary.permissionAdded, changeSummary.permissionRemoved) })
        : "",
    ].filter(Boolean).join(" · ") || undefined;
  })();
  const barSummary = isCreate
    ? p.barCreateSummary
    : changeSummary.hasChanges ? fillCopy(p.barChanges, { count: changeSummary.total }) : p.barNoChanges;
  const footer = canWriteAnything ? (
    <StickyActionBar summary={barSummary} hint={barHint}>
      <Button
        mode="contained"
        style={styles.saveButton}
        contentStyle={styles.saveButtonContent}
        disabled={!saveBar.enabled}
        loading={sequenceRunning || profileMutation.isPending || permissionsMutation.isPending}
        onPress={() => void handleSave()}
      >
        {isCreate ? copy.createRole : p.save}
      </Button>
    </StickyActionBar>
  ) : undefined;

  const handleBack = useCallback(() => backToHubTab(router, "roles"), [router]);
  const openMember = useCallback((member: IdentityRoleUser) => {
    const name = resolvePersonName({ fullName: member.fullName, givenName: member.givenName, familyName: member.familyName, nickname: member.nickname, username: member.username });
    // 管理员视角进入统一后的人员详情；参数仅作首屏预填，详情页自己按 userGuid 拉取。
    router.push({
      pathname: "/users/[userGuid]",
      params: { userGuid: member.userGUID, userName: name.primary, username: member.username },
    } as unknown as Parameters<typeof router.push>[0]);
  }, [router]);

  const shellTitle = isCreate ? p.createTitle : roleQuery.data?.roleName || copy.detailTitle;

  if (!canOpen) {
    return (
      <AdminDetailShell title={isCreate ? p.createTitle : copy.detailTitle} onBack={handleBack}>
        <View style={styles.message}><Text>{copy.accessDenied}</Text></View>
      </AdminDetailShell>
    );
  }
  if (!isCreate && roleQuery.isPending) {
    return <AdminDetailShell title={copy.detailTitle} onBack={handleBack}><ActivityIndicator style={styles.loader} /></AdminDetailShell>;
  }
  if (!isCreate && roleQuery.error) {
    return (
      <AdminDetailShell title={copy.detailTitle} onBack={handleBack}>
        <AdminError error={roleQuery.error} onRetry={() => void roleQuery.refetch()} />
      </AdminDetailShell>
    );
  }

  const permissionsReadOnly = !capabilities.canManagePermissions || writeForbidden || saving || implicitAllPermissions;
  const permissionsNotice = implicitAllPermissions ? p.implicitAllHint : !capabilities.canManagePermissions ? p.noPermissionEditHint : undefined;
  const membersManageable = capabilities.canManageUsers && membersEditable && !writeForbidden && !saving;

  return (
    <AdminDetailShell
      title={shellTitle}
      subtitle={!isCreate && roleQuery.data ? fillCopy(p.membersCount, { count: roleQuery.data.userCount }) : undefined}
      onBack={handleBack}
      action={writeForbidden ? <IconButton icon="refresh" accessibilityLabel={copy.retry} onPress={() => void handleVerifyRefresh()} /> : undefined}
      footer={footer}
    >
      <View style={styles.page}>
        {!isCreate ? (
          <View style={styles.segmentWrap}>
            <SegmentTabs<DetailTab>
              items={[
                { key: "profile", label: p.segmentProfile },
                { key: "permissions", label: p.segmentPermissions },
                { key: "members", label: p.segmentMembers },
                { key: "menus", label: p.segmentMenus },
              ]}
              value={tab}
              onChange={setTab}
            />
          </View>
        ) : null}
        {saveReportText ? (
          <View style={styles.reportWrap}>
            <NoticeBox tone="red" icon="alert-circle-outline">{saveReportText}</NoticeBox>
          </View>
        ) : null}
        <View style={styles.flex}>
          {tab === "profile" ? (
            <RoleProfileSection
              legacy={copy}
              p={p}
              draft={draft}
              onChange={setDraft}
              errors={profileErrors}
              readOnly={saving || writeForbidden || !profileWritable}
              nameLocked={!isCreate && !canEditRoleName(roleKind)}
              kind={roleKind}
              role={roleQuery.data}
            />
          ) : null}
          {tab === "permissions" ? (
            <RolePermissionsSection
              p={p}
              legacy={copy}
              view={permissionsLoading || !permissionDraft ? null : permissionView}
              loading={permissionsLoading && !(catalogQuery.error || permissionQuery.error)}
              error={catalogQuery.error ?? permissionQuery.error}
              onRetry={() => { void catalogQuery.refetch(); void permissionQuery.refetch(); }}
              selected={selectedPermissionSet}
              readOnly={permissionsReadOnly}
              readOnlyNotice={permissionsNotice}
              search={permissionSearch}
              onSearch={setPermissionSearch}
              onlySelected={onlySelectedPermissions}
              onOnlySelected={setOnlySelectedPermissions}
              onToggle={handleTogglePermission}
              onToggleGroup={handleToggleGroup}
            />
          ) : null}
          {tab === "members" ? (
            <RoleMembersSection
              legacy={copy}
              p={p}
              search={memberSearch}
              onSearch={setMemberSearch}
              members={members}
              total={membersQuery.data?.pages[0]?.total ?? 0}
              loading={membersQuery.isPending}
              loadingMore={membersQuery.isFetchingNextPage}
              hasMore={Boolean(membersQuery.hasNextPage)}
              onLoadMore={() => void membersQuery.fetchNextPage()}
              error={membersQuery.error}
              onRetry={() => void membersQuery.refetch()}
              canManage={membersManageable}
              derived={roleKind.isDerived}
              onAdd={() => { setAddSearch(""); setSelectedUsers([]); setAddNotice(""); setAddOpen(true); }}
              onRemove={(member) => {
                const name = resolvePersonName({ fullName: member.fullName, givenName: member.givenName, familyName: member.familyName, nickname: member.nickname, username: member.username }).primary;
                Alert.alert(copy.removeTitle, fillCopy(copy.removeMessage, { name }), [
                  { text: copy.cancel, style: "cancel" },
                  { text: copy.remove, style: "destructive", onPress: () => removeMemberMutation.mutate(member.userGUID) },
                ]);
              }}
              onOpen={openMember}
            />
          ) : null}
          {tab === "menus" ? (
            <RoleMenusSection
              p={p}
              legacy={copy}
              items={menuItems}
              platform={menuPlatform}
              onPlatform={setMenuPlatform}
              filter={menuFilter}
              onFilter={setMenuFilter}
              loading={catalogQuery.isPending || permissionQuery.isPending}
              error={catalogQuery.error ?? permissionQuery.error}
              onRetry={() => { void catalogQuery.refetch(); void permissionQuery.refetch(); }}
              readOnly={!capabilities.canManagePermissions || writeForbidden || saving}
              onToggle={handleMenuToggle}
            />
          ) : null}
        </View>
        {addOpen ? (
          <RoleAddMembersSheet
            legacy={copy}
            p={p}
            roleName={roleQuery.data?.roleName ?? ""}
            search={addSearch}
            onSearch={setAddSearch}
            users={availableUsers}
            loading={allMemberIdsQuery.isPending || availableUsersQuery.isPending}
            error={allMemberIdsQuery.error ?? availableUsersQuery.error}
            onRetry={() => { void allMemberIdsQuery.refetch(); void availableUsersQuery.refetch(); }}
            hasMore={Boolean(availableUsersQuery.hasNextPage)}
            loadingMore={availableUsersQuery.isFetchingNextPage}
            onLoadMore={() => void availableUsersQuery.fetchNextPage()}
            selected={selectedUsers}
            onToggle={(userGuid) => setSelectedUsers((current) => current.includes(userGuid) ? current.filter((guid) => guid !== userGuid) : [...current, userGuid])}
            onCancel={() => setAddOpen(false)}
            onSubmit={() => { setAddNotice(""); addMembersMutation.mutate(); }}
            submitting={addMembersMutation.isPending}
            notice={addNotice}
          />
        ) : null}
        <Snackbar visible={Boolean(notice)} onDismiss={() => setNotice("")} duration={5000}>{notice}</Snackbar>
      </View>
    </AdminDetailShell>
  );
}

const styles = StyleSheet.create({
  page: { flex: 1 },
  flex: { flex: 1 },
  message: { backgroundColor: C.surfaceMuted, borderRadius: 8, margin: 16, padding: 16 },
  loader: { marginTop: 32 },
  segmentWrap: { paddingHorizontal: 16, paddingTop: 12 },
  reportWrap: { paddingHorizontal: 16, paddingTop: 8 },
  saveButton: { borderRadius: 8, minWidth: 120 },
  saveButtonContent: { minHeight: 40 },
});

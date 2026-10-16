import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  useAccessRoleCatalog,
  useAccessStoreCatalog,
  useAssignUserAccessRoles,
  useAssignUserAccessStores,
  useAssignUserDirectPermissions,
  useCopyUserDirectPermissions,
  usePreviewCopyUserDirectPermissions,
  useUserAccessPermissionAccess,
  useUserAccessRoles,
  useUserAccessStores,
} from "./access-management-hooks";
import {
  applyUserAccessSectionRestrictions,
  areAccessCodeSetsEqual,
  buildDirectPermissionDraft,
  buildUserAccessStoreAssignments,
  classifyUserAccessError,
  getUserAccessEligibility,
  isStoreManagerRoleName,
  limitUserAccessRolesForActor,
  planStoreManagerGrant,
  setUserAccessStoreState,
  toggleDirectPermission,
  type StoreManagerGrantPlan,
} from "./access-management";
import {
  buildAccessChangeSummary,
  buildAccessStoreOptions,
  classifyAccessSaveOutcome,
  countAssignedStores,
  countSelectedAccessRoles,
  excludePosPermissionCategories,
  mergeAccessRoleOptions,
  planAccessSaveSteps,
  runAccessSaveSteps,
  summarizeAccessPermissionCounts,
  type AccessSaveOutcome,
  type AccessSaveStep,
  type AccessSaveStepKey,
  type AccessSaveVerdict,
  type AccessTarget,
} from "./access-page-logic";
import { createPermissionNameResolver } from "./access-permission-copy";
import {
  localizeAccessPermission,
  localizeAccessRoleName,
} from "./access-permission-presentation";
import { togglePermissionCodes } from "@/modules/identity-admin/user-authorization-logic";
import type {
  AccessStoreOption,
  CopyUserDirectPermissionsInput,
  DirectPermissionDraft,
  UserAccessErrorKind,
  UserAccessStoreAssignment,
  UserAccessStoreState,
} from "./access-management-types";
import type { AppLanguage } from "@/shared/i18n/types";
import { PERMISSIONS } from "@/shared/utils/access";
import { useAuthStore } from "@/store/auth-store";

function encodeStoreAssignments(assignments: UserAccessStoreAssignment[]) {
  return assignments.map(
    (item) => `${item.storeGUID}:${item.isPrimary ? "manage" : "view"}`,
  );
}

export interface AccessSaveReport {
  verdict: AccessSaveVerdict;
  outcome: AccessSaveOutcome;
  /** 失败步骤的错误分类（用于决定是否还能就地重试）；没有失败时为 null。 */
  failedKind: UserAccessErrorKind | null;
}

/**
 * 统一授权页的数据与草稿：目标用户 → 资格 → 分店 / 角色 / 直接权限三类查询与草稿 → 一次保存。
 * 页面只管渲染；这里不做任何 UI 提示，保存结果以 AccessSaveReport 交回由页面展示。
 */
export function useAccessEditor({
  userGuid,
  target,
  language,
}: {
  userGuid: string;
  target: AccessTarget;
  language: AppLanguage;
}) {
  const currentUser = useAuthStore((state) => state.user);
  const access = useAuthStore((state) => state.access);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const isDeviceMode = sessionKind === "device";
  const actorUserGuid = currentUser?.userGUID || currentUser?.userGuid || "";
  // 只有账号会话才把操作者绑进请求；iOS 审核会话带这个绑定会被拦截器拒绝。
  const boundActorKey = sessionKind === "account" ? actorUserGuid : "";

  const managedStoreCodes = useMemo(
    () =>
      access.isAdmin
        ? null
        : (currentUser?.stores ?? [])
            .filter((store) => store.isPrimary === true)
            .map((store) => store.storeCode)
            .filter(Boolean),
    [access.isAdmin, currentUser?.stores],
  );
  const managedStoreCodeSet = useMemo(
    () => new Set(managedStoreCodes?.map((code) => code.toLowerCase()) ?? []),
    [managedStoreCodes],
  );
  const hasManageableStores = access.isAdmin || Boolean(managedStoreCodes?.length);
  const canManageStores = access.hasPermission(PERMISSIONS.Users.ManageStores);
  const canManageRoles = access.hasPermission(PERMISSIONS.Users.ManageRoles);
  const canManagePos = access.hasPermission(PERMISSIONS.Users.ManagePos);

  const [terminalErrorKind, setTerminalErrorKind] = useState<Exclude<
    UserAccessErrorKind,
    "network"
  > | null>(null);
  const [allowRemove, setAllowRemove] = useState(false);
  const [authRedirectRequested, setAuthRedirectRequested] = useState(false);
  const [roleMutationForbidden, setRoleMutationForbidden] = useState(false);
  const [permissionMutationForbidden, setPermissionMutationForbidden] = useState(false);
  const operationInFlightRef = useRef(false);

  // 资格以「当前最可信的目标」计算：服务端数据到位前用路由预填，到位后以服务端为准。
  const routeEligibility = useMemo(
    () =>
      getUserAccessEligibility({
        isDeviceMode,
        isAdmin: access.isAdmin,
        isStoreManager: access.isStoreManager,
        canManageStores,
        canManageRoles,
        canManagePos,
        currentUserGuid: currentUser?.userGUID,
        targetUserGuid: userGuid,
        targetStatus: target.status,
        targetRoleNames: target.roleNames,
        hasManageableStores,
      }),
    [
      access.isAdmin,
      access.isStoreManager,
      canManagePos,
      canManageRoles,
      canManageStores,
      currentUser?.userGUID,
      hasManageableStores,
      isDeviceMode,
      target.roleNames,
      target.status,
      userGuid,
    ],
  );
  const queryEnabled = Boolean(
    userGuid && isAuthenticated && routeEligibility.canOpen && !terminalErrorKind,
  );
  const storesEnabled = queryEnabled && routeEligibility.storesMode !== "hidden";
  // 受限操作者直接使用登录态中的可管理分店，避免 POS-only 账号被全分店目录接口拒绝。
  const storeCatalogEnabled = storesEnabled && access.isAdmin;
  const rolesEnabled = queryEnabled && routeEligibility.rolesMode !== "hidden";

  const storesQuery = useUserAccessStores(userGuid, storesEnabled, boundActorKey);
  const storeCatalogQuery = useAccessStoreCatalog(storeCatalogEnabled, boundActorKey);
  const rolesQuery = useUserAccessRoles(userGuid, rolesEnabled, boundActorKey);
  const roleCatalogQuery = useAccessRoleCatalog(rolesEnabled, boundActorKey);
  const targetRoleNames = useMemo(
    () => rolesQuery.data?.map((role) => role.roleName) ?? target.roleNames,
    [rolesQuery.data, target.roleNames],
  );
  const baseEligibility = useMemo(
    () =>
      getUserAccessEligibility({
        isDeviceMode,
        isAdmin: access.isAdmin,
        isStoreManager: access.isStoreManager,
        canManageStores,
        canManageRoles,
        canManagePos,
        currentUserGuid: currentUser?.userGUID,
        targetUserGuid: userGuid,
        targetStatus: target.status,
        targetRoleNames,
        hasManageableStores,
      }),
    [
      access.isAdmin,
      access.isStoreManager,
      canManagePos,
      canManageRoles,
      canManageStores,
      currentUser?.userGUID,
      hasManageableStores,
      isDeviceMode,
      target.status,
      targetRoleNames,
      userGuid,
    ],
  );
  const roleAccessForbidden = Boolean(
    !access.isAdmin &&
      (roleMutationForbidden ||
        [rolesQuery.error, roleCatalogQuery.error].some(
          (error) => error && classifyUserAccessError(error) === "forbidden",
        )),
  );
  const permissionsEnabled = Boolean(
    queryEnabled && !roleAccessForbidden && baseEligibility.permissionsMode !== "hidden",
  );
  const permissionAccessQuery = useUserAccessPermissionAccess(
    userGuid,
    permissionsEnabled,
    boundActorKey,
  );
  const permissionAccessForbidden = Boolean(
    !access.isAdmin &&
      (permissionMutationForbidden ||
        (permissionAccessQuery.error &&
          classifyUserAccessError(permissionAccessQuery.error) === "forbidden")),
  );
  const eligibility = useMemo(
    () =>
      applyUserAccessSectionRestrictions(baseEligibility, {
        rolesForbidden: roleAccessForbidden,
        permissionsForbidden: permissionAccessForbidden,
      }),
    [baseEligibility, permissionAccessForbidden, roleAccessForbidden],
  );

  const assignStoresMutation = useAssignUserAccessStores(boundActorKey);
  const assignRolesMutation = useAssignUserAccessRoles(boundActorKey);
  const assignPermissionsMutation = useAssignUserDirectPermissions(boundActorKey);
  const previewCopyMutation = usePreviewCopyUserDirectPermissions(boundActorKey);
  const copyPermissionsMutation = useCopyUserDirectPermissions(boundActorKey);

  const [storeAssignments, setStoreAssignments] = useState<UserAccessStoreAssignment[]>([]);
  const [baselineStoreAssignments, setBaselineStoreAssignments] = useState<
    UserAccessStoreAssignment[]
  >([]);
  const [selectedRoleGuids, setSelectedRoleGuids] = useState<string[]>([]);
  const [baselineRoleGuids, setBaselineRoleGuids] = useState<string[]>([]);
  const [permissionDraft, setPermissionDraft] = useState<DirectPermissionDraft | null>(null);
  const storeAppliedAtRef = useRef(0);
  const roleAppliedAtRef = useRef(0);
  const permissionAppliedAtRef = useRef(0);
  const initializedScopeRef = useRef<string | null>(null);
  const busy =
    assignStoresMutation.isPending ||
    assignRolesMutation.isPending ||
    assignPermissionsMutation.isPending ||
    previewCopyMutation.isPending ||
    copyPermissionsMutation.isPending;
  const storesDirty = !areAccessCodeSetsEqual(
    encodeStoreAssignments(storeAssignments),
    encodeStoreAssignments(baselineStoreAssignments),
  );
  const rolesDirty = !areAccessCodeSetsEqual(selectedRoleGuids, baselineRoleGuids);
  const permissionsDirty = Boolean(
    permissionDraft &&
      !areAccessCodeSetsEqual(permissionDraft.selectedCodes, permissionDraft.baselineCodes),
  );
  const dirty = storesDirty || rolesDirty || permissionsDirty;

  useEffect(() => {
    if (initializedScopeRef.current === userGuid) return;
    initializedScopeRef.current = userGuid;
    storeAppliedAtRef.current = 0;
    roleAppliedAtRef.current = 0;
    permissionAppliedAtRef.current = 0;
    setStoreAssignments([]);
    setBaselineStoreAssignments([]);
    setSelectedRoleGuids([]);
    setBaselineRoleGuids([]);
    setPermissionDraft(null);
    setTerminalErrorKind(null);
    setRoleMutationForbidden(false);
    setPermissionMutationForbidden(false);
    setAllowRemove(false);
  }, [userGuid]);

  useEffect(() => {
    if (!storesQuery.data) return;
    if (
      storeAppliedAtRef.current > 0 &&
      (storesDirty || busy || storesQuery.dataUpdatedAt <= storeAppliedAtRef.current)
    )
      return;
    const assignments = buildUserAccessStoreAssignments(storesQuery.data);
    setStoreAssignments(assignments);
    setBaselineStoreAssignments(assignments);
    storeAppliedAtRef.current = storesQuery.dataUpdatedAt;
  }, [busy, storesDirty, storesQuery.data, storesQuery.dataUpdatedAt]);

  useEffect(() => {
    if (!rolesQuery.data) return;
    if (
      roleAppliedAtRef.current > 0 &&
      (rolesDirty || busy || rolesQuery.dataUpdatedAt <= roleAppliedAtRef.current)
    )
      return;
    const roleGuids = rolesQuery.data
      .filter((role) => !isStoreManagerRoleName(role.roleName))
      .map((role) => role.roleGUID);
    setSelectedRoleGuids(roleGuids);
    setBaselineRoleGuids(roleGuids);
    roleAppliedAtRef.current = rolesQuery.dataUpdatedAt;
  }, [busy, rolesDirty, rolesQuery.data, rolesQuery.dataUpdatedAt]);

  useEffect(() => {
    const data = permissionAccessQuery.data;
    if (!data) return;
    const updatedAt = permissionAccessQuery.dataUpdatedAt;
    if (permissionAppliedAtRef.current > 0 && updatedAt <= permissionAppliedAtRef.current) return;
    if (permissionAppliedAtRef.current > 0 && (permissionsDirty || busy)) {
      // 有未保存的直接权限时不能整体覆盖草稿；但分店 / 角色保存后继承来源会变，
      // 只刷新「继承」部分（继承来的权限在授权页里是锁定的，必须是最新的）。
      const inherited = buildDirectPermissionDraft(data.state).inheritedPermissionCodes;
      setPermissionDraft((current) =>
        current ? { ...current, inheritedPermissionCodes: inherited } : current,
      );
      permissionAppliedAtRef.current = updatedAt;
      return;
    }
    setPermissionDraft(buildDirectPermissionDraft(data.state));
    permissionAppliedAtRef.current = updatedAt;
  }, [busy, permissionAccessQuery.data, permissionAccessQuery.dataUpdatedAt, permissionsDirty]);

  const queryErrors = [
    storesEnabled ? storesQuery.error : null,
    storeCatalogEnabled ? storeCatalogQuery.error : null,
    rolesEnabled && !roleAccessForbidden ? rolesQuery.error : null,
    rolesEnabled && !roleAccessForbidden ? roleCatalogQuery.error : null,
    permissionsEnabled && !permissionAccessForbidden ? permissionAccessQuery.error : null,
  ].filter(Boolean);
  const firstTerminalQueryError = queryErrors.find(
    (error) => classifyUserAccessError(error) !== "network",
  );

  useEffect(() => {
    if (!firstTerminalQueryError) return;
    const errorKind = classifyUserAccessError(firstTerminalQueryError);
    if (errorKind === "network") return;
    setTerminalErrorKind(errorKind);
    if (errorKind === "unauthorized") {
      // 401 必须优先退出失效编辑上下文，不能被脏草稿离页保护卡住。
      setAuthRedirectRequested(true);
      setAllowRemove(true);
    }
  }, [firstTerminalQueryError]);

  useEffect(() => {
    if (!isAuthenticated && !isDeviceMode) {
      setAuthRedirectRequested(true);
      setAllowRemove(true);
    }
  }, [isAuthenticated, isDeviceMode]);

  /** 目标拉取失败等「整页不可用」的终态错误，由页面调用。 */
  const failTerminal = useCallback((errorKind: Exclude<UserAccessErrorKind, "network">) => {
    setTerminalErrorKind(errorKind);
    if (errorKind === "unauthorized") {
      setAuthRedirectRequested(true);
      setAllowRemove(true);
    }
  }, []);

  // ───────────── 页面展示用的数据 ─────────────
  const actorManagedStoreOptions = useMemo<AccessStoreOption[]>(
    () =>
      access.isAdmin
        ? []
        : (currentUser?.stores ?? []).flatMap((store) => {
            const storeGUID = store.storeGUID?.trim();
            if (!store.isPrimary || !storeGUID) return [];
            return [
              {
                storeGUID,
                storeCode: store.storeCode,
                storeName: store.storeName,
                ...(typeof store.isActive === "boolean" ? { isActive: store.isActive } : {}),
              },
            ];
          }),
    [access.isAdmin, currentUser?.stores],
  );
  const storeOptions = useMemo(
    () =>
      buildAccessStoreOptions({
        isAdmin: access.isAdmin,
        catalog: storeCatalogQuery.data,
        actorManaged: actorManagedStoreOptions,
        assigned: storesQuery.data,
        managedStoreCodes: managedStoreCodeSet,
      }),
    [
      access.isAdmin,
      actorManagedStoreOptions,
      managedStoreCodeSet,
      storeCatalogQuery.data,
      storesQuery.data,
    ],
  );
  const visibleStoreGuids = useMemo(
    () => new Set(storeOptions.map((store) => store.storeGUID)),
    [storeOptions],
  );
  const roleOptions = useMemo(
    () =>
      limitUserAccessRolesForActor(
        mergeAccessRoleOptions(roleCatalogQuery.data, rolesQuery.data),
        access.isAdmin,
      ),
    [access.isAdmin, roleCatalogQuery.data, rolesQuery.data],
  );
  const hasManagedStoreAssignment = storeAssignments.some((item) => item.isPrimary);

  const permissionCategories = useMemo(
    () => permissionAccessQuery.data?.categories ?? [],
    [permissionAccessQuery.data?.categories],
  );
  /** 授权页里展示的直接权限分类：收银（POS）权限统一去收银权限页，这里不列。 */
  const visiblePermissionCategories = useMemo(
    () => excludePosPermissionCategories(permissionCategories),
    [permissionCategories],
  );
  const implicitAll = permissionAccessQuery.data?.state.implicitAllPermissions === true;
  /** 当前操作者能分配的全部权限码（含收银权限，菜单分段按它判断可否开关）。 */
  const assignablePermissionCodes = useMemo(
    () =>
      permissionCategories.flatMap((category) =>
        category.permissions.map((permission) => permission.name),
      ),
    [permissionCategories],
  );
  const inheritedSourceMap = useMemo(() => {
    const result = new Map<string, string[]>();
    permissionAccessQuery.data?.state.inheritedSources.forEach((source) => {
      source.permissionCodes.forEach((code) => {
        result.set(code, [
          ...(result.get(code) ?? []),
          localizeAccessRoleName(source.roleName, language),
        ]);
      });
    });
    return result;
  }, [language, permissionAccessQuery.data?.state.inheritedSources]);
  const permissionCounts = useMemo(
    () =>
      permissionDraft
        ? summarizeAccessPermissionCounts({
            draft: permissionDraft,
            implicitAll,
            catalogCodes: assignablePermissionCodes,
          })
        : { effective: 0, inherited: 0, direct: 0 },
    [assignablePermissionCodes, implicitAll, permissionDraft],
  );
  const permissionNameResolver = useMemo(
    () =>
      createPermissionNameResolver(
        permissionCategories.flatMap((category) =>
          category.permissions.map((permission) => ({
            code: permission.name,
            name: localizeAccessPermission(permission, language).name,
          })),
        ),
      ),
    [language, permissionCategories],
  );

  const changeSummary = useMemo(
    () =>
      buildAccessChangeSummary({
        storeAssignments,
        baselineStoreAssignments,
        visibleStoreGuids,
        roleOptions,
        selectedRoleGuids,
        baselineRoleGuids,
        permissionDraft,
      }),
    [
      baselineRoleGuids,
      baselineStoreAssignments,
      permissionDraft,
      roleOptions,
      selectedRoleGuids,
      storeAssignments,
      visibleStoreGuids,
    ],
  );
  const counts = useMemo(
    () => ({
      stores: countAssignedStores(storeAssignments, visibleStoreGuids),
      roles: countSelectedAccessRoles(roleOptions, selectedRoleGuids, hasManagedStoreAssignment),
      permissions: permissionCounts.direct,
    }),
    [
      hasManagedStoreAssignment,
      permissionCounts.direct,
      roleOptions,
      selectedRoleGuids,
      storeAssignments,
      visibleStoreGuids,
    ],
  );

  // ───────────── 草稿操作 ─────────────
  const setStoreState = useCallback(
    (storeGuid: string, state: UserAccessStoreState) => {
      // 「管理」只有管理员能授予；界面已经不显示这一档，这里再兜一层。
      if (state === "manage" && !eligibility.canGrantStoreManagement) return;
      setStoreAssignments((current) => setUserAccessStoreState(current, storeGuid, state));
    },
    [eligibility.canGrantStoreManagement],
  );

  const toggleRole = useCallback((roleGuid: string) => {
    setSelectedRoleGuids((current) =>
      current.includes(roleGuid)
        ? current.filter((guid) => guid !== roleGuid)
        : [...current, roleGuid],
    );
  }, []);

  const setPermissionChecked = useCallback((code: string, checked: boolean) => {
    setPermissionDraft((current) =>
      current ? toggleDirectPermission(current, code, checked) : current,
    );
  }, []);

  /** 「菜单」分段：一个菜单项对应一组权限码（OR 条件），与直接权限共用同一份草稿。 */
  const setMenuPermissionCodes = useCallback((codes: string[], checked: boolean) => {
    setPermissionDraft((current) =>
      current ? togglePermissionCodes(current, codes, checked, assignablePermissionCodes) : current,
    );
  }, [assignablePermissionCodes]);

  /**
   * 管理员点「店长」角色行：店长由「管理」分店派生，真实操作是把某家分店设为「管理」（PR #840）。
   * 只有一家分店时直接改草稿；其余情况只返回计划，由页面引导。
   */
  const guideManagerGrant = useCallback((): {
    plan: StoreManagerGrantPlan;
    storeName: string;
  } => {
    const plan = planStoreManagerGrant({
      canGrantStoreManagement: eligibility.canGrantStoreManagement,
      storesEditable: eligibility.storesMode === "editable",
      assignments: storeAssignments,
    });
    if (plan.kind !== "set") return { plan, storeName: "" };
    const store = storeOptions.find((item) => item.storeGUID === plan.storeGUID);
    setStoreAssignments((current) => setUserAccessStoreState(current, plan.storeGUID, "manage"));
    return { plan, storeName: store?.storeName || store?.storeCode || "" };
  }, [eligibility.canGrantStoreManagement, eligibility.storesMode, storeAssignments, storeOptions]);

  // ───────────── 保存 ─────────────
  const retryQueries = useCallback(() => {
    const retries: Promise<unknown>[] = [];
    if (storesEnabled) retries.push(storesQuery.refetch());
    if (storeCatalogEnabled) retries.push(storeCatalogQuery.refetch());
    if (rolesEnabled && !roleAccessForbidden)
      retries.push(rolesQuery.refetch(), roleCatalogQuery.refetch());
    if (permissionsEnabled && !permissionAccessForbidden) {
      retries.push(permissionAccessQuery.refetch());
    }
    void Promise.allSettled(retries);
  }, [
    permissionAccessForbidden,
    permissionAccessQuery,
    permissionsEnabled,
    roleAccessForbidden,
    roleCatalogQuery,
    rolesEnabled,
    rolesQuery,
    storeCatalogEnabled,
    storeCatalogQuery,
    storesEnabled,
    storesQuery,
  ]);

  /** 某一步写入失败后的状态处理（与旧授权页一致：非管理员的局部 403 只关闭对应分段）。 */
  const handleStepFailure = useCallback(
    (key: AccessSaveStepKey, error: unknown): UserAccessErrorKind => {
      const errorKind = classifyUserAccessError(error);
      if (errorKind === "network") return errorKind;
      if (errorKind === "forbidden" && !access.isAdmin && key !== "stores") {
        // 局部授权失效只关闭对应分段，清除已不可提交的草稿并保留分店维护能力。
        const resetPermissionDraft = () =>
          setPermissionDraft((current) =>
            current ? { ...current, selectedCodes: [...current.baselineCodes] } : current,
          );
        if (key === "roles") {
          setRoleMutationForbidden(true);
          setSelectedRoleGuids([...baselineRoleGuids]);
          resetPermissionDraft();
        } else {
          setPermissionMutationForbidden(true);
          resetPermissionDraft();
        }
        return errorKind;
      }
      setTerminalErrorKind(errorKind);
      if (errorKind === "unauthorized") {
        setAuthRedirectRequested(true);
        setAllowRemove(true);
      }
      return errorKind;
    },
    [access.isAdmin, baselineRoleGuids],
  );

  /**
   * 一次保存：把有改动的分段按「分店 → 角色 → 直接权限」顺序依次提交。
   * 任何一步失败就停下，已成功的步骤把基线推进到已保存的值，失败与未执行的步骤草稿原样保留，
   * 由调用方根据返回的 report 明确告诉用户哪几步成功、哪一步失败。
   */
  const save = useCallback(async (): Promise<AccessSaveReport | null> => {
    if (busy || operationInFlightRef.current) return null;
    const keys = planAccessSaveSteps({
      stores: storesDirty,
      roles: rolesDirty,
      permissions: permissionsDirty,
    });
    if (!keys.length) return null;
    operationInFlightRef.current = true;
    const savingActor = actorUserGuid;
    try {
      const runners: Record<AccessSaveStepKey, () => Promise<void>> = {
        stores: async () => {
          const submitted = [...storeAssignments];
          await assignStoresMutation.mutateAsync({ userGuid, assignments: submitted });
          setBaselineStoreAssignments(submitted);
          // 「管理」分店变化会改变派生的店长角色与继承来的权限，重新读取（已被 403 关闭的分段不再请求）。
          const refreshes: Promise<unknown>[] = [];
          if (rolesEnabled && !roleAccessForbidden) refreshes.push(rolesQuery.refetch());
          if (permissionsEnabled && !permissionAccessForbidden) {
            refreshes.push(permissionAccessQuery.refetch());
          }
          void Promise.allSettled(refreshes);
        },
        roles: async () => {
          const submitted = [...selectedRoleGuids];
          await assignRolesMutation.mutateAsync({
            userGuid,
            roleGuids: submitted,
            roleCatalog: roleOptions,
          });
          setBaselineRoleGuids(submitted);
        },
        permissions: async () => {
          if (!permissionDraft) return;
          const submitted = [...permissionDraft.selectedCodes];
          await assignPermissionsMutation.mutateAsync({ userGuid, permissions: submitted });
          setPermissionDraft((current) =>
            current ? { ...current, baselineCodes: submitted } : current,
          );
        },
      };
      const steps: AccessSaveStep[] = keys.map((key) => ({ key, run: runners[key] }));
      const outcome = await runAccessSaveSteps(steps, {
        isCurrent: () => {
          const state = useAuthStore.getState();
          return state.isAuthenticated && (state.user?.userGUID || state.user?.userGuid || "") === savingActor;
        },
      });
      const failedKind = outcome.failed ? handleStepFailure(outcome.failed.key, outcome.failed.error) : null;
      return { verdict: classifyAccessSaveOutcome(outcome), outcome, failedKind };
    } finally {
      operationInFlightRef.current = false;
    }
  }, [
    actorUserGuid,
    assignPermissionsMutation,
    assignRolesMutation,
    assignStoresMutation,
    busy,
    handleStepFailure,
    permissionAccessForbidden,
    permissionAccessQuery,
    permissionDraft,
    permissionsDirty,
    permissionsEnabled,
    roleAccessForbidden,
    roleOptions,
    rolesDirty,
    rolesEnabled,
    rolesQuery,
    selectedRoleGuids,
    storeAssignments,
    storesDirty,
    userGuid,
  ]);

  const previewCopy = useCallback(
    (input: CopyUserDirectPermissionsInput) => previewCopyMutation.mutateAsync(input),
    [previewCopyMutation],
  );
  const commitCopy = useCallback(
    (input: CopyUserDirectPermissionsInput) => copyPermissionsMutation.mutateAsync(input),
    [copyPermissionsMutation],
  );

  // ───────────── 加载状态 ─────────────
  const requiredQueries = [
    ...(storesEnabled ? [storesQuery] : []),
    ...(storeCatalogEnabled ? [storeCatalogQuery] : []),
    ...(rolesEnabled && !roleAccessForbidden ? [rolesQuery, roleCatalogQuery] : []),
    ...(permissionsEnabled && !permissionAccessForbidden ? [permissionAccessQuery] : []),
  ];
  const initialLoading = requiredQueries.some(
    (query) => query.data === undefined && (query.isLoading || query.isFetching),
  );
  const initialError = requiredQueries.find(
    (query) => query.data === undefined && query.error,
  )?.error;

  return {
    // 会话与资格
    isDeviceMode,
    isAuthenticated,
    isAdmin: access.isAdmin,
    actorUserGuid,
    managedStoreCodes,
    eligibility,
    // 加载 / 错误
    initialLoading,
    initialError,
    terminalErrorKind,
    failTerminal,
    retryQueries,
    // 离页保护
    allowRemove,
    setAllowRemove,
    authRedirectRequested,
    // 展示数据
    storeOptions,
    assignedStoreCodes: (storesQuery.data ?? []).map((store) => store.storeCode),
    roleOptions,
    permissionCategories,
    visiblePermissionCategories,
    permissionAccessState: permissionAccessQuery.data?.state ?? null,
    implicitAll,
    assignablePermissionCodes,
    inheritedSourceMap,
    permissionCounts,
    permissionNameResolver,
    // 草稿
    storeAssignments,
    baselineStoreAssignments,
    selectedRoleGuids,
    permissionDraft,
    hasManagedStoreAssignment,
    // 脏状态 / 汇总
    storesDirty,
    rolesDirty,
    permissionsDirty,
    dirty,
    busy,
    saving:
      assignStoresMutation.isPending ||
      assignRolesMutation.isPending ||
      assignPermissionsMutation.isPending,
    changeSummary,
    counts,
    // 操作
    setStoreState,
    toggleRole,
    setPermissionChecked,
    setMenuPermissionCodes,
    guideManagerGrant,
    save,
    previewCopy,
    commitCopy,
    // 权限分段是否被 403 关闭
    permissionAccessForbidden,
  };
}

export type AccessEditor = ReturnType<typeof useAccessEditor>;

import { useCallback, useEffect, useMemo, useState } from "react";
import { Alert, FlatList, Pressable, RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { SafeAreaView } from "react-native-safe-area-context";
import {
  ActivityIndicator,
  Avatar,
  Button,
  Card,
  Chip,
  Dialog,
  Icon,
  IconButton,
  Portal,
  Searchbar,
  SegmentedButtons,
  Snackbar,
  Switch,
  Text,
  TextInput,
} from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import type { Store } from "@/modules/shop/types";
import { getDeviceBoundStoreCode } from "@/modules/shop/device-bound-store-filter";
import { getManageableStoresForSession, getPosEnabledStores, isStoreManageable } from "@/modules/shop/store-scope";
import { useStores } from "@/modules/shop/use-stores";
import {
  STORE_STAFF_ROLE,
  toSafeStoreUserErrorLog,
  useStoreUserDetail,
  useStoreUserMutations,
  useStoreUsers,
  type StoreUserFormValues,
  type StoreUserListItem,
} from "@/modules/users";
import { StaffBarcodeBatchDialog, StaffBarcodeDialog } from "@/modules/users/staff-barcode/StaffBarcodeDialogs";
import { canManageStaffBarcode } from "@/modules/users/staff-barcode/eligibility";
import { getUserAccessEligibility } from "@/modules/users/access-management";
import { validatePasswordValue, validateStoreUserForm } from "@/modules/users/validation";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { PERMISSIONS } from "@/shared/utils/access";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";

type StatusFilter = "all" | "active" | "disabled";

const EMPTY_FORM: StoreUserFormValues = {
  username: "",
  fullName: "",
  email: "",
  phone: "",
  status: true,
};

function getInitials(user: StoreUserListItem) {
  const source = user.fullName || user.username || "?";
  const words = source.trim().split(/\s+/).filter(Boolean);
  if (!words.length) {
    return "?";
  }
  if (words.length === 1) {
    return words[0].slice(0, 2).toUpperCase();
  }
  return `${words[0][0]}${words[1][0]}`.toUpperCase();
}

export default function UsersScreen() {
  const router = useRouter();
  const { t, language } = useAppTranslation(["userManagement", "common"]);
  const access = useAuthStore((state) => state.access);
  const currentUser = useAuthStore((state) => state.user);
  const authenticated = useAuthStore((state) => state.isAuthenticated);
  const actorGuid = currentUser?.userGUID || currentUser?.userGuid || "";
  const {
    stores,
    selectedStoreCode: rememberedStoreCode,
    isSelectedStoreAuto,
    isDeviceMode,
    isHydratingSelection,
    isLoading: storesLoading,
    selectStore,
  } = useStores();
  // 仅收窄选择候选，设备绑定与店员操作权限继续使用原分店范围。
  const posEnabledStores = useMemo(() => getPosEnabledStores(stores), [stores]);
  const [managedStoreCode, setManagedStoreCode] = useState<string | null>(null);
  const [storePickerVisible, setStorePickerVisible] = useState(false);
  const [keywordInput, setKeywordInput] = useState("");
  const [keyword, setKeyword] = useState("");
  const [statusFilter, setStatusFilter] = useState<StatusFilter>("all");
  const [snackbarMessage, setSnackbarMessage] = useState("");
  const [dialogVisible, setDialogVisible] = useState(false);
  const [editingUserGuid, setEditingUserGuid] = useState<string | null>(null);
  const [editingStoreCode, setEditingStoreCode] = useState<string | null>(null);
  const [formValues, setFormValues] = useState<StoreUserFormValues>(EMPTY_FORM);
  const [initialPassword, setInitialPassword] = useState("");
  const [resetPasswordVisible, setResetPasswordVisible] = useState(false);
  const [resetPasswordValue, setResetPasswordValue] = useState("");
  const [passwordUser, setPasswordUser] = useState<StoreUserListItem | null>(null);
  const [moreUser, setMoreUser] = useState<StoreUserListItem | null>(null);
  const [barcodeUser, setBarcodeUser] = useState<StoreUserListItem | null>(null);
  const [createdUser, setCreatedUser] = useState<StoreUserListItem | null>(null);
  const [batchSelecting, setBatchSelecting] = useState(false);
  const [batchVisible, setBatchVisible] = useState(false);
  const [selectedUserGuids, setSelectedUserGuids] = useState<Set<string>>(new Set());
  const deviceBoundStoreCode = getDeviceBoundStoreCode({
    isDeviceMode,
    selectedStoreCode: rememberedStoreCode,
  });

  const canViewUsers = access.isAdmin || access.canReadUser;
  const canCreateUsers = access.isAdmin || access.hasPermission(PERMISSIONS.Users.Create);
  const canEditUsers = access.isAdmin || access.hasPermission(PERMISSIONS.Users.Edit);
  const canResetPasswords = access.isAdmin || access.hasPermission(PERMISSIONS.Users.ResetPassword);
  const canManageUserRoles = access.hasPermission(PERMISSIONS.Users.ManageRoles);
  const canManageUserStores = access.hasPermission(PERMISSIONS.Users.ManageStores);
  const canManagePosTerminalPermissions = access.hasPermission(PERMISSIONS.Users.ManagePos);
  const manageableStores = useMemo(
    () =>
      getManageableStoresForSession({
        stores,
        isDeviceMode,
        deviceBoundStore: deviceBoundStoreCode ? stores.find((store) => store.storeCode === deviceBoundStoreCode) ?? null : null,
        isAdmin: access.isAdmin,
      }),
    [access.isAdmin, deviceBoundStoreCode, isDeviceMode, stores]
  );

  useEffect(() => {
    if (isHydratingSelection || storesLoading) {
      return;
    }

    setManagedStoreCode((current) => {
      if (deviceBoundStoreCode) {
        return deviceBoundStoreCode;
      }

      if (current && posEnabledStores.some((store) => store.storeCode === current)) {
        return current;
      }

      // 全局自动默认的分店不算用户选择，仍默认全部分店
      const selectedAssignedStore = rememberedStoreCode && !isSelectedStoreAuto
        ? posEnabledStores.find((store) => store.storeCode === rememberedStoreCode)
        : null;
      if (selectedAssignedStore) {
        return selectedAssignedStore.storeCode;
      }

      return null;
    });
  }, [deviceBoundStoreCode, isHydratingSelection, isSelectedStoreAuto, rememberedStoreCode, posEnabledStores, storesLoading]);

  const managedStore = useMemo(
    () => stores.find((store) => store.storeCode === managedStoreCode) ?? null,
    [managedStoreCode, stores]
  );
  const selectedStoreCanCreate = canCreateUsers && isStoreManageable(managedStoreCode, manageableStores);
  const selectedStoreCanManageUsers =
    (canCreateUsers || canEditUsers || canResetPasswords) &&
    isStoreManageable(managedStoreCode, manageableStores);

  const usersQuery = useStoreUsers(
    canViewUsers ? (isDeviceMode ? deviceBoundStoreCode : managedStoreCode) : undefined,
    keyword
  );
  const detailQuery = useStoreUserDetail(
    editingUserGuid,
    editingStoreCode
  );
  const { createMutation, updateMutation, statusMutation, passwordMutation } =
    useStoreUserMutations(managedStoreCode, keyword);

  const isBusy =
    createMutation.isPending ||
    updateMutation.isPending ||
    statusMutation.isPending ||
    passwordMutation.isPending;

  const resolveUserStoreCode = useCallback(
    (user: StoreUserListItem) => managedStoreCode || user.storeCode || null,
    [managedStoreCode]
  );
  const canModifyUserStore = useCallback(
    (user: StoreUserListItem) => canEditUsers && isStoreManageable(resolveUserStoreCode(user), manageableStores),
    [canEditUsers, manageableStores, resolveUserStoreCode]
  );
  const canResetUserPassword = useCallback(
    (user: StoreUserListItem) =>
      canResetPasswords && isStoreManageable(resolveUserStoreCode(user), manageableStores),
    [canResetPasswords, manageableStores, resolveUserStoreCode]
  );
  const canManageUserBarcode = useCallback(
    (user: StoreUserListItem) => canManageStaffBarcode({
      authenticated,
      deviceOnly: isDeviceMode,
      canEditUsers,
      canManagePosStore: isStoreManageable(resolveUserStoreCode(user), manageableStores)
        && posEnabledStores.some((store) => store.storeCode === resolveUserStoreCode(user)),
      actorGuid,
      actorRoles: currentUser?.roleNames ?? [],
      targetGuid: user.userGUID,
      targetStatus: user.status,
      targetRoles: user.roleNames,
    }),
    [actorGuid, authenticated, canEditUsers, currentUser?.roleNames, isDeviceMode, manageableStores, posEnabledStores, resolveUserStoreCode]
  );

  useEffect(() => {
    if (!detailQuery.isSuccess || !dialogVisible) {
      return;
    }

    setFormValues({
      username: detailQuery.data.username,
      fullName: detailQuery.data.fullName ?? "",
      email: detailQuery.data.email ?? "",
      phone: detailQuery.data.phone ?? "",
      status: detailQuery.data.status === 1,
    });
  }, [detailQuery.data, detailQuery.isSuccess, dialogVisible]);

  const resetDialogState = useCallback(() => {
    setDialogVisible(false);
    setEditingUserGuid(null);
    setEditingStoreCode(null);
    setFormValues(EMPTY_FORM);
    setInitialPassword("");
  }, []);

  const openCreateDialog = useCallback(() => {
    // 创建必须绑定当前已选且可管理的分店，不能回落到“全部已分配分店”。
    if (!managedStoreCode || !selectedStoreCanCreate) {
      setSnackbarMessage(t("messages.selectManageableStoreFirst"));
      return;
    }

    setEditingUserGuid(null);
    setEditingStoreCode(managedStoreCode);
    setFormValues(EMPTY_FORM);
    setInitialPassword("");
    setDialogVisible(true);
  }, [managedStoreCode, selectedStoreCanCreate, t]);

  const openEditDialog = useCallback(
    (user: StoreUserListItem) => {
      const targetStoreCode = resolveUserStoreCode(user);
      if (!targetStoreCode) {
        setSnackbarMessage(t("messages.selectStoreFirst"));
        return;
      }
      if (!canModifyUserStore(user)) {
        setSnackbarMessage(t("messages.storeReadOnly"));
        return;
      }

      setEditingUserGuid(user.userGUID);
      setEditingStoreCode(targetStoreCode);
      setFormValues({
        username: user.username,
        fullName: user.fullName ?? "",
        email: user.email ?? "",
        phone: user.phone ?? "",
        status: user.status === 1,
      });
      setDialogVisible(true);
    },
    [canModifyUserStore, resolveUserStoreCode, t]
  );

  const openStaffDetail = useCallback(
    (user: StoreUserListItem) => {
      const targetStoreCode = resolveUserStoreCode(user);
      if (!targetStoreCode) {
        setSnackbarMessage(t("messages.selectStoreFirst"));
        return;
      }

      router.push({
        pathname: "/staff/[userGuid]",
        params: { userGuid: user.userGUID, storeCode: targetStoreCode },
      } as unknown as Parameters<typeof router.push>[0]);
    },
    [resolveUserStoreCode, router, t]
  );

  const openUserAccess = useCallback(
    (user: StoreUserListItem) => {
      router.push({
        pathname: "/users/[userGuid]/access",
        params: {
          userGuid: user.userGUID,
          userName: user.fullName || user.username,
          username: user.username,
          targetStatus: String(user.status),
          roleNames: user.roleNames.join("|"),
        },
      } as unknown as Parameters<typeof router.push>[0]);
    },
    [router]
  );

  const closeResetPasswordDialog = useCallback(() => {
    setPasswordUser(null);
    setResetPasswordValue("");
    setResetPasswordVisible(false);
  }, []);

  const submitKeyword = useCallback(() => {
    setKeyword(keywordInput.trim());
  }, [keywordInput]);

  const handleSelectManagedStore = useCallback(
    async (store: Store | null) => {
      // 批量选择只能属于一个明确分店，切店时必须清空，避免跨店打印。
      setSelectedUserGuids(new Set());
      setBatchSelecting(false);
      setManagedStoreCode(deviceBoundStoreCode ?? store?.storeCode ?? null);
      setStorePickerVisible(false);

      try {
        await selectStore(store);
      } catch (error) {
        console.warn("[store-users] failed to persist store selection", error);
      }
    },
    [deviceBoundStoreCode, selectStore]
  );

  const handleRefresh = useCallback(async () => {
    try {
      await usersQuery.refetch();
    } catch (error) {
      console.warn("[store-users] refresh failed", error);
      setSnackbarMessage(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "messages.refreshFailed" }));
    }
  }, [language, t, usersQuery]);

  const validateForm = useCallback(() => {
    const validationMessage = validateStoreUserForm(formValues, t);
    if (validationMessage) {
      setSnackbarMessage(validationMessage);
      return false;
    }

    return true;
  }, [formValues, t]);

  const handleSubmit = useCallback(async () => {
    const targetStoreCode = editingStoreCode;
    const isCreating = !editingUserGuid;
    if (!targetStoreCode) {
      setSnackbarMessage(t("messages.selectStoreFirst"));
      return;
    }
    if (isCreating && (!canCreateUsers || !isStoreManageable(targetStoreCode, manageableStores))) {
      setSnackbarMessage(t("messages.storeReadOnly"));
      return;
    }
    if (!isCreating && (!canEditUsers || !isStoreManageable(targetStoreCode, manageableStores))) {
      setSnackbarMessage(t("messages.storeReadOnly"));
      return;
    }

    if (!validateForm()) {
      return;
    }

    if (isCreating) {
      const passwordValidationMessage = validatePasswordValue(initialPassword, t);
      if (passwordValidationMessage) {
        setSnackbarMessage(passwordValidationMessage);
        return;
      }
    }

    const payload = {
      username: formValues.username.trim(),
      fullName: formValues.fullName.trim() || undefined,
      email: formValues.email.trim() || undefined,
      phone: formValues.phone.trim() || undefined,
      status: formValues.status ? 1 : 0,
      storeCode: targetStoreCode,
      roleNames: [STORE_STAFF_ROLE],
    };

    try {
      if (isCreating) {
        const created = await createMutation.mutateAsync({
          ...payload,
          password: initialPassword.trim(),
          passwordFormat: "raw",
          employmentType: "casual",
        });
        setCreatedUser(created);
        setSnackbarMessage(t("messages.userCreated"));
      } else {
        await updateMutation.mutateAsync({ ...payload, userGuid: editingUserGuid });
        setSnackbarMessage(t("messages.userUpdated"));
      }

      resetDialogState();
    } catch (error) {
      console.warn("[store-users] save failed", toSafeStoreUserErrorLog(error));
      setSnackbarMessage(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "messages.saveFailed" }));
    }
  }, [
    canCreateUsers,
    canEditUsers,
    createMutation,
    editingStoreCode,
    editingUserGuid,
    formValues,
    initialPassword,
    manageableStores,
    resetDialogState,
    language,
    t,
    updateMutation,
    validateForm,
  ]);

  const handleToggleStatus = useCallback(
    (user: StoreUserListItem) => {
      const targetStoreCode = resolveUserStoreCode(user);
      if (!targetStoreCode) {
        setSnackbarMessage(t("messages.selectStoreFirst"));
        return;
      }
      if (!canModifyUserStore(user)) {
        setSnackbarMessage(t("messages.storeReadOnly"));
        return;
      }

      const nextEnabled = user.status !== 1;
      const actionLabel = nextEnabled ? t("actions.enable") : t("actions.disable");
      Alert.alert(
        actionLabel,
        t("dialogs.statusConfirmMessage", { action: actionLabel, username: user.username }),
        [
          { text: t("actions.cancel"), style: "cancel" },
          {
            text: actionLabel,
            style: nextEnabled ? "default" : "destructive",
            onPress: async () => {
              try {
                await statusMutation.mutateAsync({
                  userGuid: user.userGUID,
                  storeCode: targetStoreCode,
                  status: nextEnabled ? 1 : 0,
                });
                setSnackbarMessage(nextEnabled ? t("messages.userEnabled") : t("messages.userDisabled"));
              } catch (error) {
                console.warn("[store-users] status failed", error);
                setSnackbarMessage(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "messages.statusFailed" }));
              }
            },
          },
        ]
      );
    },
    [canModifyUserStore, language, resolveUserStoreCode, statusMutation, t]
  );

  const openResetPasswordDialog = useCallback(
    (user: StoreUserListItem) => {
      if (!canResetUserPassword(user)) {
        setSnackbarMessage(t("messages.storeReadOnly"));
        return;
      }

      setPasswordUser(user);
      setResetPasswordValue("");
      setResetPasswordVisible(true);
    },
    [canResetUserPassword, t]
  );

  const handleResetPassword = useCallback(async () => {
    if (!passwordUser) {
      return;
    }

    const targetStoreCode = resolveUserStoreCode(passwordUser);
    if (!targetStoreCode) {
      setSnackbarMessage(t("messages.selectStoreFirst"));
      return;
    }
    if (!canResetPasswords || !isStoreManageable(targetStoreCode, manageableStores)) {
      setSnackbarMessage(t("messages.storeReadOnly"));
      return;
    }

    const validationMessage = validatePasswordValue(resetPasswordValue, t);
    if (validationMessage) {
      setSnackbarMessage(validationMessage);
      return;
    }

    try {
      await passwordMutation.mutateAsync({
        userGuid: passwordUser.userGUID,
        storeCode: targetStoreCode,
        newPassword: resetPasswordValue.trim(),
        passwordFormat: "raw",
      });
      setSnackbarMessage(t("messages.passwordReset"));
      closeResetPasswordDialog();
    } catch (error) {
      console.warn("[store-users] password reset failed", toSafeStoreUserErrorLog(error));
      setSnackbarMessage(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "messages.passwordResetFailed" }));
    }
  }, [
    closeResetPasswordDialog,
    canResetPasswords,
    language,
    manageableStores,
    passwordMutation,
    passwordUser,
    resolveUserStoreCode,
    resetPasswordValue,
    t,
  ]);

  const filteredUsers = useMemo(() => {
    const items = usersQuery.data ?? [];
    const filtered = items.filter((item) => {
      if (statusFilter === "active") {
        return item.status === 1;
      }
      if (statusFilter === "disabled") {
        return item.status !== 1;
      }
      return true;
    });

    return [...filtered].sort((left, right) =>
      (left.fullName || left.username).localeCompare(right.fullName || right.username)
    );
  }, [statusFilter, usersQuery.data]);

  const statusCounts = useMemo(() => {
    const items = usersQuery.data ?? [];
    const active = items.filter((item) => item.status === 1).length;
    return { all: items.length, active, disabled: items.length - active };
  }, [usersQuery.data]);

  const selectedUsers = useMemo(
    () => (usersQuery.data ?? []).filter(
      (user) => selectedUserGuids.has(user.userGUID) && canManageUserBarcode(user)
    ),
    [canManageUserBarcode, selectedUserGuids, usersQuery.data]
  );

  const moreAccessEligibility = useMemo(() => moreUser ? getUserAccessEligibility({
    isDeviceMode,
    isAdmin: access.isAdmin,
    isStoreManager: access.isStoreManager,
    canManageStores: canManageUserStores,
    canManageRoles: canManageUserRoles,
    canManagePos: canManagePosTerminalPermissions,
    currentUserGuid: currentUser?.userGUID,
    targetUserGuid: moreUser.userGUID,
    targetStatus: moreUser.status,
    targetRoleNames: moreUser.roleNames,
    hasManageableStores: access.isAdmin || manageableStores.length > 0,
  }) : null, [
    access.isAdmin,
    access.isStoreManager,
    canManagePosTerminalPermissions,
    canManageUserRoles,
    canManageUserStores,
    currentUser?.userGUID,
    isDeviceMode,
    manageableStores.length,
    moreUser,
  ]);

  const storeCaption = useMemo(() => {
    if (!managedStoreCode) {
      return t("currentStore.allRelated");
    }

    if (!managedStore) {
      return t("currentStore.empty");
    }

    return t("currentStore.value", {
      code: managedStore.storeCode,
      name: managedStore.storeName || managedStore.storeCode,
    });
  }, [managedStore, managedStoreCode, t]);

  const renderStorePickerLabel = useCallback(
    (store: Store) => {
      const manageable = isStoreManageable(store.storeCode, manageableStores);

      return (
        <View style={styles.storePickerLabelRow}>
          <View style={styles.storePickerLabelText}>
            <Text variant="bodyMedium">{store.storeName || store.storeCode}</Text>
            <Text variant="bodySmall" style={styles.secondaryText}>
              {store.storeCode}
            </Text>
          </View>
          <Chip compact style={manageable ? styles.manageableChip : styles.viewOnlyChip}>
            {manageable ? t("currentStore.manageableBadge") : t("currentStore.viewOnlyBadge")}
          </Chip>
        </View>
      );
    },
    [manageableStores, t]
  );

  const renderUserCard = useCallback(
    ({ item }: { item: StoreUserListItem }) => {
      const employmentType = item.employmentType
        ? t(`detail.employmentTypes.${item.employmentType}`, item.employmentType)
        : t("fields.positionValue");
      const canUseBarcode = canManageUserBarcode(item);

      const toggleSelection = () => setSelectedUserGuids((current) => {
        const next = new Set(current);
        if (next.has(item.userGUID)) next.delete(item.userGUID);
        else next.add(item.userGUID);
        return next;
      });

      return (
        <Card
          style={styles.userCard}
          mode="contained"
          onPress={() => batchSelecting && canUseBarcode ? toggleSelection() : openStaffDetail(item)}
          testID="compact-staff-row"
        >
          <Card.Content style={styles.userCardContent}>
            <View style={styles.userCardHeader}>
              {batchSelecting && canUseBarcode ? (
                <IconButton
                  icon={selectedUserGuids.has(item.userGUID) ? "checkbox-marked" : "checkbox-blank-outline"}
                  size={22}
                  accessibilityLabel={t("staffBarcode.batch.selectEmployee", { name: item.fullName || item.username })}
                  onPress={toggleSelection}
                />
              ) : null}
              <View style={styles.identityRow}>
                <Avatar.Text size={38} label={getInitials(item)} style={styles.avatar} />
                <View style={styles.userTitleWrap}>
                  <Text variant="titleSmall" numberOfLines={1}>{item.fullName || item.username}</Text>
                  <Text variant="bodySmall" style={styles.secondaryText}>
                    @{item.username} · {employmentType}
                  </Text>
                  {!managedStoreCode && item.storeCode ? (
                    <Text variant="labelSmall" style={styles.secondaryText} numberOfLines={1}>
                      {item.storeName || item.storeCode} · {item.storeCode}
                    </Text>
                  ) : null}
                </View>
              </View>
              <View style={styles.cardMenuActions}>
                <Chip compact style={item.status === 1 ? styles.activeChip : styles.inactiveChip}>
                  {item.status === 1 ? t("statuses.active") : t("statuses.disabled")}
                </Chip>
                {canUseBarcode && !batchSelecting ? (
                  <Pressable
                    style={styles.codeAction}
                    accessibilityRole="button"
                    accessibilityLabel={`${item.fullName || item.username} · ${t("staffBarcode.actions.open")}`}
                    onPress={() => setBarcodeUser(item)}
                  >
                    <Icon source="qrcode" size={20} color={HB_COLORS.action} />
                    <Text variant="labelSmall" style={styles.codeActionText}>{t("staffBarcode.actions.open")}</Text>
                  </Pressable>
                ) : null}
                <IconButton
                  icon="dots-vertical"
                  size={20}
                  accessibilityLabel={`${item.fullName || item.username} · ${t("actions.more")}`}
                  onPress={() => setMoreUser(item)}
                />
              </View>
            </View>
          </Card.Content>
        </Card>
      );
    },
    [
      batchSelecting,
      canManageUserBarcode,
      managedStoreCode,
      openStaffDetail,
      selectedUserGuids,
      t,
    ]
  );

  if (!canViewUsers) {
    return (
      <SafeAreaView style={styles.screen} edges={["top", "left", "right"]}>
        <EmptyState
          title={t("messages.noAccessTitle")}
          description={t("messages.noAccessDescription")}
          primaryAction={{
            label: t("common:actions.goToSettings"),
            icon: "cog-outline",
            onPress: () => router.navigate("/(shell)/settings"),
          }}
        />
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView style={styles.screen} edges={["top", "left", "right"]}>
      <FlatList
        data={filteredUsers}
        keyExtractor={(item) => item.userGUID}
        renderItem={renderUserCard}
        refreshControl={
          <RefreshControl refreshing={usersQuery.isFetching && !usersQuery.isLoading} onRefresh={handleRefresh} />
        }
        contentContainerStyle={styles.listContent}
        ListHeaderComponent={
          <View style={styles.headerWrap}>
            <View style={styles.titleRow}>
              <View>
                <Text variant="headlineSmall">{t("title")}</Text>
                <View style={styles.storeStatusRow}>
                  <Text variant="bodyMedium" style={styles.secondaryText}>{storeCaption}</Text>
                  {managedStoreCode ? (
                    <Text variant="labelSmall" style={isStoreManageable(managedStoreCode, manageableStores) ? styles.manageableText : styles.readOnlyHint}>
                      {isStoreManageable(managedStoreCode, manageableStores) ? t("currentStore.manageableBadge") : t("currentStore.viewOnlyBadge")}
                    </Text>
                  ) : null}
                </View>
              </View>
              {canCreateUsers ? (
                <Button
                  compact
                  mode="contained"
                  icon="account-plus-outline"
                  onPress={openCreateDialog}
                  disabled={!selectedStoreCanCreate || isBusy}
                >
                  {t("actions.create")}
                </Button>
              ) : null}
            </View>

            <View style={styles.filterPanel}>
              <Button
                mode="outlined"
                icon="store-outline"
                onPress={() => setStorePickerVisible(true)}
                contentStyle={styles.storePickerButtonContent}
              >
                {managedStore?.storeName || t("currentStore.allRelated")}
              </Button>
              <Searchbar
                placeholder={t("searchPlaceholder")}
                value={keywordInput}
                onChangeText={setKeywordInput}
                onIconPress={submitKeyword}
                onSubmitEditing={submitKeyword}
                style={styles.searchbar}
                inputStyle={styles.searchInput}
              />
              <SegmentedButtons
                value={statusFilter}
                onValueChange={(value) => setStatusFilter(value as StatusFilter)}
                buttons={[
                  { value: "all", label: t("filters.statusAllCount", { count: statusCounts.all }) },
                  { value: "active", label: t("filters.statusActiveCount", { count: statusCounts.active }) },
                  { value: "disabled", label: t("filters.statusDisabledCount", { count: statusCounts.disabled }) },
                ]}
                theme={{ colors: { secondaryContainer: "#E8F1FF", onSecondaryContainer: HB_COLORS.action } }}
              />
              {!isDeviceMode && canEditUsers && managedStoreCode && isStoreManageable(managedStoreCode, manageableStores) ? (
                <View style={styles.batchActions}>
                  <Button
                    compact
                    mode={batchSelecting ? "contained-tonal" : "outlined"}
                    icon="printer-outline"
                    onPress={() => {
                      setBatchSelecting((current) => !current);
                      setSelectedUserGuids(new Set());
                    }}
                  >
                    {batchSelecting ? t("staffBarcode.batch.cancelSelection") : t("staffBarcode.batch.entry")}
                  </Button>
                  {batchSelecting ? (
                    <Button
                      compact
                      mode="contained"
                      onPress={() => setBatchVisible(true)}
                      disabled={!selectedUsers.length}
                    >
                      {t("staffBarcode.batch.confirm", { count: selectedUsers.length })}
                    </Button>
                  ) : null}
                </View>
              ) : null}
              {managedStoreCode && !selectedStoreCanManageUsers ? (
                <Text variant="bodySmall" style={styles.readOnlyHint}>{t("currentStore.readOnlyHelper")}</Text>
              ) : null}
            </View>

            {usersQuery.isError ? (
              <EmptyState
                title={t("messages.loadFailedTitle")}
                description={resolveLocalizedErrorMessage(usersQuery.error, {
                  t,
                  language,
                  fallbackKey: "messages.loadFailedDescription",
                })}
                primaryAction={{
                  label: t("common:actions.retry"),
                  icon: "refresh",
                  onPress: () => void handleRefresh(),
                }}
              />
            ) : null}

            {!usersQuery.isLoading && !usersQuery.isError && filteredUsers.length === 0 ? (
              <EmptyState
                title={keyword ? t("messages.emptySearchTitle") : t("messages.emptyTitle")}
                description={
                  keyword ? t("messages.emptySearchDescription", { keyword }) : t("messages.emptyDescription")
                }
              />
            ) : null}
          </View>
        }
        ListFooterComponent={
          usersQuery.isLoading ? (
            <View style={styles.loadingWrap}>
              <ActivityIndicator />
            </View>
          ) : null
        }
      />

      <Portal>
        <Dialog visible={dialogVisible} onDismiss={resetDialogState}>
          <Dialog.Title>{editingUserGuid ? t("dialogs.editTitle") : t("dialogs.createTitle")}</Dialog.Title>
          <Dialog.ScrollArea>
            <ScrollView contentContainerStyle={styles.dialogContent}>
              <TextInput
                mode="outlined"
                label={t("fields.username")}
                value={formValues.username}
                onChangeText={(value) => setFormValues((current) => ({ ...current, username: value }))}
                autoCapitalize="none"
                disabled={Boolean(editingUserGuid) || isBusy}
              />
              {!editingUserGuid ? (
                <TextInput
                  mode="outlined"
                  label={t("fields.initialPassword")}
                  value={initialPassword}
                  onChangeText={setInitialPassword}
                  secureTextEntry
                  autoCapitalize="none"
                  disabled={isBusy}
                />
              ) : null}
              <TextInput
                mode="outlined"
                label={t("fields.fullName")}
                value={formValues.fullName}
                onChangeText={(value) => setFormValues((current) => ({ ...current, fullName: value }))}
                disabled={isBusy}
              />
              <TextInput
                mode="outlined"
                label={t("fields.email")}
                value={formValues.email}
                onChangeText={(value) => setFormValues((current) => ({ ...current, email: value }))}
                keyboardType="email-address"
                autoCapitalize="none"
                disabled={isBusy}
              />
              <TextInput
                mode="outlined"
                label={t("fields.phone")}
                value={formValues.phone}
                onChangeText={(value) => setFormValues((current) => ({ ...current, phone: value }))}
                keyboardType="phone-pad"
                disabled={isBusy}
              />
              <View style={styles.switchRow}>
                <Text variant="bodyLarge">{t("fields.enabled")}</Text>
                <Switch
                  value={formValues.status}
                  onValueChange={(value) => setFormValues((current) => ({ ...current, status: value }))}
                  disabled={isBusy}
                />
              </View>
              <Text variant="bodySmall" style={styles.secondaryText}>
                {t("fields.fixedRoleHint")}
              </Text>
              {editingUserGuid && detailQuery.isFetching ? (
                <View style={styles.inlineLoading}>
                  <ActivityIndicator />
                </View>
              ) : null}
            </ScrollView>
          </Dialog.ScrollArea>
          <Dialog.Actions>
            <Button onPress={resetDialogState} disabled={isBusy}>
              {t("actions.cancel")}
            </Button>
            <Button onPress={handleSubmit} loading={editingUserGuid ? updateMutation.isPending : createMutation.isPending}>
              {editingUserGuid ? t("actions.save") : t("actions.create")}
            </Button>
          </Dialog.Actions>
        </Dialog>

        <Dialog visible={resetPasswordVisible} onDismiss={closeResetPasswordDialog}>
          <Dialog.Title>{t("dialogs.resetPasswordTitle")}</Dialog.Title>
          <Dialog.Content style={styles.dialogContent}>
            <Text variant="bodyMedium">
              {t("dialogs.resetPasswordDescription", { username: passwordUser?.username ?? "" })}
            </Text>
            <TextInput
              mode="outlined"
              label={t("fields.newPassword")}
              value={resetPasswordValue}
              onChangeText={setResetPasswordValue}
              secureTextEntry
              autoCapitalize="none"
              disabled={passwordMutation.isPending}
            />
          </Dialog.Content>
          <Dialog.Actions>
            <Button onPress={closeResetPasswordDialog} disabled={passwordMutation.isPending}>
              {t("actions.cancel")}
            </Button>
            <Button onPress={handleResetPassword} loading={passwordMutation.isPending}>
              {t("actions.confirmReset")}
            </Button>
          </Dialog.Actions>
        </Dialog>

        <Dialog visible={Boolean(moreUser)} onDismiss={() => setMoreUser(null)}>
          <Dialog.Title>{moreUser?.fullName || moreUser?.username}</Dialog.Title>
          <Dialog.Content style={styles.moreActions}>
            <Button icon="account-details-outline" onPress={() => { if (moreUser) openStaffDetail(moreUser); setMoreUser(null); }}>
              {t("actions.viewDetails")}
            </Button>
            {moreAccessEligibility?.canOpen ? (
              <Button icon="shield-account-outline" onPress={() => { if (moreUser) openUserAccess(moreUser); setMoreUser(null); }}>
                {t("accessManagement.actions.open")}
              </Button>
            ) : null}
            <Button icon="pencil-outline" disabled={!moreUser || !canModifyUserStore(moreUser)} onPress={() => { if (moreUser) openEditDialog(moreUser); setMoreUser(null); }}>
              {t("actions.edit")}
            </Button>
            <Button icon="lock-reset" disabled={!moreUser || !canResetUserPassword(moreUser)} onPress={() => { if (moreUser) openResetPasswordDialog(moreUser); setMoreUser(null); }}>
              {t("actions.resetPassword")}
            </Button>
            <Button
              icon={moreUser?.status === 1 ? "pause-circle-outline" : "play-circle-outline"}
              disabled={!moreUser || !canModifyUserStore(moreUser)}
              onPress={() => { if (moreUser) handleToggleStatus(moreUser); setMoreUser(null); }}
            >
              {moreUser?.status === 1 ? t("actions.disable") : t("actions.enable")}
            </Button>
          </Dialog.Content>
        </Dialog>

        <Dialog visible={Boolean(createdUser)} onDismiss={() => setCreatedUser(null)} testID="staff-created-actions-dialog">
          <Dialog.Title>{t("staffBarcode.created.title")}</Dialog.Title>
          <Dialog.Content><Text>{t("staffBarcode.created.description", { name: createdUser?.fullName || createdUser?.username })}</Text></Dialog.Content>
          <Dialog.Actions>
            <Button onPress={() => setCreatedUser(null)}>{t("staffBarcode.actions.done")}</Button>
            {createdUser && canManageUserBarcode(createdUser) ? (
              <Button mode="contained" onPress={() => { setBarcodeUser(createdUser); setCreatedUser(null); }}>
                {t("staffBarcode.actions.createAndPrint")}
              </Button>
            ) : null}
          </Dialog.Actions>
        </Dialog>
      </Portal>

      <StaffBarcodeDialog
        actorGuid={actorGuid}
        storeCode={barcodeUser ? resolveUserStoreCode(barcodeUser) ?? "" : ""}
        user={barcodeUser}
        visible={Boolean(barcodeUser)}
        onDismiss={() => setBarcodeUser(null)}
      />
      {managedStoreCode ? (
        <StaffBarcodeBatchDialog
          actorGuid={actorGuid}
          storeCode={managedStoreCode}
          users={selectedUsers}
          visible={batchVisible}
          onDismiss={() => {
            setBatchVisible(false);
            setBatchSelecting(false);
            setSelectedUserGuids(new Set());
          }}
        />
      ) : null}

      <StorePickerModal
        visible={storePickerVisible}
        presentation="sheet"
        stores={posEnabledStores}
        selectedStoreCode={managedStoreCode}
        title={t("common:labels.selectStore")}
        cancelLabel={t("common:actions.cancel")}
        includeAllOption={!deviceBoundStoreCode}
        allLabel={t("currentStore.allRelated")}
        renderStoreLabel={renderStorePickerLabel}
        onDismiss={() => setStorePickerVisible(false)}
        onSelectStore={handleSelectManagedStore}
      />

      <Snackbar visible={Boolean(snackbarMessage)} onDismiss={() => setSnackbarMessage("")} duration={2500}>
        {snackbarMessage}
      </Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  batchActions: {
    alignItems: "center",
    flexDirection: "row",
    gap: 8,
    justifyContent: "flex-end",
  },
  activeChip: {
    backgroundColor: "#D1FAE5",
  },
  avatar: {
    backgroundColor: "#1256DB",
  },
  cardMenuActions: {
    alignItems: "center",
    flexDirection: "row",
    gap: 0,
  },
  codeAction: {
    alignItems: "center",
    justifyContent: "center",
    minHeight: 44,
    minWidth: 48,
  },
  codeActionText: {
    color: HB_COLORS.action,
    fontSize: 10,
  },
  dialogContent: {
    gap: 12,
    paddingBottom: 8,
  },
  filterPanel: {
    ...BUSINESS_UI.filterGroup,
    borderWidth: StyleSheet.hairlineWidth,
    gap: 8,
    padding: 8,
  },
  headerWrap: {
    gap: 8,
    marginBottom: 8,
  },
  identityRow: {
    alignItems: "center",
    flex: 1,
    flexDirection: "row",
    gap: 8,
  },
  inactiveChip: {
    backgroundColor: "#FEE2E2",
  },
  inlineLoading: {
    alignItems: "center",
    paddingVertical: 8,
  },
  listContent: {
    gap: 8,
    padding: HB_SPACING.sm,
    paddingBottom: 112,
  },
  loadingWrap: {
    alignItems: "center",
    paddingVertical: 24,
  },
  manageableChip: {
    backgroundColor: "#D1FAE5",
  },
  readOnlyHint: {
    color: "#B45309",
  },
  screen: {
    backgroundColor: HB_COLORS.background,
    flex: 1,
  },
  searchbar: {
    backgroundColor: "#F8FAFC",
    height: 44,
  },
  searchInput: {
    minHeight: 44,
  },
  secondaryText: {
    color: "#6B7280",
  },
  storePickerButtonContent: {
    justifyContent: "flex-start",
  },
  storePickerLabelRow: {
    alignItems: "center",
    flex: 1,
    flexDirection: "row",
    gap: 8,
    justifyContent: "space-between",
    minHeight: 48,
  },
  storePickerLabelText: {
    flex: 1,
    minWidth: 0,
  },
  storeStatusRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 6,
  },
  manageableText: {
    color: HB_COLORS.success,
  },
  switchRow: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
  },
  moreActions: {
    alignItems: "flex-start",
    gap: 2,
  },
  titleRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 12,
    justifyContent: "space-between",
  },
  userCard: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
  },
  userCardContent: {
    paddingVertical: 8,
  },
  userCardHeader: {
    alignItems: "center",
    flexDirection: "row",
    gap: 4,
    justifyContent: "space-between",
  },
  userTitleWrap: {
    flex: 1,
    gap: 2,
  },
  viewOnlyChip: {
    backgroundColor: "#FEF3C7",
  },
});

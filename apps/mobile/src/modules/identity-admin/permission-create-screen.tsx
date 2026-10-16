import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Alert, ScrollView, StyleSheet, View } from "react-native";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { usePreventRemove, type NavigationAction } from "@react-navigation/native";
import { useNavigation, useRouter } from "expo-router";
import { Button, Chip, Icon, IconButton, Snackbar, Text, TextInput } from "react-native-paper";
import { InfoCard, SectionTitle, StickyActionBar } from "@/modules/people";
import { HB_COLORS as C } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { createIdentitySysPermission, fetchIdentitySysPermissions, getIdentityAdminErrorMeta } from "./api";
import { AdminDetailShell } from "./admin-detail-shell";
import { ADMIN_MONO_FONT } from "./admin-visuals";
import { fillCopy, usePeopleRolesCopy } from "./people-roles-copy";
import { backToHubTab } from "./role-detail-logic";
import { isUncertainRoleWrite } from "./role-logic";
import {
  PERMISSION_ACTION_OPTIONS,
  createEmptyPermissionDraft,
  isPermissionCreationVerified,
  listPermissionCategoryOptions,
  previewGeneratedPermissions,
  toCreatePermissionInput,
  togglePermissionAction,
  validatePermissionDraft,
  type PermissionDraft,
} from "./permission-logic";
import { interpolatePermissionCopy as interpolate, permissionQueryKeys, usePermissionCopy, usePermissionItems, usePermissionSession } from "./permission-hooks";
import { IdentitySelectionSheet } from "./selection-sheet";
import { styles } from "./ui";

const NEW_CATEGORY_OPTION = "__new_category__";

function isDraftDirty(draft: PermissionDraft) {
  return Boolean(draft.code.trim() || draft.name.trim() || draft.category.trim() || draft.description.trim() || draft.actions.length);
}

export default function PermissionCreateScreen() {
  const { actorKey } = usePermissionSession();
  return <PermissionCreateContent key={actorKey} />;
}

function PermissionCreateContent() {
  const router = useRouter();
  const navigation = useNavigation();
  const queryClient = useQueryClient();
  const { copy, appLanguage } = usePermissionCopy();
  const p = usePeopleRolesCopy();
  const { allowed, actorKey, capabilities, isAuthenticated } = usePermissionSession();
  const [draft, setDraft] = useState<PermissionDraft>(() => createEmptyPermissionDraft());
  const [categoryMode, setCategoryMode] = useState<"existing" | "custom">("existing");
  const [categoryPickerOpen, setCategoryPickerOpen] = useState(false);
  const [writeForbidden, setWriteForbidden] = useState(false);
  const [allowRemove, setAllowRemove] = useState(false);
  const [notice, setNotice] = useState("");
  const pendingActionRef = useRef<NavigationAction | null>(null);
  // 在 POST 前锁定本次创建；即使响应不明确也不再发送第二次创建，避免重复落库。
  const createPostAttemptedRef = useRef(false);

  const canOpen = allowed && capabilities.canManage;
  const permissions = usePermissionItems({ actorKey, enabled: canOpen, appLanguage });
  const categoryOptions = useMemo(() => listPermissionCategoryOptions(permissions.items), [permissions.items]);
  const existingCodes = useMemo(() => permissions.items.map((item) => item.code), [permissions.items]);
  const errors = useMemo(() => validatePermissionDraft(draft, existingCodes), [draft, existingCodes]);
  const preview = useMemo(() => previewGeneratedPermissions(draft), [draft]);
  const dirty = isDraftDirty(draft);

  const assertCurrentSession = useCallback(() => {
    const state = useAuthStore.getState();
    if (
      !state.isAuthenticated
      || state.sessionKind !== "account"
      || state.iosReviewOfflineGuardActive
      || (state.user?.userGUID ?? "") !== actorKey
      || !state.access.hasPermission("Roles.ManagePermissions")
    ) {
      throw Object.assign(new Error("IDENTITY_PERMISSION_SESSION_CHANGED"), { status: 403 });
    }
  }, [actorKey]);
  const canApplyMutationResult = useCallback(() => {
    try {
      assertCurrentSession();
      return true;
    } catch {
      return false;
    }
  }, [assertCurrentSession]);

  const createMutation = useMutation({
    retry: false,
    mutationFn: async () => {
      assertCurrentSession();
      if (createPostAttemptedRef.current) throw new Error("PERMISSION_CREATE_RETRY_BLOCKED");
      createPostAttemptedRef.current = true;
      try {
        await createIdentitySysPermission(toCreatePermissionInput(draft), actorKey);
      } catch (error) {
        const status = getIdentityAdminErrorMeta(error).status;
        // 服务端明确拒绝（如代码重复）时允许修正后重试；结果不明确则保持锁定。
        if (!isUncertainRoleWrite(error) && ![401, 403].includes(status ?? 0)) createPostAttemptedRef.current = false;
        throw error;
      }
      assertCurrentSession();
      const latest = await fetchIdentitySysPermissions(actorKey);
      assertCurrentSession();
      if (!isPermissionCreationVerified(draft, latest.map((item) => item.code))) throw new Error("PERMISSION_CREATE_READBACK_MISMATCH");
      return latest;
    },
    onSuccess: async (latest) => {
      if (!canApplyMutationResult()) return;
      queryClient.setQueryData(permissionQueryKeys.sysPermissions(actorKey), latest);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: permissionQueryKeys.catalog(actorKey) }),
        queryClient.invalidateQueries({ queryKey: permissionQueryKeys.roleCounts(actorKey) }),
      ]);
      if (!canApplyMutationResult()) return;
      setAllowRemove(true);
    },
    onError: (error) => {
      const status = getIdentityAdminErrorMeta(error).status;
      if ([401, 403].includes(status ?? 0) || isUncertainRoleWrite(error)) {
        setWriteForbidden(true);
        setNotice([401, 403].includes(status ?? 0) ? copy.forbidden : copy.uncertainWrite);
      } else {
        const code = getIdentityAdminErrorMeta(error).code;
        setNotice(code === "PERMISSION_CODE_EXISTS" || code === "PERMISSION_EXISTS" ? copy.validationCodeExists : copy.saveFailed);
      }
    },
  });

  usePreventRemove(isAuthenticated && (dirty || createMutation.isPending) && !allowRemove, ({ data }) => {
    const session = useAuthStore.getState();
    if (!session.isAuthenticated || session.sessionKind !== "account" || session.iosReviewOfflineGuardActive || (session.user?.userGUID ?? "") !== actorKey) {
      // 登出或身份切换必须立即离开，不能被旧账号的脏草稿确认框拦住。
      pendingActionRef.current = data.action;
      setAllowRemove(true);
      return;
    }
    if (createMutation.isPending) {
      Alert.alert(copy.busyTitle, copy.busyMessage, [{ text: copy.confirm }]);
      return;
    }
    Alert.alert(copy.unsavedTitle, copy.unsavedMessage, [
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
    // 创建成功：单条直接进入详情页，批量生成则回到列表。
    const created = previewGeneratedPermissions(draft);
    if (created.length === 1 && created[0]) {
      router.replace({ pathname: "/(shell)/permissions/[code]", params: { code: created[0].code } });
    } else {
      backToHubTab(router, "permissions");
    }
  }, [allowRemove, draft, navigation, router]);

  const handleVerifyRefresh = useCallback(async () => {
    await permissions.refetch();
    if (!canApplyMutationResult()) return;
    // 创建请求结果不明确时无法安全判断是否已落库，刷新后仍保持冻结，禁止重复 POST；用户可查看列表确认。
    setNotice(copy.verificationFailed);
  }, [canApplyMutationResult, copy.verificationFailed, permissions]);

  const readOnly = createMutation.isPending || writeForbidden;
  const canSubmit = canOpen && !readOnly && Object.keys(errors).length === 0 && !createPostAttemptedRef.current;
  const codeError = errors.code === "required" ? copy.validationCodeRequired : errors.code === "tooLong" ? copy.validationCodeTooLong : errors.code === "invalid" ? copy.validationCodeInvalid : errors.code === "exists" ? copy.validationCodeExists : "";
  const nameError = errors.name === "required" ? copy.validationNameRequired : errors.name === "tooLong" ? copy.validationNameTooLong : "";
  const categoryError = errors.category === "required" ? copy.validationCategoryRequired : errors.category === "tooLong" ? copy.validationCategoryTooLong : "";
  const selectedCategoryLabel = categoryOptions.find((option) => option.key === draft.category)?.label;

  const handleBack = () => backToHubTab(router, "permissions");

  if (!canOpen) {
    return <AdminDetailShell title={copy.createTitle} onBack={handleBack}><View style={localStyles.message}><Text>{copy.accessDenied}</Text></View></AdminDetailShell>;
  }

  return (
    <AdminDetailShell
      title={copy.createTitle}
      onBack={handleBack}
      action={writeForbidden ? <IconButton icon="refresh" accessibilityLabel={copy.retry} onPress={() => void handleVerifyRefresh()} /> : undefined}
      footer={(
        <StickyActionBar summary={preview.length > 0 ? fillCopy(p.createSummary, { count: preview.length }) : p.createSummaryEmpty}>
          <Button mode="contained" style={localStyles.submit} contentStyle={localStyles.submitContent} disabled={!canSubmit} loading={createMutation.isPending} onPress={() => createMutation.mutate()}>{copy.createPermission}</Button>
        </StickyActionBar>
      )}
    >
      <View style={localStyles.page}>
        <ScrollView contentContainerStyle={localStyles.content} keyboardShouldPersistTaps="handled">
          <SectionTitle title={p.sectionBasic} />
          <InfoCard style={localStyles.formCard}>
            <TextInput mode="outlined" label={copy.code} placeholder={copy.codePlaceholder} value={draft.code} disabled={readOnly} maxLength={100} autoCapitalize="none" autoCorrect={false} contentStyle={localStyles.codeInput} error={Boolean(codeError) && Boolean(draft.code)} onChangeText={(code) => setDraft({ ...draft, code })} />
            <Text style={codeError && draft.code ? localStyles.errorText : localStyles.hint}>{codeError && draft.code ? codeError : copy.codeHint}</Text>
            <TextInput mode="outlined" label={copy.name} placeholder={copy.namePlaceholder} value={draft.name} disabled={readOnly} maxLength={100} error={Boolean(nameError) && Boolean(draft.name)} onChangeText={(name) => setDraft({ ...draft, name })} />
            {nameError && draft.name ? <Text style={localStyles.errorText}>{nameError}</Text> : null}
            <TextInput mode="outlined" label={copy.description} placeholder={copy.descriptionPlaceholder} value={draft.description} disabled={readOnly} multiline maxLength={500} error={Boolean(errors.description)} onChangeText={(description) => setDraft({ ...draft, description })} />
            {errors.description ? <Text style={localStyles.errorText}>{copy.validationDescriptionTooLong}</Text> : null}
          </InfoCard>

          <SectionTitle title={copy.category} />
          <InfoCard style={localStyles.formCard}>
            {categoryMode === "existing" ? (
              <Button mode="outlined" icon="chevron-down" contentStyle={localStyles.pickerContent} style={[styles.button, localStyles.picker]} disabled={readOnly} onPress={() => setCategoryPickerOpen(true)}>
                {selectedCategoryLabel ? `${selectedCategoryLabel}${selectedCategoryLabel !== draft.category ? ` · ${draft.category}` : ""}` : copy.categoryPlaceholder}
              </Button>
            ) : (
              <View style={localStyles.customCategory}>
                <TextInput mode="outlined" style={localStyles.flex} label={copy.categoryNew} placeholder={copy.categoryCustomInput} value={draft.category} disabled={readOnly} maxLength={50} autoCapitalize="none" autoCorrect={false} error={Boolean(categoryError) && Boolean(draft.category)} onChangeText={(category) => setDraft({ ...draft, category })} />
                <IconButton icon="format-list-bulleted" accessibilityLabel={copy.categoryChoose} disabled={readOnly} onPress={() => { setCategoryMode("existing"); setDraft({ ...draft, category: "" }); setCategoryPickerOpen(true); }} />
              </View>
            )}
            {categoryError && draft.category ? <Text style={localStyles.errorText}>{categoryError}</Text> : null}
          </InfoCard>

          <SectionTitle title={copy.batchActions} />
          <InfoCard style={localStyles.formCard}>
            <View style={localStyles.chips}>
              {PERMISSION_ACTION_OPTIONS.map((action) => {
                const selected = draft.actions.includes(action);
                return (
                  <Chip key={action} compact mode="outlined" selected={selected} showSelectedCheck={false} disabled={readOnly} icon={selected ? "check" : undefined}
                    style={[localStyles.chip, selected && localStyles.chipSelected]} textStyle={selected ? localStyles.chipTextSelected : undefined}
                    onPress={() => setDraft(togglePermissionAction(draft, action))}>{action}</Chip>
                );
              })}
            </View>
            <Text style={localStyles.hint}>{copy.batchHint}</Text>
          </InfoCard>

          {preview.length > 0 ? (
            <View style={localStyles.preview}>
              <Text style={styles.muted}>{interpolate(copy.previewTitle, { count: preview.length })}</Text>
              {preview.map((item) => (
                <View key={item.code} style={localStyles.previewRow}>
                  <Icon source="key-outline" size={14} color={C.textSecondary} />
                  <Text style={localStyles.previewCode}>{item.code}</Text>
                  <Text style={styles.muted} numberOfLines={1}>· {item.name}</Text>
                </View>
              ))}
            </View>
          ) : null}
        </ScrollView>

          {categoryPickerOpen ? (
          <IdentitySelectionSheet
            title={copy.categoryChoose}
            value={draft.category || undefined}
            options={[{ value: NEW_CATEGORY_OPTION, label: `+ ${copy.categoryNew}` }, ...categoryOptions.map((option) => ({ value: option.key, label: option.label === option.key ? option.key : `${option.label} · ${option.key}` }))]}
            onSelect={(value) => {
              setCategoryPickerOpen(false);
              if (value === NEW_CATEGORY_OPTION) {
                setCategoryMode("custom");
                setDraft({ ...draft, category: "" });
                return;
              }
              setCategoryMode("existing");
              setDraft({ ...draft, category: value });
            }}
            onDismiss={() => setCategoryPickerOpen(false)}
          />
        ) : null}
        <Snackbar visible={Boolean(notice)} onDismiss={() => setNotice("")} duration={5000}>{notice}</Snackbar>
      </View>
    </AdminDetailShell>
  );
}

const localStyles = StyleSheet.create({
  page: { flex: 1 },
  flex: { flex: 1 },
  message: { margin: 16, padding: 16, borderRadius: 8, backgroundColor: C.surfaceMuted },
  content: { gap: 8, padding: 16, paddingBottom: 24 },
  formCard: { gap: 12, padding: 14 },
  submit: { borderRadius: 8, minWidth: 140 },
  submitContent: { minHeight: 40 },
  // 权限代码是编码，输入时也用等宽字体，和列表、详情里的显示一致。
  codeInput: { fontFamily: ADMIN_MONO_FONT },
  hint: { fontSize: 12, lineHeight: 17, color: C.textSecondary, marginTop: -6 },
  errorText: { color: C.danger, fontSize: 12, marginTop: -6 },
  picker: { borderColor: C.outline },
  pickerContent: { flexDirection: "row-reverse", justifyContent: "space-between", minHeight: 44 },
  customCategory: { flexDirection: "row", alignItems: "center", gap: 8 },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: 6 },
  chip: { backgroundColor: C.white, borderColor: C.outline },
  chipSelected: { backgroundColor: "#EFF6FF", borderColor: C.brand },
  chipTextSelected: { color: C.action, fontWeight: "600" },
  preview: { padding: 12, borderRadius: 8, backgroundColor: C.surfaceMuted, gap: 6, marginTop: 4 },
  previewRow: { flexDirection: "row", alignItems: "center", gap: 6 },
  previewCode: { fontSize: 12, lineHeight: 16, color: C.textPrimary, fontFamily: ADMIN_MONO_FONT },
});

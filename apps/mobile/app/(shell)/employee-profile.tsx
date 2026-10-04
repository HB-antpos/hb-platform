import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Alert, AppState, Image, Pressable, RefreshControl, ScrollView, StyleSheet, useWindowDimensions, View } from "react-native";
import { useNavigation, usePreventRemove } from "@react-navigation/native";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useFocusEffect, useRouter } from "expo-router";
import {
  ActivityIndicator,
  Avatar,
  Button,
  Chip,
  HelperText,
  IconButton,
  ProgressBar,
  SegmentedButtons,
  Snackbar,
  Surface,
  Text,
  TextInput,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";

import { EmptyState } from "@/components/ui/EmptyState";
import {
  deleteEmployeeProfileImageApi,
  getMyEmployeeProfileApi,
  getMySensitiveChangeHistoryApi,
  getMySensitiveChangeRequestApi,
  updateMyEmployeeProfileApi,
  upsertMySensitiveChangeRequestApi,
  withdrawMySensitiveChangeRequestApi,
} from "@/modules/employee-profile/api";
import { AvatarEditorField } from "@/modules/employee-profile/AvatarEditorField";
import { BirthdayPickerField } from "@/modules/employee-profile/BirthdayPickerField";
import { calculateAge, normalizeBirthday, validateBirthday } from "@/modules/employee-profile/birthday";
import {
  getEmployeeProfileQueryKey,
  getEmployeeSensitiveChangeQueryKey,
  getEmployeeSensitiveHistoryQueryKey,
  resolveEmployeeProfileIdentity,
  shouldResetEmployeeProfileDraft,
} from "@/modules/employee-profile/cache-keys";
import {
  PROFILE_COMPLETENESS_LABEL_KEYS,
  getProfileCompleteness,
} from "@/modules/employee-profile/profile-completeness";
import {
  buildSensitiveReviewTimeline,
  canResubmitSensitiveRequest,
  canWithdrawSensitiveRequest,
  getEarlierSensitiveRequests,
  getResubmitSection,
  type ReviewTimelineStepState,
} from "@/modules/employee-profile/review-progress";
import { ProfileSummaryRow } from "@/modules/employee-profile/ProfileSummaryRow";
import {
  buildSensitiveReviewPayload,
  getBackAction,
  getSensitiveSectionOrder,
  getSensitiveSubmitFailureAction,
  hasBasicProfileChanges,
  hasSensitiveProfileChanges,
  shouldApplyEmployeeProfileOperation,
  shouldUnlockSensitiveConflict,
  type EmployeeProfileView,
  type SensitiveProfileSection,
} from "@/modules/employee-profile/profile-screen-state";
import { syncEmployeeProfileDraft, toEmployeeProfileDraft } from "@/modules/employee-profile/profile-draft";
import {
  buildNonSensitiveProfilePayload,
  getChangedSensitiveFields,
  getSensitiveAccountSummary,
  getSensitiveStatusView,
  isEmailChangeValid,
  isSensitiveVersionConflict,
  isValidEmail,
  refreshEmployeeProfileAfterIdentityMutation,
  selectSensitiveDraft,
  shouldRefreshSensitiveProfile,
  shouldShowPendingIdentityPhotoRemoval,
  submitSensitiveProfileWithCache,
} from "@/modules/employee-profile/sensitive-profile";
import { EmployeeProfileImageUploadError, uploadEmployeeProfileImage } from "@/modules/employee-profile/image-upload";
import {
  EMPLOYMENT_TYPES,
  GENDERS,
  type EmployeeProfile,
  type EmployeeProfileSensitiveChangeStatus,
  type SensitiveEmployeeProfilePayload,
  type UpdateEmployeeProfilePayload,
} from "@/modules/employee-profile/types";
import {
  getIdentityPhotoRefetchDelay,
  shouldRefreshIdentityPhotoAfterLoadError,
} from "@/modules/employee-profile/identity-photo-expiry";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { resolveLocaleTag } from "@/shared/i18n/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { PERMISSIONS } from "@/shared/utils/access";
import { useAuthStore } from "@/store/auth-store";

const PROFILE_BLUE = "#1256DB";
const EMPTY_FORM: UpdateEmployeeProfilePayload = {
  phone: "",
  email: "",
  gender: "",
  employmentType: "",
  address: "",
};
const EMPTY_SENSITIVE_FORM: SensitiveEmployeeProfilePayload = {
  birthday: "",
  bankBsb: "",
  bankAccountNumber: "",
  superannuationCompanyName: "",
  superannuationCompanyCode: "",
  superannuationAccountNumber: "",
  identityType: "",
  identityId: "",
};

function formatDateTime(value: string | undefined, locale: string) {
  if (!value) return null;
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value;
  return new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(parsed);
}

// 审核状态与时间线配色：待审蓝、批准绿、驳回红、撤回/失效灰。
const STATUS_TONES: Record<EmployeeProfileSensitiveChangeStatus, { background: string; text: string }> = {
  Pending: { background: "#EAF1FF", text: PROFILE_BLUE },
  Approved: { background: "#ECFDF3", text: HB_COLORS.success },
  Rejected: { background: "#FEF3F2", text: HB_COLORS.danger },
  Withdrawn: { background: HB_COLORS.surfaceMuted, text: HB_COLORS.textSecondary },
  Superseded: { background: HB_COLORS.surfaceMuted, text: HB_COLORS.textSecondary },
};
const TIMELINE_DOT_COLORS: Record<ReviewTimelineStepState, string> = {
  done: HB_COLORS.success,
  current: PROFILE_BLUE,
  upcoming: HB_COLORS.outlineMuted,
  failed: HB_COLORS.danger,
  stopped: HB_COLORS.textSecondary,
};

function getInitials(value: string) {
  const words = value.trim().split(/\s+/).filter(Boolean);
  if (!words.length) return "?";
  if (words.length === 1) return words[0].slice(0, 2).toUpperCase();
  return `${words[0][0]}${words[1][0]}`.toUpperCase();
}

export default function EmployeeProfileScreen() {
  const router = useRouter();
  const navigation = useNavigation();
  const queryClient = useQueryClient();
  const { t, language } = useAppTranslation(["employeeProfile", "common"]);
  const { fontScale } = useWindowDimensions();
  const user = useAuthStore((state) => state.user);
  const access = useAuthStore((state) => state.access);
  const canEditPositionType =
    access.hasPermission(PERMISSIONS.EmployeeProfiles.Edit) &&
    access.hasPermission(PERMISSIONS.EmployeeProfiles.EditPositionType);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const [view, setView] = useState<EmployeeProfileView>("overview");
  const [avatarEditing, setAvatarEditing] = useState(false);
  const [activeSensitiveSection, setActiveSensitiveSection] = useState<SensitiveProfileSection>("banking");
  const [storedFormValues, setFormValues] = useState<UpdateEmployeeProfilePayload>(EMPTY_FORM);
  const [sensitiveFormValues, setSensitiveFormValues] = useState<SensitiveEmployeeProfilePayload>(EMPTY_SENSITIVE_FORM);
  const initialSensitiveDraftRef = useRef<SensitiveEmployeeProfilePayload>(EMPTY_SENSITIVE_FORM);
  const sensitiveEditRevisionRef = useRef<number | undefined>(undefined);
  const [snackbarMessage, setSnackbarMessage] = useState("");
  const [snackbarVisible, setSnackbarVisible] = useState(false);
  const [savingImageKind, setSavingImageKind] = useState<"avatar" | "identityPhoto" | null>(null);
  const [sensitiveConflictRefreshing, setSensitiveConflictRefreshing] = useState(false);
  const [allowRemove, setAllowRemove] = useState(false);
  const pendingActionRef = useRef<Parameters<typeof navigation.dispatch>[0] | null>(null);
  const formInitializedRef = useRef(false);
  const formIdentityRef = useRef("");
  const formalIdentityPhotoErrorUrlRef = useRef("");
  const pendingIdentityPhotoErrorUrlRef = useRef("");
  const saveMutationIdentityRef = useRef("");
  const saveMutationScopeRef = useRef(-1);
  const sensitiveMutationIdentityRef = useRef("");
  const sensitiveMutationScopeRef = useRef(-1);
  const sensitiveConflictIdentityRef = useRef("");
  const sensitiveConflictScopeRef = useRef(-1);
  const identityScopeRef = useRef({ identity: "", generation: 0 });
  const imageOperationSequenceRef = useRef(0);
  const imageOperationRef = useRef<{
    sequence: number;
    identity: string;
    scope: number;
    kind: "avatar" | "identityPhoto";
  } | null>(null);

  const getErrorMessage = useCallback((error: unknown, fallbackKey: string) => (
    resolveLocalizedErrorMessage(error, { language, t, fallbackKey })
  ), [language, t]);
  const showMessage = useCallback((message: string) => {
    setSnackbarMessage(message);
    setSnackbarVisible(true);
  }, []);
  const locale = useMemo(() => resolveLocaleTag(language), [language]);
  const userIdentity = resolveEmployeeProfileIdentity(user);
  if (identityScopeRef.current.identity !== userIdentity) {
    // 同名账号退出再登录也属于新作用域，旧请求不得在敏感缓存清理后重新注入。
    identityScopeRef.current = {
      identity: userIdentity,
      generation: identityScopeRef.current.generation + 1,
    };
  }
  const currentIdentityScope = identityScopeRef.current.generation;
  const formValues = formIdentityRef.current === userIdentity ? storedFormValues : EMPTY_FORM;
  const profileQueryKey = useMemo(() => getEmployeeProfileQueryKey(userIdentity), [userIdentity]);
  const sensitiveQueryKey = useMemo(() => getEmployeeSensitiveChangeQueryKey(userIdentity), [userIdentity]);
  const historyQueryKey = useMemo(() => getEmployeeSensitiveHistoryQueryKey(userIdentity), [userIdentity]);
  const isOperationCurrent = useCallback((submittedIdentity: string, submittedScope: number) => {
    const auth = useAuthStore.getState();
    return shouldApplyEmployeeProfileOperation({
      submittedIdentity,
      currentIdentity: resolveEmployeeProfileIdentity(auth.user),
      submittedScope,
      currentScope: identityScopeRef.current.generation,
      isAuthenticated: auth.isAuthenticated,
    });
  }, []);

  const profileQuery = useQuery({
    queryKey: profileQueryKey,
    queryFn: getMyEmployeeProfileApi,
    enabled: Boolean(isAuthenticated && user),
    refetchInterval: (query) => getIdentityPhotoRefetchDelay(
      (query.state.data as EmployeeProfile | undefined)?.identityPhotoUrlExpiresAt
    ),
  });
  const sensitiveQuery = useQuery({
    queryKey: sensitiveQueryKey,
    queryFn: getMySensitiveChangeRequestApi,
    enabled: Boolean(isAuthenticated && user),
    refetchInterval: (query) => getIdentityPhotoRefetchDelay(query.state.data?.identityPhotoUrlExpiresAt),
  });
  // 历史只含状态与字段标识，仅在审核进度页加载。
  const historyQuery = useQuery({
    queryKey: historyQueryKey,
    queryFn: () => getMySensitiveChangeHistoryApi(20),
    enabled: Boolean(isAuthenticated && user) && view === "progress",
  });
  const isBasicDirty = useMemo(
    () => hasBasicProfileChanges(formValues, profileQuery.data, { canEditPositionType }),
    [canEditPositionType, formValues, profileQuery.data]
  );
  const isSensitiveDirty = useMemo(
    () => hasSensitiveProfileChanges(sensitiveFormValues, initialSensitiveDraftRef.current),
    [sensitiveFormValues]
  );
  const hasUnsavedDraft = view === "basic" ? isBasicDirty : view === "sensitive" && isSensitiveDirty;
  const isSavingCurrentIdentity = saveMutationIdentityRef.current === userIdentity
    && saveMutationScopeRef.current === currentIdentityScope;
  const isSubmittingSensitiveCurrentIdentity = sensitiveMutationIdentityRef.current === userIdentity
    && sensitiveMutationScopeRef.current === currentIdentityScope;
  const currentSavingImageKind = imageOperationRef.current?.identity === userIdentity
    && imageOperationRef.current.scope === currentIdentityScope
    ? savingImageKind
    : null;
  const isSensitiveConflictRefreshingCurrent = sensitiveConflictIdentityRef.current === userIdentity
    && sensitiveConflictScopeRef.current === currentIdentityScope
    && sensitiveConflictRefreshing;
  const canEditSensitive = !sensitiveQuery.isLoading
    && !sensitiveQuery.isError
    && !profileQuery.isError
    && !isSensitiveConflictRefreshingCurrent;
  const usesLargeTextLayout = fontScale > 1.2;

  const handleNavigateBack = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.navigate("/(shell)/settings");
  }, [router]);

  const resetEditor = useCallback(() => {
    if (view === "basic" && profileQuery.data) setFormValues(toEmployeeProfileDraft(profileQuery.data));
    if (view === "sensitive") {
      setSensitiveFormValues(initialSensitiveDraftRef.current);
      sensitiveEditRevisionRef.current = undefined;
    }
    setView("overview");
  }, [profileQuery.data, view]);

  const confirmDiscard = useCallback((onDiscard: () => void) => {
    Alert.alert(t("unsaved.title"), t("unsaved.description"), [
      { text: t("common:actions.cancel"), style: "cancel" },
      { text: t("unsaved.discard"), style: "destructive", onPress: onDiscard },
    ]);
  }, [t]);

  const handleHeaderBack = useCallback(() => {
    const action = getBackAction({ view, hasUnsavedChanges: hasUnsavedDraft });
    if (action === "navigate") handleNavigateBack();
    else if (action === "show-overview") resetEditor();
    else confirmDiscard(resetEditor);
  }, [confirmDiscard, handleNavigateBack, hasUnsavedDraft, resetEditor, view]);

  usePreventRemove(isAuthenticated && hasUnsavedDraft && !allowRemove, ({ data }) => {
    if (!useAuthStore.getState().isAuthenticated) {
      // 登录失效时优先放行认证导航，不能让未保存提醒困住用户。
      pendingActionRef.current = data.action;
      setAllowRemove(true);
      return;
    }
    confirmDiscard(() => {
      pendingActionRef.current = data.action;
      setAllowRemove(true);
    });
  });

  useEffect(() => {
    if (!allowRemove || !pendingActionRef.current) return;
    const action = pendingActionRef.current;
    pendingActionRef.current = null;
    navigation.dispatch(action);
  }, [allowRemove, navigation]);

  useEffect(() => {
    if (isAuthenticated && user) return;
    setAllowRemove(true);
    showMessage(t("messages.loginRequired"));
    router.navigate("/(shell)/settings");
  }, [isAuthenticated, router, showMessage, t, user]);

  useEffect(() => {
    if (!shouldResetEmployeeProfileDraft(formIdentityRef.current, userIdentity)) return;
    // 账号变化时清空旧账号的普通和敏感草稿，防止跨账号显示私密资料。
    formIdentityRef.current = userIdentity;
    formInitializedRef.current = false;
    setFormValues(EMPTY_FORM);
    setSensitiveFormValues(EMPTY_SENSITIVE_FORM);
    initialSensitiveDraftRef.current = EMPTY_SENSITIVE_FORM;
    setAvatarEditing(false);
    setSnackbarVisible(false);
    setSnackbarMessage("");
    setView("overview");
  }, [userIdentity]);

  const refreshProfileQueries = useCallback(async () => {
    if (!shouldRefreshSensitiveProfile("manual", isAuthenticated, AppState.currentState)) return;
    await Promise.all([
      profileQuery.refetch(),
      sensitiveQuery.refetch(),
      view === "progress" ? historyQuery.refetch() : Promise.resolve(),
    ]);
  }, [historyQuery, isAuthenticated, profileQuery, sensitiveQuery, view]);

  const refreshAfterSensitiveConflict = useCallback(async (
    conflictIdentity: string,
    conflictScope: number
  ) => {
    if (!isOperationCurrent(conflictIdentity, conflictScope)) return;
    sensitiveConflictIdentityRef.current = conflictIdentity;
    sensitiveConflictScopeRef.current = conflictScope;
    setSensitiveConflictRefreshing(true);
    if (!shouldRefreshSensitiveProfile("manual", isAuthenticated, AppState.currentState)) return;
    try {
      const [profileResult] = await Promise.all([
        profileQuery.refetch(),
        sensitiveQuery.refetch(),
      ]);
      // 只有正式资料权威刷新成功才能解除门禁；否则重新进入仍会携带旧 revision。
      if (
        shouldUnlockSensitiveConflict(profileResult)
        && sensitiveConflictIdentityRef.current === conflictIdentity
        && sensitiveConflictScopeRef.current === conflictScope
        && isOperationCurrent(conflictIdentity, conflictScope)
      ) {
        setSensitiveConflictRefreshing(false);
      }
    } catch {
      // 保持门禁，概览中的重试操作继续可用。
    }
  }, [isAuthenticated, isOperationCurrent, profileQuery, sensitiveQuery]);

  const handleManualRefresh = useCallback(async () => {
    if (isSensitiveConflictRefreshingCurrent) {
      await refreshAfterSensitiveConflict(userIdentity, currentIdentityScope);
      return;
    }
    await refreshProfileQueries();
  }, [
    isSensitiveConflictRefreshingCurrent,
    refreshAfterSensitiveConflict,
    refreshProfileQueries,
    currentIdentityScope,
    userIdentity,
  ]);

  useFocusEffect(useCallback(() => {
    if (!shouldRefreshSensitiveProfile("focus", isAuthenticated, AppState.currentState)) return;
    void queryClient.invalidateQueries({ queryKey: profileQueryKey });
    void queryClient.invalidateQueries({ queryKey: sensitiveQueryKey });
    void queryClient.invalidateQueries({ queryKey: historyQueryKey });
  }, [historyQueryKey, isAuthenticated, profileQueryKey, queryClient, sensitiveQueryKey]));

  useEffect(() => {
    const subscription = AppState.addEventListener("change", (state) => {
      if (shouldRefreshSensitiveProfile("app-active", isAuthenticated, state)) {
        void queryClient.invalidateQueries({ queryKey: profileQueryKey });
        void queryClient.invalidateQueries({ queryKey: sensitiveQueryKey });
      }
    });
    return () => subscription.remove();
  }, [isAuthenticated, profileQueryKey, queryClient, sensitiveQueryKey]);

  useEffect(() => {
    if (!profileQuery.data) return;
    const wasInitialized = formInitializedRef.current;
    formInitializedRef.current = true;
    setFormValues((current) => syncEmployeeProfileDraft(current, profileQuery.data!, wasInitialized));
  }, [profileQuery.data]);

  const saveMutation = useMutation({
    mutationFn: ({ payload }: {
      identity: string;
      scope: number;
      payload: UpdateEmployeeProfilePayload;
    }) => updateMyEmployeeProfileApi(payload),
    onSuccess: (profile, variables) => {
      if (!isOperationCurrent(variables.identity, variables.scope)) return;
      queryClient.setQueryData(getEmployeeProfileQueryKey(variables.identity), profile);
      setFormValues(toEmployeeProfileDraft(profile));
      setView("overview");
      showMessage(t("messages.saveSuccess"));
    },
    onError: (error, variables) => {
      if (isOperationCurrent(variables.identity, variables.scope)) {
        showMessage(getErrorMessage(error, "messages.saveFailed"));
      }
    },
  });

  const sensitiveMutation = useMutation({
    mutationFn: ({ identity, scope, payload }: {
      identity: string;
      scope: number;
      payload: SensitiveEmployeeProfilePayload;
    }) => {
      const submittedQueryKey = getEmployeeSensitiveChangeQueryKey(identity);
      return submitSensitiveProfileWithCache(payload, {
        cancelRequestQuery: () => queryClient.cancelQueries({ queryKey: submittedQueryKey }),
        submitRequest: upsertMySensitiveChangeRequestApi,
        setRequestData: (request) => {
          if (isOperationCurrent(identity, scope)) queryClient.setQueryData(submittedQueryKey, request);
        },
        refreshRequestQuery: () => isOperationCurrent(identity, scope)
          ? queryClient.invalidateQueries({ queryKey: submittedQueryKey, refetchType: "active" })
          : Promise.resolve(),
      });
    },
    onSuccess: (_request, variables) => {
      if (!isOperationCurrent(variables.identity, variables.scope)) return;
      void queryClient.invalidateQueries({ queryKey: getEmployeeSensitiveHistoryQueryKey(variables.identity) });
      // 提交后直接进入审核进度页，员工能立刻看到时间线与本次变更。
      setView("progress");
      showMessage(t("messages.sensitiveSubmitSuccess"));
    },
    onError: (error, variables) => {
      if (!isOperationCurrent(variables.identity, variables.scope)) return;
      if (getSensitiveSubmitFailureAction(error) === "discard-stale-edit") {
        // 冲突后废弃旧 revision，刷新完成前不允许再次进入，避免确定性 409 循环。
        sensitiveEditRevisionRef.current = undefined;
        setSensitiveFormValues(EMPTY_SENSITIVE_FORM);
        initialSensitiveDraftRef.current = EMPTY_SENSITIVE_FORM;
        setView("overview");
        sensitiveConflictIdentityRef.current = variables.identity;
        sensitiveConflictScopeRef.current = variables.scope;
        setSensitiveConflictRefreshing(true);
        void refreshAfterSensitiveConflict(variables.identity, variables.scope);
        showMessage(t("messages.versionConflict"));
        return;
      }
      // 失败时留在编辑页并保留完整草稿，用户可修正后直接重试。
      showMessage(getErrorMessage(error, "messages.sensitiveSubmitFailed"));
    },
  });
  const withdrawMutation = useMutation({
    mutationFn: ({ requestId }: { identity: string; scope: number; requestId: number }) =>
      withdrawMySensitiveChangeRequestApi(requestId),
    onSuccess: (request, variables) => {
      if (!isOperationCurrent(variables.identity, variables.scope)) return;
      queryClient.setQueryData(getEmployeeSensitiveChangeQueryKey(variables.identity), request);
      void queryClient.invalidateQueries({ queryKey: getEmployeeSensitiveHistoryQueryKey(variables.identity) });
      showMessage(t("progress.withdrawSuccess"));
    },
    onError: (error, variables) => {
      if (!isOperationCurrent(variables.identity, variables.scope)) return;
      if (isSensitiveVersionConflict(error)) {
        // 409：申请已被审核或覆盖，刷新为服务端最新状态，不再重试旧申请编号。
        void queryClient.invalidateQueries({ queryKey: getEmployeeSensitiveChangeQueryKey(variables.identity) });
        void queryClient.invalidateQueries({ queryKey: getEmployeeSensitiveHistoryQueryKey(variables.identity) });
        showMessage(t("progress.withdrawConflict"));
        return;
      }
      showMessage(getErrorMessage(error, "progress.withdrawFailed"));
    },
  });
  const withdrawPendingForCurrentIdentity = withdrawMutation.isPending
    && withdrawMutation.variables?.identity === userIdentity
    && withdrawMutation.variables?.scope === currentIdentityScope;
  const savePendingForCurrentIdentity = saveMutation.isPending && isSavingCurrentIdentity;
  const sensitivePendingForCurrentIdentity = sensitiveMutation.isPending
    && isSubmittingSensitiveCurrentIdentity;

  const readonlyUsername = profileQuery.data?.username || user?.username || "";
  const readonlyDisplayName = profileQuery.data?.displayName || user?.fullName || t("common:na");
  const updatedAtText = formatDateTime(profileQuery.data?.updatedAt, locale) || t("common:na");
  const sensitiveStatus = getSensitiveStatusView(sensitiveQuery.data);
  const sensitiveChangedFields = useMemo(() => {
    if (!profileQuery.data || !sensitiveQuery.data) return [];
    return sensitiveQuery.data.changedFields.length
      ? sensitiveQuery.data.changedFields
      : getChangedSensitiveFields(profileQuery.data, selectSensitiveDraft(profileQuery.data, sensitiveQuery.data));
  }, [profileQuery.data, sensitiveQuery.data]);
  const formalBirthday = normalizeBirthday(profileQuery.data?.birthday);
  const formatBirthdayWithAge = useCallback((value: string) => {
    const age = calculateAge(value);
    return age === null ? value : t("birthday.withAge", { date: value, age });
  }, [t]);
  const birthdayError = validateBirthday(sensitiveFormValues.birthday);
  const isBirthdayChanged = sensitiveFormValues.birthday.trim() !== formalBirthday;
  // 「已修改」标记与提交按钮的变更项数都以正式资料为基线，与后端固化的 ChangedFields 口径一致。
  const draftChangedFields = useMemo(
    () => new Set<string>(profileQuery.data ? getChangedSensitiveFields(profileQuery.data, sensitiveFormValues) : []),
    [profileQuery.data, sensitiveFormValues]
  );
  const completeness = useMemo(() => getProfileCompleteness(profileQuery.data), [profileQuery.data]);
  const pendingIdentityPhotoRemoval = sensitiveQuery.data?.status === "Pending" && shouldShowPendingIdentityPhotoRemoval({
    changedFields: sensitiveChangedFields,
    pendingHasIdentityPhoto: sensitiveQuery.data.hasIdentityPhoto,
    formalHasIdentityPhoto: Boolean(profileQuery.data?.identityPhotoUrl),
  });

  const setFieldValue = useCallback(<K extends keyof UpdateEmployeeProfilePayload>(
    key: K,
    value: UpdateEmployeeProfilePayload[K]
  ) => setFormValues((current) => ({ ...current, [key]: value })), []);
  const setSensitiveFieldValue = useCallback(<K extends keyof SensitiveEmployeeProfilePayload>(
    key: K,
    value: SensitiveEmployeeProfilePayload[K]
  ) => setSensitiveFormValues((current) => ({ ...current, [key]: value })), []);

  const handleStartSensitiveEdit = (section: SensitiveProfileSection) => {
    if (!profileQuery.data) return;
    const draft = selectSensitiveDraft(profileQuery.data, sensitiveQuery.data);
    setSensitiveFormValues(draft);
    initialSensitiveDraftRef.current = draft;
    // revision 固定取自进入编辑页时的正式资料，后台刷新不得改变并发基线。
    sensitiveEditRevisionRef.current = profileQuery.data.sensitiveRevision;
    setActiveSensitiveSection(section);
    setView("sensitive");
  };

  const handleWithdraw = () => {
    const request = sensitiveQuery.data;
    if (!canWithdrawSensitiveRequest(request) || !request) return;
    const submittedIdentity = userIdentity;
    const submittedScope = currentIdentityScope;
    Alert.alert(t("progress.withdrawTitle"), t("progress.withdrawDescription"), [
      { text: t("common:actions.cancel"), style: "cancel" },
      {
        text: t("progress.withdraw"),
        style: "destructive",
        onPress: () => withdrawMutation.mutate({
          identity: submittedIdentity,
          scope: submittedScope,
          requestId: request.requestId,
        }),
      },
    ]);
  };

  const handleResubmit = () => {
    if (!canEditSensitive) return;
    handleStartSensitiveEdit(getResubmitSection(sensitiveChangedFields));
  };

  const handleGoToBirthdayReview = () => {
    const openBirthdayEditor = () => {
      if (profileQuery.data) setFormValues(toEmployeeProfileDraft(profileQuery.data));
      handleStartSensitiveEdit("personal");
    };
    if (isBasicDirty) confirmDiscard(openBirthdayEditor);
    else openBirthdayEditor();
  };

  const handleSave = async () => {
    if (!isEmailChangeValid(profileQuery.data?.email, formValues.email)) {
      showMessage(t("messages.invalidEmail"));
      return;
    }
    const submittedIdentity = userIdentity;
    const submittedScope = currentIdentityScope;
    saveMutationIdentityRef.current = submittedIdentity;
    saveMutationScopeRef.current = submittedScope;
    try {
      await saveMutation.mutateAsync({
        identity: submittedIdentity,
        scope: submittedScope,
        payload: buildNonSensitiveProfilePayload(formValues, { canEditPositionType, initialEmail: profileQuery.data?.email }),
      });
    } catch {
      // mutation 回调展示错误；当前草稿继续保留。
    }
  };
  const handleSensitiveSubmit = async () => {
    if (birthdayError) {
      showMessage(t(`birthday.errors.${birthdayError}`));
      return;
    }
    const submittedIdentity = userIdentity;
    const submittedScope = currentIdentityScope;
    sensitiveMutationIdentityRef.current = submittedIdentity;
    sensitiveMutationScopeRef.current = submittedScope;
    try {
      await sensitiveMutation.mutateAsync({
        identity: submittedIdentity,
        scope: submittedScope,
        payload: buildSensitiveReviewPayload(
          sensitiveFormValues,
          sensitiveEditRevisionRef.current
        ),
      });
    } catch {
      // mutation 回调统一展示错误，避免敏感值进入日志。
    }
  };

  const handleSaveImage = async (
    kind: "avatar" | "identityPhoto",
    image: { uri: string; fileName: string; contentType: "image/jpeg"; fileSize: number }
  ) => {
    const operation = {
      sequence: ++imageOperationSequenceRef.current,
      identity: userIdentity,
      scope: currentIdentityScope,
      kind,
    };
    imageOperationRef.current = operation;
    setSavingImageKind(kind);
    try {
      const profile = await uploadEmployeeProfileImage({ kind, ...image });
      if (!isOperationCurrent(operation.identity, operation.scope)) return;
      if (kind === "identityPhoto") {
        const refreshResult = await refreshEmployeeProfileAfterIdentityMutation({
          refetchSensitive: sensitiveQuery.refetch,
          refetchFormal: profileQuery.refetch,
        });
        if (isOperationCurrent(operation.identity, operation.scope)) {
          showMessage(t(refreshResult.isError ? "messages.identityStatusRefreshFailed" : "messages.identitySubmitSuccess"));
        }
      } else {
        queryClient.setQueryData(getEmployeeProfileQueryKey(operation.identity), profile);
        setAvatarEditing(false);
        showMessage(t("messages.uploadSuccess"));
      }
    } catch (error) {
      if (isOperationCurrent(operation.identity, operation.scope)) {
        showMessage(error instanceof EmployeeProfileImageUploadError && error.code === "signature_unavailable"
          ? t("messages.uploadNotAvailable")
          : getErrorMessage(error, "messages.uploadFailed"));
      }
      throw error;
    } finally {
      // 旧账号的晚到 finally 不能清除新账号正在执行的图片操作。
      if (imageOperationRef.current?.sequence === operation.sequence) {
        imageOperationRef.current = null;
        setSavingImageKind(null);
      }
    }
  };

  const handleDeleteImage = async (kind: "avatar" | "identityPhoto") => {
    const operation = {
      sequence: ++imageOperationSequenceRef.current,
      identity: userIdentity,
      scope: currentIdentityScope,
      kind,
    };
    imageOperationRef.current = operation;
    setSavingImageKind(kind);
    try {
      const profile = await deleteEmployeeProfileImageApi(kind);
      if (!isOperationCurrent(operation.identity, operation.scope)) return;
      if (kind === "identityPhoto") {
        const refreshResult = await refreshEmployeeProfileAfterIdentityMutation({
          refetchSensitive: sensitiveQuery.refetch,
          refetchFormal: profileQuery.refetch,
        });
        if (isOperationCurrent(operation.identity, operation.scope)) {
          showMessage(t(refreshResult.isError ? "messages.identityStatusRefreshFailed" : "messages.identityRemoveSubmitSuccess"));
        }
      } else {
        queryClient.setQueryData(getEmployeeProfileQueryKey(operation.identity), profile);
        setAvatarEditing(false);
        showMessage(t("messages.imageRemoved"));
      }
    } catch (error) {
      if (isOperationCurrent(operation.identity, operation.scope)) {
        showMessage(getErrorMessage(error, "messages.removeImageFailed"));
      }
      throw error;
    } finally {
      if (imageOperationRef.current?.sequence === operation.sequence) {
        imageOperationRef.current = null;
        setSavingImageKind(null);
      }
    }
  };

  if (!isAuthenticated || !user) {
    return (
      <SafeAreaView style={styles.container}>
        <View style={styles.centered}><ActivityIndicator size="large" /></View>
        <Snackbar visible={snackbarVisible} onDismiss={() => setSnackbarVisible(false)}>{snackbarMessage}</Snackbar>
      </SafeAreaView>
    );
  }
  if (profileQuery.isLoading && !profileQuery.data) {
    return <SafeAreaView style={styles.container}><View style={styles.centered}><ActivityIndicator size="large" /></View></SafeAreaView>;
  }
  if (profileQuery.isError && !profileQuery.data) {
    return (
      <SafeAreaView style={styles.container} edges={["top", "left", "right"]}>
        <View style={styles.centered}>
          <EmptyState
            title={t("messages.loadFailed")}
            description={getErrorMessage(profileQuery.error, "messages.loadFailed")}
            primaryAction={{ label: t("common:actions.retry"), icon: "refresh", onPress: () => void profileQuery.refetch() }}
            secondaryAction={{ label: t("common:actions.back"), icon: "arrow-left", onPress: handleNavigateBack }}
          />
        </View>
      </SafeAreaView>
    );
  }

  const renderSensitiveInput = (
    field: Exclude<keyof SensitiveEmployeeProfilePayload, "birthday" | "expectedSensitiveRevision">,
    options: { secureTextEntry?: boolean } = {}
  ) => (
    <View key={field} style={styles.fieldBlock}>
      <TextInput
        mode="outlined"
        label={t(`fields.${field}`)}
        value={sensitiveFormValues[field]}
        onChangeText={(value) => setSensitiveFieldValue(field, value)}
        secureTextEntry={options.secureTextEntry}
      />
      {/* 与正式资料不同的字段标记「已修改」，与生日的标记一致。 */}
      {draftChangedFields.has(field) ? (
        <Chip compact style={[styles.changedChip, styles.fieldChangedChip]} textStyle={styles.changedChipText}>
          {t("birthday.changed")}
        </Chip>
      ) : null}
    </View>
  );

  const renderStatusChip = (status: EmployeeProfileSensitiveChangeStatus) => (
    <Chip compact style={{ backgroundColor: STATUS_TONES[status].background }} textStyle={{ color: STATUS_TONES[status].text }}>
      {t(`status.${status.toLowerCase()}`)}
    </Chip>
  );

  const renderFieldChips = (fields: readonly string[]) => (
    <View style={styles.chipRow}>
      {fields.length
        ? fields.map((field) => <Chip key={field} compact style={styles.fieldChip}>{t(`fields.${field}`, field)}</Chip>)
        : <Text variant="bodySmall" style={styles.metaText}>{t("progress.noChangedFields")}</Text>}
    </View>
  );

  // B5 审核进度：状态卡、时间线、本次变更与操作、历史申请（只含状态与字段标识）。
  const renderProgressView = () => {
    const request = sensitiveQuery.data;
    if (sensitiveQuery.isLoading && !request) {
      return <View style={styles.centeredBlock}><ActivityIndicator /></View>;
    }
    if (!request) {
      return (
        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium" style={styles.sectionTitle}>{t("status.empty")}</Text>
          <Text variant="bodySmall" style={styles.metaText}>{t("progress.emptyDescription")}</Text>
          <Button mode="contained" buttonColor={PROFILE_BLUE} disabled={!canEditSensitive} onPress={() => handleStartSensitiveEdit("personal")}>
            {t("actions.editSensitive")}
          </Button>
        </Surface>
      );
    }
    const timeline = buildSensitiveReviewTimeline(request);
    const earlier = getEarlierSensitiveRequests(historyQuery.data ?? [], request.requestId);
    const canWithdraw = canWithdrawSensitiveRequest(request);
    const canResubmit = canResubmitSensitiveRequest(request);
    return (
      <>
        <Surface style={styles.reviewNotice} elevation={0}>
          <View style={[styles.sectionHeader, usesLargeTextLayout && styles.sectionHeaderLargeText]}>
            <Text variant="titleMedium" style={styles.sectionTitle}>{t("progress.statusTitle")}</Text>
            {renderStatusChip(request.status)}
          </View>
          <Text variant="bodySmall" style={styles.metaText}>{t(`progress.statusDescription.${request.status.toLowerCase()}`)}</Text>
          {request.status === "Rejected" && request.reviewReason ? (
            <View style={styles.rejectBox}>
              <Text variant="labelMedium" style={styles.rejectTitle}>{t("progress.rejectReason")}</Text>
              <Text variant="bodyMedium" style={styles.rejectText} selectable>{request.reviewReason}</Text>
            </View>
          ) : null}
        </Surface>

        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium" style={styles.sectionTitle}>{t("progress.timelineTitle")}</Text>
          {timeline.map((step, index) => (
            <View key={step.key} style={styles.timelineRow}>
              <View style={styles.timelineRail}>
                <View style={[styles.timelineDot, { backgroundColor: TIMELINE_DOT_COLORS[step.state] }]} />
                {index < timeline.length - 1 ? <View style={styles.timelineLine} /> : null}
              </View>
              <View style={styles.timelineCopy}>
                <Text variant="bodyMedium" style={step.state === "upcoming" ? styles.metaText : styles.timelineLabel}>{t(step.labelKey)}</Text>
                {step.time ? <Text variant="bodySmall" style={styles.metaText}>{formatDateTime(step.time, locale) ?? step.time}</Text> : null}
              </View>
            </View>
          ))}
        </Surface>

        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium" style={styles.sectionTitle}>{t("progress.changedTitle")}</Text>
          {renderFieldChips(sensitiveChangedFields)}
          {canWithdraw || canResubmit ? (
            <View style={styles.formActions}>
              {canWithdraw ? (
                <Button
                  mode="outlined"
                  icon="undo-variant"
                  textColor={HB_COLORS.danger}
                  onPress={handleWithdraw}
                  loading={withdrawPendingForCurrentIdentity}
                  disabled={withdrawPendingForCurrentIdentity}
                  style={styles.actionButton}
                >
                  {t("progress.withdraw")}
                </Button>
              ) : null}
              {canResubmit ? (
                <Button
                  mode="contained"
                  buttonColor={PROFILE_BLUE}
                  icon="pencil-outline"
                  onPress={handleResubmit}
                  disabled={!canEditSensitive || withdrawPendingForCurrentIdentity}
                  style={styles.actionButton}
                >
                  {t(request.status === "Pending" ? "progress.editPending" : "progress.resubmit")}
                </Button>
              ) : null}
            </View>
          ) : null}
        </Surface>

        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium" style={styles.sectionTitle}>{t("progress.historyTitle")}</Text>
          {historyQuery.isLoading ? <ActivityIndicator /> : null}
          {historyQuery.isError ? (
            <>
              <HelperText type="error" visible>{getErrorMessage(historyQuery.error, "progress.historyLoadFailed")}</HelperText>
              <Button mode="text" icon="refresh" onPress={() => void historyQuery.refetch()}>{t("common:actions.retry")}</Button>
            </>
          ) : null}
          {!historyQuery.isLoading && !historyQuery.isError && earlier.length === 0 ? (
            <Text variant="bodySmall" style={styles.metaText}>{t("progress.historyEmpty")}</Text>
          ) : null}
          {earlier.map((item, index) => (
            <View key={item.requestId} style={[styles.historyItem, index === earlier.length - 1 && styles.historyItemLast]}>
              <View style={styles.sectionHeader}>
                <Text variant="bodySmall" style={styles.metaText}>
                  {t("sensitive.submittedAt", { time: formatDateTime(item.submittedAt, locale) ?? item.submittedAt })}
                </Text>
                {renderStatusChip(item.status)}
              </View>
              {renderFieldChips(item.changedFields)}
              {/* 驳回原因醒目显示，方便员工按原因修正后重提。 */}
              {item.status === "Rejected" && item.reviewReason ? (
                <View style={styles.rejectBox}>
                  <Text variant="labelMedium" style={styles.rejectTitle}>{t("progress.rejectReason")}</Text>
                  <Text variant="bodySmall" style={styles.rejectText} selectable>{item.reviewReason}</Text>
                </View>
              ) : null}
            </View>
          ))}
        </Surface>
      </>
    );
  };

  const renderSensitiveEditorSection = (section: SensitiveProfileSection) => {
    if (section === "personal") {
      return (
        <Surface key={section} style={styles.card} elevation={0}>
          <View style={styles.sectionHeader}>
            <Text variant="titleMedium" style={styles.sectionTitle}>{t("sections.personal")}</Text>
            {isBirthdayChanged ? <Chip compact style={styles.changedChip} textStyle={styles.changedChipText}>{t("birthday.changed")}</Chip> : null}
          </View>
          {/* 纯 JS 年/月/日选择器，不依赖原生日期组件；校验与年龄仍走 birthday.ts。 */}
          <BirthdayPickerField
            label={t("fields.birthday")}
            value={sensitiveFormValues.birthday}
            onChange={(value) => setSensitiveFieldValue("birthday", value)}
            confirmedLabel={t("birthday.confirmed", { date: formalBirthday || t("overview.notProvided") })}
            disabled={sensitivePendingForCurrentIdentity}
          />
        </Surface>
      );
    }
    if (section === "banking") {
      return (
        <Surface key={section} style={styles.card} elevation={0}>
          <Text variant="titleMedium" style={styles.sectionTitle}>{t("sections.banking")}</Text>
          {renderSensitiveInput("bankBsb")}
          {renderSensitiveInput("bankAccountNumber", { secureTextEntry: true })}
        </Surface>
      );
    }
    if (section === "superannuation") {
      return (
        <Surface key={section} style={styles.card} elevation={0}>
          <Text variant="titleMedium" style={styles.sectionTitle}>{t("sections.superannuation")}</Text>
          {renderSensitiveInput("superannuationCompanyName")}
          {renderSensitiveInput("superannuationCompanyCode")}
          {renderSensitiveInput("superannuationAccountNumber", { secureTextEntry: true })}
        </Surface>
      );
    }
    return (
      <Surface key={section} style={styles.card} elevation={0}>
        <Text variant="titleMedium" style={styles.sectionTitle}>{t("sections.identity")}</Text>
        <Text variant="labelMedium" style={styles.metaText}>{t("sensitive.confirmed")}</Text>
        <Text variant="bodyMedium">{profileQuery.data?.identityType || t("common:na")}</Text>
        <Text variant="bodyMedium">{getSensitiveAccountSummary(profileQuery.data?.identityId) || t("common:na")}</Text>
        {profileQuery.data?.identityPhotoUrl ? (
          <Image
            source={{ uri: profileQuery.data.identityPhotoUrl }}
            style={styles.identityPreview}
            resizeMode="contain"
            onError={() => {
              const imageUrl = profileQuery.data?.identityPhotoUrl;
              if (!shouldRefreshIdentityPhotoAfterLoadError(imageUrl, formalIdentityPhotoErrorUrlRef.current)) return;
              // 每个正式签名 URL 最多自动刷新一次，避免 Image onError 紧密循环。
              formalIdentityPhotoErrorUrlRef.current = imageUrl!;
              void profileQuery.refetch();
            }}
          />
        ) : <Text variant="bodySmall" style={styles.metaText}>{t("preview.empty")}</Text>}
        {sensitiveQuery.data?.status === "Pending" ? (
          <View style={styles.pendingSnapshot}>
            <Text variant="labelMedium">{t("sensitive.pendingSnapshot")}</Text>
            <Text variant="bodyMedium">{sensitiveQuery.data.identityType || t("common:na")}</Text>
            <Text variant="bodyMedium">{getSensitiveAccountSummary(sensitiveQuery.data.identityId) || t("common:na")}</Text>
            {sensitiveQuery.data.identityPhotoUrl ? (
              <Image
                source={{ uri: sensitiveQuery.data.identityPhotoUrl }}
                style={styles.identityPreview}
                resizeMode="contain"
                onError={() => {
                  const imageUrl = sensitiveQuery.data?.identityPhotoUrl;
                  if (!shouldRefreshIdentityPhotoAfterLoadError(imageUrl, pendingIdentityPhotoErrorUrlRef.current)) return;
                  // 待审签名 URL 失败时只刷新当前签名一次。
                  pendingIdentityPhotoErrorUrlRef.current = imageUrl!;
                  void sensitiveQuery.refetch();
                }}
              />
            ) : pendingIdentityPhotoRemoval ? (
              <Text variant="bodySmall">{t("sensitive.pendingPhotoRemoval")}</Text>
            ) : <Text variant="bodySmall" style={styles.metaText}>{t("preview.empty")}</Text>}
          </View>
        ) : null}
        {renderSensitiveInput("identityType")}
        {renderSensitiveInput("identityId", { secureTextEntry: true })}
        <AvatarEditorField
          kind="identityPhoto"
          label={t("fields.identityPhotoUrl")}
          uri={sensitiveQuery.data?.status === "Pending" ? sensitiveQuery.data.identityPhotoUrl : profileQuery.data?.identityPhotoUrl ?? ""}
          onSave={(image) => handleSaveImage("identityPhoto", image)}
          onDelete={() => handleDeleteImage("identityPhoto")}
          disabled={sensitivePendingForCurrentIdentity || currentSavingImageKind !== null}
        />
      </Surface>
    );
  };

  return (
    <SafeAreaView style={styles.container} edges={["top", "left", "right"]}>
      <View style={styles.header}>
        <IconButton icon="arrow-left" size={22} accessibilityLabel={t("common:actions.back")} onPress={handleHeaderBack} style={styles.headerBack} />
        <Text variant="titleLarge" style={styles.headerTitle} numberOfLines={1}>
          {view === "overview"
            ? t("title")
            : view === "basic"
              ? t("edit.basicTitle")
              : view === "progress" ? t("progress.title") : t("edit.sensitiveTitle")}
        </Text>
        <View style={styles.headerSpacer} />
      </View>

      <ScrollView
        contentContainerStyle={styles.content}
        keyboardShouldPersistTaps="handled"
        refreshControl={view === "overview" || view === "progress" ? (
          <RefreshControl
            refreshing={profileQuery.isRefetching || sensitiveQuery.isRefetching || (view === "progress" && historyQuery.isRefetching)}
            onRefresh={() => void handleManualRefresh()}
            tintColor={PROFILE_BLUE}
          />
        ) : undefined}
      >
        {view === "overview" ? (
          <>
            <Surface style={styles.heroCard} elevation={0}>
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={t("actions.editAvatar")}
                onPress={() => setAvatarEditing((current) => !current)}
                style={({ pressed }) => [styles.avatarButton, pressed && styles.pressed]}
              >
                {profileQuery.data?.avatarUrl ? (
                  <Avatar.Image size={64} source={{ uri: profileQuery.data.avatarUrl }} />
                ) : (
                  <Avatar.Text size={64} label={getInitials(readonlyDisplayName || readonlyUsername)} style={styles.heroAvatar} />
                )}
                <View style={styles.avatarEditBadge}><Text style={styles.avatarEditIcon}>✎</Text></View>
              </Pressable>
              <View style={styles.heroCopy}>
                <Text variant="titleLarge" style={styles.displayName}>{readonlyDisplayName}</Text>
                <Text variant="bodySmall" style={styles.metaText}>{readonlyUsername || t("common:na")}</Text>
                {formValues.employmentType ? (
                  <Text variant="labelMedium" style={styles.employmentType}>
                    {t(`employmentTypeOptions.${formValues.employmentType}`, formValues.employmentType)}
                  </Text>
                ) : null}
              </View>
            </Surface>

            {avatarEditing ? (
              <Surface style={styles.card} elevation={0}>
                <AvatarEditorField
                  kind="avatar"
                  label={t("edit.avatarTitle")}
                  uri={profileQuery.data?.avatarUrl ?? ""}
                  onSave={(image) => handleSaveImage("avatar", image)}
                  onDelete={() => handleDeleteImage("avatar")}
                  disabled={savePendingForCurrentIdentity || currentSavingImageKind !== null}
                />
                <Button mode="text" onPress={() => setAvatarEditing(false)}>{t("common:actions.cancel")}</Button>
              </Surface>
            ) : null}

            {/* 资料完整度只按已确认的正式资料计算，待审内容批准后才计入。 */}
            <Surface style={styles.card} elevation={0}>
              <View style={styles.sectionHeader}>
                <Text variant="titleMedium" style={styles.sectionTitle}>{t("completeness.title")}</Text>
                <Text variant="titleMedium" style={styles.completenessCount}>
                  {t("completeness.count", { filled: completeness.filled, total: completeness.total })}
                </Text>
              </View>
              <ProgressBar progress={completeness.filled / completeness.total} color={completeness.isComplete ? HB_COLORS.success : PROFILE_BLUE} style={styles.progressBar} />
              <Text variant="bodySmall" style={styles.metaText}>
                {completeness.isComplete
                  ? t("completeness.complete")
                  : t("completeness.missing", {
                    items: completeness.missing.map((item) => t(PROFILE_COMPLETENESS_LABEL_KEYS[item])).join(t("sensitive.fieldSeparator")),
                  })}
              </Text>
            </Surface>

            <Surface style={styles.card} elevation={0}>
              <View style={[styles.sectionHeader, usesLargeTextLayout && styles.sectionHeaderLargeText]}>
                <Text variant="titleMedium" style={styles.sectionTitle}>{t("sections.basic")}</Text>
                <Button compact mode="text" icon="pencil-outline" onPress={() => setView("basic")}>{t("actions.edit")}</Button>
              </View>
              <View style={styles.summaryGrid}>
                <ProfileSummaryRow inline icon="phone-outline" label={t("fields.phone")} value={formValues.phone || t("common:na")} />
                <ProfileSummaryRow inline icon="email-outline" label={t("fields.email")} value={formValues.email || t("common:na")} />
                <ProfileSummaryRow inline icon="account-outline" label={t("fields.gender")} value={formValues.gender ? t(`genderOptions.${formValues.gender}`, formValues.gender) : t("common:na")} />
                {formValues.employmentType ? <ProfileSummaryRow inline icon="briefcase-outline" label={t("fields.employmentType")} value={t(`employmentTypeOptions.${formValues.employmentType}`, formValues.employmentType)} /> : null}
                <ProfileSummaryRow inline icon="map-marker-outline" label={t("fields.address")} value={formValues.address || t("common:na")} isLast />
              </View>
            </Surface>

            <Surface style={styles.card} elevation={0}>
              <View style={[styles.sectionHeader, usesLargeTextLayout && styles.sectionHeaderLargeText]}>
                <Text variant="titleMedium" style={styles.sectionTitle}>{t("sections.sensitive")}</Text>
                {sensitiveQuery.isLoading ? <ActivityIndicator size="small" /> : <Chip compact style={styles.statusChip}>{t(sensitiveStatus.statusKey)}</Chip>}
              </View>
              <ProfileSummaryRow
                label={t("fields.birthday")}
                value={formalBirthday ? formatBirthdayWithAge(formalBirthday) : t("overview.notProvided")}
                onPress={canEditSensitive ? () => handleStartSensitiveEdit("personal") : undefined}
              />
              <ProfileSummaryRow
                label={t("sections.banking")}
                value={getSensitiveAccountSummary(profileQuery.data?.bankAccountNumber) || t("overview.notProvided")}
                detail={profileQuery.data?.bankBsb || undefined}
                onPress={canEditSensitive ? () => handleStartSensitiveEdit("banking") : undefined}
              />
              <ProfileSummaryRow
                label={t("sections.superannuation")}
                value={profileQuery.data?.superannuationAccountNumber || profileQuery.data?.superannuationCompanyName ? t("overview.completed") : t("overview.notProvided")}
                onPress={canEditSensitive ? () => handleStartSensitiveEdit("superannuation") : undefined}
              />
              <ProfileSummaryRow
                label={t("sections.identity")}
                value={getSensitiveAccountSummary(profileQuery.data?.identityId) || t("overview.notProvided")}
                trailing={<Chip compact style={styles.statusChip}>{t(sensitiveStatus.statusKey)}</Chip>}
                stackTrailingOnLargeText
                onPress={canEditSensitive ? () => handleStartSensitiveEdit("identity") : undefined}
                isLast
              />
              {sensitiveStatus.reviewReason ? (
                <HelperText type="error" visible>{t("sensitive.rejectionReason", { reason: sensitiveStatus.reviewReason })}</HelperText>
              ) : null}
              <HelperText type="error" visible={sensitiveQuery.isError}>
                {getErrorMessage(sensitiveQuery.error, "messages.sensitiveLoadFailed")}
              </HelperText>
              {sensitiveQuery.isError ? (
                <Button mode="text" icon="refresh" onPress={() => void sensitiveQuery.refetch()}>{t("common:actions.retry")}</Button>
              ) : null}
              {sensitiveQuery.data ? (
                <Button mode="text" icon="timeline-clock-outline" onPress={() => setView("progress")}>{t("progress.open")}</Button>
              ) : null}
            </Surface>
            {/* 账户与安全：修改密码复用自助改密页；个人码沿用设置页里的卡片，避免同屏挂两份打印状态。 */}
            {sessionKind !== "device" ? (
              <Surface style={styles.card} elevation={0}>
                <Text variant="titleMedium" style={styles.sectionTitle}>{t("security.title")}</Text>
                {sessionKind === "account" ? (
                  <ProfileSummaryRow
                    icon="lock-reset"
                    label={t("security.changePassword")}
                    value={t("security.changePasswordHelper")}
                    onPress={() => router.push("/(auth)/change-password?mode=voluntary" as unknown as Parameters<typeof router.push>[0])}
                  />
                ) : null}
                <ProfileSummaryRow
                  icon="barcode"
                  label={t("cashierBarcode.title")}
                  value={t("security.personalCodeHelper")}
                  onPress={() => router.navigate("/(shell)/settings")}
                  isLast
                />
              </Surface>
            ) : null}
            <Surface style={styles.card} elevation={0}>
              <Text variant="titleMedium" style={styles.sectionTitle}>{t("minorEmployment.title")}</Text>
              <Text variant="bodySmall" style={styles.metaText}>{t("minorEmployment.description")}</Text>
              <Button mode="outlined" icon="shield-account-outline" onPress={() => router.push("/(shell)/minor-employment" as never)}>{t("minorEmployment.open")}</Button>
            </Surface>
            <Text variant="bodySmall" style={styles.updatedAt}>{t("overview.updatedAt", { time: updatedAtText })}</Text>
          </>
        ) : view === "progress" ? (
          renderProgressView()
        ) : view === "basic" ? (
          <Surface style={styles.card} elevation={0}>
            <Text variant="bodySmall" style={styles.editHint}>{t("edit.basicHint")}</Text>
            <TextInput mode="outlined" label={t("fields.phone")} value={formValues.phone} onChangeText={(value) => setFieldValue("phone", value)} keyboardType="phone-pad" />
            <TextInput mode="outlined" label={t("fields.email")} value={formValues.email} onChangeText={(value) => setFieldValue("email", value)} keyboardType="email-address" autoCapitalize="none" autoCorrect={false} maxLength={254} error={Boolean(formValues.email && !isValidEmail(formValues.email))} />
            {/* 生日属于敏感资料：这里只读，修改须到敏感资料提交审核。 */}
            <TextInput
              mode="outlined"
              label={t("fields.birthday")}
              value={formalBirthday ? formatBirthdayWithAge(formalBirthday) : t("overview.notProvided")}
              editable={false}
              right={<TextInput.Icon icon="lock-outline" disabled />}
            />
            <View style={styles.readonlyHintRow}>
              <HelperText type="info" visible style={styles.readonlyHintText}>{t("birthday.requiresReview")}</HelperText>
              <Button compact mode="text" onPress={handleGoToBirthdayReview} disabled={!canEditSensitive || savePendingForCurrentIdentity}>
                {t("birthday.goToReview")}
              </Button>
            </View>
            <View style={styles.segmentBlock}>
              <Text variant="labelLarge">{t("fields.gender")}</Text>
              <SegmentedButtons value={formValues.gender} onValueChange={(value) => setFieldValue("gender", value)} buttons={GENDERS.map((value) => ({ value, label: t(`genderOptions.${value}`) }))} />
            </View>
            {/* 有权限才列出全部工作类型供选择；无权限只展示已有的那一个，没有就整块不显示。 */}
            {canEditPositionType || formValues.employmentType ? (
              <View style={styles.segmentBlock}>
                <Text variant="labelLarge">{t("fields.employmentType")}</Text>
                <SegmentedButtons value={formValues.employmentType ?? ""} onValueChange={(value) => setFieldValue("employmentType", value)} buttons={(canEditPositionType ? EMPLOYMENT_TYPES : [formValues.employmentType ?? ""]).map((value) => ({ value, label: t(`employmentTypeOptions.${value}`, value), disabled: !canEditPositionType }))} />
                {!canEditPositionType ? <HelperText type="info">{t("messages.positionTypeReadonly")}</HelperText> : null}
              </View>
            ) : null}
            <TextInput mode="outlined" label={t("fields.address")} placeholder={t("placeholders.address")} value={formValues.address} onChangeText={(value) => setFieldValue("address", value)} multiline numberOfLines={4} />
            <View style={styles.formActions}>
              <Button mode="outlined" onPress={resetEditor} disabled={savePendingForCurrentIdentity} style={styles.actionButton}>{t("common:actions.cancel")}</Button>
              <Button mode="contained" buttonColor={PROFILE_BLUE} onPress={() => void handleSave()} loading={savePendingForCurrentIdentity} disabled={!isBasicDirty || savePendingForCurrentIdentity || currentSavingImageKind !== null} style={styles.actionButton}>{t("common:actions.save")}</Button>
            </View>
          </Surface>
        ) : (
          <>
            <Surface style={styles.reviewNotice} elevation={0}>
              <View style={[styles.sectionHeader, usesLargeTextLayout && styles.sectionHeaderLargeText]}>
                <Text variant="titleSmall">{t("edit.reviewNoticeTitle")}</Text>
                <Chip compact style={styles.statusChip}>{t(sensitiveStatus.statusKey)}</Chip>
              </View>
              <Text variant="bodySmall" style={styles.metaText}>{t("edit.reviewNoticeDescription")}</Text>
              {sensitiveStatus.submittedAt ? (
                <Text variant="bodySmall" style={styles.metaText}>{t("sensitive.submittedAt", { time: formatDateTime(sensitiveStatus.submittedAt, locale) ?? sensitiveStatus.submittedAt })}</Text>
              ) : null}
            </Surface>
            {getSensitiveSectionOrder(activeSensitiveSection).map(renderSensitiveEditorSection)}
            <Surface style={styles.card} elevation={0}>
              <Button mode="contained" buttonColor={PROFILE_BLUE} onPress={() => void handleSensitiveSubmit()} loading={sensitivePendingForCurrentIdentity} disabled={!isSensitiveDirty || Boolean(birthdayError) || sensitivePendingForCurrentIdentity || currentSavingImageKind !== null}>
                {draftChangedFields.size
                  ? t("actions.submitSensitiveWithCount", { count: draftChangedFields.size })
                  : t("actions.submitSensitive")}
              </Button>
              <Button mode="text" onPress={resetEditor} disabled={sensitivePendingForCurrentIdentity}>{t("common:actions.cancel")}</Button>
            </Surface>
          </>
        )}
        <HelperText type="error" visible={profileQuery.isError && Boolean(profileQuery.data)}>
          {getErrorMessage(profileQuery.error, "messages.loadFailed")}
        </HelperText>
        {profileQuery.isError && profileQuery.data ? (
          <Button mode="text" icon="refresh" onPress={() => void handleManualRefresh()}>
            {t("common:actions.retry")}
          </Button>
        ) : null}
      </ScrollView>
      <Snackbar visible={snackbarVisible} onDismiss={() => setSnackbarVisible(false)}>{snackbarMessage}</Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: "#F4F6F8" },
  centered: { flex: 1, alignItems: "center", justifyContent: "center", paddingHorizontal: 24 },
  header: {
    minHeight: 52,
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  headerBack: { margin: 0, marginLeft: 4 },
  headerTitle: { flex: 1, textAlign: "center", fontWeight: "700", color: HB_COLORS.textPrimary },
  headerSpacer: { width: 44 },
  content: { padding: HB_SPACING.md, paddingBottom: HB_SPACING.xl, gap: HB_SPACING.sm },
  card: {
    backgroundColor: HB_COLORS.white,
    borderRadius: 12,
    borderColor: HB_COLORS.outlineMuted,
    borderWidth: StyleSheet.hairlineWidth,
    padding: HB_SPACING.sm,
    gap: HB_SPACING.sm,
  },
  heroCard: {
    minHeight: 88,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.md,
    padding: HB_SPACING.md,
    backgroundColor: HB_COLORS.white,
    borderRadius: 12,
    borderColor: HB_COLORS.outlineMuted,
    borderWidth: StyleSheet.hairlineWidth,
  },
  avatarButton: { position: "relative", borderRadius: 34 },
  pressed: { opacity: 0.7 },
  heroAvatar: { backgroundColor: PROFILE_BLUE },
  avatarEditBadge: {
    position: "absolute",
    right: -2,
    bottom: -2,
    width: 24,
    height: 24,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 12,
    borderWidth: 2,
    borderColor: HB_COLORS.white,
    backgroundColor: PROFILE_BLUE,
  },
  avatarEditIcon: { color: HB_COLORS.white, fontSize: 14, fontWeight: "700" },
  heroCopy: { flex: 1, gap: 3 },
  displayName: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  employmentType: { alignSelf: "flex-start", color: PROFILE_BLUE },
  sectionHeader: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.sm },
  sectionHeaderLargeText: { flexDirection: "column", alignItems: "flex-start", justifyContent: "flex-start" },
  sectionTitle: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  summaryGrid: { gap: 0 },
  statusChip: { backgroundColor: "#EAF1FF" },
  metaText: { color: HB_COLORS.textSecondary },
  updatedAt: { color: HB_COLORS.textSecondary, textAlign: "center", paddingTop: HB_SPACING.xs },
  segmentBlock: { gap: HB_SPACING.xs },
  formActions: { flexDirection: "row", gap: HB_SPACING.sm, paddingTop: HB_SPACING.xs },
  actionButton: { flex: 1, borderRadius: HB_RADIUS.control },
  editHint: { color: HB_COLORS.textSecondary, marginBottom: HB_SPACING.xs },
  reviewNotice: {
    backgroundColor: "#F5F8FF",
    borderRadius: 12,
    borderColor: "#CFDCFA",
    borderWidth: StyleSheet.hairlineWidth,
    padding: HB_SPACING.md,
    gap: HB_SPACING.xs,
  },
  changedChip: { backgroundColor: "#FEF0C7" },
  changedChipText: { color: "#7A2E0E" },
  readonlyHintRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", marginTop: -HB_SPACING.xs },
  readonlyHintText: { flex: 1, paddingHorizontal: 0 },
  pendingSnapshot: { gap: HB_SPACING.xs, padding: HB_SPACING.sm, borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.surfaceMuted },
  identityPreview: { width: "100%", height: 180, borderRadius: 12, backgroundColor: HB_COLORS.surfaceMuted },
  centeredBlock: { paddingVertical: HB_SPACING.xl, alignItems: "center" },
  fieldBlock: { gap: HB_SPACING.xxs },
  fieldChangedChip: { alignSelf: "flex-end" },
  chipRow: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  fieldChip: { backgroundColor: HB_COLORS.surfaceMuted },
  completenessCount: { color: PROFILE_BLUE, fontWeight: "700" },
  progressBar: { height: 6, borderRadius: 3, backgroundColor: HB_COLORS.surfaceMuted },
  rejectBox: {
    gap: HB_SPACING.xxs,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    borderLeftWidth: 3,
    borderLeftColor: HB_COLORS.danger,
    backgroundColor: "#FEF3F2",
  },
  rejectTitle: { color: HB_COLORS.danger, fontWeight: "700" },
  rejectText: { color: "#7A271A" },
  timelineRow: { flexDirection: "row", gap: HB_SPACING.sm, minHeight: 48 },
  timelineRail: { width: 14, alignItems: "center" },
  timelineDot: { width: 12, height: 12, borderRadius: 6, marginTop: 4 },
  timelineLine: { flex: 1, width: 2, marginTop: 2, backgroundColor: HB_COLORS.outlineMuted },
  timelineCopy: { flex: 1, gap: 2, paddingBottom: HB_SPACING.sm },
  timelineLabel: { color: HB_COLORS.textPrimary, fontWeight: "600" },
  historyItem: {
    gap: HB_SPACING.xs,
    paddingBottom: HB_SPACING.sm,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  historyItemLast: { borderBottomWidth: 0, paddingBottom: 0 },
});

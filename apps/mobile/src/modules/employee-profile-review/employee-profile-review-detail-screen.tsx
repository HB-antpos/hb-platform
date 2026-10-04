import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { AppState, Image, Pressable, ScrollView, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useLocalSearchParams, useNavigation, useRouter } from "expo-router";
import { SafeAreaView } from "react-native-safe-area-context";
import {
  ActivityIndicator,
  Button,
  Card,
  Chip,
  Dialog,
  Divider,
  Portal,
  Snackbar,
  Surface,
  Text,
  TextInput,
  useTheme,
} from "react-native-paper";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { getEmployeeProfileReviewAccess } from "./access";
import {
  approveEmployeeProfileReviewApi,
  getEmployeeProfileReviewDetailApi,
  rejectEmployeeProfileReviewApi,
} from "./api";
import {
  getReviewFailureKind,
  isRejectReasonValid,
  maskSensitiveValue,
  shouldDisableReviewActions,
} from "./review-logic";
import type { EmployeeProfileSensitiveField } from "./types";
import {
  clearEmployeeProfileReviewDetailCache,
  employeeProfileReviewDetailQueryKey,
} from "./review-cache";
import {
  createIdentityPhotoErrorRefetchGuard,
  getIdentityPhotoRefreshDelay,
} from "./identity-photo-refresh";
import { createEmployeeProfileReviewAppStateHandler } from "./privacy-state";
import {
  createEmployeeProfileSensitiveDetailActivityGuard,
  isSensitiveDetailFetchBlockedError,
} from "./sensitive-detail-activity-guard";
import { calculateAge } from "@/modules/employee-profile/birthday";
import { useAppNavigationStore } from "@/modules/navigation/store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";

const MASKED_FIELDS = new Set<EmployeeProfileSensitiveField>([
  "bankBsb",
  "bankAccountNumber",
  "superannuationAccountNumber",
  "identityId",
]);

function formatValue(value: string) {
  return value.trim() || "--";
}

export function EmployeeProfileReviewDetailScreen() {
  const router = useRouter();
  const navigation = useNavigation();
  const queryClient = useQueryClient();
  const theme = useTheme();
  const { t } = useAppTranslation("employeeProfileReview");
  const params = useLocalSearchParams<{ requestId?: string | string[] }>();
  const requestIdValue = Array.isArray(params.requestId) ? params.requestId[0] : params.requestId;
  const requestId = Number(requestIdValue);
  const validRequestId = Number.isInteger(requestId) && requestId > 0;
  const currentUser = useAuthStore((state) => state.user);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const navigationItems = useAppNavigationStore((state) => state.items);
  const navigationReady = useAppNavigationStore((state) => state.isReady);
  const mountedRef = useRef(true);
  const appIsActiveRef = useRef(AppState.currentState === "active");
  const resumeGenerationRef = useRef(0);
  const reviewMutationGenerationRef = useRef(0);
  const photoErrorRefetchGuard = useRef(createIdentityPhotoErrorRefetchGuard());
  const reviewAccess = useMemo(
    () => getEmployeeProfileReviewAccess({
      roleNames: currentUser?.roleNames,
      permissions: currentUser?.permissions,
      menuRouteNames: navigationItems.map((item) => item.routeName),
      sessionKind,
    }),
    [currentUser?.permissions, currentUser?.roleNames, navigationItems, sessionKind]
  );
  const clearDetailCache = useCallback(async () => {
    if (validRequestId) {
      await clearEmployeeProfileReviewDetailCache(queryClient, requestId);
    }
  }, [queryClient, requestId, validRequestId]);
  const sensitiveDetailActivityGuard = useMemo(
    () => createEmployeeProfileSensitiveDetailActivityGuard({
      isActive: () => appIsActiveRef.current,
      getActivityGeneration: () => resumeGenerationRef.current,
      clearCache: clearDetailCache,
    }),
    [clearDetailCache]
  );
  const fetchSensitiveDetail = useCallback(
    () => sensitiveDetailActivityGuard.fetch(
      () => getEmployeeProfileReviewDetailApi(requestId)
    ),
    [requestId, sensitiveDetailActivityGuard]
  );
  const [privacyShielded, setPrivacyShielded] = useState(
    AppState.currentState !== "active"
  );
  const [privacyRefreshFailed, setPrivacyRefreshFailed] = useState(false);
  const detailQuery = useQuery({
    queryKey: employeeProfileReviewDetailQueryKey(requestId),
    enabled:
      navigationReady
      && reviewAccess.allowed
      && validRequestId
      && !privacyShielded,
    queryFn: fetchSensitiveDetail,
    // 前后台重新鉴权由本页面统一控制，禁止 Query 自动焦点/重连刷新绕过闸门。
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    // 完整敏感值离开页面后不保留默认五分钟缓存。
    gcTime: 0,
  });
  const refetchSensitiveDetail = useCallback(
    () => sensitiveDetailActivityGuard.runIfActive(() => detailQuery.refetch()),
    [detailQuery.refetch, sensitiveDetailActivityGuard]
  );
  const [revealedFields, setRevealedFields] = useState<Set<EmployeeProfileSensitiveField>>(
    () => new Set()
  );
  const [dialogAction, setDialogAction] = useState<"approve" | "reject" | null>(null);
  const [reason, setReason] = useState("");
  const [staleAfterConflict, setStaleAfterConflict] = useState(false);
  const [snackbarMessage, setSnackbarMessage] = useState("");
  const [isLeavingSensitiveDetail, setIsLeavingSensitiveDetail] = useState(false);
  // 未变更字段默认折叠，审核者先聚焦本次变更。
  const [showUnchanged, setShowUnchanged] = useState(false);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      void clearDetailCache();
    };
  }, [clearDetailCache]);

  const clearSensitiveUiState = useCallback(() => {
    if (!mountedRef.current) {
      return;
    }
    setRevealedFields(new Set());
    setShowUnchanged(false);
    setDialogAction(null);
    setReason("");
    setSnackbarMessage("");
    photoErrorRefetchGuard.current = createIdentityPhotoErrorRefetchGuard();
  }, []);

  const leaveDetail = useCallback(async (target: "/(shell)/settings" | "/(shell)/employee-profile-review") => {
    if (mountedRef.current) {
      // 离开前立即清除已揭示状态，缓存取消在途请求后再彻底移除。
      setIsLeavingSensitiveDetail(true);
      clearSensitiveUiState();
    }
    await clearDetailCache();
    if (mountedRef.current) {
      if (target === "/(shell)/employee-profile-review" && router.canGoBack()) {
        // 列表与详情已在同一嵌套 Stack；返回复用现有列表，避免 replace 生成重复列表。
        router.back();
        return;
      }
      router.replace(target);
    }
  }, [clearDetailCache, clearSensitiveUiState, router]);

  useLayoutEffect(() => {
    // 返回入口由导航栏承载，确保加载、错误和隐私遮罩状态下仍可安全退出。
    navigation.setOptions({
      header: () => (
        <SafeAreaView
          edges={["top"]}
          style={[styles.headerSafeArea, { backgroundColor: theme.colors.background }]}
        >
          <View
            style={[styles.headerRow, { borderBottomColor: theme.colors.outlineVariant }]}
          >
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("actions.back")}
              accessibilityState={{ disabled: isLeavingSensitiveDetail }}
              disabled={isLeavingSensitiveDetail}
              hitSlop={4}
              style={({ pressed }) => [
                styles.headerBackButton,
                pressed && styles.headerBackButtonPressed,
                isLeavingSensitiveDetail && styles.headerBackButtonDisabled,
              ]}
              onPress={() => void leaveDetail("/(shell)/employee-profile-review")}
            >
              <MaterialCommunityIcons
                name="chevron-left"
                size={28}
                color={theme.colors.primary}
              />
            </Pressable>
            <Text variant="titleLarge" style={styles.headerTitle} numberOfLines={1}>
              {t("detail.title")}
            </Text>
          </View>
        </SafeAreaView>
      ),
    });
  }, [
    isLeavingSensitiveDetail,
    leaveDetail,
    navigation,
    t,
    theme.colors.background,
    theme.colors.outlineVariant,
    theme.colors.primary,
  ]);

  const resumeDetailAfterForeground = useCallback(async () => {
    const generation = ++resumeGenerationRef.current;
    if (!validRequestId || !reviewAccess.allowed) {
      await leaveDetail("/(shell)/settings");
      return;
    }
    setPrivacyRefreshFailed(false);
    try {
      await clearDetailCache();
      const refreshed = await sensitiveDetailActivityGuard.runIfActive(
        () => queryClient.fetchQuery({
          queryKey: employeeProfileReviewDetailQueryKey(requestId),
          queryFn: fetchSensitiveDetail,
          gcTime: 0,
        })
      );
      if (!refreshed) {
        return;
      }
      if (
        mountedRef.current
        && appIsActiveRef.current
        && generation === resumeGenerationRef.current
      ) {
        setPrivacyShielded(false);
      }
    } catch (error) {
      if (!appIsActiveRef.current || isSensitiveDetailFetchBlockedError(error)) {
        // 前台校验途中退到后台时保持遮罩，等待下一次 active 再重新鉴权。
        await clearDetailCache();
        return;
      }
      if (getReviewFailureKind(error) === "forbidden") {
        await leaveDetail("/(shell)/settings");
        return;
      }
      if (
        mountedRef.current
        && appIsActiveRef.current
        && generation === resumeGenerationRef.current
      ) {
        setPrivacyRefreshFailed(true);
      }
    }
  }, [
    clearDetailCache,
    fetchSensitiveDetail,
    leaveDetail,
    queryClient,
    requestId,
    reviewAccess.allowed,
    sensitiveDetailActivityGuard,
    validRequestId,
  ]);

  useEffect(() => {
    const handleAppState = createEmployeeProfileReviewAppStateHandler({
      onInactive: () => {
        // 必须是 AppState 回调第一步：同步关闸后，任何迟到 callback 都无法发起详情请求。
        appIsActiveRef.current = false;
        resumeGenerationRef.current += 1;
        if (mountedRef.current) {
          setPrivacyShielded(true);
          setPrivacyRefreshFailed(false);
          clearSensitiveUiState();
        }
        void clearDetailCache();
      },
      onActive: () => {
        appIsActiveRef.current = true;
        void resumeDetailAfterForeground();
      },
    });
    const subscription = AppState.addEventListener("change", handleAppState);
    return () => subscription.remove();
  }, [clearDetailCache, clearSensitiveUiState, resumeDetailAfterForeground]);

  useEffect(() => {
    if (navigationReady && (!reviewAccess.allowed || !validRequestId)) {
      // 权限失效、设备模式、iOS 审核会话或非法参数都不得触发详情 API。
      void leaveDetail("/(shell)/settings");
    }
  }, [leaveDetail, navigationReady, reviewAccess.allowed, validRequestId]);

  const detailAccessForbidden = getReviewFailureKind(detailQuery.error) === "forbidden";

  useEffect(() => {
    if (detailAccessForbidden) {
      // 详情 GET 的 403 与审核 mutation 一样立即销毁完整值并退出。
      void leaveDetail("/(shell)/settings");
    }
  }, [detailAccessForbidden, leaveDetail]);

  useEffect(() => {
    if (privacyShielded) {
      return;
    }
    const delay = getIdentityPhotoRefreshDelay(
      detailQuery.data?.identityPhotoUrlExpiresAt
    );
    if (delay === null) {
      return;
    }
    let active = true;
    const timer = setTimeout(() => {
      if (active && mountedRef.current) {
        void refetchSensitiveDetail();
      }
    }, delay);
    return () => {
      active = false;
      clearTimeout(timer);
    };
  }, [detailQuery.data?.identityPhotoUrlExpiresAt, privacyShielded, refetchSensitiveDetail]);

  const handlePhotoError = useCallback((url: string) => {
    // 每个签名 URL 最多触发一次受控刷新，避免坏图造成 refetch 循环。
    if (photoErrorRefetchGuard.current.shouldRefetch(url) && mountedRef.current) {
      void refetchSensitiveDetail();
    }
  }, [refetchSensitiveDetail]);

  const reviewMutation = useMutation({
    mutationFn: async () => {
      reviewMutationGenerationRef.current = resumeGenerationRef.current;
      if (dialogAction === "approve") {
        return sensitiveDetailActivityGuard.fetch(
          () => approveEmployeeProfileReviewApi(requestId, reason)
        );
      }
      if (dialogAction === "reject") {
        return sensitiveDetailActivityGuard.fetch(
          () => rejectEmployeeProfileReviewApi(requestId, reason)
        );
      }
      throw new Error("Review action is not selected");
    },
    onSuccess: async () => {
      if (sensitiveDetailActivityGuard.shouldIgnoreLateCallback(
        reviewMutationGenerationRef.current
      )) {
        return;
      }
      const listRefresh = queryClient.invalidateQueries({
        queryKey: ["employeeProfileReview", "requests"],
      });
      await leaveDetail("/(shell)/employee-profile-review");
      await listRefresh;
    },
    onError: async (error) => {
      if (sensitiveDetailActivityGuard.shouldIgnoreLateCallback(
        reviewMutationGenerationRef.current
      )) {
        return;
      }
      const kind = getReviewFailureKind(error);
      if (kind === "forbidden") {
        await leaveDetail("/(shell)/settings");
        return;
      }
      if (!mountedRef.current) {
        return;
      }
      setDialogAction(null);
      if (kind === "conflict") {
        // 关键逻辑：冲突后立即刷新，但保留 stale 标记，避免对旧详情重复提交审核。
        setStaleAfterConflict(true);
        setSnackbarMessage(t("messages.conflict"));
        await Promise.all([
          refetchSensitiveDetail(),
          queryClient.invalidateQueries({ queryKey: ["employeeProfileReview", "requests"] }),
        ]);
        return;
      }
      setSnackbarMessage(t("messages.reviewFailed"));
    },
  });

  const toggleReveal = (field: EmployeeProfileSensitiveField) => {
    setRevealedFields((current) => {
      const next = new Set(current);
      if (next.has(field)) {
        next.delete(field);
      } else {
        next.add(field);
      }
      return next;
    });
  };

  if (privacyShielded) {
    return (
      <View style={[styles.privacyShield, { backgroundColor: theme.colors.background }]}>
        <Text variant="titleLarge" selectable>{t("privacy.title")}</Text>
        <Text
          variant="bodyMedium"
          style={{ color: theme.colors.onSurfaceVariant, textAlign: "center" }}
          selectable
        >
          {t(privacyRefreshFailed ? "privacy.refreshFailed" : "privacy.description")}
        </Text>
        {privacyRefreshFailed ? (
          <Button
            mode="contained"
            icon="shield-refresh-outline"
            contentStyle={styles.touchTarget}
            onPress={() => void resumeDetailAfterForeground()}
          >
            {t("privacy.retry")}
          </Button>
        ) : <ActivityIndicator accessibilityLabel={t("privacy.refreshing")} />}
      </View>
    );
  }

  if (isLeavingSensitiveDetail || detailAccessForbidden) {
    return <View style={styles.centered} />;
  }

  if (!navigationReady || (detailQuery.isLoading && !detailQuery.data)) {
    return (
      <View style={styles.centered}>
        <ActivityIndicator size="large" accessibilityLabel={t("detail.loading")} />
      </View>
    );
  }

  if (!reviewAccess.allowed || !validRequestId) {
    return (
      <View style={styles.centered}>
        <Text selectable>{t("messages.permissionChanged")}</Text>
      </View>
    );
  }

  if (detailQuery.isError || !detailQuery.data) {
    return (
      <View style={styles.centered}>
        <Text variant="titleMedium" selectable>{t("messages.detailLoadFailed")}</Text>
        <Button
          icon="refresh"
          mode="contained"
          contentStyle={styles.touchTarget}
          onPress={() => void refetchSensitiveDetail()}
        >
          {t("actions.retry")}
        </Button>
      </View>
    );
  }

  const detail = detailQuery.data;
  const formatBirthday = (value: string) => {
    const age = calculateAge(value);
    return age === null ? value : t("detail.birthdayWithAge", { date: value, age });
  };
  const definitions: Array<{
    key: EmployeeProfileSensitiveField;
    current: string;
    proposed: string;
  }> = [
    // 生日不遮挡，并附上年龄，便于店长直接核对前后差异。
    { key: "birthday", current: formatBirthday(detail.currentSnapshot.birthday), proposed: formatBirthday(detail.birthday) },
    { key: "bankBsb", current: detail.currentSnapshot.bankBsb, proposed: detail.bankBsb },
    { key: "bankAccountNumber", current: detail.currentSnapshot.bankAccountNumber, proposed: detail.bankAccountNumber },
    { key: "superannuationCompanyName", current: detail.currentSnapshot.superannuationCompanyName, proposed: detail.superannuationCompanyName },
    { key: "superannuationCompanyCode", current: detail.currentSnapshot.superannuationCompanyCode, proposed: detail.superannuationCompanyCode },
    { key: "superannuationAccountNumber", current: detail.currentSnapshot.superannuationAccountNumber, proposed: detail.superannuationAccountNumber },
    { key: "identityType", current: detail.currentSnapshot.identityType, proposed: detail.identityType },
    { key: "identityId", current: detail.currentSnapshot.identityId, proposed: detail.identityId },
  ];
  const actionsDisabled = shouldDisableReviewActions(detail.status, staleAfterConflict);
  const rejectReasonInvalid = dialogAction === "reject" && !isRejectReasonValid(reason);
  const changedDefinitions = definitions.filter(({ key }) => detail.changedFields.includes(key));
  const unchangedDefinitions = definitions.filter(({ key }) => !detail.changedFields.includes(key));
  const photoChanged = detail.changedFields.includes("identityPhotoUrl");
  const unchangedCount = unchangedDefinitions.length + (photoChanged ? 0 : 1);

  const renderComparison = ({ key, current, proposed }: (typeof definitions)[number]) => {
    const changed = detail.changedFields.includes(key);
    const masked = MASKED_FIELDS.has(key) && !revealedFields.has(key);
    return (
      <Card
        mode="outlined"
        key={key}
        style={[styles.comparisonSurface, changed ? { backgroundColor: theme.colors.secondaryContainer } : undefined]}
      >
        <Card.Content style={styles.comparisonCard}>
          <View style={styles.fieldTitleRow}>
            <Text variant="titleMedium" selectable>{t(`fields.${key}`)}</Text>
            <View style={styles.fieldActions}>
              {changed ? <Chip compact icon="swap-horizontal">{t("detail.changed")}</Chip> : null}
              {MASKED_FIELDS.has(key) ? (
                <Button
                  compact
                  mode="text"
                  icon={masked ? "eye-outline" : "eye-off-outline"}
                  contentStyle={styles.touchTarget}
                  accessibilityLabel={t(masked ? "actions.reveal" : "actions.hide")}
                  onPress={() => toggleReveal(key)}
                >
                  {t(masked ? "actions.reveal" : "actions.hide")}
                </Button>
              ) : null}
            </View>
          </View>
          <Divider />
          <View style={styles.valueRow}>
            <View style={styles.valueColumn}>
              <Text variant="labelMedium" style={{ color: theme.colors.onSurfaceVariant }} selectable>
                {t("detail.currentValue")}
              </Text>
              <Text variant="bodyLarge" selectable>
                {masked ? maskSensitiveValue(current) : formatValue(current)}
              </Text>
            </View>
            <View style={styles.valueColumn}>
              <Text variant="labelMedium" style={{ color: theme.colors.onSurfaceVariant }} selectable>
                {t("detail.proposedValue")}
              </Text>
              <Text variant="bodyLarge" selectable style={changed ? { fontWeight: "700" } : undefined}>
                {masked ? maskSensitiveValue(proposed) : formatValue(proposed)}
              </Text>
            </View>
          </View>
        </Card.Content>
      </Card>
    );
  };

  // 证件照新旧并排对比；未变更时随其他未变更字段一起折叠。
  const renderPhotoComparison = () => (
    <Card
      mode="outlined"
      style={[styles.comparisonSurface, photoChanged
        ? { backgroundColor: theme.colors.secondaryContainer }
        : undefined]}
    >
      <Card.Content style={styles.comparisonCard}>
        <View style={styles.fieldTitleRow}>
          <Text variant="titleMedium" selectable>{t("fields.identityPhotoUrl")}</Text>
          {photoChanged
            ? <Chip compact icon="swap-horizontal">{t("detail.changed")}</Chip>
            : null}
        </View>
        <View style={styles.photoRow}>
          <PhotoPreview
            label={t("detail.currentValue")}
            hasPhoto={detail.currentSnapshot.hasIdentityPhoto}
            uri={detail.currentSnapshot.identityPhotoUrl}
            emptyLabel={t("detail.noPhoto")}
            unavailableLabel={t("detail.previewUnavailable")}
            onError={handlePhotoError}
          />
          <PhotoPreview
            label={t("detail.proposedValue")}
            hasPhoto={detail.hasIdentityPhoto}
            uri={detail.identityPhotoUrl}
            emptyLabel={t("detail.noPhoto")}
            unavailableLabel={t("detail.previewUnavailable")}
            onError={handlePhotoError}
          />
        </View>
      </Card.Content>
    </Card>
  );

  return (
    <>
      <ScrollView
        contentInsetAdjustmentBehavior="automatic"
        style={{ backgroundColor: theme.colors.background }}
        contentContainerStyle={styles.content}
      >
        <Card mode="outlined" style={styles.summarySurface}>
          <Card.Content style={styles.summaryCard}>
            <View style={styles.summaryTitleRow}>
              <View style={styles.flex}>
                <Text variant="titleLarge" selectable>{detail.username || detail.userGuid}</Text>
                <Text variant="bodySmall" style={{ color: theme.colors.onSurfaceVariant }} selectable>
                  {detail.storeNames.join(" · ") || t("list.storeUnavailable")}
                </Text>
              </View>
              <Chip icon="shield-check-outline">{t(`statuses.${detail.status}`)}</Chip>
            </View>
            <Text variant="bodyMedium" selectable>{t("detail.securityNotice")}</Text>
          </Card.Content>
        </Card>

        {changedDefinitions.map(renderComparison)}
        {photoChanged ? renderPhotoComparison() : null}

        {unchangedCount > 0 ? (
          <Button
            mode="text"
            icon={showUnchanged ? "chevron-up" : "chevron-down"}
            contentStyle={styles.touchTarget}
            onPress={() => setShowUnchanged((current) => !current)}
          >
            {showUnchanged
              ? t("detail.hideUnchanged")
              : t("detail.unchangedFields", { count: unchangedCount })}
          </Button>
        ) : null}
        {showUnchanged ? (
          <>
            {unchangedDefinitions.map(renderComparison)}
            {photoChanged ? null : renderPhotoComparison()}
          </>
        ) : null}

        {detail.reviewReason ? (
          <Card mode="outlined" style={styles.comparisonSurface}>
            <Card.Content style={styles.comparisonCard}>
              <Text variant="labelLarge" selectable>{t("detail.reviewReason")}</Text>
              <Text selectable>{detail.reviewReason}</Text>
            </Card.Content>
          </Card>
        ) : null}

        {staleAfterConflict ? (
          <Card mode="outlined" style={[styles.comparisonSurface, { backgroundColor: theme.colors.errorContainer }]}>
            <Card.Content>
              <Text selectable style={{ color: theme.colors.onErrorContainer }}>
                {t("messages.conflict")}
              </Text>
            </Card.Content>
          </Card>
        ) : null}

      </ScrollView>

      <Surface style={styles.reviewFooter} elevation={3}>
        {detail.status !== "Pending" ? (
          // 已处理的申请（从「已处理」分段进入）只读查看，提示无需再审核。
          <Text variant="bodyMedium" style={{ color: theme.colors.onSurfaceVariant, textAlign: "center" }} selectable>
            {t("detail.processedNotice")}
          </Text>
        ) : null}
        <View style={styles.reviewActions}>
          <Button
            mode="outlined"
            icon="close-circle-outline"
            disabled={actionsDisabled || reviewMutation.isPending}
            contentStyle={styles.touchTarget}
            onPress={() => {
              setReason("");
              setDialogAction("reject");
            }}
          >
            {t("actions.reject")}
          </Button>
          <Button
            mode="contained"
            icon="check-circle-outline"
            disabled={actionsDisabled || reviewMutation.isPending}
            loading={reviewMutation.isPending}
            contentStyle={styles.touchTarget}
            onPress={() => {
              setReason("");
              setDialogAction("approve");
            }}
          >
            {t("actions.approve")}
          </Button>
        </View>
      </Surface>

      <Portal>
        <Dialog visible={dialogAction !== null} onDismiss={() => setDialogAction(null)}>
          <Dialog.Title>
            {t(dialogAction === "reject" ? "dialog.rejectTitle" : "dialog.approveTitle")}
          </Dialog.Title>
          <Dialog.Content style={styles.dialogContent}>
            <Text selectable>
              {t(dialogAction === "reject" ? "dialog.rejectDescription" : "dialog.approveDescription")}
            </Text>
            <TextInput
              mode="outlined"
              multiline
              numberOfLines={3}
              maxLength={1000}
              label={t(dialogAction === "reject" ? "dialog.reasonRequired" : "dialog.reasonOptional")}
              value={reason}
              error={rejectReasonInvalid}
              onChangeText={setReason}
            />
            {rejectReasonInvalid ? (
              <Text variant="bodySmall" style={{ color: theme.colors.error }} selectable>
                {t("dialog.reasonRequiredMessage")}
              </Text>
            ) : null}
          </Dialog.Content>
          <Dialog.Actions>
            <Button contentStyle={styles.touchTarget} onPress={() => setDialogAction(null)}>
              {t("actions.cancel")}
            </Button>
            <Button
              mode="contained"
              loading={reviewMutation.isPending}
              disabled={reviewMutation.isPending || rejectReasonInvalid}
              contentStyle={styles.touchTarget}
              onPress={() => reviewMutation.mutate()}
            >
              {t("actions.confirm")}
            </Button>
          </Dialog.Actions>
        </Dialog>
      </Portal>

      <Snackbar visible={Boolean(snackbarMessage)} onDismiss={() => setSnackbarMessage("")} duration={5000}>
        {snackbarMessage}
      </Snackbar>
    </>
  );
}

function PhotoPreview({
  label,
  hasPhoto,
  uri,
  emptyLabel,
  unavailableLabel,
  onError,
}: {
  label: string;
  hasPhoto: boolean;
  uri: string;
  emptyLabel: string;
  unavailableLabel: string;
  onError: (url: string) => void;
}) {
  return (
    <View style={styles.photoColumn}>
      <Text variant="labelMedium" selectable>{label}</Text>
      {hasPhoto && uri ? (
        <Image
          source={{ uri }}
          style={styles.photo}
          resizeMode="contain"
          accessibilityLabel={label}
          onError={() => onError(uri)}
        />
      ) : (
        <View style={styles.photoEmpty}>
          <Text variant="bodySmall" selectable>
            {hasPhoto ? unavailableLabel : emptyLabel}
          </Text>
        </View>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  headerSafeArea: { width: "100%" },
  headerRow: {
    height: 44,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  headerBackButton: {
    position: "absolute",
    left: 4,
    width: 44,
    height: 44,
    alignItems: "center",
    justifyContent: "center",
  },
  headerTitle: { flexShrink: 1, textAlign: "center", marginHorizontal: 52 },
  headerBackButtonPressed: { opacity: 0.6 },
  headerBackButtonDisabled: { opacity: 0.4 },
  flex: { flex: 1 },
  centered: { flex: 1, padding: 24, alignItems: "center", justifyContent: "center", gap: 16 },
  privacyShield: { flex: 1, padding: 32, alignItems: "center", justifyContent: "center", gap: 16 },
  content: { ...BUSINESS_UI.content, paddingBottom: 112 },
  summarySurface: BUSINESS_UI.section,
  comparisonSurface: BUSINESS_UI.section,
  summaryCard: { ...BUSINESS_UI.sectionContent, gap: HB_SPACING.xs },
  summaryTitleRow: { flexDirection: "row", alignItems: "flex-start", gap: HB_SPACING.sm },
  comparisonCard: { ...BUSINESS_UI.sectionContent, gap: HB_SPACING.sm },
  fieldTitleRow: { minHeight: 48, flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  fieldActions: { flexDirection: "row", alignItems: "center", flexWrap: "wrap", justifyContent: "flex-end", gap: HB_SPACING.xxs },
  valueRow: { flexDirection: "row", alignItems: "flex-start", gap: HB_SPACING.md },
  valueColumn: { flex: 1, gap: HB_SPACING.xxs },
  photoRow: { flexDirection: "row", alignItems: "stretch", gap: HB_SPACING.sm },
  photoColumn: { flex: 1, gap: HB_SPACING.xs },
  photo: { width: "100%", height: 180, borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.surfaceMuted },
  photoEmpty: { minHeight: 180, alignItems: "center", justifyContent: "center", borderWidth: 1, borderColor: HB_COLORS.outline, borderRadius: HB_RADIUS.control },
  reviewFooter: BUSINESS_UI.footer,
  reviewActions: { flexDirection: "row", gap: HB_SPACING.sm },
  touchTarget: { minHeight: 48 },
  dialogContent: { gap: 12 },
});

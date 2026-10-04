import { useCallback, useMemo, useState } from "react";
import { Alert, ScrollView, StyleSheet, View } from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import { useQuery } from "@tanstack/react-query";
import {
  ActivityIndicator,
  Avatar,
  Button,
  Card,
  Chip,
  Dialog,
  IconButton,
  Portal,
  Snackbar,
  Text,
  TextInput,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { WeeklyScheduleTable } from "@/components/attendance/WeeklyScheduleTable";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { EmptyState } from "@/components/ui/EmptyState";
import {
  StaffAttendanceEndpointUnavailableError,
  getStaffAttendanceRecords,
  getStaffAttendanceWeek,
  useStoreUserMutations,
  useStoreUserProfile,
  type StaffAttendanceRecord,
} from "@/modules/users";
import { calculateAge, maskTrailingFour } from "@/modules/users/profile-display";
import { getEmployeeProfileReviewAccess } from "@/modules/employee-profile-review/access";
import { getEmployeeProfileReviewRequestsApi } from "@/modules/employee-profile-review/api";
import { useAppNavigationStore } from "@/modules/navigation/store";
import { hasDeliverableEmail } from "@/modules/users/staff-email-username";
import { PERMISSIONS } from "@/shared/utils/access";
import { useAuthStore } from "@/store/auth-store";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { resolveLocaleTag } from "@/shared/i18n/types";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

type DetailTab = "personal" | "schedule" | "records";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value;
}

function getInitials(name: string) {
  const words = name.trim().split(/\s+/).filter(Boolean);
  if (!words.length) {
    return "?";
  }
  if (words.length === 1) {
    return words[0].slice(0, 2).toUpperCase();
  }
  return `${words[0][0]}${words[1][0]}`.toUpperCase();
}

function formatDate(value: string | undefined, locale: string) {
  if (!value) {
    return null;
  }
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return value.slice(0, 10);
  }
  return new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(
    parsed,
  );
}

function formatDateTime(value: string | undefined, locale: string) {
  if (!value) {
    return null;
  }
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return value;
  }
  return new Intl.DateTimeFormat(locale, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(parsed);
}

function formatShortValue(value: string | undefined) {
  if (!value) {
    return null;
  }
  if (value.includes("T")) {
    return value.split("T").pop()?.slice(0, 5) ?? value;
  }
  return value.length >= 5 && value.includes(":") ? value.slice(0, 5) : value;
}

function getCurrentWeekStartDate() {
  const date = new Date();
  const diff = (date.getDay() + 6) % 7;
  date.setDate(date.getDate() - diff);
  date.setHours(0, 0, 0, 0);
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

function DetailRow({
  emptyValue,
  icon,
  label,
  value,
}: {
  emptyValue: string;
  icon: string;
  label: string;
  value?: string | null;
}) {
  return (
    <View style={styles.detailRow}>
      <Avatar.Icon size={34} icon={icon} style={styles.detailIcon} />
      <View style={styles.detailCopy}>
        <Text variant="labelMedium" style={styles.muted}>
          {label}
        </Text>
        <Text variant="bodyLarge">{value || emptyValue}</Text>
      </View>
    </View>
  );
}

export default function StaffDetailScreen() {
  const router = useRouter();
  const params = useLocalSearchParams();
  const { t, language } = useAppTranslation([
    "userManagement",
    "common",
    "attendance",
  ]);
  const locale = useMemo(() => resolveLocaleTag(language), [language]);
  const userGuid = firstParam(params.userGuid);
  const storeCode = firstParam(params.storeCode);
  const profileQuery = useStoreUserProfile(userGuid, storeCode);
  const { statusMutation, setupEmailMutation } = useStoreUserMutations(
    storeCode,
    undefined,
  );
  const [snackbarMessage, setSnackbarMessage] = useState("");
  const [resetPasswordVisible, setResetPasswordVisible] = useState(false);
  const [activeTab, setActiveTab] = useState<DetailTab>("personal");
  const currentUser = useAuthStore((state) => state.user);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const navigationItems = useAppNavigationStore((state) => state.items);
  const navigationReady = useAppNavigationStore((state) => state.isReady);
  // 与审核列表同一套前端门禁；无审核权限时不显示敏感资料行，也不发请求，最终范围由后端裁决。
  const reviewAccess = useMemo(
    () => getEmployeeProfileReviewAccess({
      roleNames: currentUser?.roleNames,
      permissions: currentUser?.permissions,
      menuRouteNames: navigationItems.map((item) => item.routeName),
      sessionKind,
    }),
    [currentUser?.permissions, currentUser?.roleNames, navigationItems, sessionKind]
  );
  const pendingSensitiveQuery = useQuery({
    queryKey: ["employeeProfileReview", "requests", "byUser", userGuid ?? ""],
    enabled: Boolean(userGuid) && navigationReady && reviewAccess.allowed,
    // 只取一条摘要：列表接口不含任何敏感值，超出审核范围时后端返回空列表。
    queryFn: () => getEmployeeProfileReviewRequestsApi({
      page: 1,
      pageSize: 1,
      status: "Pending",
      userGuid: userGuid ?? "",
    }),
    staleTime: 30_000,
  });
  const pendingSensitiveRequest = pendingSensitiveQuery.data?.items[0];
  const access = useAuthStore((state) => state.access);
  // 按钮可见性与后端授权一致：本店店员专用权限或全局用户权限任一即可，后端仍会校验分店与目标范围。
  const canEditUsers = access.isAdmin
    || access.hasPermission(PERMISSIONS.Users.Edit)
    || access.hasPermission(PERMISSIONS.Users.EditStoreStaff);
  const canResetPasswords = access.isAdmin
    || access.hasPermission(PERMISSIONS.Users.ResetPassword)
    || access.hasPermission(PERMISSIONS.Users.ResetStoreStaffPassword);

  const profile = profileQuery.data;
  const title = profile?.fullName || profile?.username || t("detail.title");
  const resolvedStoreCode = profile?.storeCode || storeCode;
  const staffWeekStartDate = useMemo(() => getCurrentWeekStartDate(), []);
  const isActive = profile?.status === 1;
  const emptyValue = t("common:na");
  const age = calculateAge(profile?.birthday);
  const gender = profile?.gender
    ? t(`detail.genders.${profile.gender}`, profile.gender)
    : emptyValue;
  const employmentType = profile?.employmentType
    ? t(`detail.employmentTypes.${profile.employmentType}`, profile.employmentType)
    : emptyValue;

  const handleBack = useCallback(() => {
    if (router.canGoBack()) {
      router.back();
      return;
    }
    router.replace("/(shell)/users" as unknown as Parameters<typeof router.replace>[0]);
  }, [router]);

  const handleResetPassword = useCallback(async () => {
    if (!userGuid || !storeCode) {
      return;
    }

    // 店长不再手动设新密码：给员工邮箱发验证码，员工在登录页自己设置。
    try {
      const result = await setupEmailMutation.mutateAsync({ userGuid, storeCode });
      setResetPasswordVisible(false);
      setSnackbarMessage(t("messages.passwordSetupEmailSent", { email: result.maskedEmail }));
    } catch (error) {
      console.warn("[staff-profile] password reset failed", error);
      setSnackbarMessage(
        resolveLocalizedErrorMessage(error, {
          t,
          language,
          fallbackKey: "messages.passwordResetFailed",
        }),
      );
    }
  }, [language, setupEmailMutation, storeCode, t, userGuid]);

  const handleToggleStatus = useCallback(() => {
    if (!profile || !storeCode) {
      return;
    }

    const nextEnabled = profile.status !== 1;
    const actionLabel = nextEnabled
      ? t("actions.enable")
      : t("actions.disable");
    Alert.alert(
      actionLabel,
      t("dialogs.statusConfirmMessage", {
        action: actionLabel,
        username: profile.username,
      }),
      [
        { text: t("actions.cancel"), style: "cancel" },
        {
          text: actionLabel,
          style: nextEnabled ? "default" : "destructive",
          onPress: async () => {
            try {
              await statusMutation.mutateAsync({
                userGuid: profile.userGUID,
                storeCode,
                status: nextEnabled ? 1 : 0,
              });
              await profileQuery.refetch();
              setSnackbarMessage(
                nextEnabled
                  ? t("messages.userEnabled")
                  : t("messages.userDisabled"),
              );
            } catch (error) {
              console.warn("[staff-profile] status failed", error);
              setSnackbarMessage(
                resolveLocalizedErrorMessage(error, {
                  t,
                  language,
                  fallbackKey: "messages.statusFailed",
                }),
              );
            }
          },
        },
      ],
    );
  }, [language, profile, profileQuery, statusMutation, storeCode, t]);

  const scheduleQuery = useQuery({
    queryKey: [
      "staffAttendance",
      "week",
      resolvedStoreCode ?? "",
      userGuid ?? "",
      staffWeekStartDate,
    ],
    enabled: Boolean(
      activeTab === "schedule" && userGuid && resolvedStoreCode,
    ),
    queryFn: () =>
      getStaffAttendanceWeek({
        userGuid: userGuid!,
        storeCode: resolvedStoreCode,
        weekStartDate: staffWeekStartDate,
      }),
  });

  const recordsQuery = useQuery({
    queryKey: [
      "staffAttendance",
      "records",
      resolvedStoreCode ?? "",
      userGuid ?? "",
    ],
    enabled: Boolean(activeTab === "records" && userGuid && resolvedStoreCode),
    queryFn: () =>
      getStaffAttendanceRecords({
        userGuid: userGuid!,
        storeCode: resolvedStoreCode,
        limit: 20,
      }),
  });

  const openSchedule = useCallback(() => {
    setActiveTab("schedule");
  }, []);

  const renderTabStateCard = useCallback(
    ({
      title,
      description,
      actionLabel,
      onPress,
    }: {
      title: string;
      description: string;
      actionLabel?: string;
      onPress?: () => void;
    }) => (
      <Card mode="elevated" style={styles.sectionCard}>
        <Card.Content style={[styles.sectionContent, styles.stateCardContent]}>
          <Text variant="titleMedium">{title}</Text>
          <Text variant="bodyMedium" style={styles.muted}>
            {description}
          </Text>
          {actionLabel && onPress ? (
            <Button mode="outlined" icon="refresh" onPress={onPress}>
              {actionLabel}
            </Button>
          ) : null}
        </Card.Content>
      </Card>
    ),
    [],
  );

  const renderScheduleTab = useCallback(() => {
    if (scheduleQuery.isLoading && !scheduleQuery.data) {
      return (
        <Card mode="elevated" style={styles.sectionCard}>
          <Card.Content style={[styles.sectionContent, styles.stateCardContent]}>
            <ActivityIndicator size="small" />
            <Text variant="bodyMedium" style={styles.muted}>
              {t("detail.schedule.loading")}
            </Text>
          </Card.Content>
        </Card>
      );
    }

    if (scheduleQuery.isError && !scheduleQuery.data) {
      const description =
        scheduleQuery.error instanceof StaffAttendanceEndpointUnavailableError
          ? t("detail.schedule.endpointUnavailable")
          : resolveLocalizedErrorMessage(scheduleQuery.error, {
              t,
              language,
              fallbackKey: "detail.schedule.loadFailedDescription",
            });

      return renderTabStateCard({
        title: t("detail.schedule.loadFailedTitle"),
        description,
        actionLabel: t("common:actions.retry"),
        onPress: () => void scheduleQuery.refetch(),
      });
    }

    return (
      <>
        <Card mode="elevated" style={styles.sectionCard}>
          <Card.Content style={styles.sectionContent}>
            <Text variant="titleMedium">{t("detail.schedule.title")}</Text>
            <Text variant="bodyMedium" style={styles.muted}>
              {t("detail.schedule.description")}
            </Text>
          </Card.Content>
        </Card>
        <WeeklyScheduleTable week={scheduleQuery.data} />
      </>
    );
  }, [renderTabStateCard, scheduleQuery, t]);

  const getRecordTypeLabel = useCallback(
    (record: StaffAttendanceRecord) => {
      if (record.type === "Leave") {
        return record.leaveType
          ? t(`attendance:leaveTypes.${record.leaveType}`, record.leaveType)
          : t("detail.records.types.leave");
      }
      if (record.type === "Approval") {
        return record.sourceType
          ? t(`attendance:sourceTypes.${record.sourceType}`, record.sourceType)
          : t("detail.records.types.approval");
      }
      if (record.punchType) {
        return t(`attendance:punchTypes.${record.punchType}`, record.punchType);
      }
      return t("detail.records.types.punch");
    },
    [t],
  );

  const renderRecordMeta = useCallback(
    (record: StaffAttendanceRecord) => {
      const submittedAt = formatDateTime(record.submittedAt, locale);
      const workDateValue = formatDate(record.workDate, locale);
      const startValue = record.startTime?.includes("-")
        ? formatDate(record.startTime, locale)
        : formatShortValue(record.startTime);
      const endValue = record.endTime?.includes("-")
        ? formatDate(record.endTime, locale)
        : formatShortValue(record.endTime);
      const rows: Array<{ label: string; value: string | null }> = [];

      if (record.type === "Leave") {
        rows.push({
          label: t("detail.records.dateRange"),
          value:
            startValue && endValue ? `${startValue} - ${endValue}` : startValue || endValue,
        });
      } else {
        rows.push({
          label: t("detail.records.workDate"),
          value: workDateValue,
        });
      }

      if (submittedAt) {
        rows.push({
          label: t("detail.records.submittedAt"),
          value: submittedAt,
        });
      }

      if (record.storeName || record.storeCode) {
        rows.push({
          label: t("detail.records.store"),
          value: record.storeName || record.storeCode || null,
        });
      }

      return rows
        .filter((row) => row.value)
        .map((row) => (
          <View key={`${record.recordGuid}-${row.label}`} style={styles.recordMetaRow}>
            <Text variant="labelMedium" style={styles.muted}>
              {row.label}
            </Text>
            <Text variant="bodyMedium" style={styles.recordMetaValue}>
              {row.value}
            </Text>
          </View>
        ));
    },
    [locale, t],
  );

  const renderRecordsTab = useCallback(() => {
    if (recordsQuery.isLoading && !recordsQuery.data) {
      return (
        <Card mode="elevated" style={styles.sectionCard}>
          <Card.Content style={[styles.sectionContent, styles.stateCardContent]}>
            <ActivityIndicator size="small" />
            <Text variant="bodyMedium" style={styles.muted}>
              {t("detail.records.loading")}
            </Text>
          </Card.Content>
        </Card>
      );
    }

    if (recordsQuery.isError && !recordsQuery.data) {
      const description =
        recordsQuery.error instanceof StaffAttendanceEndpointUnavailableError
          ? t("detail.records.endpointUnavailable")
          : resolveLocalizedErrorMessage(recordsQuery.error, {
              t,
              language,
              fallbackKey: "detail.records.loadFailedDescription",
            });

      return renderTabStateCard({
        title: t("detail.records.loadFailedTitle"),
        description,
        actionLabel: t("common:actions.retry"),
        onPress: () => void recordsQuery.refetch(),
      });
    }

    const records = recordsQuery.data ?? [];
    if (!records.length) {
      return renderTabStateCard({
        title: t("detail.records.emptyTitle"),
        description: t("detail.records.emptyDescription"),
      });
    }

    return (
      <Card mode="elevated" style={styles.sectionCard}>
        <Card.Content style={styles.sectionContent}>
          <Text variant="titleMedium">{t("detail.records.title")}</Text>
          <Text variant="bodyMedium" style={styles.muted}>
            {t("detail.records.description")}
          </Text>
          {records.map((record) => (
            <View
              key={`${record.type}-${record.recordGuid || record.workDate}`}
              style={styles.recordCard}
            >
              <View style={styles.recordHeader}>
                <View style={styles.recordHeaderCopy}>
                  <Text variant="titleSmall">{getRecordTypeLabel(record)}</Text>
                  <Text variant="bodySmall" style={styles.muted}>
                    {t(`attendance:statuses.${record.status}`, record.status)}
                  </Text>
                </View>
                <Chip compact>{t(`attendance:statuses.${record.status}`, record.status)}</Chip>
              </View>
              <View style={styles.recordMetaBlock}>{renderRecordMeta(record)}</View>
              {record.detail ? (
                <Text variant="bodySmall" style={styles.muted}>
                  {record.detail}
                </Text>
              ) : null}
            </View>
          ))}
        </Card.Content>
      </Card>
    );
  }, [getRecordTypeLabel, recordsQuery, renderRecordMeta, renderTabStateCard, t]);

  const renderTabContent = useCallback(() => {
    if (activeTab === "schedule") {
      return renderScheduleTab();
    }
    if (activeTab === "records") {
      return renderRecordsTab();
    }
    return (
      <>
        <Card mode="elevated" style={styles.sectionCard}>
          <Card.Content style={styles.sectionContent}>
            <Text variant="titleMedium">
              {t("detail.sections.employeeDetails")}
            </Text>
            <DetailRow
              emptyValue={emptyValue}
              icon="badge-account-outline"
              label={t("detail.fields.employeeId")}
              value={profile?.userGUID}
            />
            <DetailRow
              emptyValue={emptyValue}
              icon="calendar-account-outline"
              label={t("detail.fields.age")}
              value={age === null ? null : String(age)}
            />
            <DetailRow
              emptyValue={emptyValue}
              icon="account-heart-outline"
              label={t("detail.fields.gender")}
              value={gender}
            />
            <DetailRow
              emptyValue={emptyValue}
              icon="briefcase-account-outline"
              label={t("detail.fields.employmentType")}
              value={employmentType}
            />
            <DetailRow
              emptyValue={emptyValue}
              icon="phone-outline"
              label={t("detail.fields.phone")}
              value={profile?.phone}
            />
            <DetailRow
              emptyValue={emptyValue}
              icon="email-outline"
              label={t("detail.fields.email")}
              value={profile?.email}
            />
            <DetailRow
              emptyValue={emptyValue}
              icon="storefront-outline"
              label={t("detail.fields.store")}
              value={profile?.storeName || profile?.storeCode}
            />
            <DetailRow
              emptyValue={emptyValue}
              icon="calendar-outline"
              label={t("detail.fields.joinDate")}
              value={formatDate(profile?.createdAt, locale)}
            />
            <DetailRow
              emptyValue={emptyValue}
              icon="cake-variant-outline"
              label={t("detail.fields.birthday")}
              value={formatDate(profile?.birthday, locale)}
            />
            <DetailRow
              emptyValue={emptyValue}
              icon="map-marker-outline"
              label={t("detail.fields.address")}
              value={profile?.address}
            />
          </Card.Content>
        </Card>

        <Card mode="elevated" style={styles.sectionCard}>
          <Card.Content style={styles.sectionContent}>
            <Text variant="titleMedium">
              {t("detail.sections.accountInfo")}
            </Text>
            <DetailRow
              emptyValue={emptyValue}
              icon="badge-account-outline"
              label={t("detail.fields.identityId")}
              value={maskTrailingFour(profile?.identityId, emptyValue)}
            />
            <DetailRow
              emptyValue={emptyValue}
              icon="credit-card-outline"
              label={t("detail.fields.bankAccount")}
              value={maskTrailingFour(profile?.bankAccountNumber, emptyValue)}
            />
            <DetailRow
              emptyValue={emptyValue}
              icon="identifier"
              label={t("detail.fields.superAccount")}
              value={maskTrailingFour(profile?.superannuationAccountNumber, emptyValue)}
            />
          </Card.Content>
        </Card>

        <Card mode="elevated" style={styles.sectionCard}>
          <Card.Content style={styles.sectionContent}>
            <Text variant="titleMedium">{t("detail.sections.payroll")}</Text>
            <DetailRow
              emptyValue={emptyValue}
              icon="bank-outline"
              label={t("detail.fields.bankBsb")}
              value={profile?.bankBsb}
            />
            <DetailRow
              emptyValue={emptyValue}
              icon="shield-account-outline"
              label={t("detail.fields.superCompany")}
              value={profile?.superannuationCompanyName}
            />
          </Card.Content>
        </Card>

        {/* A4 资料状态：首次改密 + 敏感资料是否有待审申请（无审核权限不显示该行）。 */}
        <Card mode="elevated" style={styles.sectionCard}>
          <Card.Content style={styles.sectionContent}>
            <Text variant="titleMedium">
              {t("detail.sections.profileStatus")}
            </Text>
            <View style={styles.statusRow}>
              <Avatar.Icon size={34} icon="lock-reset" style={styles.detailIcon} />
              <View style={styles.detailCopy}>
                <Text variant="bodyLarge">
                  {t("detail.profileStatus.password")}
                </Text>
              </View>
              <Chip
                compact
                style={profile?.mustChangePassword ? styles.warningChip : styles.okChip}
                textStyle={profile?.mustChangePassword ? styles.warningChipText : styles.okChipText}
              >
                {profile?.mustChangePassword
                  ? t("detail.profileStatus.passwordPending")
                  : t("detail.profileStatus.passwordDone")}
              </Chip>
            </View>
            {reviewAccess.allowed ? (
              <View style={styles.statusRow}>
                <Avatar.Icon size={34} icon="shield-account-outline" style={styles.detailIcon} />
                <View style={styles.detailCopy}>
                  <Text variant="labelMedium" style={styles.muted}>
                    {t("detail.profileStatus.sensitive")}
                  </Text>
                  <Text variant="bodyLarge">
                    {pendingSensitiveQuery.isLoading
                      ? t("detail.profileStatus.sensitiveLoading")
                      : pendingSensitiveQuery.isError
                        ? t("detail.profileStatus.sensitiveFailed")
                        : pendingSensitiveRequest
                          ? t("detail.profileStatus.sensitivePending")
                          : t("detail.profileStatus.sensitiveNone")}
                  </Text>
                </View>
                {pendingSensitiveRequest ? (
                  <Button
                    compact
                    mode="contained-tonal"
                    onPress={() => router.push({
                      pathname: "/employee-profile-review/[requestId]",
                      params: { requestId: String(pendingSensitiveRequest.requestId) },
                    })}
                  >
                    {t("detail.profileStatus.goReview")}
                  </Button>
                ) : pendingSensitiveQuery.isError ? (
                  <IconButton
                    icon="refresh"
                    accessibilityLabel={t("common:actions.retry")}
                    onPress={() => void pendingSensitiveQuery.refetch()}
                  />
                ) : null}
              </View>
            ) : null}
          </Card.Content>
        </Card>

        <Card mode="elevated" style={styles.sectionCard}>
          <Card.Content style={styles.sectionContent}>
            <Text variant="titleMedium">
              {t("detail.sections.quickActions")}
            </Text>
            <Button
              mode="outlined"
              icon="calendar-month-outline"
              onPress={openSchedule}
            >
              {t("detail.actions.viewFullSchedule")}
            </Button>
            {canResetPasswords ? (
              <Button
                mode="outlined"
                icon="key-outline"
                onPress={() => setResetPasswordVisible(true)}
              >
                {t("actions.resetPassword")}
              </Button>
            ) : null}
            {canEditUsers ? (
              <Button
                mode={isActive ? "outlined" : "contained-tonal"}
                icon={isActive ? "block-helper" : "check-circle-outline"}
                onPress={handleToggleStatus}
                loading={statusMutation.isPending}
              >
                {isActive ? t("actions.disable") : t("actions.enable")}
              </Button>
            ) : null}
          </Card.Content>
        </Card>
      </>
    );
  }, [
    activeTab,
    age,
    canEditUsers,
    canResetPasswords,
    employmentType,
    emptyValue,
    gender,
    handleToggleStatus,
    isActive,
    locale,
    openSchedule,
    pendingSensitiveQuery,
    pendingSensitiveRequest,
    profile,
    renderRecordsTab,
    renderScheduleTab,
    reviewAccess.allowed,
    router,
    statusMutation.isPending,
    t,
  ]);

  if (!userGuid || !storeCode) {
    return (
      <SafeAreaView style={styles.container}>
        <EmptyState
          title={t("detail.missingTitle")}
          description={t("detail.missingDescription")}
          primaryAction={{
            label: t("common:actions.back"),
            icon: "arrow-left",
            onPress: handleBack,
          }}
        />
      </SafeAreaView>
    );
  }

  if (profileQuery.isLoading && !profile) {
    return (
      <SafeAreaView style={styles.container}>
        <View style={styles.centered}>
          <ActivityIndicator size="large" />
        </View>
      </SafeAreaView>
    );
  }

  if (profileQuery.isError && !profile) {
    return (
      <SafeAreaView style={styles.container}>
        <EmptyState
          title={t("detail.loadFailedTitle")}
          description={resolveLocalizedErrorMessage(profileQuery.error, {
            t,
            language,
            fallbackKey: "messages.loadFailedDescription",
          })}
          primaryAction={{
            label: t("common:actions.retry"),
            icon: "refresh",
            onPress: () => void profileQuery.refetch(),
          }}
          secondaryAction={{
            label: t("common:actions.back"),
            icon: "arrow-left",
            onPress: handleBack,
          }}
        />
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView style={styles.container} edges={["top", "left", "right"]}>
      <ScrollView contentContainerStyle={styles.content}>
        <View style={styles.topBar}>
          <IconButton icon="arrow-left" onPress={handleBack} />
          <Text variant="titleLarge">{t("detail.title")}</Text>
          <IconButton
            icon="pencil-outline"
            onPress={() => router.replace("/(shell)/users" as unknown as Parameters<typeof router.replace>[0])}
          />
        </View>

        <Card mode="elevated" style={styles.heroCard}>
          <Card.Content style={styles.heroContent}>
            <Avatar.Text
              size={72}
              label={getInitials(title)}
              style={styles.heroAvatar}
            />
            <View style={styles.heroCopy}>
              <Text variant="headlineSmall">{title}</Text>
              <Text variant="bodyMedium" style={styles.muted}>
                {profile?.employmentType
                  ? t(
                      `detail.employmentTypes.${profile.employmentType}`,
                      profile.employmentType,
                    )
                  : t("fields.positionValue")}
              </Text>
              <View style={styles.heroChips}>
                <Chip
                  compact
                  style={isActive ? styles.activeChip : styles.inactiveChip}
                >
                  {isActive ? t("statuses.active") : t("statuses.disabled")}
                </Chip>
                {profile?.mustChangePassword ? (
                  <Chip compact style={styles.pendingChip}>{t("statuses.pendingFirstLogin")}</Chip>
                ) : null}
              </View>
            </View>
          </Card.Content>
        </Card>

        <View style={styles.segmentTabs}>
          <Chip
            selected={activeTab === "personal"}
            onPress={() => setActiveTab("personal")}
          >
            {t("detail.tabs.personalInfo")}
          </Chip>
          <Chip
            selected={activeTab === "schedule"}
            onPress={() => setActiveTab("schedule")}
          >
            {t("detail.tabs.schedule")}
          </Chip>
          <Chip
            selected={activeTab === "records"}
            onPress={() => setActiveTab("records")}
          >
            {t("detail.tabs.records")}
          </Chip>
        </View>
        {renderTabContent()}
      </ScrollView>

      <Portal>
        <Dialog
          visible={resetPasswordVisible}
          onDismiss={() => setResetPasswordVisible(false)}
        >
          <Dialog.Title>{t("dialogs.resetPasswordTitle")}</Dialog.Title>
          <Dialog.Content style={styles.dialogContent}>
            {hasDeliverableEmail(profile?.email) ? (
              <Text variant="bodyMedium">
                {t("dialogs.resetPasswordEmailDescription", {
                  username: profile?.fullName || profile?.username || "",
                  email: profile?.email ?? "",
                })}
              </Text>
            ) : (
              <Text variant="bodyMedium" style={styles.muted}>{t("dialogs.resetPasswordNoEmail")}</Text>
            )}
          </Dialog.Content>
          <Dialog.Actions>
            <Button
              onPress={() => setResetPasswordVisible(false)}
              disabled={setupEmailMutation.isPending}
            >
              {t("actions.cancel")}
            </Button>
            <Button
              onPress={handleResetPassword}
              loading={setupEmailMutation.isPending}
              disabled={setupEmailMutation.isPending || !hasDeliverableEmail(profile?.email)}
            >
              {t("actions.sendPasswordSetupEmail")}
            </Button>
          </Dialog.Actions>
        </Dialog>
      </Portal>

      <Snackbar
        visible={Boolean(snackbarMessage)}
        onDismiss={() => setSnackbarMessage("")}
        duration={2500}
      >
        {snackbarMessage}
      </Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  activeChip: {
    alignSelf: "flex-start",
    backgroundColor: "#DFF7E8",
  },
  centered: {
    alignItems: "center",
    flex: 1,
    justifyContent: "center",
  },
  container: {
    backgroundColor: HB_COLORS.background,
    flex: 1,
  },
  content: {
    ...BUSINESS_UI.content,
    gap: HB_SPACING.sm,
    paddingBottom: HB_SPACING.xl,
  },
  detailCopy: {
    flex: 1,
    gap: 2,
  },
  detailIcon: {
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  detailRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: HB_SPACING.sm,
  },
  dialogContent: {
    gap: 12,
  },
  heroAvatar: {
    backgroundColor: HB_COLORS.action,
  },
  heroCard: {
    ...BUSINESS_UI.section,
  },
  heroContent: {
    alignItems: "center",
    flexDirection: "row",
    gap: HB_SPACING.md,
  },
  heroCopy: {
    flex: 1,
    gap: 6,
  },
  inactiveChip: {
    alignSelf: "flex-start",
    backgroundColor: "#FDECEC",
  },
  pendingChip: {
    backgroundColor: "#EAF2FF",
  },
  heroChips: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
  },
  muted: {
    color: HB_COLORS.textSecondary,
  },
  recordCard: {
    backgroundColor: HB_COLORS.surface,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.control,
    borderWidth: StyleSheet.hairlineWidth,
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
  },
  recordHeader: {
    alignItems: "flex-start",
    flexDirection: "row",
    gap: 8,
    justifyContent: "space-between",
  },
  recordHeaderCopy: {
    flex: 1,
    gap: 2,
  },
  recordMetaBlock: {
    gap: 6,
  },
  recordMetaRow: {
    gap: 2,
  },
  recordMetaValue: {
    color: HB_COLORS.textPrimary,
  },
  sectionCard: {
    ...BUSINESS_UI.section,
  },
  sectionContent: {
    gap: HB_SPACING.sm,
  },
  segmentTabs: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
  },
  stateCardContent: {
    alignItems: "center",
  },
  statusRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: HB_SPACING.sm,
  },
  warningChip: { backgroundColor: "#FEF0C7" },
  warningChipText: { color: "#7A2E0E" },
  okChip: { backgroundColor: "#ECFDF3" },
  okChipText: { color: HB_COLORS.success },
  topBar: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
  },
});

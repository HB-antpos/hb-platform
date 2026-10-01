import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Alert,
  Linking,
  Modal,
  RefreshControl,
  ScrollView,
  StyleSheet,
  View,
} from "react-native";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { CameraView } from "expo-camera";
import { useRouter } from "expo-router";
import {
  ActivityIndicator,
  Button,
  SegmentedButtons,
  Snackbar,
  Text,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { AvailabilitySheet } from "@/components/attendance/AvailabilitySheet";
import { HolidayManagementCard } from "@/components/attendance/HolidayManagementCard";
import { LeaveManagementCard } from "@/components/attendance/LeaveManagementCard";
import { ManagedPunchRecordsCard } from "@/components/attendance/ManagedPunchRecordsCard";
import { ManagerApprovalList } from "@/components/attendance/ManagerApprovalList";
import { MonthDatePickerField } from "@/components/attendance/MonthDatePicker";
import { PunchAdjustmentCard } from "@/components/attendance/PunchAdjustmentCard";
import { ScheduleManagementCard } from "@/components/attendance/ScheduleManagementCard";
import { TodayPunchCard } from "@/components/attendance/TodayPunchCard";
import { MyRequestsPanel } from "@/components/attendance/MyRequestsPanel";
import { MySchedulePanel } from "@/components/attendance/MySchedulePanel";
import { shiftDate } from "@/modules/attendance/attendance-my-week";
import { EmptyState } from "@/components/ui/EmptyState";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import {
  approveAttendanceApproval,
  cancelAvailability,
  createAttendanceHoliday,
  createAttendanceSchedule,
  createAvailabilityBatch,
  verifyAvailabilityBatch,
  createMyAttendancePunchAdjustment,
  createManagedLeaveRequest,
  deleteAttendanceHoliday,
  deleteAttendanceSchedule,
  getAttendanceHolidays,
  getAttendanceSchedulesWeek,
  getMyAttendanceToday,
  getMyAttendanceWeek,
  getMyAvailability,
  getAttendanceEmployees,
  getPendingApprovals,
  punchAttendance,
  publishAttendanceSchedulesWeek,
  previewMyAttendancePunchAdjustment,
  rejectAttendanceApproval,
  resolveAttendanceQr,
  syncAttendanceHolidays,
  updateAttendanceHoliday,
  updateAttendanceSchedule,
  updateAvailability,
} from "@/modules/attendance/api";
import {
  applyAttendanceTrackingLifecycle,
  buildAttendanceQrPunchPayload,
  getAttendancePunchErrorCode,
  getAttendancePunchErrorKey,
  normalizeAttendanceQrTokenInput,
  prepareAttendanceQrPunch,
  resolveAttendanceQrStore,
  shouldEnableAttendanceQrScanning,
  validateAttendanceQrToken,
} from "@/modules/attendance/attendance-qr";
import {
  createAttendanceQrScanSessionGate,
  type AttendanceQrScanSession,
} from "@/modules/attendance/attendance-qr-scan-session";
import { completeAttendancePostSave } from "@/modules/attendance/attendance-post-save";
import { createAttendanceQrPerformance } from "@/modules/attendance/attendance-qr-performance";
import {
  ensureAttendanceBackgroundLocationPermission,
  hasAttendanceBackgroundLocationPermission,
  startAttendanceLocationTracking,
  stopAttendanceLocationTracking,
} from "@/modules/attendance/location-tracking";
import {
  PUBLIC_HOLIDAY_SYNC_DAYS_AHEAD,
  normalizeAustralianHolidayJurisdiction,
  resolveAustralianHolidayJurisdiction,
} from "@/modules/attendance/public-holiday-sync";
import { getAttendanceDeviceContext } from "@/modules/attendance/required-location";
import type {
  AttendanceAvailability,
  AttendanceAvailabilityPayload,
  AttendancePunch,
  AttendancePunchMutationResult,
  AttendancePunchVerificationState,
  AttendanceSchedulePayload,
  AttendanceScheduleUpdatePayload,
  AttendanceStoreHolidayPayload,
} from "@/modules/attendance/types";
import { usePunchVerification } from "@/modules/attendance/use-punch-verification";
import { playAttendancePunchSuccessSound } from "@/modules/scanner/scan-sound";
import { useCameraScan } from "@/modules/scanner/use-camera-scan";
import type { Store } from "@/modules/shop/types";
import { useStores } from "@/modules/shop/use-stores";
import {
  getPosEnabledStores,
  resolveScopedStoreCode,
} from "@/modules/shop/store-scope";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { useAuthStore } from "@/store/auth-store";

type AttendanceMainTab = "personal" | "management";
type PersonalAttendanceTab = "punchRecords" | "availabilityWeek" | "requests";
type AttendanceManagementTab = "schedule" | "holidays" | "leave" | "punches";
export type AttendanceScreenMode = "personal" | "management" | "combined";

interface AttendanceScreenProps {
  mode?: AttendanceScreenMode;
}

function toDateString(date: Date) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

function parseDate(value: Date | string) {
  const date =
    value instanceof Date ? new Date(value) : new Date(`${value}T00:00:00`);
  return Number.isNaN(date.getTime()) ? new Date() : date;
}

function getWeekStartDate(value: Date | string = new Date()) {
  const date = parseDate(value);
  const diff = (date.getDay() + 6) % 7;
  date.setDate(date.getDate() - diff);
  return toDateString(date);
}

function addWeeks(weekStartDate: string, weeks: number) {
  const date = new Date(`${weekStartDate}T00:00:00`);
  if (Number.isNaN(date.getTime())) {
    return getWeekStartDate();
  }
  date.setDate(date.getDate() + weeks * 7);
  return toDateString(date);
}

function isPrimaryStore(store: object) {
  return "isPrimary" in store && store.isPrimary === true;
}

function findStoreByCode(stores: Store[], storeCode?: string | null) {
  return storeCode
    ? stores.find((store) => store.storeCode === storeCode)
    : undefined;
}

const attendanceKeys = {
  today: (storeCode?: string, workDate?: string) =>
    [
      "attendance",
      "my",
      "today",
      storeCode ?? "all",
      workDate ?? "today",
    ] as const,
  week: (storeCode?: string, weekStartDate?: string) =>
    [
      "attendance",
      "my",
      "week",
      storeCode ?? "all",
      weekStartDate ?? "current",
    ] as const,
  availability: (storeCode?: string, weekStartDate?: string) =>
    [
      "attendance",
      "my",
      "availability",
      storeCode ?? "all",
      weekStartDate ?? "current",
    ] as const,
  approvals: (storeCode?: string) =>
    ["attendance", "approvals", "pending", storeCode ?? "all"] as const,
  holidays: (storeCode?: string) =>
    ["attendance", "holidays", storeCode ?? "all"] as const,
  schedulesWeek: (storeCode?: string, weekStartDate?: string) =>
    [
      "attendance",
      "schedules",
      "week",
      storeCode ?? "all",
      weekStartDate ?? "current",
    ] as const,
};

export function AttendanceScreen({ mode = "combined" }: AttendanceScreenProps) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const { t, language } = useAppTranslation(["attendance", "common"]);
  const user = useAuthStore((state) => state.user);
  const access = useAuthStore((state) => state.access);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const {
    stores,
    selectedStoreCode: rememberedStoreCode,
    isDeviceMode,
    deviceBoundStore,
    isHydratingSelection,
    selectStore,
  } = useStores();
  // 仅限制门店选择；扫码仍按原已分配分店与后端授权校验。
  const posEnabledStores = useMemo(() => getPosEnabledStores(stores), [stores]);
  const [activeMainTab, setActiveMainTab] = useState<AttendanceMainTab>(
    mode === "management" ? "management" : "personal",
  );
  const [activePersonalTab, setActivePersonalTab] =
    useState<PersonalAttendanceTab>("punchRecords");
  const [activeManagementTab, setActiveManagementTab] =
    useState<AttendanceManagementTab>(() =>
      access.canViewAttendancePunchRecords ? "punches" : "schedule");
  // 审核里「补录下班」跳到打卡记录时带上的日期；变化时重建打卡记录卡片。
  const [managedPunchDate, setManagedPunchDate] = useState<string>();
  const [leaveRegisterVisible, setLeaveRegisterVisible] = useState(false);
  const [availabilitySheet, setAvailabilitySheet] = useState<{
    dates: string[];
    item?: AttendanceAvailability;
  } | null>(null);
  const [selectedDate, setSelectedDate] = useState(() =>
    toDateString(new Date()),
  );
  const [selectedStoreCode, setSelectedStoreCode] = useState<
    string | undefined
  >(undefined);
  const [storePickerVisible, setStorePickerVisible] = useState(false);
  const [managerWeekStartDate, setManagerWeekStartDate] =
    useState(getWeekStartDate);
  const [snackbarMessage, setSnackbarMessage] = useState("");
  const [snackbarVisible, setSnackbarVisible] = useState(false);
  const [attendanceScannerVisible, setAttendanceScannerVisible] = useState(false);
  const [attendanceScannerError, setAttendanceScannerError] = useState("");
  const [attendanceScannerPaused, setAttendanceScannerPaused] = useState(false);
  const [attendanceScannerResetNonce, setAttendanceScannerResetNonce] = useState(0);
  const [attendanceScannerSubmitting, setAttendanceScannerSubmitting] = useState(false);
  const [punchStage, setPunchStage] = useState<"validating" | "locating" | "saving" | "tracking">();
  const [lastQrPunch, setLastQrPunch] = useState<AttendancePunch>();
  const [lastQrTrackingWarning, setLastQrTrackingWarning] = useState("");
  const attendanceScannerSessionGateRef = useRef<
    ReturnType<typeof createAttendanceQrScanSessionGate> | null
  >(null);
  const attendanceScannerSessionRef = useRef<AttendanceQrScanSession | null>(null);
  if (!attendanceScannerSessionGateRef.current) {
    attendanceScannerSessionGateRef.current = createAttendanceQrScanSessionGate();
  }
  const attendanceScannerSessionGate = attendanceScannerSessionGateRef.current;

  useEffect(() => () => {
    attendanceScannerSessionGate.invalidate();
    attendanceScannerSessionRef.current = null;
  }, [attendanceScannerSessionGate]);
  const getErrorMessage = useCallback((error: unknown, fallbackKey: string) => (
    resolveLocalizedErrorMessage(error, {
      language,
      t,
      fallbackKey,
    })
  ), [language, t]);
  const {
    verification,
    isRefreshing: isRefreshingVerification,
    refreshVerification,
    prewarmLocation,
    stopLocationCapture,
  } = usePunchVerification();

  const managerStores = useMemo(
    () => (access.isAdmin ? posEnabledStores : posEnabledStores.filter(isPrimaryStore)),
    [access.isAdmin, posEnabledStores],
  );
  const canViewAttendanceManagement = access.canViewAttendanceManagement;
  const canReviewAttendance = access.canReviewAttendance;
  const canEditAttendanceHoliday = access.canEditAttendanceHoliday;
  const canSwitchMainTabs = mode === "combined" && canViewAttendanceManagement;
  const isManagementMode =
    mode === "management" ||
    (mode === "combined" && activeMainTab === "management");
  const isPersonalTab =
    mode === "personal" ||
    (mode === "combined" && activeMainTab === "personal");
  const isManagementTab =
    canViewAttendanceManagement &&
    (mode === "management" ||
      (mode === "combined" && activeMainTab === "management"));
  const isManagementUnavailable =
    mode === "management" && !canViewAttendanceManagement;
  const isPunchRecordsTab =
    isPersonalTab && activePersonalTab === "punchRecords";
  const isAvailabilityWeekTab =
    isPersonalTab && activePersonalTab === "availabilityWeek";
  const isScheduleManagementTab =
    isManagementTab && activeManagementTab === "schedule";
  const isHolidayManagementTab =
    isManagementTab && activeManagementTab === "holidays";
  const isLeaveManagementTab =
    isManagementTab && activeManagementTab === "leave";
  const isRequestsTab = isPersonalTab && activePersonalTab === "requests";
  const canViewManagedPunches = access.canViewAttendancePunchRecords;
  const isPunchManagementTab =
    isManagementTab && canViewManagedPunches && activeManagementTab === "punches";
  const sectionStores = isManagementMode ? managerStores : posEnabledStores;
  const employeeWeekStartDate = useMemo(
    () => getWeekStartDate(selectedDate),
    [selectedDate],
  );
  const todayDate = useMemo(() => toDateString(new Date()), []);

  useEffect(() => {
    setSelectedStoreCode((current) => {
      // 设备绑定门店可能没有 POS 启用字段；它仍是查询作用域，但不能混入 Picker 候选。
      return resolveScopedStoreCode({
        currentStoreCode: current,
        persistedStoreCode: rememberedStoreCode,
        deviceBoundStoreCode: deviceBoundStore?.storeCode,
        isDeviceMode,
        stores: sectionStores,
      }) ?? sectionStores[0]?.storeCode;
    });
  }, [deviceBoundStore?.storeCode, isDeviceMode, rememberedStoreCode, sectionStores]);

  useEffect(() => {
    if (mode !== "combined") {
      return;
    }

    if (!canViewAttendanceManagement && activeMainTab !== "personal") {
      setActiveMainTab("personal");
    }
  }, [activeMainTab, canViewAttendanceManagement, mode]);

  const showMessage = useCallback((message: string) => {
    setSnackbarMessage(message);
    setSnackbarVisible(true);
  }, []);

  useEffect(() => {
    if (isAuthenticated && user) {
      return;
    }
    showMessage(t("messages.loginRequired"));
    router.navigate("/(shell)/settings");
  }, [isAuthenticated, router, showMessage, t, user]);

  const todayQuery = useQuery({
    queryKey: attendanceKeys.today(selectedStoreCode, selectedDate),
    queryFn: () => getMyAttendanceToday(selectedStoreCode, selectedDate),
    // 扫码期间统一由 fetchQuery 获取二维码门店状态，切店不能再发起重复的今日请求。
    enabled: Boolean(isAuthenticated && user && isPunchRecordsTab
      && (!attendanceScannerSubmitting || punchStage === "tracking")),
  });
  const weekQuery = useQuery({
    queryKey: attendanceKeys.week(selectedStoreCode, employeeWeekStartDate),
    queryFn: () =>
      getMyAttendanceWeek(selectedStoreCode, employeeWeekStartDate),
    enabled: Boolean(isAuthenticated && user && isAvailabilityWeekTab),
  });
  const availabilityQuery = useQuery({
    queryKey: attendanceKeys.availability(
      selectedStoreCode,
      employeeWeekStartDate,
    ),
    queryFn: () => getMyAvailability(selectedStoreCode, employeeWeekStartDate),
    enabled: Boolean(isAuthenticated && user && isAvailabilityWeekTab),
  });
  const approvalsQuery = useQuery({
    queryKey: attendanceKeys.approvals(selectedStoreCode),
    queryFn: () => getPendingApprovals(selectedStoreCode),
    // 页签上要显示待审数量，所以管理页任一页签都拉取。
    enabled: Boolean(isAuthenticated && user && isManagementTab),
  });
  // 排班与登记请假只需要本店员工身份，走考勤自己的接口，避免要求用户管理权限 Users.View。
  const storeUsersQuery = useQuery({
    queryKey: ["attendance", "employees", selectedStoreCode ?? ""],
    queryFn: () => getAttendanceEmployees(selectedStoreCode!),
    enabled: Boolean(
      isAuthenticated && user && selectedStoreCode
        && (isScheduleManagementTab || (isLeaveManagementTab && leaveRegisterVisible)),
    ),
  });
  const managerSchedulesQuery = useQuery({
    queryKey: attendanceKeys.schedulesWeek(
      selectedStoreCode,
      managerWeekStartDate,
    ),
    queryFn: () =>
      getAttendanceSchedulesWeek({
        storeCode: selectedStoreCode,
        weekStartDate: managerWeekStartDate,
      }),
    enabled: Boolean(
      isAuthenticated && user && isScheduleManagementTab && selectedStoreCode,
    ),
  });
  const holidaysQuery = useQuery({
    queryKey: attendanceKeys.holidays(selectedStoreCode),
    queryFn: () => getAttendanceHolidays({ storeCode: selectedStoreCode }),
    enabled: Boolean(
      isAuthenticated && user && isHolidayManagementTab && selectedStoreCode,
    ),
  });

  const invalidateEmployeeData = useCallback(async () => {
    await Promise.all([
      queryClient.invalidateQueries({
        queryKey: ["attendance", "my", "today"],
      }),
      queryClient.invalidateQueries({ queryKey: ["attendance", "my", "week"] }),
      queryClient.invalidateQueries({
        queryKey: ["attendance", "my", "availability"],
      }),
    ]);
  }, [queryClient]);

  const invalidateScheduleManagementData = useCallback(async () => {
    await Promise.all([
      queryClient.invalidateQueries({
        queryKey: ["attendance", "schedules", "week"],
      }),
      invalidateEmployeeData(),
    ]);
  }, [invalidateEmployeeData, queryClient]);

  const invalidateHolidayManagementData = useCallback(async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["attendance", "holidays"] }),
      invalidateScheduleManagementData(),
    ]);
  }, [invalidateScheduleManagementData, queryClient]);

  const punchMutation = useMutation({
    mutationFn: punchAttendance,
  });

  const previewPunchAdjustmentMutation = useMutation({
    mutationFn: previewMyAttendancePunchAdjustment,
  });

  const createPunchAdjustmentMutation = useMutation({
    mutationFn: createMyAttendancePunchAdjustment,
    onSuccess: async (adjustment) => {
      await invalidateEmployeeData();
      showMessage(
        adjustment.status.toLowerCase() === "applied"
          ? t("messages.adjustmentApplied")
          : t("messages.adjustmentSubmitted"),
      );
    },
  });

  const createAvailabilityMutation = useMutation({
    mutationFn: createAvailabilityBatch,
    retry: false,
    onSuccess: () => {
      showMessage(t("messages.availabilitySaved"));
    },
    onError: (error) =>
      showMessage(
        getErrorMessage(error, "messages.saveFailed"),
      ),
    onSettled: () => { void invalidateEmployeeData(); },
  });

  const updateAvailabilityMutation = useMutation({
    mutationFn: ({
      availabilityGuid,
      payload,
    }: {
      availabilityGuid: string;
      payload: AttendanceAvailabilityPayload;
    }) => updateAvailability(availabilityGuid, payload),
    retry: false,
    onSuccess: () => {
      showMessage(t("messages.availabilitySaved"));
    },
    onError: (error) =>
      showMessage(
        getErrorMessage(error, "messages.saveFailed"),
      ),
    onSettled: () => { void invalidateEmployeeData(); },
  });

  const cancelAvailabilityMutation = useMutation({
    mutationFn: cancelAvailability,
    onSuccess: async () => {
      await invalidateEmployeeData();
      showMessage(t("messages.availabilityCancelled"));
    },
    onError: (error) =>
      showMessage(
        getErrorMessage(error, "messages.cancelFailed"),
      ),
  });

  const approveMutation = useMutation({
    mutationFn: approveAttendanceApproval,
    onSuccess: async () => {
      await queryClient.invalidateQueries({
        queryKey: ["attendance", "approvals", "pending"],
      });
      await invalidateEmployeeData();
      showMessage(t("messages.approvalApproved"));
    },
    onError: (error) =>
      showMessage(
        getErrorMessage(error, "messages.approvalFailed"),
      ),
  });

  const rejectMutation = useMutation({
    mutationFn: rejectAttendanceApproval,
    onSuccess: async () => {
      await queryClient.invalidateQueries({
        queryKey: ["attendance", "approvals", "pending"],
      });
      await invalidateEmployeeData();
      showMessage(t("messages.approvalRejected"));
    },
    onError: (error) =>
      showMessage(
        getErrorMessage(error, "messages.approvalFailed"),
      ),
  });

  const createScheduleMutation = useMutation({
    mutationFn: createAttendanceSchedule,
    onSuccess: async () => {
      await invalidateScheduleManagementData();
      showMessage(t("messages.scheduleSaved"));
    },
    onError: (error) =>
      showMessage(
        getErrorMessage(error, "messages.saveFailed"),
      ),
  });

  const updateScheduleMutation = useMutation({
    mutationFn: ({
      scheduleGuid,
      payload,
    }: {
      scheduleGuid: string;
      payload: AttendanceScheduleUpdatePayload;
    }) => updateAttendanceSchedule(scheduleGuid, payload),
    onSuccess: async () => {
      await invalidateScheduleManagementData();
      showMessage(t("messages.scheduleSaved"));
    },
    onError: (error) =>
      showMessage(
        getErrorMessage(error, "messages.saveFailed"),
      ),
  });

  const deleteScheduleMutation = useMutation({
    mutationFn: deleteAttendanceSchedule,
    onSuccess: async () => {
      await invalidateScheduleManagementData();
      showMessage(t("messages.scheduleDeleted"));
    },
    onError: (error) =>
      showMessage(
        getErrorMessage(error, "messages.cancelFailed"),
      ),
  });

  const publishWeekMutation = useMutation({
    mutationFn: publishAttendanceSchedulesWeek,
    onSuccess: async () => {
      await invalidateScheduleManagementData();
      showMessage(t("messages.weekPublished"));
    },
    onError: (error) =>
      showMessage(
        getErrorMessage(error, "messages.publishFailed"),
      ),
  });

  const createHolidayMutation = useMutation({
    mutationFn: createAttendanceHoliday,
    onSuccess: async () => {
      await invalidateHolidayManagementData();
      showMessage(t("messages.holidaySaved"));
    },
    onError: (error) =>
      showMessage(
        getErrorMessage(error, "messages.saveFailed"),
      ),
  });

  const updateHolidayMutation = useMutation({
    mutationFn: ({
      holidayGuid,
      payload,
    }: {
      holidayGuid: string;
      payload: AttendanceStoreHolidayPayload;
    }) => updateAttendanceHoliday(holidayGuid, payload),
    onSuccess: async () => {
      await invalidateHolidayManagementData();
      showMessage(t("messages.holidaySaved"));
    },
    onError: (error) =>
      showMessage(
        getErrorMessage(error, "messages.saveFailed"),
      ),
  });

  const deleteHolidayMutation = useMutation({
    mutationFn: deleteAttendanceHoliday,
    onSuccess: async () => {
      await invalidateHolidayManagementData();
      showMessage(t("messages.holidayDeleted"));
    },
    onError: (error) =>
      showMessage(
        getErrorMessage(error, "messages.cancelFailed"),
      ),
  });
  const syncHolidayMutation = useMutation({
    mutationFn: syncAttendanceHolidays,
    onSuccess: async (result) => {
      await invalidateHolidayManagementData();
      showMessage(
        t("messages.holidaySyncSuccess", {
          count: result.syncedCount,
          defaultValue: `Synced ${result.syncedCount} public holidays.`,
        }),
      );
    },
    onError: (error) =>
      showMessage(
        getErrorMessage(error, "messages.saveFailed"),
      ),
  });

  const createManagedLeaveMutation = useMutation({
    mutationFn: createManagedLeaveRequest,
    onSuccess: async () => {
      await queryClient.invalidateQueries({
        queryKey: ["attendance", "approvals", "pending"],
      });
      showMessage(t("messages.leaveSubmitted"));
    },
    onError: (error) =>
      showMessage(
        getErrorMessage(error, "messages.saveFailed"),
      ),
  });

  const isPersonalRefreshing = isPunchRecordsTab
    ? todayQuery.isRefetching
    : weekQuery.isRefetching || availabilityQuery.isRefetching;
  const isManagementRefreshing =
    (isScheduleManagementTab &&
      (managerSchedulesQuery.isRefetching || storeUsersQuery.isRefetching)) ||
    (isHolidayManagementTab && holidaysQuery.isRefetching) ||
    (isLeaveManagementTab &&
      (approvalsQuery.isRefetching || storeUsersQuery.isRefetching));
  const isRefreshing = isPersonalTab
    ? isPersonalRefreshing
    : isManagementRefreshing;

  const isAvailabilityBusy =
    createAvailabilityMutation.isPending ||
    updateAvailabilityMutation.isPending ||
    cancelAvailabilityMutation.isPending;
  const isAdjustmentBusy =
    previewPunchAdjustmentMutation.isPending ||
    createPunchAdjustmentMutation.isPending;
  const isApprovalBusy = approveMutation.isPending || rejectMutation.isPending;
  const isScheduleBusy =
    createScheduleMutation.isPending ||
    updateScheduleMutation.isPending ||
    deleteScheduleMutation.isPending ||
    publishWeekMutation.isPending;
  const isHolidayBusy =
    createHolidayMutation.isPending ||
    updateHolidayMutation.isPending ||
    deleteHolidayMutation.isPending ||
    syncHolidayMutation.isPending;
  const isLeaveBusy = createManagedLeaveMutation.isPending;

  const employeeInitialLoading =
    (isPunchRecordsTab && todayQuery.isLoading) ||
    (isAvailabilityWeekTab &&
      (weekQuery.isLoading || availabilityQuery.isLoading));
  const employeeLoadError = isPunchRecordsTab
    ? todayQuery.error
    : isAvailabilityWeekTab
      ? weekQuery.error || availabilityQuery.error
      : null;
  const isManagementContentLoading =
    (isHolidayManagementTab && holidaysQuery.isLoading) ||
    (isLeaveManagementTab && approvalsQuery.isLoading);
  const managerLoadError =
    (isScheduleManagementTab &&
      (storeUsersQuery.error || managerSchedulesQuery.error)) ||
    (isHolidayManagementTab && holidaysQuery.error) ||
    // 员工列表只服务「登记请假」，它的错误在登记区域内提示，不挡住审核列表。
    (isLeaveManagementTab && approvalsQuery.error);

  const pendingApprovals = useMemo(
    () => approvalsQuery.data ?? [],
    [approvalsQuery.data],
  );

  const selectedStore = useMemo(
    () => findStoreByCode(stores, selectedStoreCode),
    [selectedStoreCode, stores],
  );
  const selectedStoreName = useMemo(
    () => selectedStore?.storeName,
    [selectedStore],
  );
  const selectedHolidayJurisdiction = useMemo(
    () =>
      normalizeAustralianHolidayJurisdiction(selectedStore?.stateCode) ??
      resolveAustralianHolidayJurisdiction(selectedStore?.postcode) ??
      null,
    [selectedStore?.postcode, selectedStore?.stateCode],
  );
  const canSyncHolidayManagement = Boolean(
    canEditAttendanceHoliday && selectedStoreCode,
  );
  const holidaySyncDisabledReason = useMemo(() => {
    if (!selectedStoreCode) {
      return t("holidayManagement.noStore");
    }

    return undefined;
  }, [selectedStoreCode, t]);
  const screenTitle =
    mode === "personal"
      ? t("tabs.personalAttendance")
      : mode === "management"
        ? t("tabs.attendanceManagement")
        : t("title");

  const handleSelectStore = useCallback(
    async (store: Store | null) => {
      if (!store) {
        return;
      }

      setSelectedStoreCode(store.storeCode);
      setStorePickerVisible(false);

      try {
        await selectStore(store);
      } catch (error) {
        console.warn("[attendance] failed to persist store selection", error);
      }
    },
    [selectStore],
  );

  const handleBack = useCallback(() => {
    if (router.canGoBack()) {
      router.back();
      return;
    }

    router.navigate("/(shell)/settings");
  }, [router]);

  const refresh = useCallback(async () => {
    try {
      await Promise.all([
        isPunchRecordsTab ? todayQuery.refetch() : Promise.resolve(),
        isAvailabilityWeekTab ? weekQuery.refetch() : Promise.resolve(),
        isAvailabilityWeekTab ? availabilityQuery.refetch() : Promise.resolve(),
        isLeaveManagementTab ? approvalsQuery.refetch() : Promise.resolve(),
        (isScheduleManagementTab || isLeaveManagementTab) && selectedStoreCode
          ? storeUsersQuery.refetch()
          : Promise.resolve(),
        isScheduleManagementTab && selectedStoreCode
          ? managerSchedulesQuery.refetch()
          : Promise.resolve(),
        isHolidayManagementTab && selectedStoreCode
          ? holidaysQuery.refetch()
          : Promise.resolve(),
      ]);
    } catch (error) {
      showMessage(
        getErrorMessage(error, "messages.refreshFailed"),
      );
    }
  }, [
    approvalsQuery,
    availabilityQuery,
    holidaysQuery,
    isAvailabilityWeekTab,
    isHolidayManagementTab,
    isLeaveManagementTab,
    isPunchRecordsTab,
    isScheduleManagementTab,
    managerSchedulesQuery,
    selectedStoreCode,
    showMessage,
    storeUsersQuery,
    t,
    todayQuery,
    weekQuery,
  ]);

  const withSelectedStore = <T extends { storeCode?: string }>(
    payload: T,
  ): T => ({
    ...payload,
    storeCode: payload.storeCode || selectedStoreCode,
  });

  const confirmBackgroundLocationUsage = useCallback(
    () =>
      new Promise<boolean>((resolve) => {
        Alert.alert(
          t("backgroundLocation.title"),
          t("backgroundLocation.description"),
          [
            {
              text: t("common:actions.cancel"),
              style: "cancel",
              onPress: () => resolve(false),
            },
            {
              text: t("backgroundLocation.confirm"),
              onPress: () => resolve(true),
            },
          ],
          {
            cancelable: true,
            onDismiss: () => resolve(false),
          },
        );
      }),
    [t],
  );

  const openLocationSettings = useCallback(() => {
    // 只有用户明确选择前往设置时才离开 App，避免权限拒绝后自动跳转。
    void Linking.openSettings().catch(() => {
      showMessage(t("locationPermission.openSettingsFailed"));
    });
  }, [showMessage, t]);

  const showLocationPermissionSettingsPrompt = useCallback(() => {
    Alert.alert(
      t("locationPermission.title"),
      t("locationPermission.description"),
      [
        {
          text: t("common:actions.cancel"),
          style: "cancel",
        },
        {
          text: t("common:actions.goToSettings"),
          onPress: openLocationSettings,
        },
      ],
    );
  }, [openLocationSettings, t]);

  const ensureQrPunchBackgroundLocationPermission = async (
    isActive = () => true,
  ) => {
    const alreadyAllowed = await hasAttendanceBackgroundLocationPermission();
    if (!isActive()) return false;
    if (alreadyAllowed) {
      return true;
    }
    const confirmed = await confirmBackgroundLocationUsage();
    if (!isActive() || !confirmed) {
      return false;
    }
    const allowed = await ensureAttendanceBackgroundLocationPermission();
    if (!isActive()) {
      return false;
    }
    if (!allowed) {
      // 系统权限被拒绝后提供明确的设置入口，避免用户停留在不可恢复状态。
      showLocationPermissionSettingsPrompt();
      return false;
    }
    prewarmLocation();
    return true;
  };

  const pauseAttendanceScanner = (message: string) => {
    setAttendanceScannerError(message);
    setAttendanceScannerPaused(true);
  };

  const failAttendanceQrScan = (
    session: AttendanceQrScanSession,
    message: string,
  ) => {
    if (!attendanceScannerSessionGate.isActive(session)) return;
    pauseAttendanceScanner(message);
    attendanceScannerSessionGate.finishSubmitting(session);
    setAttendanceScannerSubmitting(false);
  };

  const hideAttendanceScannerForProcessing = () => {
    // 格式通过后只卸载相机，当前 session 继续保护后续异步链路。
    setAttendanceScannerVisible(false);
  };

  const failAttendanceQrProcessing = (
    session: AttendanceQrScanSession,
    message: string,
  ) => {
    if (!attendanceScannerSessionGate.isActive(session)) return;
    showMessage(message);
    attendanceScannerSessionGate.finishSubmitting(session);
    attendanceScannerSessionGate.invalidate();
    attendanceScannerSessionRef.current = null;
    setAttendanceScannerSubmitting(false);
    setPunchStage(undefined);
    stopLocationCapture();
  };

  const handleAttendanceQrScan = async (qrToken: string) => {
    const session = attendanceScannerSessionRef.current;
    const normalizedQrToken = normalizeAttendanceQrTokenInput(qrToken);
    if (!session
        || !attendanceScannerSessionGate.isActive(session)
        || !attendanceScannerSessionGate.tryStartSubmitting(session, normalizedQrToken)) {
      return;
    }
    setAttendanceScannerError("");
    setAttendanceScannerSubmitting(true);
    setPunchStage("validating");
    const timing = createAttendanceQrPerformance();
    try {
      validateAttendanceQrToken(normalizedQrToken);
    } catch (error) {
      const code = error instanceof Error ? error.message : undefined;
      failAttendanceQrScan(
        session,
        t(getAttendancePunchErrorKey(code) ?? "messages.qrFormatInvalid"),
      );
      return;
    }

    hideAttendanceScannerForProcessing();

    let resolvedQr;
    try {
      // 关键逻辑：客户端不解码身份，只信任后端解密并校验后的门店和设备。
      // 首个真实请求立即校验二维码，不能让 health 探测消耗 15 秒有效窗口。
      resolvedQr = await timing.measure("resolve", () => resolveAttendanceQr(normalizedQrToken));
    } catch (error) {
      if (!attendanceScannerSessionGate.isActive(session)) return;
      const code = getAttendancePunchErrorCode(error);
      if (code === "ATTENDANCE_QR_EXPIRED"
          && attendanceScannerSessionGate.resumeAfterExpired(session, normalizedQrToken)) {
        // 仅明确未提交的过期校验可自动恢复；同一旧码在相机 gate 之前过滤。
        resetAttendanceScannerUi();
        setAttendanceScannerError(t("scanner.waitingForFreshCode"));
        setAttendanceScannerVisible(true);
        return;
      }
      // 仅记录阶段与业务错误码，严禁输出二维码 token、请求体或完整响应。
      console.warn("[attendance] qr request failed", { stage: "resolve", code });
      const errorKey = getAttendancePunchErrorKey(code);
      failAttendanceQrProcessing(
        session,
        errorKey ? t(errorKey) : getErrorMessage(error, "messages.punchFailed"),
      );
      return;
    }
    if (!attendanceScannerSessionGate.isActive(session)) return;

    let qrStore: Store;
    try {
      qrStore = resolveAttendanceQrStore(resolvedQr, stores);
    } catch (error) {
      const code = error instanceof Error ? error.message : undefined;
      failAttendanceQrProcessing(
        session,
        t(getAttendancePunchErrorKey(code) ?? "messages.qrStoreForbidden"),
      );
      return;
    }
    let qrToday;
    try {
      // 必须读取二维码门店的实时状态，不能复用切店前闭包里的 Today 数据。
      qrToday = await timing.measure("today", async () => {
        const queryKey = attendanceKeys.today(qrStore.storeCode, todayDate);
        await queryClient.cancelQueries({ queryKey, exact: true });
        const request = queryClient.fetchQuery({
          queryKey,
          queryFn: () => getMyAttendanceToday(qrStore.storeCode, todayDate),
          staleTime: 0,
          // 保持原直接请求的失败行为，避免继承全局重试而额外拖长扫码等待。
          retry: false,
        });
        const [, today] = await Promise.all([handleSelectStore(qrStore), request]);
        return today;
      });
    } catch (error) {
      if (attendanceScannerSessionGate.isActive(session)) {
        failAttendanceQrProcessing(
          session,
          getErrorMessage(error, "messages.qrNetworkRequired"),
        );
      }
      return;
    }
    if (!attendanceScannerSessionGate.isActive(session)) return;
    setPunchStage("locating");
    let latestVerification: AttendancePunchVerificationState | undefined;
    const preparation = await prepareAttendanceQrPunch(qrToday.nextPunchType, {
      isActive: () => attendanceScannerSessionGate.isActive(session),
      ensureBackgroundPermission: () => ensureQrPunchBackgroundLocationPermission(
        () => attendanceScannerSessionGate.isActive(session),
      ),
      refreshVerification: async () => {
        // resolve 和 today 的成功响应已证明联网，直接复用本次会话的新鲜定位。
        latestVerification = await timing.measure("location", () => refreshVerification({ networkVerified: true }));
        return latestVerification;
      },
    });
    if (preparation.status === "stale") return;
    if (preparation.status !== "ready") {
      if (preparation.status === "gpsRequired"
          && latestVerification?.location.status === "permissionDenied") {
        // 前台定位被拒绝时，同样引导用户前往系统设置恢复授权。
        showLocationPermissionSettingsPrompt();
      }
      const messageKey = preparation.status === "backgroundRequired"
        ? "messages.backgroundLocationRequiredForClockIn"
        : preparation.status === "gpsRequired"
          ? latestVerification?.location.reason === "timeout"
            ? "messages.qrGpsTimeout"
            : "messages.qrGpsRequired"
          : "messages.qrNetworkRequired";
      failAttendanceQrProcessing(session, t(messageKey));
      return;
    }

    let result: AttendancePunchMutationResult;
    try {
      stopLocationCapture();
      setPunchStage("saving");
      result = await timing.measure("punch", () => punchMutation.mutateAsync(
        buildAttendanceQrPunchPayload(
          normalizedQrToken,
          resolvedQr.punchAuthorizationToken,
          preparation.verification.payload,
        ),
      ));
    } catch (error) {
      if (attendanceScannerSessionGate.isActive(session)) {
        const errorKey = getAttendancePunchErrorKey(getAttendancePunchErrorCode(error));
        failAttendanceQrProcessing(
          session,
          errorKey ? t(errorKey) : getErrorMessage(error, "messages.punchFailed"),
        );
      }
      return;
    }

    let trackingWarning = "";
    try {
      await completeAttendancePostSave({
        notifySaved: () => {
          playAttendancePunchSuccessSound();
          timing.saved();
          if (!attendanceScannerSessionGate.isActive(session)) return;
          setLastQrPunch(result);
          setLastQrTrackingWarning("");
          setPunchStage("tracking");
          showMessage(t("messages.qrPunchSuccess", {
            punchType: t(`punchTypes.${result.punchType}`, result.punchType),
          }));
        },
        refresh: () => timing.measure("refresh", invalidateEmployeeData),
        onRefreshError: () => console.warn("[attendance] saved punch refresh failed"),
        // 服务端成功后必须完成定位生命周期，不受 UI 会话失效影响。
        track: () => timing.measure("tracking", () => applyAttendanceTrackingLifecycle(result, qrStore.storeCode, {
          start: async (storeCode) => {
            await startAttendanceLocationTracking({
              storeCode,
              startedAtUtc: result.serverTimeUtc,
              workDate: result.workDate,
              storeTimeZone: result.storeTimeZone,
              ...(await getAttendanceDeviceContext()),
            });
          },
          stop: stopAttendanceLocationTracking,
        })),
      });
    } catch (error) {
      trackingWarning = getErrorMessage(
        error,
        result.punchType === "ClockOut"
          ? "messages.locationTrackingFailed"
          : "messages.qrTrackingWarning",
      );
    }
    if (!attendanceScannerSessionGate.isActive(session)) return;

    closeAttendanceScanner(true);
    setLastQrTrackingWarning(trackingWarning);
    if (trackingWarning) showMessage(trackingWarning);
  };

  const attendanceCameraScan = useCameraScan({
    disabled: !shouldEnableAttendanceQrScanning({
      isVisible: attendanceScannerVisible,
      isSubmitting: attendanceScannerSubmitting,
      isPaused: attendanceScannerPaused,
    }),
    ignoreWhileProcessing: true,
    singleScanUntilReset: true,
    resetKey: `${attendanceScannerVisible}:${attendanceScannerResetNonce}`,
    onBarcode: handleAttendanceQrScan,
  });

  const resetAttendanceScannerUi = () => {
    setAttendanceScannerError("");
    setAttendanceScannerPaused(false);
    setAttendanceScannerSubmitting(false);
    setPunchStage(undefined);
    setAttendanceScannerResetNonce((value) => value + 1);
  };

  const beginAttendanceScannerSession = () => {
    attendanceScannerSessionRef.current = attendanceScannerSessionGate.begin();
    resetAttendanceScannerUi();
    prewarmLocation();
  };

  const closeAttendanceScanner = (force = false) => {
    const session = attendanceScannerSessionRef.current;
    if (!force && session && attendanceScannerSessionGate.isSubmitting(session)) return;
    attendanceScannerSessionGate.invalidate();
    attendanceScannerSessionRef.current = null;
    setAttendanceScannerVisible(false);
    resetAttendanceScannerUi();
    stopLocationCapture();
  };

  const requestAttendanceCameraPermission = async () => {
    const session = attendanceScannerSessionRef.current;
    if (!session) return false;
    const permission = await attendanceCameraScan.requestPermission();
    if (!attendanceScannerSessionGate.isActive(session)) return false;
    if (permission.granted) {
      resetAttendanceScannerUi();
    } else {
      pauseAttendanceScanner(t("messages.qrCameraPermissionRequired"));
    }
    return permission.granted;
  };

  const retryAttendanceScan = async () => {
    const session = attendanceScannerSessionRef.current;
    if (session && attendanceScannerSessionGate.isSubmitting(session)) return;
    beginAttendanceScannerSession();
    if (!attendanceCameraScan.permission?.granted) {
      await requestAttendanceCameraPermission();
    }
  };

  const openAttendanceScanner = async () => {
    beginAttendanceScannerSession();
    setAttendanceScannerVisible(true);
    if (!attendanceCameraScan.permission?.granted) {
      await requestAttendanceCameraPermission();
    }
  };

  const handleCreateSchedule = (payload: AttendanceSchedulePayload) => {
    createScheduleMutation.mutate(payload);
  };

  const handlePublishWeek = () => {
    if (!selectedStoreCode) {
      showMessage(t("messages.selectStoreFirst"));
      return;
    }

    publishWeekMutation.mutate({
      storeCode: selectedStoreCode,
      weekStartDate: managerWeekStartDate,
    });
  };

  const handleCreateHoliday = (payload: AttendanceStoreHolidayPayload) => {
    const payloadWithStore = withSelectedStore(payload);
    if (!payloadWithStore.storeCode) {
      showMessage(t("messages.selectStoreFirst"));
      return;
    }

    createHolidayMutation.mutate(payloadWithStore);
  };

  const handleUpdateHoliday = (
    holidayGuid: string,
    payload: AttendanceStoreHolidayPayload,
  ) => {
    const payloadWithStore = withSelectedStore(payload);
    if (!payloadWithStore.storeCode) {
      showMessage(t("messages.selectStoreFirst"));
      return;
    }

    updateHolidayMutation.mutate({ holidayGuid, payload: payloadWithStore });
  };

  const handleSyncHolidays = () => {
    if (!selectedStoreCode) {
      showMessage(t("messages.selectStoreFirst"));
      return;
    }

    syncHolidayMutation.mutate({
      storeCode: selectedStoreCode,
      postcode: selectedStore?.postcode,
      jurisdiction: selectedHolidayJurisdiction ?? undefined,
      stateCode: selectedHolidayJurisdiction ?? undefined,
      daysAhead: PUBLIC_HOLIDAY_SYNC_DAYS_AHEAD,
    });
  };

  if (
    !isAuthenticated ||
    !user ||
    isHydratingSelection ||
    (employeeInitialLoading && !isAvailabilityWeekTab)
  ) {
    return (
      <SafeAreaView style={styles.container} edges={["top", "left", "right"]}>
        <View style={styles.centered}>
          <ActivityIndicator size="large" />
        </View>
        <Snackbar
          visible={snackbarVisible}
          onDismiss={() => setSnackbarVisible(false)}
        >
          {snackbarMessage}
        </Snackbar>
      </SafeAreaView>
    );
  }

  if (isPersonalTab && employeeLoadError && !isAvailabilityWeekTab) {
    return (
      <SafeAreaView style={styles.container} edges={["top", "left", "right"]}>
        <View style={styles.centered}>
          <EmptyState
            title={t("messages.loadFailed")}
            description={resolveLocalizedErrorMessage(employeeLoadError, {
              t,
              language,
              fallbackKey: "messages.loadFailed",
            })}
            primaryAction={{
              label: t("common:actions.retry"),
              icon: "refresh",
              onPress: () => void refresh(),
            }}
            secondaryAction={{
              label: t("common:actions.back"),
              icon: "arrow-left",
              onPress: handleBack,
            }}
          />
        </View>
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView style={styles.container} edges={["top", "left", "right"]}>
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={
          <RefreshControl
            refreshing={isRefreshing}
            onRefresh={() => void refresh()}
          />
        }
      >
        <View style={styles.header}>
          <View style={styles.headerText}>
            <Text variant="headlineSmall" style={styles.screenTitle}>{screenTitle}</Text>
            <Text variant="bodyMedium" style={styles.muted}>
              {selectedStoreName || selectedStoreCode || t("subtitle")}
            </Text>
          </View>
          <Button
            compact
            mode="outlined"
            icon="refresh"
            onPress={() => void refresh()}
            disabled={isRefreshing}
          >
            {t("common:actions.refresh")}
          </Button>
        </View>

        {!isManagementUnavailable && sectionStores.length > 1 ? (
          <Button
            mode="outlined"
            icon="storefront-outline"
            onPress={() => setStorePickerVisible(true)}
            contentStyle={styles.storePickerButtonContent}
          >
            {selectedStoreName ||
              selectedStoreCode ||
              t("common:labels.selectStore")}
          </Button>
        ) : null}

        {canSwitchMainTabs ? (
          <SegmentedButtons
            value={activeMainTab}
            onValueChange={(value) =>
              setActiveMainTab(value as AttendanceMainTab)
            }
            buttons={[
              { value: "personal", label: t("tabs.personalAttendance") },
              { value: "management", label: t("tabs.attendanceManagement") },
            ]}
            style={styles.sectionTabs}
          />
        ) : null}

        {isPersonalTab ? (
          <>
            <SegmentedButtons
              value={activePersonalTab}
              onValueChange={(value) =>
                setActivePersonalTab(value as PersonalAttendanceTab)
              }
              buttons={[
                {
                  value: "punchRecords",
                  label: t("tabs.short.punchSelf"),
                },
                {
                  value: "availabilityWeek",
                  label: t("tabs.short.mySchedule"),
                },
                {
                  value: "requests",
                  label: t("tabs.short.myRequests"),
                },
              ]}
              style={styles.sectionTabs}
            />
            {isPunchRecordsTab ? (
              <MonthDatePickerField
                label={t("datePicker.selectDate")}
                value={selectedDate}
                onChange={setSelectedDate}
              />
            ) : null}
            {isPunchRecordsTab ? (
              <>
                <TodayPunchCard
                title={
                  selectedDate === todayDate
                    ? t("sections.today")
                    : t("sections.selectedDay")
                }
                selectedDate={selectedDate}
                storeName={selectedStoreName}
                allowPunch={selectedDate === todayDate}
                today={todayQuery.data}
                isLoading={todayQuery.isFetching}
                isVerificationRefreshing={isRefreshingVerification}
                isPunching={attendanceScannerSubmitting || punchMutation.isPending}
                processingStage={punchStage}
                hasAuthorizedStores={stores.length > 0}
                verification={verification}
                lastQrPunch={lastQrPunch}
                trackingWarning={lastQrTrackingWarning}
                onScan={() => void openAttendanceScanner()}
                />
                <PunchAdjustmentCard
                  today={todayQuery.data}
                  selectedDate={selectedDate}
                  storeCode={selectedStoreCode}
                  storeName={selectedStoreName}
                  // 本人补卡直接生效需要同时管理该店并持有补卡修改权限，与后端判定一致。
                  isManagerStore={Boolean(
                    access.canAdjustAttendancePunch &&
                    selectedStoreCode &&
                    managerStores.some((store) => store.storeCode === selectedStoreCode),
                  )}
                  isBusy={isAdjustmentBusy}
                  onPreview={(payload) => previewPunchAdjustmentMutation.mutateAsync(payload)}
                  onSubmit={async (payload) => {
                    await createPunchAdjustmentMutation.mutateAsync(payload);
                  }}
                />
              </>
            ) : null}
            {isAvailabilityWeekTab ? (
              <>
                {employeeLoadError ? (
                  <Text accessibilityRole="alert" style={styles.muted}>
                    {getErrorMessage(employeeLoadError, "messages.loadFailed")}
                  </Text>
                ) : null}
                <MySchedulePanel
                  weekStartDate={employeeWeekStartDate}
                  today={todayDate}
                  week={weekQuery.data}
                  availability={availabilityQuery.data ?? []}
                  isLoading={employeeInitialLoading}
                  onPreviousWeek={() => setSelectedDate((current) => shiftDate(current, -7))}
                  onNextWeek={() => setSelectedDate((current) => shiftDate(current, 7))}
                  onFillAvailability={(dates) => setAvailabilitySheet({ dates })}
                  onEditAvailability={(item) => setAvailabilitySheet({ dates: [item.workDate], item })}
                />
                <AvailabilitySheet
                  visible={availabilitySheet !== null}
                  weekStartDate={employeeWeekStartDate}
                  today={todayDate}
                  initialDates={availabilitySheet?.dates ?? []}
                  editingItem={availabilitySheet?.item}
                  isBusy={isAvailabilityBusy}
                  onCreate={(payload) =>
                    createAvailabilityMutation.mutateAsync(
                      withSelectedStore(payload),
                    )
                  }
                  onVerify={async (payload) => {
                    const confirmedDates = await verifyAvailabilityBatch(withSelectedStore(payload));
                    void invalidateEmployeeData();
                    return confirmedDates;
                  }}
                  onUpdate={(availabilityGuid, payload) =>
                    updateAvailabilityMutation.mutateAsync({
                      availabilityGuid,
                      payload: withSelectedStore(payload),
                    })
                  }
                  onCancelItem={(availabilityGuid) =>
                    cancelAvailabilityMutation.mutate(availabilityGuid)
                  }
                  onDismiss={() => setAvailabilitySheet(null)}
                />
              </>
            ) : null}
            {isRequestsTab ? (
              <MyRequestsPanel
                storeCode={selectedStoreCode}
                today={todayDate}
                canApplyLeave={access.canViewAttendancePersonal}
                onRequestCorrection={() => setActivePersonalTab("punchRecords")}
                onMessage={showMessage}
              />
            ) : null}
          </>
        ) : null}

        {isManagementUnavailable ? (
          <EmptyState
            title={t("messages.noManagedStoreTitle")}
            description={t("messages.noManagedStoreDescription")}
            primaryAction={{
              label: t("common:actions.goToSettings"),
              icon: "cog-outline",
              onPress: () => router.navigate("/(shell)/settings"),
            }}
          />
        ) : null}

        {isManagementTab && managerStores.length === 0 ? (
          <EmptyState
            title={t("messages.noManagedStoreTitle")}
            description={t("messages.noManagedStoreDescription")}
            primaryAction={{
              label: t("common:actions.goToSettings"),
              icon: "cog-outline",
              onPress: () => router.navigate("/(shell)/settings"),
            }}
          />
        ) : null}

        {/* 页签栏不随单个页签的加载错误消失，否则其他页签（如打卡记录）也无法进入 */}
        {isManagementTab && managerStores.length > 0 ? (
          <>
            <SegmentedButtons
              value={activeManagementTab}
              onValueChange={(value) =>
                setActiveManagementTab(value as AttendanceManagementTab)
              }
              buttons={[
                // 打卡记录依赖 Punch.ViewManagedStore；无权限时不出现该页签。
                ...(canViewManagedPunches
                  ? [{ value: "punches", label: t("tabs.short.punches"), labelStyle: styles.managementTabLabel }]
                  : []),
                {
                  value: "schedule",
                  label: t("tabs.short.schedule"),
                  labelStyle: styles.managementTabLabel,
                },
                {
                  value: "leave",
                  label: pendingApprovals.length
                    ? t("tabs.short.approvalsWithCount", { count: pendingApprovals.length })
                    : t("tabs.short.approvals"),
                  labelStyle: styles.managementTabLabel,
                },
                {
                  value: "holidays",
                  label: t("tabs.short.holidays"),
                  labelStyle: styles.managementTabLabel,
                },
              ]}
              style={styles.sectionTabs}
            />
          </>
        ) : null}

        {isManagementTab && managerStores.length > 0 && managerLoadError ? (
          <EmptyState
            title={t("messages.managerLoadFailedTitle")}
            description={
              managerLoadError instanceof Error
                ? managerLoadError.message
                : t("messages.loadFailed")
            }
            primaryAction={{
              label: t("common:actions.retry"),
              icon: "refresh",
              onPress: () => void refresh(),
            }}
            secondaryAction={{
              label: t("common:actions.back"),
              icon: "arrow-left",
              onPress: handleBack,
            }}
          />
        ) : null}

        {isManagementTab && managerStores.length > 0 && !managerLoadError ? (
          <>
            {isManagementContentLoading ? (
              <View style={styles.inlineLoading}>
                <ActivityIndicator />
              </View>
            ) : null}
            {isPunchManagementTab ? (
              <ManagedPunchRecordsCard
                key={managedPunchDate ?? "today"}
                initialWorkDate={managedPunchDate}
                storeCode={selectedStoreCode}
                canAdjust={access.canAdjustAttendancePunch}
                onMessage={showMessage}
              />
            ) : null}
            {isScheduleManagementTab ? (
              <ScheduleManagementCard
                weekStartDate={managerWeekStartDate}
                storeCode={selectedStoreCode}
                storeName={selectedStoreName}
                users={storeUsersQuery.data ?? []}
                schedules={managerSchedulesQuery.data ?? []}
                isLoading={
                  storeUsersQuery.isLoading || managerSchedulesQuery.isLoading
                }
                isBusy={isScheduleBusy}
                onPreviousWeek={() =>
                  setManagerWeekStartDate((current) => addWeeks(current, -1))
                }
                onNextWeek={() =>
                  setManagerWeekStartDate((current) => addWeeks(current, 1))
                }
                onCreate={handleCreateSchedule}
                onUpdate={(scheduleGuid, payload) =>
                  updateScheduleMutation.mutate({ scheduleGuid, payload })
                }
                onDelete={(scheduleGuid) =>
                  deleteScheduleMutation.mutate(scheduleGuid)
                }
                onPublishWeek={handlePublishWeek}
              />
            ) : null}
            {isHolidayManagementTab && !isManagementContentLoading ? (
              <HolidayManagementCard
                holidays={holidaysQuery.data ?? []}
                storeCode={selectedStoreCode}
                storeName={selectedStoreName}
                isBusy={isHolidayBusy}
                isSyncBusy={syncHolidayMutation.isPending}
                canSync={canSyncHolidayManagement}
                syncDisabledReason={holidaySyncDisabledReason}
                selectedDate={selectedDate}
                onCreate={handleCreateHoliday}
                onUpdate={handleUpdateHoliday}
                onDelete={(holidayGuid) =>
                  deleteHolidayMutation.mutate(holidayGuid)
                }
                onSync={handleSyncHolidays}
              />
            ) : null}
            {isLeaveManagementTab && !isManagementContentLoading ? (
              <>
                <ManagerApprovalList
                  title={t("sections.approvals")}
                  emptyMessage={t("approvals.empty")}
                  approvals={pendingApprovals}
                  isBusy={isApprovalBusy}
                  canReview={canReviewAttendance}
                  onApprove={(payload) => approveMutation.mutate(payload)}
                  onReject={(payload) => rejectMutation.mutate(payload)}
                  onFixMissingClockOut={canViewManagedPunches ? (approval) => {
                    // 漏下班直接去打卡记录的那一天补录，店长不必再手动找日期。
                    setManagedPunchDate(approval.workDate?.slice(0, 10));
                    setActiveManagementTab("punches");
                  } : undefined}
                />
                <Button
                  mode="outlined"
                  icon={leaveRegisterVisible ? "chevron-up" : "account-plus-outline"}
                  onPress={() => setLeaveRegisterVisible((current) => !current)}
                  style={styles.registerLeaveButton}
                >
                  {leaveRegisterVisible ? t("leaveManagement.hideRegister") : t("leaveManagement.registerAction")}
                </Button>
                {leaveRegisterVisible && storeUsersQuery.error ? (
                  <Text accessibilityRole="alert" style={styles.muted}>
                    {getErrorMessage(storeUsersQuery.error, "messages.loadFailed")}
                  </Text>
                ) : null}
                {leaveRegisterVisible ? (
                <LeaveManagementCard
                  storeCode={selectedStoreCode}
                  storeName={selectedStoreName}
                  users={storeUsersQuery.data ?? []}
                  isBusy={isLeaveBusy}
                  onSubmit={async (payload) =>
                    createManagedLeaveMutation.mutateAsync(
                      withSelectedStore(payload),
                    ).then(() => undefined)
                  }
                  onShowMessage={showMessage}
                />
                ) : null}
              </>
            ) : null}
          </>
        ) : null}
      </ScrollView>
      {attendanceScannerVisible ? (
        <Modal
          animationType="slide"
          presentationStyle="fullScreen"
          visible
          onRequestClose={() => closeAttendanceScanner()}
        >
        <SafeAreaView style={styles.cameraContainer}>
          {attendanceCameraScan.permission?.granted ? (
            <CameraView
              style={styles.cameraView}
              barcodeScannerSettings={{ barcodeTypes: ["qr"] }}
              {...attendanceCameraScan.cameraProps}
              onBarcodeScanned={(event) => {
                if (attendanceScannerSessionGate.isExpiredToken(normalizeAttendanceQrTokenInput(event.data))) return;
                return attendanceCameraScan.cameraProps.onBarcodeScanned(event);
              }}
            />
          ) : (
            <View style={styles.cameraPermission}>
              <Text variant="titleMedium" selectable>
                {t("scanner.permissionTitle")}
              </Text>
              <Text selectable>{t("scanner.permissionDescription")}</Text>
              {attendanceScannerError && !attendanceScannerSubmitting ? (
                <Text selectable style={styles.cameraError}>
                  {attendanceScannerError}
                </Text>
              ) : null}
              <Button
                mode="contained"
                onPress={() => void (
                  attendanceScannerError
                    ? retryAttendanceScan()
                    : requestAttendanceCameraPermission()
                )}
              >
                {t(attendanceScannerError ? "scanner.retry" : "scanner.grantPermission")}
              </Button>
              <Button onPress={() => closeAttendanceScanner()}>
                {t("common:actions.cancel")}
              </Button>
            </View>
          )}
          {attendanceCameraScan.permission?.granted ? (
            <View style={styles.cameraOverlay} pointerEvents="box-none">
              {attendanceScannerError && !attendanceScannerSubmitting ? (
                <Text selectable style={styles.cameraError}>
                  {attendanceScannerError}
                </Text>
              ) : (
                <Text variant="titleLarge" style={styles.cameraTitle} selectable>
                  {t("scanner.title")}
                </Text>
              )}
              <View style={styles.cameraFrame} />
              {attendanceScannerError && attendanceScannerPaused && !attendanceScannerSubmitting ? (
                <Button
                  mode="contained"
                  buttonColor="#FFFFFF"
                  textColor="#111827"
                  onPress={() => void retryAttendanceScan()}
                >
                  {t("scanner.retry")}
                </Button>
              ) : null}
              {attendanceScannerSubmitting ? (
                <ActivityIndicator color="#FFFFFF" />
              ) : (
                <Button
                  mode="contained"
                  buttonColor="#FFFFFF"
                  textColor="#111827"
                  onPress={() => closeAttendanceScanner()}
                >
                  {t("common:actions.cancel")}
                </Button>
              )}
            </View>
          ) : null}
        </SafeAreaView>
        </Modal>
      ) : null}
      <Snackbar
        visible={snackbarVisible}
        onDismiss={() => setSnackbarVisible(false)}
      >
        {snackbarMessage}
      </Snackbar>
      <StorePickerModal
        visible={storePickerVisible}
        presentation="sheet"
        stores={sectionStores}
        selectedStoreCode={selectedStoreCode}
        renderStoreLabel={(store) => (
          <View>
            <Text variant="bodyMedium">{store.storeName || store.storeCode}</Text>
            <Text variant="bodySmall" style={styles.storePickerCode}>
              {store.storeCode}
            </Text>
          </View>
        )}
        title={t("common:labels.selectStore")}
        cancelLabel={t("common:actions.cancel")}
        onDismiss={() => setStorePickerVisible(false)}
        onSelectStore={handleSelectStore}
      />
    </SafeAreaView>
  );
}

export default AttendanceScreen;

const styles = StyleSheet.create({
  // 管理端四个页签在 375pt 宽屏上平分，字号收小以免英文标签被截断。
  managementTabLabel: {
    fontSize: 13,
  },
  registerLeaveButton: {
    alignSelf: "stretch",
  },
  storePickerCode: {
    color: "#6B7280",
  },
  centered: {
    alignItems: "center",
    flex: 1,
    gap: 12,
    justifyContent: "center",
    padding: 20,
  },
  container: {
    backgroundColor: "#F4F6F8",
    flex: 1,
  },
  cameraContainer: {
    backgroundColor: "#000000",
    flex: 1,
  },
  cameraFrame: {
    borderColor: "#FFFFFF",
    borderRadius: 18,
    borderWidth: 3,
    height: 240,
    width: 240,
  },
  cameraError: {
    backgroundColor: "#FFFFFF",
    borderRadius: 10,
    color: "#B42318",
    paddingHorizontal: 14,
    paddingVertical: 10,
    textAlign: "center",
  },
  cameraOverlay: {
    alignItems: "center",
    bottom: 32,
    gap: 24,
    justifyContent: "space-between",
    left: 24,
    position: "absolute",
    right: 24,
    top: 32,
  },
  cameraPermission: {
    alignItems: "center",
    backgroundColor: "#FFFFFF",
    flex: 1,
    gap: 16,
    justifyContent: "center",
    padding: 24,
  },
  cameraTitle: {
    color: "#FFFFFF",
    textAlign: "center",
  },
  cameraView: {
    flex: 1,
  },
  content: {
    gap: 16,
    paddingHorizontal: 16,
    paddingTop: 12,
    paddingBottom: 32,
  },
  errorText: {
    color: "#B42318",
    textAlign: "center",
  },
  header: {
    alignItems: "center",
    flexDirection: "row",
    gap: 12,
    justifyContent: "space-between",
  },
  headerText: {
    flex: 1,
  },
  screenTitle: {
    color: "#101828",
    fontWeight: "700",
  },
  inlineLoading: {
    alignItems: "center",
    padding: 20,
  },
  muted: {
    color: "#475467",
  },
  sectionTabs: {
    backgroundColor: "#EEF2F6",
    borderRadius: 10,
    marginTop: 0,
    padding: 4,
  },
  storePickerButtonContent: {
    justifyContent: "flex-start",
  },
});

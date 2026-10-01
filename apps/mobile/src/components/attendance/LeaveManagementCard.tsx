import { useEffect, useMemo, useRef, useState } from "react";
import { Image, Pressable, ScrollView, StyleSheet, View } from "react-native";
import { CameraView, useCameraPermissions } from "expo-camera";
import {
  ActivityIndicator,
  Button,
  Card,
  HelperText,
  Icon,
  IconButton,
  Modal,
  Portal,
  SegmentedButtons,
  Switch,
  Text,
  TextInput,
} from "react-native-paper";
import {
  MonthDatePicker,
  normalizeMonthDate,
} from "@/components/attendance/MonthDatePicker";
import {
  getAttendanceLeaveAttachmentUploadSignature,
} from "@/modules/attendance/api";
import { leaveDayCount, shiftDate } from "@/modules/attendance/attendance-my-week";
import { uploadAttendanceLeaveAttachmentToSignedUrl } from "@/modules/attendance/leave-attachment-upload";
import type {
  AttendanceLeaveRequestPayload,
  AttendanceLeaveType,
} from "@/modules/attendance/types";
import type { StoreUserListItem } from "@/modules/users/types";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import { reviewAwareFetch } from "@/modules/ios-review/network";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ATTENDANCE_STATUS_TONES, StatusPill } from "./AdjustmentFormControls";

type LeaveFormState = AttendanceLeaveRequestPayload & {
  userGuid: string;
  reason: string;
};

const SUPPORTED_LEAVE_TYPES: AttendanceLeaveType[] = [
  "AnnualLeave",
  "SickLeave",
];

/** 员工列表展开后的最大高度：超过约 6 行时在卡片内滚动，避免把保存按钮挤出屏幕。 */
const EMPLOYEE_LIST_MAX_HEIGHT = 288;


/** 日期框空间有限：显示「10-01 周四」，年份在请假场景里几乎总是当年。 */
function formatShortDayLabel(value: string, weekdayLabel: (index: number) => string) {
  const date = new Date(`${value}T00:00:00Z`);
  if (Number.isNaN(date.getTime())) return value;
  return `${value.slice(5, 10)} ${weekdayLabel((date.getUTCDay() + 6) % 7)}`;
}

function createEmptyForm(storeCode?: string): LeaveFormState {
  const today = normalizeMonthDate();
  return {
    userGuid: "",
    storeCode,
    leaveType: "AnnualLeave",
    startDate: today,
    endDate: today,
    startTime: "",
    endTime: "",
    reason: "",
    attachmentUrl: "",
  };
}

function normalizeEmploymentType(value?: string) {
  return value?.trim().toLowerCase() ?? "";
}

/** 只有全职/兼职员工可登记年假、病假（临时工没有带薪假）。 */
function isEligibleEmployee(user: StoreUserListItem) {
  const employmentType = normalizeEmploymentType(user.employmentType);
  return employmentType === "fulltime" || employmentType === "parttime";
}

function getEmployeeLabel(user: StoreUserListItem) {
  return (
    user.fullName?.trim() ||
    user.username?.trim() ||
    user.email?.trim() ||
    user.userGUID
  );
}

function getEmployeeInitial(user: StoreUserListItem) {
  return getEmployeeLabel(user).trim().charAt(0).toUpperCase() || "?";
}

function trimToUndefined(value?: string) {
  const nextValue = value?.trim();
  return nextValue ? nextValue : undefined;
}

/** 圆形首字母头像：选择框与列表共用，未选员工时显示占位图标。 */
function EmployeeAvatar({ user, size = 32 }: { user?: StoreUserListItem; size?: number }) {
  return (
    <View
      style={[
        styles.avatar,
        { borderRadius: size / 2, height: size, width: size },
        user ? null : styles.avatarEmpty,
      ]}
    >
      {user ? (
        <Text variant="labelLarge" style={styles.avatarText}>{getEmployeeInitial(user)}</Text>
      ) : (
        <Icon source="account-outline" size={size * 0.55} color={HB_COLORS.textSecondary} />
      )}
    </View>
  );
}

/**
 * 日期框：左右箭头按天切换（请假跨度通常很短，点按最快）；
 * 点中间日期可弹出月历，用于跨度较长时直接跳转。
 */
function DateStepper({
  label,
  value,
  minDate,
  disabled,
  onChange,
}: {
  label: string;
  value: string;
  minDate?: string;
  disabled: boolean;
  onChange: (value: string) => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const [calendarVisible, setCalendarVisible] = useState(false);
  // 结束日期已等于下限时禁用「前一天」，从源头保证结束不早于开始。
  const canGoBack = !disabled && (!minDate || value > minDate);

  return (
    <View style={[styles.dateBox, disabled ? styles.disabled : null]}>
      <Text variant="labelSmall" style={styles.muted}>{label}</Text>
      <View style={styles.dateStepRow}>
        <IconButton
          icon="chevron-left"
          size={18}
          style={styles.dateArrow}
          disabled={!canGoBack}
          accessibilityLabel={t("leaveManagement.a11y.previousDay", { label })}
          onPress={() => onChange(shiftDate(value, -1))}
        />
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("leaveManagement.a11y.pickDate", { label })}
          disabled={disabled}
          onPress={() => setCalendarVisible(true)}
          hitSlop={6}
          style={({ pressed }) => [styles.dateValue, pressed ? styles.pressed : null]}
        >
          <Text variant="titleSmall" style={styles.tabular} numberOfLines={1}>
            {formatShortDayLabel(value, (index) => t(`weekdays.${index}`))}
          </Text>
        </Pressable>
        <IconButton
          icon="chevron-right"
          size={18}
          style={styles.dateArrow}
          disabled={disabled}
          accessibilityLabel={t("leaveManagement.a11y.nextDay", { label })}
          onPress={() => onChange(shiftDate(value, 1))}
        />
      </View>

      <Portal>
        <Modal
          visible={calendarVisible}
          onDismiss={() => setCalendarVisible(false)}
          contentContainerStyle={styles.dialog}
        >
          <View style={styles.dialogHeader}>
            <Text variant="titleMedium">{label}</Text>
            <Button onPress={() => setCalendarVisible(false)}>{t("common:actions.cancel")}</Button>
          </View>
          <MonthDatePicker
            value={value}
            minDate={minDate}
            onChange={(nextValue) => {
              onChange(nextValue);
              setCalendarVisible(false);
            }}
          />
        </Modal>
      </Portal>
    </View>
  );
}

interface LeaveManagementCardProps {
  storeCode?: string;
  storeName?: string;
  users: StoreUserListItem[];
  isBusy: boolean;
  onSubmit: (payload: AttendanceLeaveRequestPayload) => void | Promise<void>;
  onShowMessage?: (message: string) => void;
}

/**
 * 店长代员工登记年假/病假（审核页签内展开的紧凑表单）。
 * 病假必须先拍医嘱并上传成功才能保存；员工只列出全职/兼职。
 */
export function LeaveManagementCard({
  storeCode,
  storeName,
  users,
  isBusy,
  onSubmit,
  onShowMessage,
}: LeaveManagementCardProps) {
  const { t, language } = useAppTranslation(["attendance", "common"]);
  const [form, setForm] = useState<LeaveFormState>(() => createEmptyForm(storeCode));
  const [employeeListOpen, setEmployeeListOpen] = useState(false);
  const [timeRangeEnabled, setTimeRangeEnabled] = useState(false);
  const [cameraVisible, setCameraVisible] = useState(false);
  const [cameraError, setCameraError] = useState("");
  const [uploadPreviewUri, setUploadPreviewUri] = useState("");
  const [isUploadingAttachment, setIsUploadingAttachment] = useState(false);
  const [permission, requestPermission] = useCameraPermissions();
  const cameraRef = useRef<CameraView | null>(null);

  const eligibleUsers = useMemo(
    () => users.filter(isEligibleEmployee).sort((left, right) =>
      getEmployeeLabel(left).localeCompare(getEmployeeLabel(right)),
    ),
    [users],
  );
  const selectedEmployee = useMemo(
    () => eligibleUsers.find((user) => user.userGUID === form.userGuid),
    [eligibleUsers, form.userGuid],
  );
  const canSelectEmployee = eligibleUsers.length > 0;
  const employeeSelectorDisabled = !storeCode || !canSelectEmployee || isBusy;
  const isSickLeave = form.leaveType === "SickLeave";
  const hasAttachment = Boolean(uploadPreviewUri || form.attachmentUrl);
  const dayCount = leaveDayCount(form.startDate, form.endDate);
  const canSubmit = Boolean(
    storeCode &&
      selectedEmployee &&
      form.startDate.trim() &&
      form.endDate.trim() &&
      (!isSickLeave || form.attachmentUrl?.trim()),
  );

  useEffect(() => {
    setForm((current) => ({ ...current, storeCode }));
  }, [storeCode]);

  useEffect(() => {
    if (!form.userGuid) {
      return;
    }
    if (eligibleUsers.some((user) => user.userGUID === form.userGuid)) {
      return;
    }
    setForm((current) => ({ ...current, userGuid: "" }));
  }, [eligibleUsers, form.userGuid]);

  // 选择框不可用（换店、员工列表为空、提交中）时收起列表，避免残留展开态。
  useEffect(() => {
    if (employeeSelectorDisabled) {
      setEmployeeListOpen(false);
    }
  }, [employeeSelectorDisabled]);

  const setField = <K extends keyof LeaveFormState>(
    key: K,
    value: LeaveFormState[K],
  ) => {
    setForm((current) => ({ ...current, [key]: value }));
  };

  const employmentTypeLabel = (user: StoreUserListItem) => {
    const type = normalizeEmploymentType(user.employmentType);
    return type ? t(`leaveManagement.employmentTypes.${type}`, { defaultValue: user.employmentType }) : "";
  };

  const handleSelectEmployee = (user: StoreUserListItem) => {
    setField("userGuid", user.userGUID);
    setEmployeeListOpen(false);
  };

  const handleStartDateChange = (value: string) => {
    // 开始日期后移超过结束日期时，结束日期跟着走，保证结束不早于开始。
    setForm((current) => ({
      ...current,
      startDate: value,
      endDate: value > current.endDate ? value : current.endDate,
    }));
  };

  const handleEndDateChange = (value: string) => {
    setForm((current) => ({
      ...current,
      endDate: value < current.startDate ? current.startDate : value,
    }));
  };

  const handleTimeRangeToggle = (enabled: boolean) => {
    setTimeRangeEnabled(enabled);
    if (!enabled) {
      // 关闭「指定时段」即按整天登记，清掉已填时间，避免隐藏字段被提交。
      setForm((current) => ({ ...current, startTime: "", endTime: "" }));
    }
  };

  const handleLeaveTypeChange = (value: string) => {
    const leaveType = value as AttendanceLeaveType;
    setForm((current) => ({
      ...current,
      leaveType,
      attachmentUrl: leaveType === "SickLeave" ? current.attachmentUrl : "",
    }));
    if (leaveType !== "SickLeave") {
      setUploadPreviewUri("");
      setCameraError("");
      setCameraVisible(false);
    }
  };

  const resetForm = () => {
    setForm(createEmptyForm(storeCode));
    setTimeRangeEnabled(false);
    setEmployeeListOpen(false);
    setUploadPreviewUri("");
    setCameraError("");
  };

  const submit = async () => {
    if (!storeCode || !selectedEmployee) {
      return;
    }

    try {
      await onSubmit({
        userGuid: selectedEmployee.userGUID,
        storeCode,
        leaveType: form.leaveType,
        startDate: form.startDate.trim(),
        endDate: form.endDate.trim(),
        startTime: trimToUndefined(form.startTime),
        endTime: trimToUndefined(form.endTime),
        reason: trimToUndefined(form.reason),
        attachmentUrl: isSickLeave ? trimToUndefined(form.attachmentUrl) : undefined,
      });
      resetForm();
    } catch {
      // The parent mutation already shows the API error; keep the form intact for retry.
    }
  };

  const handleOpenCamera = async () => {
    if (!permission?.granted) {
      const nextPermission = await requestPermission();
      if (!nextPermission.granted) {
        const message = t("leaveManagement.messages.permissionRequired");
        setCameraError(message);
        onShowMessage?.(message);
        return;
      }
    }

    setCameraError("");
    setCameraVisible(true);
  };

  const handleCapturePhoto = async () => {
    if (!cameraRef.current || isUploadingAttachment) {
      return;
    }

    try {
      setIsUploadingAttachment(true);
      setCameraError("");
      const photo = await cameraRef.current.takePictureAsync({
        quality: 0.7,
      });

      if (!photo?.uri) {
        throw new Error(t("leaveManagement.messages.captureFailed"));
      }

      if (isIosReviewSessionActive()) {
        // 审核模式直接保留相机本地 URI，不申请签名或上传对象存储。
        setField("attachmentUrl", photo.uri);
        setUploadPreviewUri(photo.uri);
        setCameraVisible(false);
        onShowMessage?.(t("leaveManagement.messages.attachmentUploaded"));
        return;
      }

      const fileResponse = await reviewAwareFetch(photo.uri);
      const fileBlob = await fileResponse.blob();
      if (!fileBlob.size) {
        throw new Error(t("leaveManagement.messages.uploadFailed"));
      }

      const fileName = `leave-attachment-${Date.now()}.jpg`;
      const signature = await getAttendanceLeaveAttachmentUploadSignature({
        fileName,
        contentType: "image/jpeg",
        fileSize: fileBlob.size,
      });
      const result = await uploadAttendanceLeaveAttachmentToSignedUrl(
        photo.uri,
        signature,
      );

      setField("attachmentUrl", result.downloadUrl);
      setUploadPreviewUri(photo.uri);
      setCameraVisible(false);
      onShowMessage?.(t("leaveManagement.messages.attachmentUploaded"));
    } catch (error) {
      const message = resolveLocalizedErrorMessage(error, {
        t,
        language,
        fallbackKey: "leaveManagement.messages.uploadFailed",
      });
      setCameraError(message);
      onShowMessage?.(message);
    } finally {
      setIsUploadingAttachment(false);
    }
  };

  const attachmentDisabled = isBusy || isUploadingAttachment;

  return (
    <>
      <Card mode="outlined" style={styles.card}>
        <Card.Content style={styles.content}>
          <View style={styles.header}>
            <Text variant="titleMedium" style={styles.title}>{t("leaveManagement.registerAction")}</Text>
            <Text variant="bodySmall" style={styles.muted} numberOfLines={1}>
              {storeName || storeCode || t("leaveManagement.noStore", { defaultValue: "Select a store first" })}
            </Text>
          </View>

          {/* 员工选择：卡片内展开单行列表，不再叠加弹层 */}
          <View style={[styles.selectorShell, employeeListOpen ? styles.selectorShellOpen : null]}>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("leaveManagement.fields.employee")}
              accessibilityState={{ disabled: employeeSelectorDisabled, expanded: employeeListOpen }}
              disabled={employeeSelectorDisabled}
              onPress={() => setEmployeeListOpen((current) => !current)}
              style={({ pressed }) => [
                styles.selector,
                pressed ? styles.pressed : null,
                employeeSelectorDisabled ? styles.disabled : null,
              ]}
            >
              <EmployeeAvatar user={selectedEmployee} />
              <View style={styles.selectorText}>
                {selectedEmployee ? (
                  <>
                    <Text variant="titleSmall" numberOfLines={1}>{getEmployeeLabel(selectedEmployee)}</Text>
                    <Text variant="bodySmall" style={styles.muted} numberOfLines={1}>
                      {employmentTypeLabel(selectedEmployee)}
                    </Text>
                  </>
                ) : (
                  <Text variant="bodyLarge" style={styles.placeholder}>
                    {t("leaveManagement.fields.selectEmployee")}
                  </Text>
                )}
              </View>
              <Icon
                source={employeeListOpen ? "chevron-up" : "chevron-down"}
                size={22}
                color={HB_COLORS.textSecondary}
              />
            </Pressable>

            {employeeListOpen ? (
              <ScrollView
                style={styles.employeeList}
                nestedScrollEnabled
                keyboardShouldPersistTaps="handled"
              >
                {eligibleUsers.map((user, index) => {
                  const selected = user.userGUID === form.userGuid;
                  return (
                    <Pressable
                      key={user.userGUID}
                      accessibilityRole="radio"
                      accessibilityState={{ selected }}
                      onPress={() => handleSelectEmployee(user)}
                      style={({ pressed }) => [
                        styles.employeeRow,
                        index > 0 ? styles.employeeRowDivider : null,
                        selected ? styles.employeeRowSelected : null,
                        pressed ? styles.pressed : null,
                      ]}
                    >
                      <EmployeeAvatar user={user} size={28} />
                      <Text variant="bodyMedium" style={styles.employeeName} numberOfLines={1}>
                        {getEmployeeLabel(user)}
                      </Text>
                      <Text variant="bodySmall" style={styles.muted}>{employmentTypeLabel(user)}</Text>
                      <View style={styles.checkSlot}>
                        {selected ? (
                          <Icon source="check" size={18} color={ATTENDANCE_STATUS_TONES.accent.text} />
                        ) : null}
                      </View>
                    </Pressable>
                  );
                })}
              </ScrollView>
            ) : null}
          </View>
          {!storeCode ? (
            <HelperText type="error" visible style={styles.helper}>
              {t("leaveManagement.noStore", { defaultValue: "Select a store first" })}
            </HelperText>
          ) : null}
          {storeCode && !canSelectEmployee && !isBusy ? (
            <HelperText type="info" visible style={styles.helper}>
              {t("leaveManagement.noEligibleEmployees")}
            </HelperText>
          ) : null}

          <SegmentedButtons
            value={form.leaveType}
            onValueChange={handleLeaveTypeChange}
            buttons={SUPPORTED_LEAVE_TYPES.map((leaveType) => ({
              value: leaveType,
              label: t(`leaveTypes.${leaveType}`),
              disabled: isBusy,
            }))}
          />

          <View style={styles.dateBoxes}>
            <DateStepper
              label={t("fields.startDate")}
              value={form.startDate}
              disabled={isBusy}
              onChange={handleStartDateChange}
            />
            <DateStepper
              label={t("fields.endDate")}
              value={form.endDate}
              minDate={form.startDate}
              disabled={isBusy}
              onChange={handleEndDateChange}
            />
          </View>

          <View style={styles.inlineRow}>
            {dayCount > 0 ? (
              <StatusPill label={t("leaveManagement.dayCount", { count: dayCount })} tone="accent" />
            ) : null}
            <View style={styles.flex} />
            <Text variant="bodyMedium" style={styles.muted}>{t("leaveManagement.fields.specifyTime")}</Text>
            <Switch
              value={timeRangeEnabled}
              onValueChange={handleTimeRangeToggle}
              disabled={isBusy}
              accessibilityLabel={t("leaveManagement.fields.specifyTime")}
            />
          </View>
          {timeRangeEnabled ? (
            <View style={styles.timeRow}>
              <TextInput
                mode="outlined"
                dense
                label={t("fields.startTimeOptional")}
                value={form.startTime ?? ""}
                placeholder={t("common:placeholders.time")}
                keyboardType="numbers-and-punctuation"
                style={styles.flex}
                onChangeText={(value) => setField("startTime", value)}
                disabled={isBusy}
              />
              <TextInput
                mode="outlined"
                dense
                label={t("fields.endTimeOptional")}
                value={form.endTime ?? ""}
                placeholder={t("common:placeholders.time")}
                keyboardType="numbers-and-punctuation"
                style={styles.flex}
                onChangeText={(value) => setField("endTime", value)}
                disabled={isBusy}
              />
            </View>
          ) : null}

          {isSickLeave ? (
            hasAttachment ? (
              <View style={styles.attachmentDone}>
                {uploadPreviewUri ? (
                  <Image source={{ uri: uploadPreviewUri }} style={styles.thumbnail} />
                ) : (
                  <View style={[styles.thumbnail, styles.thumbnailFallback]}>
                    <Icon source="file-image-outline" size={24} color={HB_COLORS.textSecondary} />
                  </View>
                )}
                <View style={styles.attachmentText}>
                  <Text variant="titleSmall">{t("leaveManagement.fields.attachment")}</Text>
                  <StatusPill label={t("leaveManagement.attachment.uploaded")} tone="success" />
                </View>
                <Button
                  mode="outlined"
                  compact
                  icon="camera-retake-outline"
                  onPress={() => void handleOpenCamera()}
                  disabled={attachmentDisabled}
                  loading={isUploadingAttachment}
                >
                  {t("leaveManagement.actions.retakePhoto")}
                </Button>
              </View>
            ) : (
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={t("leaveManagement.attachment.captureTitle")}
                accessibilityHint={t("leaveManagement.attachmentRequired")}
                disabled={attachmentDisabled}
                onPress={() => void handleOpenCamera()}
                style={({ pressed }) => [
                  styles.captureBox,
                  pressed ? styles.pressed : null,
                  attachmentDisabled ? styles.disabled : null,
                ]}
              >
                {isUploadingAttachment ? (
                  <ActivityIndicator size={24} />
                ) : (
                  <Icon source="camera-outline" size={28} color={ATTENDANCE_STATUS_TONES.accent.text} />
                )}
                <Text variant="titleSmall" style={styles.captureTitle}>
                  {t("leaveManagement.attachment.captureTitle")}
                </Text>
                <Text variant="bodySmall" style={styles.muted}>
                  {t("leaveManagement.attachment.captureHint")}
                </Text>
              </Pressable>
            )
          ) : null}
          {isSickLeave && cameraError && !cameraVisible ? (
            <HelperText type="error" visible style={styles.helper}>{cameraError}</HelperText>
          ) : null}

          <TextInput
            mode="outlined"
            dense
            label={t("fields.reason")}
            value={form.reason}
            multiline
            onChangeText={(value) => setField("reason", value)}
            disabled={isBusy}
          />

          <View style={styles.actions}>
            <Button mode="text" onPress={resetForm} disabled={isBusy}>
              {t("leaveManagement.actions.reset")}
            </Button>
            <Button
              mode="contained"
              icon="check"
              onPress={() => void submit()}
              disabled={!canSubmit || isBusy}
              loading={isBusy}
              style={styles.saveButton}
              contentStyle={styles.saveButtonContent}
            >
              {t("leaveManagement.actions.save")}
            </Button>
          </View>
        </Card.Content>
      </Card>

      <Portal>
        <Modal
          visible={cameraVisible}
          onDismiss={() => {
            if (!isUploadingAttachment) {
              setCameraVisible(false);
            }
          }}
          contentContainerStyle={styles.cameraModal}
        >
          <Text variant="titleLarge">
            {t("leaveManagement.camera.title")}
          </Text>
          {!permission?.granted ? (
            <View style={styles.cameraPermissionState}>
              <Text variant="titleMedium">
                {t("leaveManagement.camera.permissionTitle")}
              </Text>
              <Text variant="bodyMedium" style={styles.muted}>
                {t("leaveManagement.camera.permissionDescription")}
              </Text>
              <Button mode="contained" onPress={() => void handleOpenCamera()}>
                {t("leaveManagement.actions.grantCameraPermission")}
              </Button>
            </View>
          ) : (
            <>
              <CameraView
                ref={cameraRef}
                facing="back"
                style={styles.cameraPreview}
              />
              <Text variant="bodySmall" style={styles.muted}>
                {t("leaveManagement.messages.cameraReady")}
              </Text>
              {cameraError ? (
                <Text variant="bodySmall" style={styles.dangerText}>{cameraError}</Text>
              ) : null}
              <View style={styles.actions}>
                <Button
                  mode="outlined"
                  onPress={() => setCameraVisible(false)}
                  disabled={isUploadingAttachment}
                >
                  {t("common:actions.cancel")}
                </Button>
                <Button
                  mode="contained"
                  icon="camera"
                  onPress={() => void handleCapturePhoto()}
                  loading={isUploadingAttachment}
                  disabled={isUploadingAttachment}
                >
                  {t("leaveManagement.actions.capture")}
                </Button>
              </View>
            </>
          )}
        </Modal>
      </Portal>
    </>
  );
}

const styles = StyleSheet.create({
  actions: {
    alignItems: "center",
    flexDirection: "row",
    gap: HB_SPACING.xs,
    justifyContent: "flex-end",
  },
  attachmentDone: {
    alignItems: "center",
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.control,
    borderWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: HB_SPACING.sm,
    padding: HB_SPACING.xs,
  },
  attachmentText: {
    alignItems: "flex-start",
    flex: 1,
    gap: HB_SPACING.xxs,
  },
  avatar: {
    alignItems: "center",
    backgroundColor: ATTENDANCE_STATUS_TONES.accent.background,
    justifyContent: "center",
  },
  avatarEmpty: {
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  avatarText: {
    color: ATTENDANCE_STATUS_TONES.accent.text,
    fontWeight: "600",
  },
  cameraModal: {
    alignSelf: "center",
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.sheet,
    gap: HB_SPACING.sm,
    padding: HB_SPACING.md,
    width: "92%",
  },
  cameraPermissionState: {
    gap: HB_SPACING.sm,
  },
  cameraPreview: {
    borderRadius: HB_RADIUS.control,
    height: 360,
    overflow: "hidden",
  },
  captureBox: {
    alignItems: "center",
    backgroundColor: HB_COLORS.surface,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    borderStyle: "dashed",
    borderWidth: 1,
    gap: HB_SPACING.xxs,
    justifyContent: "center",
    minHeight: 112,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
  },
  captureTitle: {
    color: ATTENDANCE_STATUS_TONES.accent.text,
  },
  card: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    elevation: 0,
  },
  checkSlot: {
    alignItems: "center",
    width: 20,
  },
  content: {
    gap: HB_SPACING.sm,
    paddingVertical: HB_SPACING.sm,
  },
  dangerText: {
    color: HB_COLORS.danger,
  },
  dateArrow: {
    margin: 0,
  },
  dateBox: {
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    borderWidth: StyleSheet.hairlineWidth,
    flex: 1,
    paddingHorizontal: HB_SPACING.xxs,
    paddingTop: HB_SPACING.xs,
  },
  dateBoxes: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
  },
  dateStepRow: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
  },
  dateValue: {
    alignItems: "center",
    borderRadius: HB_RADIUS.control,
    flex: 1,
    paddingVertical: HB_SPACING.xxs,
  },
  dialog: {
    alignSelf: "center",
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.sheet,
    gap: HB_SPACING.xs,
    padding: HB_SPACING.md,
    width: "92%",
  },
  dialogHeader: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
  },
  disabled: {
    opacity: 0.5,
  },
  employeeList: {
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
    flexGrow: 0,
    maxHeight: EMPLOYEE_LIST_MAX_HEIGHT,
  },
  employeeName: {
    color: HB_COLORS.textPrimary,
    flex: 1,
  },
  employeeRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: HB_SPACING.sm,
    minHeight: 48,
    paddingHorizontal: HB_SPACING.sm,
  },
  employeeRowDivider: {
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
  },
  employeeRowSelected: {
    backgroundColor: ATTENDANCE_STATUS_TONES.accent.background,
  },
  flex: {
    flex: 1,
  },
  header: {
    gap: 2,
  },
  helper: {
    marginTop: -HB_SPACING.xs,
    paddingHorizontal: 0,
  },
  inlineRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: HB_SPACING.xs,
  },
  muted: {
    color: HB_COLORS.textSecondary,
  },
  placeholder: {
    color: HB_COLORS.textSecondary,
  },
  pressed: {
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  saveButton: {
    flex: 1,
  },
  saveButtonContent: {
    minHeight: 44,
  },
  selector: {
    alignItems: "center",
    flexDirection: "row",
    gap: HB_SPACING.sm,
    minHeight: 52,
    paddingHorizontal: HB_SPACING.sm,
  },
  selectorShell: {
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    borderWidth: StyleSheet.hairlineWidth,
    overflow: "hidden",
  },
  selectorShellOpen: {
    borderColor: ATTENDANCE_STATUS_TONES.accent.text,
  },
  selectorText: {
    flex: 1,
    gap: 1,
  },
  tabular: {
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  thumbnail: {
    borderRadius: HB_RADIUS.control,
    height: 56,
    width: 56,
  },
  thumbnailFallback: {
    alignItems: "center",
    backgroundColor: HB_COLORS.surfaceMuted,
    justifyContent: "center",
  },
  timeRow: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
  },
  title: {
    color: HB_COLORS.textPrimary,
    fontWeight: "600",
  },
});

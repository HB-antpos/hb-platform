// 「带薪病假」（设计图 B）：只能选期限内、有排班且没打卡的日子（可多选），医嘱照片必填，备注选填。
// 带薪工时 = 所选排班扣餐后的小时之和（后端给的 paidHours）；提交时带上所选排班与医嘱地址，最终以后端校验为准。
import { useMemo, useState } from "react";
import { Pressable, StyleSheet } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { ActivityIndicator, Text, TextInput } from "react-native-paper";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  getMyLeaveOptions,
  LEAVE_OPTIONS_QUERY_KEY,
  MY_LEAVE_REQUESTS_QUERY_KEY,
  submitMyLeaveRequest,
} from "@/modules/attendance/leave-api";
import {
  addDays,
  formatHours,
  listSickLeaveCandidates,
  sickLeaveDateRange,
  sickLeaveEarliestDate,
  sumSickPaidHours,
  type SickCandidateState,
} from "@/modules/attendance/leave-rules";
import type { AttendanceMyLeaveOptions, AttendanceSickLeaveCandidate } from "@/modules/attendance/leave-types";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { clauseSeparator } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { CertificatePhotoField, useCertificatePhotos } from "./CertificatePhotoField";
import { LeaveCard, LeaveCardHeader, LeaveNotice, LeaveScreenFrame, PrimaryBottomButton, useLeaveDateFormat } from "./leave-ui";

export interface PaidSickLeaveContentProps {
  storeCode?: string;
  onBack: () => void;
  /** 提交成功（列表已刷新）后调用，路由外壳在这里返回上一页。 */
  onSubmitted: () => void;
}

export function PaidSickLeaveContent({ storeCode, onBack, onSubmitted }: PaidSickLeaveContentProps) {
  const { t } = useAppTranslation(["attendanceLeave", "common"]);
  const optionsQuery = useQuery({
    queryKey: [...LEAVE_OPTIONS_QUERY_KEY, storeCode ?? ""],
    queryFn: () => getMyLeaveOptions(storeCode),
  });
  const options = optionsQuery.data;
  const title = t("sick.title");

  if (!options) {
    return (
      <LeaveScreenFrame title={title} onBack={onBack}>
        {optionsQuery.isError ? (
          <LeaveNotice text={t("messages.loadFailed")} actionLabel={t("messages.retry")} onAction={() => void optionsQuery.refetch()} />
        ) : (
          <ActivityIndicator style={styles.loading} />
        )}
      </LeaveScreenFrame>
    );
  }
  if (!options.paidLeaveEligible) {
    return (
      <LeaveScreenFrame title={title} onBack={onBack}>
        <LeaveNotice tone="info" text={t("messages.notEligible")} />
      </LeaveScreenFrame>
    );
  }
  return <PaidSickLeaveForm options={options} storeCode={storeCode ?? options.storeCode} onBack={onBack} onSubmitted={onSubmitted} />;
}

function PaidSickLeaveForm({
  options,
  storeCode,
  onBack,
  onSubmitted,
}: {
  options: AttendanceMyLeaveOptions;
  storeCode?: string;
  onBack: () => void;
  onSubmitted: () => void;
}) {
  const { t, language } = useAppTranslation(["attendanceLeave", "common"]);
  const { monthDay } = useLeaveDateFormat();
  const queryClient = useQueryClient();
  const { rules, today } = options;
  const [selected, setSelected] = useState<string[]>([]);
  const [reason, setReason] = useState("");
  const [error, setError] = useState("");
  const certificate = useCertificatePhotos();

  const candidates = useMemo(
    () => listSickLeaveCandidates(options.sickLeaveCandidates, today, rules),
    [options.sickLeaveCandidates, rules, today],
  );
  const earliest = sickLeaveEarliestDate(today, rules);
  const paidHours = sumSickPaidHours(candidates, selected);

  const toggle = (scheduleGuid: string) =>
    setSelected((current) => (current.includes(scheduleGuid) ? current.filter((item) => item !== scheduleGuid) : [...current, scheduleGuid]));

  const mutation = useMutation({
    mutationFn: submitMyLeaveRequest,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: MY_LEAVE_REQUESTS_QUERY_KEY });
      onSubmitted();
    },
    onError: (cause) =>
      setError(resolveLocalizedErrorMessage(cause, { t, language, fallbackKey: "attendanceLeave:messages.submitFailed" })),
  });

  // 提交前提：选了日子、至少一张医嘱已上传、没有正在上传或失败的照片。
  const blocker = selected.length === 0 || paidHours <= 0
    ? t("sick.needDay")
    : certificate.isUploading
      ? t("sick.uploading")
      : certificate.hasFailed
        ? t("sick.photoFailed")
        : certificate.uploadedUrls.length === 0
          ? t("sick.needPhoto")
          : "";

  const submit = () => {
    const selectable = candidates.filter((candidate) => candidate.state === "selectable");
    const guids = selected.filter((guid) => selectable.some((candidate) => candidate.scheduleGuid === guid));
    const range = sickLeaveDateRange(selectable, guids);
    if (!range || blocker) return;
    setError("");
    mutation.mutate({
      storeCode,
      leaveType: "PaidSick",
      startDate: range.startDate,
      endDate: range.endDate,
      scheduleGuids: guids,
      attachmentUrls: certificate.uploadedUrls,
      reason: reason.trim() || undefined,
    });
  };

  return (
    <LeaveScreenFrame
      title={t("sick.title")}
      onBack={onBack}
      footer={(
        <>
          {error ? <Text variant="bodySmall" style={styles.error}>{error}</Text> : null}
          {!error && blocker && (selected.length > 0 || certificate.photos.length > 0) ? (
            <Text variant="bodySmall" style={styles.hint}>{blocker}</Text>
          ) : null}
          <PrimaryBottomButton
            label={paidHours > 0 ? t("sick.submitWithHours", { hours: formatHours(paidHours) }) : t("sick.submit")}
            disabled={Boolean(blocker)}
            loading={mutation.isPending}
            onPress={submit}
          />
        </>
      )}
    >
      <LeaveCard>
        <LeaveCardHeader title={t("sick.daysTitle")} aside={t("sick.daysAside", { date: monthDay(earliest) })} />
        {candidates.length === 0 ? <Text variant="bodySmall" style={styles.muted}>{t("sick.empty")}</Text> : null}
        {candidates.map((candidate) => (
          <SickDayRow
            key={candidate.scheduleGuid}
            candidate={candidate}
            selected={selected.includes(candidate.scheduleGuid)}
            onToggle={() => toggle(candidate.scheduleGuid)}
          />
        ))}
        <Text variant="bodySmall" style={styles.muted}>
          {t("sick.footnote", { date: monthDay(addDays(earliest, -1)), days: t("units.days", { count: rules.sickLeaveSubmitDays }) })}
        </Text>
      </LeaveCard>

      <LeaveCard>
        <LeaveCardHeader title={t("sick.photosTitle")} aside={t("sick.photosAside", { max: rules.maxCertificatePhotos })} />
        <CertificatePhotoField
          photos={certificate.photos}
          max={rules.maxCertificatePhotos}
          disabled={mutation.isPending}
          onAdd={certificate.add}
          onRemove={certificate.remove}
          onRetry={certificate.retry}
        />
      </LeaveCard>

      <TextInput
        mode="outlined"
        value={reason}
        onChangeText={setReason}
        placeholder={t("sick.reasonPlaceholder")}
        maxLength={500}
        multiline
        outlineStyle={styles.reasonOutline}
        style={styles.reason}
        accessibilityLabel={t("sick.reasonPlaceholder")}
      />
    </LeaveScreenFrame>
  );
}

function SickDayRow({
  candidate,
  selected,
  onToggle,
}: {
  candidate: AttendanceSickLeaveCandidate & { state: SickCandidateState };
  selected: boolean;
  onToggle: () => void;
}) {
  const { t, language } = useAppTranslation("attendanceLeave");
  const { dayLabel } = useLeaveDateFormat();
  const worked = candidate.state === "worked";
  const label = t("sick.shiftLine", { day: dayLabel(candidate.workDate), start: candidate.startTime, end: candidate.endTime });
  return (
    <Pressable
      accessibilityRole="checkbox"
      accessibilityState={{ checked: selected, disabled: worked }}
      accessibilityLabel={worked ? `${label}${clauseSeparator(language)}${t("sick.worked")}` : label}
      disabled={worked}
      onPress={onToggle}
      style={[styles.dayRow, worked ? styles.dayRowDisabled : null, selected ? styles.dayRowSelected : null]}
    >
      <MaterialCommunityIcons
        name={selected ? "checkbox-marked-circle" : "checkbox-blank-circle-outline"}
        size={22}
        color={selected ? HB_COLORS.action : HB_COLORS.outline}
      />
      <Text
        variant="bodyMedium"
        style={[styles.dayText, worked ? styles.disabledText : null, selected ? styles.selectedText : null]}
        numberOfLines={1}
      >
        {label}
      </Text>
      <Text variant="bodySmall" style={worked ? styles.disabledText : styles.hours}>
        {worked ? t("sick.worked") : t("units.hours", { hours: formatHours(candidate.paidHours) })}
      </Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  loading: { paddingVertical: HB_SPACING.lg },
  muted: { color: HB_COLORS.textSecondary, lineHeight: 18 },
  hint: { color: HB_COLORS.textSecondary, textAlign: "center" },
  error: { color: HB_COLORS.danger, textAlign: "center" },
  dayRow: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    minHeight: 48,
    paddingHorizontal: HB_SPACING.sm,
  },
  dayRowDisabled: { backgroundColor: HB_COLORS.surfaceMuted, borderColor: HB_COLORS.outlineMuted, borderStyle: "dashed" },
  dayRowSelected: { backgroundColor: "#EFF8FF", borderColor: HB_COLORS.action, borderWidth: 2 },
  dayText: { color: HB_COLORS.textPrimary, flex: 1, fontVariant: ["tabular-nums"] },
  selectedText: { fontWeight: "700" },
  disabledText: { color: "#98A2B3" },
  hours: { color: HB_COLORS.action, fontVariant: ["tabular-nums"], fontWeight: "600" },
  reason: { backgroundColor: HB_COLORS.white, minHeight: 56 },
  reasonOutline: { borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface },
});

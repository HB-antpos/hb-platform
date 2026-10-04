import { useEffect, useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Card, Icon, Text } from "react-native-paper";
import type {
  AttendancePunch,
  AttendancePunchVerificationState,
  AttendanceToday,
} from "@/modules/attendance/types";
import { canOpenAttendanceQrScanner } from "@/modules/attendance/attendance-qr";
import {
  buildAttendanceTodayDisplay,
  resolveAttendancePunchExceptionMinutes,
} from "@/modules/attendance/attendance-today-normalization";
import { resolveAttendanceTodayStatus } from "@/modules/attendance/attendance-today-status";
import { resolveAttendancePunchDisplayTime } from "@/modules/attendance/attendance-device-time";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { type AttendanceStatusTone, StatusPill } from "./AdjustmentFormControls";

function formatTime(value?: string) {
  if (!value) {
    return "--:--";
  }
  const timePart = value.includes("T") ? value.split("T").pop() : value;
  return timePart?.slice(0, 5) || value;
}

function formatClockTime(value: Date) {
  return value.toLocaleTimeString([], {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  });
}

/** 分钟数显示为 h:mm，工时汇总比「xxx 分钟」更易扫读。 */
function formatDuration(minutes?: number) {
  const total = Math.max(0, Math.round(minutes ?? 0));
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, "0")}`;
}

const STATUS_TONES: Record<string, AttendanceStatusTone> = {
  readyToClockIn: "accent",
  readyToClockOut: "success",
  completed: "neutral",
  holiday: "warning",
  viewOnly: "neutral",
};

function punchTone(status?: string): AttendanceStatusTone {
  if (!status || status === "Normal") return "success";
  return status === "Late" || status === "EarlyLeave" ? "warning" : "danger";
}

export function TodayPunchCard({
  today,
  title,
  subtitle,
  selectedDate,
  storeName,
  allowPunch = true,
  isLoading,
  isVerificationRefreshing,
  isPunching,
  processingStage,
  hasAuthorizedStores,
  verification,
  lastQrPunch,
  trackingWarning,
  onScan,
}: {
  today?: AttendanceToday;
  title?: string;
  subtitle?: string;
  selectedDate?: string;
  storeName?: string;
  allowPunch?: boolean;
  isLoading: boolean;
  isVerificationRefreshing?: boolean;
  isPunching: boolean;
  processingStage?: "validating" | "locating" | "saving" | "tracking";
  hasAuthorizedStores: boolean;
  verification: AttendancePunchVerificationState;
  lastQrPunch?: AttendancePunch;
  trackingWarning?: string;
  onScan: () => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const [currentTime, setCurrentTime] = useState(() => new Date());

  useEffect(() => {
    const timer = setInterval(() => setCurrentTime(new Date()), 1000);
    return () => clearInterval(timer);
  }, []);

  const canScan = canOpenAttendanceQrScanner({
    isLoading,
    isPunching,
    isToday: allowPunch,
    hasAuthorizedStores,
  });
  const cardSubtitle =
    subtitle ?? selectedDate ?? today?.workDate ?? t("common:loading");
  const display = useMemo(() => buildAttendanceTodayDisplay(today), [today]);
  const status = resolveAttendanceTodayStatus(today, allowPunch);
  const allSessions = display.stores.flatMap((store) => store.sessions);
  const primarySession = allSessions.find((session) => session.scheduleState !== "NoSchedule");
  const workedTotal = allSessions.reduce((sum, session) => sum + (session.workedMinutes ?? 0), 0);

  // 主按钮文案跟随当前状态，员工不用先读状态再判断该打哪种卡。
  const scanLabel = isPunching
    ? t(processingStage ? `actions.punchStages.${processingStage}` : "actions.processingPunch")
    : status === "readyToClockIn"
      ? t("actions.scanClockIn")
      : status === "readyToClockOut"
        ? t("actions.scanClockOut")
        : t("actions.scanPunch");

  // 前台只展示网络校验；定位仅用于后台采集与打卡提交，不在今日卡显示（见二维码契约测试）。
  const networkStatus = verification.network.status;
  const networkTone: AttendanceStatusTone = networkStatus === "available" ? "success" : "warning";
  const networkLabel = `${t("today.info.network")} · ${
    isVerificationRefreshing ? t("today.verification.refreshing") : t(`today.verification.statuses.${networkStatus}`)
  }`;
  // 只在需要员工处理时给出说明；校验齐全时不再重复「将携带校验信息」之类的提示。
  const verificationIssue = !allowPunch || isVerificationRefreshing || networkStatus === "available"
    ? undefined
    : verification.network.reason === "networkUnreachable"
      ? t("today.info.networkUnavailable")
      : t("today.info.networkUnknown");
  const alertMessage = !allowPunch
    ? t("today.dailyRecords.alertSelectedDate")
    : today?.holidayName
      ? t("today.dailyRecords.alertHoliday", { holidayName: today.holidayName })
      : !today?.schedules.length && !isLoading
        ? t("today.dailyRecords.alertNoSchedule")
        : undefined;

  return (
    <Card mode="outlined" style={styles.card}>
      <Card.Content style={styles.content}>
        <View style={styles.hero}>
          <View style={styles.rowBetween}>
            <StatusPill label={t(`today.statusShort.${status}`)} tone={STATUS_TONES[status] ?? "neutral"} />
            <Text variant="labelMedium" style={styles.muted}>
              {title ?? t("sections.today")} · {cardSubtitle}
            </Text>
          </View>
          <Text variant="displaySmall" style={styles.clockText} accessibilityRole="timer">
            {formatClockTime(currentTime).slice(0, 5)}
            <Text variant="titleMedium" style={styles.clockSeconds}>
              {formatClockTime(currentTime).slice(5)}
            </Text>
          </Text>
          <Text variant="bodyMedium" style={styles.muted}>
            {primarySession
              ? t("today.hero.shift", {
                  start: formatTime(primarySession.startTime),
                  end: formatTime(primarySession.endTime),
                })
              : isLoading ? t("common:loading") : t("today.hero.noShift")}
            {status === "readyToClockOut" ? ` · ${t("today.hero.worked", { duration: formatDuration(workedTotal) })}` : ""}
          </Text>

          <Pressable
            accessibilityRole="button"
            accessibilityState={{
              disabled: !canScan,
            }}
            disabled={!canScan}
            style={({ pressed }) => [
              styles.punchButton,
              !canScan ? styles.punchButtonDisabled : null,
              pressed ? styles.punchButtonPressed : null,
            ]}
            onPress={onScan}
          >
            <Icon source="qrcode-scan" size={22} color={HB_COLORS.white} />
            <Text variant="titleMedium" style={styles.punchButtonText}>
              {scanLabel}
            </Text>
          </Pressable>

          <View style={styles.pillRow}>
            <StatusPill label={networkLabel} tone={networkTone} />
            {today?.storeTimeZone ? <StatusPill label={today.storeTimeZone} tone="neutral" /> : null}
          </View>
          {verificationIssue ? (
            <Text variant="bodySmall" style={styles.warningText}>{verificationIssue}</Text>
          ) : null}
          {alertMessage ? (
            <View style={styles.banner}>
              <Icon source="information-outline" size={16} color={HB_COLORS.warning} />
              <Text variant="bodySmall" style={styles.bannerText}>{alertMessage}</Text>
            </View>
          ) : null}
          {/* 未成年用工合规只提醒、不影响打卡；最多展示两条，完整内容由主管在提醒待办跟进。 */}
          {today?.minorCompliance?.hasFindings ? (
            <View accessibilityRole="alert" style={styles.banner}>
              <Icon source="shield-alert-outline" size={16} color={HB_COLORS.warning} />
              <View style={styles.flexText}>
                <Text variant="labelLarge" style={styles.bannerText}>
                  未成年用工提醒（{today.minorCompliance.findings.length}）
                </Text>
                {today.minorCompliance.findings.slice(0, 2).map((finding) => (
                  <Text key={`${finding.ruleId}-${finding.workDate ?? "current"}`} variant="bodySmall" style={styles.bannerText}>
                    {finding.message}
                  </Text>
                ))}
                <Text variant="bodySmall" style={styles.muted}>提醒不影响本次打卡；主管可在提醒待办继续跟进。</Text>
              </View>
            </View>
          ) : null}
          {lastQrPunch ? (
            <View style={styles.lastScan}>
              <Text variant="bodySmall" selectable style={styles.tabularText}>
                {t("today.lastScan", {
                  action: t(`punchTypes.${lastQrPunch.punchType}`, lastQrPunch.punchType),
                  time: formatTime(resolveAttendancePunchDisplayTime({
                    punchTimeUtc: lastQrPunch.punchTimeUtc || lastQrPunch.serverTimeUtc,
                  })),
                  status: t(`statuses.${lastQrPunch.status}`, lastQrPunch.status),
                })}
              </Text>
              <Text variant="bodySmall" selectable style={styles.muted}>
                {[
                  lastQrPunch.storeName || lastQrPunch.storeCode,
                  lastQrPunch.posDeviceCode
                    ? t("today.lastScanDevice", { device: lastQrPunch.posDeviceCode })
                    : undefined,
                ].filter(Boolean).join(" · ")}
              </Text>
              {trackingWarning ? (
                <Text variant="bodySmall" selectable style={styles.dangerText}>
                  {trackingWarning}
                </Text>
              ) : null}
            </View>
          ) : null}
        </View>

        {display.relatedStoreAlerts.map((alert) => (
          <View key={`${alert.missingStoreCode}-${alert.activeStoreCode}`} style={styles.banner}>
            <Icon source="store-alert-outline" size={16} color={HB_COLORS.warning} />
            <Text variant="bodySmall" style={styles.bannerText}>
              {t("today.timeline.relatedStoreConflict", {
                missingStore: alert.missingStoreName || alert.missingStoreCode,
                activeStore: alert.activeStoreName || alert.activeStoreCode,
              })}
            </Text>
          </View>
        ))}
        {display.relatedStoreReminders.map((reminder) => (
          <View key={reminder} style={styles.banner}>
            <Icon source="store-alert-outline" size={16} color={HB_COLORS.warning} />
            <Text variant="bodySmall" style={styles.bannerText}>{reminder}</Text>
          </View>
        ))}

        {display.stores.map((store) => (
          <View key={store.storeCode || store.storeName} style={styles.storeGroup}>
            <View style={styles.rowBetween}>
              <Text variant="titleSmall">
                {store.storeName || store.storeCode || storeName || t("common:na")}
              </Text>
              <Text variant="labelSmall" style={styles.muted}>
                {t("today.timeline.shiftCount", { count: store.sessions.length })}
              </Text>
            </View>
            {store.relatedReminder && !display.relatedStoreReminders.includes(store.relatedReminder) ? (
              <Text variant="bodySmall" style={styles.warningText}>{store.relatedReminder}</Text>
            ) : null}
            {store.sessions.map((session) => (
              <View
                key={session.scheduleGuid || `${store.storeCode}-${session.startTime}`}
                style={styles.session}
              >
                <View style={styles.rowBetween}>
                  <Text variant="labelLarge" style={styles.tabularText}>
                    {session.scheduleState === "NoSchedule"
                      ? t("today.timeline.unscheduledPunches")
                      : `${formatTime(session.startTime)} – ${formatTime(session.endTime)}`}
                  </Text>
                  <Text variant="labelSmall" style={styles.muted}>
                    {t("today.timeline.segmentProgress", {
                      completed: session.completedSegmentCount ?? session.segments.filter((item) => item.clockOut).length,
                      limit: session.segmentLimit ?? session.segments.length,
                    })}
                  </Text>
                </View>
                {session.hasMissingClockOut ? (
                  <Text variant="bodySmall" style={styles.dangerText}>
                    {t("today.timeline.missingClockOut")}
                  </Text>
                ) : null}

                <View style={styles.timeline}>
                  {session.segments.map((segment) => {
                    const clockInMinutes = segment.clockIn
                      ? resolveAttendancePunchExceptionMinutes(segment.clockIn)
                      : undefined;
                    const clockOutMinutes = segment.clockOut
                      ? resolveAttendancePunchExceptionMinutes(segment.clockOut)
                      : undefined;
                    const clockInStatus = segment.clockIn
                      ? t(`statuses.${segment.clockIn.status}`, segment.clockIn.status)
                      : "";
                    const clockOutStatus = segment.clockOut
                      ? t(`statuses.${segment.clockOut.status}`, segment.clockOut.status)
                      : "";
                    return (
                      <View key={segment.segmentIndex} style={styles.segment}>
                        <View style={styles.timelineRow}>
                          <Text variant="bodyMedium" style={styles.timelineTime}>
                            {formatTime(resolveAttendancePunchDisplayTime(segment.clockIn))}
                          </Text>
                          <View style={[styles.dot, segment.clockIn ? styles.dotFilled : styles.dotEmpty]} />
                          <Text variant="bodyMedium" style={styles.flexText}>{t("punchTypes.ClockIn")}</Text>
                          {segment.showClockInException && segment.clockIn ? (
                            <StatusPill
                              tone={punchTone(segment.clockIn.status)}
                              label={clockInMinutes !== undefined
                                ? t("today.timeline.statusWithMinutes", { status: clockInStatus, minutes: clockInMinutes })
                                : clockInStatus}
                            />
                          ) : null}
                        </View>
                        <View style={styles.timelineRow}>
                          <Text
                            variant="bodyMedium"
                            style={[styles.timelineTime, !segment.clockOut ? styles.mutedTime : null]}
                          >
                            {formatTime(resolveAttendancePunchDisplayTime(segment.clockOut))}
                          </Text>
                          <View style={[styles.dot, segment.clockOut ? styles.dotFilled : styles.dotEmpty]} />
                          <Text variant="bodyMedium" style={[styles.flexText, !segment.clockOut ? styles.muted : null]}>
                            {t("punchTypes.ClockOut")}
                          </Text>
                          {segment.showClockOutException && segment.clockOut ? (
                            <StatusPill
                              tone={punchTone(segment.clockOut.status)}
                              label={clockOutMinutes !== undefined
                                ? t("today.timeline.statusWithMinutes", { status: clockOutStatus, minutes: clockOutMinutes })
                                : clockOutStatus}
                            />
                          ) : null}
                        </View>
                        {segment.isBreakAfter ? (
                          <View style={styles.breakRow}>
                            <Icon source="coffee-outline" size={14} color={HB_COLORS.textSecondary} />
                            <Text variant="labelSmall" style={styles.muted}>{t("today.timeline.onBreak")}</Text>
                          </View>
                        ) : null}
                      </View>
                    );
                  })}
                </View>

                <View style={styles.summary}>
                  <View style={styles.summaryTile}>
                    <Text variant="titleMedium" style={styles.tabularText}>{formatDuration(session.workedMinutes)}</Text>
                    <Text variant="labelSmall" style={styles.muted}>{t("today.summary.worked")}</Text>
                  </View>
                  <View style={styles.summaryTile}>
                    <Text variant="titleMedium" style={styles.tabularText}>{formatDuration(session.breakMinutes)}</Text>
                    <Text variant="labelSmall" style={styles.muted}>{t("today.summary.break")}</Text>
                  </View>
                  <View style={styles.summaryTile}>
                    <Text variant="titleMedium" style={styles.tabularText}>
                      {formatDuration(session.overtime.approvedMinutes || session.overtime.candidateMinutes)}
                    </Text>
                    <Text variant="labelSmall" style={styles.muted}>{t("today.summary.overtime")}</Text>
                  </View>
                </View>
                {(session.overtime.rawMinutes > 0 ||
                  session.overtime.candidateMinutes > 0 ||
                  session.overtime.approvedMinutes > 0) ? (
                  <Text variant="bodySmall" style={styles.muted}>
                    {t("today.timeline.overtimeSummary", {
                      raw: session.overtime.rawMinutes,
                      candidate: session.overtime.candidateMinutes,
                      approved: session.overtime.approvedMinutes,
                    })}
                  </Text>
                ) : null}
              </View>
            ))}
          </View>
        ))}
      </Card.Content>
    </Card>
  );
}

const styles = StyleSheet.create({
  banner: {
    alignItems: "flex-start",
    alignSelf: "stretch",
    backgroundColor: "#FFFAEB",
    borderRadius: HB_RADIUS.control,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.xs,
  },
  bannerText: { color: HB_COLORS.warning, flex: 1 },
  breakRow: { alignItems: "center", flexDirection: "row", gap: 4, paddingLeft: 64 },
  card: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    elevation: 0,
  },
  clockSeconds: { color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  clockText: { color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"], fontWeight: "700" },
  content: { gap: HB_SPACING.md },
  dangerText: { color: HB_COLORS.danger },
  dot: { borderRadius: 5, height: 10, width: 10 },
  dotEmpty: { borderColor: HB_COLORS.outline, borderStyle: "dashed", borderWidth: 1.5 },
  dotFilled: { backgroundColor: HB_COLORS.success },
  flexText: { flex: 1 },
  hero: { gap: HB_SPACING.xs },
  lastScan: {
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: HB_RADIUS.control,
    gap: 2,
    padding: HB_SPACING.xs,
  },
  muted: { color: HB_COLORS.textSecondary },
  mutedTime: { color: HB_COLORS.outline },
  pillRow: { flexDirection: "row", flexWrap: "wrap", gap: 6 },
  punchButton: {
    alignItems: "center",
    alignSelf: "stretch",
    backgroundColor: HB_COLORS.action,
    borderRadius: HB_RADIUS.control,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    justifyContent: "center",
    marginTop: HB_SPACING.xs,
    minHeight: 52,
    paddingHorizontal: HB_SPACING.md,
  },
  punchButtonDisabled: { backgroundColor: "#98A2B3" },
  punchButtonPressed: { opacity: 0.85 },
  punchButtonText: { color: HB_COLORS.white, fontWeight: "700" },
  rowBetween: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs, justifyContent: "space-between" },
  segment: { gap: 2 },
  session: {
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.control,
    borderWidth: StyleSheet.hairlineWidth,
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
  },
  storeGroup: { gap: HB_SPACING.xs },
  summary: { flexDirection: "row", gap: 6 },
  summaryTile: {
    alignItems: "center",
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: HB_RADIUS.control,
    flex: 1,
    paddingVertical: 6,
  },
  tabularText: { fontVariant: ["tabular-nums"] },
  timeline: { gap: 4 },
  timelineRow: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs, minHeight: 28 },
  timelineTime: { fontVariant: ["tabular-nums"], width: 48 },
  warningText: { color: HB_COLORS.warning },
});

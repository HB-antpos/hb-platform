import { useEffect, useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Icon, Text } from "react-native-paper";
import type {
  AttendancePunch,
  AttendancePunchVerificationState,
  AttendanceToday,
} from "@/modules/attendance/types";
import { canOpenAttendanceQrScanner } from "@/modules/attendance/attendance-qr";
import {
  buildAttendanceTodayDisplay,
  resolveAttendancePunchExceptionMinutes,
  splitExceptionDuration,
} from "@/modules/attendance/attendance-today-normalization";
import { resolveAttendanceTodayStatus } from "@/modules/attendance/attendance-today-status";
import {
  resolveAttendancePunchDisplayTime,
  toAttendanceDeviceLocalTime,
} from "@/modules/attendance/attendance-device-time";
import {
  findActiveMealSession,
  resolveAttendanceMealPanelState,
} from "@/modules/attendance/attendance-meal-break";
import { MEAL_BREAK_MINUTES } from "@/modules/attendance/attendance-my-week";
import {
  lastClockIn,
  liveWorkedMinutes,
  minutesToShiftStart,
  shiftProgress,
  shiftWindow,
} from "@/modules/attendance/attendance-punch-hero";
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

/** 计薪小时：最多 2 位小数，去掉末尾 0（7.5、8、7.25）。 */
function formatHoursValue(minutes: number) {
  return String(Number((Math.max(0, minutes) / 60).toFixed(2)));
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
  isMealBreakBusy,
  onStartMealBreak,
  onEndMealBreak,
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
  isMealBreakBusy?: boolean;
  onStartMealBreak?: (storeCode: string) => void;
  onEndMealBreak?: (storeCode: string) => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  // 迟到 / 早退等异常时长：不足 60 分钟显示分钟，满 60 分钟起显示「X 小时 Y 分钟」（整小时不带分钟）。
  const formatExceptionDuration = (totalMinutes: number) => {
    const { hours, minutes } = splitExceptionDuration(totalMinutes);
    if (hours === 0) return t("today.timeline.duration.minutes", { minutes });
    if (minutes === 0) return t("today.timeline.duration.hours", { hours });
    return t("today.timeline.duration.hoursMinutes", { hours, minutes });
  };
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
  const networkLabel = `${t("today.info.network")} · ${
    isVerificationRefreshing ? t("today.verification.refreshing") : t(`today.verification.statuses.${networkStatus}`)
  }`;
  // 只在需要员工处理时给出说明；校验齐全时不再重复「将携带校验信息」之类的提示。
  const verificationIssue = !allowPunch || isVerificationRefreshing || networkStatus === "available"
    ? undefined
    : verification.network.reason === "networkUnreachable"
      ? t("today.info.networkUnavailable")
      : t("today.info.networkUnknown");
  // 用餐区只在当天、正在上班（或休息中）且排班有用餐要求时出现；旧后端没有 meal 字段时整块不显示。
  const mealSession = allowPunch ? findActiveMealSession(today?.scheduleSessions) : undefined;
  const mealPanel = mealSession
    ? resolveAttendanceMealPanelState(mealSession.meal, currentTime.getTime())
    : undefined;
  const mealTotal = mealSession?.meal.effectiveMealBreakCount ?? 0;
  const mealHandled = Math.min(mealSession?.meal.handledCount ?? 0, mealTotal);
  const alertMessage = !allowPunch
    ? t("today.dailyRecords.alertSelectedDate")
    : today?.holidayName
      ? t("today.dailyRecords.alertHoliday", { holidayName: today.holidayName })
      : !today?.schedules.length && !isLoading
        ? t("today.dailyRecords.alertNoSchedule")
        : undefined;

  // ── 主卡片按状态换样式：上班前蓝色（大圆按钮扫码上班）、工作中绿色（已工作时长 + 班次进度 + 扫码下班 / 用餐）、
  // 其余（已完成 / 假期 / 历史日期）白色汇总。扫码可不可点仍只由 canOpenAttendanceQrScanner 决定。
  const nowMs = currentTime.getTime();
  const allSegments = allSessions.flatMap((session) => session.segments);
  const liveWorked = liveWorkedMinutes(allSegments, nowMs);
  const window = primarySession
    ? shiftWindow(today?.workDate ?? selectedDate ?? "", primarySession.startTime, primarySession.endTime)
    : undefined;
  const latestClockIn = lastClockIn(allSegments);
  const firstClockInText = formatTime(resolveAttendancePunchDisplayTime(allSegments.find((segment) => segment.clockIn)?.clockIn));
  const lastRealClockOut = [...allSegments].reverse().find((segment) => segment.clockOut && !segment.clockOut.isSystemAutoClockOut)?.clockOut;
  const paidTotal = allSessions.reduce((sum, session) => sum + (session.paidMinutes ?? session.workedMinutes ?? 0), 0);
  const breakTotal = allSessions.reduce((sum, session) => sum + (session.breakMinutes ?? 0), 0);
  const overtimeTotal = allSessions.reduce((sum, session) => sum + (session.overtime.approvedMinutes || session.overtime.candidateMinutes), 0);
  const mealDeductionTotal = allSessions.reduce((sum, session) => sum + (session.mealDeductionMinutes ?? 0), 0);
  const isOnBreak = mealPanel?.kind === "onBreak";
  const heroVariant = status === "readyToClockOut" || isOnBreak ? "working" : status === "readyToClockIn" && allowPunch ? "before" : "summary";
  const canStartMeal = Boolean(mealSession && mealPanel && mealPanel.kind !== "done" && !isOnBreak && mealSession.hasOpenSegment && onStartMealBreak);
  const clockText = formatClockTime(currentTime);

  // 上班前副行：班次 + 离上班还有多久 / 已过上班时间多久；已经打过卡（中间休息）时写已工作。
  const beforeLine = (() => {
    if (!primarySession) return isLoading ? t("common:loading") : t("today.hero.noShift");
    const shift = t("today.hero.shift", { start: formatTime(primarySession.startTime), end: formatTime(primarySession.endTime) });
    if (liveWorked > 0) return `${shift} · ${t("today.hero.worked", { duration: formatDuration(liveWorked) })}`;
    if (!window) return shift;
    const minutes = minutesToShiftStart(window, nowMs);
    if (minutes > 0) return `${shift} · ${t("today.hero.startsIn", { duration: formatExceptionDuration(minutes) })}`;
    if (minutes < 0) return `${shift} · ${t("today.hero.startedAgo", { duration: formatExceptionDuration(-minutes) })}`;
    return shift;
  })();

  const networkChips = (
    <View style={styles.heroChips}>
      <View style={[styles.heroChip, networkStatus !== "available" ? styles.heroChipWarn : null]}>
        <Text style={styles.heroChipText}>{`${networkStatus === "available" ? "✓ " : ""}${networkLabel}`}</Text>
      </View>
      {today?.storeTimeZone ? (
        <View style={styles.heroChip}><Text style={styles.heroChipText}>{today.storeTimeZone}</Text></View>
      ) : null}
    </View>
  );

  const renderHero = () => {
    if (heroVariant === "before") {
      return (
        <View style={[styles.hero, styles.heroBefore]}>
          <View style={styles.rowBetween}>
            <View style={styles.heroPill}><Text style={styles.heroPillText}>{t(`today.statusShort.${status}`)}</Text></View>
            <Text style={styles.heroMeta} numberOfLines={1}>{title ?? t("sections.today")} · {cardSubtitle}</Text>
          </View>
          <Text style={styles.heroClock} accessibilityRole="timer">
            {clockText.slice(0, 5)}
            <Text style={styles.heroClockSeconds}>{clockText.slice(5)}</Text>
          </Text>
          <Text style={styles.heroLine}>{beforeLine}</Text>
          <Pressable
            accessibilityRole="button"
            accessibilityState={{ disabled: !canScan }}
            disabled={!canScan}
            onPress={onScan}
            style={({ pressed }) => [styles.bigButtonHalo, pressed ? styles.punchButtonPressed : null]}
          >
            <View style={[styles.bigButton, !canScan ? styles.bigButtonDisabled : null]}>
              <Icon source="qrcode-scan" size={40} color={canScan ? HB_COLORS.action : "#98A2B3"} />
              <Text style={[styles.bigButtonText, !canScan ? styles.bigButtonTextDisabled : null]}>{scanLabel}</Text>
              <Text style={styles.bigButtonHint}>{t("today.hero.scanHint")}</Text>
            </View>
          </Pressable>
          {networkChips}
        </View>
      );
    }
    if (heroVariant === "working") {
      const progress = window ? shiftProgress(window, nowMs) : undefined;
      return (
        <View style={[styles.hero, styles.heroWorking]}>
          <View style={styles.rowBetween}>
            <View style={styles.heroPill}>
              <Text style={styles.heroPillText}>{isOnBreak ? t("today.meal.onBreak") : t(`today.statusShort.${status}`)}</Text>
            </View>
            <Text style={styles.heroMeta} numberOfLines={1}>
              {latestClockIn
                ? [
                    t("today.hero.clockedInAt", { time: formatTime(resolveAttendancePunchDisplayTime(latestClockIn)) }),
                    latestClockIn.posDeviceCode,
                  ].filter(Boolean).join(" · ")
                : `${title ?? t("sections.today")} · ${cardSubtitle}`}
            </Text>
          </View>
          <Text style={styles.heroClock} accessibilityRole="timer">
            {formatDuration(liveWorked)}
            <Text style={styles.heroClockSuffix}>{` ${t("today.hero.workedSuffix")}`}</Text>
          </Text>
          {progress && window ? (
            <View style={styles.progress}>
              <View style={styles.progressTrack}>
                <View style={[styles.progressFill, { width: `${Math.round(progress.ratio * 100)}%` }]} />
              </View>
              <View style={styles.rowBetween}>
                <Text style={styles.progressText}>{formatTime(primarySession?.startTime)}</Text>
                <Text style={styles.progressText}>
                  {progress.remainingMinutes > 0
                    ? t("today.hero.remaining", { duration: formatExceptionDuration(progress.remainingMinutes) })
                    : t("today.hero.shiftEnded")}
                </Text>
                <Text style={styles.progressText}>{formatTime(primarySession?.endTime)}</Text>
              </View>
            </View>
          ) : null}
          <View style={styles.heroActions}>
            <Pressable
              accessibilityRole="button"
              accessibilityState={{ disabled: !canScan }}
              disabled={!canScan}
              onPress={onScan}
              style={({ pressed }) => [styles.heroPrimary, !canScan ? styles.heroPrimaryDisabled : null, pressed ? styles.punchButtonPressed : null]}
            >
              <Icon source="qrcode-scan" size={20} color={canScan ? HB_COLORS.success : "#98A2B3"} />
              <Text style={[styles.heroPrimaryText, !canScan ? styles.bigButtonTextDisabled : null]}>{scanLabel}</Text>
            </Pressable>
            {mealSession && isOnBreak && onEndMealBreak ? (
              <Pressable
                accessibilityRole="button"
                disabled={isMealBreakBusy}
                onPress={() => onEndMealBreak(mealSession.storeCode)}
                style={({ pressed }) => [styles.heroSecondary, pressed ? styles.punchButtonPressed : null]}
              >
                <Icon source="coffee-outline" size={18} color={HB_COLORS.white} />
                <Text style={styles.heroSecondaryText}>{t("today.meal.end")}</Text>
              </Pressable>
            ) : mealSession && canStartMeal && onStartMealBreak ? (
              <Pressable
                accessibilityRole="button"
                disabled={isMealBreakBusy || isPunching}
                onPress={() => onStartMealBreak(mealSession.storeCode)}
                style={({ pressed }) => [styles.heroSecondary, pressed ? styles.punchButtonPressed : null]}
              >
                <Icon source="coffee-outline" size={18} color={HB_COLORS.white} />
                <Text style={styles.heroSecondaryText}>{t("today.meal.start")}</Text>
              </Pressable>
            ) : null}
          </View>
          {networkStatus !== "available" ? networkChips : null}
        </View>
      );
    }
    const hasPunches = allSegments.some((segment) => segment.clockIn || segment.clockOut);
    return (
      <View style={[styles.hero, styles.heroSummary]}>
        <View style={styles.rowBetween}>
          <StatusPill label={t(`today.statusShort.${status}`)} tone={STATUS_TONES[status] ?? "neutral"} />
          <Text variant="labelMedium" style={styles.muted} numberOfLines={1}>{title ?? t("sections.today")} · {cardSubtitle}</Text>
        </View>
        {hasPunches ? (
          <>
            <View style={styles.summaryHead}>
              <View style={styles.checkCircle}><Icon source="check" size={26} color={HB_COLORS.success} /></View>
              <View style={styles.flexText}>
                <Text style={styles.summaryTitle}>{t("today.hero.paidTotal", { hours: formatHoursValue(paidTotal) })}</Text>
                <Text variant="bodySmall" style={styles.muted}>
                  {[
                    lastRealClockOut
                      ? t("today.hero.inOut", { in: firstClockInText, out: formatTime(resolveAttendancePunchDisplayTime(lastRealClockOut)) })
                      : t("today.hero.clockedInAt", { time: firstClockInText }),
                    mealDeductionTotal > 0 ? t("today.hero.mealDeducted", { duration: formatExceptionDuration(mealDeductionTotal) }) : undefined,
                  ].filter(Boolean).join(" · ")}
                </Text>
              </View>
            </View>
            <View style={styles.summary}>
              <View style={styles.summaryTile}>
                <Text variant="labelSmall" style={styles.muted}>{t("today.summary.worked")}</Text>
                <Text variant="titleMedium" style={styles.tabularText}>{formatDuration(liveWorked)}</Text>
              </View>
              <View style={styles.summaryTile}>
                <Text variant="labelSmall" style={styles.muted}>{t("today.summary.break")}</Text>
                <Text variant="titleMedium" style={styles.tabularText}>{formatDuration(breakTotal)}</Text>
              </View>
              <View style={styles.summaryTile}>
                <Text variant="labelSmall" style={styles.muted}>{t("today.summary.overtime")}</Text>
                <Text variant="titleMedium" style={[styles.tabularText, overtimeTotal ? null : styles.mutedTime]}>{formatDuration(overtimeTotal)}</Text>
              </View>
            </View>
          </>
        ) : (
          <Text variant="bodyMedium" style={styles.muted}>{beforeLine}</Text>
        )}
        {/* 已完成 / 假期当天仍可能要再打一段：保留一个次要的扫码入口 */}
        {allowPunch ? (
          <Button mode="outlined" icon="qrcode-scan" disabled={!canScan} onPress={onScan} style={styles.summaryScan}>
            {scanLabel}
          </Button>
        ) : null}
      </View>
    );
  };

  return (
    <View style={styles.root}>
      {renderHero()}
          {/* 用餐区：休息中显示计时；满 4 小时显示提醒横幅；未到时只给一行提示。不扫码，一键开始/结束。 */}
          {mealSession && mealPanel ? (
            <View
              accessibilityRole={mealPanel.kind === "due" ? "alert" : undefined}
              style={[
                styles.mealPanel,
                mealPanel.kind === "due" ? styles.mealPanelDue : null,
                mealPanel.kind === "onBreak" ? styles.mealPanelOnBreak : null,
              ]}
            >
              <Icon
                source={mealPanel.kind === "onBreak"
                  ? "coffee-outline"
                  : mealPanel.kind === "done" ? "check-circle-outline" : "food-outline"}
                size={18}
                color={mealPanel.kind === "due"
                  ? HB_COLORS.warning
                  : mealPanel.kind === "done" ? HB_COLORS.success : HB_COLORS.textSecondary}
              />
              <View style={styles.flexText}>
                {mealPanel.kind === "onBreak" ? (
                  <>
                    <Text variant="labelLarge" style={styles.tabularText}>
                      {t("today.meal.onBreak")}
                    </Text>
                    {mealSession.meal.openBreakStartedAtUtc ? (
                      <Text variant="bodySmall" style={styles.muted}>
                        {t("today.meal.onBreakSince", {
                          time: formatTime(toAttendanceDeviceLocalTime(mealSession.meal.openBreakStartedAtUtc)),
                        })}
                      </Text>
                    ) : null}
                  </>
                ) : mealPanel.kind === "due" ? (
                  <>
                    <Text variant="labelLarge" style={styles.bannerText}>{t("today.meal.dueTitle")}</Text>
                    <Text variant="bodySmall" style={styles.bannerText}>
                      {t("today.meal.dueBody", { minutes: MEAL_BREAK_MINUTES })}
                    </Text>
                  </>
                ) : mealPanel.kind === "upcoming" ? (
                  <Text variant="bodySmall" style={styles.muted}>
                    {mealPanel.dueAtMs !== undefined
                      ? t("today.meal.upcomingAt", {
                          handled: mealHandled,
                          total: mealTotal,
                          time: formatTime(toAttendanceDeviceLocalTime(mealSession.meal.nextReminderAtUtc)),
                        })
                      : t("today.meal.upcoming", { handled: mealHandled, total: mealTotal })}
                  </Text>
                ) : (
                  <Text variant="bodySmall" style={styles.muted}>
                    {t("today.meal.done", { handled: mealHandled, total: mealTotal })}
                  </Text>
                )}
              </View>
              {/* 开始 / 结束用餐的常用入口在主卡片里；这里只在「该吃饭了」时再给一个醒目的按钮。 */}
              {mealPanel.kind === "due" && mealSession.hasOpenSegment && onStartMealBreak ? (
                <Button
                  compact
                  mode="contained"
                  buttonColor={HB_COLORS.warning}
                  icon="coffee-outline"
                  disabled={isMealBreakBusy || isPunching}
                  loading={isMealBreakBusy}
                  onPress={() => onStartMealBreak(mealSession.storeCode)}
                >
                  {t("today.meal.start")}
                </Button>
              ) : null}
            </View>
          ) : null}

          {verificationIssue ? (
            <Text variant="bodySmall" style={styles.warningText}>{verificationIssue}</Text>
          ) : null}
          {alertMessage ? (
            <View style={styles.banner}>
              <Icon source="information-outline" size={16} color={HB_COLORS.warning} />
              <Text variant="bodySmall" style={styles.bannerText}>{alertMessage}</Text>
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

      <View style={styles.timelineCard}>
        <Text variant="titleSmall" style={styles.timelineTitle}>{t("today.hero.timelineTitle")}</Text>
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
                                ? t("today.timeline.statusWithDuration", { status: clockInStatus, duration: formatExceptionDuration(clockInMinutes) })
                                : clockInStatus}
                            />
                          ) : null}
                        </View>
                        {segment.clockIn?.posDeviceCode ? (
                          <Text variant="labelSmall" style={styles.deviceText}>{t("today.hero.viaDevice", { device: segment.clockIn.posDeviceCode })}</Text>
                        ) : null}
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
                                ? t("today.timeline.statusWithDuration", { status: clockOutStatus, duration: formatExceptionDuration(clockOutMinutes) })
                                : clockOutStatus}
                            />
                          ) : null}
                        </View>
                        {segment.clockOut?.posDeviceCode ? (
                          <Text variant="labelSmall" style={styles.deviceText}>{t("today.hero.viaDevice", { device: segment.clockOut.posDeviceCode })}</Text>
                        ) : null}
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

                {/* 只有一个班次时，工时 / 休息 / 加班已在主卡片里实时显示，这里不再重复（后端汇总值不随时间走，会和主卡片矛盾）。 */}
                {allSessions.length > 1 ? (
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
                ) : null}
                {/* 计薪工时说明：排班有用餐扣除或有加回申请时才显示，员工能看到扣了多少、待审多少。 */}
                {(session.mealDeductionMinutes ?? 0) > 0
                  || (session.pendingMealAddBackMinutes ?? 0) > 0
                  || (session.approvedMealAddBackMinutes ?? 0) > 0 ? (
                  <Text variant="bodySmall" style={[styles.muted, styles.tabularText]}>
                    {[
                      t("today.meal.deduction", { duration: formatDuration(session.mealDeductionMinutes) }),
                      (session.pendingMealAddBackMinutes ?? 0) > 0
                        ? t("today.meal.pendingAddBack", { duration: formatDuration(session.pendingMealAddBackMinutes) })
                        : undefined,
                      (session.approvedMealAddBackMinutes ?? 0) > 0
                        ? t("today.meal.approvedAddBack", { duration: formatDuration(session.approvedMealAddBackMinutes) })
                        : undefined,
                      session.paidMinutes !== undefined
                        ? t("today.meal.paid", { duration: formatDuration(session.paidMinutes) })
                        : undefined,
                    ].filter(Boolean).join(" · ")}
                  </Text>
                ) : null}
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
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { gap: HB_SPACING.sm },
  // 主卡片：上班前蓝、工作中绿、其余白色汇总
  hero: { borderRadius: 20, gap: 6, overflow: "hidden", padding: HB_SPACING.md },
  heroBefore: { backgroundColor: "#0958D9" },
  heroWorking: { backgroundColor: "#067647" },
  heroSummary: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderWidth: StyleSheet.hairlineWidth,
    gap: HB_SPACING.sm,
  },
  heroPill: { backgroundColor: "rgba(255,255,255,0.18)", borderRadius: 10, paddingHorizontal: 9, paddingVertical: 3 },
  heroPillText: { color: HB_COLORS.white, fontSize: 12, fontWeight: "700" },
  heroMeta: { color: "rgba(255,255,255,0.88)", flexShrink: 1, fontSize: 12.5 },
  heroClock: { color: HB_COLORS.white, fontSize: 46, fontVariant: ["tabular-nums"], fontWeight: "800", letterSpacing: -1, marginTop: 4 },
  heroClockSeconds: { color: "rgba(255,255,255,0.72)", fontSize: 20, fontWeight: "600" },
  heroClockSuffix: { color: "rgba(255,255,255,0.85)", fontSize: 20, fontWeight: "700" },
  heroLine: { color: HB_COLORS.white, fontSize: 14 },
  bigButtonHalo: {
    alignSelf: "center",
    backgroundColor: "rgba(255,255,255,0.12)",
    borderRadius: 104,
    marginTop: HB_SPACING.sm,
    padding: 18,
  },
  bigButton: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderRadius: 84,
    gap: 4,
    height: 168,
    justifyContent: "center",
    paddingHorizontal: 12,
    width: 168,
  },
  bigButtonDisabled: { backgroundColor: "#F2F4F7" },
  bigButtonText: { color: HB_COLORS.action, fontSize: 18, fontWeight: "800", textAlign: "center" },
  bigButtonTextDisabled: { color: "#98A2B3" },
  bigButtonHint: { color: HB_COLORS.textSecondary, fontSize: 11, textAlign: "center" },
  heroChips: { flexDirection: "row", flexWrap: "wrap", gap: 6, justifyContent: "center", marginTop: HB_SPACING.xs },
  heroChip: { backgroundColor: "rgba(255,255,255,0.16)", borderRadius: 9, paddingHorizontal: 8, paddingVertical: 3 },
  heroChipWarn: { backgroundColor: "#FEF0C7" },
  heroChipText: { color: HB_COLORS.white, fontSize: 11.5 },
  progress: { gap: 4, marginTop: HB_SPACING.xs },
  progressTrack: { backgroundColor: "rgba(255,255,255,0.22)", borderRadius: 4, height: 8, overflow: "hidden" },
  progressFill: { backgroundColor: HB_COLORS.white, borderRadius: 4, height: 8 },
  progressText: { color: "rgba(255,255,255,0.88)", fontSize: 11.5, fontVariant: ["tabular-nums"] },
  heroActions: { flexDirection: "row", gap: HB_SPACING.xs, marginTop: HB_SPACING.sm },
  heroPrimary: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderRadius: 24,
    flex: 1,
    flexDirection: "row",
    gap: 6,
    height: 48,
    justifyContent: "center",
  },
  heroPrimaryDisabled: { backgroundColor: "#F2F4F7" },
  heroPrimaryText: { color: HB_COLORS.success, fontSize: 16, fontWeight: "800" },
  heroSecondary: {
    alignItems: "center",
    backgroundColor: "rgba(255,255,255,0.18)",
    borderRadius: 24,
    flexDirection: "row",
    gap: 6,
    height: 48,
    justifyContent: "center",
    paddingHorizontal: 16,
  },
  heroSecondaryText: { color: HB_COLORS.white, fontSize: 15, fontWeight: "700" },
  summaryHead: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.sm },
  checkCircle: {
    alignItems: "center",
    backgroundColor: "#ECFDF3",
    borderRadius: 26,
    height: 52,
    justifyContent: "center",
    width: 52,
  },
  summaryTitle: { color: HB_COLORS.textPrimary, fontSize: 22, fontWeight: "800" },
  summaryScan: { alignSelf: "flex-start" },
  timelineCard: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface + 2,
    borderWidth: StyleSheet.hairlineWidth,
    gap: HB_SPACING.sm,
    padding: HB_SPACING.sm,
  },
  timelineTitle: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  deviceText: { color: HB_COLORS.textSecondary, paddingLeft: 72 },
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
  lastScan: {
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: HB_RADIUS.control,
    gap: 2,
    padding: HB_SPACING.xs,
  },
  // 用餐区默认是一行轻量提示（无底色），到期换成警示横幅色，休息中换成中性底色，保持主按钮是视觉焦点。
  mealPanel: {
    alignItems: "center",
    alignSelf: "stretch",
    borderRadius: HB_RADIUS.control,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    minHeight: 44,
    paddingHorizontal: HB_SPACING.xs,
    paddingVertical: 4,
  },
  mealPanelDue: { backgroundColor: "#FFFAEB" },
  mealPanelOnBreak: { backgroundColor: HB_COLORS.surfaceMuted },
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
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: HB_RADIUS.control,
    flex: 1,
    paddingHorizontal: 10,
    paddingVertical: 6,
  },
  tabularText: { fontVariant: ["tabular-nums"] },
  timeline: { gap: 4 },
  timelineRow: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs, minHeight: 28 },
  timelineTime: { fontVariant: ["tabular-nums"], width: 48 },
  warningText: { color: HB_COLORS.warning },
});

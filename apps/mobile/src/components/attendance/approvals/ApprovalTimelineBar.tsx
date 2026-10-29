import { StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import {
  formatClockMinutes,
  timelinePercent,
  type ApprovalTimeline,
} from "@/modules/attendance/approval-board";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { OVERTIME_TONE, PatternFill, TIMELINE_COLORS } from "./approval-ui";

interface Bar {
  key: string;
  start: number;
  end: number;
  color: string;
  pattern?: "diagonal" | "dashed";
}

/**
 * 当天打卡时间轴：上排灰色＝排班，下排蓝色＝实际打卡段。
 * - mode="overtime"：实际段超出排班窗口的部分用橙色斜纹，一眼看出加班从哪来。
 * - mode="missingClockOut"：最后一段从店长填的下班时间到系统自动下班之间用红色竖虚线（不计薪）。
 */
export function ApprovalTimelineBar({
  timeline,
  mode,
  autoClockOutAt,
  scheduleLabel,
  actualLabel,
}: {
  timeline: ApprovalTimeline;
  mode: "overtime" | "missingClockOut";
  /** 漏下班：系统自动下班的分钟（通常 1440 = 次日 00:00）。 */
  autoClockOutAt?: number;
  scheduleLabel?: string;
  actualLabel?: string;
}) {
  const { range, schedule } = timeline;
  const bars: Bar[] = [];
  for (const segment of timeline.segments) {
    const end = segment.end ?? autoClockOutAt ?? segment.start;
    if (mode === "overtime" && schedule) {
      // 把每段拆成「排班前 / 排班内 / 排班后」三截，前后两截是候选加班。
      const inStart = Math.max(segment.start, schedule.start);
      const inEnd = Math.min(end, schedule.end);
      if (segment.start < schedule.start) {
        bars.push({ key: `${segment.number}-early`, start: segment.start, end: Math.min(end, schedule.start), color: OVERTIME_TONE.fill, pattern: "diagonal" });
      }
      if (inEnd > inStart) {
        bars.push({ key: `${segment.number}-in`, start: inStart, end: inEnd, color: TIMELINE_COLORS.actual });
      }
      if (end > schedule.end) {
        bars.push({ key: `${segment.number}-late`, start: Math.max(segment.start, schedule.end), end, color: OVERTIME_TONE.fill, pattern: "diagonal" });
      }
    } else {
      bars.push({ key: `${segment.number}`, start: segment.start, end, color: TIMELINE_COLORS.actual });
    }
  }
  const last = timeline.segments[timeline.segments.length - 1];
  if (mode === "missingClockOut" && last && autoClockOutAt !== undefined) {
    const filledEnd = last.end ?? last.start;
    if (autoClockOutAt > filledEnd) {
      bars.push({ key: "auto", start: filledEnd, end: autoClockOutAt, color: TIMELINE_COLORS.auto, pattern: "dashed" });
    }
  }

  const renderBar = (bar: Bar) => {
    const left = timelinePercent(bar.start, range);
    const width = Math.max(0.8, timelinePercent(bar.end, range) - left);
    return (
      <View
        key={bar.key}
        style={[
          styles.bar,
          { left: `${left}%`, width: `${width}%` },
          bar.pattern ? { backgroundColor: `${bar.color}33` } : { backgroundColor: bar.color },
        ]}
      >
        {bar.pattern ? <PatternFill color={bar.color} variant={bar.pattern} /> : null}
      </View>
    );
  };

  return (
    <View style={styles.wrapper} accessible accessibilityRole="image">
      {scheduleLabel ? <Text variant="labelSmall" style={styles.rowLabel}>{scheduleLabel}</Text> : null}
      <View style={styles.track}>
        {schedule ? renderBar({ key: "schedule", start: schedule.start, end: schedule.end, color: TIMELINE_COLORS.schedule }) : null}
      </View>
      {actualLabel ? <Text variant="labelSmall" style={styles.rowLabel}>{actualLabel}</Text> : null}
      <View style={styles.track}>{bars.map(renderBar)}</View>
      <View style={styles.axis}>
        {timeline.ticks.map((tick) => (
          <Text
            key={tick}
            variant="labelSmall"
            style={[styles.tick, { left: `${timelinePercent(tick, range)}%` }]}
          >
            {formatClockMinutes(tick).slice(0, 2)}
          </Text>
        ))}
      </View>
    </View>
  );
}

const TRACK_HEIGHT = 14;

const styles = StyleSheet.create({
  axis: { height: 18, marginHorizontal: 10, marginTop: HB_SPACING.xxs },
  bar: { borderRadius: TRACK_HEIGHT / 2, height: TRACK_HEIGHT, overflow: "hidden", position: "absolute", top: 0 },
  rowLabel: { color: HB_COLORS.textSecondary, fontSize: 11 },
  tick: {
    color: HB_COLORS.textSecondary,
    fontVariant: ["tabular-nums"],
    marginLeft: -8,
    position: "absolute",
    textAlign: "center",
    width: 16,
  },
  track: {
    backgroundColor: TIMELINE_COLORS.track,
    borderRadius: TRACK_HEIGHT / 2,
    height: TRACK_HEIGHT,
    marginHorizontal: 10,
    marginVertical: 2,
  },
  wrapper: { gap: 2 },
});

// 打卡记录的一条时间轴：底轨 + 排班 / 实际 / 迟到缺卡 / 请假色块 + 今天的「现在」刻度。
import { StyleSheet, View, type StyleProp, type ViewStyle } from "react-native";
import { timelinePercent } from "@/modules/attendance/approval-board";
import { HB_COLORS } from "@/shared/theme/tokens";
import { PatternFill } from "../approvals/approval-ui";
import type { RowBar } from "./managed-record-display";

const BAR_COLORS: Record<RowBar["kind"], string> = {
  schedule: "#D1E9FF",
  actual: "#1570EF",
  open: "#1570EF",
  late: "#F04438",
  missing: "#F04438",
  leave: "#98A2B3",
};

/** 一条时间轴：底轨 + 各色块 + 今天的「现在」刻度。 */
export function RecordTimeline({ bars, axis, now, height = 10, style }: { bars: RowBar[]; axis: { start: number; end: number }; now?: number; height?: number; style?: StyleProp<ViewStyle> }) {
  return (
    <View style={[styles.track, { height, borderRadius: height / 2 }, style]} accessible={false}>
      {bars.map((bar) => {
        const left = timelinePercent(bar.start, axis);
        const width = Math.max(0.8, timelinePercent(bar.end, axis) - left);
        const color = BAR_COLORS[bar.kind];
        const patterned = bar.kind === "missing" || bar.kind === "open" || bar.kind === "leave";
        const inset = bar.kind === "schedule" || bar.kind === "leave" ? 0 : 2;
        return (
          <View
            key={bar.key}
            style={[
              styles.bar,
              { left: `${left}%`, width: `${width}%`, top: inset, bottom: inset, borderRadius: (height - inset * 2) / 2 },
              patterned ? { backgroundColor: bar.kind === "open" ? "#84CAFF" : `${color}22` } : { backgroundColor: color },
            ]}
          >
            {patterned ? <PatternFill color={bar.kind === "open" ? color : color} variant={bar.kind === "leave" ? "diagonal" : "dashed"} /> : null}
          </View>
        );
      })}
      {now !== undefined && now >= axis.start && now <= axis.end ? (
        <View style={[styles.nowTick, { left: `${timelinePercent(now, axis)}%` }]} />
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  track: { backgroundColor: HB_COLORS.surfaceMuted, overflow: "hidden", position: "relative" },
  bar: { overflow: "hidden", position: "absolute" },
  nowTick: { backgroundColor: "#F04438", bottom: 0, marginLeft: -1, position: "absolute", top: 0, width: 2 },
});

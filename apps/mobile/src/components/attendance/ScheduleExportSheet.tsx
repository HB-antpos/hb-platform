import { forwardRef, useMemo, useRef, useState } from "react";
import { Alert, Pressable, ScrollView, StyleSheet, View } from "react-native";
import * as FileSystem from "expo-file-system/legacy";
import * as Sharing from "expo-sharing";
import { Button, IconButton, Text } from "react-native-paper";
import Svg, { G, Line, Path, Rect, Text as SvgText } from "react-native-svg";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import {
  MEAL_ICON_PATH,
  MEAL_NONE_TONE,
  MEAL_SLASH_PATH,
  type MealMark,
} from "@/modules/attendance/meal-mark";
import {
  buildScheduleExportBoard,
  estimateTextWidth,
  fitTextToWidth,
  layoutScheduleExport,
  SCHEDULE_EXPORT_LAYOUT as L,
  scheduleExportFileName,
  type ScheduleExportBoard,
} from "@/modules/attendance/schedule-export";
import type { StaffColor } from "@/modules/attendance/schedule-board";
import type { AttendanceSchedule } from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

/** 截图用的隐藏画布最高 2000pt：iOS 按屏幕倍率出图，再高位图内存吃紧。 */
const MAX_CAPTURE_HEIGHT = 2000;
const MUTED_TEXT = "#98A2B3";
const WEEKEND_FILL = "#F9FAFB";
const GRID_LINE = HB_COLORS.outlineMuted;
/**
 * 预览按 0.8 倍显示、可左右滑动：整张图 7 列约 900 单位宽，塞进手机宽度里字会缩到看不清，
 * 所以不再强行缩到屏幕宽，而是保持可读的字号让用户滑动查看；分享出去的图仍是完整高清大图。
 */
const PREVIEW_SCALE = 0.8;

interface ScheduleImageLabels {
  title: string;
  subtitle: string;
  footerNote: string;
  generatedAt: string;
  legendMeal: string;
  legendNoMeal: string;
  weekday: (index: number) => string;
  dayOnDuty: (count: number) => string;
}

type CaptureSvg = Svg & {
  toDataURL: (callback: (base64?: string) => void, options?: object) => void;
};

function formatGeneratedAt(date: Date) {
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/** 午饭标识小块的宽度：内边距 + 图标，扣 2 次以上再加「×N」。 */
function mealChipWidth(mark: MealMark) {
  return 6 + 11 + (mark.count > 1 ? 13 : 0);
}

/**
 * 午饭标识（与看板卡片同款：自动＝浅灰叉勺，手动＝加虚线框，长班次无午饭＝橙色斜杠 + 橙色虚线框）。
 * right 是小块右边缘的 x，top 是上边缘的 y。
 */
function MealChip({ mark, right, top, tint }: { mark: MealMark; right: number; top: number; tint: string }) {
  if (mark.kind === "hidden") return null;
  const none = mark.kind === "none";
  const width = mealChipWidth(mark);
  const x = right - width;
  const color = none ? MEAL_NONE_TONE.text : tint;
  const dashed = none || mark.manual;
  return (
    <G>
      <Rect
        x={x}
        y={top}
        width={width}
        height={14}
        rx={3}
        fill={none ? MEAL_NONE_TONE.background : "#FFFFFF"}
        fillOpacity={none ? 1 : 0.72}
        stroke={none ? MEAL_NONE_TONE.border : dashed ? tint : "none"}
        strokeWidth={1}
        strokeDasharray={dashed ? "2.5 2" : undefined}
      />
      {/* 图标按 24×24 的图形坐标画，缩放到 11 单位 */}
      <G transform={`translate(${x + 3} ${top + 1.5}) scale(${11 / 24})`}>
        <Path d={MEAL_ICON_PATH} fill="none" stroke={color} strokeWidth={2.4} strokeLinecap="round" strokeLinejoin="round" opacity={none ? 1 : 0.75} />
        {none ? <Path d={MEAL_SLASH_PATH} fill="none" stroke={MEAL_NONE_TONE.text} strokeWidth={2.6} strokeLinecap="round" /> : null}
      </G>
      {mark.count > 1 ? (
        <SvgText x={x + 3 + 11 + 1} y={top + 10.5} fontSize={9.5} fontWeight="700" fill={color}>
          {`×${mark.count}`}
        </SvgText>
      ) : null}
    </G>
  );
}

/** 页脚图例用的示意标记（不带次数）。 */
const LEGEND_MEAL: MealMark = { kind: "meal", count: 1, manual: false, deductMinutes: 30, durationMinutes: 0 };
const LEGEND_NONE: MealMark = { kind: "none", count: 0, manual: true, deductMinutes: 0, durationMinutes: 0 };

/**
 * 排班图片本体（按天分列的看板）：固定 viewBox 坐标系，按传入宽度等比缩放。
 * 预览与截图共用同一份绘制，只是渲染尺寸不同。
 */
const ScheduleImage = forwardRef<Svg, {
  board: ScheduleExportBoard;
  labels: ScheduleImageLabels;
  width: number;
}>(function ScheduleImage({ board, labels, width }, ref) {
  const layout = useMemo(() => layoutScheduleExport(board), [board]);
  const height = (width * layout.height) / layout.width;
  const { boardLeft, boardTop, boardWidth, bodyTop, bodyHeight, boardBottom, cardWidth } = layout;
  const boardRight = boardLeft + boardWidth;

  // 页脚左侧：共 N 人 + 本周出现过的午饭图例（没出现过的不放，免得图例说明图里没有的东西）
  const legendItems: { mark: MealMark; label: string }[] = [];
  if (board.hasMeal) legendItems.push({ mark: LEGEND_MEAL, label: labels.legendMeal });
  if (board.hasNoMeal) legendItems.push({ mark: LEGEND_NONE, label: labels.legendNoMeal });
  let legendCursor = boardLeft + estimateTextWidth(labels.footerNote, 11) + 16;

  return (
    <Svg ref={ref} width={width} height={height} viewBox={`0 0 ${layout.width} ${layout.height}`}>
      <Rect x={0} y={0} width={layout.width} height={layout.height} fill={HB_COLORS.white} />

      <SvgText x={L.padding} y={L.padding + 22} fontSize={22} fontWeight="700" fill={HB_COLORS.textPrimary}>
        {fitTextToWidth(labels.title, boardWidth, 22)}
      </SvgText>
      <SvgText x={L.padding} y={L.padding + 46} fontSize={13} fill={HB_COLORS.textSecondary}>
        {labels.subtitle}
      </SvgText>

      {/* 周末两列整列浅灰底，一眼区分周末班。 */}
      {board.days.map((day, index) =>
        index >= 5 ? (
          <Rect key={`weekend-${day.date}`} x={layout.columnLefts[index]} y={bodyTop} width={L.columnWidth} height={bodyHeight} fill={WEEKEND_FILL} />
        ) : null,
      )}

      {/* 表头：星期 + 日期，下面一行当天在岗人数（0 人标红） */}
      <Rect x={boardLeft} y={boardTop} width={boardWidth} height={L.headerHeight} fill={HB_COLORS.surfaceMuted} />
      {board.days.map((day, index) => {
        const centerX = layout.columnLefts[index] + L.columnWidth / 2;
        return (
          <G key={`head-${day.date}`}>
            <SvgText x={centerX} y={boardTop + 19} fontSize={13} fontWeight="700" fill={HB_COLORS.textPrimary} textAnchor="middle">
              {`${labels.weekday(index)} ${day.date.slice(5, 10)}`}
            </SvgText>
            <SvgText x={centerX} y={boardTop + 35} fontSize={11} fill={day.headcount ? HB_COLORS.textSecondary : HB_COLORS.danger} textAnchor="middle">
              {labels.dayOnDuty(day.headcount)}
            </SvgText>
          </G>
        );
      })}

      {/* 班次卡片：浅底色 + 左侧色条 + 同色系边框（与屏幕看板同款），名字、时间一行、午饭标识在时间行右侧。 */}
      {board.days.map((day, dayIndex) =>
        day.cards.map((card, cardIndex) => {
          const x = layout.columnLefts[dayIndex] + L.columnPaddingX;
          const y = layout.cardTops[dayIndex][cardIndex];
          return (
            <G key={card.scheduleGuid}>
              <Rect x={x} y={y} width={cardWidth} height={L.cardHeight} rx={6} fill={card.color.bg} stroke={card.color.bar} strokeOpacity={0.35} strokeWidth={1} />
              <Rect x={x + 3} y={y + 5} width={3} height={L.cardHeight - 10} rx={1.5} fill={card.color.bar} />
              <SvgText x={x + 12} y={y + 16} fontSize={13} fontWeight="700" fill={card.color.ink}>
                {fitTextToWidth(card.name, cardWidth - 12 - 6, 13)}
              </SvgText>
              <SvgText x={x + 12} y={y + 31} fontSize={12} fill={card.color.ink}>
                {`${card.startTime}–${card.endTime}`}
              </SvgText>
              <MealChip mark={card.meal} right={x + cardWidth - 5} top={y + 20} tint={card.color.ink} />
            </G>
          );
        }),
      )}

      {/* 列分隔线、表头下沿与外框 */}
      {board.days.map((_, index) => (index > 0 ? (
        <Line key={`v-${index}`} x1={layout.columnLefts[index]} y1={boardTop} x2={layout.columnLefts[index]} y2={boardBottom} stroke={GRID_LINE} strokeWidth={1} />
      ) : null))}
      <Line x1={boardLeft} y1={bodyTop} x2={boardRight} y2={bodyTop} stroke={GRID_LINE} strokeWidth={1} />
      <Rect x={boardLeft} y={boardTop} width={boardWidth} height={boardBottom - boardTop} rx={2} fill="none" stroke={HB_COLORS.outline} strokeWidth={1} />

      <SvgText x={boardLeft} y={boardBottom + 21} fontSize={11} fill={MUTED_TEXT}>
        {labels.footerNote}
      </SvgText>
      {legendItems.map((item) => {
        const chipRight = legendCursor + mealChipWidth(item.mark);
        const labelX = chipRight + 4;
        const node = (
          <G key={item.label}>
            <MealChip mark={item.mark} right={chipRight} top={boardBottom + 10} tint={HB_COLORS.textSecondary} />
            <SvgText x={labelX} y={boardBottom + 21} fontSize={11} fill={MUTED_TEXT}>{item.label}</SvgText>
          </G>
        );
        legendCursor = labelX + estimateTextWidth(item.label, 11) + 14;
        return node;
      })}
      <SvgText x={boardRight} y={boardBottom + 21} fontSize={11} fill={MUTED_TEXT} textAnchor="end">
        {labels.generatedAt}
      </SvgText>
    </Svg>
  );
});

/**
 * 排班导出：预览 + 分享。
 * - presentation="sheet"：竖屏卡片里用底部弹层；
 * - presentation="overlay"：横屏全屏 Modal 内用居中浮层，不再叠原生 Modal（与编辑侧栏同理）。
 * 分享走 react-native-svg 自带的 toDataURL 出 PNG，不引入新原生依赖，可随 OTA 发布。
 */
export function ScheduleExportSheet({
  visible,
  presentation,
  days,
  schedules,
  nameOf,
  colorOf,
  storeCode,
  storeLabel,
  weekStartDate,
  weekLabel,
  onDismiss,
}: {
  visible: boolean;
  presentation: "sheet" | "overlay";
  days: string[];
  /** 本周全部班次；只有已发布的上班班次会进图（草稿、请假、已取消都不进）。 */
  schedules: readonly AttendanceSchedule[];
  /** 图上显示的名字：与看板当前的昵称 / 全名开关一致。 */
  nameOf: (userGuid: string) => string;
  /** 员工色：与屏幕看板同一套（按全名分配，切换昵称 / 全名不换色）。 */
  colorOf: (userGuid: string) => StaffColor | undefined;
  storeCode?: string;
  storeLabel: string;
  weekStartDate: string;
  /** 如「2026 年第 40 周 · 2026-09-28 - 2026-10-04」。 */
  weekLabel: string;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const captureRef = useRef<Svg>(null);
  const [isSharing, setIsSharing] = useState(false);

  const board = useMemo(
    () => buildScheduleExportBoard({ days, schedules, nameOf, colorOf }),
    [colorOf, days, nameOf, schedules],
  );
  const layout = useMemo(() => layoutScheduleExport(board), [board]);
  // 生成时间在打开时定格，预览与分享出去的图一致。
  const generatedAt = useMemo(() => (visible ? formatGeneratedAt(new Date()) : ""), [visible]);
  const labels = useMemo<ScheduleImageLabels>(() => ({
    title: t("scheduleManagement.export.imageTitle", { store: storeLabel }),
    subtitle: t("scheduleManagement.export.subtitleWeek", { week: weekLabel }),
    footerNote: t("scheduleManagement.export.footerNote", { count: board.peopleCount }),
    generatedAt: t("scheduleManagement.export.generatedAt", { time: generatedAt }),
    legendMeal: t("scheduleManagement.export.legendMeal"),
    legendNoMeal: t("scheduleManagement.export.legendNoMeal"),
    weekday: (index) => t(`weekdays.${index}`),
    dayOnDuty: (count) => t("scheduleManagement.export.dayOnDuty", { count }),
  }), [board.peopleCount, generatedAt, storeLabel, t, weekLabel]);

  // 隐藏画布按原始坐标 1:1 渲染（超高时等比缩小）：整图约 900pt 宽，按手机屏幕倍率出图至少是 2 倍清晰度，
  // 出图分辨率与手机屏幕宽度无关。
  const captureScale = Math.min(1, MAX_CAPTURE_HEIGHT / layout.height);
  const captureWidth = Math.round(layout.width * captureScale);
  const previewWidth = Math.round(layout.width * PREVIEW_SCALE);

  const share = async () => {
    const svg = captureRef.current as CaptureSvg | null;
    if (!svg || isSharing) return;
    setIsSharing(true);
    try {
      if (!(await Sharing.isAvailableAsync())) {
        Alert.alert(t("scheduleManagement.export.unavailable"));
        return;
      }
      const base64 = await new Promise<string>((resolve, reject) => {
        svg.toDataURL((data) => (data ? resolve(data) : reject(new Error("EMPTY_IMAGE"))));
      });
      const fileUri = `${FileSystem.cacheDirectory}${scheduleExportFileName(storeCode, weekStartDate)}`;
      // iOS 返回的 base64 每 64 字符带换行，写文件前去掉空白。
      await FileSystem.writeAsStringAsync(fileUri, base64.replace(/\s/g, ""), {
        encoding: FileSystem.EncodingType.Base64,
      });
      // 不在分享后立即删文件：Android 目标应用（如微信）可能在分享面板关闭后才读取；缓存目录由系统回收。
      await Sharing.shareAsync(fileUri, {
        mimeType: "image/png",
        UTI: "public.png",
        dialogTitle: t("scheduleManagement.export.title"),
      });
    } catch (error) {
      console.warn("[schedule-export] share failed", error);
      Alert.alert(t("scheduleManagement.export.failed"));
    } finally {
      setIsSharing(false);
    }
  };

  if (!visible) return null;

  const body = (
    <>
      <View
        style={styles.previewFrame}
        accessible
        accessibilityRole="image"
        accessibilityLabel={`${labels.title} ${weekLabel}`}
      >
        {/* 预览保持可读的字号，左右滑动查看整周；分享出去的是完整高清大图 */}
        <ScrollView horizontal showsHorizontalScrollIndicator>
          <ScheduleImage board={board} labels={labels} width={previewWidth} />
        </ScrollView>
      </View>
      <Text variant="bodySmall" style={styles.hint}>
        {t("scheduleManagement.export.hint")}
      </Text>
      <Text variant="bodySmall" style={styles.hint}>
        {t("scheduleManagement.export.previewHint")}
      </Text>
      {/* 截图专用画布：透明、不拦截触摸，只给 toDataURL 取图。 */}
      <View pointerEvents="none" style={styles.captureHost} importantForAccessibility="no-hide-descendants">
        <ScheduleImage ref={captureRef} board={board} labels={labels} width={captureWidth} />
      </View>
    </>
  );

  const shareButton = (
    <Button
      mode="contained"
      icon="share-variant"
      onPress={share}
      loading={isSharing}
      disabled={isSharing || !board.peopleCount}
      buttonColor={HB_COLORS.action}
      textColor={HB_COLORS.white}
    >
      {t("scheduleManagement.export.share")}
    </Button>
  );

  if (presentation === "sheet") {
    return (
      <BusinessSheet
        visible
        title={t("scheduleManagement.export.title")}
        subtitle={t("scheduleManagement.export.subtitle")}
        onDismiss={onDismiss}
        footer={shareButton}
      >
        {body}
      </BusinessSheet>
    );
  }

  return (
    <View style={StyleSheet.absoluteFill}>
      <Pressable style={styles.backdrop} onPress={onDismiss} accessible={false} importantForAccessibility="no" />
      <View style={styles.overlayPanel} accessibilityViewIsModal>
        <View style={styles.overlayHeader}>
          <View style={styles.overlayHeading}>
            <Text variant="titleMedium" accessibilityRole="header">
              {t("scheduleManagement.export.title")}
            </Text>
            <Text variant="bodySmall" style={styles.hint}>
              {t("scheduleManagement.export.subtitle")}
            </Text>
          </View>
          <IconButton icon="close" accessibilityLabel={t("common:actions.close")} onPress={onDismiss} />
        </View>
        <ScrollView contentContainerStyle={styles.overlayBody}>{body}</ScrollView>
        <View style={styles.overlayFooter}>{shareButton}</View>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  backdrop: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: "rgba(16,24,40,0.32)",
  },
  captureHost: {
    left: 0,
    opacity: 0,
    position: "absolute",
    top: 0,
    zIndex: -1,
  },
  hint: {
    color: HB_COLORS.textSecondary,
  },
  overlayBody: {
    gap: HB_SPACING.xs,
    padding: HB_SPACING.md,
  },
  overlayFooter: {
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
    padding: HB_SPACING.sm,
  },
  overlayHeader: {
    alignItems: "center",
    flexDirection: "row",
    paddingLeft: HB_SPACING.md,
    paddingTop: HB_SPACING.xs,
  },
  overlayHeading: {
    flex: 1,
  },
  overlayPanel: {
    alignSelf: "center",
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.sheet,
    bottom: HB_SPACING.md,
    maxWidth: 720,
    overflow: "hidden",
    position: "absolute",
    top: HB_SPACING.md,
    width: "70%",
  },
  previewFrame: {
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    overflow: "hidden",
  },
});

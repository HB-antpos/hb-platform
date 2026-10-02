import { forwardRef, useMemo, useRef, useState } from "react";
import { Alert, Pressable, ScrollView, StyleSheet, View } from "react-native";
import * as FileSystem from "expo-file-system/legacy";
import * as Sharing from "expo-sharing";
import { Button, IconButton, Text } from "react-native-paper";
import Svg, { G, Line, Rect, Text as SvgText } from "react-native-svg";
import { ATTENDANCE_STATUS_TONES } from "./AdjustmentFormControls";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import {
  buildScheduleExportTable,
  fitTextToWidth,
  layoutScheduleExport,
  SCHEDULE_EXPORT_LAYOUT as L,
  scheduleExportFileName,
  type ScheduleExportSourceRow,
  type ScheduleExportTable,
} from "@/modules/attendance/schedule-export";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

/** 截图用的隐藏画布最高 2000pt：iOS 按屏幕倍率出图，再高位图内存吃紧。 */
const MAX_CAPTURE_HEIGHT = 2000;
const ACCENT = ATTENDANCE_STATUS_TONES.accent;
const LEAVE = ATTENDANCE_STATUS_TONES.warning;
const GRID_LINE = HB_COLORS.outlineMuted;
const MUTED_TEXT = "#98A2B3";
const WEEKEND_FILL = "#F9FAFB";

interface ScheduleImageLabels {
  title: string;
  subtitle: string;
  employee: string;
  rest: string;
  onDuty: string;
  footerNote: string;
  generatedAt: string;
  weekday: (index: number) => string;
  leaveType: (type?: string) => string;
}

type CaptureSvg = Svg & {
  toDataURL: (callback: (base64?: string) => void, options?: object) => void;
};

function formatGeneratedAt(date: Date) {
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/**
 * 排班图片本体：固定 viewBox 坐标系，按传入宽度等比缩放。
 * 预览与截图共用同一份绘制，只是渲染尺寸不同。
 */
const ScheduleImage = forwardRef<Svg, {
  table: ScheduleExportTable;
  labels: ScheduleImageLabels;
  width: number;
}>(function ScheduleImage({ table, labels, width }, ref) {
  const layout = useMemo(() => layoutScheduleExport(table), [table]);
  const height = (width * layout.height) / layout.width;
  const { tableLeft, tableTop, tableWidth, summaryTop, tableBottom } = layout;
  const dayLeft = (index: number) => tableLeft + L.nameWidth + index * L.dayWidth;
  const tableRight = tableLeft + tableWidth;
  // SVG 文字以基线定位：取字号约 0.35 倍下移，让文字在行内视觉居中。
  const baseline = (centerY: number, fontSize: number) => centerY + fontSize * 0.35;

  return (
    <Svg ref={ref} width={width} height={height} viewBox={`0 0 ${layout.width} ${layout.height}`}>
      <Rect x={0} y={0} width={layout.width} height={layout.height} fill={HB_COLORS.white} />

      <SvgText x={L.padding} y={L.padding + 22} fontSize={22} fontWeight="700" fill={HB_COLORS.textPrimary}>
        {fitTextToWidth(labels.title, tableWidth, 22)}
      </SvgText>
      <SvgText x={L.padding} y={L.padding + 46} fontSize={13} fill={HB_COLORS.textSecondary}>
        {labels.subtitle}
      </SvgText>

      {/* 周末两列整列浅灰底，员工一眼区分周末班。 */}
      {table.days.map((day, index) =>
        index >= 5 ? (
          <Rect
            key={`weekend-${day}`}
            x={dayLeft(index)}
            y={tableTop}
            width={L.dayWidth}
            height={summaryTop - tableTop}
            fill={WEEKEND_FILL}
          />
        ) : null,
      )}

      {/* 表头 */}
      <Rect x={tableLeft} y={tableTop} width={tableWidth} height={L.headerHeight} fill={HB_COLORS.surfaceMuted} />
      <SvgText x={tableLeft + 12} y={baseline(tableTop + L.headerHeight / 2, 13)} fontSize={13} fontWeight="600" fill={HB_COLORS.textSecondary}>
        {labels.employee}
      </SvgText>
      {table.days.map((day, index) => {
        const centerX = dayLeft(index) + L.dayWidth / 2;
        return (
          <G key={`head-${day}`}>
            <SvgText x={centerX} y={tableTop + 19} fontSize={13} fontWeight="700" fill={HB_COLORS.textPrimary} textAnchor="middle">
              {labels.weekday(index)}
            </SvgText>
            <SvgText x={centerX} y={tableTop + 35} fontSize={11} fill={HB_COLORS.textSecondary} textAnchor="middle">
              {day.slice(5, 10)}
            </SvgText>
          </G>
        );
      })}

      {/* 员工行 */}
      {table.rows.map((row, rowIndex) => {
        const top = layout.rowTops[rowIndex];
        const rowHeight = layout.rowHeights[rowIndex];
        const centerY = top + rowHeight / 2;
        return (
          <G key={row.userGuid}>
            <SvgText x={tableLeft + 12} y={baseline(centerY, 14)} fontSize={14} fontWeight="600" fill={HB_COLORS.textPrimary}>
              {fitTextToWidth(row.employeeName, L.nameWidth - 18, 14)}
            </SvgText>
            {row.cells.map((cell, dayIndex) => {
              const left = dayLeft(dayIndex);
              const centerX = left + L.dayWidth / 2;
              const pillX = left + 5;
              const pillWidth = L.dayWidth - 10;
              if (cell.kind === "leave") {
                return (
                  <G key={dayIndex}>
                    <Rect x={pillX} y={centerY - L.shiftHeight / 2} width={pillWidth} height={L.shiftHeight} rx={6} fill={LEAVE.background} stroke="#FEDF89" strokeWidth={1} />
                    <SvgText x={centerX} y={baseline(centerY, 13)} fontSize={13} fontWeight="700" fill={LEAVE.text} textAnchor="middle">
                      {fitTextToWidth(labels.leaveType(cell.leaveType), pillWidth - 8, 13)}
                    </SvgText>
                  </G>
                );
              }
              if (cell.kind === "shift") {
                // 多个班次从行顶依次叠放；开始时间加粗，结束时间常规，两行比一行「09:00–17:30」更易读。
                return (
                  <G key={dayIndex}>
                    {cell.shifts.map((shift, shiftIndex) => {
                      const shiftTop = top + L.cellPaddingY + shiftIndex * (L.shiftHeight + L.shiftGap);
                      return (
                        <G key={`${shift.startTime}-${shiftIndex}`}>
                          <Rect x={pillX} y={shiftTop} width={pillWidth} height={L.shiftHeight} rx={6} fill={ACCENT.background} stroke="#B2DDFF" strokeWidth={1} />
                          <SvgText x={centerX} y={shiftTop + 16} fontSize={13} fontWeight="700" fill={ACCENT.text} textAnchor="middle">
                            {shift.startTime}
                          </SvgText>
                          <SvgText x={centerX} y={shiftTop + 30} fontSize={12} fill={ACCENT.text} textAnchor="middle">
                            {`– ${shift.endTime}`}
                          </SvgText>
                        </G>
                      );
                    })}
                  </G>
                );
              }
              return (
                <SvgText key={dayIndex} x={centerX} y={baseline(centerY, 13)} fontSize={13} fill={MUTED_TEXT} textAnchor="middle">
                  {labels.rest}
                </SvgText>
              );
            })}
            <Line x1={tableLeft} y1={top} x2={tableRight} y2={top} stroke={GRID_LINE} strokeWidth={1} />
          </G>
        );
      })}

      {/* 汇总行：每天在岗人数 */}
      <Rect x={tableLeft} y={summaryTop} width={tableWidth} height={L.summaryHeight} fill={HB_COLORS.surfaceMuted} />
      <Line x1={tableLeft} y1={summaryTop} x2={tableRight} y2={summaryTop} stroke={GRID_LINE} strokeWidth={1} />
      <SvgText x={tableLeft + 12} y={baseline(summaryTop + L.summaryHeight / 2, 12)} fontSize={12} fontWeight="600" fill={HB_COLORS.textSecondary}>
        {fitTextToWidth(labels.onDuty, L.nameWidth - 18, 12)}
      </SvgText>
      {table.dayHeadcounts.map((count, index) => (
        <SvgText
          key={`count-${index}`}
          x={dayLeft(index) + L.dayWidth / 2}
          y={baseline(summaryTop + L.summaryHeight / 2, 13)}
          fontSize={13}
          fontWeight="700"
          fill={count ? HB_COLORS.textPrimary : HB_COLORS.danger}
          textAnchor="middle"
        >
          {String(count)}
        </SvgText>
      ))}

      {/* 竖向分隔线与外框 */}
      {table.days.map((_, index) => L.nameWidth + index * L.dayWidth).map((offset) => (
        <Line key={`v-${offset}`} x1={tableLeft + offset} y1={tableTop} x2={tableLeft + offset} y2={tableBottom} stroke={GRID_LINE} strokeWidth={1} />
      ))}
      <Line x1={tableLeft} y1={tableTop + L.headerHeight} x2={tableRight} y2={tableTop + L.headerHeight} stroke={GRID_LINE} strokeWidth={1} />
      <Rect x={tableLeft} y={tableTop} width={tableWidth} height={tableBottom - tableTop} rx={2} fill="none" stroke={HB_COLORS.outline} strokeWidth={1} />

      <SvgText x={tableLeft} y={tableBottom + 21} fontSize={11} fill={MUTED_TEXT}>
        {labels.footerNote}
      </SvgText>
      <SvgText x={tableRight} y={tableBottom + 21} fontSize={11} fill={MUTED_TEXT} textAnchor="end">
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
  rows,
  storeCode,
  storeLabel,
  weekStartDate,
  weekLabel,
  onDismiss,
}: {
  visible: boolean;
  presentation: "sheet" | "overlay";
  days: string[];
  rows: ScheduleExportSourceRow[];
  storeCode?: string;
  storeLabel: string;
  weekStartDate: string;
  /** 如「2026 年第 40 周 · 2026-09-28 - 2026-10-04」。 */
  weekLabel: string;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const captureRef = useRef<Svg>(null);
  const [previewWidth, setPreviewWidth] = useState(0);
  const [isSharing, setIsSharing] = useState(false);

  const table = useMemo(() => buildScheduleExportTable(days, rows), [days, rows]);
  const layout = useMemo(() => layoutScheduleExport(table), [table]);
  // 生成时间在打开时定格，预览与分享出去的图一致。
  const generatedAt = useMemo(() => (visible ? formatGeneratedAt(new Date()) : ""), [visible]);
  const labels = useMemo<ScheduleImageLabels>(() => ({
    title: t("scheduleManagement.export.imageTitle", { store: storeLabel }),
    subtitle: weekLabel,
    employee: t("scheduleManagement.export.employee"),
    rest: t("scheduleManagement.export.rest"),
    onDuty: t("scheduleManagement.export.onDuty"),
    footerNote: t("scheduleManagement.export.footerNote", { count: table.rows.length }),
    generatedAt: t("scheduleManagement.export.generatedAt", { time: generatedAt }),
    weekday: (index) => t(`weekdays.${index}`),
    leaveType: (type) => (type ? t(`leaveTypes.${type}`, type) : t("scheduleManagement.legend.leave")),
  }), [generatedAt, storeLabel, t, table.rows.length, weekLabel]);

  // 隐藏画布按原始坐标 1:1 渲染（超高时等比缩小），出图分辨率与手机屏幕宽度无关。
  const captureScale = Math.min(1, MAX_CAPTURE_HEIGHT / layout.height);
  const captureWidth = Math.round(layout.width * captureScale);

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
        onLayout={(event) => {
          const next = Math.floor(event.nativeEvent.layout.width) - 2;
          setPreviewWidth((current) => (current === next ? current : next));
        }}
        accessible
        accessibilityRole="image"
        accessibilityLabel={`${labels.title} ${weekLabel}`}
      >
        {previewWidth > 0 ? <ScheduleImage table={table} labels={labels} width={previewWidth} /> : null}
      </View>
      <Text variant="bodySmall" style={styles.hint}>
        {t("scheduleManagement.export.hint")}
      </Text>
      {/* 截图专用画布：透明、不拦截触摸，只给 toDataURL 取图。 */}
      <View pointerEvents="none" style={styles.captureHost} importantForAccessibility="no-hide-descendants">
        <ScheduleImage ref={captureRef} table={table} labels={labels} width={captureWidth} />
      </View>
    </>
  );

  const shareButton = (
    <Button
      mode="contained"
      icon="share-variant"
      onPress={share}
      loading={isSharing}
      disabled={isSharing || !table.rows.length}
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

import { useEffect, useMemo, useRef, useState } from "react";
import * as ScreenOrientation from "expo-screen-orientation";
import {
  Alert,
  Animated,
  Modal,
  PanResponder,
  Pressable,
  ScrollView,
  StyleSheet,
  View,
} from "react-native";
import {
  Button,
  Card,
  Chip,
  IconButton,
  Text,
  TextInput,
} from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import {
  ATTENDANCE_STATUS_TONES,
  StatusPill,
  type AttendanceStatusTone,
} from "./AdjustmentFormControls";
import { ScheduleExportSheet } from "./ScheduleExportSheet";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAuthStore } from "@/store/auth-store";
import {
  buildWeekDates,
  computeScheduleHourStats,
  defaultMealBreakCount,
  formatScheduleHours,
  MAX_MEAL_BREAK_COUNT,
  MEAL_BREAK_MINUTES,
} from "@/modules/attendance/attendance-my-week";
import {
  availabilityKey,
  classifyScheduleGridCell,
  countUncoveredDays,
  employmentTypeCode,
  type EmploymentTypeCode,
  filterScheduleUsers,
  isBatchSchedulableCell,
  formatShiftShort,
  groupAvailabilityByUserDate,
  isAllDayRange,
  normalizeClockTime,
  shiftEditorMinutes,
  stepClockTime,
  summarizeSchedulePublishState,
  type ScheduleGridCell,
  type SchedulePublishState,
} from "@/modules/attendance/schedule-grid";
import {
  DEFAULT_SHIFT,
  SHIFT_PRESETS,
  getAvailabilityConflict,
  getCellMealBadge,
  getPlacementVerdict,
  isScheduleLocked,
  type PlacementKind,
  type PlacementSource,
  type PlacementVerdict,
} from "@/modules/attendance/schedule-placement";
import {
  clampScrollOffset,
  getDragStartVerdict,
  getEdgeScrollStep,
  hitTestScheduleCell,
  isSameDragCell,
  type DragCellIndex,
  type DragGridGeometry,
} from "@/modules/attendance/schedule-drag";
import type { PlacementTargetCell } from "@/modules/attendance/schedule-placement-actions";
import type {
  AttendanceAvailability,
  AttendanceSchedule,
  AttendanceSchedulePayload,
  AttendanceScheduleStatus,
  AttendanceScheduleUpdatePayload,
} from "@/modules/attendance/types";
import type { StoreUserListItem } from "@/modules/users/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

const ROW_HEADER_WIDTH = 104;
const HEADER_HEIGHT = 52;
const MIN_CELL_WIDTH = 56;
const CELL_HEIGHT = 52;
/** 长按多久后拿起班次开始拖动（毫秒）。 */
const DRAG_LONG_PRESS_MS = 280;
/** 拖动时跟手的小浮标尺寸；浮标显示在手指上方，避免被手指挡住。 */
const DRAG_GHOST_WIDTH = 104;
const DRAG_GHOST_HEIGHT = 32;
const DRAG_GHOST_LIFT = 18;
const DRAG_AUTO_SCROLL_INTERVAL_MS = 32;
/** 手指要在左 / 右边缘带里停留这么久才开始自动滚动，避免拖到靠边的日期列时列在手底下漂移。 */
const DRAG_AUTO_SCROLL_DWELL_MS = 280;
/** 边缘带宽度（比一列的最小宽度 56 窄，保证最靠边的一整列仍有一半能直接点中）。 */
const DRAG_AUTO_SCROLL_EDGE = 28;
const STEP_MINUTES = 30;
const SIDE_PANEL_WIDTH = 380;
const ACCENT = ATTENDANCE_STATUS_TONES.accent;
const LEAVE = ATTENDANCE_STATUS_TONES.warning;
const UNAVAILABLE = ATTENDANCE_STATUS_TONES.danger;
const EMPTY_FORM = {
  startTime: DEFAULT_SHIFT.startTime,
  endTime: DEFAULT_SHIFT.endTime,
  status: "Draft" as AttendanceScheduleStatus,
  remark: "",
  /** 店长指定的用餐次数；null 表示按班次时长默认。 */
  mealBreakCount: null as number | null,
};
const PUBLISH_STATE_TONES: Record<SchedulePublishState, AttendanceStatusTone> = {
  draft: "warning",
  published: "success",
  empty: "neutral",
};

type ScheduleDraft = typeof EMPTY_FORM;
/** 批量排班入口：点员工选多天，或点日期选多位员工。 */
type BatchTarget =
  | { mode: "employee"; userGuid: string; employeeName?: string }
  | { mode: "date"; workDate: string; weekdayIndex: number };
type GridVariant = "card" | "fullscreen";

/** 一次长按拖动的运行时状态：手指位置、格子几何都变得很快，放 ref 里不触发渲染。 */
interface DragRuntime {
  source: PlacementSource;
  variant: GridVariant;
  /** PanResponder 是否已接管手势（长按后手指没动就抬起时为 false，要自行收尾）。 */
  granted: boolean;
  /** 日期区内容与外层容器在屏幕上的位置是否已量到。 */
  measured: boolean;
  contentX: number;
  contentY: number;
  /** 量到 contentX 时的横向滚动偏移，用来换算之后滚动造成的位移。 */
  scrollX0: number;
  wrapperX: number;
  wrapperY: number;
  point: { x: number; y: number };
  hover: DragCellIndex | null;
  /** 手指进入边缘带的时刻；不在边缘带时为 null。 */
  edgeSince: number | null;
  timer: ReturnType<typeof setInterval> | null;
}

interface EditingTarget {
  schedule?: AttendanceSchedule;
  /** 当天该员工未取消的全部班次，用于多班次切换与请假提示。 */
  daySchedules: AttendanceSchedule[];
  userGuid: string;
  employeeName?: string;
  workDate: string;
  weekdayIndex: number;
}

interface ScheduleRow {
  userGuid: string;
  employeeName?: string;
  employmentType?: EmploymentTypeCode;
  /** 仅未成年员工有值。 */
  age?: number;
  isStoreManager?: boolean;
  schedules: AttendanceSchedule[];
}

// F/P/C 用不同色系区分，未成年年龄用警示色提醒排班注意工时限制。
// F/P/C 用实心色块区分（蓝/绿/紫、白字），与浅底的状态、经理、年龄标签明显不同，一眼可辨。
const EMPLOYMENT_TYPE_COLORS: Record<EmploymentTypeCode, string> = {
  F: "#175CD3",
  P: "#079455",
  C: "#7A5AF8",
};
const EMPLOYMENT_TYPE_LEGEND: { code: EmploymentTypeCode; labelKey: string }[] = [
  { code: "F", labelKey: "fullTime" },
  { code: "P", labelKey: "partTime" },
  { code: "C", labelKey: "casual" },
];

function EmploymentTypeBadge({ code }: { code: EmploymentTypeCode }) {
  return (
    <View style={[styles.typeBadge, { backgroundColor: EMPLOYMENT_TYPE_COLORS[code] }]}>
      <Text variant="labelSmall" style={styles.typeBadgeText}>
        {code}
      </Text>
    </View>
  );
}

function toLocalDateString(date: Date) {
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${date.getFullYear()}-${month}-${day}`;
}

function shortDate(value: string) {
  return value ? value.slice(5, 10) : "--";
}

function formatWeekRange(start: string, end: string) {
  if (!start) {
    return "--";
  }
  return end ? `${start} - ${end}` : start;
}

function getIsoWeekInfo(value: string) {
  const parsed = new Date(`${value}T00:00:00Z`);
  if (Number.isNaN(parsed.getTime())) {
    return null;
  }

  const date = new Date(
    Date.UTC(
      parsed.getUTCFullYear(),
      parsed.getUTCMonth(),
      parsed.getUTCDate(),
    ),
  );
  const day = date.getUTCDay() || 7;
  date.setUTCDate(date.getUTCDate() + 4 - day);

  const year = date.getUTCFullYear();
  const yearStart = new Date(Date.UTC(year, 0, 1));
  const week = Math.ceil(
    ((date.getTime() - yearStart.getTime()) / 86400000 + 1) / 7,
  );

  return { week, year };
}

function getUserDisplayName(user: StoreUserListItem) {
  return user.fullName || user.username || user.userGUID;
}

/** 内联 ±30 分钟步进：位于底部弹层内，不能再弹二级选择器（原生 Modal 会互相遮挡）。 */
function TimeStepField({
  label,
  value,
  minusLabel,
  plusLabel,
  disabled,
  onChange,
}: {
  label: string;
  value: string;
  minusLabel: string;
  plusLabel: string;
  disabled?: boolean;
  onChange: (value: string) => void;
}) {
  const renderStep = (delta: number, text: string, a11y: string) => (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={a11y}
      disabled={disabled}
      onPress={() => onChange(stepClockTime(value, delta))}
      style={({ pressed }) => [
        styles.stepButton,
        pressed ? styles.stepButtonPressed : null,
      ]}
    >
      <Text variant="labelLarge" style={styles.stepButtonText}>
        {text}
      </Text>
    </Pressable>
  );

  return (
    <View style={styles.stepField}>
      <Text variant="labelMedium" style={styles.muted}>
        {label}
      </Text>
      <View style={styles.stepRow}>
        {renderStep(-STEP_MINUTES, "−30", minusLabel)}
        <View style={styles.stepValue} accessibilityLiveRegion="polite">
          <Text variant="titleLarge" style={styles.stepValueText}>
            {normalizeClockTime(value) || "--:--"}
          </Text>
        </View>
        {renderStep(STEP_MINUTES, "+30", plusLabel)}
      </View>
    </View>
  );
}

/**
 * 用餐次数步进：默认按班次时长推算（满 4.5 小时 1 次、超过 9 小时 2 次），店长可增减（0 次＝不扣用餐）。
 * 改过后显示「恢复默认」，回到按时长自动计算。
 */
function MealBreakStepField({
  label,
  count,
  isDefault,
  valueText,
  defaultText,
  resetText,
  minusLabel,
  plusLabel,
  disabled,
  onChange,
  onReset,
}: {
  label: string;
  count: number;
  isDefault: boolean;
  valueText: string;
  defaultText: string;
  resetText: string;
  minusLabel: string;
  plusLabel: string;
  disabled?: boolean;
  onChange: (count: number) => void;
  onReset: () => void;
}) {
  const renderStep = (delta: number, text: string, a11y: string) => {
    const next = count + delta;
    const outOfRange = next < 0 || next > MAX_MEAL_BREAK_COUNT;
    return (
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={a11y}
        disabled={disabled || outOfRange}
        onPress={() => onChange(next)}
        style={({ pressed }) => [
          styles.stepButton,
          outOfRange ? styles.stepButtonDisabled : null,
          pressed ? styles.stepButtonPressed : null,
        ]}
      >
        <Text variant="labelLarge" style={outOfRange ? styles.muted : styles.stepButtonText}>
          {text}
        </Text>
      </Pressable>
    );
  };

  return (
    <View style={styles.stepField}>
      <View style={styles.mealLabelRow}>
        <Text variant="labelMedium" style={styles.muted}>
          {label}
        </Text>
        {isDefault ? (
          <Text variant="labelSmall" style={styles.muted}>
            {defaultText}
          </Text>
        ) : (
          <Pressable accessibilityRole="button" disabled={disabled} onPress={onReset} hitSlop={8}>
            <Text variant="labelMedium" style={styles.stepButtonText}>
              {resetText}
            </Text>
          </Pressable>
        )}
      </View>
      <View style={styles.stepRow}>
        {renderStep(-1, "−1", minusLabel)}
        <View style={styles.stepValue} accessibilityLiveRegion="polite">
          <Text variant="titleMedium" style={styles.stepValueText}>
            {valueText}
          </Text>
        </View>
        {renderStep(1, "+1", plusLabel)}
      </View>
    </View>
  );
}

export function ScheduleManagementCard({
  weekStartDate,
  storeCode,
  storeName,
  users,
  schedules,
  availability,
  isLoading,
  isBusy,
  isCopying,
  onPreviousWeek,
  onNextWeek,
  onCreate,
  onCreateBatch,
  onUpdate,
  onDelete,
  onPublishWeek,
  onCopyPreviousWeek,
  punchScheduleGuids,
  punchesKnown,
  onPlaceShift,
  onDragActiveChange,
}: {
  weekStartDate: string;
  storeCode?: string;
  storeName?: string;
  users: StoreUserListItem[];
  schedules: AttendanceSchedule[];
  /** 本店员工本周填写的可上班时间；查询失败时传空数组，只是不显示「可」标记。 */
  availability: AttendanceAvailability[];
  isLoading: boolean;
  isBusy: boolean;
  isCopying: boolean;
  onPreviousWeek: () => void;
  onNextWeek: () => void;
  onCreate: (payload: AttendanceSchedulePayload) => void;
  /** 批量新建（依次提交、统一汇总提示）。 */
  onCreateBatch: (payloads: AttendanceSchedulePayload[]) => void;
  onUpdate: (
    scheduleGuid: string,
    payload: AttendanceScheduleUpdatePayload,
  ) => void;
  onDelete: (scheduleGuid: string) => void;
  onPublishWeek: () => void;
  onCopyPreviousWeek: () => void;
  /** 本周有打卡记录的排班 GUID：这些班次不能移动 / 复制（仍可删除）。 */
  punchScheduleGuids: ReadonlySet<string>;
  /** 是否成功拿到了本周打卡；拿不到（无权限 / 失败）时保守地锁今天及以前的班次。 */
  punchesKnown: boolean;
  /** 「点选目标格」放下：移动 / 复制 / 常用班次添加，由屏幕层落地并提示（含撤销）。 */
  onPlaceShift: (source: PlacementSource, target: PlacementTargetCell) => void;
  /** 长按拖动开始 / 结束：屏幕层据此暂时锁住整页纵向滚动，避免拖动时页面跟着动。 */
  onDragActiveChange?: (active: boolean) => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const insets = useSafeAreaInsets();
  const [editingTarget, setEditingTarget] = useState<EditingTarget | null>(
    null,
  );
  const [form, setForm] = useState<ScheduleDraft>(EMPTY_FORM);
  const [batchTarget, setBatchTarget] = useState<BatchTarget | null>(null);
  // 员工模式存日期、日期模式存员工 GUID。
  const [batchSelection, setBatchSelection] = useState<ReadonlySet<string>>(() => new Set());
  const [isFullscreen, setIsFullscreen] = useState(false);
  const [isExportOpen, setIsExportOpen] = useState(false);
  // 「点选目标格」：拿起一个班次（移动 / 复制）或常用班次（添加）后，点格子放下
  const [placement, setPlacement] = useState<PlacementSource | null>(null);
  // 「长按拖动」：长按班次 / 常用班次拿起，手指拖到目标格松手放下（点选模式的快捷方式，落点规则相同）
  const [drag, setDrag] = useState<{ source: PlacementSource; variant: GridVariant; label: string } | null>(null);
  const [dragHover, setDragHover] = useState<DragCellIndex | null>(null);
  const dragRuntimeRef = useRef<DragRuntime | null>(null);
  // 每次渲染都更新成最新闭包；PanResponder 与定时器只创建一次，通过它读到最新的行 / 列宽 / 回调
  const dragApiRef = useRef<{
    move: (x: number, y: number) => void;
    release: (x: number, y: number) => void;
    finish: (commit: boolean) => void;
    tick: () => void;
  }>({ move: () => undefined, release: () => undefined, finish: () => undefined, tick: () => undefined });
  const dragGhost = useRef(new Animated.ValueXY({ x: 0, y: 0 })).current;
  const lastTouchRef = useRef({ x: 0, y: 0 });
  const wrapperRefs = useRef<Record<GridVariant, View | null>>({ card: null, fullscreen: null });
  const contentRefs = useRef<Record<GridVariant, View | null>>({ card: null, fullscreen: null });
  const scrollRefs = useRef<Record<GridVariant, ScrollView | null>>({ card: null, fullscreen: null });
  const scrollXRef = useRef<Record<GridVariant, number>>({ card: 0, fullscreen: 0 });
  // 只在长按拿起后才接管手势，平时点按、滚动完全不受影响
  const dragPanResponder = useRef(
    PanResponder.create({
      onStartShouldSetPanResponder: () => false,
      onMoveShouldSetPanResponder: () => Boolean(dragRuntimeRef.current && !dragRuntimeRef.current.granted),
      onMoveShouldSetPanResponderCapture: () => Boolean(dragRuntimeRef.current && !dragRuntimeRef.current.granted),
      onPanResponderGrant: () => {
        if (dragRuntimeRef.current) dragRuntimeRef.current.granted = true;
      },
      onPanResponderMove: (_event, gesture) => dragApiRef.current.move(gesture.moveX, gesture.moveY),
      // 松手时用抬起点再算一次落点：兜住 JS 线程忙时被合并 / 丢掉的最后几次 move
      onPanResponderRelease: (event) =>
        dragApiRef.current.release(event.nativeEvent.pageX, event.nativeEvent.pageY),
      onPanResponderTerminate: () => dragApiRef.current.finish(false),
      onPanResponderTerminationRequest: () => false,
    }),
  ).current;
  // 网格容器宽度：宽屏（横屏全屏）时把 7 天均分铺满，窄屏保持最小列宽并横向滚动。
  const [gridWidths, setGridWidths] = useState<Record<GridVariant, number>>({
    card: 0,
    fullscreen: 0,
  });

  useEffect(() => {
    if (!isFullscreen) {
      return;
    }

    ScreenOrientation.lockAsync(
      ScreenOrientation.OrientationLock.LANDSCAPE,
    ).catch((error) =>
      console.warn("[schedule-management] landscape lock failed", error),
    );

    return () => {
      ScreenOrientation.lockAsync(
        ScreenOrientation.OrientationLock.PORTRAIT_UP,
      ).catch((error) =>
        console.warn("[schedule-management] portrait lock failed", error),
      );
    };
  }, [isFullscreen]);

  const days = useMemo(() => buildWeekDates(weekStartDate), [weekStartDate]);
  const today = toLocalDateString(new Date());

  // 店长默认只看到自己；其他店长由筛选开关控制，店员始终在表中。
  const currentUser = useAuthStore((state) => state.user);
  const currentUserGuid = currentUser?.userGUID || currentUser?.userGuid;
  const [showOtherManagers, setShowOtherManagers] = useState(false);
  const { visible: visibleUsers, otherManagerCount } = useMemo(
    () =>
      filterScheduleUsers(
        users,
        new Set(schedules.map((schedule) => schedule.userGuid)),
        showOtherManagers,
        currentUserGuid,
      ),
    [currentUserGuid, schedules, showOtherManagers, users],
  );

  const rows = useMemo<ScheduleRow[]>(() => {
    const rowMap = new Map<string, ScheduleRow>();
    visibleUsers.forEach((user) => {
      rowMap.set(user.userGUID, {
        userGuid: user.userGUID,
        employeeName: getUserDisplayName(user),
        employmentType: employmentTypeCode(user.employmentType),
        age: user.age,
        isStoreManager: user.isStoreManager,
        schedules: [],
      });
    });

    schedules.forEach((schedule) => {
      const existing = rowMap.get(schedule.userGuid);
      const row = existing ?? {
        userGuid: schedule.userGuid,
        employeeName: schedule.employeeName || schedule.userGuid,
        schedules: [],
      };
      row.schedules = [...row.schedules, schedule];
      rowMap.set(schedule.userGuid, row);
    });

    return Array.from(rowMap.values()).sort((left, right) =>
      (left.employeeName || left.userGuid).localeCompare(
        right.employeeName || right.userGuid,
      ),
    );
  }, [schedules, visibleUsers]);

  const availabilityMap = useMemo(
    () => groupAvailabilityByUserDate(availability),
    [availability],
  );

  // 每位员工本周工时（排除已取消与请假），行首与编辑弹层共用。
  const rowMinutes = useMemo(() => {
    const map = new Map<string, number>();
    rows.forEach((row) => {
      map.set(row.userGuid, computeScheduleHourStats(row.schedules).totalMinutes);
    });
    return map;
  }, [rows]);

  const hourStats = useMemo(
    () => computeScheduleHourStats(schedules),
    [schedules],
  );
  const scheduledPeople = useMemo(
    () => rows.filter((row) => (rowMinutes.get(row.userGuid) ?? 0) > 0).length,
    [rowMinutes, rows],
  );
  const uncoveredDays = useMemo(
    () => countUncoveredDays(days, schedules),
    [days, schedules],
  );
  const publishState = useMemo(
    () => summarizeSchedulePublishState(schedules),
    [schedules],
  );

  const weekEndDate = days[6] || "";
  const weekRangeLabel = formatWeekRange(weekStartDate, weekEndDate);
  const storeLabel = storeName || storeCode || t("scheduleManagement.noStore");
  const weekInfo = getIsoWeekInfo(weekStartDate);
  const weekNumberLabel = weekInfo
    ? t("scheduleManagement.weekNumberLabel", weekInfo)
    : weekRangeLabel;
  const hoursShort = (minutes: number) =>
    t("scheduleManagement.hoursShort", { hours: formatScheduleHours(minutes) });
  // 本周班次全部发布后才能导出图片，避免把草稿发到群里。
  const canExportImage =
    publishState === "published" && Boolean(storeCode) && !isLoading;

  // 切周或又出现草稿时收起导出弹层，避免条件恢复后弹层意外重新出现。
  useEffect(() => {
    if (!canExportImage) setIsExportOpen(false);
  }, [canExportImage]);

  // 不能上班的时段加前缀标明，店长排班时一眼看到员工不能到岗的时间。
  const describeAvailability = (items: AttendanceAvailability[]) =>
    items.length
      ? items
          .map((item) => {
            const range = isAllDayRange(item.startTime, item.endTime)
              ? t("availability.allDay")
              : `${normalizeClockTime(item.startTime)}–${normalizeClockTime(item.endTime)}`;
            return item.isUnavailable
              ? `${t("availability.unavailableLabel")} ${range}`
              : range;
          })
          .join(" · ")
      : t("scheduleManagement.editor.noAvailability");

  const openCreate = (
    row: ScheduleRow,
    workDate: string,
    weekdayIndex: number,
    daySchedules: AttendanceSchedule[] = [],
  ) => {
    if (!storeCode) {
      return;
    }
    setEditingTarget({
      daySchedules,
      userGuid: row.userGuid,
      employeeName: row.employeeName,
      workDate,
      weekdayIndex,
    });
    // 员工只填了一段非全天的可上班时间时，直接用它作为默认班次，少点几下。
    // 只参考可上班时段；不能上班的时段不能拿来当默认班次。
    const dayAvailability = (
      availabilityMap.get(availabilityKey(row.userGuid, workDate)) ?? []
    ).filter((item) => !item.isUnavailable);
    const single = dayAvailability.length === 1 ? dayAvailability[0] : undefined;
    setForm(
      single && !isAllDayRange(single.startTime, single.endTime)
        ? {
            ...EMPTY_FORM,
            startTime: normalizeClockTime(single.startTime) || EMPTY_FORM.startTime,
            endTime: normalizeClockTime(single.endTime) || EMPTY_FORM.endTime,
          }
        : EMPTY_FORM,
    );
  };

  const openEdit = (
    schedule: AttendanceSchedule,
    row: ScheduleRow,
    weekdayIndex: number,
    daySchedules: AttendanceSchedule[],
  ) => {
    setEditingTarget({
      schedule,
      daySchedules,
      userGuid: row.userGuid,
      employeeName: row.employeeName,
      workDate: schedule.workDate.slice(0, 10),
      weekdayIndex,
    });
    setForm({
      startTime: normalizeClockTime(schedule.startTime),
      endTime: normalizeClockTime(schedule.endTime),
      status: schedule.status || "Draft",
      remark: schedule.remark ?? "",
      mealBreakCount: schedule.mealBreakCount ?? null,
    });
  };

  const openCell = (
    row: ScheduleRow,
    workDate: string,
    weekdayIndex: number,
    cell: ScheduleGridCell,
  ) => {
    const first = cell.schedules[0];
    if (first) {
      openEdit(first, row, weekdayIndex, cell.schedules);
      return;
    }
    openCreate(row, workDate, weekdayIndex);
  };

  const closeEditor = () => {
    setEditingTarget(null);
    setBatchTarget(null);
    setBatchSelection(new Set());
    setForm(EMPTY_FORM);
  };

  const isLocked = (schedule: AttendanceSchedule) =>
    isScheduleLocked({
      scheduleGuid: schedule.scheduleGuid,
      workDate: schedule.workDate,
      today,
      punchScheduleGuids,
      punchesKnown,
    });

  // 编辑器里点「移到… / 复制到…」：关掉编辑器，进入点选目标格模式
  const startPlacement = (kind: Extract<PlacementKind, "move" | "copy">, schedule: AttendanceSchedule) => {
    setEditingTarget(null);
    setBatchTarget(null);
    setForm(EMPTY_FORM);
    setPlacement({
      kind,
      schedule,
      startTime: normalizeClockTime(schedule.startTime),
      endTime: normalizeClockTime(schedule.endTime),
    });
  };

  const startPresetPlacement = (preset: (typeof SHIFT_PRESETS)[number]) => {
    setEditingTarget(null);
    setPlacement({ kind: "preset", startTime: preset.startTime, endTime: preset.endTime });
  };

  // 当前「拿在手里」的内容：点选模式的 placement，或正在拖动的班次
  const activeSource = placement ?? drag?.source ?? null;

  const verdictFor = (source: PlacementSource, row: ScheduleRow, day: string): PlacementVerdict =>
    getPlacementVerdict(source, {
      userGuid: row.userGuid,
      workDate: day,
      schedules: row.schedules.filter((item) => item.workDate.slice(0, 10) === day),
      availability: availabilityMap.get(availabilityKey(row.userGuid, day)),
    });

  const placementVerdictOf = (row: ScheduleRow, day: string): PlacementVerdict | undefined =>
    activeSource ? verdictFor(activeSource, row, day) : undefined;

  // 点格子放下：交给 commitPlacement（点选与拖动共用同一套落点处理）
  const handlePlacementPress = (row: ScheduleRow, day: string) => {
    if (!placement) {
      return;
    }
    commitPlacement(placement, row, day);
  };

  // 放下：不能放（重叠）直接提示；提醒（员工当天不能上班等）手机上没有悬停，必须二次确认
  const commitPlacement = (source: PlacementSource, row: ScheduleRow, day: string) => {
    if (!storeCode) {
      return;
    }
    const verdict = verdictFor(source, row, day);
    if (verdict.state === "noop") {
      setPlacement(null);
      return;
    }
    if (verdict.state === "bad") {
      Alert.alert(t("scheduleManagement.placement.badOverlap"));
      return;
    }
    const place = () => {
      onPlaceShift(source, { storeCode, userGuid: row.userGuid, workDate: day });
      setPlacement(null);
    };
    if (verdict.state === "warn") {
      const name = row.employeeName || row.userGuid;
      Alert.alert(
        t("scheduleManagement.placement.warnTitle"),
        t(
          verdict.reason === "unavailable"
            ? "scheduleManagement.placement.warnUnavailable"
            : "scheduleManagement.placement.warnOutside",
          { name },
        ),
        [
          { text: t("common:actions.cancel"), style: "cancel" },
          { text: t("scheduleManagement.placement.warnConfirm"), onPress: place },
        ],
      );
      return;
    }
    place();
  };

  const cellWidthOf = (variant: GridVariant) =>
    Math.max(MIN_CELL_WIDTH, Math.floor((gridWidths[variant] - ROW_HEADER_WIDTH - 2) / 7));

  // 当前手指下的格子：用拿起时量到的日期区位置，扣掉之后横向滚动造成的位移再做命中测试
  const updateDragHover = () => {
    const runtime = dragRuntimeRef.current;
    if (!runtime) {
      return;
    }
    let next: DragCellIndex | null = null;
    // 手指在固定姓名列上方时，下面可能压着已滚走的日期列，不能算命中
    if (runtime.measured && runtime.point.x >= runtime.wrapperX + ROW_HEADER_WIDTH) {
      const geometry: DragGridGeometry = {
        originX: runtime.contentX - (scrollXRef.current[runtime.variant] - runtime.scrollX0),
        originY: runtime.contentY,
        cellWidth: cellWidthOf(runtime.variant),
        headerHeight: HEADER_HEIGHT,
        cellHeight: CELL_HEIGHT,
        rowCount: rows.length,
        dayCount: days.length,
      };
      next = hitTestScheduleCell(runtime.point, geometry) ?? null;
    }
    if (!isSameDragCell(next, runtime.hover)) {
      runtime.hover = next;
      setDragHover(next);
    }
  };

  const moveDrag = (x: number, y: number) => {
    const runtime = dragRuntimeRef.current;
    if (!runtime) {
      return;
    }
    runtime.point = { x, y };
    dragGhost.setValue({
      x: x - runtime.wrapperX - DRAG_GHOST_WIDTH / 2,
      y: y - runtime.wrapperY - DRAG_GHOST_HEIGHT - DRAG_GHOST_LIFT,
    });
    updateDragHover();
  };

  // 手指停在日期区左 / 右边缘时自动横向滚动，才能拖到屏幕外的日期列
  const tickDragAutoScroll = () => {
    const runtime = dragRuntimeRef.current;
    if (!runtime || !runtime.measured) {
      return;
    }
    const { variant } = runtime;
    const viewportWidth = gridWidths[variant] - ROW_HEADER_WIDTH - 2;
    const step = getEdgeScrollStep({
      pointX: runtime.point.x,
      viewportLeft: runtime.wrapperX + ROW_HEADER_WIDTH,
      viewportRight: runtime.wrapperX + gridWidths[variant],
      edge: DRAG_AUTO_SCROLL_EDGE,
    });
    if (!step) {
      runtime.edgeSince = null;
      return;
    }
    const now = Date.now();
    runtime.edgeSince ??= now;
    if (now - runtime.edgeSince < DRAG_AUTO_SCROLL_DWELL_MS) {
      return;
    }
    const current = scrollXRef.current[variant];
    const next = clampScrollOffset(current + step, cellWidthOf(variant) * days.length, viewportWidth);
    if (next === current) {
      return;
    }
    scrollXRef.current[variant] = next;
    scrollRefs.current[variant]?.scrollTo({ x: next, animated: false });
    updateDragHover();
  };

  const finishDrag = (commit: boolean) => {
    const runtime = dragRuntimeRef.current;
    if (!runtime) {
      return;
    }
    dragRuntimeRef.current = null;
    if (runtime.timer) {
      clearInterval(runtime.timer);
    }
    setDrag(null);
    setDragHover(null);
    onDragActiveChange?.(false);
    if (!commit || !runtime.hover) {
      return;
    }
    const row = rows[runtime.hover.rowIndex];
    const day = days[runtime.hover.dayIndex];
    if (row && day) {
      commitPlacement(runtime.source, row, day);
    }
  };

  const releaseDrag = (x: number, y: number) => {
    if (Number.isFinite(x) && Number.isFinite(y)) {
      moveDrag(x, y);
    }
    finishDrag(true);
  };

  dragApiRef.current = { move: moveDrag, release: releaseDrag, finish: finishDrag, tick: tickDragAutoScroll };

  const beginDrag = (variant: GridVariant, source: PlacementSource) => {
    if (isBusy || placement || dragRuntimeRef.current) {
      return;
    }
    const point = { ...lastTouchRef.current };
    const runtime: DragRuntime = {
      source,
      variant,
      granted: false,
      measured: false,
      contentX: 0,
      contentY: 0,
      scrollX0: scrollXRef.current[variant],
      wrapperX: 0,
      wrapperY: 0,
      point,
      hover: null,
      edgeSince: null,
      timer: setInterval(() => dragApiRef.current.tick(), DRAG_AUTO_SCROLL_INTERVAL_MS),
    };
    dragRuntimeRef.current = runtime;
    setEditingTarget(null);
    setDrag({
      source,
      variant,
      label: `${normalizeClockTime(source.startTime)}–${normalizeClockTime(source.endTime)}`,
    });
    onDragActiveChange?.(true);
    // 先量外层容器（定位浮标），再量日期区内容（命中测试）；两次回调都到了才能算落点
    wrapperRefs.current[variant]?.measure((_x, _y, _width, _height, pageX, pageY) => {
      runtime.wrapperX = pageX;
      runtime.wrapperY = pageY;
      moveDrag(runtime.point.x, runtime.point.y);
    });
    contentRefs.current[variant]?.measure((_x, _y, _width, _height, pageX, pageY) => {
      runtime.contentX = pageX;
      runtime.contentY = pageY;
      runtime.scrollX0 = scrollXRef.current[variant];
      runtime.measured = true;
      updateDragHover();
    });
  };

  // 长按有班次的格子：拿起这条班次移动到别的格子（一格多班次、请假、空格不拖，仍走点开编辑器）
  const handleCellLongPress = (variant: GridVariant, cell: ScheduleGridCell) => {
    if (!storeCode || isBusy || placement) {
      return;
    }
    const verdict = getDragStartVerdict(cell, isLocked);
    if (verdict.state === "none") {
      return;
    }
    if (verdict.state === "locked") {
      Alert.alert(
        t(
          punchScheduleGuids.has(cell.schedules[0].scheduleGuid)
            ? "scheduleManagement.placement.lockedNotice"
            : "scheduleManagement.placement.lockedUnknownNotice",
        ),
      );
      return;
    }
    const { schedule } = verdict;
    beginDrag(variant, {
      kind: "move",
      schedule,
      startTime: normalizeClockTime(schedule.startTime),
      endTime: normalizeClockTime(schedule.endTime),
    });
  };

  const handlePresetLongPress = (variant: GridVariant, preset: (typeof SHIFT_PRESETS)[number]) => {
    if (!storeCode || isBusy || placement) {
      return;
    }
    beginDrag(variant, { kind: "preset", startTime: preset.startTime, endTime: preset.endTime });
  };

  // 组件卸载时收尾，别让定时器和「整页锁滚动」残留
  useEffect(
    () => () => {
      const runtime = dragRuntimeRef.current;
      if (runtime?.timer) {
        clearInterval(runtime.timer);
      }
      if (runtime) {
        dragRuntimeRef.current = null;
        onDragActiveChange?.(false);
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [],
  );

  const openBatch = (target: BatchTarget) => {
    if (!storeCode) {
      return;
    }
    setEditingTarget(null);
    setBatchTarget(target);
    setBatchSelection(new Set());
    setForm(EMPTY_FORM);
  };

  const cellOf = (row: ScheduleRow, day: string) =>
    classifyScheduleGridCell(
      row.schedules.filter((item) => item.workDate.slice(0, 10) === day),
      availabilityMap.get(availabilityKey(row.userGuid, day)),
    );

  // 批量候选：员工模式列本周 7 天，日期模式列表内员工；已有班次或请假的格子置灰不可选。
  const getBatchOptions = () => {
    if (!batchTarget) {
      return [];
    }
    if (batchTarget.mode === "employee") {
      const row = rows.find((item) => item.userGuid === batchTarget.userGuid);
      return days.map((day, index) => ({
        key: day,
        label: `${t(`weekdays.${index}`)} ${shortDate(day)}`,
        enabled: Boolean(row) && isBatchSchedulableCell(cellOf(row!, day)),
      }));
    }
    return rows.map((row) => ({
      key: row.userGuid,
      label: row.employeeName || row.userGuid,
      enabled: isBatchSchedulableCell(cellOf(row, batchTarget.workDate)),
    }));
  };
  const batchOptions = getBatchOptions();
  const batchEnabledKeys = batchOptions.filter((item) => item.enabled).map((item) => item.key);
  const batchSelectedKeys = batchEnabledKeys.filter((key) => batchSelection.has(key));

  const toggleBatchKey = (key: string) =>
    setBatchSelection((current) => {
      const next = new Set(current);
      if (next.has(key)) {
        next.delete(key);
      } else {
        next.add(key);
      }
      return next;
    });

  // 批量新建一律为草稿，与单个新建一致；只提交可排的选中项。
  const submitBatch = () => {
    if (!batchTarget || !storeCode || !batchSelectedKeys.length) {
      return;
    }
    const shift = {
      startTime: form.startTime.trim(),
      endTime: form.endTime.trim(),
      status: "Draft" as AttendanceScheduleStatus,
      remark: form.remark.trim() || undefined,
      mealBreakCount: form.mealBreakCount,
      storeCode,
    };
    onCreateBatch(
      batchTarget.mode === "employee"
        ? batchSelectedKeys.map((workDate) => ({ ...shift, userGuid: batchTarget.userGuid, workDate }))
        : batchSelectedKeys.map((userGuid) => ({ ...shift, userGuid, workDate: batchTarget.workDate })),
    );
    closeEditor();
  };

  // 提交逻辑沿用原规则：新建一律为草稿，编辑保留原状态。
  const submit = () => {
    if (!editingTarget || !storeCode) {
      return;
    }

    const normalized = {
      workDate: editingTarget.workDate,
      startTime: form.startTime.trim(),
      endTime: form.endTime.trim(),
      status: form.status,
      remark: form.remark.trim() || undefined,
    };

    if (editingTarget.schedule?.scheduleGuid) {
      onUpdate(editingTarget.schedule.scheduleGuid, {
        ...normalized,
        // 指定了次数就覆盖；回到默认时显式要求后端置空。不传两者时后端保持原值（兼容旧客户端）。
        mealBreakCount: form.mealBreakCount,
        resetMealBreakCount: form.mealBreakCount == null,
      });
    } else {
      onCreate({
        workDate: normalized.workDate,
        startTime: normalized.startTime,
        endTime: normalized.endTime,
        status: "Draft",
        remark: normalized.remark,
        mealBreakCount: form.mealBreakCount,
        storeCode,
        userGuid: editingTarget.userGuid,
      });
    }
    closeEditor();
  };

  const confirmDelete = (target?: AttendanceSchedule) => {
    const scheduleGuid = (target ?? editingTarget?.schedule)?.scheduleGuid;
    if (!scheduleGuid) {
      return;
    }

    Alert.alert(
      t("scheduleManagement.deleteTitle"),
      t("scheduleManagement.deleteMessage"),
      [
        { text: t("common:actions.cancel"), style: "cancel" },
        {
          text: t("scheduleManagement.deleteConfirm"),
          style: "destructive",
          onPress: () => {
            onDelete(scheduleGuid);
            closeEditor();
          },
        },
      ],
    );
  };

  const confirmCopyPreviousWeek = () => {
    Alert.alert(
      t("scheduleManagement.copyConfirmTitle"),
      t("scheduleManagement.copyConfirmMessage"),
      [
        { text: t("common:actions.cancel"), style: "cancel" },
        {
          text: t("scheduleManagement.copyConfirm"),
          onPress: onCopyPreviousWeek,
        },
      ],
    );
  };

  const canSubmit = Boolean(form.startTime.trim() && form.endTime.trim());
  // 编辑器显示的用餐次数：店长指定值优先，否则按当前起止时间（不扣用餐的总时长）推算默认次数，调时间时实时变化。
  const mealBreakCount = form.mealBreakCount
    ?? defaultMealBreakCount(shiftEditorMinutes(form.startTime, form.endTime, 0));
  const canOperateWeek = Boolean(storeCode) && !isBusy;
  const editorTitle = batchTarget
    ? batchTarget.mode === "employee"
      ? batchTarget.employeeName || batchTarget.userGuid
      : `${t(`weekdays.${batchTarget.weekdayIndex}`)} ${shortDate(batchTarget.workDate)}`
    : editingTarget
      ? `${editingTarget.employeeName || editingTarget.userGuid} · ${t(`weekdays.${editingTarget.weekdayIndex}`)} ${shortDate(editingTarget.workDate)}`
      : "";
  const editorSubtitle = batchTarget
    ? t(batchTarget.mode === "employee"
      ? "scheduleManagement.batch.employeeSubtitle"
      : "scheduleManagement.batch.dateSubtitle")
    : editingTarget?.schedule
      ? t("scheduleManagement.editTitle")
      : t("scheduleManagement.createTitle");
  const isEditorOpen = Boolean(editingTarget || batchTarget);

  const renderStats = (compact = false) => {
    const items = [
      { key: "week", label: t("scheduleManagement.stats.weekHours"), value: hoursShort(hourStats.totalMinutes) },
      { key: "weekday", label: t("scheduleManagement.stats.weekdayHours"), value: hoursShort(hourStats.weekdayMinutes) },
      { key: "weekend", label: t("scheduleManagement.stats.weekendHours"), value: hoursShort(hourStats.weekendMinutes) },
      // 等效工时：周六 ×1.25、周日 ×1.5，便于估算周末加价后的人工成本。
      { key: "equivalent", label: t("scheduleManagement.stats.equivalentHours"), value: hoursShort(hourStats.equivalentMinutes) },
      {
        key: "people",
        label: t("scheduleManagement.stats.scheduledPeople"),
        value: `${scheduledPeople}/${rows.length}`,
      },
    ];
    return (
      <View style={[styles.statStrip, compact ? styles.statStripCompact : null]}>
        {items.map((item, index) => (
          <View
            key={item.key}
            style={[styles.statItem, index > 0 ? styles.statDivider : null]}
          >
            <Text variant="labelSmall" style={styles.muted} numberOfLines={1}>
              {item.label}
            </Text>
            <Text
              style={[styles.statValue, index === 0 ? styles.statValuePrimary : null]}
              numberOfLines={1}
              adjustsFontSizeToFit
              minimumFontScale={0.8}
            >
              {item.value}
            </Text>
          </View>
        ))}
      </View>
    );
  };

  const renderLegend = () => (
    <View style={styles.legend}>
      <View style={[styles.legendSwatch, styles.legendShift]} />
      <Text variant="labelSmall" style={styles.muted}>
        {t("scheduleManagement.legend.shift")}
      </Text>
      <View style={[styles.legendSwatch, styles.legendShift, styles.shiftChipDraft]} />
      <Text variant="labelSmall" style={styles.muted}>
        {t("scheduleManagement.legend.draft")}
      </Text>
      <View style={[styles.legendSwatch, styles.legendLeave]} />
      <Text variant="labelSmall" style={styles.muted}>
        {t("scheduleManagement.legend.leave")}
      </Text>
      <View style={[styles.legendSwatch, styles.legendAvailable]} />
      <Text variant="labelSmall" style={styles.muted}>
        {t("scheduleManagement.legend.available")}
      </Text>
      <View style={[styles.legendSwatch, styles.legendUnavailable]} />
      <Text variant="labelSmall" style={styles.muted}>
        {t("scheduleManagement.legend.unavailable")}
      </Text>
      {EMPLOYMENT_TYPE_LEGEND.map((item) => (
        <View key={item.code} style={styles.legendType}>
          <EmploymentTypeBadge code={item.code} />
          <Text variant="labelSmall" style={styles.muted}>
            {t(`scheduleManagement.legend.${item.labelKey}`)}
          </Text>
        </View>
      ))}
    </View>
  );

  // 全屏横屏宽度够，图例与状态同一行；竖屏卡片里图例放到网格下方。
  const renderStatusLine = (withLegend = false) => (
    <View style={styles.statusLine}>
      <StatusPill
        label={t(`scheduleManagement.publishState.${publishState}`)}
        tone={PUBLISH_STATE_TONES[publishState]}
      />
      {canExportImage ? (
        <Button
          compact
          mode="text"
          icon="image-outline"
          onPress={() => setIsExportOpen(true)}
          disabled={isBusy}
        >
          {t("scheduleManagement.export.action")}
        </Button>
      ) : null}
      {uncoveredDays > 0 && rows.length > 0 ? (
        <Text variant="labelSmall" style={styles.uncoveredText}>
          {t("scheduleManagement.uncoveredDays", { count: uncoveredDays })}
        </Text>
      ) : null}
      {otherManagerCount > 0 ? (
        <Chip
          compact
          selected={showOtherManagers}
          showSelectedCheck
          onPress={() => setShowOtherManagers((current) => !current)}
        >
          {t("scheduleManagement.showOtherManagers", { count: otherManagerCount })}
        </Chip>
      ) : null}
      {withLegend ? <View style={styles.legendPush}>{renderLegend()}</View> : null}
    </View>
  );

  const renderCellContent = (cell: ScheduleGridCell) => {
    if (cell.kind === "leave") {
      return (
        <View style={[styles.cellChip, styles.leaveChip]}>
          <Text style={styles.leaveText} numberOfLines={1}>
            {t("scheduleManagement.cell.leave")}
          </Text>
        </View>
      );
    }
    if (cell.kind === "shift") {
      const [first] = cell.schedules;
      const isDraft = cell.schedules.some(
        (item) => item.status.toLowerCase() === "draft",
      );
      const hasMinorFindings = cell.schedules.some(
        (item) => item.minorCompliance?.hasFindings,
      );
      const hasAvailabilityConflict = cell.schedules.some(
        (item) => !item.leaveType && getAvailabilityConflict(item, cell.availability),
      );
      // 用餐小标记：手动指定的（含 0）醒目，按时长自动扣的（≥4.5h 1 次、>9h 2 次）弱化显示，合计为 0 的自动班次不显示
      const mealBadge = getCellMealBadge(cell.schedules);
      return (
        <View style={[styles.cellChip, styles.shiftChip, isDraft ? styles.shiftChipDraft : null]}>
          <Text
            style={styles.shiftText}
            numberOfLines={1}
            adjustsFontSizeToFit
            minimumFontScale={0.75}
          >
            {formatShiftShort(first.startTime, first.endTime)}
          </Text>
          {cell.schedules.length > 1 ? (
            <Text style={styles.moreText}>+{cell.schedules.length - 1}</Text>
          ) : null}
          {mealBadge ? (
            <Text style={[styles.mealBadge, mealBadge.manual ? null : styles.mealBadgeAuto]}>
              {t("scheduleManagement.cell.mealBadge", { count: mealBadge.count })}
            </Text>
          ) : null}
          {/* 与员工上报的不能上班 / 可上班时间冲突：只提醒不拦截，小三角标记 */}
          {hasAvailabilityConflict ? (
            <Text style={styles.conflictMark} accessibilityLabel={t("scheduleManagement.cell.conflictA11y")}>⚠︎</Text>
          ) : null}
          {/* 未成年用工合规只提醒不拦截：格子里用小标记提示店长，详情在提醒待办里看。 */}
          {hasMinorFindings ? (
            <Text style={styles.minorWarning} accessibilityLabel={t("minorCompliance.cellWarning")}>!</Text>
          ) : null}
        </View>
      );
    }
    if (cell.kind === "available") {
      return (
        <View style={[styles.cellChip, styles.availableChip]}>
          <Text style={styles.availableText} numberOfLines={1}>
            {t("scheduleManagement.cell.available")}
          </Text>
        </View>
      );
    }
    if (cell.kind === "unavailable") {
      return (
        <View style={[styles.cellChip, styles.unavailableChip]}>
          <Text style={styles.unavailableText} numberOfLines={1}>
            {t("scheduleManagement.cell.unavailable")}
          </Text>
        </View>
      );
    }
    return null;
  };

  // 放置模式的提示条与常用班次托盘：拿起后点格子放下；没拿起时显示「添加班次」托盘
  const renderPlacementBar = (variant: GridVariant) => {
    if (!storeCode || !rows.length) {
      return null;
    }
    if (placement) {
      const shift = `${normalizeClockTime(placement.startTime)}–${normalizeClockTime(placement.endTime)}`;
      const key = placement.kind === "move" ? "bannerMove" : placement.kind === "copy" ? "bannerCopy" : "bannerAdd";
      return (
        <View style={styles.placementBar}>
          <Text variant="labelLarge" style={styles.placementBarText}>
            {t(`scheduleManagement.placement.${key}`, { shift })}
          </Text>
          <Button compact mode="text" onPress={() => setPlacement(null)}>
            {t("scheduleManagement.placement.cancel")}
          </Button>
        </View>
      );
    }
    return (
      <View style={styles.trayBar}>
        <Text variant="labelMedium" style={styles.muted}>
          {t("scheduleManagement.placement.trayTitle")}
        </Text>
        <View style={styles.chipRow}>
          {SHIFT_PRESETS.map((preset) => (
            <Chip
              key={`${preset.startTime}-${preset.endTime}`}
              compact
              disabled={isBusy}
              onPress={() => startPresetPlacement(preset)}
              delayLongPress={DRAG_LONG_PRESS_MS}
              onLongPress={() => handlePresetLongPress(variant, preset)}
            >
              {`${preset.startTime}–${preset.endTime}`}
            </Chip>
          ))}
        </View>
        <Text variant="labelSmall" style={styles.muted}>
          {t("scheduleManagement.placement.trayHint")}
        </Text>
      </View>
    );
  };

  // 拖动时跟手的小浮标：显示在手指上方，位置用 Animated.Value 直接驱动，不触发整表重渲染
  const renderDragGhost = (variant: GridVariant) =>
    drag && drag.variant === variant ? (
      <Animated.View
        pointerEvents="none"
        style={[styles.dragGhost, { transform: dragGhost.getTranslateTransform() }]}
      >
        <Text variant="labelLarge" style={styles.dragGhostText}>
          {drag.label}
        </Text>
      </Animated.View>
    ) : null;

  // 外层容器负责长按拖动的手势：记录最后触点（长按回调拿不到坐标）、接管拖动、没拖动就抬手时收尾
  const renderGrid = (variant: GridVariant) => (
    <View
      ref={(node) => {
        wrapperRefs.current[variant] = node;
      }}
      collapsable={false}
      style={styles.dragRoot}
      {...dragPanResponder.panHandlers}
      onTouchStart={(event) => {
        lastTouchRef.current = { x: event.nativeEvent.pageX, y: event.nativeEvent.pageY };
      }}
      onTouchMove={(event) => {
        lastTouchRef.current = { x: event.nativeEvent.pageX, y: event.nativeEvent.pageY };
      }}
      onTouchEnd={() => {
        if (dragRuntimeRef.current && !dragRuntimeRef.current.granted) finishDrag(false);
      }}
      onTouchCancel={() => {
        if (dragRuntimeRef.current && !dragRuntimeRef.current.granted) finishDrag(false);
      }}
    >
      {renderPlacementBar(variant)}
      {renderGridBody(variant)}
      {renderDragGhost(variant)}
    </View>
  );

  const renderGridBody = (variant: GridVariant) => {
    const width = gridWidths[variant];
    const cellWidth = Math.max(
      MIN_CELL_WIDTH,
      Math.floor((width - ROW_HEADER_WIDTH - 2) / 7),
    );
    return (
      <View
        onLayout={(event) => {
          const next = Math.round(event.nativeEvent.layout.width);
          setGridWidths((current) =>
            current[variant] === next ? current : { ...current, [variant]: next },
          );
        }}
      >
        {/* 姓名列固定在左侧，只有日期区横向滚动，滑到周末时仍能看清是谁的班。 */}
        <View style={[styles.grid, styles.gridRow]}>
          <View>
            <View style={[styles.headerCell, styles.rowHeader]}>
              <Text variant="labelSmall" style={styles.muted} numberOfLines={2}>
                {t("scheduleManagement.employeeColumn")}
              </Text>
            </View>
            {rows.map((row) => (
              // 点员工名：给该员工批量排多天。
              <Pressable
                key={row.userGuid}
                accessibilityRole="button"
                accessibilityLabel={t("scheduleManagement.batch.employeeA11y", {
                  name: row.employeeName || row.userGuid,
                })}
                disabled={!storeCode || isBusy || Boolean(placement)}
                onPress={() =>
                  openBatch({ mode: "employee", userGuid: row.userGuid, employeeName: row.employeeName })
                }
                style={({ pressed }) => [
                  styles.bodyCell,
                  styles.rowHeader,
                  pressed ? styles.cellPressed : null,
                ]}
              >
                <Text variant="labelLarge" numberOfLines={1} style={styles.employeeName}>
                  {row.employeeName || row.userGuid}
                </Text>
                <View style={styles.rowMeta}>
                  <Text variant="labelSmall" style={styles.muted}>
                    {hoursShort(rowMinutes.get(row.userGuid) ?? 0)}
                  </Text>
                  {row.employmentType ? <EmploymentTypeBadge code={row.employmentType} /> : null}
                  {row.isStoreManager ? (
                    <StatusPill label={t("scheduleManagement.managerBadge")} tone="neutral" />
                  ) : null}
                  {row.age !== undefined ? (
                    <StatusPill label={t("scheduleManagement.ageBadge", { age: row.age })} tone="warning" />
                  ) : null}
                </View>
              </Pressable>
            ))}
          </View>
          <ScrollView
            ref={(node) => {
              scrollRefs.current[variant] = node;
            }}
            horizontal
            showsHorizontalScrollIndicator={false}
            // 拖动中锁住日期区自己的滚动（改由靠边自动滚动）
            scrollEnabled={!drag}
            scrollEventThrottle={16}
            onScroll={(event) => {
              scrollXRef.current[variant] = event.nativeEvent.contentOffset.x;
            }}
          >
            <View
              ref={(node) => {
                contentRefs.current[variant] = node;
              }}
              collapsable={false}
            >
              <View style={styles.gridRow}>
                {days.map((day, index) => {
                  const isToday = day === today;
                  return (
                    // 点日期：给当天多位员工批量排同一班。
                    <Pressable
                      key={day || index}
                      accessibilityRole="button"
                      accessibilityLabel={t("scheduleManagement.batch.dateA11y", {
                        date: `${t(`weekdays.${index}`)} ${shortDate(day)}`,
                      })}
                      disabled={!storeCode || isBusy || !rows.length || Boolean(placement)}
                      onPress={() => openBatch({ mode: "date", workDate: day, weekdayIndex: index })}
                      style={({ pressed }) => [
                        styles.headerCell,
                        styles.dayHeader,
                        { width: cellWidth },
                        isToday ? styles.todayHeader : null,
                        pressed ? styles.cellPressed : null,
                      ]}
                    >
                      <Text
                        variant="labelMedium"
                        style={isToday ? styles.todayHeaderText : styles.dayHeaderText}
                      >
                        {t(`weekdays.${index}`)}
                      </Text>
                      <Text
                        variant="labelSmall"
                        style={isToday ? styles.todayHeaderText : styles.muted}
                      >
                        {shortDate(day)}
                      </Text>
                    </Pressable>
                  );
                })}
              </View>

              {rows.map((row, rowIndex) => (
                <View key={row.userGuid} style={styles.gridRow}>
                  {days.map((day, index) => {
                    const cell = classifyScheduleGridCell(
                      row.schedules.filter((item) => item.workDate.slice(0, 10) === day),
                      availabilityMap.get(availabilityKey(row.userGuid, day)),
                    );
                    const verdict = placementVerdictOf(row, day);
                    return (
                      <Pressable
                        key={`${row.userGuid}-${day}`}
                        accessibilityRole="button"
                        accessibilityLabel={`${row.employeeName || row.userGuid} ${t(`weekdays.${index}`)} ${shortDate(day)}`}
                        disabled={!storeCode || isBusy}
                        onPress={() =>
                          placement ? handlePlacementPress(row, day) : openCell(row, day, index, cell)
                        }
                        delayLongPress={DRAG_LONG_PRESS_MS}
                        onLongPress={() => handleCellLongPress(variant, cell)}
                        style={({ pressed }) => [
                          styles.bodyCell,
                          styles.dayCell,
                          { width: cellWidth },
                          day === today ? styles.todayColumn : null,
                          // 放置 / 拖动模式：可放蓝 / 提醒琥珀 / 不能放红，与 Web 拖拽的落点提示一致
                          verdict?.state === "ok" ? styles.placementOk : null,
                          verdict?.state === "warn" ? styles.placementWarn : null,
                          verdict?.state === "bad" ? styles.placementBad : null,
                          // 拖动时手指正下方的格子加粗描边
                          drag && dragHover?.rowIndex === rowIndex && dragHover.dayIndex === index
                            ? styles.dragHoverCell
                            : null,
                          pressed ? styles.cellPressed : null,
                        ]}
                      >
                        {renderCellContent(cell)}
                      </Pressable>
                    );
                  })}
                </View>
              ))}
            </View>
          </ScrollView>
        </View>
      </View>
    );
  };

  const renderScheduleArea = (variant: GridVariant) => {
    if (!storeCode) {
      return (
        <Text variant="bodyMedium" style={styles.muted}>
          {t("scheduleManagement.noStore")}
        </Text>
      );
    }

    if (isLoading) {
      return (
        <Text variant="bodyMedium" style={styles.muted}>
          {t("common:loading")}
        </Text>
      );
    }

    if (!rows.length) {
      return (
        <Text variant="bodyMedium" style={styles.muted}>
          {t("scheduleManagement.noEmployees")}
        </Text>
      );
    }

    return renderGrid(variant);
  };

  const renderWeekActions = (compact = false) => (
    <>
      <Button
        compact={compact}
        mode="outlined"
        icon="content-copy"
        onPress={confirmCopyPreviousWeek}
        loading={isCopying}
        disabled={!canOperateWeek}
        style={compact ? null : styles.secondaryAction}
      >
        {t("scheduleManagement.copyPreviousWeek")}
      </Button>
      <Button
        compact={compact}
        mode="contained"
        icon="send-clock-outline"
        onPress={onPublishWeek}
        loading={isBusy && !isCopying}
        disabled={!canOperateWeek}
        buttonColor={HB_COLORS.action}
        textColor={HB_COLORS.white}
        style={compact ? null : styles.primaryAction}
      >
        {t("actions.publishWeek")}
      </Button>
    </>
  );

  // 班次设置（常用时间段、起止时间、用餐、备注）：单个编辑与批量排班共用。
  const renderShiftFields = () => (
    <>
        <View style={styles.editorSection}>
          <Text variant="labelMedium" style={styles.muted}>
            {t("scheduleManagement.quickShiftTitle")}
          </Text>
          <View style={styles.chipRow}>
            {SHIFT_PRESETS.map((shift) => (
              <Chip
                key={`${shift.startTime}-${shift.endTime}`}
                compact
                selected={
                  form.startTime === shift.startTime &&
                  form.endTime === shift.endTime
                }
                onPress={() =>
                  setForm((current) => ({
                    ...current,
                    startTime: shift.startTime,
                    endTime: shift.endTime,
                  }))
                }
              >
                {`${shift.startTime}–${shift.endTime}`}
              </Chip>
            ))}
          </View>
        </View>

        <View style={styles.timeRow}>
          <TimeStepField
            label={t("fields.startTime")}
            value={form.startTime}
            minusLabel={t("scheduleManagement.editor.stepMinus", { label: t("fields.startTime") })}
            plusLabel={t("scheduleManagement.editor.stepPlus", { label: t("fields.startTime") })}
            disabled={isBusy}
            onChange={(value) =>
              setForm((current) => ({ ...current, startTime: value }))
            }
          />
          <TimeStepField
            label={t("fields.endTime")}
            value={form.endTime}
            minusLabel={t("scheduleManagement.editor.stepMinus", { label: t("fields.endTime") })}
            plusLabel={t("scheduleManagement.editor.stepPlus", { label: t("fields.endTime") })}
            disabled={isBusy}
            onChange={(value) =>
              setForm((current) => ({ ...current, endTime: value }))
            }
          />
        </View>

        <MealBreakStepField
          label={t("scheduleManagement.editor.mealBreakLabel")}
          count={mealBreakCount}
          isDefault={form.mealBreakCount == null}
          valueText={mealBreakCount
            ? t("scheduleManagement.editor.mealBreakValue", {
                count: mealBreakCount,
                minutes: MEAL_BREAK_MINUTES,
              })
            : t("scheduleManagement.editor.mealBreakNone")}
          defaultText={t("scheduleManagement.editor.mealBreakDefault")}
          resetText={t("scheduleManagement.editor.mealBreakReset")}
          minusLabel={t("scheduleManagement.editor.mealBreakMinus")}
          plusLabel={t("scheduleManagement.editor.mealBreakPlus")}
          disabled={isBusy}
          onChange={(count) =>
            setForm((current) => ({ ...current, mealBreakCount: count }))
          }
          onReset={() =>
            setForm((current) => ({ ...current, mealBreakCount: null }))
          }
        />

        <TextInput
          mode="outlined"
          dense
          label={t("fields.note")}
          value={form.remark}
          onChangeText={(value) =>
            setForm((current) => ({ ...current, remark: value }))
          }
        />
    </>
  );

  const renderBatchBody = () => {
    if (!batchTarget) {
      return null;
    }
    const allSelected =
      batchEnabledKeys.length > 0 && batchSelectedKeys.length === batchEnabledKeys.length;
    return (
      <>
        <View style={styles.editorSection}>
          <View style={styles.mealLabelRow}>
            <Text variant="labelMedium" style={styles.muted}>
              {t(batchTarget.mode === "employee"
                ? "scheduleManagement.batch.pickDays"
                : "scheduleManagement.batch.pickEmployees")}
            </Text>
            {batchEnabledKeys.length ? (
              <Pressable
                accessibilityRole="button"
                hitSlop={8}
                onPress={() =>
                  setBatchSelection(allSelected ? new Set() : new Set(batchEnabledKeys))
                }
              >
                <Text variant="labelMedium" style={styles.stepButtonText}>
                  {t(allSelected ? "scheduleManagement.batch.clearAll" : "scheduleManagement.batch.selectAll")}
                </Text>
              </Pressable>
            ) : null}
          </View>
          <View style={styles.chipRow}>
            {batchOptions.map((option) => (
              <Chip
                key={option.key}
                compact
                showSelectedCheck
                selected={option.enabled && batchSelection.has(option.key)}
                disabled={!option.enabled}
                onPress={() => toggleBatchKey(option.key)}
              >
                {option.label}
              </Chip>
            ))}
          </View>
          <Text variant="labelSmall" style={styles.muted}>
            {t(batchTarget.mode === "employee"
              ? "scheduleManagement.batch.skipDaysHint"
              : "scheduleManagement.batch.skipEmployeesHint")}
          </Text>
        </View>
        {renderShiftFields()}
      </>
    );
  };

  const renderEditorBody = () => {
    if (!editingTarget) {
      return null;
    }
    const dayAvailability =
      availabilityMap.get(
        availabilityKey(editingTarget.userGuid, editingTarget.workDate),
      ) ?? [];
    const leaveSchedule = editingTarget.daySchedules.find(
      (item) => item.leaveType,
    );
    const currentGuid = editingTarget.schedule?.scheduleGuid;
    const row = rows.find((item) => item.userGuid === editingTarget.userGuid);
    // 正在编辑的时间与员工上报的冲突：只提醒，不拦截保存
    const editorConflict = getAvailabilityConflict(
      { startTime: form.startTime, endTime: form.endTime },
      dayAvailability,
    );
    const editingLocked = editingTarget.schedule ? isLocked(editingTarget.schedule) : false;

    return (
      <>
        <View style={styles.infoBlock}>
          {leaveSchedule?.leaveType ? (
            <Text variant="labelMedium" style={styles.leaveNotice}>
              {t("scheduleManagement.editor.leaveNotice", {
                type: t(`leaveTypes.${leaveSchedule.leaveType}`, leaveSchedule.leaveType),
              })}
            </Text>
          ) : null}
          <View style={styles.infoRow}>
            <Text variant="bodySmall" style={styles.muted}>
              {t("scheduleManagement.editor.availabilityLabel")}
            </Text>
            <Text variant="bodyMedium" style={styles.infoValue}>
              {describeAvailability(dayAvailability)}
            </Text>
          </View>
          {editorConflict ? (
            <Text variant="labelMedium" style={styles.conflictNotice}>
              {t(editorConflict === "unavailable"
                ? "scheduleManagement.editor.conflictUnavailable"
                : "scheduleManagement.editor.conflictOutside")}
            </Text>
          ) : null}
          <View style={styles.infoRow}>
            <Text variant="bodySmall" style={styles.muted}>
              {t("scheduleManagement.editor.weekHoursLabel")}
            </Text>
            <Text variant="bodyMedium" style={styles.infoValue}>
              {t("scheduleManagement.editor.hoursValue", {
                hours: formatScheduleHours(rowMinutes.get(editingTarget.userGuid) ?? 0),
              })}
            </Text>
          </View>
        </View>

        {editingTarget.daySchedules.length && row ? (
          // 同一天的班次都列在这里切换；每个班次块右侧的「×」直接删除（先确认）；也可以给同一员工再加一班。
          <View style={styles.chipRow}>
            {editingTarget.daySchedules.map((item) => (
              <Chip
                key={item.scheduleGuid}
                compact
                selected={item.scheduleGuid === currentGuid}
                onPress={() =>
                  openEdit(item, row, editingTarget.weekdayIndex, editingTarget.daySchedules)
                }
                onClose={() => confirmDelete(item)}
                closeIconAccessibilityLabel={t("scheduleManagement.placement.closeA11y", {
                  shift: formatShiftShort(item.startTime, item.endTime),
                })}
              >
                {formatShiftShort(item.startTime, item.endTime)}
              </Chip>
            ))}
            <Chip
              compact
              icon="plus"
              selected={!editingTarget.schedule}
              onPress={() =>
                openCreate(row, editingTarget.workDate, editingTarget.weekdayIndex, editingTarget.daySchedules)
              }
            >
              {t("scheduleManagement.editor.addShift")}
            </Chip>
          </View>
        ) : null}

        {editingTarget.schedule ? (
          // 移动 / 复制：不在手机上拖动（要新增原生手势库），改成点选目标格；已有打卡的班次不能移动或复制
          <View style={styles.placementActions}>
            <Button
              mode="outlined"
              icon="arrow-right-bold-outline"
              compact
              disabled={editingLocked || isBusy}
              onPress={() => startPlacement("move", editingTarget.schedule!)}
            >
              {t("scheduleManagement.placement.moveAction")}
            </Button>
            <Button
              mode="outlined"
              icon="content-copy"
              compact
              disabled={editingLocked || isBusy}
              onPress={() => startPlacement("copy", editingTarget.schedule!)}
            >
              {t("scheduleManagement.placement.copyAction")}
            </Button>
          </View>
        ) : null}
        {editingLocked ? (
          <Text variant="labelMedium" style={styles.muted}>
            {t(punchesKnown
              ? "scheduleManagement.placement.lockedNotice"
              : "scheduleManagement.placement.lockedUnknownNotice")}
          </Text>
        ) : null}

        {renderShiftFields()}
      </>
    );
  };

  const renderEditorFooter = () => batchTarget ? (
    <View style={styles.editorFooter}>
      <Button
        mode="contained"
        onPress={submitBatch}
        disabled={!canSubmit || !batchSelectedKeys.length || isBusy}
        loading={isBusy}
        buttonColor={HB_COLORS.action}
        textColor={HB_COLORS.white}
        style={styles.primaryAction}
      >
        {t("scheduleManagement.batch.save", {
          count: batchSelectedKeys.length,
          hours: formatScheduleHours(
            shiftEditorMinutes(form.startTime, form.endTime, form.mealBreakCount),
          ),
        })}
      </Button>
    </View>
  ) : (
    <View style={styles.editorFooter}>
      {editingTarget?.schedule ? (
        <Button
          mode="outlined"
          textColor={HB_COLORS.danger}
          onPress={() => confirmDelete()}
          disabled={isBusy}
        >
          {t("scheduleManagement.editor.setRest")}
        </Button>
      ) : null}
      <Button
        mode="contained"
        onPress={submit}
        disabled={!canSubmit || isBusy}
        loading={isBusy}
        buttonColor={HB_COLORS.action}
        textColor={HB_COLORS.white}
        style={styles.primaryAction}
      >
        {t("scheduleManagement.editor.save", {
          hours: formatScheduleHours(
            shiftEditorMinutes(form.startTime, form.endTime, form.mealBreakCount),
          ),
        })}
      </Button>
    </View>
  );

  const exportSheetProps = {
    days,
    rows,
    storeCode,
    storeLabel,
    weekStartDate,
    weekLabel: `${weekNumberLabel} · ${weekRangeLabel}`,
    onDismiss: () => setIsExportOpen(false),
  };

  return (
    <>
      <Card mode="outlined" style={styles.card}>
        <Card.Title
          title={t("sections.scheduleManagement")}
          subtitle={storeLabel}
          right={(props) => (
            <IconButton
              {...props}
              icon="fullscreen"
              accessibilityLabel={t("scheduleManagement.fullscreen")}
              onPress={() => setIsFullscreen(true)}
            />
          )}
        />
        <Card.Content style={styles.content}>
          <View style={styles.toolbar}>
            <IconButton
              icon="chevron-left"
              mode="outlined"
              accessibilityLabel={t("scheduleManagement.previousWeek")}
              onPress={onPreviousWeek}
              disabled={isBusy}
            />
            <View style={styles.weekTitle}>
              <Text variant="titleMedium">{weekNumberLabel}</Text>
              <Text variant="bodySmall" style={styles.muted}>
                {weekRangeLabel}
              </Text>
            </View>
            <IconButton
              icon="chevron-right"
              mode="outlined"
              accessibilityLabel={t("scheduleManagement.nextWeek")}
              onPress={onNextWeek}
              disabled={isBusy}
            />
          </View>

          {renderStats()}
          {renderStatusLine()}
          {renderScheduleArea("card")}
          {storeCode && rows.length ? (
            <View style={styles.gridFooter}>
              {renderLegend()}
              <Text variant="labelSmall" style={styles.hint}>
                {t("scheduleManagement.gridHint")}
              </Text>
            </View>
          ) : null}

          <View style={styles.weekActions}>{renderWeekActions()}</View>
        </Card.Content>
      </Card>

      <BusinessSheet
        visible={isEditorOpen && !isFullscreen}
        title={editorTitle}
        subtitle={editorSubtitle}
        onDismiss={closeEditor}
        footer={renderEditorFooter()}
      >
        {batchTarget ? renderBatchBody() : renderEditorBody()}
      </BusinessSheet>

      <ScheduleExportSheet
        visible={isExportOpen && canExportImage && !isFullscreen}
        presentation="sheet"
        {...exportSheetProps}
      />

      <Modal
        animationType="slide"
        onRequestClose={() => {
          if (isExportOpen) {
            setIsExportOpen(false);
            return;
          }
          if (isEditorOpen) {
            closeEditor();
            return;
          }
          setIsFullscreen(false);
        }}
        presentationStyle="fullScreen"
        supportedOrientations={["landscape", "portrait"]}
        visible={isFullscreen}
      >
        <View
          style={[
            styles.fullscreenRoot,
            { paddingLeft: insets.left, paddingRight: insets.right },
          ]}
        >
          <View style={styles.fullscreenHeader}>
            <IconButton
              icon="close"
              accessibilityLabel={t("scheduleManagement.exitFullscreen")}
              onPress={() => setIsFullscreen(false)}
            />
            <View style={styles.fullscreenTitleBlock}>
              <Text variant="titleMedium">
                {t("scheduleManagement.fullscreenTitle")} · {weekNumberLabel}
              </Text>
              <Text variant="bodySmall" style={styles.muted}>
                {storeLabel} · {weekRangeLabel}
              </Text>
            </View>
            <View style={styles.fullscreenControls}>
              <IconButton
                icon="chevron-left"
                mode="outlined"
                size={18}
                accessibilityLabel={t("scheduleManagement.previousWeek")}
                onPress={onPreviousWeek}
                disabled={isBusy}
              />
              <IconButton
                icon="chevron-right"
                mode="outlined"
                size={18}
                accessibilityLabel={t("scheduleManagement.nextWeek")}
                onPress={onNextWeek}
                disabled={isBusy}
              />
              {renderWeekActions(true)}
            </View>
          </View>
          <ScrollView
            style={styles.fullscreenBody}
            contentContainerStyle={[
              styles.fullscreenBodyContent,
              { paddingBottom: Math.max(insets.bottom, HB_SPACING.md) },
            ]}
          >
            {renderStats(true)}
            {renderStatusLine(true)}
            {renderScheduleArea("fullscreen")}
          </ScrollView>

          {/* 横屏全屏内用右侧面板编辑：不在全屏 Modal 上再叠原生 Modal（方向与层级都会冲突）。 */}
          {isFullscreen && isEditorOpen ? (
            <View style={StyleSheet.absoluteFill}>
              <Pressable
                style={styles.panelBackdrop}
                onPress={closeEditor}
                accessible={false}
                importantForAccessibility="no"
              />
              <View
                style={[
                  styles.sidePanel,
                  { paddingRight: insets.right, paddingBottom: Math.max(insets.bottom, HB_SPACING.xs) },
                ]}
                accessibilityViewIsModal
              >
                <View style={styles.sidePanelHeader}>
                  <View style={styles.sidePanelHeading}>
                    <Text variant="titleMedium" numberOfLines={1} accessibilityRole="header">
                      {editorTitle}
                    </Text>
                    <Text variant="bodySmall" style={styles.muted}>
                      {editorSubtitle}
                    </Text>
                  </View>
                  <IconButton
                    icon="close"
                    accessibilityLabel={t("common:actions.close")}
                    onPress={closeEditor}
                  />
                </View>
                <ScrollView
                  style={styles.sidePanelScroll}
                  contentContainerStyle={styles.sidePanelBody}
                  keyboardShouldPersistTaps="handled"
                >
                  {batchTarget ? renderBatchBody() : renderEditorBody()}
                </ScrollView>
                <View style={styles.sidePanelFooter}>{renderEditorFooter()}</View>
              </View>
            </View>
          ) : null}

          <ScheduleExportSheet
            visible={isFullscreen && isExportOpen && canExportImage}
            presentation="overlay"
            {...exportSheetProps}
          />
        </View>
      </Modal>
    </>
  );
}

const styles = StyleSheet.create({
  availableChip: {
    borderColor: HB_COLORS.outline,
    borderStyle: "dashed",
    borderWidth: 1,
  },
  availableText: {
    color: "#98A2B3",
    fontSize: 12,
    fontWeight: "600",
  },
  unavailableChip: {
    backgroundColor: UNAVAILABLE.background,
  },
  unavailableText: {
    color: UNAVAILABLE.text,
    fontSize: 12,
    fontWeight: "600",
  },
  bodyCell: {
    borderColor: HB_COLORS.outlineMuted,
    borderRightWidth: StyleSheet.hairlineWidth,
    borderTopWidth: StyleSheet.hairlineWidth,
    height: CELL_HEIGHT,
  },
  card: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    elevation: 0,
  },
  cellChip: {
    alignItems: "center",
    borderRadius: 6,
    flex: 1,
    flexDirection: "row",
    gap: 2,
    justifyContent: "center",
    paddingHorizontal: 3,
  },
  cellPressed: {
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  chipRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
  },
  content: {
    gap: HB_SPACING.sm,
  },
  dayCell: {
    padding: 4,
  },
  dayHeader: {
    alignItems: "center",
  },
  dayHeaderText: {
    color: HB_COLORS.textPrimary,
  },
  editorFooter: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
  },
  editorSection: {
    gap: 6,
  },
  employeeName: {
    color: HB_COLORS.textPrimary,
  },
  rowMeta: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 4,
  },
  fullscreenBody: {
    flex: 1,
  },
  fullscreenBodyContent: {
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
  },
  fullscreenControls: {
    alignItems: "center",
    flexDirection: "row",
    gap: 6,
  },
  fullscreenHeader: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.xs,
    paddingVertical: 4,
  },
  fullscreenRoot: {
    backgroundColor: HB_COLORS.background,
    flex: 1,
  },
  fullscreenTitleBlock: {
    flex: 1,
    minWidth: 160,
  },
  grid: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.control,
    borderWidth: StyleSheet.hairlineWidth,
    overflow: "hidden",
  },
  gridRow: {
    flexDirection: "row",
  },
  headerCell: {
    backgroundColor: HB_COLORS.surfaceMuted,
    borderColor: HB_COLORS.outlineMuted,
    borderRightWidth: StyleSheet.hairlineWidth,
    // 固定表头高度，保证固定姓名列与可滚动日期区逐行对齐。
    height: HEADER_HEIGHT,
    justifyContent: "center",
    paddingHorizontal: 6,
    paddingVertical: 6,
  },
  gridFooter: {
    alignItems: "center",
    gap: 4,
  },
  hint: {
    color: HB_COLORS.textSecondary,
    textAlign: "center",
  },
  infoBlock: {
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: HB_RADIUS.control,
    gap: 6,
    padding: HB_SPACING.sm,
  },
  infoRow: {
    alignItems: "flex-start",
    flexDirection: "row",
    gap: HB_SPACING.sm,
    justifyContent: "space-between",
  },
  infoValue: {
    color: HB_COLORS.textPrimary,
    flexShrink: 1,
    fontWeight: "600",
    textAlign: "right",
  },
  leaveChip: {
    backgroundColor: LEAVE.background,
  },
  leaveNotice: {
    color: LEAVE.text,
  },
  leaveText: {
    color: LEAVE.text,
    fontSize: 12,
    fontWeight: "700",
  },
  legend: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 4,
    justifyContent: "center",
  },
  legendPush: {
    marginLeft: "auto",
  },
  legendAvailable: {
    borderColor: HB_COLORS.outline,
    borderStyle: "dashed",
    borderWidth: 1,
  },
  legendUnavailable: {
    backgroundColor: UNAVAILABLE.background,
  },
  legendLeave: {
    backgroundColor: LEAVE.background,
    borderColor: "#FEC84B",
    borderWidth: StyleSheet.hairlineWidth,
  },
  legendShift: {
    backgroundColor: ACCENT.background,
    borderColor: "#B2DDFF",
    borderWidth: StyleSheet.hairlineWidth,
  },
  legendSwatch: {
    borderRadius: 3,
    height: 10,
    marginLeft: 4,
    width: 10,
  },
  moreText: {
    color: ACCENT.text,
    fontSize: 10,
    fontWeight: "700",
  },
  muted: {
    color: HB_COLORS.textSecondary,
  },
  panelBackdrop: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: "rgba(16,24,40,0.32)",
  },
  primaryAction: {
    flex: 1,
  },
  rowHeader: {
    gap: 2,
    justifyContent: "center",
    paddingHorizontal: HB_SPACING.xs,
    width: ROW_HEADER_WIDTH,
  },
  secondaryAction: {
    flex: 1,
  },
  shiftChip: {
    backgroundColor: ACCENT.background,
    borderColor: "#B2DDFF",
    borderWidth: StyleSheet.hairlineWidth,
  },
  minorWarning: { color: "#B54708", fontSize: 11, fontWeight: "700", marginLeft: 2 },
  conflictMark: { color: "#D46B08", fontSize: 11, fontWeight: "700", marginLeft: 1 },
  conflictNotice: { color: "#B54708" },
  mealBadge: { bottom: 0, color: "#667085", fontSize: 9, fontWeight: "700", position: "absolute", right: 2 },
  // 自动扣餐：比手动指定的更浅更细，一眼能分辨
  mealBadgeAuto: { color: "#98A2B3", fontWeight: "500" },
  placementActions: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  placementBar: {
    alignItems: "center",
    backgroundColor: "#EFF8FF",
    borderColor: "#B2DDFF",
    borderRadius: 8,
    borderWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    justifyContent: "space-between",
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 2,
  },
  placementBarText: { color: "#175CD3", flexShrink: 1 },
  dragRoot: { gap: HB_SPACING.sm },
  dragHoverCell: { borderColor: "#175CD3", borderWidth: 2 },
  dragGhost: {
    alignItems: "center",
    backgroundColor: "#175CD3",
    borderRadius: 8,
    elevation: 8,
    height: DRAG_GHOST_HEIGHT,
    justifyContent: "center",
    left: 0,
    position: "absolute",
    shadowColor: "#000",
    shadowOffset: { height: 3, width: 0 },
    shadowOpacity: 0.25,
    shadowRadius: 6,
    top: 0,
    width: DRAG_GHOST_WIDTH,
  },
  dragGhostText: { color: HB_COLORS.white, fontWeight: "700" },
  placementOk: { backgroundColor: "#F0F7FF" },
  placementWarn: { backgroundColor: "#FFFBE6" },
  placementBad: { backgroundColor: "#FFF1F0", opacity: 0.6 },
  trayBar: { gap: HB_SPACING.xs },
  // 草稿班次：同样浅蓝底，用虚线描边区分未发布。
  shiftChipDraft: {
    borderColor: "#53B1FD",
    borderStyle: "dashed",
    borderWidth: 1,
  },
  shiftText: {
    color: ACCENT.text,
    flexShrink: 1,
    fontSize: 13,
    fontWeight: "700",
  },
  sidePanel: {
    backgroundColor: HB_COLORS.white,
    borderBottomLeftRadius: HB_RADIUS.sheet,
    borderTopLeftRadius: HB_RADIUS.sheet,
    bottom: 0,
    maxWidth: "70%",
    position: "absolute",
    right: 0,
    top: 0,
    width: SIDE_PANEL_WIDTH,
  },
  sidePanelBody: {
    gap: HB_SPACING.sm,
    padding: HB_SPACING.md,
  },
  sidePanelFooter: {
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.xs,
  },
  sidePanelHeader: {
    alignItems: "center",
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    paddingLeft: HB_SPACING.md,
    paddingVertical: 4,
  },
  sidePanelHeading: {
    flex: 1,
    minWidth: 0,
  },
  sidePanelScroll: {
    flex: 1,
  },
  statDivider: {
    borderLeftColor: HB_COLORS.outlineMuted,
    borderLeftWidth: StyleSheet.hairlineWidth,
  },
  typeBadge: {
    alignItems: "center",
    borderRadius: 4,
    justifyContent: "center",
    minWidth: 18,
    paddingHorizontal: 4,
    paddingVertical: 1,
  },
  typeBadgeText: {
    color: HB_COLORS.white,
    fontWeight: "700",
  },
  legendType: {
    alignItems: "center",
    flexDirection: "row",
    gap: 4,
  },
  statItem: {
    flex: 1,
    gap: 2,
    paddingHorizontal: HB_SPACING.xs,
  },
  statStrip: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    paddingVertical: HB_SPACING.sm,
  },
  statStripCompact: {
    paddingVertical: HB_SPACING.xs,
  },
  statValue: {
    color: HB_COLORS.textPrimary,
    fontSize: 20,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
  },
  statValuePrimary: {
    color: HB_COLORS.action,
  },
  statusLine: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
  },
  stepButton: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    borderWidth: StyleSheet.hairlineWidth,
    height: 40,
    justifyContent: "center",
    width: 44,
  },
  stepButtonPressed: {
    backgroundColor: ACCENT.background,
  },
  stepButtonDisabled: {
    opacity: 0.5,
  },
  mealLabelRow: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
  },
  stepButtonText: {
    color: HB_COLORS.action,
  },
  stepField: {
    flex: 1,
    gap: 6,
  },
  stepRow: {
    alignItems: "center",
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: HB_RADIUS.control,
    flexDirection: "row",
    gap: 4,
    padding: 4,
  },
  stepValue: {
    alignItems: "center",
    flex: 1,
  },
  stepValueText: {
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
  },
  timeRow: {
    flexDirection: "row",
    gap: HB_SPACING.sm,
  },
  todayColumn: {
    backgroundColor: "#F5FAFF",
  },
  todayHeader: {
    backgroundColor: ACCENT.background,
    borderBottomColor: HB_COLORS.brand,
    borderBottomWidth: 2,
  },
  todayHeaderText: {
    color: HB_COLORS.action,
    fontWeight: "700",
  },
  toolbar: {
    alignItems: "center",
    flexDirection: "row",
    gap: HB_SPACING.xs,
    justifyContent: "space-between",
  },
  uncoveredText: {
    color: HB_COLORS.warning,
  },
  weekActions: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
  },
  weekTitle: {
    alignItems: "center",
    flex: 1,
  },
});

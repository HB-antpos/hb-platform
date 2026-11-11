import { useCallback, useEffect, useRef, useState } from "react";
import { Animated, PanResponder, Pressable, StyleSheet, View } from "react-native";
import { Icon, IconButton, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { buildSlotMap, computeDropIndex, moveItem } from "./logic";
import { ColorDot, SC_COLORS } from "./shared";
import type { SpecialCategory } from "./types";

export const CATEGORY_ROW_HEIGHT = 56;

interface DraggableCategoryListProps {
  /** 当前显示顺序（已含乐观重排）。 */
  categories: readonly SpecialCategory[];
  /** 排序模式：显示上移 / 下移按钮（拖不动或使用读屏时的兜底），点行不再进入分类。 */
  reorderMode: boolean;
  disabled?: boolean;
  onOpen: (category: SpecialCategory) => void;
  /** 顺序变化（拖动松手或点上移 / 下移）：传入新的完整 id 顺序。 */
  onReorder: (nextIds: string[]) => void;
  /** 拖动开始 / 结束：父级据此暂停外层 ScrollView 滚动，避免手势互相抢。 */
  onDraggingChange?: (dragging: boolean) => void;
}

interface DragState {
  id: string;
  startIndex: number;
  targetIndex: number;
  ids: string[];
}

/**
 * 分类长按拖动排序。App 没有拖动 / 手势库（不允许新增原生依赖），这里只用 RN 自带的 PanResponder + Animated：
 * - 行高固定、绝对定位；拖动开始后被拖行跟手，其它行按「虚拟顺序」让位（槽位由 buildSlotMap 算出）。
 * - 起点有两个：行右侧的把手（按下即拖）和整行长按（350ms）。起点只负责登记「正在拖哪一行」，
 *   后续移动由容器的 PanResponder 在捕获阶段接管（onMoveShouldSetPanResponderCapture 只在已登记拖动时返回 true），
 *   这样不影响没有拖动时的点击与外层滚动。
 * - 松手时一次性回调 onReorder(新顺序)；是否提交 / 回滚由父级负责。
 * - 兜底：排序模式下每行有上移 / 下移按钮，并提供 accessibilityActions，读屏用户无需手势。
 */
export function DraggableCategoryList({
  categories,
  reorderMode,
  disabled = false,
  onOpen,
  onReorder,
  onDraggingChange,
}: DraggableCategoryListProps) {
  const [drag, setDrag] = useState<DragState | null>(null);
  // 手势回调是创建一次的闭包，所以所有会变的东西都走 ref。
  const dragRef = useRef<DragState | null>(null);
  const capturedRef = useRef(false);
  const idsRef = useRef<string[]>([]);
  const onReorderRef = useRef(onReorder);
  const onDraggingChangeRef = useRef(onDraggingChange);
  const dragTop = useRef(new Animated.Value(0)).current;
  const count = categories.length;
  idsRef.current = categories.map((item) => item.id);
  onReorderRef.current = onReorder;
  onDraggingChangeRef.current = onDraggingChange;

  const beginDrag = useCallback(
    (id: string) => {
      if (disabled || reorderMode || dragRef.current) return;
      const ids = idsRef.current;
      const startIndex = ids.indexOf(id);
      if (startIndex < 0 || ids.length < 2) return;
      capturedRef.current = false;
      const next: DragState = { id, startIndex, targetIndex: startIndex, ids: [...ids] };
      dragRef.current = next;
      dragTop.setValue(startIndex * CATEGORY_ROW_HEIGHT);
      setDrag(next);
      onDraggingChangeRef.current?.(true);
    },
    [disabled, dragTop, reorderMode],
  );

  const finishDrag = useCallback(
    (commit: boolean) => {
      const current = dragRef.current;
      if (!current) return;
      dragRef.current = null;
      capturedRef.current = false;
      setDrag(null);
      onDraggingChangeRef.current?.(false);
      if (commit && current.targetIndex !== current.startIndex) {
        onReorderRef.current(moveItem(current.ids, current.startIndex, current.targetIndex));
      }
    },
    [],
  );

  /** 按下但没有移动就抬手（普通点击把手 / 长按后不动）：只取消，不提交。 */
  const cancelIfNotCaptured = useCallback(() => {
    if (dragRef.current && !capturedRef.current) finishDrag(false);
  }, [finishDrag]);

  const panResponder = useRef(
    PanResponder.create({
      onStartShouldSetPanResponder: () => false,
      onMoveShouldSetPanResponder: () => dragRef.current !== null,
      onMoveShouldSetPanResponderCapture: () => dragRef.current !== null,
      onPanResponderTerminationRequest: () => false,
      onPanResponderGrant: () => {
        capturedRef.current = true;
      },
      onPanResponderMove: (_event, gesture) => {
        const current = dragRef.current;
        if (!current) return;
        const total = current.ids.length;
        const top = Math.min(
          Math.max(current.startIndex * CATEGORY_ROW_HEIGHT + gesture.dy, 0),
          (total - 1) * CATEGORY_ROW_HEIGHT,
        );
        dragTop.setValue(top);
        const target = computeDropIndex(current.startIndex, gesture.dy, CATEGORY_ROW_HEIGHT, total);
        if (target !== current.targetIndex) {
          current.targetIndex = target;
          // 只在越过一个槽位时刷新其它行的位置，拖动中的逐帧位移走 Animated 不触发渲染。
          setDrag({ ...current });
        }
      },
      onPanResponderRelease: () => finishDrag(true),
      onPanResponderTerminate: () => finishDrag(true),
    }),
  ).current;

  // 卸载（含切换分店导致列表被换掉）时若还在拖，通知父级恢复滚动。
  useEffect(
    () => () => {
      if (dragRef.current) onDraggingChangeRef.current?.(false);
    },
    [],
  );

  const ids = categories.map((item) => item.id);
  const slotMap = buildSlotMap(ids, drag?.id ?? null, drag?.targetIndex ?? 0);

  const moveBy = (index: number, delta: -1 | 1) => {
    const next = moveItem(ids, index, index + delta);
    if (next !== ids) onReorder(next);
  };

  return (
    <View style={[styles.card, { height: Math.max(count, 1) * CATEGORY_ROW_HEIGHT }]} {...panResponder.panHandlers}>
      {categories.map((category, index) => (
        <CategoryRow
          key={category.id}
          category={category}
          index={index}
          total={count}
          slot={slotMap.get(category.id) ?? index}
          dragging={drag?.id === category.id}
          dragTop={dragTop}
          showMoveButtons={reorderMode}
          disabled={disabled}
          onOpen={onOpen}
          onBeginDrag={beginDrag}
          onCancelDrag={cancelIfNotCaptured}
          onMove={moveBy}
        />
      ))}
    </View>
  );
}

interface CategoryRowProps {
  category: SpecialCategory;
  index: number;
  total: number;
  slot: number;
  dragging: boolean;
  dragTop: Animated.Value;
  showMoveButtons: boolean;
  disabled: boolean;
  onOpen: (category: SpecialCategory) => void;
  onBeginDrag: (id: string) => void;
  onCancelDrag: () => void;
  onMove: (index: number, delta: -1 | 1) => void;
}

function CategoryRow({
  category,
  index,
  total,
  slot,
  dragging,
  dragTop,
  showMoveButtons,
  disabled,
  onOpen,
  onBeginDrag,
  onCancelDrag,
  onMove,
}: CategoryRowProps) {
  const { t } = useAppTranslation("specialProductCategories");
  const top = useRef(new Animated.Value(slot * CATEGORY_ROW_HEIGHT)).current;
  const wasDragging = useRef(false);

  useEffect(() => {
    if (dragging) {
      wasDragging.current = true;
      return;
    }
    const target = slot * CATEGORY_ROW_HEIGHT;
    if (wasDragging.current) {
      // 刚松手：被拖行已经停在落点附近，直接落位，避免再滑一遍。
      wasDragging.current = false;
      top.setValue(target);
      return;
    }
    Animated.timing(top, { toValue: target, duration: 140, useNativeDriver: false }).start();
  }, [dragging, slot, top]);

  const rowLabel = t("list.rowLabel", { name: category.name, count: category.itemCount });
  return (
    <Animated.View
      style={[
        styles.row,
        { top: dragging ? dragTop : top },
        index < total - 1 && !dragging ? styles.rowDivider : null,
        dragging ? styles.rowLifted : null,
      ]}
    >
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={rowLabel}
        accessibilityActions={
          total > 1 && !disabled
            ? [
                { name: "moveUp", label: t("list.moveUp") },
                { name: "moveDown", label: t("list.moveDown") },
              ]
            : undefined
        }
        onAccessibilityAction={(event) => {
          if (event.nativeEvent.actionName === "moveUp") onMove(index, -1);
          else if (event.nativeEvent.actionName === "moveDown") onMove(index, 1);
        }}
        disabled={showMoveButtons}
        onPress={() => onOpen(category)}
        onLongPress={() => onBeginDrag(category.id)}
        delayLongPress={350}
        onPressOut={onCancelDrag}
        style={styles.rowMain}
      >
        <Pressable
          accessible={false}
          importantForAccessibility="no"
          hitSlop={6}
          disabled={disabled || showMoveButtons || total < 2}
          onPressIn={() => onBeginDrag(category.id)}
          onPressOut={onCancelDrag}
          style={styles.grip}
        >
          <Icon source="drag-horizontal-variant" size={22} color={total < 2 || showMoveButtons ? HB_COLORS.outline : HB_COLORS.textSecondary} />
        </Pressable>
        <ColorDot color={category.color} />
        <Text style={styles.name} numberOfLines={1}>
          {category.name}
        </Text>
        <Text style={styles.count}>{category.itemCount}</Text>
        {showMoveButtons ? null : <Icon source="chevron-right" size={20} color={HB_COLORS.textSecondary} />}
      </Pressable>
      {showMoveButtons ? (
        <View style={styles.moveButtons}>
          <IconButton
            icon="chevron-up"
            size={20}
            disabled={disabled || index === 0}
            accessibilityLabel={t("list.moveUpNamed", { name: category.name })}
            onPress={() => onMove(index, -1)}
            style={styles.moveButton}
          />
          <IconButton
            icon="chevron-down"
            size={20}
            disabled={disabled || index === total - 1}
            accessibilityLabel={t("list.moveDownNamed", { name: category.name })}
            onPress={() => onMove(index, 1)}
            style={styles.moveButton}
          />
        </View>
      ) : null}
    </Animated.View>
  );
}

const styles = StyleSheet.create({
  card: { backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface, overflow: "visible" },
  row: { position: "absolute", left: 0, right: 0, height: CATEGORY_ROW_HEIGHT, flexDirection: "row", alignItems: "center", backgroundColor: HB_COLORS.white, borderRadius: HB_RADIUS.surface },
  rowDivider: { borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted, borderRadius: 0 },
  rowLifted: {
    zIndex: 10,
    elevation: 6,
    backgroundColor: SC_COLORS.selectedBg,
    borderWidth: 1,
    borderColor: HB_COLORS.brand,
    shadowColor: "#101828",
    shadowOpacity: 0.18,
    shadowRadius: 8,
    shadowOffset: { width: 0, height: 4 },
  },
  rowMain: { flex: 1, height: "100%", flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, paddingRight: HB_SPACING.sm },
  grip: { width: 44, height: "100%", alignItems: "center", justifyContent: "center" },
  name: { flex: 1, minWidth: 0, fontSize: 15, fontWeight: "600", color: HB_COLORS.textPrimary },
  count: { fontSize: 14, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"], minWidth: 24, textAlign: "right" },
  moveButtons: { flexDirection: "row", alignItems: "center" },
  moveButton: { margin: 0 },
});

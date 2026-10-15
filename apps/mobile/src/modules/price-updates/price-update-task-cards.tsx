import { memo, useEffect, useState } from "react";
import { Image, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, Card, Checkbox, Menu, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import {
  buildPriceComparison,
  formatDiscountLabel,
  formatMoney,
  formatRelativeTime,
  resolveChangedFieldsKind,
  resolveCompletedStatusKey,
  resolveHqSyncChipKey,
  resolveInitiatorName,
  resolveInitiatorSourceLabel,
} from "./presentation";
import type { StorePriceUpdateTask } from "./types";

type Tone = "warning" | "brand" | "success" | "danger" | "neutral";

const TONE_COLORS: Record<Tone, { background: string; text: string }> = {
  warning: { background: "#FEF0C7", text: HB_COLORS.warning },
  brand: { background: "#E8F1FF", text: HB_COLORS.action },
  success: { background: "#DCFAE6", text: HB_COLORS.success },
  danger: { background: "#FEE4E2", text: HB_COLORS.danger },
  neutral: { background: HB_COLORS.surfaceMuted, text: HB_COLORS.textSecondary },
};

function Tag({ label, tone }: { label: string; tone: Tone }) {
  const colors = TONE_COLORS[tone];
  return (
    <View style={[styles.tag, { backgroundColor: colors.background }]}>
      <Text variant="labelSmall" style={[styles.tagText, { color: colors.text }]} numberOfLines={1}>
        {label}
      </Text>
    </View>
  );
}

/** 固定尺寸缩略图：无图或加载失败都显示同尺寸占位，保证行高稳定、列表不跳动。 */
export function ProductThumb({ uri, size }: { uri: string | null; size: number }) {
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    setFailed(false);
  }, [uri]);

  const boxStyle = [styles.thumb, { width: size, height: size }];
  if (!uri || failed) {
    return (
      <View style={boxStyle}>
        <MaterialCommunityIcons name="image-off-outline" size={size * 0.4} color={HB_COLORS.outline} />
      </View>
    );
  }
  return (
    <View style={boxStyle}>
      <Image
        source={{ uri }}
        resizeMode="contain"
        style={{ width: size, height: size }}
        onError={() => setFailed(true)}
      />
    </View>
  );
}

function ComparisonStrip({ task }: { task: StorePriceUpdateTask }) {
  const { t } = useAppTranslation("priceUpdates");
  const comparison = buildPriceComparison(task);
  const isLabelOnly = task.kind === "LabelOnly";
  const deltaTone = comparison.direction === "up" ? HB_COLORS.warning : HB_COLORS.success;
  const deltaText =
    comparison.delta == null || comparison.direction === "same"
      ? null
      : t(comparison.direction === "up" ? "card.deltaUp" : "card.deltaDown", {
          amount: formatMoney(Math.abs(comparison.delta)),
        });

  return (
    <View style={styles.compare}>
      <View style={styles.compareRow}>
        <Text variant="labelSmall" style={styles.compareLabel}>
          {t(isLabelOnly ? "card.shelfLabel" : "card.storePrice")}
        </Text>
        <Text variant="bodyMedium" style={[styles.number, isLabelOnly ? styles.strike : null]}>
          {formatMoney(comparison.fromPrice)}
        </Text>
        <MaterialCommunityIcons name="arrow-right" size={16} color={HB_COLORS.textSecondary} />
        <Text variant="labelSmall" style={styles.compareLabel}>
          {t(isLabelOnly ? "card.currentPrice" : "card.warehousePrice")}
        </Text>
        <Text variant="titleMedium" style={styles.number}>
          {formatMoney(comparison.toPrice)}
        </Text>
        {deltaText ? (
          <Text variant="labelMedium" style={[styles.delta, { color: deltaTone }]}>
            {deltaText}
          </Text>
        ) : null}
      </View>
      {comparison.discountChanged ? (
        <View style={styles.compareRow}>
          <Text variant="labelSmall" style={styles.compareLabel}>
            {t("card.discount")}
          </Text>
          <Text variant="bodySmall" style={[styles.secondary, isLabelOnly ? styles.strike : null]}>
            {formatDiscountLabel(comparison.fromDiscountRate, t)}
          </Text>
          <MaterialCommunityIcons name="arrow-right" size={14} color={HB_COLORS.textSecondary} />
          <Text variant="bodySmall" style={styles.discountTo}>
            {formatDiscountLabel(comparison.toDiscountRate, t)}
          </Text>
          <Text variant="bodySmall" style={styles.secondary}>
            {t("card.finalPrice", { price: formatMoney(comparison.toFinalPrice) })}
          </Text>
        </View>
      ) : null}
    </View>
  );
}

function InitiatorRow({ task, now }: { task: StorePriceUpdateTask; now: Date }) {
  const { t } = useAppTranslation("priceUpdates");
  const parts = [
    resolveInitiatorName(task.initiatorName, t),
    resolveInitiatorSourceLabel(task.initiatorSource, task.initiatorReference, t),
    task.changeCount > 1 ? t("card.changeCount", { count: task.changeCount }) : "",
  ].filter(Boolean);
  return (
    <View style={styles.initiatorRow}>
      <MaterialCommunityIcons name="account-arrow-right-outline" size={16} color={HB_COLORS.textSecondary} />
      <Text variant="bodySmall" style={[styles.secondary, styles.flex]} numberOfLines={1}>
        {parts.join(" · ")}
      </Text>
      <Text variant="bodySmall" style={styles.secondary}>
        {formatRelativeTime(task.initiatedAtUtc, now, t)}
      </Text>
    </View>
  );
}

export interface PendingTaskCardProps {
  task: StorePriceUpdateTask;
  now: Date;
  /** 只读分店：隐藏所有操作。 */
  canOperate: boolean;
  selecting: boolean;
  selected: boolean;
  busy: boolean;
  onToggleSelected: (task: StorePriceUpdateTask) => void;
  onApplyOnly: (task: StorePriceUpdateTask) => void;
  onApplyAndPrint: (task: StorePriceUpdateTask) => void;
  onKeepStorePrice: (task: StorePriceUpdateTask) => void;
  onViewProduct: (task: StorePriceUpdateTask) => void;
  onMarkReplaced: (task: StorePriceUpdateTask) => void;
  onPrintLabel: (task: StorePriceUpdateTask) => void;
}

export const PendingTaskCard = memo(function PendingTaskCard({
  task,
  now,
  canOperate,
  selecting,
  selected,
  busy,
  onToggleSelected,
  onApplyOnly,
  onApplyAndPrint,
  onKeepStorePrice,
  onViewProduct,
  onMarkReplaced,
  onPrintLabel,
}: PendingTaskCardProps) {
  const { t } = useAppTranslation("priceUpdates");
  const [menuVisible, setMenuVisible] = useState(false);
  const isPriceUpdate = task.kind === "PriceUpdate";
  const title = task.productName || task.productCode;

  return (
    <Card
      mode="outlined"
      style={[styles.card, task.isOverdue ? styles.cardOverdue : null, selected ? styles.cardSelected : null]}
      onPress={selecting ? () => onToggleSelected(task) : undefined}
      accessibilityLabel={title}
    >
      <Card.Content style={selecting ? styles.cardContentCompact : styles.cardContent}>
        <View style={styles.topRow}>
          {selecting ? (
            <Checkbox.Android
              status={selected ? "checked" : "unchecked"}
              onPress={() => onToggleSelected(task)}
              color={HB_COLORS.action}
            />
          ) : null}
          <ProductThumb uri={task.productImage} size={selecting ? 48 : 60} />
          <View style={styles.titleBlock}>
            <View style={styles.titleRow}>
              <Text variant="titleSmall" style={styles.title} numberOfLines={selecting ? 1 : 2}>
                {title}
              </Text>
              {/* 类型标签和超期标签纵向叠放：英文标签更长，并排会把商品名挤没。 */}
              <View style={styles.tagColumn}>
                <Tag
                  label={t(isPriceUpdate ? "kinds.priceUpdate" : "kinds.labelOnly")}
                  tone={isPriceUpdate ? "warning" : "brand"}
                />
                {task.isOverdue ? (
                  <Tag label={t("card.overdueTag", { days: task.overdueByDays })} tone="danger" />
                ) : null}
              </View>
            </View>
            <Text
              variant="bodySmall"
              style={task.isOverdue ? styles.overdueMeta : styles.secondary}
              numberOfLines={1}
            >
              {task.isOverdue
                ? `${task.itemNumber || task.productCode} · ${t("card.waiting", { days: task.waitingDays })}`
                : `${task.itemNumber || task.productCode} · ${t(`changedFields.${resolveChangedFieldsKind(task)}`)}`}
            </Text>
          </View>
        </View>

        <ComparisonStrip task={task} />
        <InitiatorRow task={task} now={now} />

        {canOperate && !selecting ? (
          <View style={styles.actions}>
            {isPriceUpdate ? (
              <>
                <Menu
                  visible={menuVisible}
                  onDismiss={() => setMenuVisible(false)}
                  anchor={(
                    <Button compact mode="text" disabled={busy} onPress={() => setMenuVisible(true)}>
                      {t("actions.more")}
                    </Button>
                  )}
                >
                  <Menu.Item
                    leadingIcon="hand-back-right-outline"
                    title={t("actions.keepStorePrice")}
                    onPress={() => {
                      setMenuVisible(false);
                      onKeepStorePrice(task);
                    }}
                  />
                  <Menu.Item
                    leadingIcon="open-in-new"
                    title={t("actions.viewProduct")}
                    onPress={() => {
                      setMenuVisible(false);
                      onViewProduct(task);
                    }}
                  />
                </Menu>
                <View style={styles.flex} />
                <Button compact mode="outlined" disabled={busy} onPress={() => onApplyOnly(task)}>
                  {t("actions.applyOnly")}
                </Button>
                <Button
                  compact
                  mode="contained"
                  icon="printer-outline"
                  disabled={busy}
                  onPress={() => onApplyAndPrint(task)}
                >
                  {t("actions.applyAndPrint")}
                </Button>
              </>
            ) : (
              <>
                <View style={styles.flex} />
                <Button compact mode="outlined" disabled={busy} onPress={() => onMarkReplaced(task)}>
                  {t("actions.markReplaced")}
                </Button>
                <Button
                  compact
                  mode="contained"
                  icon="printer-outline"
                  disabled={busy}
                  onPress={() => onPrintLabel(task)}
                >
                  {t("actions.printLabel")}
                </Button>
              </>
            )}
          </View>
        ) : null}
      </Card.Content>
    </Card>
  );
});

export interface CompletedTaskCardProps {
  task: StorePriceUpdateTask;
  now: Date;
  canOperate: boolean;
  hqSyncEnabled: boolean;
  busy: boolean;
  onReprint: (task: StorePriceUpdateTask) => void;
  onRetryHqSync: (task: StorePriceUpdateTask) => void;
}

export const CompletedTaskCard = memo(function CompletedTaskCard({
  task,
  now,
  canOperate,
  hqSyncEnabled,
  busy,
  onReprint,
  onRetryHqSync,
}: CompletedTaskCardProps) {
  const { t } = useAppTranslation("priceUpdates");
  const statusKey = resolveCompletedStatusKey(task);
  const hqKey = resolveHqSyncChipKey(task, hqSyncEnabled);
  // 已完成卡片展示「处理前 → 处理后」：保持本店价的任务价格未变，只显示本店价。
  const oldPrice = task.shelfRetailPrice ?? task.storeRetailPrice;
  const newPrice = task.storeRetailPrice;
  const priceText =
    task.completionMode === "KeptStorePrice" || oldPrice == null || oldPrice === newPrice
      ? formatMoney(newPrice)
      : `${formatMoney(oldPrice)} → ${formatMoney(newPrice)}`;
  const canReprint = canOperate && task.completionMode === "Printed";
  const canRetryHq = canOperate && hqKey === "hqSyncFailed" && Boolean(task.hqSyncOperationId);

  return (
    <Card mode="outlined" style={styles.card}>
      <Card.Content style={styles.cardContentCompact}>
        <View style={styles.topRow}>
          <ProductThumb uri={task.productImage} size={48} />
          <View style={styles.titleBlock}>
            <Text variant="titleSmall" style={styles.title} numberOfLines={1}>
              {task.productName || task.productCode}
            </Text>
            <Text variant="bodySmall" style={[styles.secondary, styles.numberLight]} numberOfLines={1}>
              {`${task.itemNumber || task.productCode} · ${priceText}`}
            </Text>
          </View>
        </View>
        <View style={styles.tagRow}>
          <Tag
            label={t(`completed.status.${statusKey}`)}
            tone={statusKey === "markedReplaced" || statusKey === "keptStorePrice" ? "neutral" : "success"}
          />
          {hqKey ? (
            <Tag
              label={t(`completed.status.${hqKey}`)}
              tone={hqKey === "hqSyncFailed" ? "danger" : hqKey === "hqSynced" ? "success" : "brand"}
            />
          ) : null}
        </View>
        <View style={styles.completedFooter}>
          <Text variant="bodySmall" style={[styles.secondary, styles.flex]} numberOfLines={1}>
            {t("completed.initiated", {
              name: resolveInitiatorName(task.initiatorName, t),
              time: formatRelativeTime(task.initiatedAtUtc, now, t),
            })}
          </Text>
          <Text variant="bodySmall" style={styles.secondary} numberOfLines={1}>
            {t("completed.handled", {
              name: task.completedBy || task.priceAppliedBy || t("initiator.system"),
              time: formatRelativeTime(task.completedAtUtc, now, t),
            })}
          </Text>
        </View>
        {canReprint || canRetryHq ? (
          <View style={styles.actions}>
            <View style={styles.flex} />
            {canRetryHq ? (
              <Button compact mode="outlined" icon="cloud-sync-outline" disabled={busy} onPress={() => onRetryHqSync(task)}>
                {t("actions.retryHqSync")}
              </Button>
            ) : null}
            {canReprint ? (
              <Button compact mode="outlined" icon="printer-outline" disabled={busy} onPress={() => onReprint(task)}>
                {t("actions.reprint")}
              </Button>
            ) : null}
          </View>
        ) : null}
      </Card.Content>
    </Card>
  );
});

const styles = StyleSheet.create({
  card: { backgroundColor: HB_COLORS.white, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface },
  cardSelected: { borderColor: HB_COLORS.brand, backgroundColor: "#F5F9FF" },
  cardOverdue: { borderColor: "#FDA29B" },
  tagColumn: { alignItems: "flex-end", gap: 4 },
  overdueMeta: { color: HB_COLORS.danger, fontWeight: "600", fontVariant: ["tabular-nums"] },
  cardContent: { gap: HB_SPACING.xs, paddingVertical: HB_SPACING.sm },
  cardContentCompact: { gap: HB_SPACING.xs, paddingVertical: HB_SPACING.xs },
  topRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  thumb: { borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.surfaceMuted, alignItems: "center", justifyContent: "center", overflow: "hidden" },
  titleBlock: { flex: 1, minWidth: 0, gap: HB_SPACING.xxs },
  titleRow: { flexDirection: "row", alignItems: "flex-start", gap: HB_SPACING.xs },
  title: { flex: 1, fontWeight: "700", color: HB_COLORS.textPrimary },
  tag: { paddingHorizontal: HB_SPACING.xs, paddingVertical: 2, borderRadius: HB_RADIUS.control, alignSelf: "flex-start" },
  tagText: { fontWeight: "600" },
  tagRow: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  secondary: { color: HB_COLORS.textSecondary },
  flex: { flex: 1 },
  compare: { gap: HB_SPACING.xxs, padding: HB_SPACING.xs, borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.surfaceMuted },
  compareRow: { flexDirection: "row", alignItems: "center", flexWrap: "wrap", gap: 6 },
  compareLabel: { color: HB_COLORS.textSecondary },
  number: { fontVariant: ["tabular-nums"], fontWeight: "700", color: HB_COLORS.textPrimary },
  numberLight: { fontVariant: ["tabular-nums"] },
  strike: { textDecorationLine: "line-through", color: HB_COLORS.textSecondary, fontWeight: "400" },
  delta: { marginLeft: "auto", fontVariant: ["tabular-nums"], fontWeight: "700" },
  discountTo: { color: HB_COLORS.textPrimary, fontWeight: "600" },
  initiatorRow: { flexDirection: "row", alignItems: "center", gap: 6 },
  actions: { flexDirection: "row", alignItems: "center", flexWrap: "wrap", gap: HB_SPACING.xs },
  completedFooter: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
});

import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, Dialog, Portal, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import type { PriceReminderRowViewModel, PriceReminderViewModel } from "./view-model";

/** 列表最多露出 4 行，更多分店在列表内部滚动，弹窗不会超出 PDA 屏幕。 */
const ROW_HEIGHT = 56;
const MAX_VISIBLE_ROWS = 4;

export type PriceReminderPrinterState = "ready" | "offline" | "hidden";

interface PriceReminderDialogProps {
  visible: boolean;
  viewModel: PriceReminderViewModel | null;
  /** 标签机状态只在 PDA（设备模式）单店布局里显示；店长手机一般不连标签机。 */
  printerState: PriceReminderPrinterState;
  onLater: () => void;
  onPrimary: () => void;
  onSelectStore: (storeCode: string) => void;
  onConnectPrinter: () => void;
}

function CountTile({
  tone,
  icon,
  label,
  hint,
  count,
  wide,
}: {
  tone: "warn" | "brand";
  icon: keyof typeof MaterialCommunityIcons.glyphMap;
  label: string;
  hint: string;
  count: number;
  wide: boolean;
}) {
  const color = tone === "warn" ? HB_COLORS.warning : HB_COLORS.action;
  return (
    <View style={[styles.tile, tone === "warn" ? styles.tileWarn : styles.tileBrand, wide ? styles.tileWide : null]}>
      <View style={wide ? styles.tileLeft : undefined}>
        <View style={styles.tileLabelRow}>
          <MaterialCommunityIcons name={icon} size={16} color={color} />
          <Text variant="labelLarge" style={[styles.tileLabel, { color }]} numberOfLines={1}>{label}</Text>
        </View>
        {wide ? null : <Text style={[styles.tileNumber, { color }]}>{count}</Text>}
        <Text variant="bodySmall" style={styles.tileHint} numberOfLines={1}>{hint}</Text>
      </View>
      {wide ? <Text style={[styles.tileNumber, { color }]}>{count}</Text> : null}
    </View>
  );
}

function StoreRow({
  row,
  onPress,
}: {
  row: PriceReminderRowViewModel;
  onPress: (storeCode: string) => void;
}) {
  const { t } = useAppTranslation("priceUpdates");
  const counts: { key: string; text: string; color: string }[] = [];
  if (row.pendingPriceUpdateCount > 0) {
    counts.push({
      key: "price",
      text: t("reminder.rowPrice", { count: row.pendingPriceUpdateCount }),
      color: HB_COLORS.warning,
    });
  }
  if (row.pendingLabelOnlyCount > 0) {
    counts.push({
      key: "label",
      text: t("reminder.rowLabel", { count: row.pendingLabelOnlyCount }),
      color: HB_COLORS.action,
    });
  }

  const content = (
    <>
      <View style={styles.rowBody}>
        <View style={styles.rowNameLine}>
          <Text variant="titleSmall" style={[styles.rowName, row.done ? styles.rowNameDone : null]} numberOfLines={1}>
            {row.storeName}
          </Text>
          {row.isCurrent ? (
            <View style={styles.currentTag}>
              <Text variant="labelSmall" style={styles.currentTagText}>{t("reminder.current")}</Text>
            </View>
          ) : null}
        </View>
        {row.done ? (
          <View style={styles.doneLine}>
            <MaterialCommunityIcons name="check-circle-outline" size={14} color={HB_COLORS.success} />
            <Text variant="bodySmall" style={styles.doneText}>{t("reminder.allDone")}</Text>
          </View>
        ) : (
          <Text variant="bodySmall" numberOfLines={1} style={styles.rowCounts}>
            {counts.map((item, index) => (
              <Text key={item.key} style={{ color: item.color, fontWeight: "700" }}>
                {index > 0 ? <Text style={styles.rowSeparator}>{" · "}</Text> : null}
                {item.text}
              </Text>
            ))}
          </Text>
        )}
      </View>
      {row.overdueCount > 0 ? (
        <View style={styles.overdueTag}>
          <MaterialCommunityIcons name="clock-alert-outline" size={12} color={HB_COLORS.danger} />
          <Text variant="labelSmall" style={styles.overdueTagText}>
            {t("reminder.overdueTag", { count: row.overdueCount })}
          </Text>
        </View>
      ) : null}
      {row.done ? null : <MaterialCommunityIcons name="chevron-right" size={22} color={HB_COLORS.textSecondary} />}
    </>
  );

  // 已全部完成的分店不可点：没有任务可处理，也不让它抢走点击。
  if (row.done) {
    return <View style={styles.row}>{content}</View>;
  }
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={row.storeName}
      onPress={() => onPress(row.storeCode)}
      style={({ pressed }) => [styles.row, pressed ? styles.rowPressed : null]}
    >
      {content}
    </Pressable>
  );
}

export function PriceReminderDialog({
  visible,
  viewModel,
  printerState,
  onLater,
  onPrimary,
  onSelectStore,
  onConnectPrinter,
}: PriceReminderDialogProps) {
  const { t } = useAppTranslation("priceUpdates");
  if (!viewModel) {
    return null;
  }

  const { single, lock } = viewModel;
  const isMulti = viewModel.variant === "multi";
  const pendingStoreCount = viewModel.rows.filter((row) => !row.done).length;

  let title: string;
  let subtitle: string;
  if (isMulti || !single) {
    title = t("reminder.titleMulti", { stores: pendingStoreCount, count: viewModel.totalPending });
    subtitle = t("reminder.subMulti", {
      stores: pendingStoreCount,
      total: viewModel.storeCount,
      days: viewModel.overdueDays,
    });
  } else {
    const count = single.priceUpdateCount + single.labelOnlyCount;
    const headlineKey = single.headline === "both" ? "Both" : single.headline === "labelOnly" ? "LabelOnly" : "PriceOnly";
    title = t(`reminder.title${headlineKey}`, { count });
    subtitle = t(`reminder.sub${headlineKey}`, { store: single.storeName });
  }

  return (
    <Portal>
      <Dialog
        visible={visible}
        onDismiss={onLater}
        style={styles.dialog}
        theme={{ colors: { elevation: { level3: HB_COLORS.white } } }}
      >
        <Dialog.Content style={styles.content} accessibilityLabel={t("reminder.accessibility")}>
          <View style={styles.top}>
            {/* 多分店弹窗列表和锁定框已经很高，去掉图标把高度让给列表；单店弹窗保留。 */}
            {isMulti ? null : (
              <View style={styles.iconWrap}>
                <MaterialCommunityIcons
                  name={single?.headline === "labelOnly" ? "tag-outline" : "tag-arrow-up-outline"}
                  size={24}
                  color={HB_COLORS.action}
                />
              </View>
            )}
            <Text style={styles.title}>{title}</Text>
            <Text variant="bodyMedium" style={styles.subtitle}>{subtitle}</Text>
          </View>

          {isMulti ? (
            <View style={styles.stores}>
              <ScrollView style={{ maxHeight: ROW_HEIGHT * MAX_VISIBLE_ROWS }} nestedScrollEnabled>
                {viewModel.rows.map((row, index) => (
                  <View key={row.storeCode} style={index > 0 ? styles.rowDivider : null}>
                    <StoreRow row={row} onPress={onSelectStore} />
                  </View>
                ))}
              </ScrollView>
            </View>
          ) : single ? (
            <View style={styles.tiles}>
              {single.priceUpdateCount > 0 ? (
                <CountTile
                  tone="warn"
                  icon="cash-sync"
                  label={t("reminder.tilePriceUpdate")}
                  hint={t("reminder.tilePriceUpdateHint")}
                  count={single.priceUpdateCount}
                  wide={single.labelOnlyCount === 0}
                />
              ) : null}
              {single.labelOnlyCount > 0 ? (
                <CountTile
                  tone="brand"
                  icon="tag-outline"
                  label={t("reminder.tileLabelOnly")}
                  hint={t(single.priceUpdateCount === 0 ? "reminder.tileLabelOnlyWideHint" : "reminder.tileLabelOnlyHint")}
                  count={single.labelOnlyCount}
                  wide={single.priceUpdateCount === 0}
                />
              ) : null}
            </View>
          ) : null}

          {/* PDA 不锁任何功能，超期只多一行红色提示；店长账号超期则见下面的锁定框。 */}
          {!lock && single && single.overdueCount > 0 ? (
            <View style={[styles.infoRow, styles.infoDanger]}>
              <MaterialCommunityIcons name="clock-alert-outline" size={18} color={HB_COLORS.danger} />
              <Text variant="bodySmall" style={[styles.infoText, { color: HB_COLORS.danger }]}>
                {t("reminder.overdueRow", { count: single.overdueCount, days: viewModel.overdueDays })}
              </Text>
            </View>
          ) : null}

          {printerState === "ready" ? (
            <View style={[styles.infoRow, styles.infoMuted]}>
              <MaterialCommunityIcons name="printer-check" size={18} color={HB_COLORS.success} />
              <Text variant="bodySmall" style={[styles.infoText, { color: HB_COLORS.textSecondary }]}>
                {t("reminder.printerReady")}
              </Text>
            </View>
          ) : printerState === "offline" ? (
            <View style={[styles.infoRow, styles.infoMuted]}>
              <MaterialCommunityIcons name="printer-alert" size={18} color={HB_COLORS.warning} />
              <Text variant="bodySmall" style={[styles.infoText, { color: HB_COLORS.textSecondary }]}>
                {t("reminder.printerOffline")}
              </Text>
              <Pressable accessibilityRole="button" onPress={onConnectPrinter} hitSlop={8}>
                <Text variant="labelLarge" style={styles.infoLink}>{t("reminder.printerConnect")}</Text>
              </Pressable>
            </View>
          ) : null}

          {lock ? (
            <View style={styles.lockBox}>
              <MaterialCommunityIcons name="lock-alert-outline" size={22} color={HB_COLORS.danger} style={styles.lockIcon} />
              <View style={styles.flex}>
                <Text variant="titleSmall" style={styles.lockTitle}>{t("reminder.lockTitle")}</Text>
                <Text variant="bodySmall" style={styles.lockBody}>
                  {t("reminder.lockBody", { count: lock.overdueCount, days: viewModel.overdueDays })}
                </Text>
              </View>
            </View>
          ) : null}

          <View style={styles.actions}>
            <Button mode="contained" onPress={onPrimary} contentStyle={styles.primaryContent} style={styles.primary}>
              {lock
                ? t("reminder.primaryOverdue", { count: lock.overdueCount })
                : t("reminder.primaryDefault")}
            </Button>
            <Button mode="text" onPress={onLater}>{t("reminder.later")}</Button>
          </View>
          <Text variant="labelSmall" style={styles.footer}>{t("reminder.footer")}</Text>
        </Dialog.Content>
      </Dialog>
    </Portal>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  dialog: { borderRadius: 28, backgroundColor: HB_COLORS.white },
  content: { gap: HB_SPACING.sm, paddingHorizontal: HB_SPACING.md },
  top: { alignItems: "center", gap: HB_SPACING.xs },
  iconWrap: {
    width: 44,
    height: 44,
    borderRadius: 22,
    backgroundColor: "#E8F1FF",
    alignItems: "center",
    justifyContent: "center",
  },
  title: { fontSize: 20, lineHeight: 28, fontWeight: "700", color: HB_COLORS.textPrimary, textAlign: "center" },
  subtitle: { color: HB_COLORS.textSecondary, textAlign: "center" },
  stores: {
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    overflow: "hidden",
  },
  row: {
    minHeight: ROW_HEIGHT,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingLeft: HB_SPACING.sm,
    paddingRight: HB_SPACING.xxs,
  },
  rowPressed: { backgroundColor: HB_COLORS.surfaceMuted },
  rowDivider: { borderTopWidth: 1, borderTopColor: HB_COLORS.outlineMuted },
  rowBody: { flex: 1, minWidth: 0, gap: 2 },
  rowNameLine: { flexDirection: "row", alignItems: "center", gap: 6 },
  rowName: { flexShrink: 1, fontWeight: "700", color: HB_COLORS.textPrimary },
  rowNameDone: { color: HB_COLORS.textSecondary },
  rowCounts: { color: HB_COLORS.textSecondary },
  rowSeparator: { color: HB_COLORS.textSecondary, fontWeight: "400" },
  currentTag: { backgroundColor: "#E8F1FF", borderRadius: 6, paddingHorizontal: 6, paddingVertical: 1 },
  currentTagText: { color: HB_COLORS.action, fontWeight: "600" },
  doneLine: { flexDirection: "row", alignItems: "center", gap: 4 },
  doneText: { color: HB_COLORS.success, fontWeight: "600" },
  overdueTag: {
    flexDirection: "row",
    alignItems: "center",
    gap: 2,
    backgroundColor: "#FEE4E2",
    borderRadius: 6,
    paddingHorizontal: 6,
    paddingVertical: 1,
  },
  overdueTagText: { color: HB_COLORS.danger, fontWeight: "600" },
  tiles: { flexDirection: "row", gap: HB_SPACING.xs },
  tile: { flex: 1, minWidth: 0, borderRadius: HB_RADIUS.surface, paddingHorizontal: HB_SPACING.sm, paddingVertical: 10 },
  tileWarn: { backgroundColor: "#FEF0C7" },
  tileBrand: { backgroundColor: "#E8F1FF" },
  tileWide: { flexDirection: "row", alignItems: "center", justifyContent: "space-between" },
  tileLeft: { flexShrink: 1, gap: 2 },
  tileLabelRow: { flexDirection: "row", alignItems: "center", gap: 4 },
  tileLabel: { fontWeight: "600" },
  tileNumber: { fontSize: 28, lineHeight: 34, fontWeight: "700", fontVariant: ["tabular-nums"] },
  tileHint: { color: HB_COLORS.textSecondary },
  infoRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, borderRadius: HB_RADIUS.control, paddingHorizontal: 10, paddingVertical: 6 },
  infoDanger: { backgroundColor: "#FEE4E2" },
  infoMuted: { backgroundColor: HB_COLORS.surfaceMuted },
  infoText: { flex: 1 },
  infoLink: { color: HB_COLORS.action, fontWeight: "700" },
  lockBox: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: 10,
    backgroundColor: "#FEE4E2",
    borderRadius: HB_RADIUS.surface,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 10,
  },
  lockIcon: { marginTop: 2 },
  lockTitle: { color: HB_COLORS.danger, fontWeight: "700" },
  lockBody: { color: "#7A271A" },
  actions: { gap: 4 },
  primary: { borderRadius: 22, backgroundColor: HB_COLORS.action },
  primaryContent: { minHeight: 44 },
  footer: { textAlign: "center", color: HB_COLORS.textSecondary },
});

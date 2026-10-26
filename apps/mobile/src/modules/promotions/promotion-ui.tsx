/**
 * 促销管理页三屏共用的小组件：规则块、徽标、状态标签、分区卡片、扫码弹层、商品搜索弹层。
 */
import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Icon, Text, TextInput } from "react-native-paper";
import { CameraView } from "expo-camera";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { CameraScanSheet } from "@/components/ui/CameraScanSheet";
import type { ProductLookupItem } from "@/modules/product-maintenance/types";
import { useCameraScan, type CameraScanMode } from "@/modules/scanner/use-camera-scan";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { formatMoney } from "./promotion-logic";
import type { PromotionLifecycleStatus } from "./types";

/** 促销浅橙（设计稿）：规则块、提醒框共用。 */
export const PROMO_COLORS = {
  orangeBg: "#FFF7E6",
  orangeBorder: "#FFD591",
  orangeText: "#AD4E00",
  blueBg: "#E6F4FF",
  blueText: "#0958D9",
  redBg: "#FFF1F0",
  redText: "#CF1322",
  greenBg: "#ECFDF3",
  greenText: "#067647",
} as const;

export const TABULAR = { fontVariant: ["tabular-nums" as const] };

/** 左侧大号规则块「N件 / $X」，一眼看清组合规则。 */
export function RuleBlock({ quantity, price, label, size = "md" }: { quantity: number; price: number; label: string; size?: "md" | "lg" }) {
  const large = size === "lg";
  return (
    <View style={[styles.ruleBlock, large && styles.ruleBlockLarge]} accessible accessibilityLabel={label}>
      <Text style={[styles.ruleQuantity, large && styles.ruleQuantityLarge, TABULAR]} numberOfLines={1}>
        {label}
      </Text>
      <Text style={[styles.rulePrice, TABULAR]} numberOfLines={1}>
        ${formatMoney(price)}
      </Text>
    </View>
  );
}

export type BadgeTone = "blue" | "red" | "neutral" | "green" | "orange";

const BADGE_TONES: Record<BadgeTone, { backgroundColor: string; color: string }> = {
  blue: { backgroundColor: PROMO_COLORS.blueBg, color: PROMO_COLORS.blueText },
  red: { backgroundColor: PROMO_COLORS.redBg, color: PROMO_COLORS.redText },
  neutral: { backgroundColor: HB_COLORS.surfaceMuted, color: HB_COLORS.textSecondary },
  green: { backgroundColor: PROMO_COLORS.greenBg, color: PROMO_COLORS.greenText },
  orange: { backgroundColor: PROMO_COLORS.orangeBg, color: PROMO_COLORS.orangeText },
};

export function Badge({ label, tone = "neutral", icon }: { label: string; tone?: BadgeTone; icon?: string }) {
  const palette = BADGE_TONES[tone];
  return (
    <View style={[styles.badge, { backgroundColor: palette.backgroundColor }]}>
      {icon ? <Icon source={icon} size={12} color={palette.color} /> : null}
      <Text style={[styles.badgeText, { color: palette.color }]} numberOfLines={1}>
        {label}
      </Text>
    </View>
  );
}

const STATUS_TONES: Record<PromotionLifecycleStatus, BadgeTone> = {
  active: "green",
  upcoming: "blue",
  disabled: "neutral",
  ended: "neutral",
};

export function StatusBadge({ status, label }: { status: PromotionLifecycleStatus; label: string }) {
  return <Badge label={label} tone={STATUS_TONES[status]} />;
}

export function SectionCard({ title, right, children }: { title?: string; right?: ReactNode; children: ReactNode }) {
  return (
    <View style={styles.section}>
      {title || right ? (
        <View style={styles.sectionHeader}>
          {title ? (
            <Text style={styles.sectionTitle} numberOfLines={1}>
              {title}
            </Text>
          ) : (
            <View />
          )}
          {right}
        </View>
      ) : null}
      {children}
    </View>
  );
}

/** 橙色提醒框（只提醒不拦）。 */
export function WarningBox({ children, tone = "warning" }: { children: ReactNode; tone?: "warning" | "info" | "danger" }) {
  const palette =
    tone === "info"
      ? { bg: HB_COLORS.white, border: HB_COLORS.outlineMuted, color: HB_COLORS.textSecondary, icon: "information-outline" }
      : tone === "danger"
        ? { bg: PROMO_COLORS.redBg, border: "#FFA39E", color: HB_COLORS.danger, icon: "alert-circle-outline" }
        : { bg: PROMO_COLORS.orangeBg, border: PROMO_COLORS.orangeBorder, color: PROMO_COLORS.orangeText, icon: "alert-outline" };
  return (
    <View style={[styles.warning, { backgroundColor: palette.bg, borderColor: palette.border }]} accessibilityRole="alert">
      <Icon source={palette.icon} size={16} color={palette.color} />
      <Text style={[styles.warningText, { color: palette.color }]}>{children}</Text>
    </View>
  );
}

/**
 * 相机扫码弹层：单次模式识别一次即关闭，连续模式保持相机（编辑页连续添加商品）。
 * 识别结果交给调用方处理；处理期间忽略新的识别，避免同一条码并发查询。
 */
export function PromotionScanSheet({
  visible,
  title,
  subtitle,
  defaultMode,
  statusText,
  permissionTitle,
  permissionDescription,
  grantLabel,
  onDismiss,
  onBarcode,
}: {
  visible: boolean;
  title: string;
  subtitle?: string;
  defaultMode: CameraScanMode;
  statusText?: string;
  permissionTitle: string;
  permissionDescription: string;
  grantLabel: string;
  onDismiss: () => void;
  /** 返回 false 表示本次识别无效（如查不到商品），单次模式下不关闭相机。 */
  onBarcode: (barcode: string) => Promise<boolean | void>;
}) {
  const [mode, setMode] = useState<CameraScanMode>(defaultMode);
  useEffect(() => {
    if (visible) setMode(defaultMode);
  }, [defaultMode, visible]);

  const handleBarcode = useCallback(
    async (barcode: string) => {
      const accepted = await onBarcode(barcode);
      if (mode === "single" && accepted !== false) onDismiss();
    },
    [mode, onBarcode, onDismiss],
  );

  const cameraScan = useCameraScan({
    disabled: !visible,
    ignoreWhileProcessing: true,
    suppressRepeatsUntilChange: mode === "continuous",
    resetKey: visible ? mode : null,
    onBarcode: handleBarcode,
  });

  return (
    <CameraScanSheet visible={visible} title={title} subtitle={subtitle} mode={mode} onModeChange={setMode} onDismiss={onDismiss}>
      {cameraScan.permission?.granted ? (
        <CameraView style={styles.camera} {...cameraScan.cameraProps} />
      ) : (
        <View style={styles.permission}>
          <Text variant="titleMedium">{permissionTitle}</Text>
          <Text variant="bodySmall" style={styles.muted}>
            {permissionDescription}
          </Text>
          <Button mode="contained" onPress={() => void cameraScan.requestPermission()}>
            {grantLabel}
          </Button>
        </View>
      )}
      {statusText ? (
        <Text variant="bodySmall" style={styles.scanStatus} accessibilityLiveRegion="polite">
          {statusText}
        </Text>
      ) : null}
    </CameraScanSheet>
  );
}

/**
 * 商品搜索 / 候选选择弹层：输入名称、条码或编码搜索本店商品，点一行即加入（可连续添加）。
 * presetCandidates 用于扫码命中多个商品时直接展示候选。
 */
export function ProductSearchSheet({
  visible,
  title,
  placeholder,
  emptyLabel,
  searchingLabel,
  closeLabel,
  addedCodes,
  presetCandidates,
  onSearch,
  onPick,
  onDismiss,
}: {
  visible: boolean;
  title: string;
  placeholder: string;
  emptyLabel: string;
  searchingLabel: string;
  closeLabel: string;
  addedCodes: ReadonlySet<string>;
  presetCandidates?: ProductLookupItem[] | null;
  onSearch: (keyword: string) => Promise<ProductLookupItem[]>;
  onPick: (item: ProductLookupItem) => void;
  onDismiss: () => void;
}) {
  const [keyword, setKeyword] = useState("");
  const [results, setResults] = useState<ProductLookupItem[]>([]);
  const [searching, setSearching] = useState(false);
  const [searched, setSearched] = useState(false);
  const requestRef = useRef(0);

  useEffect(() => {
    if (!visible) return;
    setKeyword("");
    setResults(presetCandidates ?? []);
    setSearched(Boolean(presetCandidates?.length));
  }, [presetCandidates, visible]);

  const runSearch = useCallback(async () => {
    const text = keyword.trim();
    if (!text) return;
    // 只认最后一次搜索的结果，避免慢请求覆盖新结果。
    const requestId = ++requestRef.current;
    setSearching(true);
    try {
      const items = await onSearch(text);
      if (requestId === requestRef.current) setResults(items);
    } catch {
      if (requestId === requestRef.current) setResults([]);
    } finally {
      if (requestId === requestRef.current) {
        setSearching(false);
        setSearched(true);
      }
    }
  }, [keyword, onSearch]);

  return (
    <BusinessSheet
      visible={visible}
      title={title}
      onDismiss={onDismiss}
      footer={
        <Button mode="outlined" onPress={onDismiss} contentStyle={styles.footerButton}>
          {closeLabel}
        </Button>
      }
    >
      <TextInput
        mode="outlined"
        dense
        value={keyword}
        placeholder={placeholder}
        onChangeText={setKeyword}
        onSubmitEditing={() => void runSearch()}
        returnKeyType="search"
        autoCorrect={false}
        autoCapitalize="none"
        style={styles.searchInput}
        left={<TextInput.Icon icon="magnify" />}
        right={keyword ? <TextInput.Icon icon="arrow-right" onPress={() => void runSearch()} /> : undefined}
      />
      {searching ? (
        <View style={styles.searching}>
          <ActivityIndicator size={16} />
          <Text variant="bodySmall" style={styles.muted}>
            {searchingLabel}
          </Text>
        </View>
      ) : null}
      {!searching && searched && results.length === 0 ? (
        <Text variant="bodyMedium" style={[styles.muted, styles.emptyText]}>
          {emptyLabel}
        </Text>
      ) : null}
      {results.map((item) => {
          const added = addedCodes.has(item.productCode);
          return (
            <Pressable
              key={item.productCode}
              accessibilityRole="button"
              accessibilityState={{ selected: added }}
              onPress={() => onPick(item)}
              style={({ pressed }) => [styles.resultRow, pressed && styles.pressed]}
            >
              <View style={styles.resultText}>
                <Text style={styles.resultName} numberOfLines={2}>
                  {item.productName || item.productCode}
                </Text>
                <Text style={[styles.resultMeta, TABULAR]} numberOfLines={1}>
                  {[item.barcode || item.productCode, item.itemNumber].filter(Boolean).join(" · ")}
                </Text>
              </View>
              <Icon
                source={added ? "check-circle" : "plus-circle-outline"}
                size={22}
                color={added ? HB_COLORS.success : HB_COLORS.action}
              />
            </Pressable>
          );
        })}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  ruleBlock: {
    width: 76,
    minHeight: 76,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: PROMO_COLORS.orangeBorder,
    backgroundColor: PROMO_COLORS.orangeBg,
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: 4,
  },
  ruleBlockLarge: { width: 88, minHeight: 88 },
  ruleQuantity: { fontSize: 22, lineHeight: 28, fontWeight: "800", color: PROMO_COLORS.orangeText },
  ruleQuantityLarge: { fontSize: 26, lineHeight: 32 },
  rulePrice: { fontSize: 13, lineHeight: 18, fontWeight: "700", color: PROMO_COLORS.orangeText },
  badge: {
    flexDirection: "row",
    alignItems: "center",
    gap: 2,
    borderRadius: 6,
    paddingHorizontal: 6,
    paddingVertical: 2,
    maxWidth: "100%",
  },
  badgeText: { fontSize: 12, lineHeight: 16, fontWeight: "600" },
  section: {
    backgroundColor: HB_COLORS.white,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    padding: HB_SPACING.md,
    gap: HB_SPACING.sm,
  },
  sectionHeader: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  sectionTitle: { fontSize: 15, lineHeight: 22, fontWeight: "700", color: HB_COLORS.textPrimary, flexShrink: 1 },
  warning: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
    borderWidth: 1,
    borderRadius: HB_RADIUS.control,
    padding: HB_SPACING.sm,
    alignItems: "flex-start",
  },
  warningText: { flex: 1, fontSize: 13, lineHeight: 20 },
  camera: { height: 320, width: "100%" },
  permission: { gap: HB_SPACING.xs, padding: HB_SPACING.md, backgroundColor: HB_COLORS.surfaceMuted },
  muted: { color: HB_COLORS.textSecondary },
  scanStatus: { color: HB_COLORS.textSecondary, paddingVertical: HB_SPACING.xs, textAlign: "center" },
  footerButton: { minHeight: 48 },
  searchInput: { backgroundColor: HB_COLORS.white },
  searching: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, paddingVertical: HB_SPACING.xs },
  emptyText: { paddingVertical: HB_SPACING.md, textAlign: "center" },
  resultRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    minHeight: 56,
    paddingVertical: HB_SPACING.xs,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  pressed: { opacity: 0.6 },
  resultText: { flex: 1, minWidth: 0 },
  resultName: { fontSize: 14, lineHeight: 20, fontWeight: "600", color: HB_COLORS.textPrimary },
  resultMeta: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
});

import { Pressable, StyleSheet, View } from "react-native";
import { Button, SegmentedButtons, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { BEND_PRESETS, money, RATE_PRESETS } from "./logic";
import { fmtRate } from "./pricing-curve";
import {
  UNBOUNDED_COST,
  actualPrice,
  arcLimit,
  bendPercent,
  endPrice,
  isFixed,
  setBend,
  setBoundary,
  setEdge,
  setEndpointRate,
  setKind,
  setRate,
  setShape,
  startPrice,
  type CurveShape,
  type Result,
  type Rule,
} from "./pricing-intervals";
import { CommitNumberField, PRICING_COLORS } from "./shared";

/**
 * 单个区间的编辑面板（底部弹出）：上下限、定价方式、固定成率或曲线两端成率、形状与弧度、交界联动。
 * 所有修改都走 pricing-intervals 的纯函数，返回失败时由 onApply 把错误显示在面板顶部并保持原值。
 * 面板是原生 Modal（BusinessSheet），里面不能用 Paper 的 Portal / Dialog / Menu（会被压在下面）。
 */
export function IntervalSheet({
  visible,
  rules,
  index,
  flags,
  readOnly,
  notice,
  onApply,
  onBreak,
  onRelink,
  onRemove,
  onClose,
}: {
  visible: boolean;
  rules: Rule[];
  index: number;
  flags: boolean[];
  readOnly: boolean;
  notice: string;
  onApply: (result: Result) => boolean;
  onBreak: (boundary: number) => void;
  onRelink: (boundary: number, driver: "left" | "right") => void;
  onRemove: () => void;
  onClose: () => void;
}) {
  const { t } = useAppTranslation("pricingStrategies");
  const n = rules.length;
  const r = rules[index];
  if (!visible || !r) return null;
  const first = index === 0;
  const last = index === n - 1;
  const fixed = isFixed(r);
  const unbounded = r.maxPrice >= UNBOUNDED_COST;
  const boundText = (value: number) => (value >= UNBOUNDED_COST ? t("interval.unbounded") : value.toFixed(2));
  const range = t("interval.range", { from: money(r.minPrice), to: boundText(r.maxPrice) });

  /** 曲线某一端是否跟随相邻固定区间（锁定，需先断开）。 */
  const endpointInfo = (which: "start" | "end") => {
    const b = which === "start" ? index - 1 : index;
    const hasBoundary = b >= 0 && b < n - 1;
    const nb = rules[which === "start" ? index - 1 : index + 1];
    const linked = hasBoundary && flags[b];
    return { b, hasBoundary, nb, linked, locked: linked && !!nb && isFixed(nb) };
  };

  const endpoint = (which: "start" | "end") => {
    const info = endpointInfo(which);
    const cost = which === "start" ? r.minPrice : r.maxPrice;
    const price = which === "start" ? startPrice(r) : endPrice(r);
    // 输入的是成率；零售价由 成本 × 成率 算出并取合法尾数后显示。跟随固定区间时显示固定成率（不可改）。
    const rate = info.locked ? info.nb.startRate : which === "start" ? r.startRate : r.endRate;
    const raw = cost * rate;
    return (
      <View style={styles.col} key={which}>
        <Text style={styles.label}>{t(which === "start" ? "interval.startRate" : "interval.endRate", { cost: money(cost) })}</Text>
        <CommitNumberField
          value={rate}
          format={(value) => value.toFixed(4)}
          disabled={readOnly || info.locked}
          right={t("interval.rateUnit")}
          accessibilityLabel={t(which === "start" ? "interval.startRate" : "interval.endRate", { cost: money(cost) })}
          onCommit={(value) => onApply(setEndpointRate(rules, index, which, value, flags))}
        />
        <Text style={styles.retail}>
          {t("interval.retail", { price: "" })}
          <Text style={styles.retailValue}>{money(price)}</Text>
          {!info.locked && Math.abs(raw - price) > 0.004 ? <Text style={styles.snapped}>{t("interval.snapped", { raw: money(raw) })}</Text> : null}
        </Text>
        {info.hasBoundary ? (
          info.linked ? (
            <Text style={styles.link}>
              {`🔗 ${info.nb && isFixed(info.nb) ? t("interval.followFixed", { rate: fmtRate(info.nb.startRate) }) : t("interval.followInterval", { n: which === "start" ? index : index + 2 })}`}
              {!readOnly ? (
                <Text style={styles.linkAction} onPress={() => onBreak(info.b)} accessibilityRole="button">
                  {` · ${t("actions.breakLink")}`}
                </Text>
              ) : null}
            </Text>
          ) : rules[info.b] && (!isFixed(rules[info.b]) || !isFixed(rules[info.b + 1])) ? (
            <Text style={styles.linkMuted}>
              {t("interval.brokenHint")}
              {!readOnly ? (
                <Text style={styles.linkAction} onPress={() => onRelink(info.b, which === "start" ? "right" : "left")} accessibilityRole="button">
                  {` · ${t("actions.relink")}`}
                </Text>
              ) : null}
            </Text>
          ) : null
        ) : null}
      </View>
    );
  };

  const limit = arcLimit(r);
  const percent = Math.round(bendPercent(r));

  return (
    <BusinessSheet
      visible
      title={t("interval.title", { n: index + 1, range })}
      onDismiss={onClose}
      footer={
        <View style={styles.footer}>
          {!readOnly ? (
            <Button mode="outlined" textColor={HB_COLORS.danger} style={[styles.footerButton, styles.danger]} disabled={n <= 1} onPress={onRemove}>
              {t("actions.removeInterval")}
            </Button>
          ) : null}
          <Button mode="contained" style={[styles.footerButton, styles.done]} onPress={onClose}>
            {t("actions.done")}
          </Button>
        </View>
      }
    >
      {notice ? (
        <View style={styles.notice} accessibilityLiveRegion="polite">
          <Text style={styles.noticeText}>{notice}</Text>
        </View>
      ) : null}
      <View style={styles.grid}>
        <View style={styles.col}>
          <Text style={styles.label}>{t(first ? "interval.minFirst" : "interval.min")}</Text>
          <CommitNumberField
            value={r.minPrice}
            format={(value) => value.toFixed(2)}
            disabled={readOnly || !first}
            accessibilityLabel={t(first ? "interval.minFirst" : "interval.min")}
            onCommit={(value) => onApply(setEdge(rules, "min", value))}
          />
          {!first ? <Text style={styles.help}>{t("interval.minFollows", { n: index })}</Text> : null}
        </View>
        <View style={styles.col}>
          <Text style={styles.label}>{t("interval.max")}</Text>
          <CommitNumberField
            value={r.maxPrice}
            format={boundText}
            disabled={readOnly}
            accessibilityLabel={t("interval.max")}
            onCommit={(value) => onApply(last ? setEdge(rules, "max", value) : setBoundary(rules, index, value, flags))}
          />
          {last && !unbounded && !readOnly ? (
            <Pressable accessibilityRole="button" onPress={() => onApply(setEdge(rules, "max", UNBOUNDED_COST))} hitSlop={6}>
              <Text style={styles.linkAction}>{t("interval.setUnbounded")}</Text>
            </Pressable>
          ) : null}
        </View>
      </View>

      <Text style={[styles.label, styles.section]}>{t("interval.kind")}</Text>
      <SegmentedButtons
        value={fixed ? "fixed" : "curve"}
        onValueChange={(value) => {
          if (!readOnly) onApply(setKind(rules, index, value as "fixed" | "curve", flags));
        }}
        buttons={[
          { value: "fixed", label: t("interval.fixed"), disabled: readOnly },
          { value: "curve", label: t("interval.curve"), disabled: readOnly },
        ]}
      />

      {fixed ? (
        <View style={styles.section}>
          <Text style={styles.label}>{t("interval.rate")}</Text>
          <CommitNumberField
            value={r.startRate}
            format={(value) => value.toFixed(4)}
            disabled={readOnly}
            right={t("interval.rateUnit")}
            accessibilityLabel={t("interval.rate")}
            onCommit={(value) => onApply(setRate(rules, index, value, flags))}
          />
          <View style={styles.presets}>
            {RATE_PRESETS.map((preset) => (
              <PresetChip key={preset} label={String(preset)} active={r.startRate === preset} disabled={readOnly} onPress={() => onApply(setRate(rules, index, preset, flags))} />
            ))}
          </View>
          <Text style={styles.help}>
            {t("interval.fixedFormula", {
              rate: fmtRate(r.startRate),
              from: money(actualPrice(r, r.minPrice)),
              to: unbounded ? t("interval.unbounded") : money(actualPrice(r, r.maxPrice)),
            })}
          </Text>
        </View>
      ) : (
        <>
          <View style={[styles.grid, styles.section]}>
            {endpoint("start")}
            {endpoint("end")}
          </View>
          <Text style={[styles.label, styles.section]}>{t("interval.shape")}</Text>
          <SegmentedButtons
            value={r.algorithm}
            onValueChange={(value) => {
              if (!readOnly) onApply(setShape(rules, index, value as CurveShape));
            }}
            buttons={(["Linear", "ArcUp", "ArcDown"] as const).map((shape) => ({ value: shape, label: t(`interval.${shape}`), disabled: readOnly }))}
          />
          <View style={styles.bendRow}>
            <Text style={styles.label}>{t("interval.bend")}</Text>
            <Text style={styles.bendValue}>{r.algorithm === "Linear" ? t("interval.bendDisabled") : t("interval.bendValue", { value: percent })}</Text>
          </View>
          {/* 弧度用 5 档快捷值而不是滑块：面板本身可滚动，滑块拖动会和滚动手势冲突。 */}
          <View style={styles.presets}>
            {BEND_PRESETS.map((preset) => (
              <PresetChip
                key={preset}
                label={`${preset}%`}
                active={r.algorithm !== "Linear" && Math.abs(percent - preset) < 0.5}
                disabled={readOnly || r.algorithm === "Linear" || limit === 0}
                onPress={() => onApply(setBend(rules, index, preset))}
              />
            ))}
          </View>
        </>
      )}
    </BusinessSheet>
  );
}

function PresetChip({ label, active, disabled, onPress }: { label: string; active: boolean; disabled: boolean; onPress: () => void }) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected: active, disabled }}
      disabled={disabled}
      onPress={onPress}
      style={[styles.preset, active ? styles.presetActive : null, disabled ? styles.presetDisabled : null]}
    >
      <Text style={[styles.presetText, active ? styles.presetTextActive : null]}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  notice: { backgroundColor: PRICING_COLORS.dropBg, borderColor: PRICING_COLORS.goldBorder, borderWidth: 1, borderRadius: HB_RADIUS.control, padding: 10, marginBottom: HB_SPACING.sm },
  noticeText: { fontSize: 13, color: HB_COLORS.warning },
  grid: { flexDirection: "row", gap: HB_SPACING.sm },
  col: { flex: 1, minWidth: 0 },
  section: { marginTop: HB_SPACING.md },
  label: { fontSize: 12, color: HB_COLORS.textSecondary, marginBottom: 4 },
  help: { fontSize: 12, color: PRICING_COLORS.muted, marginTop: 4, lineHeight: 17 },
  retail: { fontSize: 12, color: HB_COLORS.textSecondary, marginTop: 4 },
  retailValue: { fontSize: 14, fontWeight: "700", color: HB_COLORS.brand, fontVariant: ["tabular-nums"] },
  snapped: { color: PRICING_COLORS.muted },
  link: { fontSize: 12, color: HB_COLORS.brand, marginTop: 4 },
  linkMuted: { fontSize: 12, color: PRICING_COLORS.muted, marginTop: 4 },
  linkAction: { fontSize: 12, color: HB_COLORS.action, fontWeight: "600", textDecorationLine: "underline", marginTop: 4 },
  presets: { flexDirection: "row", flexWrap: "wrap", gap: 6, marginTop: HB_SPACING.xs },
  preset: { minWidth: 48, height: 30, paddingHorizontal: 10, borderRadius: 15, borderWidth: 1, borderColor: HB_COLORS.outline, alignItems: "center", justifyContent: "center", backgroundColor: HB_COLORS.white },
  presetActive: { borderColor: HB_COLORS.brand, backgroundColor: PRICING_COLORS.blueBg },
  presetDisabled: { opacity: 0.45 },
  presetText: { fontSize: 13, color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  presetTextActive: { color: HB_COLORS.brand, fontWeight: "700" },
  bendRow: { flexDirection: "row", justifyContent: "space-between", alignItems: "baseline", marginTop: HB_SPACING.md },
  bendValue: { fontSize: 12, color: HB_COLORS.textSecondary },
  footer: { flexDirection: "row", gap: HB_SPACING.sm },
  footerButton: { borderRadius: HB_RADIUS.control },
  danger: { flex: 1, borderColor: "#FDA29B" },
  done: { flex: 2 },
});

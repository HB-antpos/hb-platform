import { useEffect, useMemo, useState, type ReactNode } from "react";
import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import { useQueryClient } from "@tanstack/react-query";
import { ActivityIndicator, Button, Dialog, Icon, IconButton, Portal, SegmentedButtons, Snackbar, Switch, Text, TextInput } from "react-native-paper";
import { SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { EmptyState } from "@/components/ui/EmptyState";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { createPricingStrategy, deletePricingStrategy, updatePricingStrategy } from "./api";
import { IntervalChart } from "./IntervalChart";
import { IntervalSheet } from "./IntervalSheet";
import { buildSavePayload, draftFromDetail, money, sortNamedOptions, summarizeDraftRules, validateDraft, type StrategyDraft } from "./logic";
import { MultiSelectSheet } from "./MultiSelectSheet";
import { defaultCurve, fixedRules, fmtRate } from "./pricing-curve";
import {
  MAX_INTERVALS,
  UNBOUNDED_COST,
  addInterval,
  appendInterval,
  breakLink,
  convertLegacy,
  endPrice,
  hasCurveSide,
  intervalsError,
  isEditable,
  isFixed,
  linkedFlags,
  priceDrops,
  relink,
  removeInterval,
  shiftBrokenAfterAdd,
  shiftBrokenAfterRemove,
  startPrice,
  type ConversionNote,
  type PriceDrop,
  type Result,
  type Rule,
} from "./pricing-intervals";
import { IndexBadge, PRICING_COLORS, PricingScreenMessage, useApiErrorText, useIssueText, usePricingGuard } from "./shared";
import type { PricingStrategyLevel } from "./types";
import { pricingStrategyKeys, usePricingScopeOptions, usePricingStrategyDetail } from "./use-pricing-strategies";

const first = (value: string | string[] | undefined) => (Array.isArray(value) ? value[0] : value);
const newDraft = (): StrategyDraft => ({ name: "", level: "Global", priority: "0", isEnabled: true, storeCodes: [], supplierCodes: [], rules: defaultCurve() });

export function PricingStrategyEditScreen() {
  const router = useRouter();
  const params = useLocalSearchParams<{ id?: string | string[] }>();
  const id = first(params.id) || null;
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const { blocked, canEdit } = usePricingGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/pricing-strategies"));
  if (blocked) return <PricingScreenMessage message={blocked} onBack={goBack} />;
  return <EditLoader key={`${userGuid}:${id ?? "new"}`} id={id} canEdit={canEdit} onBack={goBack} />;
}

/** 取策略详情（新建时直接用默认曲线），拿到后才挂载编辑器，编辑器内部的草稿只从这一份数据初始化。 */
function EditLoader({ id, canEdit, onBack }: { id: string | null; canEdit: boolean; onBack: () => void }) {
  const { t } = useAppTranslation("pricingStrategies");
  const errorText = useApiErrorText();
  const query = usePricingStrategyDetail(id);
  if (id && query.isPending) {
    return (
      <SafeAreaView style={styles.safe}>
        <Header title={t("editor.editTitle")} onBack={onBack} />
        <View style={styles.state}>
          <ActivityIndicator color={HB_COLORS.brand} />
          <Text style={styles.stateText}>{t("editor.loading")}</Text>
        </View>
      </SafeAreaView>
    );
  }
  if (id && (query.isError || !query.data)) {
    return (
      <SafeAreaView style={styles.safe}>
        <Header title={t("editor.editTitle")} onBack={onBack} />
        <View style={styles.state}>
          <EmptyState title={t("editor.loadFailed")} description={errorText(query.error, "editor.loadFailed")} actionLabel={t("actions.retry")} onAction={() => void query.refetch()} />
        </View>
      </SafeAreaView>
    );
  }
  return <Editor id={id} initial={query.data ? draftFromDetail(query.data) : newDraft()} canEdit={canEdit} onBack={onBack} />;
}

function Header({ title, onBack, right }: { title: string; onBack: () => void; right?: ReactNode }) {
  const { t } = useAppTranslation("pricingStrategies");
  return (
    <View style={styles.header}>
      <IconButton icon="close" accessibilityLabel={t("actions.back")} onPress={onBack} />
      <Text variant="titleMedium" style={styles.title} numberOfLines={1}>
        {title}
      </Text>
      {right}
    </View>
  );
}

type Confirm = { kind: "drops"; drops: PriceDrop[] } | { kind: "delete" } | { kind: "preset"; preset: "fixed" | "curve" };

function Editor({ id, initial, canEdit, onBack }: { id: string | null; initial: StrategyDraft; canEdit: boolean; onBack: () => void }) {
  const { t, language } = useAppTranslation("pricingStrategies");
  const insets = useSafeAreaInsets();
  const queryClient = useQueryClient();
  const issueText = useIssueText();
  const errorText = useApiErrorText();
  const { stores, suppliers, storeNames, supplierNames } = usePricingScopeOptions();
  // 适用供应商多选列表按名字排序（分店多选不在本次范围内，保持原样）。
  const sortedSuppliers = useMemo(() => sortNamedOptions(suppliers, language), [suppliers, language]);
  const sortedStores = useMemo(() => sortNamedOptions(stores, language), [stores, language]);
  const [draft, setDraft] = useState<StrategyDraft>(initial);
  const [selected, setSelected] = useState(0);
  const [sheetOpen, setSheetOpen] = useState(false);
  const [notice, setNotice] = useState("");
  // 用户主动断开联动的交界下标（库里不存联动状态，重新打开时按「售价是否连续」推断）。
  const [broken, setBroken] = useState<ReadonlySet<number>>(new Set());
  const [scopeSheet, setScopeSheet] = useState<"stores" | "suppliers" | null>(null);
  const [converted, setConverted] = useState<{ previous: Rule[]; notes: ConversionNote[] } | null>(null);
  const [notesOpen, setNotesOpen] = useState(false);
  const [confirm, setConfirm] = useState<Confirm | null>(null);
  const [saving, setSaving] = useState(false);
  const [snack, setSnack] = useState("");
  const readOnly = !canEdit;
  const rules = draft.rules;
  const editable = isEditable(rules);
  const n = rules.length;
  const index = Math.min(selected, Math.max(n - 1, 0));
  const flags = useMemo(() => (editable ? linkedFlags(rules, broken) : []), [broken, editable, rules]);

  // 区间数变化后选中项可能越界，收回到最后一个。
  useEffect(() => {
    if (selected > n - 1) setSelected(Math.max(n - 1, 0));
  }, [n, selected]);

  const setRules = (next: Rule[]) => setDraft((current) => ({ ...current, rules: next }));
  /** 应用区间操作：失败时把原因显示出来并保持原值（输入框据此还原）。 */
  const apply = (result: Result): boolean => {
    if (!result.rules) {
      setNotice(issueText(result.error));
      return false;
    }
    setNotice("");
    setRules(result.rules);
    return true;
  };
  const showError = (result: Result) => {
    if (!result.rules) setSnack(issueText(result.error));
  };

  const openInterval = (i: number) => {
    setSelected(i);
    setNotice("");
    setSheetOpen(true);
  };
  const onAppend = () => {
    const result = appendInterval(rules);
    if (!result.rules) return showError(result);
    setRules(result.rules);
    openInterval(n);
  };
  const onSplit = () => {
    const result = addInterval(rules, index);
    if (!result.rules) return showError(result);
    setRules(result.rules);
    setBroken(shiftBrokenAfterAdd(broken, index));
    setSelected(index + 1);
  };
  const onRemove = () => {
    const result = removeInterval(rules, index);
    if (!apply(result)) return;
    setBroken(shiftBrokenAfterRemove(broken, index));
    setSelected(Math.max(0, index - 1));
    setSheetOpen(false);
  };
  const onBreak = (boundary: number) => {
    if (apply(breakLink(rules, boundary))) setBroken(new Set([...broken, boundary]));
  };
  const onRelink = (boundary: number, driver: "left" | "right") => {
    if (!apply(relink(rules, boundary, driver))) return;
    const next = new Set(broken);
    next.delete(boundary);
    setBroken(next);
  };
  const applyPreset = (preset: "fixed" | "curve") => {
    setBroken(new Set());
    setSelected(0);
    setRules(preset === "fixed" ? fixedRules(2.5) : defaultCurve());
  };
  const convert = () => {
    const result = convertLegacy(rules);
    if (!result.rules) {
      setSnack(issueText(result.error));
      return;
    }
    setConverted({ previous: rules, notes: result.notes });
    setBroken(new Set());
    setSelected(0);
    setRules(result.rules);
    setNotesOpen(true);
  };
  const undoConvert = () => {
    if (converted) setRules(converted.previous);
    setConverted(null);
    setNotesOpen(false);
  };

  const invalidate = () => queryClient.invalidateQueries({ queryKey: pricingStrategyKeys.all });
  const save = async (confirmedDrops: boolean) => {
    const issue = validateDraft(draft);
    if (issue) {
      setSnack(issueText(issue));
      return;
    }
    // 交界处成本变大、售价反而降低（阶梯定价）：保存前必须二次确认（与 Web 一致）。
    const drops = priceDrops(draft.rules);
    if (drops.length && !confirmedDrops) {
      setConfirm({ kind: "drops", drops });
      return;
    }
    setSaving(true);
    try {
      const payload = buildSavePayload(draft);
      if (id) await updatePricingStrategy(id, payload);
      else await createPricingStrategy(payload);
      await invalidate();
      setSnack(t("editor.saved"));
      onBack();
    } catch (cause) {
      setSnack(errorText(cause, "editor.saveFailed"));
    } finally {
      setSaving(false);
    }
  };
  const remove = async () => {
    if (!id) return;
    setSaving(true);
    try {
      await deletePricingStrategy(id);
      await invalidate();
      onBack();
    } catch (cause) {
      setSnack(errorText(cause, "editor.deleteFailed"));
    } finally {
      setSaving(false);
    }
  };

  const summary = summarizeDraftRules(rules);
  const ruleIssue = editable ? intervalsError(rules) : null;
  const draftIssue = validateDraft(draft);
  const lastMax = rules[n - 1]?.maxPrice ?? 0;
  const boundText = (value: number) => (value >= UNBOUNDED_COST ? t("interval.unbounded") : money(value));
  const scopeText = (codes: string[], names: Map<string, string>, all: string) =>
    codes.length ? `${codes.slice(0, 3).map((code) => names.get(code) ?? code).join(", ")}${codes.length > 3 ? ` +${codes.length - 3}` : ""}` : all;

  return (
    <SafeAreaView style={styles.safe} edges={["top", "left", "right"]}>
      <Header
        title={id ? t("editor.editTitle") : t("editor.newTitle")}
        onBack={onBack}
        right={
          canEdit ? (
            <Button onPress={() => void save(false)} disabled={saving} labelStyle={styles.headerSave}>
              {t("actions.save")}
            </Button>
          ) : null
        }
      />
      <ScrollView style={styles.scroll} contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        {readOnly ? <Text style={styles.readOnly}>{t("messages.readOnly")}</Text> : null}

        {/* ① 基本信息 */}
        <View style={styles.card}>
          <TextInput
            mode="outlined"
            dense
            label={t("editor.name")}
            value={draft.name}
            onChangeText={(name) => setDraft((current) => ({ ...current, name }))}
            disabled={readOnly}
            style={styles.input}
            outlineStyle={styles.outline}
          />
          <Text style={[styles.label, styles.gap]}>{t("editor.level")}</Text>
          <SegmentedButtons
            value={draft.level}
            onValueChange={(level) => setDraft((current) => ({ ...current, level: level as PricingStrategyLevel }))}
            buttons={(["Global", "Store", "Supplier"] as const).map((level) => ({ value: level, label: t(`level.${level}`), disabled: readOnly }))}
          />
          <TextInput
            mode="outlined"
            dense
            label={t("editor.priority")}
            value={draft.priority}
            onChangeText={(priority) => setDraft((current) => ({ ...current, priority }))}
            keyboardType="number-pad"
            disabled={readOnly}
            style={[styles.input, styles.gap]}
            outlineStyle={styles.outline}
          />
          <ScopeRow
            label={t("editor.stores")}
            value={scopeText(draft.storeCodes, storeNames, t("editor.storesAll"))}
            placeholder={!draft.storeCodes.length}
            disabled={readOnly}
            onPress={() => setScopeSheet("stores")}
          />
          <ScopeRow
            label={t("editor.suppliers")}
            value={scopeText(draft.supplierCodes, supplierNames, t("editor.suppliersAll"))}
            placeholder={!draft.supplierCodes.length}
            disabled={readOnly}
            onPress={() => setScopeSheet("suppliers")}
          />
          <View style={[styles.switchRow, styles.gap]}>
            <Text style={styles.switchLabel}>{t("editor.enabled")}</Text>
            <Switch value={draft.isEnabled} disabled={readOnly} onValueChange={(isEnabled) => setDraft((current) => ({ ...current, isEnabled }))} />
          </View>
        </View>

        {/* ② 价格区间：旧版规则先转换；否则显示只读图 + 区间列表 */}
        {!editable ? (
          <>
            <View style={styles.banner}>
              <Text style={styles.bannerTitle}>{t("editor.legacyTitle")}</Text>
              <Text style={styles.bannerBody}>{t("editor.legacyBody")}</Text>
              {canEdit ? (
                <Button mode="contained" onPress={convert} style={styles.bannerButton}>
                  {t("actions.convert")}
                </Button>
              ) : null}
            </View>
            <View style={styles.card}>
              <Text style={styles.label}>{t("editor.legacyRules")}</Text>
              {rules.map((rule, i) => (
                <Text key={i} style={styles.legacyRow}>
                  {t("editor.legacyRow", {
                    from: money(rule.minPrice),
                    to: boundText(rule.maxPrice),
                    algorithm: t(`algorithm.${rule.algorithm}`),
                    rates: rule.startRate === rule.endRate ? fmtRate(rule.startRate) : `${fmtRate(rule.startRate)} → ${fmtRate(rule.endRate)}`,
                  })}
                </Text>
              ))}
            </View>
          </>
        ) : (
          <>
            <View style={styles.card}>
              <View style={styles.sectionHead}>
                <Text style={styles.sectionTitle}>{t("editor.intervals")}</Text>
                <Text style={styles.muted}>{` · ${t("editor.intervalCount", { count: n })}`}</Text>
                {canEdit ? (
                  <View style={styles.sectionActions}>
                    <SmallButton label={t("actions.split")} disabled={n >= MAX_INTERVALS} onPress={onSplit} />
                    <SmallButton label={`＋ ${t("actions.append")}`} primary disabled={n >= MAX_INTERVALS || lastMax >= UNBOUNDED_COST} onPress={onAppend} />
                  </View>
                ) : null}
              </View>
              <IntervalChart
                rules={rules}
                selected={index}
                onSelect={setSelected}
                accessibilityLabel={t("editor.chartCaption", { from: money(rules[0].minPrice), to: boundText(lastMax) })}
              />
              <Text style={styles.caption}>{t("editor.chartCaption", { from: money(rules[0].minPrice), to: boundText(lastMax) })}</Text>
              {canEdit ? (
                <View style={styles.presetRow}>
                  <SmallButton label={t("actions.presetFixed")} onPress={() => setConfirm({ kind: "preset", preset: "fixed" })} />
                  <SmallButton label={t("actions.presetCurve")} onPress={() => setConfirm({ kind: "preset", preset: "curve" })} />
                </View>
              ) : null}
            </View>
            <Text style={styles.hint}>{t("editor.intervalHint")}</Text>
            {rules.map((rule, i) => (
              <View key={i}>
                <IntervalRow rule={rule} n={i + 1} active={i === index} onPress={() => openInterval(i)} boundText={boundText} />
                {i < n - 1 ? <BoundaryRow rules={rules} i={i} linked={flags[i]} /> : null}
              </View>
            ))}
            <View style={[styles.card, styles.checklist]}>
              {ruleIssue ? (
                <Text style={styles.checkError}>{`✗ ${issueText(ruleIssue)}`}</Text>
              ) : (
                <>
                  <Check ok text={t("rules.rateOk")} />
                  <Check ok text={t("rules.coverage", { from: money(rules[0].minPrice), to: boundText(lastMax) })} />
                  <Check ok text={t("rules.monotonic")} />
                  {summary.drops > 0 ? <Check warn text={t("rules.drops", { count: summary.drops })} /> : <Check ok text={t("rules.continuous")} />}
                  {lastMax < UNBOUNDED_COST ? <Text style={styles.checkMuted}>{t("rules.beyond", { max: money(lastMax) })}</Text> : null}
                </>
              )}
            </View>
          </>
        )}

        {id && canEdit ? (
          <Button mode="outlined" textColor={HB_COLORS.danger} icon="delete-outline" style={styles.deleteButton} onPress={() => setConfirm({ kind: "delete" })} disabled={saving}>
            {t("actions.deleteStrategy")}
          </Button>
        ) : null}
      </ScrollView>

      {/* 底部常驻：区间组成 + 校验结论 + 保存 */}
      <View style={[styles.bar, { paddingBottom: Math.max(insets.bottom, HB_SPACING.sm) }]}>
        <View style={styles.barText}>
          <Text style={styles.barSummary} numberOfLines={1}>
            {editable ? t("editor.summary", { total: summary.total, fixed: summary.fixedCount, curve: summary.curveCount }) : t("editor.legacyTitle")}
          </Text>
          <Text
            numberOfLines={2}
            style={[styles.barStatus, { color: draftIssue ? HB_COLORS.danger : summary.drops ? HB_COLORS.warning : HB_COLORS.success }]}
          >
            {`● ${draftIssue ? issueText(draftIssue) : summary.drops ? t("editor.dropsPending", { count: summary.drops }) : t("editor.valid")}`}
          </Text>
        </View>
        <Button mode="outlined" onPress={onBack} style={styles.barButton}>
          {t("actions.cancel")}
        </Button>
        {canEdit ? (
          <Button mode="contained" onPress={() => void save(false)} loading={saving} disabled={saving} style={styles.barButton}>
            {t("actions.save")}
          </Button>
        ) : null}
      </View>

      <IntervalSheet
        visible={sheetOpen && editable}
        rules={rules}
        index={index}
        flags={flags}
        readOnly={readOnly}
        notice={notice}
        onApply={apply}
        onBreak={onBreak}
        onRelink={onRelink}
        onRemove={onRemove}
        onClose={() => {
          setSheetOpen(false);
          setNotice("");
        }}
      />
      {scopeSheet ? (
        <MultiSelectSheet
          title={scopeSheet === "stores" ? t("editor.stores") : t("editor.suppliers")}
          options={scopeSheet === "stores" ? sortedStores : sortedSuppliers}
          selected={scopeSheet === "stores" ? draft.storeCodes : draft.supplierCodes}
          onClose={() => setScopeSheet(null)}
          onDone={(codes) => {
            setDraft((current) => (scopeSheet === "stores" ? { ...current, storeCodes: codes } : { ...current, supplierCodes: codes }));
            setScopeSheet(null);
          }}
        />
      ) : null}
      <BusinessSheet
        visible={notesOpen && converted !== null}
        title={t("editor.convertedTitle")}
        onDismiss={() => setNotesOpen(false)}
        footer={
          <View style={styles.sheetFooter}>
            <Button mode="outlined" style={[styles.barButton, styles.flex1]} onPress={undoConvert}>
              {t("actions.undoConvert")}
            </Button>
            <Button mode="contained" style={[styles.barButton, styles.flex2]} onPress={() => setNotesOpen(false)}>
              {t("actions.gotIt")}
            </Button>
          </View>
        }
      >
        <Text style={styles.notesHint}>{converted?.notes.length ? t("editor.convertedHint") : t("editor.convertedNoChanges")}</Text>
        {converted?.notes.map((note, i) => (
          <Text key={i} style={[styles.note, note.code === "drops" ? styles.noteWarn : null]}>
            {`• ${t(`notes.${note.code}`, note.params)}`}
          </Text>
        ))}
      </BusinessSheet>
      <Portal>
        <Dialog visible={confirm !== null} onDismiss={() => setConfirm(null)}>
          <Dialog.Title>
            {confirm?.kind === "drops" ? t("editor.dropsTitle") : confirm?.kind === "delete" ? t("editor.deleteTitle") : t("editor.presetTitle")}
          </Dialog.Title>
          <Dialog.Content>
            {confirm?.kind === "drops" ? (
              <>
                <Text style={styles.dialogText}>{t("editor.dropsHint")}</Text>
                {confirm.drops.map((drop) => (
                  <Text key={drop.index} style={styles.dropRow}>
                    {`• ${t("editor.dropRow", { at: money(drop.at), from: money(drop.from), to: money(drop.to), diff: money(drop.from - drop.to) })}`}
                  </Text>
                ))}
              </>
            ) : confirm?.kind === "delete" ? (
              <Text style={styles.dialogText}>{t("editor.deleteBody")}</Text>
            ) : (
              <Text style={styles.dialogText}>{confirm?.kind === "preset" && confirm.preset === "fixed" ? t("editor.presetFixedBody") : t("editor.presetCurveBody")}</Text>
            )}
          </Dialog.Content>
          <Dialog.Actions>
            <Button onPress={() => setConfirm(null)}>{t("actions.cancel")}</Button>
            <Button
              textColor={confirm?.kind === "delete" ? HB_COLORS.danger : undefined}
              onPress={() => {
                const current = confirm;
                setConfirm(null);
                if (current?.kind === "drops") void save(true);
                else if (current?.kind === "delete") void remove();
                else if (current?.kind === "preset") applyPreset(current.preset);
              }}
            >
              {confirm?.kind === "drops" ? t("actions.confirmSave") : confirm?.kind === "delete" ? t("actions.delete") : t("actions.replace")}
            </Button>
          </Dialog.Actions>
        </Dialog>
      </Portal>
      <Snackbar visible={Boolean(snack)} onDismiss={() => setSnack("")} duration={3500} style={{ marginBottom: 72 + insets.bottom }}>
        {snack}
      </Snackbar>
    </SafeAreaView>
  );
}

function IntervalRow({ rule, n, active, onPress, boundText }: { rule: Rule; n: number; active: boolean; onPress: () => void; boundText: (value: number) => string }) {
  const { t } = useAppTranslation("pricingStrategies");
  const fixed = isFixed(rule);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={t("interval.open", { n })}
      onPress={onPress}
      style={({ pressed }) => [styles.row, active ? styles.rowActive : null, pressed ? styles.rowPressed : null]}
    >
      <IndexBadge n={n} active={active} />
      <Text style={styles.rowRange}>{t("interval.range", { from: money(rule.minPrice), to: boundText(rule.maxPrice) })}</Text>
      <View style={[styles.tag, fixed ? styles.tagFixed : styles.tagCurve]}>
        <Text style={[styles.tagText, { color: fixed ? PRICING_COLORS.gold : "#175CD3" }]} numberOfLines={1}>
          {fixed ? t("interval.tagFixed", { rate: fmtRate(rule.startRate) }) : t("interval.tagCurve", { from: fmtRate(rule.startRate), to: fmtRate(rule.endRate) })}
        </Text>
      </View>
      <Icon source="chevron-right" size={18} color={PRICING_COLORS.muted} />
    </Pressable>
  );
}

/** 两个区间之间的交界说明：连续 / 回落（橙色）/ 上调，以及是否被断开。 */
function BoundaryRow({ rules, i, linked }: { rules: Rule[]; i: number; linked: boolean }) {
  const { t } = useAppTranslation("pricingStrategies");
  const from = endPrice(rules[i]);
  const to = startPrice(rules[i + 1]);
  const params = { at: money(rules[i].maxPrice), from: money(from), to: money(to), diff: money(Math.abs(from - to)) };
  const drop = to < from - 0.004;
  const text = drop ? t("editor.boundaryDrop", params) : Math.abs(to - from) <= 0.005 ? t("editor.boundaryContinuous", params) : t("editor.boundaryRaise", params);
  return (
    <View style={[styles.boundary, drop ? styles.boundaryDrop : null]}>
      <Text style={[styles.boundaryText, drop ? styles.boundaryTextDrop : null]}>
        {`${drop ? "▼" : "✓"} ${text}`}
        {hasCurveSide(rules, i) && !linked ? `  · ${t("editor.boundaryBroken")}` : ""}
      </Text>
    </View>
  );
}

function ScopeRow({ label, value, placeholder, disabled, onPress }: { label: string; value: string; placeholder: boolean; disabled: boolean; onPress: () => void }) {
  return (
    <View style={styles.gap}>
      <Text style={styles.label}>{label}</Text>
      <Pressable accessibilityRole="button" accessibilityLabel={`${label}: ${value}`} disabled={disabled} onPress={onPress} style={[styles.select, disabled ? styles.selectDisabled : null]}>
        <Text style={[styles.selectText, placeholder ? styles.placeholder : null]} numberOfLines={1}>
          {value}
        </Text>
        <Icon source="chevron-right" size={18} color={PRICING_COLORS.muted} />
      </Pressable>
    </View>
  );
}

function SmallButton({ label, onPress, primary, disabled }: { label: string; onPress: () => void; primary?: boolean; disabled?: boolean }) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      style={[styles.small, primary ? styles.smallPrimary : null, disabled ? styles.smallDisabled : null]}
    >
      <Text style={[styles.smallText, primary ? styles.smallTextPrimary : null]}>{label}</Text>
    </Pressable>
  );
}

function Check({ text, ok, warn }: { text: string; ok?: boolean; warn?: boolean }) {
  return (
    <Text style={styles.check}>
      <Text style={{ color: warn ? HB_COLORS.warning : ok ? HB_COLORS.success : HB_COLORS.danger, fontWeight: "700" }}>{warn ? "⚠ " : "✓ "}</Text>
      {text}
    </Text>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  header: { flexDirection: "row", alignItems: "center", paddingRight: HB_SPACING.xs, backgroundColor: HB_COLORS.surface, borderBottomWidth: 1, borderBottomColor: HB_COLORS.outlineMuted },
  title: { flex: 1, fontWeight: "700", color: HB_COLORS.textPrimary },
  headerSave: { fontWeight: "700" },
  state: { paddingTop: HB_SPACING.xl, paddingHorizontal: HB_SPACING.lg, alignItems: "center", gap: HB_SPACING.sm },
  stateText: { fontSize: 13, color: HB_COLORS.textSecondary },
  scroll: { flex: 1 },
  content: { padding: HB_SPACING.sm, paddingBottom: HB_SPACING.xl },
  readOnly: { fontSize: 12, color: HB_COLORS.textSecondary, marginBottom: HB_SPACING.xs },
  card: { backgroundColor: HB_COLORS.surface, borderRadius: HB_RADIUS.surface, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, padding: HB_SPACING.sm, marginBottom: HB_SPACING.sm },
  input: { backgroundColor: HB_COLORS.white },
  outline: { borderRadius: HB_RADIUS.control },
  label: { fontSize: 12, color: HB_COLORS.textSecondary, marginBottom: 4 },
  gap: { marginTop: HB_SPACING.sm },
  select: {
    height: 42,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.white,
    paddingHorizontal: 12,
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
  },
  selectDisabled: { backgroundColor: HB_COLORS.surfaceMuted },
  selectText: { flex: 1, fontSize: 14, color: HB_COLORS.textPrimary },
  placeholder: { color: PRICING_COLORS.muted },
  switchRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between" },
  switchLabel: { fontSize: 15, color: HB_COLORS.textPrimary },
  sectionHead: { flexDirection: "row", alignItems: "center", marginBottom: 6 },
  sectionTitle: { fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary },
  muted: { fontSize: 12, color: PRICING_COLORS.muted },
  sectionActions: { marginLeft: "auto", flexDirection: "row", gap: 6 },
  caption: { fontSize: 11, color: PRICING_COLORS.muted, textAlign: "center", marginTop: 2 },
  presetRow: { flexDirection: "row", gap: 6, marginTop: HB_SPACING.xs, justifyContent: "center" },
  hint: { fontSize: 12, color: HB_COLORS.textSecondary, marginHorizontal: 4, marginBottom: 6 },
  small: { height: 30, paddingHorizontal: 12, borderRadius: 15, borderWidth: 1, borderColor: HB_COLORS.outline, backgroundColor: HB_COLORS.white, justifyContent: "center" },
  smallPrimary: { backgroundColor: HB_COLORS.brand, borderColor: HB_COLORS.brand },
  smallDisabled: { opacity: 0.45 },
  smallText: { fontSize: 12, fontWeight: "600", color: HB_COLORS.textPrimary },
  smallTextPrimary: { color: HB_COLORS.white },
  row: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    padding: 10,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 10,
    backgroundColor: HB_COLORS.white,
  },
  rowActive: { borderColor: PRICING_COLORS.blueBorder },
  rowPressed: { backgroundColor: HB_COLORS.surfaceMuted },
  rowRange: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  tag: { marginLeft: "auto", flexShrink: 1, borderWidth: 1, borderRadius: 6, paddingHorizontal: 8, paddingVertical: 1 },
  tagFixed: { backgroundColor: PRICING_COLORS.goldBg, borderColor: PRICING_COLORS.goldBorder },
  tagCurve: { backgroundColor: PRICING_COLORS.blueBg, borderColor: PRICING_COLORS.blueBorder },
  tagText: { fontSize: 12, fontVariant: ["tabular-nums"] },
  boundary: { marginVertical: 3, marginLeft: 18, paddingVertical: 2, paddingLeft: 12, borderLeftWidth: 2, borderLeftColor: PRICING_COLORS.okBorder },
  boundaryDrop: { borderLeftColor: "#FDB022", backgroundColor: PRICING_COLORS.dropBg, borderTopRightRadius: 6, borderBottomRightRadius: 6 },
  boundaryText: { fontSize: 11, color: PRICING_COLORS.muted, fontVariant: ["tabular-nums"] },
  boundaryTextDrop: { color: HB_COLORS.warning },
  checklist: { marginTop: HB_SPACING.sm, backgroundColor: HB_COLORS.surfaceMuted },
  check: { fontSize: 12, lineHeight: 20, color: HB_COLORS.textSecondary },
  checkMuted: { fontSize: 12, lineHeight: 20, color: PRICING_COLORS.muted },
  checkError: { fontSize: 13, color: HB_COLORS.danger },
  banner: { borderWidth: 1, borderColor: PRICING_COLORS.blueBorder, backgroundColor: PRICING_COLORS.blueBg, borderRadius: HB_RADIUS.surface, padding: HB_SPACING.sm, marginBottom: HB_SPACING.sm },
  bannerTitle: { fontSize: 14, fontWeight: "700", color: "#175CD3" },
  bannerBody: { fontSize: 13, color: "#175CD3", marginTop: 4, lineHeight: 19 },
  bannerButton: { marginTop: HB_SPACING.sm, borderRadius: HB_RADIUS.control },
  legacyRow: { fontSize: 13, lineHeight: 24, color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  deleteButton: { marginTop: HB_SPACING.md, borderRadius: HB_RADIUS.control, borderColor: "#FDA29B" },
  bar: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingTop: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    backgroundColor: HB_COLORS.surface,
    borderTopWidth: 1,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  barText: { flex: 1, minWidth: 0 },
  barSummary: { fontSize: 12, color: HB_COLORS.textSecondary },
  barStatus: { fontSize: 12, marginTop: 1 },
  barButton: { borderRadius: HB_RADIUS.control },
  sheetFooter: { flexDirection: "row", gap: HB_SPACING.sm },
  flex1: { flex: 1 },
  flex2: { flex: 2 },
  notesHint: { fontSize: 13, color: HB_COLORS.textSecondary, marginBottom: HB_SPACING.xs },
  note: { fontSize: 13, lineHeight: 21, color: HB_COLORS.textPrimary },
  noteWarn: { color: HB_COLORS.warning },
  dialogText: { color: HB_COLORS.textSecondary },
  dropRow: { marginTop: 4, fontVariant: ["tabular-nums"] },
});

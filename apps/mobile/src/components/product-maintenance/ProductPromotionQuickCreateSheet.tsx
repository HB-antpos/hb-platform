import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Pressable, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Button, Icon, Switch, Text } from "react-native-paper";
import { MonthDatePicker } from "@/components/attendance/MonthDatePicker";
import {
  ProductPromotionConflictContent,
  ProductPromotionConflictFooter,
  type ProductPromotionConflictState,
} from "@/components/product-maintenance/ProductPromotionConflictPanel";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { getProductFastDetail, lookupProducts } from "@/modules/product-maintenance/api";
import {
  addDraftProduct,
  applyCustomDate,
  applyQuickRange,
  buildQuickPromotionPayload,
  evaluateQuickPromotionDraft,
  isPromotionExclusiveConflictError,
  parseMaxPerOrderText,
  removeDraftProduct,
  resolveDraftName,
  sanitizeMoneyInput,
  toConflictCandidate,
  toLocalDateString,
  type QuickPromotionDraft,
} from "@/modules/product-maintenance/product-promotion-section";
import type { ProductLookupItem } from "@/modules/product-maintenance/types";
import { createPromotion, updatePromotion } from "@/modules/promotions/api";
import {
  MIN_APPLY_QUANTITY,
  findExclusiveConflicts,
  formatMoney,
  formatPercent,
  formatPromotionDateTime,
  suggestEndBeforeConflicts,
  type QuickRangeKey,
} from "@/modules/promotions/promotion-logic";
import type { PromotionListItem } from "@/modules/promotions/types";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import { useScannerGuardedInput } from "@/modules/scanner/use-scanner-guarded-input";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

const QUICK_RANGES: QuickRangeKey[] = ["sevenDays", "monthEnd", "thirtyDays"];
const MAX_APPLY_QUANTITY = 99;

type SheetView = "form" | "pickStart" | "pickEnd" | "conflict";

interface ProductPromotionQuickCreateSheetProps {
  visible: boolean;
  mode: "create" | "edit";
  /** 打开时的草稿（新建由区块按当前商品生成；编辑由详情生成）。 */
  initialDraft: QuickPromotionDraft | null;
  anchorProductCode: string;
  anchorProductName: string;
  anchorRetailPrice: number | null;
  storeCode: string;
  storeName: string;
  /** 区块已加载的本商品促销，用于保存前本地冲突预检。 */
  existingPromotions: readonly PromotionListItem[];
  onDismiss: () => void;
  onSaved: (mode: "create" | "edit") => void;
}

/** 「为本商品新建促销」底部弹层（设计稿第 2 屏）；编辑本店独享促销复用同一个弹层。 */
export function ProductPromotionQuickCreateSheet({
  visible,
  mode,
  initialDraft,
  anchorProductCode,
  anchorProductName,
  anchorRetailPrice,
  storeCode,
  storeName,
  existingPromotions,
  onDismiss,
  onSaved,
}: ProductPromotionQuickCreateSheetProps) {
  const { t, language } = useAppTranslation(["productQuery", "common"]);
  const [draft, setDraft] = useState<QuickPromotionDraft | null>(initialDraft);
  const [view, setView] = useState<SheetView>("form");
  const [saving, setSaving] = useState(false);
  const [errorText, setErrorText] = useState<string | null>(null);
  const [moreExpanded, setMoreExpanded] = useState(false);
  const [productsExpanded, setProductsExpanded] = useState(false);
  const [conflictState, setConflictState] = useState<ProductPromotionConflictState | null>(null);
  const [searchText, setSearchText] = useState("");
  const [searching, setSearching] = useState(false);
  const [searchMessage, setSearchMessage] = useState<string | null>(null);
  const [searchCandidates, setSearchCandidates] = useState<ProductLookupItem[]>([]);
  // 弹层关闭后迟到的商品查找结果不再写回。
  const searchGenerationRef = useRef(0);

  // 每次打开都从传入的草稿重新开始，避免上一次取消的输入残留。
  useEffect(() => {
    if (visible) {
      setDraft(initialDraft);
      setView("form");
      setSaving(false);
      setErrorText(null);
      setMoreExpanded(false);
      setProductsExpanded(false);
      setConflictState(null);
      setSearchText("");
      setSearchMessage(null);
      setSearchCandidates([]);
    } else {
      searchGenerationRef.current += 1;
    }
  }, [visible, initialDraft]);

  // ---------------- 硬件扫码（扫码枪 / Zebra PDA） ----------------
  // 弹层是独立窗口，商品查询页的扫码监听收不到按键；弹层打开期间由弹层自己接收，扫到即加入同组商品。
  const scanBarcodeRef = useRef<(barcode: string) => void>(() => {});
  const scanQueueRef = useRef<Promise<unknown>>(Promise.resolve());
  const hidEnabled = visible && view === "form" && !saving;
  const hidScanner = useHidBarcodeScanner({
    enabled: hidEnabled,
    textInputOnly: true,
    onScan: (barcode) => scanBarcodeRef.current(barcode),
  });
  const visibleInputFocusedRef = useRef(false);
  const resumeHiddenFocusTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const { pauseHiddenInputFocus, resumeHiddenInputFocus } = hidScanner;
  // 可见输入框聚焦时暂停隐藏扫码框抢焦点，失焦后延迟交还，避免输入框之间切换时互相抢。
  const handleVisibleInputFocus = useCallback(() => {
    visibleInputFocusedRef.current = true;
    if (resumeHiddenFocusTimerRef.current) {
      clearTimeout(resumeHiddenFocusTimerRef.current);
      resumeHiddenFocusTimerRef.current = null;
    }
    pauseHiddenInputFocus();
  }, [pauseHiddenInputFocus]);
  const handleVisibleInputBlur = useCallback(() => {
    visibleInputFocusedRef.current = false;
    if (resumeHiddenFocusTimerRef.current) clearTimeout(resumeHiddenFocusTimerRef.current);
    resumeHiddenFocusTimerRef.current = setTimeout(() => {
      resumeHiddenFocusTimerRef.current = null;
      if (!visibleInputFocusedRef.current) resumeHiddenInputFocus();
    }, 250);
  }, [resumeHiddenInputFocus]);
  useEffect(
    () => () => {
      if (resumeHiddenFocusTimerRef.current) clearTimeout(resumeHiddenFocusTimerRef.current);
    },
    [],
  );
  // 新建时价格框自动聚焦会暂停隐藏扫码框；关闭弹层后复位，下次打开重新判断。
  useEffect(() => {
    if (!visible) visibleInputFocusedRef.current = false;
  }, [visible]);

  const patchDraftField = (next: Partial<QuickPromotionDraft>) => {
    setDraft((current) => (current ? { ...current, ...next } : current));
    setErrorText(null);
  };
  const routeScan = (barcode: string) => scanBarcodeRef.current(barcode);
  // 输入框聚焦时扫码枪会把条码写进框里：恢复原值，条码改为加入同组商品。
  const searchGuard = useScannerGuardedInput<TextInput>({
    value: searchText,
    onChangeText: setSearchText,
    onScan: routeScan,
  });
  const nameGuard = useScannerGuardedInput<TextInput>({
    value: draft?.name ?? "",
    onChangeText: (name) => patchDraftField({ name: name.replace(/[\r\n]+/g, " ") }),
    onScan: routeScan,
  });
  const priceGuard = useScannerGuardedInput<TextInput>({
    value: draft?.fixedPriceText ?? "",
    onChangeText: (text) => patchDraftField({ fixedPriceText: sanitizeMoneyInput(text) }),
    onScan: routeScan,
  });
  const maxPerOrderGuard = useScannerGuardedInput<TextInput>({
    value: draft?.maxPerOrderText ?? "",
    onChangeText: (text) => patchDraftField({ maxPerOrderText: text.replace(/[^\d]/g, "") }),
    onScan: routeScan,
  });

  const evaluation = useMemo(
    () => (draft ? evaluateQuickPromotionDraft(draft, anchorProductCode) : null),
    [draft, anchorProductCode],
  );
  const defaultName = draft
    ? t("promotion.form.defaultName", { name: anchorProductName, count: draft.applyQuantity })
    : "";

  if (!draft || !evaluation) {
    return null;
  }

  const patch = (next: Partial<QuickPromotionDraft>) => {
    setDraft((current) => (current ? { ...current, ...next } : current));
    setErrorText(null);
  };

  const resolveError = (error: unknown) =>
    isPromotionExclusiveConflictError(error)
      ? t("promotion.messages.exclusiveConflict")
      : resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "promotion.messages.actionFailed" });

  /**
   * 保存：先做本地冲突预检（停用保存不预检），有冲突切到冲突提示；
   * 「仍然保存」「改到 X 结束」都会跳过预检直接提交，服务端校验仍是最终防线。
   */
  const save = async (isEnabled: boolean, override?: QuickPromotionDraft, skipPrecheck = false) => {
    const target = override ?? draft;
    if (saving || !evaluateQuickPromotionDraft(target, anchorProductCode).canSave) return;
    if (!skipPrecheck) {
      const conflicts = findExclusiveConflicts(toConflictCandidate(target, isEnabled), existingPromotions);
      if (conflicts.length > 0) {
        setConflictState({
          isEnabled,
          conflicts,
          suggestedEnd: suggestEndBeforeConflicts(target.effectiveStart, conflicts),
        });
        setView("conflict");
        return;
      }
    }
    setSaving(true);
    setErrorText(null);
    try {
      const payload = buildQuickPromotionPayload(target, { storeCode, isEnabled, defaultName });
      if (mode === "edit" && target.id) {
        await updatePromotion(target.id, payload);
      } else {
        await createPromotion(payload);
      }
      onSaved(mode);
    } catch (error) {
      setErrorText(resolveError(error));
      setView("form");
    } finally {
      setSaving(false);
    }
  };

  const handleEndBefore = () => {
    if (!conflictState?.suggestedEnd) return;
    const next = { ...draft, rangeKey: "custom" as const, effectiveEnd: conflictState.suggestedEnd };
    setDraft(next);
    void save(conflictState.isEnabled, next, true);
  };

  /** 加入同组商品：扫码枪扫入或手输后回车 / 点「添加」；唯一命中直接加入，多个命中让店员选。 */
  const handleSearch = async (input: string = searchText, queued = false) => {
    const keyword = input.trim();
    if (!keyword || (searching && !queued)) return;
    const generation = ++searchGenerationRef.current;
    setSearching(true);
    setSearchMessage(null);
    setSearchCandidates([]);
    try {
      const items = (await lookupProducts({ keyword, storeCode })).filter((item) => item.productCode);
      if (generation !== searchGenerationRef.current) return;
      if (items.length === 0) {
        setSearchMessage(t("promotion.form.productSearchEmpty"));
      } else if (items.length === 1) {
        await addProduct(items[0], generation);
      } else {
        setSearchCandidates(items.slice(0, 8));
      }
    } catch {
      if (generation === searchGenerationRef.current) {
        setSearchMessage(t("promotion.form.productSearchFailed"));
      }
    } finally {
      if (generation === searchGenerationRef.current) setSearching(false);
    }
  };

  const addProduct = async (item: ProductLookupItem, generation = searchGenerationRef.current) => {
    // 尽力取本店零售价，用于「买满也没有优惠」提醒；取不到不影响加入。
    let retailPrice: number | null = null;
    try {
      const detail = await getProductFastDetail(item.productCode, storeCode);
      retailPrice = detail.storePrice?.retailPrice ?? null;
    } catch {
      retailPrice = null;
    }
    if (generation !== searchGenerationRef.current) return;
    setDraft((current) =>
      current
        ? addDraftProduct(current, {
            productCode: item.productCode,
            productName: item.productName,
            retailPrice,
            unitWeight: 1,
          })
        : current,
    );
    setSearchText("");
    setSearchCandidates([]);
    setSearchMessage(null);
  };

  // 硬件扫码排队逐个查找加入，连续快扫不会因上一条还在查而被丢掉。
  scanBarcodeRef.current = (barcode: string) => {
    setProductsExpanded(true);
    scanQueueRef.current = scanQueueRef.current.then(() => handleSearch(barcode, true)).catch(() => undefined);
  };

  const isCreate = mode === "create";
  const issues = evaluation.issues;
  const savings = evaluation.savings;
  const maxPerOrder = parseMaxPerOrderText(draft.maxPerOrderText);
  const moreSummary = t("promotion.form.moreSummary", {
    exclusive: t(draft.isExclusive ? "promotion.exclusive" : "promotion.stackable"),
    priority: draft.priority,
    max: maxPerOrder ? t("promotion.form.maxLimited", { count: maxPerOrder }) : t("promotion.form.maxUnlimited"),
  });

  // ---------------- 日期选择（替换表单内容，避免在 Modal 里再叠 Portal） ----------------
  if (view === "pickStart" || view === "pickEnd") {
    const edge = view === "pickStart" ? "start" : "end";
    return (
      <BusinessSheet
        visible={visible}
        title={t(edge === "start" ? "promotion.form.startDate" : "promotion.form.endDate")}
        onDismiss={() => setView("form")}
      >
        <MonthDatePicker
          value={toLocalDateString(edge === "start" ? draft.effectiveStart : draft.effectiveEnd)}
          minDate={edge === "end" ? toLocalDateString(draft.effectiveStart) : undefined}
          onChange={(date) => {
            setDraft((current) => (current ? applyCustomDate(current, edge, date) : current));
            setView("form");
          }}
        />
      </BusinessSheet>
    );
  }

  // ---------------- 冲突提示 ----------------
  if (view === "conflict" && conflictState) {
    return (
      <BusinessSheet
        visible={visible}
        title={t("promotion.conflict.title")}
        onDismiss={() => {
          if (!saving) setView("form");
        }}
        dismissable={!saving}
        footer={(
          <ProductPromotionConflictFooter
            suggestedEnd={conflictState.suggestedEnd}
            saving={saving}
            onEndBefore={handleEndBefore}
            onSaveAnyway={() => void save(conflictState.isEnabled, undefined, true)}
          />
        )}
      >
        <ProductPromotionConflictContent
          state={conflictState}
          candidateName={resolveDraftName(draft, defaultName)}
          candidatePriority={draft.priority}
          candidateStart={draft.effectiveStart}
          candidateEnd={draft.effectiveEnd}
          candidateExclusive={draft.isExclusive}
        />
      </BusinessSheet>
    );
  }

  // ---------------- 表单 ----------------
  const notDiscount = issues.includes("notDiscount");
  const previewTone = notDiscount ? "danger" : evaluation.nonDiscounted.length > 0 ? "warning" : "ok";

  return (
    <BusinessSheet
      visible={visible}
      title={t(isCreate ? "promotion.form.createTitle" : "promotion.form.editTitle")}
      onDismiss={onDismiss}
      dismissable={!saving}
      footer={(
        <View style={styles.footer}>
          {isCreate ? (
            <>
              <Button
                mode="outlined"
                onPress={() => void save(false)}
                disabled={!evaluation.canSave || saving}
                style={[styles.footerButton, styles.footerSecondary]}
                contentStyle={styles.buttonContent}
              >
                {t("promotion.form.saveDisabled")}
              </Button>
              <Button
                mode="contained"
                onPress={() => void save(true)}
                disabled={!evaluation.canSave || saving}
                loading={saving}
                style={[styles.footerButton, styles.footerPrimary]}
                contentStyle={styles.buttonContent}
              >
                {t("promotion.form.saveEnabled")}
              </Button>
            </>
          ) : (
            <>
              <Button
                mode="outlined"
                onPress={onDismiss}
                disabled={saving}
                style={[styles.footerButton, styles.footerSecondary]}
                contentStyle={styles.buttonContent}
              >
                {t("common:actions.cancel")}
              </Button>
              <Button
                mode="contained"
                onPress={() => void save(draft.isEnabled)}
                disabled={!evaluation.canSave || saving}
                loading={saving}
                style={[styles.footerButton, styles.footerPrimary]}
                contentStyle={styles.buttonContent}
              >
                {t("promotion.form.save")}
              </Button>
            </>
          )}
        </View>
      )}
    >
      {errorText ? (
        <View style={styles.errorBanner} accessibilityLiveRegion="polite">
          <Icon source="alert-circle-outline" size={16} color={HB_COLORS.danger} />
          <Text style={styles.errorText}>{errorText}</Text>
        </View>
      ) : null}

      {/* 入口商品 */}
      <View style={styles.productChip}>
        <Icon source="tag-outline" size={16} color={HB_COLORS.textSecondary} />
        <Text style={styles.productChipName} numberOfLines={1}>{anchorProductName || anchorProductCode}</Text>
        <Text style={styles.productChipPrice}>
          {anchorRetailPrice != null ? `$${formatMoney(anchorRetailPrice)}` : "--"}
        </Text>
      </View>

      {/* 加入同组商品 */}
      <Pressable
        accessibilityRole="button"
        accessibilityState={{ expanded: productsExpanded }}
        onPress={() => setProductsExpanded((current) => !current)}
        style={({ pressed }) => [styles.linkRow, pressed ? styles.pressed : null]}
      >
        <Icon source="barcode-scan" size={16} color={HB_COLORS.action} />
        <Text style={styles.linkText}>{t("promotion.form.addProducts", { count: draft.products.length })}</Text>
        <Icon source={productsExpanded ? "chevron-up" : "chevron-down"} size={16} color={HB_COLORS.action} />
      </Pressable>
      {productsExpanded ? (
        <View style={styles.productsBox}>
          <View style={styles.searchRow}>
            <TextInput
              ref={searchGuard.inputRef}
              style={[styles.input, styles.searchInput]}
              value={searchText}
              onChangeText={searchGuard.onChangeText}
              onFocus={handleVisibleInputFocus}
              onBlur={handleVisibleInputBlur}
              placeholder={t("promotion.form.productSearchPlaceholder")}
              placeholderTextColor="#98A2B3"
              autoCapitalize="none"
              autoCorrect={false}
              returnKeyType="search"
              // 扫码枪模拟键盘输入并回车，直接触发查找。
              onSubmitEditing={searchGuard.guardSubmit(() => void handleSearch())}
              submitBehavior="submit"
              editable={!saving}
              accessibilityLabel={t("promotion.form.productSearchPlaceholder")}
            />
            <Button
              mode="contained-tonal"
              onPress={() => void handleSearch()}
              disabled={!searchText.trim() || searching || saving}
              compact
              style={styles.searchButton}
              contentStyle={styles.searchButtonContent}
            >
              {t("promotion.form.productSearchAdd")}
            </Button>
          </View>
          {searching ? <ActivityIndicator size="small" style={styles.searchSpinner} /> : null}
          {searchMessage ? <Text style={styles.helperText}>{searchMessage}</Text> : null}
          {searchCandidates.length > 0 ? (
            <View style={styles.candidateList}>
              <Text style={styles.helperText}>{t("promotion.form.productSearchPick")}</Text>
              {searchCandidates.map((item) => (
                <Pressable
                  key={item.productCode}
                  accessibilityRole="button"
                  onPress={() => void addProduct(item)}
                  style={({ pressed }) => [styles.candidateRow, pressed ? styles.pressed : null]}
                >
                  <Text style={styles.candidateName} numberOfLines={1}>{item.productName || item.productCode}</Text>
                  <Text style={styles.candidateCode} numberOfLines={1}>{item.barcode || item.productCode}</Text>
                </Pressable>
              ))}
            </View>
          ) : null}
          {draft.products.map((product) => {
            const isAnchor = product.productCode === anchorProductCode;
            return (
              <View key={product.productCode} style={styles.selectedRow}>
                <Text style={styles.selectedName} numberOfLines={1}>
                  {product.productName || product.productCode}
                </Text>
                <Text style={styles.selectedPrice}>
                  {product.retailPrice != null ? `$${formatMoney(product.retailPrice)}` : ""}
                </Text>
                {isAnchor ? (
                  <Text style={styles.selectedTag}>{t("promotion.form.productCurrent")}</Text>
                ) : (
                  <Pressable
                    accessibilityRole="button"
                    accessibilityLabel={`${t("promotion.form.productRemove")} ${product.productName || product.productCode}`}
                    hitSlop={8}
                    onPress={() => setDraft((current) => (current ? removeDraftProduct(current, product.productCode, anchorProductCode) : current))}
                  >
                    <Icon source="close-circle-outline" size={18} color={HB_COLORS.textSecondary} />
                  </Pressable>
                )}
              </View>
            );
          })}
        </View>
      ) : null}

      {/* 促销名称 */}
      <View style={styles.field}>
        <Text style={styles.label}>{t("promotion.form.name")}</Text>
        <TextInput
          ref={nameGuard.inputRef}
          style={styles.input}
          value={draft.name}
          onChangeText={nameGuard.onChangeText}
          onFocus={handleVisibleInputFocus}
          onBlur={handleVisibleInputBlur}
          placeholder={defaultName}
          placeholderTextColor="#98A2B3"
          maxLength={100}
          editable={!saving}
          accessibilityLabel={t("promotion.form.name")}
        />
      </View>

      {/* 买满数量 + 组合总价 */}
      <View style={styles.twoColumns}>
        <View style={[styles.field, styles.column]}>
          <Text style={styles.label}>{t("promotion.form.applyQuantity")}</Text>
          <View style={styles.stepper}>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("promotion.form.decrease")}
              disabled={draft.applyQuantity <= MIN_APPLY_QUANTITY || saving}
              onPress={() => patch({ applyQuantity: Math.max(MIN_APPLY_QUANTITY, draft.applyQuantity - 1) })}
              style={styles.stepperButton}
            >
              <Icon
                source="minus"
                size={20}
                color={draft.applyQuantity <= MIN_APPLY_QUANTITY ? HB_COLORS.outline : HB_COLORS.action}
              />
            </Pressable>
            <Text style={styles.stepperValue}>{draft.applyQuantity}</Text>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("promotion.form.increase")}
              disabled={draft.applyQuantity >= MAX_APPLY_QUANTITY || saving}
              onPress={() => patch({ applyQuantity: Math.min(MAX_APPLY_QUANTITY, draft.applyQuantity + 1) })}
              style={styles.stepperButton}
            >
              <Icon source="plus" size={20} color={HB_COLORS.action} />
            </Pressable>
          </View>
        </View>
        <View style={[styles.field, styles.column]}>
          <Text style={styles.label}>{t("promotion.form.fixedPrice")}</Text>
          <View style={[styles.priceInputWrap, notDiscount ? styles.inputError : styles.inputFocusTone]}>
            <Text style={styles.pricePrefix}>$</Text>
            <TextInput
              ref={priceGuard.inputRef}
              style={styles.priceInput}
              value={draft.fixedPriceText}
              onChangeText={priceGuard.onChangeText}
              onFocus={handleVisibleInputFocus}
              onBlur={handleVisibleInputBlur}
              keyboardType="decimal-pad"
              placeholder="0.00"
              placeholderTextColor="#98A2B3"
              editable={!saving}
              autoFocus={isCreate}
              accessibilityLabel={t("promotion.form.fixedPrice")}
            />
          </View>
        </View>
      </View>

      {/* 实时预览：单件均价与折扣率 */}
      <View
        style={[
          styles.preview,
          previewTone === "danger" ? styles.previewDanger : previewTone === "warning" ? styles.previewWarning : null,
        ]}
        accessibilityLiveRegion="polite"
      >
        {evaluation.fixedPrice == null || evaluation.fixedPrice <= 0 ? (
          <Text style={styles.previewMuted}>{t("promotion.form.pricePending")}</Text>
        ) : (
          <>
            <Text style={[styles.previewStrong, toneText(previewTone)]}>
              {t("promotion.form.previewRule", {
                count: draft.applyQuantity,
                price: formatMoney(evaluation.fixedPrice),
                unit: formatMoney(savings?.unitPrice ?? evaluation.fixedPrice / draft.applyQuantity),
              })}
            </Text>
            {notDiscount && anchorRetailPrice != null ? (
              <Text style={[styles.previewText, toneText("danger")]}>
                {t("promotion.form.notDiscount", { total: formatMoney(anchorRetailPrice * draft.applyQuantity) })}
              </Text>
            ) : savings ? (
              savings.isDiscount ? (
                <Text style={[styles.previewText, toneText(previewTone)]}>
                  {t("promotion.form.previewSaving", {
                    retail: formatMoney(anchorRetailPrice),
                    percent: formatPercent(savings.discountRate),
                    amount: formatMoney(savings.savingPerBundle),
                  })}
                </Text>
              ) : null
            ) : (
              <Text style={styles.previewMuted}>{t("promotion.form.previewNoRetail")}</Text>
            )}
            {evaluation.nonDiscounted.length > 0 ? (
              <Text style={[styles.previewText, toneText("warning")]}>
                {t("promotion.form.nonDiscounted", {
                  count: draft.applyQuantity,
                  names: evaluation.nonDiscounted.map((item) => item.productName || item.productCode).join("、"),
                })}
              </Text>
            ) : null}
          </>
        )}
      </View>

      {/* 有效期 */}
      <View style={styles.field}>
        <Text style={styles.label}>{t("promotion.form.validity")}</Text>
        <View style={styles.chips}>
          {QUICK_RANGES.map((key) => (
            <QuickChip
              key={key}
              label={t(`promotion.form.quick.${key}`)}
              selected={draft.rangeKey === key}
              disabled={saving}
              onPress={() => {
                setDraft((current) => (current ? applyQuickRange(current, key) : current));
                setErrorText(null);
              }}
            />
          ))}
          <QuickChip
            label={t("promotion.form.quick.custom")}
            selected={draft.rangeKey === "custom"}
            disabled={saving}
            onPress={() => patch({ rangeKey: "custom" })}
          />
        </View>
        {draft.rangeKey === "custom" ? (
          <View style={styles.customRange}>
            <DateField
              label={t("promotion.form.startDate")}
              value={formatPromotionDateTime(draft.effectiveStart)}
              disabled={saving}
              onPress={() => setView("pickStart")}
            />
            <DateField
              label={t("promotion.form.endDate")}
              value={formatPromotionDateTime(draft.effectiveEnd)}
              disabled={saving}
              onPress={() => setView("pickEnd")}
            />
          </View>
        ) : (
          <Text style={styles.rangeText}>
            {t("promotion.dateRange", {
              start: formatPromotionDateTime(draft.effectiveStart),
              end: formatPromotionDateTime(draft.effectiveEnd),
            })}
          </Text>
        )}
        {issues.includes("rangeInvalid") ? (
          <Text style={styles.fieldError}>{t("promotion.form.rangeInvalid")}</Text>
        ) : issues.includes("rangeEnded") ? (
          <Text style={styles.fieldError}>{t("promotion.form.rangeEnded")}</Text>
        ) : null}
      </View>

      {/* 适用分店：固定为当前店 */}
      <View style={styles.field}>
        <Text style={styles.label}>{t("promotion.form.store")}</Text>
        <View style={styles.readonlyBox}>
          <Icon source="storefront-outline" size={18} color={HB_COLORS.textSecondary} />
          <Text style={styles.readonlyText} numberOfLines={1}>
            {t("promotion.form.storeValue", { store: storeName || storeCode })}
          </Text>
        </View>
      </View>

      {/* 更多设置 */}
      <View style={styles.moreBlock}>
        <Pressable
          accessibilityRole="button"
          accessibilityState={{ expanded: moreExpanded }}
          onPress={() => setMoreExpanded((current) => !current)}
          style={({ pressed }) => [styles.moreHeader, pressed ? styles.pressed : null]}
        >
          <Text style={styles.moreTitle} numberOfLines={2}>
            {`${t("promotion.form.more")}：${moreSummary}`}
          </Text>
          <Icon source={moreExpanded ? "chevron-up" : "chevron-down"} size={20} color={HB_COLORS.textSecondary} />
        </Pressable>
        {moreExpanded ? (
          <View style={styles.moreBody}>
            <View style={styles.switchRow}>
              <Text style={styles.switchLabel}>{t("promotion.form.exclusiveLabel")}</Text>
              <Switch
                value={draft.isExclusive}
                onValueChange={(isExclusive) => patch({ isExclusive })}
                disabled={saving}
                accessibilityLabel={t("promotion.form.exclusiveLabel")}
              />
            </View>
            <View style={styles.field}>
              <Text style={styles.label}>{t("promotion.form.maxPerOrderLabel")}</Text>
              <TextInput
                style={[styles.input, issues.includes("maxPerOrderInvalid") ? styles.inputError : null]}
                ref={maxPerOrderGuard.inputRef}
                value={draft.maxPerOrderText}
                onChangeText={maxPerOrderGuard.onChangeText}
                onFocus={handleVisibleInputFocus}
                onBlur={handleVisibleInputBlur}
                keyboardType="number-pad"
                placeholder={t("promotion.form.maxPerOrderPlaceholder")}
                placeholderTextColor="#98A2B3"
                editable={!saving}
                accessibilityLabel={t("promotion.form.maxPerOrderLabel")}
              />
              {issues.includes("maxPerOrderInvalid") ? (
                <Text style={styles.fieldError}>{t("promotion.form.maxPerOrderInvalid")}</Text>
              ) : null}
            </View>
            {/* 后端店级新建强制优先级 0，这里只显示不提供修改。 */}
            <View style={styles.field}>
              <Text style={styles.label}>{`${t("promotion.form.priorityLabel")}：${draft.priority}`}</Text>
              <Text style={styles.helperText}>{t("promotion.form.priorityHint")}</Text>
            </View>
          </View>
        ) : null}
      </View>

      {/* 弹层内的隐藏扫码接收框（必须渲染在 Modal 里才能拿到按键）；不接收时卸载。 */}
      {hidEnabled && hidScanner.textInputProps ? (
        <TextInput style={styles.hiddenScannerInput} {...hidScanner.textInputProps} />
      ) : null}
    </BusinessSheet>
  );
}

function toneText(tone: "ok" | "warning" | "danger") {
  return tone === "danger" ? styles.toneDanger : tone === "warning" ? styles.toneWarning : styles.toneOk;
}

function QuickChip({
  label,
  selected,
  disabled,
  onPress,
}: {
  label: string;
  selected: boolean;
  disabled?: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected, disabled }}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [styles.chip, selected ? styles.chipSelected : null, pressed ? styles.pressed : null]}
    >
      <Text style={[styles.chipText, selected ? styles.chipTextSelected : null]}>{label}</Text>
    </Pressable>
  );
}

function DateField({
  label,
  value,
  disabled,
  onPress,
}: {
  label: string;
  value: string;
  disabled?: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${label} ${value}`}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [styles.dateField, pressed ? styles.pressed : null]}
    >
      <Text style={styles.dateLabel}>{label}</Text>
      <View style={styles.dateValueRow}>
        <Icon source="calendar-month-outline" size={16} color={HB_COLORS.textSecondary} />
        <Text style={styles.dateValue}>{value}</Text>
      </View>
    </Pressable>
  );
}

const PROMO_OK_BG = "#ECFDF3";

const styles = StyleSheet.create({
  hiddenScannerInput: { position: "absolute", width: 1, height: 1, opacity: 0 },
  errorBanner: {
    flexDirection: "row",
    gap: 6,
    alignItems: "flex-start",
    backgroundColor: "#FEF3F2",
    borderRadius: HB_RADIUS.control,
    padding: HB_SPACING.xs,
  },
  errorText: { flex: 1, fontSize: 13, lineHeight: 18, color: HB_COLORS.danger },
  productChip: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: HB_RADIUS.control,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 10,
  },
  productChipName: { flex: 1, minWidth: 0, fontSize: 14, fontWeight: "600", color: HB_COLORS.textPrimary },
  productChipPrice: { fontSize: 14, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  linkRow: { flexDirection: "row", alignItems: "center", gap: 6, minHeight: 32 },
  linkText: { flexShrink: 1, fontSize: 13, fontWeight: "700", color: HB_COLORS.action },
  productsBox: {
    gap: HB_SPACING.xs,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.control,
    padding: HB_SPACING.xs,
  },
  searchRow: { flexDirection: "row", gap: HB_SPACING.xs, alignItems: "center" },
  searchInput: { flex: 1, minHeight: 44 },
  searchButton: { borderRadius: HB_RADIUS.control },
  searchButtonContent: { minHeight: 44 },
  searchSpinner: { alignSelf: "flex-start" },
  candidateList: { gap: 4 },
  candidateRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    minHeight: 40,
    paddingHorizontal: HB_SPACING.xs,
    borderRadius: HB_RADIUS.control,
    backgroundColor: "#EFF8FF",
  },
  candidateName: { flex: 1, minWidth: 0, fontSize: 13, color: HB_COLORS.textPrimary },
  candidateCode: { fontSize: 12, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  selectedRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, minHeight: 32 },
  selectedName: { flex: 1, minWidth: 0, fontSize: 13, color: HB_COLORS.textPrimary },
  selectedPrice: { fontSize: 12, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  selectedTag: { fontSize: 11, fontWeight: "700", color: HB_COLORS.action },
  field: { gap: 6 },
  label: { fontSize: 12, fontWeight: "700", color: HB_COLORS.textSecondary },
  input: {
    minHeight: 48,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    paddingHorizontal: HB_SPACING.sm,
    fontSize: 16,
    color: HB_COLORS.textPrimary,
    backgroundColor: HB_COLORS.white,
  },
  inputError: { borderColor: HB_COLORS.danger },
  inputFocusTone: { borderColor: HB_COLORS.brand },
  twoColumns: { flexDirection: "row", gap: HB_SPACING.sm },
  column: { flex: 1, minWidth: 0 },
  stepper: {
    flexDirection: "row",
    alignItems: "center",
    minHeight: 48,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.white,
  },
  stepperButton: { width: 48, minHeight: 46, alignItems: "center", justifyContent: "center" },
  stepperValue: {
    flex: 1,
    textAlign: "center",
    fontSize: 18,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  priceInputWrap: {
    flexDirection: "row",
    alignItems: "center",
    minHeight: 48,
    borderWidth: 1.5,
    borderRadius: HB_RADIUS.control,
    paddingHorizontal: HB_SPACING.sm,
    backgroundColor: HB_COLORS.white,
  },
  pricePrefix: { fontSize: 16, fontWeight: "700", color: HB_COLORS.textPrimary, marginRight: 6 },
  priceInput: {
    flex: 1,
    minHeight: 44,
    fontSize: 18,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  preview: { gap: 2, borderRadius: HB_RADIUS.control, padding: 10, backgroundColor: PROMO_OK_BG },
  previewDanger: { backgroundColor: "#FEF3F2" },
  previewWarning: { backgroundColor: "#FFFAEB" },
  previewStrong: { fontSize: 14, fontWeight: "800", fontVariant: ["tabular-nums"] },
  previewText: { fontSize: 13, lineHeight: 19, fontVariant: ["tabular-nums"] },
  previewMuted: { fontSize: 13, lineHeight: 19, color: HB_COLORS.textSecondary },
  toneOk: { color: HB_COLORS.success },
  toneWarning: { color: HB_COLORS.warning },
  toneDanger: { color: HB_COLORS.danger },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: 6 },
  chip: {
    minHeight: 34,
    paddingHorizontal: 12,
    borderRadius: 17,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
    justifyContent: "center",
  },
  chipSelected: { borderColor: HB_COLORS.brand, backgroundColor: "#EFF8FF" },
  chipText: { fontSize: 13, fontWeight: "700", color: HB_COLORS.textSecondary },
  chipTextSelected: { color: HB_COLORS.action },
  rangeText: { fontSize: 13, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  customRange: { flexDirection: "row", gap: HB_SPACING.xs },
  dateField: {
    flex: 1,
    gap: 2,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 6,
  },
  dateLabel: { fontSize: 11, color: HB_COLORS.textSecondary },
  dateValueRow: { flexDirection: "row", alignItems: "center", gap: 4 },
  dateValue: { fontSize: 14, fontWeight: "600", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  fieldError: { fontSize: 12, color: HB_COLORS.danger },
  helperText: { fontSize: 12, lineHeight: 17, color: HB_COLORS.textSecondary },
  readonlyBox: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    minHeight: 48,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.control,
    paddingHorizontal: HB_SPACING.sm,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  readonlyText: { flex: 1, minWidth: 0, fontSize: 15, color: HB_COLORS.textPrimary },
  moreBlock: {
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
    paddingTop: HB_SPACING.xs,
    gap: HB_SPACING.xs,
  },
  moreHeader: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, minHeight: 40 },
  moreTitle: { flex: 1, fontSize: 13, fontWeight: "700", color: HB_COLORS.textPrimary },
  moreBody: { gap: HB_SPACING.sm },
  switchRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  switchLabel: { flex: 1, fontSize: 14, color: HB_COLORS.textPrimary },
  footer: { flexDirection: "row", gap: HB_SPACING.xs },
  footerButton: { borderRadius: HB_RADIUS.control },
  footerSecondary: { flex: 1 },
  footerPrimary: { flex: 1.6 },
  buttonContent: { minHeight: 44 },
  pressed: { opacity: 0.72 },
});

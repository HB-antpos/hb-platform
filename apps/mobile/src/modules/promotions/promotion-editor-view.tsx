/**
 * 新建 / 编辑本店独享促销（设计稿 ②）：规则 → 有效期 → 参与商品 → 适用与高级，底部固定「提前结束 / 保存」。
 * - 新建一律本店独享，分店固定为当前店；优先级只读（新建固定 0，编辑保持原值）。
 * - 逐个商品校验「组合价 ≥ 零售价 × 数量」：多商品只提醒不拦，单商品不优惠禁止保存。
 * - 不提供删除（后端是物理删除）；结束活动用「提前结束」或列表开关。
 */
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Alert, KeyboardAvoidingView, Platform, Pressable, ScrollView, StyleSheet, View } from "react-native";
import { Button, Dialog, Icon, IconButton, Portal, Switch, Text, TextInput } from "react-native-paper";
import { DateRangePickerModal } from "@/components/ui/DateRangePickerModal";
import type { ProductLookupItem } from "@/modules/product-maintenance/types";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { createPromotion, endPromotionNow, fetchPromotions, updatePromotion } from "./api";
import {
  MIN_APPLY_QUANTITY,
  buildQuickRange,
  canEndPromotionNow,
  findExclusiveConflicts,
  formatMoney,
  formatPromotionDateTime,
  getBundleSavings,
  getPromotionStatus,
  getUnitAveragePrice,
  isScopeVisible,
  suggestEndBeforeConflicts,
  type QuickRangeKey,
} from "./promotion-logic";
import {
  fetchPromotionProductInfo,
  resolveBarcodeToProduct,
  searchPromotionProducts,
  type PromotionProductInfo,
} from "./promotion-product-lookup";
import {
  addDraftProduct,
  buildFormValuesFromDraft,
  combineLocalDateTime,
  createEmptyDraft,
  draftFromDetail,
  getDraftFingerprint,
  getDraftNonDiscountedProducts,
  getPromotionErrorKind,
  parseMoneyInput,
  parseTimeInput,
  runWithConcurrency,
  splitLocalDateTime,
  stepApplyQuantity,
  stepUnitWeight,
  validateDraft,
  type PromotionDraft,
  type PromotionDraftIssue,
  type PromotionDraftProduct,
} from "./promotion-screen-model";
import { Badge, PROMO_COLORS, ProductSearchSheet, PromotionScanSheet, SectionCard, StatusBadge, TABULAR, WarningBox } from "./promotion-ui";
import type { PromotionDetail, PromotionListItem } from "./types";

type TranslateFn = (key: string, options?: Record<string, unknown>) => string;

/** 一次最多显示的商品行数，超出后「显示更多」；名称 / 零售价只查已显示的行。 */
const PRODUCT_PAGE = 30;
const LOOKUP_CONCURRENCY = 4;

export interface PromotionEditorViewProps {
  t: TranslateFn;
  storeCode: string;
  storeName: string;
  /** 编辑时传入完整详情；新建时为 null。 */
  detail: PromotionDetail | null;
  localeTag: string;
  weekdayLabels: string[];
  onClose: (result: { saved: boolean }) => void;
  onMessage: (message: string) => void;
  getErrorMessage: (error: unknown, fallbackKey: string) => string;
  /** 父组件注册返回键处理：有未保存修改时先确认。 */
  registerBackHandler: (handler: (() => void) | null) => void;
}

export function PromotionEditorView({
  t,
  storeCode,
  storeName,
  detail,
  localeTag,
  weekdayLabels,
  onClose,
  onMessage,
  getErrorMessage,
  registerBackHandler,
}: PromotionEditorViewProps) {
  const isEdit = Boolean(detail?.id);
  const [draft, setDraft] = useState<PromotionDraft>(() => (detail ? draftFromDetail(detail) : createEmptyDraft()));
  const initialFingerprint = useRef(getDraftFingerprint(draft));
  const [showErrors, setShowErrors] = useState(false);
  const [saving, setSaving] = useState(false);
  const [visibleCount, setVisibleCount] = useState(PRODUCT_PAGE);
  const [dateVisible, setDateVisible] = useState(false);
  const [startTimeText, setStartTimeText] = useState(() => splitLocalDateTime(draft.effectiveStart)?.time ?? "00:00");
  const [endTimeText, setEndTimeText] = useState(() => splitLocalDateTime(draft.effectiveEnd)?.time ?? "23:59");
  const [scanVisible, setScanVisible] = useState(false);
  const [scanStatus, setScanStatus] = useState("");
  const [searchVisible, setSearchVisible] = useState(false);
  const [searchCandidates, setSearchCandidates] = useState<ProductLookupItem[] | null>(null);
  const [weightTarget, setWeightTarget] = useState<PromotionDraftProduct | null>(null);
  const [storePromotions, setStorePromotions] = useState<PromotionListItem[]>([]);
  const lookupInFlight = useRef(new Set<string>());
  const mountedRef = useRef(true);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  const dirty = getDraftFingerprint(draft) !== initialFingerprint.current;

  const requestClose = useCallback(() => {
    if (!dirty) {
      onClose({ saved: false });
      return;
    }
    Alert.alert(t("editor.discardTitle"), t("editor.discardMessage"), [
      { text: t("editor.keepEditing"), style: "cancel" },
      { text: t("editor.discard"), style: "destructive", onPress: () => onClose({ saved: false }) },
    ]);
  }, [dirty, onClose, t]);

  useEffect(() => {
    registerBackHandler(requestClose);
    return () => registerBackHandler(null);
  }, [registerBackHandler, requestClose]);

  // 预检用：取本店相关的促销（尽力而为，失败不影响保存，服务端排他校验才是最终防线）。
  useEffect(() => {
    let cancelled = false;
    void fetchPromotions({ storeCode, status: "all", page: 1, pageSize: 100 })
      .then((result) => {
        if (cancelled) return;
        const now = new Date();
        setStorePromotions(
          result.items.filter((item) => isScopeVisible(item.scopeType) && getPromotionStatus(item, now) !== "ended"),
        );
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [storeCode]);

  const applyProductInfo = useCallback((productCode: string, info: PromotionProductInfo | null) => {
    setDraft((current) => ({
      ...current,
      products: current.products.map((row) =>
        row.productCode === productCode
          ? info
            ? {
                ...row,
                productName: info.productName ?? row.productName,
                barcode: info.barcode ?? row.barcode,
                retailPrice: info.retailPrice,
                lookupState: "ready",
              }
            : { ...row, lookupState: "failed" }
          : row,
      ),
    }));
  }, []);

  // 已显示但还没查过的商品行：按固定并发查本店名称与零售价。
  const visibleProducts = draft.products.slice(0, visibleCount);
  const pendingCodes = visibleProducts
    .filter((row) => row.lookupState === "idle" && !lookupInFlight.current.has(row.productCode))
    .map((row) => row.productCode);
  const pendingKey = pendingCodes.join("|");
  useEffect(() => {
    if (!pendingKey) return;
    const codes = pendingKey.split("|");
    codes.forEach((code) => lookupInFlight.current.add(code));
    setDraft((current) => ({
      ...current,
      products: current.products.map((row) =>
        codes.includes(row.productCode) && row.lookupState === "idle" ? { ...row, lookupState: "loading" } : row,
      ),
    }));
    void runWithConcurrency(codes, LOOKUP_CONCURRENCY, async (code) => {
      try {
        const info = await fetchPromotionProductInfo(code, storeCode);
        if (mountedRef.current) applyProductInfo(code, info);
      } catch {
        if (mountedRef.current) applyProductInfo(code, null);
      } finally {
        lookupInFlight.current.delete(code);
      }
    });
  }, [applyProductInfo, pendingKey, storeCode]);

  const fixedPrice = parseMoneyInput(draft.fixedPriceText);
  const unitPrice = fixedPrice != null ? getUnitAveragePrice(draft.applyQuantity, fixedPrice) : null;
  const issues = useMemo(() => validateDraft(draft), [draft]);
  const nonDiscounted = useMemo(() => getDraftNonDiscountedProducts(draft), [draft]);
  const nonDiscountedCodes = useMemo(() => new Set(nonDiscounted.map((row) => row.productCode)), [nonDiscounted]);
  const addedCodes = useMemo(() => new Set(draft.products.map((row) => row.productCode)), [draft.products]);
  const hasIssue = (issue: PromotionDraftIssue) => showErrors && issues.includes(issue);
  const singleNotDiscount = issues.includes("notDiscount");

  const status = detail ? getPromotionStatus(detail) : null;
  const canEndNow = Boolean(detail && canEndPromotionNow(detail));

  const patchDraft = useCallback((patch: Partial<PromotionDraft>) => setDraft((current) => ({ ...current, ...patch })), []);

  // 最新草稿的引用：加商品时先同步判断是否重复（setState 的更新函数可能延后执行，不能从里面取结果）。
  const draftRef = useRef(draft);
  draftRef.current = draft;

  const addProduct = useCallback(
    (product: { productCode: string; productName?: string | null; barcode?: string | null; retailPrice?: number | null }, ready: boolean) => {
      const code = product.productCode.trim();
      if (!code || draftRef.current.products.some((row) => row.productCode === code)) return false;
      const entry = {
        productCode: code,
        productName: product.productName ?? null,
        barcode: product.barcode ?? null,
        retailPrice: product.retailPrice ?? null,
        lookupState: ready ? ("ready" as const) : ("idle" as const),
      };
      setDraft((current) => ({ ...current, products: addDraftProduct(current.products, entry).products }));
      // 立即更新引用，连续两次识别到同一商品时第二次能判为重复。
      draftRef.current = { ...draftRef.current, products: addDraftProduct(draftRef.current.products, entry).products };
      return true;
    },
    [],
  );

  /** 连续扫码添加：条码 → 本店商品（含零售价）；命中多个时转到候选列表让用户挑。 */
  const handleBarcode = useCallback(
    async (barcode: string) => {
      setScanStatus(t("scan.looking", { barcode }));
      try {
        const result = await resolveBarcodeToProduct(barcode, storeCode);
        if (result.kind === "notFound") {
          setScanStatus(t("scan.notFound", { barcode }));
          return false;
        }
        if (result.kind === "multiple") {
          setScanVisible(false);
          setSearchCandidates(result.candidates);
          setSearchVisible(true);
          return false;
        }
        const name = result.product.productName || result.product.productCode;
        const added = addProduct(result.product, true);
        setScanStatus(added ? t("scan.added", { name }) : t("scan.duplicated", { name }));
        return true;
      } catch (error) {
        setScanStatus(getErrorMessage(error, "scan.failed"));
        return false;
      }
    },
    [addProduct, getErrorMessage, storeCode, t],
  );

  const handlePickSearchResult = useCallback(
    (item: ProductLookupItem) => {
      const added = addProduct({ productCode: item.productCode, productName: item.productName, barcode: item.barcode }, false);
      onMessage(added ? t("scan.added", { name: item.productName || item.productCode }) : t("scan.duplicated", { name: item.productName || item.productCode }));
    },
    [addProduct, onMessage, t],
  );

  const searchProducts = useCallback((keyword: string) => searchPromotionProducts(keyword, storeCode), [storeCode]);

  const removeProduct = useCallback((productCode: string) => {
    setDraft((current) => ({ ...current, products: current.products.filter((row) => row.productCode !== productCode) }));
  }, []);

  const applyTime = useCallback(
    (which: "start" | "end", text: string) => {
      const parsed = parseTimeInput(text);
      const iso = which === "start" ? draft.effectiveStart : draft.effectiveEnd;
      const parts = splitLocalDateTime(iso);
      if (!parsed || !parts) {
        // 输入不合法：恢复原值。
        if (which === "start") setStartTimeText(parts?.time ?? "00:00");
        else setEndTimeText(parts?.time ?? "23:59");
        return;
      }
      const next = combineLocalDateTime(parts.date, parsed, which === "end");
      if (!next) return;
      const normalized = `${String(parsed.hours).padStart(2, "0")}:${String(parsed.minutes).padStart(2, "0")}`;
      if (which === "start") {
        setStartTimeText(normalized);
        patchDraft({ effectiveStart: next });
      } else {
        setEndTimeText(normalized);
        patchDraft({ effectiveEnd: next });
      }
    },
    [draft.effectiveEnd, draft.effectiveStart, patchDraft],
  );

  const applyDateRange = useCallback(
    (range: { from?: string; to?: string }) => {
      setDateVisible(false);
      const from = range.from;
      const to = range.to ?? range.from;
      if (!from || !to) return;
      // 换日期时保留已填的时间。
      const startTime = parseTimeInput(startTimeText) ?? { hours: 0, minutes: 0 };
      const endTime = parseTimeInput(endTimeText) ?? { hours: 23, minutes: 59 };
      const start = combineLocalDateTime(from, startTime, false);
      const end = combineLocalDateTime(to, endTime, true);
      if (start && end) patchDraft({ effectiveStart: start, effectiveEnd: end });
    },
    [endTimeText, patchDraft, startTimeText],
  );

  const applyQuickRange = useCallback(
    (key: QuickRangeKey) => {
      const range = buildQuickRange(key);
      setStartTimeText("00:00");
      setEndTimeText("23:59");
      patchDraft(range);
    },
    [patchDraft],
  );

  const doSave = useCallback(
    async (target: PromotionDraft) => {
      setSaving(true);
      try {
        const payload = buildFormValuesFromDraft(target, storeCode);
        if (target.id) await updatePromotion(target.id, payload);
        else await createPromotion(payload);
        onMessage(t("messages.saved"));
        onClose({ saved: true });
      } catch (error) {
        onMessage(
          getPromotionErrorKind(error) === "exclusiveConflict"
            ? t("messages.saveConflict")
            : getErrorMessage(error, "messages.saveFailed"),
        );
      } finally {
        if (mountedRef.current) setSaving(false);
      }
    },
    [getErrorMessage, onClose, onMessage, storeCode, t],
  );

  const handleSave = useCallback(() => {
    if (issues.length > 0) {
      setShowErrors(true);
      onMessage(t(`validation.${issues[0]}`));
      return;
    }
    // 本地排他冲突预检：只提醒，可选择「改到冲突前结束」或仍然保存。
    const conflicts = findExclusiveConflicts(
      {
        id: draft.id,
        effectiveStart: draft.effectiveStart,
        effectiveEnd: draft.effectiveEnd,
        isEnabled: draft.isEnabled,
        isExclusive: draft.isExclusive,
      },
      storePromotions,
    );
    if (conflicts.length === 0) {
      void doSave(draft);
      return;
    }
    const names = conflicts
      .slice(0, 3)
      .map((item) => `「${item.name}」`)
      .join("、");
    const suggestedEnd = suggestEndBeforeConflicts(draft.effectiveStart, conflicts);
    const buttons: Parameters<typeof Alert.alert>[2] = [
      { text: t("editor.backToEdit"), style: "cancel" },
    ];
    if (suggestedEnd) {
      buttons.push({
        text: t("conflict.endBefore", { time: formatPromotionDateTime(suggestedEnd) }),
        onPress: () => {
          const next = { ...draft, effectiveEnd: suggestedEnd };
          setDraft(next);
          setEndTimeText(splitLocalDateTime(suggestedEnd)?.time ?? "23:59");
          void doSave(next);
        },
      });
    }
    buttons.push({ text: t("conflict.saveAnyway"), onPress: () => void doSave(draft) });
    Alert.alert(t("conflict.title"), t("conflict.message", { count: conflicts.length, names }), buttons);
  }, [doSave, draft, issues, onMessage, storePromotions, t]);

  const handleEndNow = useCallback(() => {
    if (!detail) return;
    Alert.alert(t("endNow.title"), t("endNow.message", { name: detail.name }), [
      { text: t("common:actions.cancel"), style: "cancel" },
      {
        text: t("endNow.confirm"),
        style: "destructive",
        onPress: () => {
          setSaving(true);
          void endPromotionNow(detail.id, storeCode)
            .then(() => {
              onMessage(t("endNow.done"));
              onClose({ saved: true });
            })
            .catch((error: unknown) => onMessage(getErrorMessage(error, "endNow.failed")))
            .finally(() => {
              if (mountedRef.current) setSaving(false);
            });
        },
      },
    ]);
  }, [detail, getErrorMessage, onClose, onMessage, storeCode, t]);

  const startParts = splitLocalDateTime(draft.effectiveStart);
  const endParts = splitLocalDateTime(draft.effectiveEnd);
  const formatDateLabel = (date?: string) => (date ? date.slice(5).replace("-", "/") : "--");

  const renderProductRow = (row: PromotionDraftProduct) => {
    const flagged = nonDiscountedCodes.has(row.productCode) || (singleNotDiscount && draft.products.length === 1);
    const savings = fixedPrice != null ? getBundleSavings(row.retailPrice, draft.applyQuantity, fixedPrice) : null;
    let meta: string;
    if (row.lookupState === "loading" || row.lookupState === "idle") {
      meta = t("editor.productLoading");
    } else if (row.retailPrice == null) {
      meta = t("editor.noRetailPrice");
    } else if (flagged) {
      meta = t("editor.productNotDiscount", {
        retail: formatMoney(row.retailPrice),
        count: draft.applyQuantity,
        price: formatMoney(fixedPrice ?? 0),
      });
    } else if (savings) {
      meta = t("editor.productPrice", { retail: formatMoney(row.retailPrice), unit: formatMoney(savings.unitPrice) });
    } else {
      meta = t("editor.productRetail", { retail: formatMoney(row.retailPrice) });
    }
    const code = row.barcode || row.productCode;
    return (
      <Pressable
        key={row.productCode}
        onLongPress={() => setWeightTarget(row)}
        delayLongPress={350}
        accessibilityHint={t("editor.weightHint")}
        style={styles.productRow}
      >
        <View style={[styles.productIcon, flagged && styles.productIconFlagged]}>
          <Icon source="package-variant-closed" size={20} color={flagged ? HB_COLORS.danger : HB_COLORS.textSecondary} />
        </View>
        <View style={styles.productText}>
          <Text style={styles.productName} numberOfLines={1}>
            {row.productName || row.productCode}
          </Text>
          <Text style={[styles.productMeta, TABULAR, flagged && styles.dangerText]} numberOfLines={2}>
            {flagged ? meta : `${code} · ${meta}`}
          </Text>
          {row.unitWeight !== 1 ? (
            <View style={styles.weightBadge}>
              <Badge label={t("editor.weightBadge", { value: row.unitWeight })} tone="orange" />
            </View>
          ) : null}
        </View>
        <IconButton
          icon="dots-vertical"
          size={18}
          onPress={() => setWeightTarget(row)}
          accessibilityLabel={t("editor.weightMenu", { name: row.productName || row.productCode })}
          style={styles.rowIcon}
        />
        <IconButton
          icon="close"
          size={20}
          onPress={() => removeProduct(row.productCode)}
          accessibilityLabel={t("editor.removeProduct", { name: row.productName || row.productCode })}
          style={styles.rowIcon}
        />
      </Pressable>
    );
  };

  return (
    <View style={styles.flex}>
      <View style={styles.topBar}>
        <IconButton icon="close" onPress={requestClose} accessibilityLabel={t("common:actions.close")} />
        <Text style={styles.topTitle} numberOfLines={1}>
          {isEdit ? t("editor.editTitle") : t("editor.createTitle")}
        </Text>
        {status ? <StatusBadge status={status} label={t(`status.${status}`)} /> : null}
      </View>

      <KeyboardAvoidingView style={styles.flex} behavior={Platform.OS === "ios" ? "padding" : undefined}>
        <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
          {/* 规则：名称 + 买满数量 + 组合总价 */}
          <SectionCard>
            <Text style={styles.label}>{t("fields.name")}</Text>
            <TextInput
              mode="outlined"
              value={draft.name}
              placeholder={t("editor.namePlaceholder")}
              onChangeText={(name) => patchDraft({ name })}
              error={hasIssue("nameRequired")}
              style={styles.input}
              outlineStyle={styles.outline}
            />
            <View style={styles.ruleRow}>
              <View style={styles.ruleCol}>
                <Text style={styles.label}>{t("fields.applyQuantity")}</Text>
                <View style={[styles.stepper, hasIssue("quantityTooSmall") && styles.errorBorder]}>
                  <IconButton
                    icon="minus"
                    size={20}
                    disabled={draft.applyQuantity <= MIN_APPLY_QUANTITY}
                    onPress={() => patchDraft({ applyQuantity: stepApplyQuantity(draft.applyQuantity, -1) })}
                    accessibilityLabel={t("editor.decrease")}
                  />
                  <Text style={[styles.stepperValue, TABULAR]}>{draft.applyQuantity}</Text>
                  <IconButton
                    icon="plus"
                    size={20}
                    iconColor={HB_COLORS.action}
                    onPress={() => patchDraft({ applyQuantity: stepApplyQuantity(draft.applyQuantity, 1) })}
                    accessibilityLabel={t("editor.increase")}
                  />
                </View>
              </View>
              <View style={styles.ruleColWide}>
                <Text style={styles.label}>{t("fields.fixedPrice")}</Text>
                <TextInput
                  mode="outlined"
                  value={draft.fixedPriceText}
                  keyboardType="decimal-pad"
                  placeholder="0.00"
                  onChangeText={(text) => patchDraft({ fixedPriceText: text.replace(/[^\d.]/g, "") })}
                  error={hasIssue("priceRequired")}
                  left={<TextInput.Affix text="$" />}
                  style={[styles.input, TABULAR]}
                  outlineStyle={styles.outline}
                />
              </View>
            </View>
            {unitPrice != null && fixedPrice != null && fixedPrice > 0 ? (
              <Text style={[styles.hint, TABULAR]}>{t("editor.unitPriceHint", { price: formatMoney(unitPrice) })}</Text>
            ) : null}
          </SectionCard>

          {/* 有效期 */}
          <SectionCard title={t("editor.periodTitle")}>
            <View style={styles.dateRow}>
              <View style={[styles.dateField, hasIssue("rangeInvalid") && styles.errorBorder]}>
                <Pressable
                  accessibilityRole="button"
                  accessibilityLabel={t("fields.effectiveStart")}
                  onPress={() => setDateVisible(true)}
                  style={styles.datePress}
                >
                  <Icon source="calendar-blank-outline" size={18} color={HB_COLORS.textSecondary} />
                  <Text style={[styles.dateText, TABULAR]}>{formatDateLabel(startParts?.date)}</Text>
                </Pressable>
                <TextInput
                  dense
                  mode="flat"
                  value={startTimeText}
                  keyboardType="numbers-and-punctuation"
                  onChangeText={setStartTimeText}
                  onBlur={() => applyTime("start", startTimeText)}
                  onSubmitEditing={() => applyTime("start", startTimeText)}
                  accessibilityLabel={t("editor.startTime")}
                  style={[styles.timeInput, TABULAR]}
                  underlineColor="transparent"
                />
              </View>
              <View style={[styles.dateField, hasIssue("rangeInvalid") && styles.errorBorder]}>
                <Pressable
                  accessibilityRole="button"
                  accessibilityLabel={t("fields.effectiveEnd")}
                  onPress={() => setDateVisible(true)}
                  style={styles.datePress}
                >
                  <Icon source="calendar-blank-outline" size={18} color={HB_COLORS.textSecondary} />
                  <Text style={[styles.dateText, TABULAR]}>{formatDateLabel(endParts?.date)}</Text>
                </Pressable>
                <TextInput
                  dense
                  mode="flat"
                  value={endTimeText}
                  keyboardType="numbers-and-punctuation"
                  onChangeText={setEndTimeText}
                  onBlur={() => applyTime("end", endTimeText)}
                  onSubmitEditing={() => applyTime("end", endTimeText)}
                  accessibilityLabel={t("editor.endTime")}
                  style={[styles.timeInput, TABULAR]}
                  underlineColor="transparent"
                />
              </View>
            </View>
            <View style={styles.quickRow}>
              {(["sevenDays", "monthEnd", "thirtyDays"] as const).map((key) => (
                <Button key={key} compact mode="outlined" onPress={() => applyQuickRange(key)} style={styles.quickButton}>
                  {t(`quickRange.${key}`)}
                </Button>
              ))}
            </View>
          </SectionCard>

          {/* 参与商品 */}
          <SectionCard
            title={t("editor.productsTitle", { count: draft.products.length })}
            right={
              <View style={styles.productActions}>
                <Button compact icon="barcode-scan" onPress={() => { setScanStatus(""); setScanVisible(true); }}>
                  {t("editor.scanAdd")}
                </Button>
                <Button compact icon="magnify" onPress={() => { setSearchCandidates(null); setSearchVisible(true); }}>
                  {t("editor.searchAdd")}
                </Button>
              </View>
            }
          >
            {draft.products.length === 0 ? (
              <Text style={[styles.hint, hasIssue("productsRequired") && styles.dangerText]}>{t("editor.productsEmpty")}</Text>
            ) : (
              <View>
                {visibleProducts.map(renderProductRow)}
                {draft.products.length > visibleCount ? (
                  <Button onPress={() => setVisibleCount((count) => count + PRODUCT_PAGE)}>
                    {t("editor.showMoreProducts", { count: draft.products.length - visibleCount })}
                  </Button>
                ) : null}
              </View>
            )}
            {draft.products.length > 0 ? <Text style={styles.hint}>{t("editor.weightHint")}</Text> : null}
          </SectionCard>

          {singleNotDiscount && draft.products.length === 1 ? (
            <WarningBox tone="danger">
              {t("editor.singleNotDiscount", {
                name: draft.products[0].productName || draft.products[0].productCode,
                retail: formatMoney(draft.products[0].retailPrice),
                count: draft.applyQuantity,
                original: formatMoney((draft.products[0].retailPrice ?? 0) * draft.applyQuantity),
                price: formatMoney(fixedPrice ?? 0),
              })}
            </WarningBox>
          ) : null}
          {nonDiscounted.length > 0 ? (
            <WarningBox>
              {nonDiscounted.length === 1
                ? t("editor.mixedNotDiscountOne", {
                    name: nonDiscounted[0].productName || nonDiscounted[0].productCode,
                    retail: formatMoney(nonDiscounted[0].retailPrice),
                    count: draft.applyQuantity,
                    original: formatMoney((nonDiscounted[0].retailPrice ?? 0) * draft.applyQuantity),
                    price: formatMoney(fixedPrice ?? 0),
                  })
                : t("editor.mixedNotDiscountMany", {
                    count: nonDiscounted.length,
                    names: nonDiscounted
                      .slice(0, 3)
                      .map((row) => `「${row.productName || row.productCode}」`)
                      .join("、"),
                  })}
            </WarningBox>
          ) : null}

          {/* 适用与高级 */}
          <SectionCard title={t("editor.advancedTitle")}>
            <View style={styles.settingRow}>
              <Text style={styles.settingLabel}>{t("editor.appliesTo")}</Text>
              <Text style={styles.settingValue} numberOfLines={1}>
                {t("editor.storeOnlyValue", { store: storeName })}
              </Text>
            </View>
            <View style={styles.settingRow}>
              <View style={styles.settingTextCol}>
                <Text style={styles.settingLabel}>{t("fields.enabled")}</Text>
              </View>
              <Switch value={draft.isEnabled} onValueChange={(isEnabled) => patchDraft({ isEnabled })} />
            </View>
            <View style={styles.settingRow}>
              <View style={styles.settingTextCol}>
                <Text style={styles.settingLabel}>{t("fields.exclusive")}</Text>
                <Text style={styles.hint}>{t("editor.exclusiveHint")}</Text>
              </View>
              <Switch value={draft.isExclusive} onValueChange={(isExclusive) => patchDraft({ isExclusive })} />
            </View>
            <View style={styles.settingRow}>
              <View style={styles.settingTextCol}>
                <Text style={styles.settingLabel}>{t("fields.priority")}</Text>
                <Text style={styles.hint}>{isEdit ? t("editor.priorityKeepHint") : t("editor.priorityNewHint")}</Text>
              </View>
              <Text style={[styles.settingValue, TABULAR]}>{isEdit ? draft.priority : 0}</Text>
            </View>
            <View style={styles.settingRow}>
              <View style={styles.settingTextCol}>
                <Text style={styles.settingLabel}>{t("fields.maxApplications")}</Text>
                <Text style={styles.hint}>{t("editor.maxApplicationsHint")}</Text>
              </View>
              <TextInput
                dense
                mode="outlined"
                value={draft.maxApplicationsText}
                keyboardType="number-pad"
                placeholder={t("editor.unlimited")}
                onChangeText={(text) => patchDraft({ maxApplicationsText: text.replace(/[^\d]/g, "") })}
                error={hasIssue("maxApplicationsInvalid")}
                style={[styles.maxInput, TABULAR]}
                outlineStyle={styles.outline}
              />
            </View>
          </SectionCard>
        </ScrollView>
      </KeyboardAvoidingView>

      <View style={styles.footer}>
        {canEndNow ? (
          <Button
            mode="outlined"
            icon="stop-circle-outline"
            disabled={saving}
            onPress={handleEndNow}
            style={styles.footerSecondary}
            contentStyle={styles.footerButtonContent}
            textColor={HB_COLORS.textPrimary}
          >
            {t("endNow.button")}
          </Button>
        ) : null}
        <Button
          mode="contained"
          loading={saving}
          disabled={saving}
          onPress={handleSave}
          style={styles.footerPrimary}
          contentStyle={styles.footerButtonContent}
        >
          {t("common:actions.save")}
        </Button>
      </View>

      <DateRangePickerModal
        visible={dateVisible}
        title={t("editor.periodTitle")}
        value={{ from: startParts?.date, to: endParts?.date }}
        onApply={applyDateRange}
        onCancel={() => setDateVisible(false)}
        cancelLabel={t("common:actions.cancel")}
        clearLabel={t("common:actions.clear")}
        applyLabel={t("common:actions.apply")}
        weekdayLabels={weekdayLabels}
        localeTag={localeTag}
      />

      <Portal>
        <Dialog visible={Boolean(weightTarget)} onDismiss={() => setWeightTarget(null)}>
          <Dialog.Title>{t("editor.weightTitle")}</Dialog.Title>
          <Dialog.Content style={styles.weightContent}>
            <Text variant="bodyMedium" numberOfLines={2}>
              {weightTarget?.productName || weightTarget?.productCode}
            </Text>
            <Text style={styles.hint}>{t("editor.weightDescription")}</Text>
            <View style={[styles.stepper, styles.weightStepper]}>
              <IconButton
                icon="minus"
                size={20}
                disabled={(weightTarget?.unitWeight ?? 1) <= 1}
                onPress={() => setWeightTarget((row) => (row ? { ...row, unitWeight: stepUnitWeight(row.unitWeight, -1) } : row))}
                accessibilityLabel={t("editor.decrease")}
              />
              <Text style={[styles.stepperValue, TABULAR]}>{weightTarget?.unitWeight ?? 1}</Text>
              <IconButton
                icon="plus"
                size={20}
                iconColor={HB_COLORS.action}
                onPress={() => setWeightTarget((row) => (row ? { ...row, unitWeight: stepUnitWeight(row.unitWeight, 1) } : row))}
                accessibilityLabel={t("editor.increase")}
              />
            </View>
          </Dialog.Content>
          <Dialog.Actions>
            <Button onPress={() => setWeightTarget(null)}>{t("common:actions.cancel")}</Button>
            <Button
              onPress={() => {
                const target = weightTarget;
                if (target) {
                  setDraft((current) => ({
                    ...current,
                    products: current.products.map((row) =>
                      row.productCode === target.productCode ? { ...row, unitWeight: target.unitWeight } : row,
                    ),
                  }));
                }
                setWeightTarget(null);
              }}
            >
              {t("common:actions.confirm")}
            </Button>
          </Dialog.Actions>
        </Dialog>
      </Portal>

      <PromotionScanSheet
        visible={scanVisible}
        title={t("scan.editorTitle")}
        subtitle={t("scan.editorSubtitle")}
        defaultMode="continuous"
        statusText={scanStatus}
        permissionTitle={t("scan.permissionTitle")}
        permissionDescription={t("scan.permissionDescription")}
        grantLabel={t("scan.grantPermission")}
        onDismiss={() => setScanVisible(false)}
        onBarcode={handleBarcode}
      />

      <ProductSearchSheet
        visible={searchVisible}
        title={searchCandidates ? t("search.candidatesTitle") : t("search.title")}
        placeholder={t("search.placeholder")}
        emptyLabel={t("search.empty")}
        searchingLabel={t("search.searching")}
        closeLabel={t("common:actions.close")}
        addedCodes={addedCodes}
        presetCandidates={searchCandidates}
        onSearch={searchProducts}
        onPick={handlePickSearchResult}
        onDismiss={() => setSearchVisible(false)}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  topBar: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xxs,
    paddingRight: HB_SPACING.md,
    minHeight: 56,
    backgroundColor: HB_COLORS.white,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  topTitle: { flex: 1, fontSize: 20, lineHeight: 28, fontWeight: "700", color: HB_COLORS.textPrimary },
  content: { padding: HB_SPACING.md, gap: HB_SPACING.sm, paddingBottom: HB_SPACING.xl },
  label: { fontSize: 13, fontWeight: "600", color: HB_COLORS.textSecondary },
  input: { backgroundColor: HB_COLORS.white },
  outline: { borderRadius: HB_RADIUS.control },
  ruleRow: { flexDirection: "row", gap: HB_SPACING.sm },
  ruleCol: { flex: 1, gap: 6 },
  ruleColWide: { flex: 1.2, gap: 6 },
  stepper: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    minHeight: 50,
    marginTop: 6,
  },
  stepperValue: { fontSize: 18, fontWeight: "700", color: HB_COLORS.textPrimary, minWidth: 32, textAlign: "center" },
  errorBorder: { borderColor: HB_COLORS.danger },
  hint: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  dangerText: { color: HB_COLORS.danger },
  dateRow: { flexDirection: "row", gap: HB_SPACING.sm },
  dateField: {
    flex: 1,
    flexDirection: "row",
    alignItems: "center",
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    minHeight: 50,
    paddingLeft: HB_SPACING.sm,
    overflow: "hidden",
  },
  datePress: { flexDirection: "row", alignItems: "center", gap: 6, minHeight: 48 },
  dateText: { fontSize: 16, color: HB_COLORS.textPrimary },
  timeInput: { flex: 1, backgroundColor: "transparent", fontSize: 16, height: 48, paddingHorizontal: 0 },
  quickRow: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  quickButton: { borderRadius: HB_RADIUS.control },
  productActions: { flexDirection: "row", alignItems: "center" },
  productRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingVertical: HB_SPACING.xs,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  productIcon: {
    width: 40,
    height: 40,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
    alignItems: "center",
    justifyContent: "center",
  },
  productIconFlagged: { backgroundColor: PROMO_COLORS.redBg },
  productText: { flex: 1, minWidth: 0, gap: 2 },
  productName: { fontSize: 15, lineHeight: 20, fontWeight: "600", color: HB_COLORS.textPrimary },
  productMeta: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  weightBadge: { flexDirection: "row" },
  rowIcon: { margin: 0 },
  settingRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.sm, minHeight: 48 },
  settingTextCol: { flex: 1, minWidth: 0 },
  settingLabel: { fontSize: 14, fontWeight: "600", color: HB_COLORS.textPrimary },
  settingValue: { fontSize: 14, color: HB_COLORS.textSecondary, flexShrink: 1, textAlign: "right" },
  maxInput: { width: 96, backgroundColor: HB_COLORS.white, textAlign: "center" },
  footer: {
    flexDirection: "row",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
    backgroundColor: HB_COLORS.white,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  footerSecondary: { flex: 1, borderRadius: HB_RADIUS.control, borderColor: HB_COLORS.outline },
  footerPrimary: { flex: 1.6, borderRadius: HB_RADIUS.control },
  footerButtonContent: { minHeight: 48 },
  weightContent: { gap: HB_SPACING.xs },
  weightStepper: { alignSelf: "center", width: 180 },
});

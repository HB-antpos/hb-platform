import { useEffect, useMemo, useRef, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { CameraView } from "expo-camera";
import { ActivityIndicator, Button, Icon, Text, TextInput } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useCameraScan } from "@/modules/scanner/use-camera-scan";
import { formatMoney } from "@/modules/price-updates/presentation";
import { quoteName } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { canSearchCandidates, classifyCandidate, type CandidateSituation } from "./logic";
import { ProductThumb, SC_COLORS } from "./shared";
import { useCandidateSearch } from "./use-special-product-categories";
import type { CandidateProduct } from "./types";

interface AddProductsSheetProps {
  visible: boolean;
  storeCode: string;
  storeLabel: string;
  categoryId: string;
  categoryName: string;
  submitting: boolean;
  errorMessage: string;
  /** 提交选中的商品（一次调用：未标记的先标记，别的分类的移过来）。 */
  onSubmit: (productCodes: string[]) => void;
  onDismiss: () => void;
}

const SITUATION_STYLE: Record<Exclude<CandidateSituation, "already">, { bg: string; fg: string }> = {
  mark: { bg: SC_COLORS.successBg, fg: HB_COLORS.success },
  move: { bg: SC_COLORS.warnBg, fg: HB_COLORS.warning },
  fromUncategorized: { bg: SC_COLORS.blueBg, fg: HB_COLORS.action },
};

/**
 * 「添加商品」底部弹层：搜索（或扫码）本店商品，勾选后加入当前分类。三种情况：
 * ① 还不是特殊商品 → 标记并放进本分类；② 已在别的分类 → 移过来；③ 已在本分类 → 置灰不可选。
 * 搜索词与已选商品存在本组件的 state 里；扫码时相机弹层与本弹层互斥显示（不嵌套两个 Modal），
 * 本组件始终挂载，所以扫码返回后已选内容不会丢。
 */
export function AddProductsSheet({
  visible,
  storeCode,
  storeLabel,
  categoryId,
  categoryName,
  submitting,
  errorMessage,
  onSubmit,
  onDismiss,
}: AddProductsSheetProps) {
  const { t } = useAppTranslation("specialProductCategories");
  const [keywordDraft, setKeywordDraft] = useState("");
  const [keyword, setKeyword] = useState("");
  const [selected, setSelected] = useState<Map<string, CandidateProduct>>(new Map());
  const [cameraOpen, setCameraOpen] = useState(false);
  const [cameraGeneration, setCameraGeneration] = useState(0);
  const [cameraMessage, setCameraMessage] = useState("");
  // 扫码得到的条码：搜索结果回来后若恰好 1 条就直接勾上。
  const pendingScanRef = useRef<string | null>(null);

  // 每次重新打开清空搜索与已选。
  useEffect(() => {
    if (visible) return;
    setKeywordDraft("");
    setKeyword("");
    setSelected(new Map());
    setCameraOpen(false);
    setCameraMessage("");
    pendingScanRef.current = null;
  }, [visible]);

  // 搜索词防抖 350ms。
  useEffect(() => {
    const timer = setTimeout(() => setKeyword(keywordDraft), 350);
    return () => clearTimeout(timer);
  }, [keywordDraft]);

  const search = useCandidateSearch(storeCode, keyword, visible);
  const results = useMemo(() => search.data ?? [], [search.data]);
  const searchable = canSearchCandidates(keyword);

  const toggle = (item: CandidateProduct) => {
    if (classifyCandidate(item, categoryId) === "already" || submitting) return;
    setSelected((current) => {
      const next = new Map(current);
      if (next.has(item.productCode)) next.delete(item.productCode);
      else next.set(item.productCode, item);
      return next;
    });
  };

  // 扫码结果：只有一条可加入的商品就自动勾选。
  useEffect(() => {
    const barcode = pendingScanRef.current;
    if (!barcode || !search.isSuccess || keyword.trim() !== barcode) return;
    pendingScanRef.current = null;
    if (results.length === 1 && classifyCandidate(results[0], categoryId) !== "already") {
      const only = results[0];
      setSelected((current) => new Map(current).set(only.productCode, only));
    }
  }, [categoryId, keyword, results, search.isSuccess]);

  const camera = useCameraScan({
    disabled: !cameraOpen,
    resetKey: `${storeCode}:${cameraGeneration}:${cameraOpen}`,
    singleScanUntilReset: true,
    onBarcode: (barcode) => {
      pendingScanRef.current = barcode;
      setCameraOpen(false);
      setKeywordDraft(barcode);
      setKeyword(barcode);
    },
  });

  const openCamera = async () => {
    setCameraMessage("");
    try {
      const permission = camera.permission?.granted ? camera.permission : await camera.requestPermission();
      if (!permission.granted) {
        setCameraMessage(t("add.cameraPermission"));
        return;
      }
      setCameraGeneration((value) => value + 1);
      setCameraOpen(true);
    } catch {
      setCameraMessage(t("add.cameraUnavailable"));
    }
  };

  const count = selected.size;
  const situationLabel = (situation: CandidateSituation, item: CandidateProduct) => {
    if (situation === "mark") return t("add.situationMark");
    if (situation === "already") return t("add.situationAlready");
    if (situation === "fromUncategorized") return t("add.situationFromUncategorized");
    return t("add.situationMove", { category: quoteName(item.categoryName ?? "") });
  };

  return (
    <>
      <BusinessSheet
        visible={visible && !cameraOpen}
        title={t("add.title", { category: quoteName(categoryName) })}
        subtitle={t("add.subtitle", { store: storeLabel })}
        onDismiss={onDismiss}
        dismissable={!submitting}
        footer={
          <View style={styles.footer}>
            {errorMessage ? (
              <Text variant="bodySmall" style={styles.error} accessibilityLiveRegion="polite">
                {errorMessage}
              </Text>
            ) : null}
            <Button
              mode="contained"
              loading={submitting}
              disabled={submitting || count === 0}
              onPress={() => onSubmit([...selected.keys()])}
              contentStyle={styles.buttonContent}
            >
              {t("add.submit", { category: quoteName(categoryName), count })}
            </Button>
          </View>
        }
      >
        <TextInput
          mode="outlined"
          dense
          value={keywordDraft}
          onChangeText={setKeywordDraft}
          placeholder={t("add.searchPlaceholder")}
          accessibilityLabel={t("add.searchPlaceholder")}
          autoCapitalize="none"
          autoCorrect={false}
          returnKeyType="search"
          left={<TextInput.Icon icon="magnify" />}
          right={<TextInput.Icon icon="barcode-scan" accessibilityLabel={t("add.scan")} onPress={() => void openCamera()} />}
        />
        {cameraMessage ? (
          <Text variant="bodySmall" style={styles.error}>
            {cameraMessage}
          </Text>
        ) : null}

        {!searchable ? (
          <Text style={styles.hint}>{t("add.minKeyword")}</Text>
        ) : search.isPending || (search.isFetching && results.length === 0) ? (
          <ActivityIndicator style={styles.loader} color={HB_COLORS.brand} />
        ) : search.isError ? (
          <View style={styles.loader}>
            <Text style={styles.error}>{t("add.searchFailed")}</Text>
            <Button compact mode="text" icon="refresh" onPress={() => void search.refetch()}>
              {t("actions.retry")}
            </Button>
          </View>
        ) : results.length === 0 ? (
          <Text style={styles.hint}>{t("add.noResults")}</Text>
        ) : (
          <View style={styles.list}>
            {results.map((item, index) => {
              const situation = classifyCandidate(item, categoryId);
              const already = situation === "already";
              const checked = selected.has(item.productCode);
              return (
                <Pressable
                  key={item.productCode}
                  accessibilityRole="checkbox"
                  accessibilityState={{ checked, disabled: already }}
                  disabled={already}
                  onPress={() => toggle(item)}
                  style={[styles.row, index < results.length - 1 ? styles.rowDivider : null, checked ? styles.rowChecked : null, already ? styles.rowDisabled : null]}
                >
                  <Icon
                    source={checked ? "check-circle" : "checkbox-blank-circle-outline"}
                    size={24}
                    color={checked ? HB_COLORS.brand : HB_COLORS.outline}
                  />
                  <ProductThumb uri={item.productImage} name={item.displayName} size={40} />
                  <View style={styles.rowText}>
                    <Text style={styles.name} numberOfLines={2}>
                      {item.displayName}
                    </Text>
                    <View style={styles.meta}>
                      <View style={[styles.chip, already ? styles.chipMuted : { backgroundColor: SITUATION_STYLE[situation as keyof typeof SITUATION_STYLE].bg }]}>
                        <Text style={[styles.chipText, { color: already ? HB_COLORS.textSecondary : SITUATION_STYLE[situation as keyof typeof SITUATION_STYLE].fg }]} numberOfLines={1}>
                          {situationLabel(situation, item)}
                        </Text>
                      </View>
                      <Text style={styles.code} numberOfLines={1}>
                        {t("add.itemNumber", { itemNumber: item.itemNumber || "-" })}
                      </Text>
                    </View>
                  </View>
                  <Text style={styles.price}>{formatMoney(item.retailPrice)}</Text>
                </Pressable>
              );
            })}
          </View>
        )}
      </BusinessSheet>

      <BusinessSheet
        visible={visible && cameraOpen}
        title={t("add.scanTitle")}
        onDismiss={() => setCameraOpen(false)}
        footer={<Button onPress={() => setCameraOpen(false)}>{t("actions.close")}</Button>}
      >
        {cameraOpen && camera.permission?.granted ? <CameraView style={styles.camera} {...camera.cameraProps} /> : null}
      </BusinessSheet>
    </>
  );
}

const styles = StyleSheet.create({
  footer: { gap: HB_SPACING.xs },
  buttonContent: { minHeight: 44 },
  error: { color: HB_COLORS.danger },
  hint: { fontSize: 13, color: HB_COLORS.textSecondary, textAlign: "center", paddingVertical: HB_SPACING.md },
  loader: { paddingVertical: HB_SPACING.md, alignItems: "center", gap: HB_SPACING.xs },
  list: { borderWidth: 1, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface, overflow: "hidden" },
  row: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, paddingHorizontal: HB_SPACING.sm, paddingVertical: HB_SPACING.xs, minHeight: 64, backgroundColor: HB_COLORS.white },
  rowDivider: { borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted },
  rowChecked: { backgroundColor: SC_COLORS.selectedBg },
  rowDisabled: { opacity: 0.5 },
  rowText: { flex: 1, minWidth: 0, gap: 4 },
  name: { fontSize: 14, fontWeight: "600", color: HB_COLORS.textPrimary },
  meta: { flexDirection: "row", alignItems: "center", flexWrap: "wrap", gap: HB_SPACING.xs },
  chip: { borderRadius: 4, paddingHorizontal: 6, paddingVertical: 2, maxWidth: "100%" },
  chipMuted: { backgroundColor: HB_COLORS.surfaceMuted },
  chipText: { fontSize: 11, fontWeight: "600" },
  code: { fontSize: 12, color: HB_COLORS.textSecondary, flexShrink: 1 },
  price: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  camera: { height: 300 },
});

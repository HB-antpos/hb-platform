import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { FlatList, Image, RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import * as Clipboard from "expo-clipboard";
import {
  ActivityIndicator,
  Button,
  Card,
  Chip,
  Divider,
  Modal,
  Portal,
  SegmentedButtons,
  Snackbar,
  Switch,
  Text,
  TextInput,
} from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import {
  fetchDomesticProducts,
  fetchDomesticSuppliers,
  updateDomesticProduct,
} from "@/modules/domestic-purchase/api";
import type {
  DomesticProductListItem,
  DomesticSupplierOption,
  UpdateDomesticProductRequest,
} from "@/modules/domestic-purchase/types";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { sortSuppliersByName } from "@/shared/utils/name-sort";

const PAGE_SIZE_OPTIONS = [20, 50, 100] as const;

type PageSizeOption = (typeof PAGE_SIZE_OPTIONS)[number];

interface DomesticProductEditState {
  productName: string;
  englishProductName: string;
  productSpecification: string;
  productType: string;
  domesticPrice: string;
  oemPrice: string;
  importPrice: string;
  packingQuantity: string;
  unitVolume: string;
  middlePackQuantity: string;
  productImage: string;
  isActive: boolean;
}

function formatDisplayValue(value?: string | null) {
  const nextValue = value?.trim();
  return nextValue ? nextValue : "--";
}

function formatNumberInput(value?: number | null) {
  return value == null || !Number.isFinite(value) ? "" : String(value);
}

function formatRmb(value?: number | null) {
  if (value == null || !Number.isFinite(value)) {
    return "--";
  }
  return `¥${value.toFixed(2)}`;
}

function parseNullableNumber(value: string) {
  const trimmed = value.trim();
  if (!trimmed) {
    return null;
  }
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : Number.NaN;
}

function parseNullableInteger(value: string) {
  const trimmed = value.trim();
  if (!trimmed) {
    return null;
  }
  const parsed = Number(trimmed);
  return Number.isInteger(parsed) ? parsed : Number.NaN;
}

function buildEditState(product: DomesticProductListItem): DomesticProductEditState {
  return {
    productName: product.productName ?? "",
    englishProductName: product.englishProductName ?? "",
    productSpecification: product.productSpecification ?? "",
    productType: String(product.productType ?? 0),
    domesticPrice: formatNumberInput(product.domesticPrice),
    oemPrice: formatNumberInput(product.oemPrice),
    importPrice: formatNumberInput(product.importPrice),
    packingQuantity: formatNumberInput(product.packingQuantity),
    unitVolume: formatNumberInput(product.unitVolume),
    middlePackQuantity: formatNumberInput(product.middlePackQuantity),
    productImage: product.productImage ?? "",
    isActive: product.isActive,
  };
}

function buildCopyText(product: DomesticProductListItem, labels: Record<string, string>) {
  return [
    `${labels.supplier}: ${formatDisplayValue([product.supplierCode, product.supplierName].filter(Boolean).join(" - "))}`,
    `${labels.image}: ${formatDisplayValue(product.productImage)}`,
    `${labels.itemNumber}: ${formatDisplayValue(product.hbProductNo)}`,
    `${labels.barcode}: ${formatDisplayValue(product.barcode)}`,
    `${labels.domesticPrice}: ${formatRmb(product.domesticPrice)}`,
    `${labels.oemPrice}: ${formatRmb(product.oemPrice)}`,
    `${labels.productName}: ${formatDisplayValue(product.productName)}`,
    `${labels.productCode}: ${formatDisplayValue(product.productCode)}`,
  ].join("\n");
}

export function DomesticProductList() {
  const { t, language } = useAppTranslation(["domesticPurchase", "common"]);
  const [items, setItems] = useState<DomesticProductListItem[]>([]);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState<PageSizeOption>(20);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [loadErrorMessage, setLoadErrorMessage] = useState("");
  const [suppliers, setSuppliers] = useState<DomesticSupplierOption[]>([]);
  const [supplierModalVisible, setSupplierModalVisible] = useState(false);
  const [selectedSupplier, setSelectedSupplier] = useState<DomesticSupplierOption | null>(null);
  const [productNoKeyword, setProductNoKeyword] = useState("");
  const [appliedProductNo, setAppliedProductNo] = useState("");
  const [snackbar, setSnackbar] = useState("");
  const getErrorMessage = useCallback((error: unknown, fallbackKey: string) => (
    resolveLocalizedErrorMessage(error, {
      language,
      t,
      fallbackKey,
    })
  ), [language, t]);
  const [copyingProductCode, setCopyingProductCode] = useState("");
  const [editingProduct, setEditingProduct] = useState<DomesticProductListItem | null>(null);
  const [editState, setEditState] = useState<DomesticProductEditState | null>(null);
  const [saving, setSaving] = useState(false);
  const [imageLoadFailures, setImageLoadFailures] = useState<Record<string, boolean>>({});
  const didLoadInitialData = useRef(false);

  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const canGoPrevious = page > 1;
  const canGoNext = page * pageSize < total;

  const loadProducts = useCallback(
    async (
      nextPage = 1,
      options?: {
        supplier?: DomesticSupplierOption | null;
        productNo?: string;
        nextPageSize?: PageSizeOption;
      }
    ) => {
      const supplier = options && "supplier" in options ? options.supplier : selectedSupplier;
      const productNo = options && "productNo" in options ? options.productNo ?? "" : appliedProductNo;
      const nextPageSize = options?.nextPageSize ?? pageSize;

      setLoading(true);
      try {
        const result = await fetchDomesticProducts({
          page: nextPage,
          pageSize: nextPageSize,
          supplierCode: supplier?.supplierCode,
          productNo: productNo.trim(),
        });
        setItems(result.items);
        setTotal(result.total);
        setPage(result.page);
        setLoadErrorMessage("");
      } catch (error) {
        const message = getErrorMessage(error, "productList.messages.loadFailed");
        setLoadErrorMessage(message);
        setSnackbar(message);
      } finally {
        setLoading(false);
        setRefreshing(false);
      }
    },
    [appliedProductNo, getErrorMessage, pageSize, selectedSupplier]
  );

  useEffect(() => {
    if (didLoadInitialData.current) {
      return;
    }
    didLoadInitialData.current = true;

    let cancelled = false;
    async function loadInitialData() {
      try {
        const nextSuppliers = await fetchDomesticSuppliers();
        if (!cancelled) {
          setSuppliers(nextSuppliers);
        }
      } catch (error) {
        if (!cancelled) {
          setSnackbar(getErrorMessage(error, "messages.loadSuppliersFailed"));
        }
      }
    }

    void loadInitialData();
    void loadProducts(1);

    return () => {
      cancelled = true;
    };
  }, [getErrorMessage, loadProducts]);

  // 供应商筛选弹窗按名字排序（接口原序不保证顺序）。
  const sortedSuppliers = useMemo(() => sortSuppliersByName(suppliers, language), [suppliers, language]);

  const activeFilterCount = useMemo(
    () => [selectedSupplier, appliedProductNo.trim()].filter(Boolean).length,
    [appliedProductNo, selectedSupplier]
  );

  const handleSupplierSelect = useCallback(
    (supplier: DomesticSupplierOption | null) => {
      setSelectedSupplier(supplier);
      setSupplierModalVisible(false);
      void loadProducts(1, { supplier });
    },
    [loadProducts]
  );

  const handleSearch = useCallback(() => {
    const nextKeyword = productNoKeyword.trim();
    setAppliedProductNo(nextKeyword);
    void loadProducts(1, { productNo: nextKeyword });
  }, [loadProducts, productNoKeyword]);

  const handleClearFilters = useCallback(() => {
    setSelectedSupplier(null);
    setProductNoKeyword("");
    setAppliedProductNo("");
    void loadProducts(1, { productNo: "", supplier: null });
  }, [loadProducts]);

  const handlePageSizeChange = useCallback(
    (nextPageSize: PageSizeOption) => {
      setPageSize(nextPageSize);
      void loadProducts(1, { nextPageSize });
    },
    [loadProducts]
  );

  const handleCopyProduct = useCallback(
    async (product: DomesticProductListItem) => {
      setCopyingProductCode(product.productCode);
      try {
        await Clipboard.setStringAsync(
          buildCopyText(product, {
            supplier: t("productList.fields.supplier"),
            image: t("productList.fields.image"),
            itemNumber: t("productList.fields.itemNumber"),
            barcode: t("productList.fields.barcode"),
            domesticPrice: t("productList.fields.domesticPrice"),
            oemPrice: t("productList.fields.oemPrice"),
            productName: t("productList.fields.productName"),
            productCode: t("productList.fields.productCode"),
          })
        );
        setSnackbar(t("productList.messages.copySuccess"));
      } catch (error) {
        setSnackbar(getErrorMessage(error, "productList.messages.copyFailed"));
      } finally {
        setCopyingProductCode("");
      }
    },
    [getErrorMessage, t]
  );

  const openEdit = useCallback((product: DomesticProductListItem) => {
    setEditingProduct(product);
    setEditState(buildEditState(product));
  }, []);

  const updateEditState = useCallback((patch: Partial<DomesticProductEditState>) => {
    setEditState((current) => (current ? { ...current, ...patch } : current));
  }, []);

  const handleSaveEdit = useCallback(async () => {
    if (!editingProduct || !editState) {
      return;
    }

    const domesticPrice = parseNullableNumber(editState.domesticPrice);
    const oemPrice = parseNullableNumber(editState.oemPrice);
    const importPrice = parseNullableNumber(editState.importPrice);
    const unitVolume = parseNullableNumber(editState.unitVolume);
    const packingQuantity = parseNullableInteger(editState.packingQuantity);
    const middlePackQuantity = parseNullableInteger(editState.middlePackQuantity);
    const productType = Number(editState.productType);

    const invalidDecimal = [domesticPrice, oemPrice, importPrice, unitVolume].some(
      (value) => Number.isNaN(value) || (value != null && value < 0)
    );
    const invalidInteger = [packingQuantity, middlePackQuantity].some(
      (value) => Number.isNaN(value) || (value != null && value <= 0)
    );

    if (!Number.isInteger(productType) || productType < 0 || productType > 2 || invalidDecimal || invalidInteger) {
      setSnackbar(t("productList.messages.invalidEdit"));
      return;
    }

    const payload: UpdateDomesticProductRequest = {
      productName: editState.productName.trim() || null,
      englishProductName: editState.englishProductName.trim() || null,
      productSpecification: editState.productSpecification.trim() || null,
      productType,
      domesticPrice,
      oemPrice,
      importPrice,
      packingQuantity,
      unitVolume,
      middlePackQuantity,
      productImage: editState.productImage.trim() || null,
      isActive: editState.isActive,
    };

    setSaving(true);
    try {
      const updated = await updateDomesticProduct(editingProduct.productCode, payload);
      setItems((current) =>
        current.map((item) => (item.productCode === updated.productCode ? { ...item, ...updated } : item))
      );
      setEditingProduct(null);
      setEditState(null);
      setSnackbar(t("productList.messages.saveSuccess"));
    } catch (error) {
      setSnackbar(getErrorMessage(error, "productList.messages.saveFailed"));
    } finally {
      setSaving(false);
    }
  }, [editState, editingProduct, getErrorMessage, t]);

  const renderProduct = useCallback(
    ({ item }: { item: DomesticProductListItem }) => {
      const imageKey = item.productCode || item.hbProductNo || item.barcode || "";
      const showImage = Boolean(item.productImage && !imageLoadFailures[imageKey]);

      return (
        <Card mode="outlined" style={styles.productCard}>
          <Card.Content style={styles.productCardContent}>
            <View style={styles.productTopRow}>
              <View style={styles.imageFrame}>
                {showImage ? (
                  <Image
                    source={{ uri: item.productImage! }}
                    resizeMode="cover"
                    style={styles.productImage}
                    onError={() => setImageLoadFailures((current) => ({ ...current, [imageKey]: true }))}
                  />
                ) : (
                  <Text variant="labelSmall" style={styles.imagePlaceholder}>
                    {t("productList.fields.image")}
                  </Text>
                )}
              </View>
              <View style={styles.productMain}>
                <View style={styles.productTitleRow}>
                  <Text variant="titleSmall" style={styles.productTitle}>
                    {formatDisplayValue(item.hbProductNo)}
                  </Text>
                  <Chip compact selected={item.isActive} style={styles.statusChip}>
                    {item.isActive ? t("productList.status.active") : t("productList.status.inactive")}
                  </Chip>
                </View>
                <Text variant="bodySmall" style={styles.mutedText} numberOfLines={2}>
                  {formatDisplayValue(item.productName)}
                </Text>
                <Text variant="bodySmall" style={styles.mutedText}>
                  {formatDisplayValue([item.supplierCode, item.supplierName].filter(Boolean).join(" - "))}
                </Text>
              </View>
            </View>

            <View style={styles.productInfoGrid}>
              <InfoTile label={t("productList.fields.barcode")} value={formatDisplayValue(item.barcode)} />
              <InfoTile label={t("productList.fields.domesticPrice")} value={formatRmb(item.domesticPrice)} />
              <InfoTile label={t("productList.fields.oemPrice")} value={formatRmb(item.oemPrice)} />
            </View>

            <View style={styles.cardActions}>
              <Button compact mode="outlined" icon="pencil-outline" onPress={() => openEdit(item)}>
                {t("productList.actions.edit")}
              </Button>
              <Button
                compact
                icon="content-copy"
                loading={copyingProductCode === item.productCode}
                disabled={copyingProductCode === item.productCode}
                onPress={() => handleCopyProduct(item)}
              >
                {t("productList.actions.copy")}
              </Button>
            </View>
          </Card.Content>
        </Card>
      );
    },
    [copyingProductCode, handleCopyProduct, imageLoadFailures, openEdit, t]
  );

  return (
    <View style={styles.container}>
      <View style={styles.filterPanel}>
        <View style={styles.filterHeader}>
          <View>
            <Text variant="titleMedium" style={styles.sectionTitle}>
              {t("productList.title")}
            </Text>
            <Text variant="bodySmall" style={styles.mutedText}>
              {t("productList.summary", { total, page, totalPages })}
            </Text>
          </View>
          <Button compact icon="refresh" onPress={() => loadProducts(page)} disabled={loading}>
            {t("common:actions.refresh")}
          </Button>
        </View>

        <Button mode="outlined" onPress={() => setSupplierModalVisible(true)} style={styles.fullButton}>
          {selectedSupplier
            ? `${selectedSupplier.supplierCode} - ${selectedSupplier.supplierName}`
            : t("productList.filters.allSuppliers")}
        </Button>

        <View style={styles.searchRow}>
          <TextInput
            mode="outlined"
            dense
            label={t("productList.filters.productNo")}
            value={productNoKeyword}
            onChangeText={setProductNoKeyword}
            style={styles.searchInput}
            returnKeyType="search"
            onSubmitEditing={handleSearch}
          />
          <Button mode="contained" compact onPress={handleSearch}>
            {t("common:actions.search")}
          </Button>
        </View>

        <View style={styles.filterActions}>
          <Button compact icon="filter-remove-outline" disabled={!activeFilterCount} onPress={handleClearFilters}>
            {t("productList.filters.clear")}
          </Button>
          <View style={styles.pageSizeRow}>
            {PAGE_SIZE_OPTIONS.map((option) => (
              <Chip
                compact
                key={option}
                selected={pageSize === option}
                onPress={() => handlePageSizeChange(option)}
              >
                {option}
              </Chip>
            ))}
          </View>
        </View>
      </View>

      <FlatList
        data={items}
        keyExtractor={(item) => item.productCode}
        contentContainerStyle={items.length ? styles.listContent : styles.emptyContent}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => {
              setRefreshing(true);
              void loadProducts(page);
            }}
          />
        }
        renderItem={renderProduct}
        ListEmptyComponent={
          loading ? (
            <ActivityIndicator style={styles.emptyLoader} />
          ) : loadErrorMessage ? (
            <EmptyState
              title={t("productList.messages.loadFailed")}
              description={loadErrorMessage}
              primaryAction={{
                label: t("common:actions.retry"),
                icon: "refresh",
                onPress: () => void loadProducts(page),
              }}
            />
          ) : (
            <EmptyState
              title={t("productList.empty.title")}
              description={t("productList.empty.description")}
            />
          )
        }
        ListFooterComponent={
          items.length ? (
            <View style={styles.paginationRow}>
              <Button mode="outlined" disabled={!canGoPrevious || loading} onPress={() => loadProducts(page - 1)}>
                {t("productList.pagination.previous")}
              </Button>
              <Text variant="bodyMedium" style={styles.paginationText}>
                {t("productList.pagination.page", { page, totalPages })}
              </Text>
              <Button mode="outlined" disabled={!canGoNext || loading} onPress={() => loadProducts(page + 1)}>
                {t("productList.pagination.next")}
              </Button>
            </View>
          ) : null
        }
      />

      <Portal>
        <Modal
          visible={supplierModalVisible}
          onDismiss={() => setSupplierModalVisible(false)}
          contentContainerStyle={styles.supplierModal}
        >
          <View style={styles.supplierModalHeader}>
            <View style={styles.supplierModalTitleWrap}>
              <Text variant="titleMedium" style={styles.modalTitle}>
                {t("productList.filters.supplierTitle")}
              </Text>
              <Text variant="bodySmall" style={styles.mutedText}>
                {selectedSupplier
                  ? `${selectedSupplier.supplierCode} - ${selectedSupplier.supplierName}`
                  : t("productList.filters.allSuppliers")}
              </Text>
            </View>
            <Button compact onPress={() => setSupplierModalVisible(false)}>
              {t("common:actions.close")}
            </Button>
          </View>
          <Divider style={styles.divider} />
          <ScrollView style={styles.supplierList}>
            <Button
              mode={selectedSupplier ? "text" : "contained-tonal"}
              icon={selectedSupplier ? undefined : "check"}
              onPress={() => handleSupplierSelect(null)}
              style={styles.supplierOption}
              contentStyle={styles.supplierOptionContent}
            >
              {t("productList.filters.allSuppliers")}
            </Button>
            {sortedSuppliers.map((supplier) => {
              const selected = selectedSupplier?.supplierCode === supplier.supplierCode;
              return (
                <Button
                  key={supplier.supplierCode}
                  mode={selected ? "contained-tonal" : "text"}
                  icon={selected ? "check" : undefined}
                  onPress={() => handleSupplierSelect(supplier)}
                  style={styles.supplierOption}
                  contentStyle={styles.supplierOptionContent}
                >
                  {supplier.supplierCode} - {supplier.supplierName}
                </Button>
              );
            })}
          </ScrollView>
        </Modal>

        <Modal
          visible={Boolean(editingProduct && editState)}
          onDismiss={() => {
            if (!saving) {
              setEditingProduct(null);
              setEditState(null);
            }
          }}
          contentContainerStyle={styles.editModal}
        >
          <ScrollView contentContainerStyle={styles.editModalContent}>
            <Text variant="titleMedium" style={styles.modalTitle}>
              {t("productList.edit.title")}
            </Text>
            <Text variant="bodySmall" style={styles.mutedText}>
              {formatDisplayValue(editingProduct?.hbProductNo)} · {formatDisplayValue(editingProduct?.barcode)}
            </Text>
            <Divider style={styles.divider} />

            {editState ? (
              <>
                <TextInput
                  mode="outlined"
                  label={t("productList.fields.productName")}
                  value={editState.productName}
                  onChangeText={(value) => updateEditState({ productName: value })}
                  style={styles.input}
                />
                <TextInput
                  mode="outlined"
                  label={t("productList.fields.englishProductName")}
                  value={editState.englishProductName}
                  onChangeText={(value) => updateEditState({ englishProductName: value })}
                  style={styles.input}
                />
                <TextInput
                  mode="outlined"
                  label={t("productList.fields.productSpecification")}
                  value={editState.productSpecification}
                  onChangeText={(value) => updateEditState({ productSpecification: value })}
                  style={styles.input}
                />
                <Text variant="labelLarge" style={styles.inputLabel}>
                  {t("productList.fields.productType")}
                </Text>
                <SegmentedButtons
                  value={editState.productType}
                  onValueChange={(value) => updateEditState({ productType: value })}
                  buttons={[
                    { value: "0", label: t("productList.productTypes.normal") },
                    { value: "1", label: t("productList.productTypes.set") },
                    { value: "2", label: t("productList.productTypes.multi") },
                  ]}
                  style={styles.segmentedControl}
                />
                <View style={styles.editGrid}>
                  <TextInput
                    mode="outlined"
                    label={t("productList.fields.domesticPrice")}
                    value={editState.domesticPrice}
                    keyboardType="decimal-pad"
                    onChangeText={(value) => updateEditState({ domesticPrice: value })}
                    style={styles.editGridInput}
                  />
                  <TextInput
                    mode="outlined"
                    label={t("productList.fields.oemPrice")}
                    value={editState.oemPrice}
                    keyboardType="decimal-pad"
                    onChangeText={(value) => updateEditState({ oemPrice: value })}
                    style={styles.editGridInput}
                  />
                  <TextInput
                    mode="outlined"
                    label={t("productList.fields.importPrice")}
                    value={editState.importPrice}
                    keyboardType="decimal-pad"
                    onChangeText={(value) => updateEditState({ importPrice: value })}
                    style={styles.editGridInput}
                  />
                  <TextInput
                    mode="outlined"
                    label={t("productList.fields.packingQuantity")}
                    value={editState.packingQuantity}
                    keyboardType="number-pad"
                    onChangeText={(value) => updateEditState({ packingQuantity: value })}
                    style={styles.editGridInput}
                  />
                  <TextInput
                    mode="outlined"
                    label={t("productList.fields.unitVolume")}
                    value={editState.unitVolume}
                    keyboardType="decimal-pad"
                    onChangeText={(value) => updateEditState({ unitVolume: value })}
                    style={styles.editGridInput}
                  />
                  <TextInput
                    mode="outlined"
                    label={t("productList.fields.middlePackQuantity")}
                    value={editState.middlePackQuantity}
                    keyboardType="number-pad"
                    onChangeText={(value) => updateEditState({ middlePackQuantity: value })}
                    style={styles.editGridInput}
                  />
                </View>
                <TextInput
                  mode="outlined"
                  label={t("productList.fields.image")}
                  value={editState.productImage}
                  onChangeText={(value) => updateEditState({ productImage: value })}
                  style={styles.input}
                />
                <View style={styles.switchRow}>
                  <Text variant="bodyMedium">{t("productList.fields.isActive")}</Text>
                  <Switch value={editState.isActive} onValueChange={(value) => updateEditState({ isActive: value })} />
                </View>
                <View style={styles.readonlyPanel}>
                  <Text variant="bodySmall" style={styles.mutedText}>
                    {t("productList.edit.readonlyHint")}
                  </Text>
                  <Text variant="bodySmall" style={styles.mutedText}>
                    {t("productList.fields.supplier")}:{" "}
                    {formatDisplayValue([editingProduct?.supplierCode, editingProduct?.supplierName].filter(Boolean).join(" - "))}
                  </Text>
                </View>
                <View style={styles.modalActions}>
                  <Button
                    disabled={saving}
                    onPress={() => {
                      setEditingProduct(null);
                      setEditState(null);
                    }}
                  >
                    {t("common:actions.cancel")}
                  </Button>
                  <Button mode="contained" icon="content-save" loading={saving} disabled={saving} onPress={handleSaveEdit}>
                    {t("common:actions.save")}
                  </Button>
                </View>
              </>
            ) : null}
          </ScrollView>
        </Modal>
      </Portal>

      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={3000}>
        {snackbar}
      </Snackbar>
    </View>
  );
}

function InfoTile({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.infoTile}>
      <Text variant="labelSmall" style={styles.infoLabel}>
        {label}
      </Text>
      <Text variant="bodyMedium" style={styles.infoValue} numberOfLines={2}>
        {value}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
  },
  filterPanel: {
    paddingHorizontal: 12,
    paddingTop: 8,
    paddingBottom: 10,
    backgroundColor: "#FFFFFF",
    borderColor: "#E4E7EC",
    borderWidth: 1,
    borderRadius: 12,
    gap: 8,
  },
  filterHeader: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "flex-start",
    gap: 10,
  },
  sectionTitle: {
    fontWeight: "700",
  },
  mutedText: {
    color: "#667085",
  },
  fullButton: {
    alignItems: "stretch",
  },
  searchRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  searchInput: {
    flex: 1,
    backgroundColor: "#FFFFFF",
  },
  filterActions: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    flexWrap: "wrap",
    gap: 8,
  },
  pageSizeRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 6,
  },
  listContent: {
    padding: 16,
    paddingTop: 12,
    paddingBottom: 24,
  },
  emptyContent: {
    flexGrow: 1,
    justifyContent: "center",
    padding: 24,
  },
  emptyLoader: {
    paddingVertical: 32,
  },
  productCard: {
    marginBottom: 10,
    borderRadius: 12,
    borderColor: "#E4E7EC",
    backgroundColor: "#FFFFFF",
  },
  productCardContent: {
    gap: 10,
  },
  productTopRow: {
    flexDirection: "row",
    gap: 10,
  },
  imageFrame: {
    width: 72,
    height: 72,
    borderRadius: 8,
    overflow: "hidden",
    backgroundColor: "#F2F4F7",
    alignItems: "center",
    justifyContent: "center",
  },
  productImage: {
    width: "100%",
    height: "100%",
  },
  imagePlaceholder: {
    color: "#667085",
    textAlign: "center",
  },
  productMain: {
    flex: 1,
    gap: 3,
  },
  productTitleRow: {
    flexDirection: "row",
    alignItems: "flex-start",
    justifyContent: "space-between",
    gap: 8,
  },
  productTitle: {
    flex: 1,
    fontWeight: "700",
  },
  statusChip: {
    flexShrink: 0,
  },
  productInfoGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  infoTile: {
    minWidth: 104,
    flex: 1,
    paddingHorizontal: 10,
    paddingVertical: 8,
    borderRadius: 8,
    backgroundColor: "#F8FAFC",
  },
  infoLabel: {
    color: "#667085",
    marginBottom: 2,
  },
  infoValue: {
    fontWeight: "600",
  },
  cardActions: {
    flexDirection: "row",
    justifyContent: "flex-end",
    gap: 8,
  },
  paginationRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    paddingTop: 8,
    gap: 8,
  },
  paginationText: {
    color: "#344054",
  },
  supplierModal: {
    margin: 14,
    padding: 14,
    borderRadius: 18,
    backgroundColor: "#FFFFFF",
    maxHeight: "82%",
  },
  supplierModalHeader: {
    flexDirection: "row",
    alignItems: "flex-start",
    justifyContent: "space-between",
    gap: 10,
  },
  supplierModalTitleWrap: {
    flex: 1,
  },
  supplierList: {
    maxHeight: 520,
  },
  supplierOption: {
    marginVertical: 2,
    alignItems: "stretch",
  },
  supplierOptionContent: {
    justifyContent: "flex-start",
  },
  editModal: {
    margin: 14,
    borderRadius: 18,
    backgroundColor: "#FFFFFF",
    maxHeight: "90%",
  },
  editModalContent: {
    padding: 14,
  },
  modalTitle: {
    fontWeight: "700",
  },
  divider: {
    marginVertical: 10,
  },
  input: {
    marginTop: 10,
    backgroundColor: "#FFFFFF",
  },
  inputLabel: {
    marginTop: 12,
    marginBottom: 6,
  },
  segmentedControl: {
    marginBottom: 2,
  },
  editGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
    marginTop: 2,
  },
  editGridInput: {
    minWidth: 140,
    flex: 1,
    backgroundColor: "#FFFFFF",
  },
  switchRow: {
    marginTop: 12,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
  },
  readonlyPanel: {
    marginTop: 12,
    padding: 10,
    borderRadius: 8,
    backgroundColor: "#F8FAFC",
    gap: 4,
  },
  modalActions: {
    marginTop: 14,
    flexDirection: "row",
    justifyContent: "flex-end",
    gap: 8,
  },
});

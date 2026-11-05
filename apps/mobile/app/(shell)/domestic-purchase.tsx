import { useCallback, useMemo, useState } from "react";
import {
  FlatList,
  RefreshControl,
  ScrollView,
  StyleSheet,
  View,
} from "react-native";
import { useFocusEffect } from "@react-navigation/native";
import * as Clipboard from "expo-clipboard";
import { useRouter } from "expo-router";
import {
  ActivityIndicator,
  Button,
  Card,
  Divider,
  Modal,
  Portal,
  SegmentedButtons,
  Snackbar,
  Text,
  TextInput,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { EmptyState } from "@/components/ui/EmptyState";
import { hasVisibleTabRoute } from "@/modules/navigation/default-route";
import { useAppNavigationStore } from "@/modules/navigation/store";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { resolveLocaleTag } from "@/shared/i18n/types";
import { useAuthStore } from "@/store/auth-store";
import {
  exportDomesticProductBatch,
  fetchDomesticProductBatchDetail,
  fetchDomesticProductBatches,
  updateDomesticProductBatchItems,
} from "@/modules/domestic-purchase/api";
import { CreateBatchModal } from "@/modules/domestic-purchase/CreateBatchModal";
import { DomesticProductList } from "@/modules/domestic-purchase/DomesticProductList";
import { formatDomesticSupplierLabel } from "@/modules/domestic-purchase/supplier-label";
import type {
  DomesticProductBatch,
  DomesticProductBatchDetail,
  DomesticProductBatchItem,
} from "@/modules/domestic-purchase/types";
import { ProductCreationType } from "@/modules/domestic-purchase/types";

const PAGE_SIZE = 20;

type DomesticPurchaseTab = "creation" | "products";

interface DetailEditState {
  productName: string;
  privateLabelPrice: string;
}

function formatDateTime(value: string | undefined, localeTag: string) {
  if (!value) {
    return "--";
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  return date.toLocaleString(localeTag, { hour12: false });
}

function formatPriceInput(value?: number | null) {
  if (value == null || !Number.isFinite(value)) {
    return "";
  }
  return String(value);
}

function buildDetailEdits(items: DomesticProductBatchItem[]) {
  return items.reduce<Record<string, DetailEditState>>((current, item) => {
    const key = item.productCode || item.itemNumber;
    current[key] = {
      productName: item.productName || "",
      privateLabelPrice: formatPriceInput(item.privateLabelPrice),
    };
    return current;
  }, {});
}

function formatCopyValue(value?: string | null) {
  const nextValue = value?.trim();
  return nextValue ? nextValue : "--";
}

function buildBatchDetailCopyText(
  items: DomesticProductBatchItem[],
  header: string
) {
  if (items.length === 0) {
    return "";
  }

  const rows = items.map((item) => {
    const productNo = formatCopyValue(item.hbProductNo || item.itemNumber || item.productCode);
    const barcode = formatCopyValue(item.barcode);
    const privateLabelPrice =
      item.privateLabelPrice == null || !Number.isFinite(item.privateLabelPrice) ? "--" : String(item.privateLabelPrice);

    return `${productNo} ${barcode} ${privateLabelPrice}`;
  });

  return [header, ...rows].join("\n");
}

function typeLabel(type: ProductCreationType, t: (key: string) => string) {
  if (type === ProductCreationType.Set) {
    return t("types.set");
  }
  if (type === ProductCreationType.SetSubItem) {
    return t("types.setSubItem");
  }
  return t("types.normal");
}

export default function DomesticPurchaseScreen() {
  const router = useRouter();
  const { t, language } = useAppTranslation(["domesticPurchase", "common"]);
  const localeTag = resolveLocaleTag(language);
  const access = useAuthStore((state) => state.access);
  const navigationItems = useAppNavigationStore((state) => state.items);
  const hasAccess =
    hasVisibleTabRoute(
      navigationItems.map((item) => item.routeName),
      "domestic-purchase"
    ) || access.hasPermission("DomesticPurchase.ManageProducts");

  const [activeTab, setActiveTab] = useState<DomesticPurchaseTab>("creation");
  const [batches, setBatches] = useState<DomesticProductBatch[]>([]);
  const [page, setPage] = useState(1);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [copyingBatchNumber, setCopyingBatchNumber] = useState("");
  const [loadErrorMessage, setLoadErrorMessage] = useState("");
  const [snackbar, setSnackbar] = useState("");

  const [detailVisible, setDetailVisible] = useState(false);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailErrorMessage, setDetailErrorMessage] = useState("");
  const [selectedBatch, setSelectedBatch] = useState<DomesticProductBatch | null>(null);
  const [detail, setDetail] = useState<DomesticProductBatchDetail | null>(null);
  const [detailEdits, setDetailEdits] = useState<Record<string, DetailEditState>>({});
  const [detailSaving, setDetailSaving] = useState(false);

  const [createVisible, setCreateVisible] = useState(false);
  const getErrorMessage = useCallback((error: unknown, fallbackKey: string) => (
    resolveLocalizedErrorMessage(error, {
      language,
      t,
      fallbackKey,
    })
  ), [language, t]);

  const hasMore = batches.length < total;

  const loadBatches = useCallback(
    async (nextPage = 1, mode: "replace" | "append" = "replace") => {
      if (!hasAccess) {
        return;
      }

      setLoading(true);
      try {
        const result = await fetchDomesticProductBatches(nextPage, PAGE_SIZE);
        setBatches((current) => (mode === "append" ? [...current, ...result.items] : result.items));
        setTotal(result.total);
        setPage(result.page);
        setLoadErrorMessage("");
      } catch (error) {
        const message = getErrorMessage(error, "messages.loadFailed");
        setLoadErrorMessage(message);
        setSnackbar(message);
      } finally {
        setLoading(false);
        setRefreshing(false);
      }
    },
    [getErrorMessage, hasAccess]
  );

  useFocusEffect(
    useCallback(() => {
      if (activeTab === "creation") {
        void loadBatches(1, "replace");
      }
    }, [activeTab, loadBatches])
  );

  const openDetail = useCallback(
    async (batch: DomesticProductBatch) => {
      setSelectedBatch(batch);
      setDetailVisible(true);
      setDetailLoading(true);
      setDetail(null);
      setDetailEdits({});
      setDetailErrorMessage("");
      try {
        const nextDetail = await fetchDomesticProductBatchDetail(batch.batchNumber);
        setDetail(nextDetail);
        setDetailEdits(buildDetailEdits(nextDetail.items));
      } catch (error) {
        const message = getErrorMessage(error, "messages.loadDetailFailed");
        setDetailErrorMessage(message);
        setSnackbar(message);
      } finally {
        setDetailLoading(false);
      }
    },
    [getErrorMessage]
  );

  const handleExport = useCallback(
    async (batchNumber: string) => {
      setBusy(true);
      try {
        await exportDomesticProductBatch(batchNumber);
        setSnackbar(t("messages.exportSuccess"));
      } catch (error) {
        setSnackbar(getErrorMessage(error, "messages.exportFailed"));
      } finally {
        setBusy(false);
      }
    },
    [getErrorMessage, t]
  );

  const handleCopyBatch = useCallback(
    async (batchNumber: string) => {
      setCopyingBatchNumber(batchNumber);
      try {
        const nextDetail = await fetchDomesticProductBatchDetail(batchNumber);
        const copyText = buildBatchDetailCopyText(
          nextDetail.items,
          t("copy.batchHeader")
        );

        if (!copyText) {
          setSnackbar(t("messages.copyEmpty"));
          return;
        }

        await Clipboard.setStringAsync(copyText);
        setSnackbar(t("messages.copySuccess"));
      } catch (error) {
        setSnackbar(getErrorMessage(error, "messages.copyFailed"));
      } finally {
        setCopyingBatchNumber("");
      }
    },
    [getErrorMessage, t]
  );

  const updateDetailEdit = useCallback((key: string, patch: Partial<DetailEditState>) => {
    setDetailEdits((current) => ({
      ...current,
      [key]: {
        productName: current[key]?.productName ?? "",
        privateLabelPrice: current[key]?.privateLabelPrice ?? "",
        ...patch,
      },
    }));
  }, []);

  const handleSaveDetailChanges = useCallback(async () => {
    if (!selectedBatch || !detail) {
      return;
    }

    const items = detail.items.map((item) => {
      const key = item.productCode || item.itemNumber;
      const edit = detailEdits[key] ?? {
        productName: item.productName || "",
        privateLabelPrice: formatPriceInput(item.privateLabelPrice),
      };
      const rawPrice = edit.privateLabelPrice.trim();
      const privateLabelPrice = rawPrice ? Number(rawPrice) : null;

      return {
        productCode: key,
        productName: edit.productName,
        privateLabelPrice,
      };
    });

    if (items.some((item) => item.privateLabelPrice != null && (!Number.isFinite(item.privateLabelPrice) || item.privateLabelPrice < 0))) {
      setSnackbar(t("messages.invalidPrice"));
      return;
    }

    setDetailSaving(true);
    try {
      await updateDomesticProductBatchItems(selectedBatch.batchNumber, { items });
      const nextDetail = await fetchDomesticProductBatchDetail(selectedBatch.batchNumber);
      setDetail(nextDetail);
      setDetailEdits(buildDetailEdits(nextDetail.items));
      setSnackbar(t("messages.saveSuccess"));
    } catch (error) {
      setSnackbar(getErrorMessage(error, "messages.saveFailed"));
    } finally {
      setDetailSaving(false);
    }
  }, [detail, detailEdits, getErrorMessage, selectedBatch, t]);

  const summaryText = useMemo(
    () =>
      activeTab === "products"
        ? t("productList.headerSubtitle")
        : t("summary", { total, shown: batches.length }),
    [activeTab, batches.length, t, total]
  );

  if (!hasAccess) {
    return (
      <SafeAreaView style={styles.safeArea}>
        <EmptyState
          title={t("empty.noAccessTitle")}
          description={t("empty.noAccessDescription")}
          primaryAction={{
            label: t("common:actions.goToSettings"),
            icon: "cog-outline",
            onPress: () => router.navigate("/(shell)/settings"),
          }}
        />
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView style={styles.safeArea}>
      <View style={styles.header}>
        <View>
          <Text variant="titleLarge" style={styles.title}>
            {t("title")}
          </Text>
          <Text variant="bodySmall" style={styles.subtitle}>
            {summaryText}
          </Text>
        </View>
        {activeTab === "creation" ? (
          <Button mode="contained" icon="plus" compact onPress={() => setCreateVisible(true)}>
            {t("actions.create")}
          </Button>
        ) : null}
      </View>

      <SegmentedButtons
        value={activeTab}
        onValueChange={(value) => setActiveTab(value as DomesticPurchaseTab)}
        buttons={[
          { value: "creation", label: t("subTabs.creation") },
          { value: "products", label: t("subTabs.products") },
        ]}
        style={styles.tabSwitcher}
      />

      {activeTab === "creation" ? (
        <FlatList
          data={batches}
          keyExtractor={(item) => item.batchNumber}
          contentContainerStyle={batches.length ? styles.listContent : styles.emptyContent}
          refreshControl={
            <RefreshControl
              refreshing={refreshing}
              onRefresh={() => {
                setRefreshing(true);
                void loadBatches(1, "replace");
              }}
            />
          }
          ListEmptyComponent={
            loading ? (
              <ActivityIndicator style={styles.emptyLoader} />
            ) : loadErrorMessage ? (
              <EmptyState
                title={t("messages.loadFailed")}
                description={loadErrorMessage}
                primaryAction={{
                  label: t("common:actions.retry"),
                  icon: "refresh",
                  onPress: () => void loadBatches(1, "replace"),
                }}
              />
            ) : (
              <EmptyState title={t("empty.noBatchTitle")} description={t("empty.noBatchDescription")} />
            )
          }
          renderItem={({ item }) => (
            <Card mode="outlined" style={styles.batchCard}>
              <Card.Content style={styles.batchCardContent}>
                <View style={styles.batchHeader}>
                  <View style={styles.batchTitleBlock}>
                    <Text variant="titleMedium" style={styles.batchNumber}>
                      {item.batchNumber}
                    </Text>
                    <Text variant="bodySmall" style={styles.mutedText}>
                      {formatDomesticSupplierLabel(item.supplierCode, item.supplierName)}
                    </Text>
                  </View>
                  <Text variant="labelMedium" style={styles.countBadge}>
                    {item.totalCount}
                  </Text>
                </View>
                <View style={styles.metricRow}>
                  <Text variant="bodySmall" style={styles.mutedText}>
                    {t("fields.normalCount", { value: item.normalCount })}
                  </Text>
                  <Text variant="bodySmall" style={styles.mutedText}>
                    {t("fields.setCount", { value: item.setCount })}
                  </Text>
                  <Text variant="bodySmall" style={styles.mutedText}>
                    {formatDateTime(item.createdAt, localeTag)}
                  </Text>
                </View>
                <View style={styles.cardActions}>
                  <Button compact mode="outlined" onPress={() => openDetail(item)}>
                    {t("actions.detail")}
                  </Button>
                  <Button
                    compact
                    icon="content-copy"
                    onPress={() => handleCopyBatch(item.batchNumber)}
                    loading={copyingBatchNumber === item.batchNumber}
                    disabled={copyingBatchNumber === item.batchNumber || busy}
                  >
                    {t("actions.copy")}
                  </Button>
                  <Button compact icon="download" onPress={() => handleExport(item.batchNumber)} disabled={busy}>
                    {t("actions.export")}
                  </Button>
                </View>
              </Card.Content>
            </Card>
          )}
          ListFooterComponent={
            hasMore ? (
              <Button
                mode="outlined"
                loading={loading}
                disabled={loading}
                style={styles.loadMoreButton}
                onPress={() => loadBatches(page + 1, "append")}
              >
                {t("actions.loadMore")}
              </Button>
            ) : null
          }
        />
      ) : (
        <DomesticProductList />
      )}

      {createVisible ? <CreateBatchModal
        onDismiss={() => setCreateVisible(false)}
        onReturnToList={() => {
          setCreateVisible(false);
          void loadBatches(1, "replace");
        }}
        onCreated={(result, supplier, prefix) => {
          setCreateVisible(false);
          setSnackbar(t(result.batchNumber ? "messages.createSuccess" : "wizard.createdWithoutBatch"));
          void loadBatches(1, "replace");
          if (result.batchNumber) {
            void openDetail({
              batchNumber: result.batchNumber,
              supplierCode: supplier.supplierCode,
              supplierName: supplier.supplierName,
              prefixCode: prefix?.prefixCode,
              normalCount: result.normalProductCount,
              setCount: result.setProductCount,
              totalCount: result.totalCreated,
              createdAt: new Date().toISOString(),
            });
          }
        }}
      /> : null}

      <Portal>
        <Modal
          visible={detailVisible}
          onDismiss={() => {
            setDetailVisible(false);
            setDetailEdits({});
          }}
          contentContainerStyle={styles.detailModal}
        >
          <View style={styles.detailHeader}>
            <View>
              <Text variant="titleMedium">{selectedBatch?.batchNumber}</Text>
              <Text variant="bodySmall" style={styles.mutedText}>
                {detail?.supplierName || selectedBatch?.supplierName || selectedBatch?.supplierCode}
              </Text>
            </View>
            <View style={styles.detailHeaderActions}>
              <Button
                compact
                mode="contained"
                icon="content-save"
                loading={detailSaving}
                disabled={!detail || detailLoading || detailSaving}
                onPress={handleSaveDetailChanges}
              >
                {t("actions.saveChanges")}
              </Button>
              <Button
                compact
                icon="download"
                loading={busy}
                disabled={!selectedBatch || busy || detailSaving}
                onPress={() => selectedBatch && handleExport(selectedBatch.batchNumber)}
              >
                {t("actions.export")}
              </Button>
            </View>
          </View>
          <Divider style={styles.divider} />
          {detailLoading ? (
            <ActivityIndicator style={styles.detailLoader} />
          ) : detailErrorMessage ? (
            <EmptyState
              title={t("messages.loadDetailFailed")}
              description={detailErrorMessage}
              primaryAction={{
                label: t("common:actions.retry"),
                icon: "refresh",
                onPress: () => selectedBatch && void openDetail(selectedBatch),
              }}
              secondaryAction={{
                label: t("common:actions.close"),
                icon: "close",
                onPress: () => setDetailVisible(false),
              }}
            />
          ) : (
            <ScrollView style={styles.detailList}>
              {(detail?.items ?? []).map((item, index) => (
                <View key={`${item.hbProductNo}-${item.barcode}-${index}`} style={styles.detailItem}>
                  <View style={styles.detailItemHeader}>
                    <Text variant="titleSmall" style={styles.itemNumber}>
                      {item.hbProductNo || item.itemNumber || "--"}
                    </Text>
                    <Text variant="labelSmall" style={styles.typeBadge}>
                      {typeLabel(item.productType, t)}
                    </Text>
                  </View>
                  <TextInput
                    mode="outlined"
                    dense
                    label={t("fields.productName")}
                    value={detailEdits[item.productCode || item.itemNumber]?.productName ?? item.productName ?? ""}
                    onChangeText={(value) => updateDetailEdit(item.productCode || item.itemNumber, { productName: value })}
                    style={styles.detailInput}
                  />
                  <Text variant="bodySmall" style={styles.mutedText}>
                    {t("fields.barcode", { value: item.barcode || "--" })}
                  </Text>
                  <View style={styles.detailMetaRow}>
                    <TextInput
                      mode="outlined"
                      dense
                      label={t("fields.privateLabelPriceLabel")}
                      value={detailEdits[item.productCode || item.itemNumber]?.privateLabelPrice ?? formatPriceInput(item.privateLabelPrice)}
                      keyboardType="decimal-pad"
                      onChangeText={(value) => updateDetailEdit(item.productCode || item.itemNumber, { privateLabelPrice: value })}
                      style={styles.detailPriceInput}
                    />
                    {item.parentItemNumber ? (
                      <Text variant="bodySmall" style={styles.mutedText}>
                        {t("fields.parent", { value: item.parentItemNumber })}
                      </Text>
                    ) : null}
                  </View>
                </View>
              ))}
            </ScrollView>
          )}
        </Modal>
      </Portal>

      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={3000}>
        {snackbar}
      </Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: "#F4F6F8",
  },
  header: {
    paddingHorizontal: 16,
    paddingTop: 12,
    paddingBottom: 10,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
  },
  title: {
    fontWeight: "700",
  },
  subtitle: {
    color: "#667085",
    marginTop: 2,
  },
  tabSwitcher: {
    marginHorizontal: 12,
    marginBottom: 8,
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
  batchCard: {
    marginBottom: 10,
    borderRadius: 12,
    borderColor: "#E4E7EC",
    backgroundColor: "#FFFFFF",
  },
  batchCardContent: {
    gap: 10,
  },
  batchHeader: {
    flexDirection: "row",
    alignItems: "flex-start",
    justifyContent: "space-between",
    gap: 10,
  },
  batchTitleBlock: {
    flex: 1,
  },
  batchNumber: {
    fontWeight: "700",
  },
  mutedText: {
    color: "#667085",
  },
  countBadge: {
    minWidth: 36,
    textAlign: "center",
    color: "#0958D9",
    backgroundColor: "#E6F4FF",
    borderRadius: 6,
    paddingHorizontal: 8,
    paddingVertical: 4,
  },
  metricRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 12,
  },
  cardActions: {
    flexDirection: "row",
    justifyContent: "flex-end",
    gap: 8,
  },
  loadMoreButton: {
    marginTop: 6,
  },
  detailModal: {
    margin: 14,
    padding: 14,
    borderRadius: 18,
    backgroundColor: "#FFFFFF",
    maxHeight: "86%",
  },
  detailHeader: {
    flexDirection: "row",
    alignItems: "flex-start",
    justifyContent: "space-between",
    gap: 10,
  },
  detailHeaderActions: {
    flexDirection: "row",
    flexWrap: "wrap",
    justifyContent: "flex-end",
    gap: 8,
  },
  divider: {
    marginVertical: 10,
  },
  detailLoader: {
    paddingVertical: 32,
  },
  detailList: {
    maxHeight: 560,
  },
  detailItem: {
    paddingVertical: 10,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: "#EAECF0",
    gap: 8,
  },
  detailItemHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
  },
  itemNumber: {
    flex: 1,
    fontWeight: "700",
  },
  typeBadge: {
    color: "#237804",
    backgroundColor: "#F6FFED",
    borderRadius: 6,
    paddingHorizontal: 8,
    paddingVertical: 3,
  },
  detailMetaRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    alignItems: "center",
    gap: 12,
  },
  detailInput: {
    backgroundColor: "#FFFFFF",
  },
  detailPriceInput: {
    width: 160,
    backgroundColor: "#FFFFFF",
  },
});

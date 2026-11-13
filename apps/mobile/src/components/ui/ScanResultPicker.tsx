import { Image, ScrollView, StyleSheet, View } from "react-native";
import { Button, Dialog, Portal, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import type { StoreOrderProductItem } from "@/modules/shop/types";

const PRODUCT_GRADE_CONFIG: Record<string, { color: string }> = {
  A: { color: "#722ED1" },
  B: { color: "#1890FF" },
  C: { color: "#FA8C16" },
  D: { color: "#F5222D" },
};

interface ScanResultPickerProps {
  visible: boolean;
  barcode?: string;
  items: StoreOrderProductItem[];
  onDismiss: () => void;
  onSelect: (product: StoreOrderProductItem) => void | Promise<void>;
  selectLabel?: string;
  cancelLabel?: string;
  title?: string;
  tip?: string;
  /** 非订货场景（如仓库打包选商品）没有起订量和订货价，隐藏这一行；默认显示，现有调用不受影响。 */
  hideOrderMeta?: boolean;
}

function resolveGrade(item: StoreOrderProductItem) {
  return item.grade?.trim().toUpperCase();
}

export function ScanResultPicker({
  visible,
  barcode,
  items,
  onDismiss,
  onSelect,
  selectLabel,
  cancelLabel,
  title,
  tip,
  hideOrderMeta = false,
}: ScanResultPickerProps) {
  const { t } = useAppTranslation(["common", "cart", "productQuery"]);
  const resolvedSelectLabel = selectLabel || t("common:actions.select");
  const resolvedTitle = title || t("productQuery:lookup.title");

  return (
    <Portal>
      <Dialog visible={visible} onDismiss={onDismiss}>
        <Dialog.Title>{resolvedTitle}</Dialog.Title>
        <Dialog.Content>
          <Text variant="bodyMedium" style={styles.tip}>
            {tip ?? t("productQuery:lookup.query", { value: barcode || t("common:na") })}
          </Text>
          <ScrollView style={styles.list}>
            {items.map((item) => {
              const grade = resolveGrade(item);
              const gradeColor = grade ? PRODUCT_GRADE_CONFIG[grade]?.color ?? "#999" : "#999";

              return (
                <View key={item.productCode} style={styles.item}>
                  <View style={styles.itemMain}>
                    {item.productImage ? (
                      <Image source={{ uri: item.productImage }} style={styles.itemImage} resizeMode="cover" />
                    ) : (
                      <View style={styles.itemImagePlaceholder} />
                    )}

                    <View style={styles.itemContent}>
                      {grade ? (
                        <View style={[styles.gradeBadge, { backgroundColor: gradeColor }]}>
                          <Text style={styles.gradeBadgeText}>Grade {grade}</Text>
                        </View>
                      ) : null}

                      <Text variant="titleSmall" numberOfLines={2}>
                        {item.productName || item.productCode}
                      </Text>
                      <Text variant="bodySmall" style={styles.secondaryText}>
                        {t("common:labels.itemNumber")}: {item.itemNumber || t("common:na")}
                      </Text>
                      {hideOrderMeta ? null : (
                        <View style={styles.metaRow}>
                          <Text variant="bodySmall" style={styles.secondaryText}>
                            {t("common:labels.minOrder")} {item.minOrderQuantity || 1}
                          </Text>
                          <Text variant="bodySmall" style={styles.priceText}>
                            ${Number(item.oemPrice ?? 0).toFixed(2)}
                          </Text>
                        </View>
                      )}
                    </View>
                  </View>

                  <Button
                    compact
                    mode="contained-tonal"
                    style={styles.selectButton}
                    contentStyle={styles.selectButtonContent}
                    onPress={() => void onSelect(item)}
                  >
                    {resolvedSelectLabel}
                  </Button>
                </View>
              );
            })}
          </ScrollView>
        </Dialog.Content>
        <Dialog.Actions>
          <Button onPress={onDismiss}>{cancelLabel || t("common:actions.cancel")}</Button>
        </Dialog.Actions>
      </Dialog>
    </Portal>
  );
}

const styles = StyleSheet.create({
  tip: {
    color: "#666",
    marginBottom: 12,
  },
  list: {
    maxHeight: 320,
  },
  item: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
    paddingVertical: 10,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: "#ddd",
  },
  itemMain: {
    flex: 1,
    flexDirection: "row",
    alignItems: "flex-start",
    gap: 8,
  },
  itemImage: {
    width: 40,
    height: 40,
    borderRadius: 8,
    backgroundColor: "#f5f5f5",
  },
  itemImagePlaceholder: {
    width: 40,
    height: 40,
    borderRadius: 8,
    backgroundColor: "#f0f0f0",
  },
  itemContent: {
    flex: 1,
    gap: 3,
  },
  gradeBadge: {
    alignSelf: "flex-start",
    borderRadius: 999,
    paddingHorizontal: 8,
    paddingVertical: 2,
  },
  gradeBadgeText: {
    color: "#fff",
    fontSize: 12,
    fontWeight: "700",
  },
  metaRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
  },
  secondaryText: {
    color: "#666",
  },
  priceText: {
    color: "#1677FF",
    fontWeight: "700",
  },
  selectButton: {
    marginLeft: 4,
    alignSelf: "center",
  },
  selectButtonContent: {
    minHeight: 32,
    paddingHorizontal: 2,
  },
});

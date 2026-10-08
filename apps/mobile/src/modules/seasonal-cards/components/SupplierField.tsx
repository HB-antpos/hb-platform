import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { HelperText, Icon, Text } from "react-native-paper";
import { OptionPickerSheet } from "@/components/ui/OptionPickerSheet";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import type { SeasonalCardSupplierOption } from "@/modules/seasonal-cards/types";
import { SEASONAL_CARD_COLORS } from "./palette";

/**
 * 供应商选择框 +「最近使用」。弹层是 OptionPickerSheet（Paper Portal），
 * 只能放在页面层，不能放进 BusinessSheet 之类的原生 Modal，否则会被盖住。
 */
export function SupplierField({
  title,
  placeholder,
  pickerTitle,
  searchPlaceholder,
  cancelLabel,
  recentLabel,
  selectedCode,
  selectedName,
  suppliers,
  recentSuppliers,
  loadErrorMessage,
  disabled = false,
  onSelect,
}: {
  title: string;
  placeholder: string;
  pickerTitle: string;
  searchPlaceholder: string;
  cancelLabel: string;
  recentLabel: string;
  selectedCode: string;
  selectedName: string;
  suppliers: SeasonalCardSupplierOption[];
  recentSuppliers: SeasonalCardSupplierOption[];
  loadErrorMessage?: string | null;
  disabled?: boolean;
  onSelect: (supplier: SeasonalCardSupplierOption) => void;
}) {
  const [pickerVisible, setPickerVisible] = useState(false);
  // 搜索同时匹配名称与编码（OptionPickerSheet 按 label 与 value 过滤）。
  const pickerOptions = useMemo(
    () =>
      suppliers.map((supplier) => ({
        value: supplier.supplierCode,
        label: `${supplier.supplierName} · ${supplier.supplierCode}`,
      })),
    [suppliers]
  );
  const hasSelection = Boolean(selectedCode);

  return (
    <View style={styles.block}>
      <Text style={styles.title}>{title}</Text>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={hasSelection ? `${title} ${selectedName}` : placeholder}
        accessibilityState={{ disabled }}
        disabled={disabled}
        onPress={() => setPickerVisible(true)}
        style={({ pressed }) => [
          styles.field,
          !hasSelection ? styles.fieldEmpty : null,
          pressed ? styles.pressed : null,
        ]}
      >
        <View style={styles.fieldText}>
          {hasSelection ? (
            <>
              <Text style={styles.fieldName}>{selectedName}</Text>
              <Text style={styles.fieldCode}>{selectedCode}</Text>
            </>
          ) : (
            <Text style={styles.fieldPlaceholder}>{placeholder}</Text>
          )}
        </View>
        <Icon source="magnify" size={20} color={HB_COLORS.textSecondary} />
      </Pressable>
      {loadErrorMessage ? (
        <HelperText type="error" visible style={styles.helper}>
          {loadErrorMessage}
        </HelperText>
      ) : null}
      {recentSuppliers.length ? (
        <View style={styles.recentRow}>
          <Text style={styles.recentLabel}>{recentLabel}</Text>
          {recentSuppliers.map((supplier) => {
            const selected =
              supplier.supplierCode.toLowerCase() === selectedCode.toLowerCase();
            return (
              <Pressable
                key={supplier.supplierCode}
                accessibilityRole="button"
                accessibilityState={{ selected, disabled }}
                disabled={disabled}
                onPress={() => onSelect(supplier)}
                style={({ pressed }) => [
                  styles.recentChip,
                  selected ? styles.recentChipSelected : null,
                  pressed ? styles.pressed : null,
                ]}
              >
                <Text style={[styles.recentChipText, selected ? styles.recentChipTextSelected : null]}>
                  {supplier.supplierName}
                </Text>
              </Pressable>
            );
          })}
        </View>
      ) : null}

      <OptionPickerSheet
        visible={pickerVisible}
        title={pickerTitle}
        cancelLabel={cancelLabel}
        options={pickerOptions}
        selectedValue={selectedCode || null}
        searchable
        searchPlaceholder={searchPlaceholder}
        onDismiss={() => setPickerVisible(false)}
        onSelect={(value) => {
          const supplier = suppliers.find((item) => item.supplierCode === value);
          setPickerVisible(false);
          if (supplier) {
            onSelect(supplier);
          }
        }}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  block: { gap: HB_SPACING.xs },
  title: { fontSize: 13, lineHeight: 18, fontWeight: "600", color: HB_COLORS.textSecondary },
  field: {
    minHeight: 52,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: 10,
    backgroundColor: HB_COLORS.white,
    paddingLeft: 14,
    paddingRight: 12,
    paddingVertical: 6,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  fieldEmpty: {
    borderColor: SEASONAL_CARD_COLORS.selectedBorder,
    borderStyle: "dashed",
  },
  pressed: { opacity: 0.8 },
  fieldText: { flex: 1, gap: 2 },
  fieldName: { fontSize: 15, lineHeight: 20, color: HB_COLORS.textPrimary },
  fieldCode: { fontSize: 12, lineHeight: 16, color: SEASONAL_CARD_COLORS.mutedText },
  fieldPlaceholder: { fontSize: 15, lineHeight: 20, color: SEASONAL_CARD_COLORS.selectedText },
  helper: { paddingHorizontal: 0 },
  recentRow: { flexDirection: "row", flexWrap: "wrap", alignItems: "center", gap: HB_SPACING.xs },
  recentLabel: { fontSize: 12, lineHeight: 16, color: SEASONAL_CARD_COLORS.mutedText },
  recentChip: {
    minHeight: 44,
    paddingHorizontal: 12,
    paddingVertical: 6,
    borderRadius: 22,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
    justifyContent: "center",
    maxWidth: "100%",
  },
  recentChipSelected: {
    borderColor: SEASONAL_CARD_COLORS.selectedBorder,
    backgroundColor: SEASONAL_CARD_COLORS.selectedBackground,
  },
  recentChipText: { fontSize: 13, lineHeight: 18, color: "#344054" },
  recentChipTextSelected: { color: SEASONAL_CARD_COLORS.selectedText },
});

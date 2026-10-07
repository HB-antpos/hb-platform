import { Platform, StyleSheet, View } from "react-native";
import { HelperText, Switch, Text } from "react-native-paper";

import type { PrinterTransportFilters } from "@/modules/printer/device-list";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

interface PrinterTransportFilterControlsProps {
  value: PrinterTransportFilters;
  onChange: (value: PrinterTransportFilters) => void;
  disabled?: boolean;
}

/** Android 经典蓝牙与 BLE 都能打印，默认两种都列出（DEFAULT_PRINTER_TRANSPORT_FILTERS）；可按需只看其中一种。 */
export function PrinterTransportFilterControls({
  value,
  onChange,
  disabled = false,
}: PrinterTransportFilterControlsProps) {
  const { t } = useAppTranslation(["settings"]);

  if (Platform.OS !== "android") {
    return null;
  }

  const hasSelectedTransport = value.showClassic || value.showBle;

  return (
    <View style={styles.container}>
      <View style={styles.filterRow}>
        <Text variant="bodyMedium" style={styles.label}>
          {t("printer.showClassic")}
        </Text>
        <Switch
          value={value.showClassic}
          onValueChange={(showClassic) => onChange({ ...value, showClassic })}
          disabled={disabled}
          accessibilityLabel={t("printer.showClassic")}
        />
      </View>
      <View style={styles.filterRow}>
        <Text variant="bodyMedium" style={styles.label}>
          {t("printer.showBle")}
        </Text>
        <Switch
          value={value.showBle}
          onValueChange={(showBle) => onChange({ ...value, showBle })}
          disabled={disabled}
          accessibilityLabel={t("printer.showBle")}
        />
      </View>
      {!hasSelectedTransport ? (
        <HelperText type="info" visible style={styles.helper}>
          {t("printer.selectTransport")}
        </HelperText>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    gap: HB_SPACING.xxs,
  },
  filterRow: {
    minHeight: 44,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  label: {
    flex: 1,
    color: HB_COLORS.textPrimary,
  },
  helper: {
    paddingVertical: 0,
  },
});

import { Platform, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";

import {
  getDisplayPrinterTransport,
  getPrinterDeviceIcon,
  isUnsupportedPrinterTransport,
  requiresSystemPairing,
} from "@/modules/printer/device-list";
import { isBlePrintingSupported } from "@/modules/printer/native";
import type { PrinterDevice } from "@/modules/printer/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

interface PrinterDeviceDetailsProps {
  device: PrinterDevice;
}

const TRANSPORT_LABEL_KEYS = {
  classic: "printer.transportClassic",
  ble: "printer.transportBle",
  dual: "printer.transportDual",
  unknown: "printer.transportUnknown",
} as const;

/** 显示地址与系统报告的能力，不以同名设备或图标猜测打印通道。 */
export function PrinterDeviceDetails({ device }: PrinterDeviceDetailsProps) {
  const { t } = useAppTranslation(["settings"]);
  const transport = getDisplayPrinterTransport(device, Platform.OS);
  const unsupported = isUnsupportedPrinterTransport(device, Platform.OS, isBlePrintingSupported());
  const needsSystemPairing = requiresSystemPairing(device, Platform.OS);
  // BLE 不看配对状态，直接连接；经典蓝牙未配对时提示去系统蓝牙设置配对。
  const connectHint = transport === "ble"
    ? t("printer.bleDirectConnect")
    : device.bonded ? t("printer.bonded") : needsSystemPairing ? t("printer.systemPairingRequired") : t("printer.unbonded");
  const icon = getPrinterDeviceIcon(device) === "printer" ? "printer-outline" : "bluetooth";

  return (
    <View style={styles.container}>
      <View style={styles.iconBox}>
        <Icon source={icon} size={20} color={unsupported ? HB_COLORS.warning : HB_COLORS.brand} />
      </View>
      <View style={styles.meta}>
        <Text variant="bodyMedium" style={styles.name} numberOfLines={1}>
          {device.name || device.address}
        </Text>
        <Text variant="bodySmall" style={styles.address} numberOfLines={1}>
          {device.address}
        </Text>
        <Text
          variant="bodySmall"
          style={[styles.secondary, needsSystemPairing && styles.unbonded]}
          numberOfLines={2}
        >
          {unsupported
            ? t(TRANSPORT_LABEL_KEYS[transport])
            : `${t(TRANSPORT_LABEL_KEYS[transport])} · ${connectHint}`}
        </Text>
        {unsupported ? (
          <Text variant="bodySmall" style={styles.unsupported} numberOfLines={2}>
            {t("printer.bleUnsupported")}
          </Text>
        ) : null}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    minWidth: 0,
    flexDirection: "row",
    alignItems: "flex-start",
    gap: HB_SPACING.xs,
  },
  iconBox: {
    width: 32,
    height: 32,
    alignItems: "center",
    justifyContent: "center",
    flexShrink: 0,
  },
  meta: {
    flex: 1,
    minWidth: 0,
    gap: 1,
  },
  name: {
    color: HB_COLORS.textPrimary,
    fontWeight: "600",
  },
  address: {
    color: HB_COLORS.textSecondary,
    fontVariant: ["tabular-nums"],
  },
  secondary: {
    color: HB_COLORS.textSecondary,
  },
  unbonded: {
    color: HB_COLORS.warning,
    fontWeight: "700",
  },
  unsupported: {
    color: HB_COLORS.warning,
    fontWeight: "700",
  },
});

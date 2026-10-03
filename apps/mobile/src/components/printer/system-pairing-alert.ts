import { Alert } from "react-native";

import { openBluetoothSettings } from "@/modules/printer/native";
import type { PrinterDevice } from "@/modules/printer/types";

type Translate = (key: string, options?: Record<string, unknown>) => string;

/** 经典蓝牙打印机未配对：引导用户去系统蓝牙设置配对，App 内不再代为发起配对。 */
export function showSystemPairingRequiredAlert(t: Translate, device: PrinterDevice) {
  Alert.alert(
    t("dialogs.printerPairingTitle"),
    t("dialogs.printerPairingMessage", {
      printer: device.name || device.address,
      address: device.address,
    }),
    [
      { text: t("common:actions.cancel"), style: "cancel" },
      {
        text: t("dialogs.printerPairingAction"),
        onPress: () => void openBluetoothSettings(),
      },
    ]
  );
}

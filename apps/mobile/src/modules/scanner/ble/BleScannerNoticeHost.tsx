import { StyleSheet } from "react-native";
import { Snackbar } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { useBleScannerStore } from "./ble-scanner-state";

/**
 * BLE 扫码枪在业务页面上的轻提示（目前只有「当前页面不接收扫码」）。
 * 扫码枪不占输入框，所以没人接收时用户看不到任何反应，必须给一句提示。
 */
export function BleScannerNoticeHost() {
  const { t } = useAppTranslation("settings");
  const notice = useBleScannerStore((state) => state.notice);
  const clearNotice = useBleScannerStore((state) => state.clearNotice);
  return (
    <Snackbar
      key={notice?.id ?? 0}
      visible={Boolean(notice)}
      onDismiss={clearNotice}
      duration={2_000}
      wrapperStyle={styles.wrapper}
    >
      {notice ? t("bleScanner.notice.noReceiver") : ""}
    </Snackbar>
  );
}

const styles = StyleSheet.create({
  // 让开底部标签栏。
  wrapper: { bottom: 72 },
});

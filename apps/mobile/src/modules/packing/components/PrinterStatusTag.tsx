import { Pressable, StyleSheet } from "react-native";
import { useRouter } from "expo-router";
import { usePrinterStore } from "@/modules/printer/state";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { Tag } from "./pack-ui";

/** 打印机连接状态胶囊（批量箱标页用）：点开去设置里连接打印机。 */
export function PrinterStatusTag() {
  const { t } = useAppTranslation("packing");
  const router = useRouter();
  const connected = usePrinterStore((state) => state.status) === "connected";
  const label = connected ? t("home.printerConnected") : t("home.printerDisconnected");
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={label} onPress={() => router.push("/(shell)/settings")} style={styles.press}>
      <Tag tone={connected ? "green" : "orange"} icon={connected ? "printer-check" : "printer-alert"} label={label} />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  press: { minHeight: 44, justifyContent: "center" },
});

import { useCallback, type ComponentType } from "react";
import { StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { Button, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";

import { isSupplierAssistantNativeAvailable } from "@/modules/supplier-assistant/native-availability";
import {
  resolveSupplierAssistantLocale,
  supplierAssistantText,
} from "@/modules/supplier-assistant/supplier-assistant-copy";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

/**
 * 「订货助手」路由。
 *
 * 中文注释：Expo Router 启动时会加载全部路由文件，而 react-native-webview 一加载就强制要求原生模块；
 * OTA 推到还没装 WebView 的旧原生包时，顶层 import 会让整个 App 启动即崩。
 * 所以这里先探测原生模块，探测到才 require 真正的页面；探测不到显示「请更新 App」。
 */
let cachedScreen: ComponentType | null = null;

function loadSupplierAssistantScreen(): ComponentType {
  if (!cachedScreen) {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    cachedScreen = (require("@/modules/supplier-assistant/supplier-assistant-screen") as typeof import("@/modules/supplier-assistant/supplier-assistant-screen")).SupplierAssistantScreen;
  }
  return cachedScreen;
}

export default function SupplierAssistantRoute() {
  if (isSupplierAssistantNativeAvailable()) {
    const Screen = loadSupplierAssistantScreen();
    return <Screen />;
  }
  return <SupplierAssistantUpdateRequired />;
}

function SupplierAssistantUpdateRequired() {
  const router = useRouter();
  const { language } = useAppTranslation("common");
  const locale = resolveSupplierAssistantLocale(language);
  const goBack = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.replace("/(shell)/workbench");
  }, [router]);
  return (
    <SafeAreaView style={styles.safe} edges={["top"]} testID="supplier-assistant-update-required">
      <View style={styles.centered}>
        <Text style={styles.message}>{supplierAssistantText(locale, "state.updateRequired")}</Text>
        <Button mode="contained" icon="chevron-left" onPress={goBack}>
          {supplierAssistantText(locale, "action.back")}
        </Button>
      </View>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  centered: { alignItems: "center", flex: 1, gap: HB_SPACING.sm, justifyContent: "center", padding: HB_SPACING.lg },
  message: { color: HB_COLORS.textPrimary, fontSize: 16, fontWeight: "600", textAlign: "center" },
});

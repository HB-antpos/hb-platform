// 「收银端设置」入口：全局 / 门店 / 设备三个页签。
// 看需要 PosSettings.View，改需要 PosSettings.Manage（只有 View 时所有分段控件只读、不显示保存按钮）。
import { useCallback, useState } from "react";
import { StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { SegmentedButtons } from "react-native-paper";
import { CashScreenFrame } from "@/modules/store-cash/ui";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_SPACING } from "@/shared/theme/tokens";
import { DevicesPanel } from "./DevicesPanel";
import { GlobalPanel } from "./GlobalPanel";
import { MessageState, usePosSettingsAccess } from "./panel-common";
import { StoresPanel } from "./StoresPanel";

type PosSettingsTab = "global" | "stores" | "devices";

export function PosSettingsScreen() {
  const { t } = useAppTranslation(["posSettings", "common"]);
  const router = useRouter();
  const { canView, canManage } = usePosSettingsAccess();
  const [tab, setTab] = useState<PosSettingsTab>("global");

  const goBack = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.replace("/(shell)/workbench");
  }, [router]);

  if (!canView) {
    return (
      <CashScreenFrame title={t("title")} onBack={goBack}>
        <MessageState text={t("states.notAllowed")} />
      </CashScreenFrame>
    );
  }

  return (
    <CashScreenFrame title={t("title")} onBack={goBack}>
      <View style={styles.tabs}>
        <SegmentedButtons
          value={tab}
          onValueChange={(value) => setTab(value as PosSettingsTab)}
          density="small"
          buttons={[
            { value: "global", label: t("tabs.global"), labelStyle: styles.tabLabel },
            { value: "stores", label: t("tabs.stores"), labelStyle: styles.tabLabel },
            { value: "devices", label: t("tabs.devices"), labelStyle: styles.tabLabel },
          ]}
        />
      </View>
      {/* 全局页签始终挂载（只是隐藏）：切到别的页签再回来，没保存的修改不会丢。 */}
      <View style={[styles.panel, tab === "global" ? null : styles.hidden]}>
        <GlobalPanel canManage={canManage} />
      </View>
      {tab === "stores" ? <View style={styles.panel}><StoresPanel /></View> : null}
      {tab === "devices" ? <View style={styles.panel}><DevicesPanel /></View> : null}
    </CashScreenFrame>
  );
}

const styles = StyleSheet.create({
  tabs: { paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.sm },
  tabLabel: { fontSize: 14, marginHorizontal: 0 },
  panel: { flex: 1 },
  hidden: { display: "none" },
});

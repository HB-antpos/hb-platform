import { useEffect, useState } from "react";
import { StyleSheet } from "react-native";
import { Button, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { useAuthStore } from "@/store/auth-store";
import { useDeviceStore } from "@/store/device-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { canAccountPick, isPickerUsable, usePickerStore } from "../picker-store";
import { PICK_COLORS } from "../components/pick-theme";
import { PickerConfirmView } from "./PickerConfirmView";
import { PickOrderListView } from "./PickOrderListView";

/**
 * 订单拣货入口：先确认拣货人（扫员工码或本机账号），确认后进入订单列表。
 * 拣货人只存内存；账号切换或员工码凭证过期会自动回到确认页。
 */
export function WarehousePickingEntryScreen() {
  const { t } = useAppTranslation("warehousePicking");
  const router = useRouter();
  const focused = useIsFocused();
  const user = useAuthStore((state) => state.user);
  const access = useAuthStore((state) => state.access);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const deviceSession = useDeviceStore((state) => state.session);
  const picker = usePickerStore((state) => state.picker);
  const clearPicker = usePickerStore((state) => state.clearPicker);
  const [nowMs, setNowMs] = useState(() => Date.now());

  // 回到本页时重新判断凭证是否过期。
  useEffect(() => {
    if (focused) setNowMs(Date.now());
  }, [focused]);

  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));

  if (review || sessionKind === "iosReview") {
    return <ScreenMessage message={t("messages.reviewUnavailable")} onBack={goBack} />;
  }

  const userGuid = user?.userGUID || user?.userGuid || null;
  const hasDevice = Boolean(deviceSession?.hardwareId && deviceSession.authCode);
  if (!userGuid && !hasDevice) {
    return <ScreenMessage message={t("messages.notAllowed")} onBack={goBack} />;
  }

  if (!isPickerUsable(picker, userGuid, nowMs)) {
    return (
      <PickerConfirmView
        account={
          userGuid
            ? { userGuid, name: user?.fullName?.trim() || user?.username || userGuid, canPick: canAccountPick(access) }
            : null
        }
        onBack={goBack}
      />
    );
  }

  return <PickOrderListView picker={picker} onBack={goBack} onSwitchPicker={clearPicker} />;
}

export function ScreenMessage({ message, onBack }: { message: string; onBack: () => void }) {
  const { t } = useAppTranslation("warehousePicking");
  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.message}>
      <Text accessibilityLiveRegion="polite" style={styles.messageText}>
        {message}
      </Text>
      <Button onPress={onBack}>{t("actions.back")}</Button>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  message: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24, backgroundColor: PICK_COLORS.background },
  messageText: { textAlign: "center", color: PICK_COLORS.ink },
});

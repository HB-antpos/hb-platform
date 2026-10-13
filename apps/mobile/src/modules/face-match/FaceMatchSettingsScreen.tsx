// 「人脸比对设置」页（分店维度）：选一家门店 → 修改该店人脸比对档案（需要 FaceMatch.Manage）+ 管理该店摄像头启停（需要 FaceCapture.Cameras.Manage）。
// 入口只要两者任一；进来后每个区域按自己的权限显示。门店选择沿用 App 现有的门店服务与选择弹层，不改动全局已选门店。
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Alert, KeyboardAvoidingView, Platform, ScrollView, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { ActivityIndicator, Snackbar, Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { resolveSettingsSections } from "./access";
import { CamerasSection } from "./CamerasSection";
import { ProfileSection } from "./ProfileSection";
import { formatStoreLabel, useFaceSettingsGuard, useStoreNameMap } from "./shared";
import { useFaceStoreOptions } from "./use-face-match";
import { useLeaveGuard } from "./use-leave-guard";
import { FaceScreenFrame, FaceScreenMessage, FilterChip, Notice, faceStyles } from "./ui";

export function FaceMatchSettingsScreen() {
  const router = useRouter();
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const blocked = useFaceSettingsGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));
  if (blocked) return <FaceScreenMessage message={blocked} backLabel={t("common:actions.back")} onBack={goBack} />;
  // 账号变化时重建页面状态，避免把上一个账号的编辑值留在屏幕上。
  return <SettingsContent key={userGuid} onBack={goBack} />;
}

function SettingsContent({ onBack }: { onBack: () => void }) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const sections = resolveSettingsSections(hasPermission);
  const { stores, isLoading, preferredStoreCode } = useFaceStoreOptions();
  const storeNames = useStoreNameMap(stores);
  const [storeCode, setStoreCode] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [snack, setSnack] = useState("");
  const [profileDirty, setProfileDirty] = useState(false);
  const [profileBusy, setProfileBusy] = useState(false);
  const allowLeaveRef = useRef(false);

  // 默认选 App 当前已选门店（在可选列表里才用），否则第一家；只读取，不改全局已选门店。
  useEffect(() => {
    if (storeCode && stores.some((store) => store.storeCode === storeCode)) return;
    const preferred = stores.find((store) => store.storeCode === preferredStoreCode) ?? stores[0] ?? null;
    setStoreCode(preferred?.storeCode ?? null);
  }, [preferredStoreCode, storeCode, stores]);

  useLeaveGuard({ dirty: profileDirty, busy: profileBusy, allowLeaveRef });

  const requestStoreChange = useCallback(
    (next: string | null) => {
      setPickerOpen(false);
      if (!next || next === storeCode) return;
      if (!profileDirty) {
        setStoreCode(next);
        return;
      }
      // 档案有未保存的修改：换店会丢掉它们，先确认。
      Alert.alert(t("leave.discardTitle"), t("leave.discardMessage"), [
        { text: t("leave.keepEditing"), style: "cancel" },
        { text: t("leave.discard"), style: "destructive", onPress: () => setStoreCode(next) },
      ]);
    },
    [profileDirty, storeCode, t],
  );

  const storeLabel = useMemo(() => (storeCode ? formatStoreLabel(storeCode, storeNames) : ""), [storeCode, storeNames]);
  const selectedStore = stores.find((store) => store.storeCode === storeCode) ?? null;
  const notify = useCallback((message: string) => setSnack(message), []);

  let body;
  if (isLoading && stores.length === 0) {
    body = (
      <View style={faceStyles.state}>
        <ActivityIndicator color={HB_COLORS.brand} />
        <Text style={faceStyles.muted}>{t("states.loading")}</Text>
      </View>
    );
  } else if (stores.length === 0 || !storeCode) {
    body = (
      <View style={faceStyles.state}>
        <EmptyState title={t("settings.noStores")} />
      </View>
    );
  } else {
    body = (
      <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled" keyboardDismissMode="on-drag">
        {sections.profile ? <Notice tone="warning" text={t("settings.legalNotice")} style={styles.notice} /> : null}
        <View style={styles.storeRow}>
          <Text style={faceStyles.muted}>{t("settings.store")}</Text>
          <FilterChip icon="storefront-outline" selected label={selectedStore ? selectedStore.storeName || selectedStore.storeCode : storeCode} onPress={() => setPickerOpen(true)} />
        </View>
        {sections.profile ? (
          <ProfileSection
            storeCode={storeCode}
            storeLabel={storeLabel}
            onDirtyChange={setProfileDirty}
            onBusyChange={setProfileBusy}
            onNotify={notify}
          />
        ) : null}
        {sections.cameras ? <CamerasSection storeCode={storeCode} onNotify={notify} /> : null}
      </ScrollView>
    );
  }

  return (
    <FaceScreenFrame title={t("settings.title")} onBack={onBack}>
      <KeyboardAvoidingView style={styles.flex} behavior={Platform.OS === "ios" ? "padding" : undefined}>
        {body}
      </KeyboardAvoidingView>
      <StorePickerModal
        presentation="sheet"
        visible={pickerOpen}
        stores={stores}
        selectedStoreCode={storeCode}
        title={t("settings.pickStore")}
        cancelLabel={t("common:actions.cancel")}
        onDismiss={() => setPickerOpen(false)}
        onSelectStore={(store) => requestStoreChange(store?.storeCode ?? null)}
      />
      <Snackbar visible={snack !== ""} onDismiss={() => setSnack("")} duration={4000}>
        {snack}
      </Snackbar>
    </FaceScreenFrame>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  content: { paddingBottom: HB_SPACING.xl },
  notice: { marginHorizontal: HB_SPACING.md, marginTop: HB_SPACING.xs },
  storeRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.sm },
});

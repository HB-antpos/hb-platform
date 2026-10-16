// 「人脸比对设置」页（分店维度）：选一家门店 → 修改该店人脸比对档案（需要 FaceMatch.Manage）+ 管理该店摄像头启停（需要 FaceCapture.Cameras.Manage）。
// 入口只要两者任一；进来后每个区域按自己的权限显示。门店选择沿用 App 现有的门店服务与选择弹层，不改动全局已选门店。
// 内容抽成 SettingsPanel（不含页面框架与权限守卫），供「人脸比对」合并页（FaceHubScreen）的「设置」页签嵌入；
// FaceMatchSettingsScreen 保留原页面外壳（框架 + 守卫）。旧路由 face-match-settings 现在重定向到合并页，外壳不再被路由使用。
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
  return (
    <FaceScreenFrame title={t("settings.title")} onBack={goBack}>
      <SettingsPanel key={userGuid} />
    </FaceScreenFrame>
  );
}

/**
 * 「设置」内容面板：不含页面框架与权限守卫，由外壳（单页或「人脸比对」合并页）负责。
 * 档案有未保存修改时，离开页面仍由 useLeaveGuard 拦截；合并页切换页签不卸载本面板，编辑中的值不会丢。
 */
export function SettingsPanel() {
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
  // 键盘避让要知道本区域顶部离窗口顶部多远：嵌进合并页后上面多了页签条，KeyboardAvoidingView 自己只量得出相对父容器的位置，
  // 所以在外层容器上量一次「窗口内 y」当作 keyboardVerticalOffset（布局变化、页签重新显示时都会重新量）。
  const areaRef = useRef<View>(null);
  const [keyboardOffset, setKeyboardOffset] = useState(0);
  const measureArea = useCallback(() => {
    areaRef.current?.measureInWindow((_x, y) => setKeyboardOffset((previous) => (previous === y ? previous : y)));
  }, []);

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
        {sections.cameras ? <CamerasSection storeCode={storeCode} stores={stores} onNotify={notify} /> : null}
      </ScrollView>
    );
  }

  return (
    <>
      <View ref={areaRef} style={styles.flex} onLayout={measureArea}>
        <KeyboardAvoidingView
          style={styles.flex}
          behavior={Platform.OS === "ios" ? "padding" : undefined}
          keyboardVerticalOffset={keyboardOffset}
        >
          {body}
        </KeyboardAvoidingView>
      </View>
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
    </>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  content: { paddingBottom: HB_SPACING.xl },
  notice: { marginHorizontal: HB_SPACING.md, marginTop: HB_SPACING.xs },
  storeRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.sm },
});

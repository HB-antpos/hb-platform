/**
 * 移动端「促销管理」页：同一路由内切换三屏（列表 / 新建编辑 / 只读详情），不新增路由，避免牵连菜单登记。
 * - 查看需 Promotions.View（菜单已控制）；新建、编辑、启停、提前结束、复制到本店需 Promotions.Edit。
 * - 分店只列当前账号能管理的店；后端店级接口对无权分店也会返回 STORE_FORBIDDEN。
 */
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { BackHandler, StyleSheet, View } from "react-native";
import { ActivityIndicator, Snackbar } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { getManageableStoresForSession } from "@/modules/shop/store-scope";
import type { Store } from "@/modules/shop/types";
import { useStores } from "@/modules/shop/use-stores";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { resolveLocaleTag } from "@/shared/i18n/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { PERMISSIONS } from "@/shared/utils/access";
import { useAuthStore } from "@/store/auth-store";
import { fetchPromotionDetail } from "./api";
import { PromotionDetailView } from "./promotion-detail-view";
import { PromotionEditorView } from "./promotion-editor-view";
import { PromotionListView } from "./promotion-list-view";
import { canEditPromotionInStore } from "./promotion-logic";
import { getPromotionErrorKind } from "./promotion-screen-model";
import type { PromotionDetail, PromotionListItem } from "./types";

type ScreenView =
  | { kind: "list" }
  | { kind: "editor"; detail: PromotionDetail | null; key: number }
  | { kind: "detail"; detail: PromotionDetail };

export function PromotionsScreen() {
  const { t, language } = useAppTranslation(["promotions", "common"]);
  const access = useAuthStore((state) => state.access);
  const { stores, selectedStoreCode, isDeviceMode, deviceBoundStore, selectStore } = useStores();
  const [storePickerVisible, setStorePickerVisible] = useState(false);
  const [view, setView] = useState<ScreenView>({ kind: "list" });
  const [opening, setOpening] = useState(false);
  const [reloadToken, setReloadToken] = useState(0);
  const [snackbar, setSnackbar] = useState("");
  const editorBackHandler = useRef<(() => void) | null>(null);

  const canEdit = access.hasPermission(PERMISSIONS.Promotions.Edit);

  // 只列当前账号能管理的分店（与人员管理等页面同一口径；仓库管理后端不限分店）。
  const manageableStores = useMemo(
    () =>
      getManageableStoresForSession({
        stores,
        isDeviceMode,
        deviceBoundStore,
        isAdmin: access.isAdmin || access.isWarehouseManager,
      }),
    [access.isAdmin, access.isWarehouseManager, deviceBoundStore, isDeviceMode, stores],
  );
  // 全局所选分店不在可管理范围时，退回第一家可管理分店（不改全局选择）。
  const store: Store | null =
    manageableStores.find((item) => item.storeCode === selectedStoreCode) ?? manageableStores[0] ?? null;
  const storeCode = store?.storeCode ?? null;
  const storeName = store?.storeName || storeCode || "";
  const canSwitchStore = !isDeviceMode && manageableStores.length > 1;

  const localeTag = resolveLocaleTag(language);
  const weekdayLabels = useMemo(() => {
    // 2026-01-05 是周一：依次取周一到周日的窄星期名。
    const formatter = new Intl.DateTimeFormat(localeTag, { weekday: "narrow" });
    return Array.from({ length: 7 }, (_, index) => formatter.format(new Date(2026, 0, 5 + index)));
  }, [localeTag]);

  const getErrorMessage = useCallback(
    (error: unknown, fallbackKey: string) => {
      if (getPromotionErrorKind(error) === "storeForbidden") return t("messages.storeForbidden");
      return resolveLocalizedErrorMessage(error, { language, t, fallbackKey });
    },
    [language, t],
  );

  const backToList = useCallback((reload: boolean) => {
    setView({ kind: "list" });
    if (reload) setReloadToken((token) => token + 1);
  }, []);

  // Android 返回键：编辑页先走「放弃修改」确认，详情页回列表。
  useEffect(() => {
    if (view.kind === "list") return undefined;
    const subscription = BackHandler.addEventListener("hardwareBackPress", () => {
      if (view.kind === "editor") {
        editorBackHandler.current?.();
      } else {
        backToList(false);
      }
      return true;
    });
    return () => subscription.remove();
  }, [backToList, view.kind]);

  // 切换分店时回到列表（编辑中的内容属于原分店）。
  useEffect(() => {
    setView({ kind: "list" });
  }, [storeCode]);

  const openItem = useCallback(
    async (item: PromotionListItem) => {
      if (!storeCode) return;
      setOpening(true);
      try {
        // 列表行可能不带商品明细，统一取完整详情。
        const detail = await fetchPromotionDetail(item.id, storeCode);
        if (!detail) {
          setSnackbar(t("messages.detailMissing"));
          return;
        }
        if (canEdit && canEditPromotionInStore(detail)) {
          setView({ kind: "editor", detail, key: Date.now() });
        } else {
          setView({ kind: "detail", detail });
        }
      } catch (error) {
        setSnackbar(getErrorMessage(error, "messages.detailFailed"));
      } finally {
        setOpening(false);
      }
    },
    [canEdit, getErrorMessage, storeCode, t],
  );

  const handleCopied = useCallback(
    async (copied: PromotionDetail | null) => {
      setReloadToken((token) => token + 1);
      if (copied?.id && storeCode) {
        // 复制接口返回的就是新促销；再取一次店级详情，确保范围 / 可编辑标记是本店口径。
        try {
          const fresh = (await fetchPromotionDetail(copied.id, storeCode)) ?? copied;
          setView({ kind: "editor", detail: fresh, key: Date.now() });
          return;
        } catch {
          setView({ kind: "editor", detail: copied, key: Date.now() });
          return;
        }
      }
      setView({ kind: "list" });
    },
    [storeCode],
  );

  const registerBackHandler = useCallback((handler: (() => void) | null) => {
    editorBackHandler.current = handler;
  }, []);

  return (
    <SafeAreaView style={styles.safeArea} edges={["top", "left", "right"]}>
      {/* 列表常驻（隐藏而不卸载），从编辑 / 详情返回时保留分段、搜索与滚动位置。 */}
      <View style={[styles.flex, view.kind !== "list" && styles.hidden]}>
        <PromotionListView
          t={t}
          storeCode={storeCode}
          store={store}
          canSwitchStore={canSwitchStore}
          canEdit={canEdit}
          reloadToken={reloadToken}
          onOpenStorePicker={() => setStorePickerVisible(true)}
          onOpenItem={(item) => void openItem(item)}
          onCreate={() => setView({ kind: "editor", detail: null, key: Date.now() })}
          onMessage={setSnackbar}
          getErrorMessage={getErrorMessage}
        />
      </View>

      {view.kind === "editor" && storeCode ? (
        <View style={styles.flex}>
          <PromotionEditorView
            key={view.key}
            t={t}
            storeCode={storeCode}
            storeName={storeName}
            detail={view.detail}
            localeTag={localeTag}
            weekdayLabels={weekdayLabels}
            onClose={({ saved }) => backToList(saved)}
            onMessage={setSnackbar}
            getErrorMessage={getErrorMessage}
            registerBackHandler={registerBackHandler}
          />
        </View>
      ) : null}

      {view.kind === "detail" && storeCode ? (
        <View style={styles.flex}>
          <PromotionDetailView
            t={t}
            storeCode={storeCode}
            storeName={storeName}
            detail={view.detail}
            canEdit={canEdit}
            onBack={() => backToList(false)}
            onCopied={(copied) => void handleCopied(copied)}
            onMessage={setSnackbar}
            getErrorMessage={getErrorMessage}
          />
        </View>
      ) : null}

      {opening ? (
        <View style={styles.opening} pointerEvents="auto">
          <ActivityIndicator size="large" />
        </View>
      ) : null}

      <StorePickerModal
        presentation="sheet"
        visible={storePickerVisible}
        stores={manageableStores}
        selectedStoreCode={storeCode}
        title={t("common:labels.selectStore")}
        cancelLabel={t("common:actions.cancel")}
        onDismiss={() => setStorePickerVisible(false)}
        onSelectStore={async (next: Store | null) => {
          if (next) await selectStore(next);
          setStorePickerVisible(false);
        }}
      />

      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={4000}>
        {snackbar}
      </Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: BUSINESS_UI.screen,
  flex: { flex: 1 },
  hidden: { display: "none" },
  opening: {
    ...StyleSheet.absoluteFillObject,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "rgba(255,255,255,0.5)",
  },
});

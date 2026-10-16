import { StyleSheet, View } from "react-native";
import { Icon } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { isStoreManageable } from "@/modules/shop/store-scope";
import type { Store } from "@/modules/shop/types";
import { ActionRow, InfoCard, NoticeBox, PeopleTag } from "@/modules/people/ui";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";

/**
 * 店长「切换分店」底部弹层（设计稿 A1③）：每家分店直接标出「可管理 / 仅查看」和人数，
 * 「全部已分配分店」只能看、不能新增；底部说明「仅查看」的含义。
 *
 * 数据逻辑与原 StorePickerModal 一致：候选分店由调用方传入（POS 已启用的分店），
 * 「可管理」按 manageableStores（来自原始分店集合）判断，设备绑定模式不给「全部」选项。
 */
export function StoreSwitchSheet({
  visible,
  stores,
  manageableStores,
  selectedStoreCode,
  includeAllOption,
  counts,
  onDismiss,
  onSelectStore,
}: {
  visible: boolean;
  stores: Store[];
  manageableStores: Store[];
  selectedStoreCode: string | null;
  includeAllOption: boolean;
  /** 各分店人数；还没取到（或取失败）传 null，行上只显示分店编码。 */
  counts: { total: number; byStore: Record<string, number> } | null;
  onDismiss: () => void;
  onSelectStore: (store: Store | null) => void | Promise<void>;
}) {
  const { t } = useAppTranslation(["people", "userManagement"]);
  const check = <Icon source="check" size={20} color={HB_COLORS.brand} />;

  return (
    <BusinessSheet visible={visible} title={t("people:storeSheet.title")} subtitle={t("people:storeSheet.subtitle")} onDismiss={onDismiss}>
      <InfoCard>
        {includeAllOption ? (
          <ActionRow
            first
            icon="store-outline"
            tone={selectedStoreCode ? "gray" : "blue"}
            title={t("userManagement:currentStore.allRelated")}
            subtitle={
              counts
                ? t("people:storeSheet.allCaptionWithCount", { count: counts.total })
                : t("people:storeSheet.allCaption")
            }
            chevron={false}
            right={selectedStoreCode ? undefined : check}
            onPress={() => void onSelectStore(null)}
          />
        ) : null}
        {stores.map((store, index) => {
          const selected = store.storeCode === selectedStoreCode;
          const manageable = isStoreManageable(store.storeCode, manageableStores);
          const count = counts?.byStore[store.storeCode];
          return (
            <ActionRow
              key={store.storeCode}
              first={!includeAllOption && index === 0}
              icon={selected ? "storefront" : "storefront-outline"}
              tone={selected ? "blue" : "gray"}
              title={t("userManagement:currentStore.value", { name: store.storeName || store.storeCode, code: store.storeCode })}
              subtitle={typeof count === "number" ? t("people:storeSheet.peopleCount", { count }) : undefined}
              chevron={false}
              right={
                <View style={styles.right}>
                  <PeopleTag
                    label={manageable ? t("userManagement:currentStore.manageableBadge") : t("userManagement:currentStore.viewOnlyBadge")}
                    tone={manageable ? "blue" : "gray"}
                    icon={manageable ? undefined : "eye-outline"}
                  />
                  {selected ? check : null}
                </View>
              }
              onPress={() => void onSelectStore(store)}
            />
          );
        })}
      </InfoCard>
      <NoticeBox tone="blue">{t("people:storeSheet.viewOnlyNotice")}</NoticeBox>
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  right: { alignItems: "center", flexDirection: "row", gap: 6 },
});

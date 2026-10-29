import { useMemo, type ReactNode } from "react";
import {
  Modal as NativeModal,
  Pressable,
  ScrollView,
  StyleSheet,
  useWindowDimensions,
  View,
} from "react-native";
import { Button, Modal, Portal, RadioButton, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { HB_COLORS } from "@/shared/theme/tokens";
import { sortStoresByName } from "@/shared/utils/name-sort";
import type { Store } from "@/modules/shop/types";

interface StorePickerModalProps {
  presentation?: "dialog" | "sheet";
  /**
   * 默认通过 Paper Portal 渲染在应用根部；当调用方本身位于原生 Modal（如 BusinessSheet）内时，
   * PortalHost 会被那个原生 Modal 盖住，必须改用原生 Modal 承载才能显示在最上层。
   */
  host?: "portal" | "native-modal";
  visible: boolean;
  stores: Store[];
  selectedStoreCode?: string | null;
  title: string;
  cancelLabel: string;
  includeAllOption?: boolean;
  allLabel?: string;
  renderAllLabel?: (label: string) => ReactNode;
  renderStoreLabel?: (store: Store) => ReactNode;
  onDismiss: () => void;
  onSelectStore: (store: Store | null) => void | Promise<void>;
}

export function StorePickerModal({
  presentation = "dialog",
  host = "portal",
  visible,
  stores,
  selectedStoreCode,
  title,
  cancelLabel,
  includeAllOption = false,
  allLabel,
  renderAllLabel,
  renderStoreLabel,
  onDismiss,
  onSelectStore,
}: StorePickerModalProps) {
  const { height } = useWindowDimensions();
  const insets = useSafeAreaInsets();
  const isSheet = presentation === "sheet";
  // 所有分店选择列表统一按名字排序：不依赖调用方传入的顺序（有的按编号、有的按接口顺序）。
  const sortedStores = useMemo(() => sortStoresByName(stores), [stores]);
  const rowCount = sortedStores.length + (includeAllOption ? 1 : 0);
  const maxListHeight = Math.max(160, Math.min(480, height * 0.58));
  const listHeight = Math.min(rowCount * 48, maxListHeight);

  const content = (
      <Modal
        visible={visible}
        onDismiss={onDismiss}
        style={isSheet ? styles.sheetOverlay : undefined}
        contentContainerStyle={[styles.container, isSheet && styles.sheet, isSheet && { paddingBottom: Math.max(insets.bottom, 16) }]}
      >
        {isSheet ? <View style={styles.handle} /> : null}
        <Text variant="titleLarge" style={[styles.title, isSheet && styles.sheetTitle]}>
          {title}
        </Text>
        <ScrollView
          style={[styles.list, { height: listHeight }]}
          contentContainerStyle={styles.listContent}
          bounces={false}
          alwaysBounceVertical={false}
          keyboardShouldPersistTaps="handled"
          nestedScrollEnabled
          overScrollMode="never"
          showsVerticalScrollIndicator
        >
          {includeAllOption ? (
            <PickerRow
              label={allLabel ?? title}
              content={renderAllLabel?.(allLabel ?? title)}
              selected={!selectedStoreCode}
              onPress={() => void onSelectStore(null)}
            />
          ) : null}
          {sortedStores.map((store) => (
            <PickerRow
              key={store.storeCode}
              label={store.storeName || store.storeCode}
              content={renderStoreLabel?.(store)}
              selected={store.storeCode === selectedStoreCode}
              onPress={() => void onSelectStore(store)}
            />
          ))}
        </ScrollView>
        <View style={styles.actions}>
          <Button contentStyle={isSheet ? { minHeight: 44 } : undefined} onPress={onDismiss}>{cancelLabel}</Button>
        </View>
      </Modal>
  );

  if (host === "native-modal") {
    return (
      <NativeModal
        transparent
        visible={visible}
        animationType="fade"
        statusBarTranslucent
        onRequestClose={onDismiss}
      >
        {content}
      </NativeModal>
    );
  }

  return <Portal>{content}</Portal>;
}

function PickerRow({
  label,
  content,
  selected,
  onPress,
}: {
  label: string;
  content?: ReactNode;
  selected: boolean;
  onPress: () => void;
}) {
  return (
    <View style={styles.item}>
      <RadioButton
        value={label}
        status={selected ? "checked" : "unchecked"}
        onPress={onPress}
      />
      {content ? (
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={label}
          accessibilityState={{ selected }}
          onPress={onPress}
          style={styles.itemButton}
        >
          {content}
        </Pressable>
      ) : (
        <Button
          accessibilityState={{ selected }}
          mode="text"
          onPress={onPress}
          style={styles.itemButton}
          contentStyle={styles.itemButtonContent}
        >
          {label}
        </Button>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  sheetOverlay: { justifyContent: "flex-end" },
  sheet: { width: "100%", maxWidth: 680, borderTopLeftRadius: 18, borderTopRightRadius: 18, borderBottomLeftRadius: 0, borderBottomRightRadius: 0, paddingHorizontal: 16, paddingTop: 10 },
  sheetTitle: { fontWeight: "700", color: HB_COLORS.textPrimary },
  handle: { width: 44, height: 4, borderRadius: 2, backgroundColor: HB_COLORS.outline, alignSelf: "center", marginBottom: 16 },
  actions: {
    alignItems: "flex-end",
    marginTop: 8,
  },
  container: {
    alignSelf: "center",
    backgroundColor: "#FFFFFF",
    borderRadius: 12,
    padding: 20,
    width: "88%",
  },
  item: {
    alignItems: "center",
    flexDirection: "row",
    gap: 4,
    minHeight: 48,
    paddingVertical: 2,
  },
  itemButton: {
    flex: 1,
    minWidth: 0,
  },
  itemButtonContent: {
    justifyContent: "flex-start",
  },
  list: {
    flexGrow: 0,
  },
  listContent: {
    paddingBottom: 4,
  },
  title: {
    marginBottom: 12,
  },
});

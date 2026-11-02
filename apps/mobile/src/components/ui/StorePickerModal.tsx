import { useMemo, useState, type ReactNode } from "react";
import {
  Modal as NativeModal,
  Pressable,
  ScrollView,
  StyleSheet,
  useWindowDimensions,
  View,
} from "react-native";
import { Button, Icon, Modal, Portal, RadioButton, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { HB_COLORS } from "@/shared/theme/tokens";
import { sortStoresByName } from "@/shared/utils/name-sort";
import type { Store } from "@/modules/shop/types";

/**
 * 多选模式：行前显示复选框，点选只改草稿，按确认按钮才一次性回传；中途关闭不改变原选择。
 * 空数组代表「全部」（需同时开启 includeAllOption 才有对应的一行）。
 */
export interface StorePickerMultipleOptions {
  selectedStoreCodes: readonly string[];
  confirmLabel: string;
  /** 草稿已选家数的提示，如「已选 3 家分店」；未选时显示 allLabel。 */
  selectedCountLabel: (count: number) => string;
  onApply: (storeCodes: string[]) => void;
}

interface StorePickerModalBaseProps {
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
}

type StorePickerModalProps = StorePickerModalBaseProps & (
  | { multiple?: undefined; onSelectStore: (store: Store | null) => void | Promise<void> }
  | { multiple: StorePickerMultipleOptions; onSelectStore?: undefined }
);

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
  multiple,
}: StorePickerModalProps) {
  const { height } = useWindowDimensions();
  const insets = useSafeAreaInsets();
  const isSheet = presentation === "sheet";
  // 所有分店选择列表统一按名字排序：不依赖调用方传入的顺序（有的按编号、有的按接口顺序）。
  const sortedStores = useMemo(() => sortStoresByName(stores), [stores]);
  const rowCount = sortedStores.length + (includeAllOption ? 1 : 0);
  const maxListHeight = Math.max(160, Math.min(480, height * 0.58));
  const listHeight = Math.min(rowCount * 48, maxListHeight);
  // 多选草稿：每次打开时从调用方的已选值重新开始（渲染期同步，避免打开瞬间闪一下旧草稿）。
  const [draftCodes, setDraftCodes] = useState<string[]>([]);
  const [draftOpen, setDraftOpen] = useState(false);
  if (visible !== draftOpen) {
    setDraftOpen(visible);
    if (visible && multiple) setDraftCodes([...multiple.selectedStoreCodes]);
  }
  const draftSet = useMemo(() => new Set(draftCodes), [draftCodes]);
  const toggleDraft = (storeCode: string) => setDraftCodes((current) => (
    current.includes(storeCode) ? current.filter((code) => code !== storeCode) : [...current, storeCode]
  ));

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
          {multiple ? (
            <>
              {includeAllOption ? (
                <MultiPickerRow
                  label={allLabel ?? title}
                  content={renderAllLabel?.(allLabel ?? title)}
                  checked={draftCodes.length === 0}
                  onPress={() => setDraftCodes([])}
                />
              ) : null}
              {sortedStores.map((store) => (
                <MultiPickerRow
                  key={store.storeCode}
                  label={store.storeName || store.storeCode}
                  content={renderStoreLabel?.(store)}
                  checked={draftSet.has(store.storeCode)}
                  onPress={() => toggleDraft(store.storeCode)}
                />
              ))}
            </>
          ) : (
            <>
              {includeAllOption ? (
                <PickerRow
                  label={allLabel ?? title}
                  content={renderAllLabel?.(allLabel ?? title)}
                  selected={!selectedStoreCode}
                  onPress={() => void onSelectStore?.(null)}
                />
              ) : null}
              {sortedStores.map((store) => (
                <PickerRow
                  key={store.storeCode}
                  label={store.storeName || store.storeCode}
                  content={renderStoreLabel?.(store)}
                  selected={store.storeCode === selectedStoreCode}
                  onPress={() => void onSelectStore?.(store)}
                />
              ))}
            </>
          )}
        </ScrollView>
        {multiple ? (
          <View style={styles.multiFooter}>
            <Text variant="bodySmall" style={styles.multiSummary} numberOfLines={1}>
              {draftCodes.length === 0 ? (allLabel ?? title) : multiple.selectedCountLabel(draftCodes.length)}
            </Text>
            <View style={styles.multiActions}>
              <Button mode="outlined" style={styles.multiButton} contentStyle={{ minHeight: 44 }} onPress={onDismiss}>
                {cancelLabel}
              </Button>
              <Button
                mode="contained"
                style={[styles.multiButton, styles.multiPrimary]}
                contentStyle={{ minHeight: 44 }}
                onPress={() => multiple.onApply(draftCodes)}
              >
                {multiple.confirmLabel}
              </Button>
            </View>
          </View>
        ) : (
          <View style={styles.actions}>
            <Button contentStyle={isSheet ? { minHeight: 44 } : undefined} onPress={onDismiss}>{cancelLabel}</Button>
          </View>
        )}
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

function MultiPickerRow({
  label,
  content,
  checked,
  onPress,
}: {
  label: string;
  content?: ReactNode;
  checked: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="checkbox"
      accessibilityLabel={label}
      accessibilityState={{ checked }}
      onPress={onPress}
      style={({ pressed }) => [styles.multiItem, pressed ? styles.multiItemPressed : null]}
    >
      <Icon
        source={checked ? "checkbox-marked" : "checkbox-blank-outline"}
        size={22}
        color={checked ? HB_COLORS.brand : HB_COLORS.outline}
      />
      <View style={styles.itemButton}>
        {content ?? <Text variant="bodyLarge" numberOfLines={1}>{label}</Text>}
      </View>
    </Pressable>
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
  multiItem: {
    alignItems: "center",
    flexDirection: "row",
    gap: 12,
    minHeight: 48,
    paddingHorizontal: 8,
    paddingVertical: 4,
  },
  multiItemPressed: {
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  multiFooter: {
    gap: 8,
    marginTop: 8,
  },
  multiSummary: {
    color: HB_COLORS.textSecondary,
  },
  multiActions: {
    flexDirection: "row",
    gap: 8,
  },
  multiButton: {
    flex: 1,
  },
  multiPrimary: {
    flex: 2,
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

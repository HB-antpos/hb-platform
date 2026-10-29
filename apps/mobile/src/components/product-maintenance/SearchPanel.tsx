import { useEffect, useRef, useState } from "react";
import { Keyboard, Platform, StyleSheet, useWindowDimensions, View } from "react-native";
import { IconButton, Menu, Searchbar, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { extractVisibleBarcodeInput } from "@/modules/scanner/visible-barcode-input";

interface SearchPanelProps {
  value: string;
  loading?: boolean;
  lastHitLabel?: string;
  refreshing?: boolean;
  onChangeText: (value: string) => void;
  onFocus: () => void;
  onBlur: () => void;
  onSubmit: () => void;
  onScannerInput?: (barcode: string) => void;
  onClear: () => void;
  onScanPress?: () => void;
  onRefreshPress?: () => void;
  onOpenPrintSettings?: () => void;
  /** 已查到商品时，新建商品收进顶部一行为图标按钮，把首屏整行让给商品信息。 */
  onCreateProduct?: () => void;
  createProductDisabled?: boolean;
}

/** 窗口宽度低于该值（dp）视为窄屏：刷新 / 打印设置 / 新建商品收进「⋯」菜单，把整行宽度让给搜索框。 */
const COMPACT_WIDTH = 400;

/** 顶部一行：搜索框（左扫码、右清空）+ 搜索 + 刷新 + 打印设置；窄屏后三者收进「⋯」菜单。 */
export function SearchPanel({
  value,
  loading = false,
  lastHitLabel,
  refreshing = false,
  onChangeText,
  onFocus,
  onBlur,
  onSubmit,
  onScannerInput,
  onClear,
  onScanPress,
  onRefreshPress,
  onOpenPrintSettings,
  onCreateProduct,
  createProductDisabled = false,
}: SearchPanelProps) {
  const { t } = useAppTranslation(["productQuery", "common"]);
  const searchbarRef = useRef<React.ElementRef<typeof Searchbar>>(null);
  const inputValueRef = useRef(value);
  const { width: windowWidth } = useWindowDimensions();
  const compact = windowWidth < COMPACT_WIDTH;
  const [moreVisible, setMoreVisible] = useState(false);
  useEffect(() => {
    inputValueRef.current = value;
  }, [value]);

  useEffect(() => {
    const subscription = Keyboard.addListener("keyboardDidHide", () => {
      // Zebra 收起软键盘后输入框仍可能保持焦点；失焦才会交还隐藏扫码输入框。
      // 仅限安卓：iOS 模拟器 / 外接键盘下软键盘本就不弹，聚焦即触发 keyboardDidHide，会让搜索框刚聚焦就被失焦、无法输入。
      if (Platform.OS === "android" && searchbarRef.current?.isFocused()) searchbarRef.current.blur();
    });
    return () => subscription.remove();
  }, []);

  const handleSubmit = () => {
    searchbarRef.current?.blur();
    onSubmit();
  };

  const handleChangeText = (nextValue: string) => {
    const previousValue = inputValueRef.current;
    const keyboardVisible = Keyboard.isVisible();
    // DataWedge 可能把整条码交给仍聚焦的搜索框；不能只依赖隐藏输入框接收。
    const barcode =
      Platform.OS === "android" &&
      onScannerInput &&
      searchbarRef.current?.isFocused() &&
      !keyboardVisible
        ? extractVisibleBarcodeInput(previousValue, nextValue)
        : null;
    if (barcode) {
      searchbarRef.current?.blur();
      if (!loading) {
        inputValueRef.current = barcode;
        onScannerInput?.(barcode);
      }
      return;
    }
    inputValueRef.current = nextValue;
    onChangeText(nextValue);
  };

  return (
    <View style={styles.container}>
      <View style={styles.searchRow}>
        <Searchbar
          ref={searchbarRef}
          placeholder={t("search.placeholder")}
          value={value}
          onChangeText={handleChangeText}
          onFocus={onFocus}
          onBlur={onBlur}
          onSubmitEditing={handleSubmit}
          icon="barcode-scan"
          iconColor={HB_COLORS.action}
          onIconPress={onScanPress}
          searchAccessibilityLabel={t("search.scan")}
          // 清空还会清掉当前商品详情，因此关键字为空时也要保留 ×，不能用 Paper 自带的按值显示清除按钮。
          right={() => (
            <IconButton
              accessibilityLabel={t("common:actions.clear")}
              icon="close"
              size={18}
              disabled={loading && !value}
              onPress={onClear}
              style={styles.clearButton}
            />
          )}
          style={styles.searchbar}
          inputStyle={styles.input}
        />
        <IconButton
          accessibilityLabel={t("common:actions.search")}
          icon="magnify"
          mode="contained"
          containerColor="#EAF2FF"
          iconColor={HB_COLORS.action}
          size={20}
          loading={loading}
          disabled={loading}
          onPress={handleSubmit}
          style={styles.searchButton}
        />
        {compact ? (
          <Menu
            visible={moreVisible}
            onDismiss={() => setMoreVisible(false)}
            anchorPosition="bottom"
            anchor={
              <IconButton
                accessibilityLabel={t("search.more")}
                icon="dots-horizontal"
                size={20}
                onPress={() => setMoreVisible(true)}
                style={styles.iconButton}
              />
            }
          >
            <Menu.Item
              leadingIcon={refreshing ? "loading" : "refresh"}
              title={t("common:actions.refresh")}
              disabled={refreshing || !onRefreshPress}
              onPress={() => {
                setMoreVisible(false);
                onRefreshPress?.();
              }}
            />
            <Menu.Item
              leadingIcon="cog-outline"
              title={t("print.settingsTitle")}
              onPress={() => {
                setMoreVisible(false);
                onOpenPrintSettings?.();
              }}
            />
            {onCreateProduct ? (
              <Menu.Item
                leadingIcon="plus"
                title={t("createProduct.action")}
                disabled={createProductDisabled}
                onPress={() => {
                  setMoreVisible(false);
                  onCreateProduct();
                }}
              />
            ) : null}
          </Menu>
        ) : (
          <>
            <IconButton
              accessibilityLabel={t("common:actions.refresh")}
              icon={refreshing ? "loading" : "refresh"}
              size={20}
              disabled={refreshing || !onRefreshPress}
              onPress={onRefreshPress}
              style={styles.iconButton}
            />
            <IconButton
              accessibilityLabel={t("print.settingsTitle")}
              icon="cog-outline"
              size={20}
              onPress={onOpenPrintSettings}
              style={styles.iconButton}
            />
            {onCreateProduct ? (
              <IconButton
                accessibilityLabel={t("createProduct.action")}
                icon="plus"
                mode="contained-tonal"
                size={20}
                disabled={createProductDisabled}
                onPress={onCreateProduct}
                style={styles.iconButton}
              />
            ) : null}
          </>
        )}
      </View>
      {lastHitLabel ? (
        <Text variant="labelSmall" style={styles.lastHit} numberOfLines={1}>
          {t("search.lastHit", { value: lastHitLabel })}
        </Text>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    paddingHorizontal: HB_SPACING.sm,
    paddingTop: HB_SPACING.xxs,
    paddingBottom: HB_SPACING.xs,
    gap: HB_SPACING.xxs,
  },
  searchRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 2,
  },
  searchbar: {
    flex: 1,
    height: 44,
    elevation: 0,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  input: {
    alignSelf: "center",
    minHeight: 0,
    paddingBottom: 0,
    paddingTop: 0,
    fontSize: 14,
  },
  clearButton: {
    margin: 0,
    marginRight: 2,
  },
  searchButton: {
    width: 44,
    height: 44,
    margin: 0,
    marginLeft: HB_SPACING.xxs,
    borderRadius: HB_RADIUS.control,
  },
  iconButton: {
    width: 36,
    height: 44,
    margin: 0,
  },
  lastHit: {
    color: "#98A2B3",
    paddingHorizontal: HB_SPACING.xxs,
  },
});

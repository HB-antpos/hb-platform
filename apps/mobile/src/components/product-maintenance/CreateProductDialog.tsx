import { memo, forwardRef, useEffect, useRef, useState, type RefObject } from "react";
import {
  Keyboard,
  Platform,
  ScrollView,
  StyleSheet,
  TextInput,
  useWindowDimensions,
  View,
  type LayoutChangeEvent,
  type TextInputProps,
} from "react-native";
import { Button, IconButton, Modal, Switch, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import type {
  CreateProductDialogError,
  CreateProductErrorField,
  CreateProductFormValues,
} from "@/modules/product-maintenance/create-product-validation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS as C } from "@/shared/theme/tokens";

interface Props {
  visible: boolean;
  values: CreateProductFormValues;
  supplierLabel: string | null;
  suppliersLoading: boolean;
  hasSuppliers: boolean;
  saving: boolean;
  generating: boolean;
  /**
   * 校验失败、创建失败等错误必须显示在弹层里：弹层之上看不到页面级 Snackbar，
   * 之前这些提示全被盖住，用户只看到「点了没反应」。
   */
  error: CreateProductDialogError | null;
  onChange: (patch: Partial<CreateProductFormValues>) => void;
  onSelectSupplier: () => void;
  onReloadSuppliers: () => void;
  onGenerate: () => void;
  onScan: () => void;
  onDismiss: () => void;
  onSubmit: () => void;
}

export const CreateProductDialog = memo(function CreateProductDialog(props: Props) {
  const { t } = useAppTranslation(["productQuery", "common"]);
  const { height, width } = useWindowDimensions();
  const insets = useSafeAreaInsets();
  const [keyboardHeight, setKeyboardHeight] = useState(0);
  const nameRef = useRef<TextInput>(null);
  const itemRef = useRef<TextInput>(null);
  const barcodeRef = useRef<TextInput>(null);
  const purchaseRef = useRef<TextInput>(null);
  const retailRef = useRef<TextInput>(null);
  const busy = props.saving || props.generating;
  const restricted = props.values.localSupplierCode.trim() === "200";
  const errorField = props.error?.field ?? null;
  const scrollRef = useRef<ScrollView>(null);
  // 各字段所在行在滚动内容里的纵坐标，出错时据此滚动定位。
  const fieldYRef = useRef<Partial<Record<CreateProductErrorField, number>>>({});
  const trackY =
    (...keys: CreateProductErrorField[]) =>
    (event: LayoutChangeEvent) => {
      for (const key of keys) {
        fieldYRef.current[key] = event.nativeEvent.layout.y;
      }
    };

  useEffect(() => {
    if (!props.visible) {
      setKeyboardHeight(0);
      return;
    }
    // 键盘占用区域从弹窗可用高度中扣除，标题和操作栏始终留在滚动区域外。
    const show = Keyboard.addListener(
      Platform.OS === "ios" ? "keyboardWillChangeFrame" : "keyboardDidShow",
      (event) => {
        setKeyboardHeight(Math.max(0, height - event.endCoordinates.screenY));
      },
    );
    const hide = Keyboard.addListener(
      Platform.OS === "ios" ? "keyboardWillHide" : "keyboardDidHide",
      () => setKeyboardHeight(0),
    );
    return () => {
      show.remove();
      hide.remove();
    };
  }, [height, props.visible]);

  // 出错时：滚动到出错字段并聚焦，用户一眼看到问题所在、可以直接改。
  // 每次失败 parent 都会生成新的 error 对象，所以同一字段连续失败也会重新定位。
  useEffect(() => {
    if (!props.visible || !props.error?.field) {
      return;
    }
    const target = props.error.field;
    const inputs: Record<
      Exclude<CreateProductErrorField, "supplier">,
      RefObject<TextInput | null>
    > = {
      productName: nameRef,
      itemNumber: itemRef,
      barcode: barcodeRef,
      purchasePrice: purchaseRef,
      retailPrice: retailRef,
    };
    const scrollToTarget = () =>
      scrollRef.current?.scrollTo({
        y: Math.max(0, (fieldYRef.current[target] ?? 0) - 12),
        animated: true,
      });

    scrollToTarget();
    // 供应商是选择按钮不是输入框，只滚动不聚焦。
    if (target !== "supplier") {
      inputs[target].current?.focus();
    }
    // 键盘弹出会让弹窗变矮：等布局稳定后再滚一次，避免出错字段被挤出可见区域。
    const timer = setTimeout(scrollToTarget, 350);
    return () => clearTimeout(timer);
  }, [props.error, props.visible]);

  const field = (
    key:
      | "productName"
      | "itemNumber"
      | "barcode"
      | "purchasePrice"
      | "retailPrice",
  ) => ({
    label: t(`createProduct.fields.${key}`),
    value: props.values[key],
    editable: !busy,
    onChangeText: (value: string) => props.onChange({ [key]: value }),
  });

  return (
    <Modal
      visible={props.visible}
      dismissable={!busy}
      onDismiss={props.onDismiss}
      style={{
        marginTop: insets.top + 12,
        marginBottom: Math.max(keyboardHeight, insets.bottom) + 12,
      }}
      contentContainerStyle={[
        styles.dialog,
        {
          width: Math.min(width - 24, 520),
          maxHeight:
            height - insets.top - Math.max(keyboardHeight, insets.bottom) - 24,
        },
      ]}
    >
      <View style={styles.header}>
        <View style={styles.titleBlock}>
          <Text variant="titleLarge" style={styles.title}>
            {t("createProduct.title")}
          </Text>
          <Text style={styles.subtitle}>{t("createProduct.formHint")}</Text>
        </View>
        <IconButton
          icon="close"
          size={21}
          disabled={busy}
          onPress={props.onDismiss}
          accessibilityLabel={t("common:actions.cancel")}
          style={styles.close}
        />
      </View>
      <ScrollView
        ref={scrollRef}
        style={styles.scroll}
        contentContainerStyle={styles.body}
        keyboardShouldPersistTaps="handled"
        keyboardDismissMode="on-drag"
        showsVerticalScrollIndicator
      >
        <View style={styles.accuracyNotice}>
          <Text style={styles.accuracyNoticeText}>
            {t("createProduct.accuracyHint")}
          </Text>
          <Text style={[styles.accuracyNoticeText, styles.accuracyConsequence]}>
            {t("createProduct.permissionWarning")}
          </Text>
        </View>
        <View style={styles.field} onLayout={trackY("supplier")}>
          <Text style={styles.label}>{t("createProduct.fields.supplier")}</Text>
          <Button
            mode="outlined"
            icon="chevron-down"
            disabled={busy || props.suppliersLoading}
            onPress={() => {
              Keyboard.dismiss();
              if (props.hasSuppliers) props.onSelectSupplier();
              else props.onReloadSuppliers();
            }}
            style={[
              styles.supplier,
              restricted && styles.restrictedBorder,
              errorField === "supplier" && styles.invalidBorder,
            ]}
            contentStyle={styles.supplierContent}
            labelStyle={styles.supplierLabel}
          >
            {props.suppliersLoading
              ? t("createProduct.messages.suppliersLoading")
              : props.supplierLabel ||
                t(
                  props.hasSuppliers
                    ? "createProduct.selectSupplier"
                    : "createProduct.reloadSuppliers",
                )}
          </Button>
          {restricted ? (
            <Text accessibilityRole="alert" style={styles.warning}>
              {t("createProduct.messages.supplierRestricted")}
            </Text>
          ) : null}
        </View>
        <Field
          ref={nameRef}
          {...field("productName")}
          invalid={errorField === "productName"}
          onFieldLayout={trackY("productName")}
          placeholder={t("createProduct.namePlaceholder")}
          returnKeyType="next"
          onSubmitEditing={() => itemRef.current?.focus()}
        />
        <Field
          ref={itemRef}
          {...field("itemNumber")}
          invalid={errorField === "itemNumber"}
          onFieldLayout={trackY("itemNumber")}
          placeholder={t("createProduct.itemPlaceholder")}
          autoCapitalize="characters"
          autoCorrect={false}
          returnKeyType="next"
          onSubmitEditing={() => barcodeRef.current?.focus()}
        />
        <View style={styles.field} onLayout={trackY("barcode")}>
          <Text style={styles.label}>{t("createProduct.fields.barcode")}</Text>
          <View style={styles.barcodeRow}>
            <Field
              ref={barcodeRef}
              {...field("barcode")}
              invalid={errorField === "barcode"}
              hideLabel
              placeholder={t("createProduct.barcodePlaceholder")}
              autoCapitalize="none"
              autoCorrect={false}
              returnKeyType="next"
              onSubmitEditing={() => purchaseRef.current?.focus()}
            />
          </View>
          <View style={styles.barcodeActions}>
            <Button
              mode="contained-tonal"
              icon="camera-outline"
              compact
              disabled={busy || restricted}
              onPress={() => {
                Keyboard.dismiss();
                props.onScan();
              }}
              style={styles.generate}
              contentStyle={styles.generateContent}
              labelStyle={styles.generateLabel}
            >
              {t("createProduct.cameraScan")}
            </Button>
            <Button
              mode="contained-tonal"
              icon="barcode"
              compact
              loading={props.generating}
              disabled={
                busy || restricted || !props.values.localSupplierCode.trim()
              }
              onPress={props.onGenerate}
              style={styles.generate}
              contentStyle={styles.generateContent}
              labelStyle={styles.generateLabel}
              accessibilityLabel={t("createProduct.generateBarcode")}
            >
              {t("createProduct.generateShort")}
            </Button>
          </View>
        </View>
        <View
          style={styles.priceRow}
          onLayout={trackY("purchasePrice", "retailPrice")}
        >
          <Field
            ref={purchaseRef}
            {...field("purchasePrice")}
            invalid={errorField === "purchasePrice"}
            placeholder="0.00"
            keyboardType="decimal-pad"
            prefix="$"
          />
          <Field
            ref={retailRef}
            {...field("retailPrice")}
            invalid={errorField === "retailPrice"}
            placeholder="0.00"
            keyboardType="decimal-pad"
            prefix="$"
          />
        </View>
        <View style={styles.settings}>
          <View style={styles.settingRow}>
            <Text style={styles.settingLabel}>
              {t("createProduct.specialShort")}
            </Text>
            <Switch
              value={props.values.isSpecialProduct}
              disabled={busy}
              color={C.brand}
              onValueChange={(isSpecialProduct) =>
                props.onChange({ isSpecialProduct })
              }
              accessibilityLabel={t("createProduct.specialShort")}
            />
          </View>
          <View style={[styles.settingRow, styles.settingDivider]}>
            <Text style={styles.settingLabel}>
              {t("createProduct.autoPriceShort")}
            </Text>
            <Switch
              value={props.values.isAutoPricing}
              disabled={busy}
              color={C.brand}
              onValueChange={(isAutoPricing) =>
                props.onChange({ isAutoPricing })
              }
              accessibilityLabel={t("createProduct.autoPriceShort")}
            />
          </View>
        </View>
      </ScrollView>
      <View style={styles.footer}>
        {keyboardHeight > 0 ? (
          <Button
            compact
            onPress={Keyboard.dismiss}
            style={styles.keyboardDone}
          >
            {t("createProduct.keyboardDone")}
          </Button>
        ) : null}
        {props.error ? (
          // 放在固定的底栏里而不是滚动区：不管表单滚到哪，用户点完按钮都能立刻看到原因。
          <Text
            accessibilityRole="alert"
            accessibilityLiveRegion="polite"
            style={styles.errorText}
          >
            {props.error.message}
          </Text>
        ) : null}
        <View style={styles.actions}>
          <Button
            mode="outlined"
            disabled={busy}
            onPress={props.onDismiss}
            style={styles.cancel}
            contentStyle={styles.actionContent}
          >
            {t("common:actions.cancel")}
          </Button>
          <Button
            mode="contained"
            loading={props.saving}
            disabled={busy || restricted}
            onPress={props.onSubmit}
            style={styles.submit}
            buttonColor={C.brand}
            contentStyle={styles.actionContent}
          >
            {t("createProduct.action")}
          </Button>
        </View>
      </View>
    </Modal>
  );
}, (previous, next) => {
  // 隐藏期间不重复生成整张表单；打开或关闭时仍渲染，保留弹窗动画和键盘清理。
  // 打开后始终接收最新表单值与回调，不比较或缓存可见状态下的业务数据。
  return !previous.visible && !next.visible;
});

const Field = forwardRef<
  TextInput,
  TextInputProps & {
    label: string;
    hideLabel?: boolean;
    prefix?: string;
    /** 该字段是当前错误的出处：描边标红（优先于聚焦态，聚焦后红框仍然保留直到用户修改）。 */
    invalid?: boolean;
    /** 字段整行的布局回调，弹层用它记录纵坐标以便出错时滚动定位。 */
    onFieldLayout?: (event: LayoutChangeEvent) => void;
  }
>(function Field(
  { label, hideLabel, prefix, invalid, onFieldLayout, ...props },
  ref,
) {
  const [focused, setFocused] = useState(false);
  return (
    <View style={styles.field} onLayout={onFieldLayout}>
      {!hideLabel ? <Text style={styles.label}>{label}</Text> : null}
      <View
        style={[
          styles.inputFrame,
          focused && styles.inputFocused,
          invalid && styles.invalidBorder,
          props.editable === false && styles.inputDisabled,
        ]}
      >
        {prefix ? <Text style={styles.prefix}>{prefix}</Text> : null}
        <TextInput
          {...props}
          ref={ref}
          accessibilityLabel={label}
          placeholderTextColor="#98A2B3"
          style={styles.input}
          onFocus={() => setFocused(true)}
          onBlur={() => setFocused(false)}
        />
      </View>
    </View>
  );
});

const styles = StyleSheet.create({
  dialog: {
    alignSelf: "center",
    borderRadius: 20,
    backgroundColor: C.white,
    overflow: "hidden",
  },
  header: {
    padding: 20,
    paddingBottom: 16,
    flexDirection: "row",
    alignItems: "center",
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: C.outlineMuted,
  },
  titleBlock: { flex: 1, gap: 4 },
  title: { color: C.textPrimary, fontWeight: "700", fontSize: 21 },
  subtitle: { color: C.textSecondary, fontSize: 12 },
  close: { margin: -6, marginLeft: 8 },
  scroll: { flexShrink: 1, minHeight: 0 },
  body: { padding: 20, paddingTop: 16, gap: 14 },
  accuracyNotice: {
    backgroundColor: "#FFFAEB",
    borderWidth: 1,
    borderColor: "#FEE4B5",
    borderRadius: 8,
    padding: 12,
    gap: 4,
  },
  accuracyNoticeText: { color: C.warning, fontSize: 13, lineHeight: 20 },
  accuracyConsequence: { fontWeight: "600" },
  field: { gap: 6, flexShrink: 1, flexGrow: 1, minWidth: 0 },
  label: { color: C.textSecondary, fontSize: 12, fontWeight: "600" },
  supplier: {
    borderRadius: 10,
    borderColor: C.outline,
    backgroundColor: C.surface,
  },
  supplierContent: {
    minHeight: 46,
    flexDirection: "row-reverse",
    justifyContent: "space-between",
  },
  supplierLabel: {
    flex: 1,
    textAlign: "left",
    color: C.textPrimary,
    fontSize: 15,
    marginHorizontal: 12,
    marginVertical: 10,
  },
  restrictedBorder: { borderColor: C.warning },
  invalidBorder: { borderColor: C.danger },
  errorText: {
    color: C.danger,
    fontSize: 13,
    lineHeight: 18,
    fontWeight: "600",
    marginBottom: 10,
  },
  warning: {
    color: C.warning,
    backgroundColor: "#FFFAEB",
    borderRadius: 8,
    padding: 10,
    fontSize: 12,
    lineHeight: 18,
  },
  inputFrame: {
    minHeight: 46,
    borderWidth: 1,
    borderColor: C.outline,
    borderRadius: 10,
    backgroundColor: C.white,
    flexDirection: "row",
    alignItems: "center",
    paddingHorizontal: 12,
  },
  inputFocused: { borderColor: C.brand, backgroundColor: "#F8FBFF" },
  inputDisabled: { backgroundColor: C.surfaceMuted },
  input: {
    flex: 1,
    minWidth: 0,
    paddingVertical: 11,
    paddingHorizontal: 0,
    color: C.textPrimary,
    fontSize: 15,
  },
  prefix: { color: C.textSecondary, marginRight: 7, fontSize: 15 },
  barcodeRow: { flexDirection: "row", alignItems: "stretch", gap: 8 },
  barcodeActions: { flexDirection: "row", gap: 8, marginTop: 2 },
  generate: {
    flex: 1,
    borderRadius: 10,
    alignSelf: "flex-start",
    backgroundColor: "#EAF3FF",
  },
  generateContent: { minHeight: 46 },
  generateLabel: { fontSize: 13, color: C.action, marginHorizontal: 12 },
  priceRow: { flexDirection: "row", gap: 12 },
  settings: {
    backgroundColor: C.surfaceMuted,
    borderRadius: 12,
    paddingHorizontal: 12,
  },
  settingRow: {
    minHeight: 48,
    paddingVertical: 5,
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    gap: 12,
  },
  settingLabel: { flex: 1, fontSize: 14, color: C.textPrimary },
  settingDivider: {
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: C.outline,
  },
  footer: {
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: C.outlineMuted,
    padding: 16,
    backgroundColor: C.white,
  },
  actions: { flexDirection: "row", gap: 12 },
  cancel: { borderRadius: 10, borderColor: C.outline, flex: 1 },
  submit: { borderRadius: 10, flex: 2 },
  actionContent: { minHeight: 46 },
  keyboardDone: { alignSelf: "flex-end", marginTop: -10, marginBottom: 4 },
});

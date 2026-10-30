/**
 * 新增 / 编辑整页表单的共用界面部件（设计稿 A3）：页面壳、带标签的输入框、开关行、
 * 姓名区（姓 / 名 / 中间名一行 + 昵称 / 电话一行）、放弃修改确认框与离开拦截。
 */
import { useCallback, useEffect, useRef, useState, type ReactNode, type RefObject } from "react";
import {
  KeyboardAvoidingView,
  Platform,
  Pressable,
  ScrollView,
  StyleSheet,
  TextInput as NativeTextInput,
  View,
  type KeyboardTypeOptions,
  type LayoutChangeEvent,
  type TextInputProps,
} from "react-native";
import { useNavigation } from "expo-router";
import { usePreventRemove } from "@react-navigation/native";
import { Button, Dialog, IconButton, Portal, Switch, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";

import { StickyActionBar } from "../ui";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import type { PersonNameErrorCode, PersonNameErrors, PersonNameField, PersonNameInput } from "@/modules/users/validation";

// ───────────────────────── 页面壳 ─────────────────────────

/**
 * 整页表单外壳：顶部「×  标题 / 副标题」，中间可滚动内容（键盘弹出时避让），底部吸附操作栏。
 * 底部栏放在 KeyboardAvoidingView 之外，与此前的新增页行为一致。
 */
export function FormScreen({
  title,
  subtitle,
  onClose,
  closeLabel,
  children,
  footer,
  scrollToTopKey,
  overlay,
  scrollRef: externalScrollRef,
}: {
  title: string;
  subtitle?: string;
  onClose: () => void;
  closeLabel: string;
  children: ReactNode;
  footer?: ReactNode;
  /** 每次变化就滚回顶部（提交校验失败时，顶部的错误提示条才看得见）。 */
  scrollToTopKey?: number;
  /** 铺在整页上的内容（Snackbar 等），放在页壳内才能贴着屏幕底部显示。 */
  overlay?: ReactNode;
  /** 页面需要自己控制滚动（如校验失败滚到第一个错误，见 useFieldAnchors）时传入。 */
  scrollRef?: RefObject<ScrollView | null>;
}) {
  const internalScrollRef = useRef<ScrollView>(null);
  const scrollRef = externalScrollRef ?? internalScrollRef;
  useEffect(() => {
    if (scrollToTopKey) scrollRef.current?.scrollTo({ y: 0, animated: true });
  }, [scrollRef, scrollToTopKey]);
  return (
    <SafeAreaView style={styles.screen} edges={["top", "left", "right", "bottom"]}>
      <View style={styles.header}>
        <IconButton icon="close" size={22} accessibilityLabel={closeLabel} onPress={onClose} />
        <View style={styles.headerText}>
          <Text accessibilityRole="header" style={styles.headerTitle} numberOfLines={1}>{title}</Text>
          {subtitle ? <Text style={styles.headerSubtitle} numberOfLines={1}>{subtitle}</Text> : null}
        </View>
      </View>
      <KeyboardAvoidingView style={styles.flex} behavior={Platform.OS === "ios" ? "padding" : undefined}>
        <ScrollView ref={scrollRef} contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
          {children}
        </ScrollView>
      </KeyboardAvoidingView>
      {footer}
      {overlay}
    </SafeAreaView>
  );
}

/** 底部「取消 / 主按钮」操作栏。 */
export function FormFooter({
  cancelLabel,
  submitLabel,
  onCancel,
  onSubmit,
  submitDisabled = false,
  loading = false,
  cancelDisabled = false,
  hint,
}: {
  cancelLabel: string;
  submitLabel: string;
  onCancel: () => void;
  onSubmit: () => void;
  submitDisabled?: boolean;
  loading?: boolean;
  cancelDisabled?: boolean;
  hint?: string;
}) {
  return (
    <StickyActionBar hint={hint}>
      <Button mode="outlined" onPress={onCancel} disabled={cancelDisabled || loading} style={styles.footerButton} contentStyle={styles.footerButtonContent}>
        {cancelLabel}
      </Button>
      <Button
        mode="contained"
        buttonColor={HB_COLORS.action}
        onPress={onSubmit}
        loading={loading}
        disabled={submitDisabled || loading}
        style={styles.footerButtonPrimary}
        contentStyle={styles.footerButtonContent}
      >
        {submitLabel}
      </Button>
    </StickyActionBar>
  );
}

// ───────────────────────── 输入 ─────────────────────────

export interface FormFieldProps {
  label: string;
  required?: boolean;
  value: string;
  onChangeText?: (value: string) => void;
  placeholder?: string;
  /** 红色错误提示，显示在输入框下方（优先于 hint）。 */
  error?: string;
  /** 灰色说明文字。 */
  hint?: string;
  /** 只读（账号创建后不可修改等）：灰底、不可编辑。 */
  readOnly?: boolean;
  disabled?: boolean;
  keyboardType?: KeyboardTypeOptions;
  autoCapitalize?: TextInputProps["autoCapitalize"];
  maxLength?: number;
  secureTextEntry?: boolean;
  textContentType?: TextInputProps["textContentType"];
  autoComplete?: TextInputProps["autoComplete"];
  /** 输入框内右侧的小标签（如「可收信」）。 */
  adornment?: ReactNode;
  /** 输入框右侧的操作（如显示 / 隐藏密码）。 */
  trailing?: ReactNode;
  mono?: boolean;
  testID?: string;
  onBlur?: () => void;
  /** 传入时整个输入框变成按钮（不可键入），点击打开选择器（如生日）。 */
  onPress?: () => void;
}

export function FormField({
  label,
  required = false,
  value,
  onChangeText,
  placeholder,
  error,
  hint,
  readOnly = false,
  disabled = false,
  keyboardType,
  autoCapitalize = "none",
  maxLength,
  secureTextEntry,
  textContentType,
  autoComplete,
  adornment,
  trailing,
  mono = false,
  testID,
  onBlur,
  onPress,
}: FormFieldProps) {
  const locked = readOnly || disabled;
  const box = (
    <View
      style={[styles.inputBox, locked && styles.inputBoxLocked, Boolean(error) && styles.inputBoxError]}
      pointerEvents={onPress ? "none" : undefined}
    >
      <NativeTextInput
        testID={testID}
        style={[styles.input, mono && styles.mono, locked && styles.inputLocked]}
        value={value}
        onChangeText={onChangeText}
        onBlur={onBlur}
        placeholder={placeholder}
        placeholderTextColor="#98A2B3"
        editable={!locked && !onPress}
        keyboardType={keyboardType}
        autoCapitalize={autoCapitalize}
        autoCorrect={false}
        maxLength={maxLength}
        secureTextEntry={secureTextEntry}
        textContentType={textContentType}
        autoComplete={autoComplete}
        accessibilityLabel={label}
        accessibilityState={{ disabled: locked }}
      />
      {/* PeopleTag 自带 alignSelf: flex-start，会贴到输入框顶部；包一层居中。 */}
      {adornment ? <View style={styles.adornment}>{adornment}</View> : null}
      {trailing}
    </View>
  );
  return (
    <View style={styles.field}>
      <Text style={styles.fieldLabel} numberOfLines={1}>
        {label}
        {required ? <Text style={styles.required}>{" *"}</Text> : null}
      </Text>
      {onPress ? (
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={value ? `${label} ${value}` : label}
          accessibilityState={{ disabled: locked }}
          disabled={locked}
          onPress={onPress}
        >
          {box}
        </Pressable>
      ) : box}
      {error ? (
        <Text style={styles.errorText} accessibilityLiveRegion="polite">{error}</Text>
      ) : hint ? (
        <Text style={styles.hintText}>{hint}</Text>
      ) : null}
    </View>
  );
}

/** 等宽并排的一行字段（姓 / 名 / 中间名；昵称 / 电话）。 */
export function FieldRow({ children }: { children: ReactNode }) {
  const items = Array.isArray(children) ? children : [children];
  return (
    <View style={styles.fieldRow}>
      {items.map((child, index) => (
        <View key={index} style={styles.fieldRowItem}>{child}</View>
      ))}
    </View>
  );
}

/** 卡片里的「标题 + 说明 + 开关」行。 */
export function SwitchRow({
  title,
  description,
  value,
  onValueChange,
  disabled = false,
  first = false,
  testID,
}: {
  title: string;
  description?: string;
  value: boolean;
  onValueChange: (value: boolean) => void;
  disabled?: boolean;
  first?: boolean;
  testID?: string;
}) {
  return (
    <View style={[styles.switchRow, !first && styles.switchDivider]} testID={testID}>
      <View style={styles.switchText}>
        <Text style={styles.switchTitle}>{title}</Text>
        {description ? <Text style={styles.switchDescription}>{description}</Text> : null}
      </View>
      {/* RN 的 iOS Switch 默认自带 alignSelf: flex-start，会贴到行顶；显式居中才和左侧文字对齐。 */}
      <Switch value={value} onValueChange={onValueChange} disabled={disabled} accessibilityLabel={title} style={styles.switchControl} />
    </View>
  );
}

// ───────────────────────── 姓名区 ─────────────────────────

export interface PhoneFieldProps {
  value: string;
  onChange: (value: string) => void;
  error?: string;
  placeholder?: string;
}

/**
 * 姓名区：姓* / 名* / 中间名 并排一行；昵称（选填）与电话（选填）并排一行。
 * 不传 `phone` 时（管理员接口不支持电话）昵称独占一行。
 */
export function NameFields({
  values,
  onChange,
  errors,
  phone,
  disabled = false,
  legacyHint = false,
}: {
  values: PersonNameInput;
  onChange: (key: PersonNameField, value: string) => void;
  errors: PersonNameErrors;
  phone?: PhoneFieldProps;
  disabled?: boolean;
  /** 存量账号由全名自动拆分时给一行小提示。 */
  legacyHint?: boolean;
}) {
  const { t } = useAppTranslation("peopleForm");
  const message = (field: PersonNameField | "fullName", code: PersonNameErrorCode | undefined) => {
    if (!code) return undefined;
    if (code === "required") return t("name.errors.required", { field: t(`name.${field}`) });
    if (code === "tooLong") return t("name.errors.tooLong", { max: 50 });
    return t("name.errors.fullNameTooLong", { max: 100 });
  };

  const nickname = (
    <FormField
      label={t("name.nickname")}
      value={values.nickname}
      onChangeText={(value) => onChange("nickname", value)}
      placeholder={t("name.nicknamePlaceholder")}
      hint={t("name.nicknameHint")}
      error={message("nickname", errors.nickname)}
      autoCapitalize="words"
      disabled={disabled}
      testID="person-nickname"
    />
  );

  return (
    <View style={styles.nameBlock}>
      <FieldRow>
        <FormField
          label={t("name.familyName")}
          required
          value={values.familyName}
          onChangeText={(value) => onChange("familyName", value)}
          error={message("familyName", errors.familyName)}
          autoCapitalize="words"
          disabled={disabled}
          testID="person-family-name"
        />
        <FormField
          label={t("name.givenName")}
          required
          value={values.givenName}
          onChangeText={(value) => onChange("givenName", value)}
          error={message("givenName", errors.givenName)}
          autoCapitalize="words"
          disabled={disabled}
          testID="person-given-name"
        />
        <FormField
          label={t("name.middleName")}
          value={values.middleName}
          onChangeText={(value) => onChange("middleName", value)}
          placeholder={t("name.optional")}
          error={message("middleName", errors.middleName)}
          autoCapitalize="words"
          disabled={disabled}
          testID="person-middle-name"
        />
      </FieldRow>
      {errors.fullName ? (
        <Text style={styles.errorText} accessibilityLiveRegion="polite">{message("fullName", errors.fullName)}</Text>
      ) : null}
      {legacyHint ? <Text style={styles.hintText} testID="person-name-legacy-hint">{t("name.legacySplitHint")}</Text> : null}
      {phone ? (
        <FieldRow>
          {nickname}
          <FormField
            label={t("name.phone")}
            value={phone.value}
            onChangeText={phone.onChange}
            placeholder={phone.placeholder ?? "04xx xxx xxx"}
            error={phone.error}
            keyboardType="phone-pad"
            disabled={disabled}
            testID="person-phone"
          />
        </FieldRow>
      ) : nickname}
    </View>
  );
}

// ───────────────────────── 放弃修改 ─────────────────────────

/** 居中确认框：有未保存改动时返回 / 取消先问一句（居中对话框只用于确认类）。 */
export function DiscardChangesDialog({
  visible,
  onKeepEditing,
  onDiscard,
}: {
  visible: boolean;
  onKeepEditing: () => void;
  onDiscard: () => void;
}) {
  const { t } = useAppTranslation("peopleForm");
  return (
    <Portal>
      <Dialog visible={visible} onDismiss={onKeepEditing}>
        <Dialog.Title>{t("discard.title")}</Dialog.Title>
        <Dialog.Content>
          <Text>{t("discard.message")}</Text>
        </Dialog.Content>
        <Dialog.Actions>
          <Button onPress={onKeepEditing}>{t("discard.keepEditing")}</Button>
          <Button textColor={HB_COLORS.danger} onPress={onDiscard}>{t("discard.discard")}</Button>
        </Dialog.Actions>
      </Dialog>
    </Portal>
  );
}

/**
 * 离开拦截：有未保存改动（dirty）时，返回键 / 侧滑 / 关闭按钮 / 取消先弹「放弃修改？」。
 * - 所有离开方式都会触发 usePreventRemove，所以页面里的「取消」「×」直接 router.back() 即可；
 * - 保存成功等不需要再确认的离开，用 `leaveWith(action)`：先解除拦截，再在下一次渲染后执行 action。
 */
export function useLeaveGuard(dirty: boolean) {
  const navigation = useNavigation();
  const [leaving, setLeaving] = useState(false);
  const [confirmVisible, setConfirmVisible] = useState(false);
  const pending = useRef<(() => void) | null>(null);

  usePreventRemove(dirty && !leaving, ({ data }) => {
    pending.current = () => navigation.dispatch(data.action);
    setConfirmVisible(true);
  });

  useEffect(() => {
    if (!leaving) return;
    const run = pending.current;
    pending.current = null;
    run?.();
  }, [leaving]);

  const keepEditing = useCallback(() => {
    pending.current = null;
    setConfirmVisible(false);
  }, []);
  const discard = useCallback(() => {
    setConfirmVisible(false);
    setLeaving(true);
  }, []);
  const leaveWith = useCallback((action: () => void) => {
    pending.current = action;
    setConfirmVisible(false);
    setLeaving(true);
  }, []);

  return { confirmVisible, keepEditing, discard, leaveWith };
}

// ───────────────────────── 滚到第一个错误 ─────────────────────────

/**
 * 校验失败时滚到第一个出错的字段。用法：把 `scrollRef` 传给 FormScreen，
 * 用 `<View onLayout={anchor("phone")}>` 包住 ScrollView 的直接子项（位置才是相对滚动内容的），
 * 再按页面从上到下的顺序把出错的锚点传给 `scrollToFirst`。
 * 关键逻辑：顶部错误条和字段红字出现后布局会下移，所以等 onLayout 回报新位置后再滚动。
 */
export function useFieldAnchors<K extends string>() {
  const scrollRef = useRef<ScrollView>(null);
  const positions = useRef(new Map<K, number>());
  const [request, setRequest] = useState<{ tick: number; keys: K[] } | null>(null);

  useEffect(() => {
    if (!request) return;
    const timer = setTimeout(() => {
      const first = request.keys.find((key) => positions.current.has(key));
      const y = first === undefined ? 0 : positions.current.get(first) ?? 0;
      scrollRef.current?.scrollTo({ y: Math.max(y - HB_SPACING.sm, 0), animated: true });
    }, 80);
    return () => clearTimeout(timer);
  }, [request]);

  const anchor = useCallback((key: K) => (event: LayoutChangeEvent) => {
    positions.current.set(key, event.nativeEvent.layout.y);
  }, []);
  const scrollToFirst = useCallback((keys: K[]) => {
    setRequest((current) => ({ tick: (current?.tick ?? 0) + 1, keys }));
  }, []);

  return { scrollRef, anchor, scrollToFirst };
}

// ───────────────────────── 小部件 ─────────────────────────

/** 输入框右侧的显示 / 隐藏密码按钮。 */
export function VisibilityToggle({ visible, onToggle, showLabel, hideLabel }: { visible: boolean; onToggle: () => void; showLabel: string; hideLabel: string }) {
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={visible ? hideLabel : showLabel} onPress={onToggle} hitSlop={8} style={styles.visibilityToggle}>
      <IconButton icon={visible ? "eye-off-outline" : "eye-outline"} size={20} style={styles.visibilityIcon} />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  screen: { backgroundColor: HB_COLORS.background, flex: 1 },
  flex: { flex: 1 },
  header: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    minHeight: 56,
    paddingRight: HB_SPACING.md,
  },
  headerText: { flex: 1, minWidth: 0 },
  headerTitle: { color: HB_COLORS.textPrimary, fontSize: 18, fontWeight: "800" },
  headerSubtitle: { color: HB_COLORS.textSecondary, fontSize: 12.5, marginTop: 1 },
  content: { gap: HB_SPACING.sm, padding: HB_SPACING.md, paddingBottom: HB_SPACING.xl },
  footerButton: { flex: 1 },
  footerButtonPrimary: { flex: 1.4 },
  footerButtonContent: { minHeight: 48 },
  field: { gap: 4 },
  fieldLabel: { color: HB_COLORS.textSecondary, fontSize: 12.5, fontWeight: "700" },
  required: { color: HB_COLORS.danger },
  inputBox: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outline,
    borderRadius: 10,
    borderWidth: 1,
    flexDirection: "row",
    gap: 6,
    minHeight: 44,
    paddingHorizontal: 12,
  },
  inputBoxLocked: { backgroundColor: HB_COLORS.surfaceMuted, borderColor: HB_COLORS.outlineMuted },
  inputBoxError: { borderColor: HB_COLORS.danger },
  input: { color: HB_COLORS.textPrimary, flex: 1, fontSize: 15, minWidth: 0, paddingVertical: 8, textAlignVertical: "center" },
  inputLocked: { color: HB_COLORS.textSecondary },
  mono: { fontFamily: Platform.select({ ios: "Menlo", default: "monospace" }) },
  errorText: { color: HB_COLORS.danger, fontSize: 12, lineHeight: 16 },
  hintText: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 16 },
  fieldRow: { alignItems: "flex-start", flexDirection: "row", gap: 8 },
  fieldRowItem: { flex: 1, minWidth: 0 },
  nameBlock: { gap: HB_SPACING.xs },
  switchRow: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.sm, minHeight: 56, paddingHorizontal: 14, paddingVertical: 10 },
  switchDivider: { borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth },
  switchText: { flex: 1, minWidth: 0 },
  switchControl: { alignSelf: "center" },
  adornment: { alignSelf: "center" },
  switchTitle: { color: HB_COLORS.textPrimary, fontSize: 14.5, fontWeight: "700" },
  switchDescription: { color: HB_COLORS.textSecondary, fontSize: 12.5, lineHeight: 17, marginTop: 1 },
  visibilityToggle: { borderRadius: HB_RADIUS.control },
  visibilityIcon: { margin: 0 },
});

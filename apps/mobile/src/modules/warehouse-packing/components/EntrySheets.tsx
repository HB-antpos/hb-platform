import { useEffect, useState } from "react";
import { StyleSheet, TextInput, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { parseQuantityInput } from "../packing-logic";
import { MONO_FONT, PACK_COLORS, packStyles } from "./pack-ui";

/**
 * 输入件数：「扫后输数量」时是本次要加的件数（不能为 0），改已装数量时是该商品在箱里的总数（0 = 移出）。
 */
export function QuantitySheet({
  visible,
  title,
  productName,
  hint,
  initial,
  allowZero,
  saving,
  onDismiss,
  onConfirm,
}: {
  visible: boolean;
  title: string;
  productName?: string | null;
  hint?: string | null;
  initial: number;
  allowZero: boolean;
  saving: boolean;
  onDismiss: () => void;
  onConfirm: (quantity: number) => void;
}) {
  const { t } = useAppTranslation("warehousePacking");
  const [text, setText] = useState(String(initial));
  useEffect(() => {
    if (visible) setText(String(initial));
  }, [initial, visible]);
  const value = parseQuantityInput(text, allowZero);
  const submit = () => {
    if (value != null && !saving) onConfirm(value);
  };

  return (
    <BusinessSheet
      visible={visible}
      title={title}
      subtitle={productName ?? undefined}
      onDismiss={onDismiss}
      dismissable={!saving}
      footer={
        <Button
          mode="contained"
          loading={saving}
          disabled={value == null || saving}
          onPress={submit}
          style={packStyles.button}
          contentStyle={packStyles.buttonContent}
        >
          {value === 0 ? t("quantity.remove") : t("quantity.confirm", { count: value ?? 0 })}
        </Button>
      }
    >
      <View style={packStyles.sheetBody}>
        {/* 弹层是独立窗口，扫码枪 / 数字键盘都落在这个可见输入框里。 */}
        <TextInput
          accessibilityLabel={t("quantity.label")}
          value={text}
          onChangeText={(next) => setText(next.replace(/\D/g, "").slice(0, 6))}
          onSubmitEditing={submit}
          keyboardType="number-pad"
          autoFocus
          selectTextOnFocus
          style={styles.input}
        />
        {hint ? <Text style={packStyles.muted}>{hint}</Text> : null}
        {value == null && text ? <Text style={packStyles.errorText}>{t("quantity.invalid")}</Text> : null}
      </View>
    </BusinessSheet>
  );
}

/** 输入 / 扫一个码（货位码、托盘号）：扫码枪按键盘输入落在输入框里，回车即提交。 */
export function CodeEntrySheet({
  visible,
  title,
  subtitle,
  placeholder,
  confirmLabel,
  saving,
  errorMessage,
  onDismiss,
  onSubmit,
  children,
}: {
  visible: boolean;
  title: string;
  subtitle?: string;
  placeholder: string;
  confirmLabel: string;
  saving: boolean;
  errorMessage?: string | null;
  onDismiss: () => void;
  onSubmit: (code: string) => void;
  children?: React.ReactNode;
}) {
  const [text, setText] = useState("");
  useEffect(() => {
    if (visible) setText("");
  }, [visible]);
  const submit = () => {
    const code = text.trim();
    if (code && !saving) onSubmit(code);
  };
  return (
    <BusinessSheet
      visible={visible}
      title={title}
      subtitle={subtitle}
      onDismiss={onDismiss}
      dismissable={!saving}
      footer={
        <Button
          mode="contained"
          loading={saving}
          disabled={!text.trim() || saving}
          onPress={submit}
          style={packStyles.button}
          contentStyle={packStyles.buttonContent}
        >
          {confirmLabel}
        </Button>
      }
    >
      <View style={packStyles.sheetBody}>
        <TextInput
          accessibilityLabel={placeholder}
          placeholder={placeholder}
          placeholderTextColor={PACK_COLORS.inkMuted}
          value={text}
          onChangeText={setText}
          onSubmitEditing={submit}
          autoCapitalize="characters"
          autoCorrect={false}
          autoFocus
          returnKeyType="done"
          style={packStyles.textInput}
        />
        {errorMessage ? <Text style={packStyles.errorText}>{errorMessage}</Text> : null}
        {children}
      </View>
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  input: {
    height: 60,
    borderRadius: 8,
    borderWidth: 2,
    borderColor: PACK_COLORS.action,
    textAlign: "center",
    fontFamily: MONO_FONT,
    fontSize: 28,
    fontWeight: "700",
    color: PACK_COLORS.ink,
    backgroundColor: PACK_COLORS.white,
  },
});

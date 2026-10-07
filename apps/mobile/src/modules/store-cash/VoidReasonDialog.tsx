// 作废确认：必须填写原因（至少两个字）后才能确认，防止误点。
// 用 Paper Dialog（Portal）承载，只能在页面层使用——不要放进 BusinessSheet 这类原生 Modal 里。
import { useEffect, useState } from "react";
import { StyleSheet } from "react-native";
import { Button, Dialog, HelperText, Portal, Text, TextInput } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { validateVoidReason } from "./void-form";

export function VoidReasonDialog({
  visible,
  title,
  description,
  busy,
  errorMessage,
  onCancel,
  onConfirm,
}: {
  visible: boolean;
  title: string;
  description: string;
  busy: boolean;
  /** 上一次作废失败的提示，显示在输入框下方，保留已填原因便于重试。 */
  errorMessage?: string;
  onCancel: () => void;
  onConfirm: (reason: string) => void;
}) {
  const { t } = useAppTranslation(["storeCash", "common"]);
  const [reason, setReason] = useState("");
  const [touched, setTouched] = useState(false);

  useEffect(() => {
    if (visible) {
      setReason("");
      setTouched(false);
    }
  }, [visible]);

  const issue = validateVoidReason(reason);
  const showIssue = touched && issue != null;

  return (
    <Portal>
      <Dialog visible={visible} onDismiss={busy ? undefined : onCancel} dismissable={!busy}>
        <Dialog.Title>{title}</Dialog.Title>
        <Dialog.Content>
          <Text style={styles.description}>{description}</Text>
          <TextInput
            mode="outlined"
            label={t("void.reasonLabel")}
            placeholder={t("void.reasonPlaceholder")}
            value={reason}
            onChangeText={(value) => {
              setReason(value);
              setTouched(true);
            }}
            multiline
            numberOfLines={2}
            maxLength={500}
            error={showIssue}
            disabled={busy}
            style={styles.input}
          />
          <HelperText type="error" visible={showIssue}>
            {issue === "tooLong" ? t("void.reasonTooLong") : t("void.reasonTooShort")}
          </HelperText>
          {errorMessage ? (
            <HelperText type="error" visible>
              {errorMessage}
            </HelperText>
          ) : null}
        </Dialog.Content>
        <Dialog.Actions>
          <Button onPress={onCancel} disabled={busy}>{t("common:actions.cancel")}</Button>
          <Button
            mode="contained"
            buttonColor={HB_COLORS.danger}
            onPress={() => {
              setTouched(true);
              if (!issue) onConfirm(reason.trim());
            }}
            loading={busy}
            disabled={busy || issue != null}
          >
            {t("void.confirm")}
          </Button>
        </Dialog.Actions>
      </Dialog>
    </Portal>
  );
}

const styles = StyleSheet.create({
  description: { color: HB_COLORS.textSecondary, marginBottom: 8 },
  input: { backgroundColor: HB_COLORS.white },
});

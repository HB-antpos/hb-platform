// 条目操作的二次确认：批准（确认）、撤回（必填原因）、申诉备注（可清空）。
// 用 Paper Dialog（Portal）承载，只能在页面层使用——不要放进 BusinessSheet 这类原生 Modal 里。
import { useEffect, useState } from "react";
import { StyleSheet } from "react-native";
import { Button, Dialog, HelperText, Portal, Text, TextInput } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { DISPUTE_NOTE_MAX_LENGTH, REASON_MAX_LENGTH } from "./constants";
import { needsMultipleApprovals } from "./entries-logic";
import { validateDisputeNote, validateWithdrawReason } from "./entry-form";
import { useIssueText } from "./shared";
import type { FaceUnwelcomeEntry } from "./types";

export type EntryAction = { kind: "approve" | "withdraw" | "dispute"; entry: FaceUnwelcomeEntry };

interface Props {
  action: EntryAction | null;
  busy: boolean;
  /** 上一次提交失败的提示（保留已填内容便于重试）。 */
  errorMessage?: string;
  onCancel: () => void;
  onApprove: (entry: FaceUnwelcomeEntry) => void;
  onWithdraw: (entry: FaceUnwelcomeEntry, reason: string) => void;
  onDispute: (entry: FaceUnwelcomeEntry, note: string) => void;
}

export function EntryActionDialogs({ action, busy, errorMessage, onCancel, onApprove, onWithdraw, onDispute }: Props) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const issueText = useIssueText();
  const [text, setText] = useState("");
  const [touched, setTouched] = useState(false);

  // 每次打开新的操作都从空白（或已有申诉备注）重新开始。
  useEffect(() => {
    setTouched(false);
    setText(action?.kind === "dispute" ? (action.entry.disputeNote ?? "") : "");
  }, [action]);

  if (!action) return null;
  const { entry } = action;

  if (action.kind === "approve") {
    return (
      <Portal>
        <Dialog visible onDismiss={busy ? undefined : onCancel} dismissable={!busy}>
          <Dialog.Title>{t("unwelcome.approve.title")}</Dialog.Title>
          <Dialog.Content>
            <Text style={styles.description}>
              {needsMultipleApprovals(entry)
                ? t("unwelcome.approve.contentTwo", { done: entry.approvalsDone, needed: entry.approvalsNeeded })
                : t("unwelcome.approve.content")}
            </Text>
            {errorMessage ? <HelperText type="error" visible>{errorMessage}</HelperText> : null}
          </Dialog.Content>
          <Dialog.Actions>
            <Button onPress={onCancel} disabled={busy}>{t("common:actions.cancel")}</Button>
            <Button mode="contained" onPress={() => onApprove(entry)} loading={busy} disabled={busy}>
              {t("unwelcome.approve.ok")}
            </Button>
          </Dialog.Actions>
        </Dialog>
      </Portal>
    );
  }

  const isWithdraw = action.kind === "withdraw";
  const issue = isWithdraw ? validateWithdrawReason(text) : validateDisputeNote(text);
  const showIssue = touched && issue != null;
  const maxLength = isWithdraw ? REASON_MAX_LENGTH : DISPUTE_NOTE_MAX_LENGTH;
  return (
    <Portal>
      <Dialog visible onDismiss={busy ? undefined : onCancel} dismissable={!busy}>
        <Dialog.Title>{isWithdraw ? t("unwelcome.withdraw.title") : t("unwelcome.dispute.title")}</Dialog.Title>
        <Dialog.Content>
          <Text style={styles.description}>
            {isWithdraw ? t("unwelcome.withdraw.warning") : t("unwelcome.dispute.description")}
          </Text>
          <TextInput
            mode="outlined"
            label={isWithdraw ? t("unwelcome.withdraw.reasonLabel") : t("unwelcome.dispute.label")}
            value={text}
            onChangeText={(value) => {
              setText(value);
              setTouched(true);
            }}
            multiline
            numberOfLines={3}
            maxLength={maxLength}
            error={showIssue}
            disabled={busy}
            style={styles.input}
          />
          <HelperText type={showIssue ? "error" : "info"} visible>
            {showIssue ? issueText(issue) : `${text.length} / ${maxLength}`}
          </HelperText>
          {errorMessage ? <HelperText type="error" visible>{errorMessage}</HelperText> : null}
        </Dialog.Content>
        <Dialog.Actions>
          <Button onPress={onCancel} disabled={busy}>{t("common:actions.cancel")}</Button>
          <Button
            mode="contained"
            buttonColor={isWithdraw ? HB_COLORS.danger : undefined}
            loading={busy}
            disabled={busy || issue != null}
            onPress={() => {
              setTouched(true);
              if (issue) return;
              if (isWithdraw) onWithdraw(entry, text.trim());
              else onDispute(entry, text.trim());
            }}
          >
            {isWithdraw ? t("unwelcome.withdraw.ok") : t("unwelcome.dispute.ok")}
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

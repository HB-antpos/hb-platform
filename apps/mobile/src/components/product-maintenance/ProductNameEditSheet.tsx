import { useEffect, useMemo, useState } from "react";
import { StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import {
  PRODUCT_NAME_MAX_LENGTH,
  evaluateProductNameDraft,
  formatUtcInstantAsLocal,
} from "@/modules/product-maintenance/product-name-edit";
import type { ProductNameHistoryItem } from "@/modules/product-maintenance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

interface ProductNameEditSheetProps {
  visible: boolean;
  /** 当前主档中文名称；作为草稿初值，也用来判断「没有变化」。 */
  currentName: string;
  saving: boolean;
  /** 修改记录；加载中或失败时为空数组，由 historyLoading / historyFailed 区分。 */
  history: ProductNameHistoryItem[];
  historyLoading: boolean;
  historyFailed: boolean;
  /** 提交的是去掉首尾空白后的新名称。 */
  onSubmit: (name: string) => void;
  onDismiss: () => void;
}

/** 修改商品主档中文名称的面板：输入框 + 全局生效提示 + 该商品的名称修改记录。 */
export function ProductNameEditSheet({
  visible,
  currentName,
  saving,
  history,
  historyLoading,
  historyFailed,
  onSubmit,
  onDismiss,
}: ProductNameEditSheetProps) {
  const { t } = useAppTranslation(["productQuery", "common"]);
  const [draft, setDraft] = useState(currentName);

  // 每次打开都从当前名称重新开始，避免上一次取消的草稿残留。
  useEffect(() => {
    if (visible) {
      setDraft(currentName);
    }
  }, [visible, currentName]);

  const state = useMemo(() => evaluateProductNameDraft(draft, currentName), [draft, currentName]);
  const draftLength = draft.trim().length;
  const overLimit = draftLength > PRODUCT_NAME_MAX_LENGTH;

  const submit = () => {
    if (state.canSave && !saving) {
      onSubmit(state.normalized);
    }
  };

  return (
    <BusinessSheet
      visible={visible}
      title={t("nameEdit.title")}
      onDismiss={onDismiss}
      dismissable={!saving}
      footer={(
        <View style={styles.footer}>
          <Button
            mode="outlined"
            onPress={onDismiss}
            disabled={saving}
            style={styles.footerButton}
            contentStyle={styles.buttonContent}
          >
            {t("common:actions.cancel")}
          </Button>
          <Button
            mode="contained"
            onPress={submit}
            disabled={!state.canSave}
            loading={saving}
            style={styles.footerButton}
            contentStyle={styles.buttonContent}
          >
            {t("nameEdit.save")}
          </Button>
        </View>
      )}
    >
      <View style={styles.field}>
        <Text style={styles.label}>{t("nameEdit.currentLabel")}</Text>
        <Text selectable style={styles.currentName}>
          {currentName || t("nameEdit.emptyName")}
        </Text>
      </View>

      <View style={styles.field}>
        <Text style={styles.label}>{t("nameEdit.inputLabel")}</Text>
        <TextInput
          style={[styles.input, overLimit ? styles.inputError : null]}
          value={draft}
          // 名称不允许换行：粘贴进来的换行直接换成空格，Enter 键当作「提交」。
          onChangeText={(text) => setDraft(text.replace(/[\r\n]+/g, " "))}
          placeholder={t("nameEdit.placeholder")}
          placeholderTextColor="#98A2B3"
          autoFocus
          multiline
          submitBehavior="blurAndSubmit"
          returnKeyType="done"
          onSubmitEditing={submit}
          editable={!saving}
          selectTextOnFocus
          accessibilityLabel={t("nameEdit.inputLabel")}
        />
        <Text style={[styles.counter, overLimit ? styles.counterError : null]}>
          {t("nameEdit.counter", { count: draftLength, max: PRODUCT_NAME_MAX_LENGTH })}
        </Text>
      </View>

      <Text style={styles.hint}>{t("nameEdit.globalWarning")}</Text>

      <View style={styles.historyBlock}>
        <Text style={styles.historyTitle}>{t("nameEdit.history")}</Text>
        {historyLoading ? (
          <View style={styles.historyState}>
            <ActivityIndicator size="small" />
            <Text style={styles.historyStateText}>{t("nameEdit.historyLoading")}</Text>
          </View>
        ) : historyFailed ? (
          <Text style={styles.historyStateText}>{t("nameEdit.historyFailed")}</Text>
        ) : history.length === 0 ? (
          <Text style={styles.historyStateText}>{t("nameEdit.historyEmpty")}</Text>
        ) : (
          history.map((item, index) => (
            <View key={`${item.occurredAtUtc}-${index}`} style={styles.historyRow}>
              <Text style={styles.historyChange} selectable>
                {t("nameEdit.historyRow", {
                  before: item.beforeName || t("nameEdit.emptyName"),
                  after: item.afterName || t("nameEdit.emptyName"),
                })}
              </Text>
              <Text style={styles.historyMeta}>
                {t("nameEdit.historyMeta", {
                  actor: item.actorName || t("nameEdit.unknownActor"),
                  time: formatUtcInstantAsLocal(item.occurredAtUtc),
                })}
              </Text>
            </View>
          ))
        )}
      </View>
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  field: { gap: 6 },
  label: { fontSize: 12, fontWeight: "600", color: HB_COLORS.textSecondary },
  currentName: { fontSize: 15, color: HB_COLORS.textPrimary },
  input: {
    minHeight: 48,
    maxHeight: 120,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
    fontSize: 16,
    color: HB_COLORS.textPrimary,
    backgroundColor: HB_COLORS.white,
    textAlignVertical: "top",
  },
  inputError: { borderColor: HB_COLORS.danger },
  counter: { fontSize: 12, color: HB_COLORS.textSecondary, textAlign: "right", fontVariant: ["tabular-nums"] },
  counterError: { color: HB_COLORS.danger, fontWeight: "700" },
  hint: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  historyBlock: {
    gap: 6,
    paddingTop: HB_SPACING.xs,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  historyTitle: { fontSize: 13, fontWeight: "700", color: HB_COLORS.textPrimary },
  historyState: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  historyStateText: { fontSize: 12, color: HB_COLORS.textSecondary },
  historyRow: { gap: 2, paddingVertical: 4 },
  historyChange: { fontSize: 13, color: HB_COLORS.textPrimary },
  historyMeta: { fontSize: 11, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  footer: { flexDirection: "row", gap: HB_SPACING.xs },
  footerButton: { flex: 1, borderRadius: HB_RADIUS.control },
  buttonContent: { minHeight: 44 },
});

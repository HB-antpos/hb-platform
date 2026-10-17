import { useEffect, useState } from "react";
import { StyleSheet, View } from "react-native";
import { Button, Dialog, Portal, Text, TextInput } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { CANCEL_NOTE_MAX_LENGTH, distinctProductCodes } from "./cancel";
import type { StorePriceUpdateTask } from "./types";

export interface CancelTasksDialogProps {
  /** 待撤销的任务；为空数组时对话框关闭。撤销按商品覆盖所有分店，任务只用来确定是哪些商品。 */
  targets: StorePriceUpdateTask[];
  /** 单个商品时：目前有未完成任务的分店数；还没查到 / 查不到为 null，退回不带数量的说明。 */
  storeCount: number | null;
  busy: boolean;
  onConfirm: (note: string) => void;
  onDismiss: () => void;
}

/**
 * 撤销未完成任务的确认框，对齐 Web：写明「只取消提醒、不改价格、不可恢复」，并收集可选备注。
 * 撤销没有「恢复」，所以必须明确二次确认，确认键用警示色。
 */
export function CancelTasksDialog({ targets, storeCount, busy, onConfirm, onDismiss }: CancelTasksDialogProps) {
  const { t } = useAppTranslation("priceUpdates");
  const [note, setNote] = useState("");
  const visible = targets.length > 0;
  const productCount = distinctProductCodes(targets).length;
  const productName = targets[0]?.productName || targets[0]?.productCode || "";

  // 每次重新打开都从空备注开始，避免带上上一次的内容。
  useEffect(() => {
    if (visible) setNote("");
  }, [visible]);

  return (
    <Portal>
      <Dialog visible={visible} onDismiss={busy ? undefined : onDismiss} dismissable={!busy}>
        <Dialog.Title>{t("cancelDialog.title")}</Dialog.Title>
        <Dialog.Content style={styles.content}>
          <Text variant="bodyMedium">
            {productCount > 1
              ? t("cancelDialog.batchDescription", { count: productCount })
              : storeCount != null
                ? t("cancelDialog.descriptionWithCount", { name: productName, count: storeCount })
                : t("cancelDialog.description", { name: productName })}
          </Text>
          {/* 撤销范围是所有分店，不只当前这家：和 Web「按商品」撤销一致，必须在确认前说清楚。 */}
          <Text variant="bodySmall" style={styles.scopeNote}>{t("cancelDialog.scopeNote")}</Text>
          <View style={styles.warning} accessibilityRole="alert">
            <MaterialCommunityIcons name="alert-outline" size={20} color={HB_COLORS.warning} />
            <Text variant="bodySmall" style={styles.warningText}>{t("cancelDialog.warning")}</Text>
          </View>
          <TextInput
            mode="outlined"
            dense
            multiline
            numberOfLines={2}
            maxLength={CANCEL_NOTE_MAX_LENGTH}
            label={t("cancelDialog.noteLabel")}
            placeholder={t("cancelDialog.notePlaceholder")}
            value={note}
            onChangeText={setNote}
            disabled={busy}
          />
        </Dialog.Content>
        <Dialog.Actions>
          <Button disabled={busy} onPress={onDismiss}>{t("common:actions.cancel")}</Button>
          <Button
            textColor={HB_COLORS.danger}
            loading={busy}
            disabled={busy}
            onPress={() => onConfirm(note)}
          >
            {t("cancelDialog.confirm")}
          </Button>
        </Dialog.Actions>
      </Dialog>
    </Portal>
  );
}

const styles = StyleSheet.create({
  content: { gap: HB_SPACING.sm },
  scopeNote: { color: HB_COLORS.danger, fontWeight: "700" },
  warning: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    borderRadius: 8,
    backgroundColor: "#FFF4E5",
  },
  warningText: { flex: 1, color: "#7A4B00" },
});

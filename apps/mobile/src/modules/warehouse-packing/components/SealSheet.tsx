import { useEffect, useState } from "react";
import { TextInput, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { clampCopies, formatWeight, parseWeightInput, type LabelPreviewContent } from "../packing-logic";
import { LabelPreview } from "./LabelPreview";
import { KeyValueRow, PACK_COLORS, Stepper, packStyles } from "./pack-ui";

/**
 * 封箱 / 封托确认：标签预览、去向、重量、打印份数；可以只封不打印（打印机没连时之后补打）。
 * 箱和托盘共用，文案由 kind 区分。
 */
export function SealSheet({
  visible,
  kind,
  preview,
  destinationText,
  initialWeightKg,
  defaultCopies,
  smallLabel,
  onToggleSmallLabel,
  saving,
  errorMessage,
  onDismiss,
  onChangeDestination,
  onCopiesChange,
  onConfirm,
}: {
  visible: boolean;
  kind: "carton" | "pallet";
  /** 按当前份数生成预览内容（托盘的 1/N 随份数变化）。 */
  preview: (copies: number) => LabelPreviewContent;
  destinationText: string;
  initialWeightKg: number | null;
  defaultCopies: number;
  smallLabel: boolean;
  onToggleSmallLabel: () => void;
  saving: boolean;
  /** 封箱 / 封托失败的原因：弹层盖住了页面的提示条，必须显示在弹层里。 */
  errorMessage?: string | null;
  onDismiss: () => void;
  onChangeDestination: () => void;
  onCopiesChange?: (copies: number) => void;
  onConfirm: (input: { weightKg: number | null; copies: number; print: boolean }) => void;
}) {
  const { t } = useAppTranslation("warehousePacking");
  const [weightText, setWeightText] = useState("");
  const [copies, setCopies] = useState(defaultCopies);

  useEffect(() => {
    if (!visible) return;
    setWeightText(formatWeight(initialWeightKg));
    setCopies(defaultCopies);
    // 只在打开时同步初值。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [visible]);

  const weight = parseWeightInput(weightText);
  const weightInvalid = weight === "invalid";
  const changeCopies = (next: number) => {
    const clamped = clampCopies(next);
    setCopies(clamped);
    onCopiesChange?.(clamped);
  };
  const submit = (print: boolean) => {
    if (weight === "invalid") return;
    onConfirm({ weightKg: weight, copies, print });
  };

  return (
    <BusinessSheet
      visible={visible}
      title={t(`seal.${kind}.title`)}
      subtitle={t(`seal.${kind}.subtitle`)}
      onDismiss={onDismiss}
      dismissable={!saving}
      footer={
        <View style={packStyles.buttons}>
          <Button
            mode="outlined"
            disabled={saving || weightInvalid}
            onPress={() => submit(false)}
            style={[packStyles.button, packStyles.flexSmall]}
            contentStyle={packStyles.buttonContent}
          >
            {t(`seal.${kind}.sealOnly`)}
          </Button>
          <Button
            mode="contained"
            icon="printer"
            loading={saving}
            disabled={saving || weightInvalid}
            onPress={() => submit(true)}
            style={[packStyles.button, packStyles.flex]}
            contentStyle={packStyles.buttonContent}
          >
            {t(`seal.${kind}.sealAndPrint`)}
          </Button>
        </View>
      }
    >
      <View style={packStyles.sheetBody}>
        <LabelPreview content={preview(copies)} accessibilityLabel={t("seal.previewLabel")} />
        <View style={packStyles.cardList}>
          <KeyValueRow first label={t("fields.destination")} value={destinationText} onPress={saving ? undefined : onChangeDestination} />
          <KeyValueRow label={t("seal.weight")}>
            <TextInput
              accessibilityLabel={t("seal.weight")}
              placeholder="kg"
              placeholderTextColor={PACK_COLORS.inkMuted}
              value={weightText}
              onChangeText={(next) => setWeightText(next.replace(/[^\d.,]/g, "").slice(0, 9))}
              keyboardType="decimal-pad"
              style={{ minWidth: 96, textAlign: "right", fontSize: 15, fontWeight: "600", color: PACK_COLORS.ink, paddingVertical: 0 }}
            />
          </KeyValueRow>
          <KeyValueRow label={t("seal.copies")}>
            <Stepper
              compact
              value={copies}
              onDecrease={() => changeCopies(copies - 1)}
              onIncrease={() => changeCopies(copies + 1)}
              decreaseLabel={t("seal.copiesDecrease")}
              increaseLabel={t("seal.copiesIncrease")}
              valueLabel={t("seal.copiesValue", { count: copies })}
            />
          </KeyValueRow>
          <KeyValueRow
            label={t("seal.smallLabel")}
            value={smallLabel ? t("seal.smallLabelOn") : t("seal.smallLabelOff")}
            onPress={onToggleSmallLabel}
          />
        </View>
        {weightInvalid ? <Text style={packStyles.errorText}>{t("seal.weightInvalid")}</Text> : null}
        {errorMessage ? <Text style={packStyles.errorText}>{errorMessage}</Text> : null}
      </View>
    </BusinessSheet>
  );
}

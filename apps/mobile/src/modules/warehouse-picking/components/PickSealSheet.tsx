import { useEffect, useMemo, useState } from "react";
import { TextInput, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { LabelPreview } from "../../packing/components/LabelPreview";
import { KeyValueRow, PACK_COLORS, Segmented, packStyles } from "../../packing/components/pack-ui";
import { usePackPreferences } from "../../packing/pack-preferences";
import { buildCartonLabelPayload, cartonLabelPreview, parseWeightInput } from "../../packing/packing-logic";
import { normalizeCartonRef, previewCartonOf } from "../pack-carton-logic";
import { usePickerStore } from "../picker-store";
import type { OrderCartonController } from "../use-order-carton";

type NextChoice = "new" | "none" | "scan";

/**
 * 封箱，换下一只：标签预览、可选重量、下一只箱怎么开（新建一只 / 先不开箱），
 * 「只封箱」或「封箱并打印」。拣货页与先拣后装页共用。
 * 这一版不在这里选托盘：箱封好后在「扫码打包」里组托盘。
 */
export function PickSealSheet({
  pack,
  onSealed,
  defaultNext = "new",
}: {
  pack: OrderCartonController;
  onSealed?: () => void;
  /** 封箱后默认怎么开下一只：拣货 / 装箱页默认新建一只；核对页只是补封，默认不开。 */
  defaultNext?: NextChoice;
}) {
  const { t } = useAppTranslation("warehousePicking");
  const operatorName = usePickerStore((state) => state.picker?.name ?? null);
  const smallLabel = usePackPreferences((state) => state.smallLabel);
  const toggleSmallLabel = usePackPreferences((state) => state.toggleSmallLabel);
  const [weightText, setWeightText] = useState("");
  const [next, setNext] = useState<NextChoice>("new");
  // 「扫空箱标」：扫到（或手输）的箱号；封箱成功后关联到本订单并设为当前箱。
  const [blankText, setBlankText] = useState("");
  const [error, setError] = useState("");
  // 标签预览的时间用打开弹层那一刻的本机时间，不随每次重绘跳动。
  const [previewAt, setPreviewAt] = useState(() => new Date());
  const { carton, sealVisible, sealing, progress } = pack;

  useEffect(() => {
    if (!sealVisible) return;
    setWeightText("");
    setNext(defaultNext);
    setBlankText("");
    setError("");
    setPreviewAt(new Date());
    // 只在打开时同步初值。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sealVisible]);

  const preview = useMemo(
    () => (carton ? cartonLabelPreview(buildCartonLabelPayload(previewCartonOf(carton, progress, operatorName), previewAt)) : null),
    [carton, operatorName, previewAt, progress],
  );

  if (!carton || !preview) return null;
  const weight = parseWeightInput(weightText);
  const weightInvalid = weight === "invalid";
  const empty = carton.pieceCount === 0;
  // 选了「扫空箱标」但还没扫：不能封箱（封箱不可撤销，要先确定下一只箱）。
  const blankRef = normalizeCartonRef(blankText);
  const blankMissing = next === "scan" && !blankRef;

  const submit = async (print: boolean) => {
    if (weight === "invalid" || sealing || empty || blankMissing) return;
    setError("");
    const outcome = await pack.sealCurrent({
      weightKg: weight,
      print,
      createNext: next === "new",
      attachBlankRef: next === "scan" ? blankRef : null,
    });
    if (outcome.ok) {
      pack.setSealVisible(false);
      onSealed?.();
    } else {
      setError(outcome.message);
    }
  };

  return (
    <BusinessSheet
      visible={sealVisible}
      title={t("carton.sealTitle")}
      subtitle={t("carton.sealSubtitle", { sku: carton.skuCount, pieces: carton.pieceCount })}
      onDismiss={() => pack.setSealVisible(false)}
      dismissable={!sealing}
      footer={
        <View style={packStyles.buttons}>
          <Button
            mode="outlined"
            disabled={sealing || weightInvalid || empty || blankMissing}
            onPress={() => void submit(false)}
            style={[packStyles.button, packStyles.flexSmall]}
            contentStyle={packStyles.buttonContent}
          >
            {t("carton.sealOnly")}
          </Button>
          <Button
            mode="contained"
            icon="printer"
            loading={sealing}
            disabled={sealing || weightInvalid || empty || blankMissing}
            onPress={() => void submit(true)}
            style={[packStyles.button, packStyles.flex]}
            contentStyle={packStyles.buttonContent}
          >
            {t("carton.sealAndPrint")}
          </Button>
        </View>
      }
    >
      <View style={packStyles.sheetBody}>
        <LabelPreview content={preview} accessibilityLabel={t("carton.previewLabel")} />
        <View style={packStyles.cardList}>
          <KeyValueRow first label={t("carton.weight")}>
            <TextInput
              accessibilityLabel={t("carton.weight")}
              placeholder="kg"
              placeholderTextColor={PACK_COLORS.inkMuted}
              value={weightText}
              onChangeText={(value) => setWeightText(value.replace(/[^\d.,]/g, "").slice(0, 9))}
              keyboardType="decimal-pad"
              style={{ minWidth: 96, textAlign: "right", fontSize: 15, fontWeight: "600", color: PACK_COLORS.ink, paddingVertical: 0 }}
            />
          </KeyValueRow>
          <KeyValueRow
            label={t("carton.smallLabel")}
            value={smallLabel ? t("carton.smallLabelOn") : t("carton.smallLabelOff")}
            onPress={toggleSmallLabel}
          />
        </View>
        <View style={{ gap: 6 }}>
          <Text style={packStyles.muted}>{t("carton.nextTitle")}</Text>
          <Segmented<NextChoice>
            label={t("carton.nextTitle")}
            value={next}
            onChange={setNext}
            options={[
              { key: "new", label: t("carton.nextNew") },
              { key: "scan", label: t("carton.nextScan") },
              { key: "none", label: t("carton.nextNone") },
            ]}
          />
          {next === "none" ? <Text style={packStyles.muted}>{t("carton.nextNoneHint")}</Text> : null}
          {next === "scan" ? (
            <>
              {/* 弹层是独立窗口：扫码枪按键盘输入落在这个可见输入框里。 */}
              <TextInput
                accessibilityLabel={t("carton.scanBlankLabel")}
                placeholder={t("carton.scanBlankPlaceholder")}
                placeholderTextColor={PACK_COLORS.inkMuted}
                value={blankText}
                onChangeText={(value) => setBlankText(value.slice(0, 64))}
                autoCapitalize="characters"
                autoCorrect={false}
                autoFocus
                returnKeyType="done"
                style={packStyles.textInput}
              />
              <Text style={packStyles.muted}>{t("carton.nextScanHint")}</Text>
            </>
          ) : null}
        </View>
        {empty ? <Text style={packStyles.errorText}>{t("carton.errors.sealEmpty")}</Text> : null}
        {weightInvalid ? <Text style={packStyles.errorText}>{t("carton.weightInvalid")}</Text> : null}
        {error ? <Text style={packStyles.errorText}>{error}</Text> : null}
      </View>
    </BusinessSheet>
  );
}

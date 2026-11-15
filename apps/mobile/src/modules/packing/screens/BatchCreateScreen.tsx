import { useMemo, useRef, useState } from "react";
import { ScrollView, StyleSheet, View } from "react-native";
import { Button, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { PickHeader } from "@/modules/warehouse-picking/components/PickHeader";
import { newClientRequestId } from "@/modules/warehouse-picking/api";
import { usePrinterStore } from "@/modules/printer/state";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { createBlankCartonBatch, fetchCartons } from "../api";
import {
  BATCH_QUICK_COUNTS,
  DEFAULT_BATCH_COUNT,
  MAX_BATCH_COPIES,
  MAX_BATCH_COUNT,
  batchRequestKey,
  blankRemaining,
  clampBatchCount,
  createRequestIdReuse,
  parseBatchCountInput,
} from "../batch-logic";
import { packingErrorMessage } from "../packing-errors";
import { blankCartonLabelPreview, buildBlankCartonLabelPayload, destinationFormOf, emptyDestinationForm, toDestinationRequest } from "../packing-logic";
import { usePackPreferences } from "../pack-preferences";
import { PACKING_HOME, batchRoute } from "../pack-print";
import { usePackScope } from "../use-pack-scope";
import { DESTINATION_TYPE, type DestinationFormValue, type PackDestination } from "../types";
import { DestinationSheet } from "../components/DestinationSheet";
import { QuantitySheet } from "../components/EntrySheets";
import { LabelPreview } from "../components/LabelPreview";
import { OwnerBar } from "../components/OwnerBar";
import { PrinterStatusTag } from "../components/PrinterStatusTag";
import { destinationText } from "../components/destination-text";
import { Chip, KeyValueRow, PACK_COLORS, SectionTitle, Stepper, packStyles } from "../components/pack-ui";

/** 预览用的示例箱号：BX + 今天的 yyMMdd + 0001（真实箱号以创建结果为准）。 */
function sampleCartonNo(now: Date) {
  const pad = (value: number) => String(value).padStart(2, "0");
  return `BX${pad(now.getFullYear() % 100)}${pad(now.getMonth() + 1)}${pad(now.getDate())}0001`;
}

/**
 * 批量创建空箱标：数量（1..100）、去向（默认装箱时再选）、标签纸、每个箱号打印份数；
 * 「只创建」或「创建并打印 N 张」（进入连续打印页自动开始）。打印机没连上时只能「只创建」。
 */
export function BatchCreateScreen() {
  const { t, language } = useAppTranslation("packing");
  const router = useRouter();
  const queryClient = useQueryClient();
  const user = useAuthStore((state) => state.user);
  const printerConnected = usePrinterStore((state) => state.status) === "connected";
  const smallLabel = usePackPreferences((state) => state.smallLabel);
  const toggleSmallLabel = usePackPreferences((state) => state.toggleSmallLabel);
  const scope = usePackScope();
  const owner = scope.owner;
  const [count, setCount] = useState(DEFAULT_BATCH_COUNT);
  const [copies, setCopies] = useState(1);
  const [destination, setDestination] = useState<DestinationFormValue>(emptyDestinationForm());
  const [destinationVisible, setDestinationVisible] = useState(false);
  const [countVisible, setCountVisible] = useState(false);
  const [submitting, setSubmitting] = useState<"create" | "print" | null>(null);
  const [snackbar, setSnackbar] = useState("");
  // 失败重试沿用同一个 clientRequestId（服务端据此返回同一批，不会重复创建）；数量 / 去向变了换新号。
  const requestIds = useRef(createRequestIdReuse(newClientRequestId)).current;

  const ownerCode = owner?.code ?? "";
  const blankQuery = useQuery({
    queryKey: ["packing", "cartons", ownerCode, "blank", false, ""],
    queryFn: ({ signal }) => fetchCartons(ownerCode, "blank", "", false, signal),
    enabled: Boolean(ownerCode),
    staleTime: 10_000,
  });
  const unused = blankQuery.data?.blankCount ?? null;
  const remaining = blankRemaining(unused);
  const operatorName = user?.fullName?.trim() || user?.username || null;
  const destinationIsSet = destination.destinationType !== DESTINATION_TYPE.unassigned;

  const preview = useMemo(() => {
    const now = new Date();
    const packDestination: PackDestination = {
      destinationType: destination.destinationType,
      code: destination.code,
      name: destination.name,
      carrierGuid: destination.carrierGuid,
      carrierName: destination.carrierName,
    };
    return blankCartonLabelPreview(
      buildBlankCartonLabelPayload(
        {
          cartonNo: sampleCartonNo(now),
          originName: owner?.name ?? "",
          destination: packDestination,
          orderNo: null,
          createdByName: operatorName ?? "",
          batchSeq: 1,
          batchCount: count,
        },
        now,
        operatorName,
      ),
    );
  }, [count, destination, operatorName, owner?.name]);

  const goBack = () => (router.canGoBack() ? router.back() : router.replace(PACKING_HOME));

  const submit = async (print: boolean) => {
    if (!owner || submitting) return;
    setSubmitting(print ? "print" : "create");
    try {
      const request = destinationIsSet ? toDestinationRequest(destination) : null;
      const key = batchRequestKey({
        ownerCode: owner.code,
        count,
        destinationType: request?.destinationType ?? DESTINATION_TYPE.unassigned,
        destinationCode: request?.destinationCode ?? null,
        carrierGuid: request?.carrierGuid ?? null,
      });
      const result = await createBlankCartonBatch({
        ownerCode: owner.code,
        count,
        clientRequestId: requestIds.get(key),
        ...(request ?? {}),
      });
      requestIds.reset();
      void queryClient.invalidateQueries({ queryKey: ["packing", "cartons"] });
      void queryClient.invalidateQueries({ queryKey: ["packing", "batches"] });
      router.replace(batchRoute(result.batchGuid, { autoPrint: print, copies, duplicate: result.duplicate }));
    } catch (error) {
      setSnackbar(t("batch.createFailed", { detail: packingErrorMessage(error, t, language) }));
    } finally {
      setSubmitting(null);
    }
  };

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={packStyles.screen}>
      <PickHeader title={t("batch.createTitle")} onBack={goBack} backLabel={t("actions.back")}>
        <OwnerBar owners={scope.owners} owner={owner} onChange={scope.setOwner} right={<PrinterStatusTag />} />
      </PickHeader>
      <ScrollView contentContainerStyle={packStyles.body} keyboardShouldPersistTaps="handled">
        <View style={packStyles.card}>
          <Text style={packStyles.muted}>{t("batch.count")}</Text>
          <View style={styles.stepperWrap}>
            <Stepper
              value={count}
              onDecrease={() => setCount((current) => clampBatchCount(current - 1))}
              onIncrease={() => setCount((current) => clampBatchCount(current + 1))}
              onPressValue={() => setCountVisible(true)}
              decreaseLabel={t("batch.countDecrease")}
              increaseLabel={t("batch.countIncrease")}
              valueLabel={t("batch.countValue", { count })}
            />
          </View>
          <View accessibilityRole="radiogroup" accessibilityLabel={t("batch.quickLabel")} style={styles.quick}>
            {BATCH_QUICK_COUNTS.map((value) => (
              <Chip key={value} label={String(value)} selected={count === value} onPress={() => setCount(value)} />
            ))}
          </View>
          <Text style={[packStyles.muted, remaining !== null && count > remaining ? styles.warn : null]}>
            {unused === null
              ? t("batch.limitHintNoCount", { max: MAX_BATCH_COUNT })
              : t("batch.limitHint", { max: MAX_BATCH_COUNT, count: unused })}
          </Text>
        </View>

        <View style={packStyles.cardList}>
          <KeyValueRow
            first
            label={t("batch.destination")}
            value={destinationIsSet ? destinationText(destination, t, ownerCode) : t("batch.destinationLater")}
            onPress={() => setDestinationVisible(true)}
          />
          <KeyValueRow
            label={t("batch.labelPaper")}
            value={smallLabel ? t("seal.smallLabelOn") : t("seal.smallLabelOff")}
            onPress={toggleSmallLabel}
          />
          <KeyValueRow label={t("batch.copies")}>
            <Stepper
              compact
              value={copies}
              onDecrease={() => setCopies((current) => Math.max(1, current - 1))}
              onIncrease={() => setCopies((current) => Math.min(MAX_BATCH_COPIES, current + 1))}
              decreaseLabel={t("batch.copiesDecrease")}
              increaseLabel={t("batch.copiesIncrease")}
              valueLabel={t("batch.copiesValue", { count: copies })}
            />
          </KeyValueRow>
        </View>

        <SectionTitle title={t("batch.preview")} />
        <View style={packStyles.card}>
          <LabelPreview content={preview} accessibilityLabel={t("batch.preview")} />
          <Text style={styles.previewNote}>{t("batch.previewNote", { count })}</Text>
        </View>
      </ScrollView>

      <View style={packStyles.bar}>
        {printerConnected ? null : (
          <View style={styles.offline}>
            <MaterialCommunityIcons name="printer-alert" size={16} color={PACK_COLORS.warning} />
            <Text style={styles.offlineText}>{t("batch.printerOffline")}</Text>
          </View>
        )}
        <View style={packStyles.buttons}>
          <Button
            mode="outlined"
            loading={submitting === "create"}
            disabled={!owner || submitting !== null}
            onPress={() => void submit(false)}
            style={[packStyles.button, packStyles.flexSmall]}
            contentStyle={packStyles.buttonContent}
          >
            {t("batch.createOnly")}
          </Button>
          <Button
            mode="contained"
            icon="printer"
            loading={submitting === "print"}
            disabled={!owner || submitting !== null || !printerConnected}
            onPress={() => void submit(true)}
            style={[packStyles.button, packStyles.flex]}
            contentStyle={packStyles.buttonContent}
          >
            {t("batch.createAndPrint", { count })}
          </Button>
        </View>
      </View>

      <QuantitySheet
        visible={countVisible}
        title={t("batch.count")}
        hint={t("batch.limitHintNoCount", { max: MAX_BATCH_COUNT })}
        initial={count}
        allowZero={false}
        saving={false}
        onDismiss={() => setCountVisible(false)}
        onConfirm={(value) => {
          setCountVisible(false);
          // 超过上限按上限处理；0 / 空不会走到这里（弹层里不能确认）。
          setCount(parseBatchCountInput(String(value)) ?? count);
        }}
      />
      <DestinationSheet
        visible={destinationVisible}
        kind="carton"
        ownerCode={ownerCode}
        title={t("batch.destinationTitle")}
        confirmLabel={t("actions.confirm")}
        initial={destinationFormOf(
          { destinationType: destination.destinationType, code: destination.code, name: destination.name, carrierGuid: destination.carrierGuid, carrierName: destination.carrierName },
        )}
        saving={false}
        onDismiss={() => setDestinationVisible(false)}
        onConfirm={(value) => {
          setDestination(value);
          setDestinationVisible(false);
        }}
      />
      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={4000}>
        {snackbar}
      </Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  stepperWrap: { marginTop: 6, marginBottom: 10 },
  quick: { flexDirection: "row", flexWrap: "wrap", gap: 8, marginBottom: 8 },
  warn: { color: PACK_COLORS.warning, fontWeight: "600" },
  previewNote: { marginTop: 8, textAlign: "center", fontSize: 12, lineHeight: 16, color: PACK_COLORS.textSecondary },
  offline: { flexDirection: "row", alignItems: "center", gap: 6 },
  offlineText: { flex: 1, fontSize: 12, lineHeight: 17, color: PACK_COLORS.warning },
});

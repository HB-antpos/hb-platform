import { useCallback, useEffect, useRef, useState } from "react";
import { Pressable, ScrollView, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useIsFocused } from "@react-navigation/native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import { playScanFeedbackSound, preloadScanFeedbackSounds } from "@/modules/scanner/scan-sound";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { resolvePicker } from "../api";
import { ORDER_QR_PREFIX } from "../code-resolver";
import { usePickerStore } from "../picker-store";
import { pickingErrorMessage } from "../picking-errors";
import { Avatar, PickHeader } from "../components/PickHeader";
import { PickCameraSheet } from "../components/PickCameraSheet";
import { PICK_COLORS } from "../components/pick-theme";

/**
 * 进入拣货前确认拣货人：扫员工码（设备会话也可用），或直接用本机登录的账号。
 * 员工码换回的凭证绑定本终端，之后每次拣货写入都记到这个人名下。
 */
export function PickerConfirmView({
  account,
  onBack,
}: {
  account: { userGuid: string; name: string; canPick: boolean } | null;
  onBack: () => void;
}) {
  const { t, language } = useAppTranslation("warehousePicking");
  const focused = useIsFocused();
  const setPicker = usePickerStore((state) => state.setPicker);
  const [resolving, setResolving] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [cameraVisible, setCameraVisible] = useState(false);
  const busyRef = useRef(false);

  useEffect(() => {
    preloadScanFeedbackSounds();
  }, []);

  const handleScan = useCallback(
    async (raw: string) => {
      const code = raw.trim();
      if (!code || busyRef.current) return;
      if (code.toUpperCase().startsWith(ORDER_QR_PREFIX)) {
        // 先扫了配货单条码：提示先确认人，不当成员工码去查。
        playScanFeedbackSound("blocked");
        setMessage(t("picker.orderCodeFirst"));
        return;
      }

      busyRef.current = true;
      setResolving(true);
      setMessage(null);
      try {
        const result = await resolvePicker(code);
        playScanFeedbackSound("found");
        setPicker({
          userGuid: result.pickerUserGuid,
          name: result.pickerName,
          method: "staffBarcode",
          ticket: result.ticket,
          expiresAtUtc: result.expiresAtUtc,
        });
      } catch (error) {
        playScanFeedbackSound("not_found");
        setMessage(pickingErrorMessage(error, t, language));
      } finally {
        busyRef.current = false;
        setResolving(false);
      }
    },
    [language, setPicker, t],
  );

  const hid = useHidBarcodeScanner({
    enabled: focused && !cameraVisible && !resolving,
    onScan: handleScan,
  });

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.screen}>
      <PickHeader title={t("title")} onBack={onBack} backLabel={t("actions.back")} />
      <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        <View style={styles.headingBlock}>
          <Text accessibilityRole="header" style={styles.heading}>
            {t("picker.heading")}
          </Text>
          <Text style={styles.subheading}>{t("picker.subheading")}</Text>
        </View>

        <View style={styles.scanCard} accessibilityLabel={t("picker.scanTitle")}>
          <View style={styles.scanIcon}>
            {resolving ? (
              <ActivityIndicator color={PICK_COLORS.action} />
            ) : (
              <MaterialCommunityIcons name="barcode-scan" size={34} color={PICK_COLORS.action} />
            )}
          </View>
          <Text style={styles.scanTitle}>{t("picker.scanTitle")}</Text>
          <Text style={styles.scanHint}>{t("picker.scanHint")}</Text>
          <View accessibilityLiveRegion="polite" style={styles.readyPill}>
            <View style={styles.readyDot} />
            <Text style={styles.readyText}>{resolving ? t("picker.resolving") : t("picker.ready")}</Text>
          </View>
          {message ? (
            <Text accessibilityLiveRegion="assertive" style={styles.error}>
              {message}
            </Text>
          ) : null}
          <Pressable accessibilityRole="button" onPress={() => setCameraVisible(true)} style={styles.cameraButton}>
            <MaterialCommunityIcons name="camera-outline" size={18} color={PICK_COLORS.action} />
            <Text style={styles.cameraText}>{t("actions.useCamera")}</Text>
          </Pressable>
        </View>

        <View style={styles.divider}>
          <View style={styles.dividerLine} />
          <Text style={styles.dividerText}>{t("picker.or")}</Text>
          <View style={styles.dividerLine} />
        </View>

        {account ? (
          account.canPick ? (
            <View style={styles.accountCard}>
              <View style={styles.accountRow}>
                <Avatar name={account.name} color={PICK_COLORS.action} size={44} />
                <View style={styles.accountText}>
                  <Text style={styles.accountName}>{account.name}</Text>
                  <Text style={styles.accountMeta}>{t("picker.accountStatus")}</Text>
                </View>
              </View>
              <Pressable
                accessibilityRole="button"
                onPress={() =>
                  setPicker({ userGuid: account.userGuid, name: account.name, method: "account", ticket: null, expiresAtUtc: null })
                }
                style={styles.accountButton}
              >
                <Text style={styles.accountButtonText}>{t("picker.continueAs", { name: account.name })}</Text>
              </Pressable>
            </View>
          ) : (
            <MutedCard icon="account-lock-outline" title={t("picker.accountNotAllowedTitle")} hint={t("picker.accountNotAllowedHint")} />
          )
        ) : (
          <MutedCard icon="cellphone" title={t("picker.deviceModeTitle")} hint={t("picker.deviceModeHint")} />
        )}
      </ScrollView>

      <PickCameraSheet
        visible={cameraVisible}
        title={t("picker.cameraTitle")}
        onDismiss={() => setCameraVisible(false)}
        onBarcode={(barcode) => void handleScan(barcode)}
      />
      {hid.textInputProps ? (
        <TextInput {...hid.textInputProps} style={styles.hiddenInput} accessible={false} importantForAccessibility="no-hide-descendants" />
      ) : null}
    </SafeAreaView>
  );
}

function MutedCard({ icon, title, hint }: { icon: "cellphone" | "account-lock-outline"; title: string; hint: string }) {
  return (
    <View style={styles.mutedCard}>
      <View style={styles.mutedIcon}>
        <MaterialCommunityIcons name={icon} size={22} color={PICK_COLORS.textSecondary} />
      </View>
      <View style={styles.accountText}>
        <Text style={styles.mutedTitle}>{title}</Text>
        <Text style={styles.accountMeta}>{hint}</Text>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: PICK_COLORS.background },
  content: { padding: 16, paddingTop: 20, gap: 16 },
  headingBlock: { gap: 4 },
  heading: { fontSize: 22, lineHeight: 30, fontWeight: "700", color: PICK_COLORS.ink },
  subheading: { fontSize: 13, lineHeight: 20, color: PICK_COLORS.textSecondary },
  scanCard: {
    backgroundColor: PICK_COLORS.white,
    borderWidth: 1.5,
    borderStyle: "dashed",
    borderColor: "#84ADFF",
    borderRadius: 12,
    paddingTop: 20,
    paddingHorizontal: 16,
    paddingBottom: 6,
    alignItems: "center",
    gap: 8,
  },
  scanIcon: { width: 64, height: 64, borderRadius: 32, backgroundColor: PICK_COLORS.infoBg, alignItems: "center", justifyContent: "center" },
  scanTitle: { fontSize: 18, lineHeight: 26, fontWeight: "700", color: PICK_COLORS.ink },
  scanHint: { fontSize: 13, lineHeight: 18, color: PICK_COLORS.textSecondary },
  readyPill: { flexDirection: "row", alignItems: "center", gap: 8, minHeight: 28, paddingHorizontal: 12, borderRadius: 14, backgroundColor: PICK_COLORS.infoBg, marginTop: 4 },
  readyDot: { width: 8, height: 8, borderRadius: 4, backgroundColor: PICK_COLORS.action },
  readyText: { fontSize: 12, fontWeight: "600", color: PICK_COLORS.infoText },
  error: { fontSize: 13, lineHeight: 18, color: PICK_COLORS.danger, textAlign: "center" },
  cameraButton: { minHeight: 44, flexDirection: "row", alignItems: "center", gap: 6, paddingHorizontal: 12 },
  cameraText: { fontSize: 14, fontWeight: "600", color: PICK_COLORS.action },
  divider: { flexDirection: "row", alignItems: "center", gap: 12 },
  dividerLine: { flex: 1, height: 1, backgroundColor: PICK_COLORS.outline },
  dividerText: { fontSize: 12, color: PICK_COLORS.textSecondary },
  accountCard: { backgroundColor: PICK_COLORS.white, borderWidth: 1, borderColor: PICK_COLORS.outlineMuted, borderRadius: 12, padding: 12, gap: 12 },
  accountRow: { flexDirection: "row", alignItems: "center", gap: 12 },
  accountText: { flex: 1, minWidth: 0 },
  accountName: { fontSize: 16, lineHeight: 22, fontWeight: "600", color: PICK_COLORS.ink },
  accountMeta: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  accountButton: { minHeight: 48, borderRadius: 8, borderWidth: 1, borderColor: PICK_COLORS.action, alignItems: "center", justifyContent: "center", paddingHorizontal: 12 },
  accountButtonText: { fontSize: 15, fontWeight: "700", color: PICK_COLORS.action },
  mutedCard: { flexDirection: "row", alignItems: "center", gap: 12, backgroundColor: PICK_COLORS.neutralChipBg, borderWidth: 1, borderColor: PICK_COLORS.outlineMuted, borderRadius: 12, padding: 12 },
  mutedIcon: { width: 44, height: 44, borderRadius: 22, backgroundColor: PICK_COLORS.outlineMuted, alignItems: "center", justifyContent: "center" },
  mutedTitle: { fontSize: 15, lineHeight: 22, fontWeight: "600", color: PICK_COLORS.ink },
  hiddenInput: { position: "absolute", width: 1, height: 1, opacity: 0, left: -100 },
});

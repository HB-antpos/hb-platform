import type { ReactNode } from "react";
import { StyleSheet, View } from "react-native";
import { Button, Text } from "react-native-paper";
import type { CameraScanMode } from "@/modules/scanner/use-camera-scan";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS } from "@/shared/theme/tokens";
import { BusinessSheet } from "./BusinessSheet";
import { CameraScanModeSelector } from "./CameraScanModeSelector";

interface CameraScanSheetProps {
  visible: boolean;
  title: string;
  subtitle?: string;
  mode: CameraScanMode;
  onModeChange: (mode: CameraScanMode) => void;
  onDismiss: () => void;
  /** 相机扫码 hook 返回的相册识别入口；为空（平台 / 原生包不支持）时不显示按钮。 */
  libraryPicker?: { onPress: () => void; busy: boolean } | null;
  children: ReactNode;
}

/** 扫码设置属于当前相机会话，各页面继续持有自己的识别和业务处理逻辑。 */
export function CameraScanSheet({ visible, title, subtitle, mode, onModeChange, onDismiss, libraryPicker, children }: CameraScanSheetProps) {
  const { t } = useAppTranslation("common");
  return (
    <BusinessSheet
      visible={visible}
      title={title}
      subtitle={subtitle}
      onDismiss={onDismiss}
      footer={<Button mode="outlined" onPress={onDismiss} contentStyle={styles.closeButton}>{t("actions.close")}</Button>}
    >
      <View style={styles.settings}>
        <Text variant="labelLarge" style={styles.label}>{t("scanner.cameraModeLabel")}</Text>
        <CameraScanModeSelector value={mode} onChange={onModeChange} style={styles.selector} />
        <Text variant="bodySmall" style={styles.hint} accessibilityLiveRegion="polite">
          {t(mode === "single" ? "scanner.cameraSingleHint" : "scanner.cameraContinuousHint")}
        </Text>
      </View>
      <View style={styles.camera}>{children}</View>
      {libraryPicker ? (
        <Button
          mode="outlined"
          icon="image-outline"
          onPress={libraryPicker.onPress}
          loading={libraryPicker.busy}
          disabled={libraryPicker.busy}
          contentStyle={styles.closeButton}
          style={styles.libraryButton}
        >
          {t("scanner.pickFromLibrary")}
        </Button>
      ) : null}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  settings: { gap: 8 },
  label: { fontWeight: "700", color: HB_COLORS.textPrimary },
  selector: { marginTop: 0 },
  hint: { color: HB_COLORS.textSecondary, lineHeight: 18 },
  camera: { overflow: "hidden", borderRadius: HB_RADIUS.surface },
  closeButton: { minHeight: 48 },
  libraryButton: { marginTop: 12 },
});

import { StyleSheet, View } from "react-native";
import QRCode from "react-native-qrcode-svg";
import { Surface, Text } from "react-native-paper";

interface QrCodePanelProps {
  label: string;
  value: string;
  size?: number;
  /** 二维码下方是否再显示一遍原文；调用方已在别处展示同一串值时可关闭，默认显示。 */
  showValue?: boolean;
}

export function QrCodePanel({ label, value, size = 168, showValue = true }: QrCodePanelProps) {
  const normalizedValue = value.trim();

  return (
    <Surface style={styles.panel} elevation={0}>
      <Text variant="labelLarge" style={styles.label}>
        {label}
      </Text>
      <View style={styles.codeWrap}>
        {normalizedValue ? (
          <QRCode
            value={normalizedValue}
            size={size}
            backgroundColor="#FFFFFF"
            color="#111111"
          />
        ) : (
          <View style={[styles.placeholder, { height: size, width: size }]}>
            <Text variant="headlineMedium" style={styles.placeholderText}>
              --
            </Text>
          </View>
        )}
      </View>
      {showValue ? (
        <Text selectable variant="bodyMedium" style={styles.value}>
          {normalizedValue || "--"}
        </Text>
      ) : null}
    </Surface>
  );
}

const styles = StyleSheet.create({
  panel: {
    alignItems: "center",
    backgroundColor: "#FFFFFF",
    borderColor: "#E4E7EC",
    borderRadius: 12,
    borderWidth: 1,
    gap: 12,
    padding: 16,
  },
  label: {
    color: "#344054",
    fontWeight: "700",
  },
  codeWrap: {
    alignItems: "center",
    justifyContent: "center",
  },
  placeholder: {
    alignItems: "center",
    backgroundColor: "#F8FAFC",
    borderColor: "#D0D5DD",
    borderRadius: 12,
    borderStyle: "dashed",
    borderWidth: 1,
    justifyContent: "center",
  },
  placeholderText: {
    color: "#98A2B3",
    fontWeight: "700",
  },
  value: {
    color: "#475467",
    fontVariant: ["tabular-nums"],
    textAlign: "center",
  },
});

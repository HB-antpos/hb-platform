import { memo, type ReactNode } from "react";
import { StyleSheet, View } from "react-native";
import { Button, IconButton, Modal, Switch, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

interface PrintSettingsModalProps {
  visible: boolean;
  continuousPrint: boolean;
  smallLabel: boolean;
  printQuantity: number;
  quantitySingleUse: boolean;
  /** 弹窗顶部的"打印机"区块（连接状态与去打印机设置页的入口），由页面注入。 */
  printerSection?: ReactNode;
  onToggleContinuousPrint: (value: boolean) => void;
  onToggleSmallLabel: (value: boolean) => void;
  onChangePrintQuantity: (value: number) => void;
  onToggleQuantitySingleUse: (value: boolean) => void;
  onDismiss: () => void;
}

export const PrintSettingsModal = memo(function PrintSettingsModal({
  visible,
  continuousPrint,
  smallLabel,
  printQuantity,
  quantitySingleUse,
  printerSection,
  onToggleContinuousPrint,
  onToggleSmallLabel,
  onChangePrintQuantity,
  onToggleQuantitySingleUse,
  onDismiss,
}: PrintSettingsModalProps) {
  const { t } = useAppTranslation("productQuery");
  // 数量为 1 时"单次有效"没有可恢复的内容，开关只保留用户偏好、不可操作。
  const singleUseApplicable = printQuantity > 1;

  return (
    <Modal visible={visible} onDismiss={onDismiss} contentContainerStyle={styles.modal}>
      <View style={styles.content}>
        <Text variant="titleMedium" style={styles.title}>
          {t("print.settingsTitle")}
        </Text>

        {printerSection}

        <View style={styles.row}>
          <Text variant="bodyMedium">{t("print.continuousPrint")}</Text>
          <Switch value={continuousPrint} onValueChange={onToggleContinuousPrint} />
        </View>

        <View style={styles.row}>
          <Text variant="bodyMedium">{t("print.smallLabel")}</Text>
          <Switch value={smallLabel} onValueChange={onToggleSmallLabel} />
        </View>

        {/* 单次有效只对数量大于 1 的打印有意义（打完恢复为 1），所以挂在打印数量下面成组，数量为 1 时置灰。 */}
        <View style={styles.quantityGroup}>
          <View style={styles.row}>
            <Text variant="bodyMedium">{t("print.quantity")}</Text>
            <View style={styles.quantityRow}>
              <IconButton
                icon="minus"
                size={16}
                onPress={() => onChangePrintQuantity(Math.max(1, printQuantity - 1))}
                disabled={printQuantity <= 1}
                style={styles.qtyButton}
              />
              <Text variant="titleMedium" style={styles.qtyValue}>
                {printQuantity}
              </Text>
              <IconButton
                icon="plus"
                size={16}
                onPress={() => onChangePrintQuantity(Math.min(99, printQuantity + 1))}
                disabled={printQuantity >= 99}
                style={styles.qtyButton}
              />
            </View>
          </View>

          <View style={[styles.row, styles.subRow, !singleUseApplicable && styles.subRowIdle]}>
            <View style={styles.subCopy}>
              <Text variant="bodyMedium">{t("print.quantitySingleUse")}</Text>
              <Text variant="bodySmall" style={styles.subHint}>
                {singleUseApplicable
                  ? t("print.quantitySingleUseHint")
                  : t("print.quantitySingleUseIdleHint")}
              </Text>
            </View>
            <Switch
              accessibilityLabel={t("print.quantitySingleUse")}
              value={quantitySingleUse}
              onValueChange={onToggleQuantitySingleUse}
              disabled={!singleUseApplicable}
            />
          </View>
        </View>

        <View style={styles.footer}>
          <Button mode="contained" onPress={onDismiss}>
            {t("common:actions.confirm")}
          </Button>
        </View>
      </View>
    </Modal>
  );
}, (previous, next) => {
  // 关闭期间跳过父页面刷新；开关切换及打开期间始终接收最新数据和回调。
  return !previous.visible && !next.visible;
});

const styles = StyleSheet.create({
  modal: {
    marginHorizontal: 24,
    borderRadius: 16,
    backgroundColor: "#FFFFFF",
  },
  content: {
    padding: 20,
    gap: 12,
  },
  title: {
    fontWeight: "700",
    color: "#111827",
  },
  row: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  quantityGroup: {
    gap: 4,
  },
  quantityRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
  },
  // 缩进 + 左侧细线，表明它从属于上一行的打印数量。
  subRow: {
    marginLeft: 4,
    paddingLeft: 12,
    borderLeftWidth: 2,
    borderLeftColor: "#E4E7EC",
  },
  subRowIdle: {
    opacity: 0.45,
  },
  subCopy: {
    flex: 1,
    paddingRight: 12,
  },
  subHint: {
    color: "#475467",
  },
  qtyButton: {
    width: 40,
    height: 40,
    margin: 0,
  },
  qtyValue: {
    minWidth: 28,
    textAlign: "center",
    fontWeight: "700",
  },
  footer: {
    alignItems: "flex-end",
    paddingTop: 4,
  },
});

import { StyleSheet, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import type { OrderReceiveEntry as OrderEntry } from "../order-receive-logic";
import { KeyValueRow, PACK_COLORS, packStyles } from "./pack-ui";

/**
 * 签收扫码结果里的「按订单签收」入口：这批货属于某张订货单时，可以改成把这张订单发给本店的全部在途箱一次签收，
 * 不用逐托盘、逐箱扫。同一订单只出现一次。
 */
export function OrderReceiveEntries({ entries, onOpen }: { entries: readonly OrderEntry[]; onOpen: (orderGuid: string) => void }) {
  const { t } = useAppTranslation("packing");
  if (entries.length === 0) return null;
  return (
    <View style={[packStyles.card, styles.card]}>
      <Text style={packStyles.muted}>{t("receive.byOrderHint")}</Text>
      {entries.map((entry) => (
        <Button
          key={entry.orderGuid}
          mode="outlined"
          icon="clipboard-check-outline"
          onPress={() => onOpen(entry.orderGuid)}
          style={packStyles.button}
          contentStyle={packStyles.buttonContent}
        >
          {entry.orderNo ? t("receive.byOrderWithNo", { orderNo: entry.orderNo }) : t("receive.byOrder")}
        </Button>
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  card: { gap: 8, backgroundColor: PACK_COLORS.cellBg },
});

/** 一批货关联多张订单时先选订单：列出订单号，点哪张就进入哪张的按订单签收。 */
export function OrderChooserSheet({
  visible,
  entries,
  onDismiss,
  onSelect,
}: {
  visible: boolean;
  entries: readonly OrderEntry[];
  onDismiss: () => void;
  onSelect: (orderGuid: string) => void;
}) {
  const { t } = useAppTranslation("packing");
  return (
    <BusinessSheet visible={visible} title={t("receive.chooseOrderTitle")} subtitle={t("receive.chooseOrderSubtitle")} onDismiss={onDismiss}>
      <View style={packStyles.cardList}>
        {entries.map((entry, index) => (
          <KeyValueRow
            key={entry.orderGuid}
            first={index === 0}
            label={entry.orderNo ?? t("receive.byOrder")}
            value={t("receive.byOrder")}
            onPress={() => onSelect(entry.orderGuid)}
          />
        ))}
      </View>
    </BusinessSheet>
  );
}

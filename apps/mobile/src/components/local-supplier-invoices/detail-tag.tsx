import { StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { TAG_COLORS, type TagTone } from "@/components/local-supplier-invoices/invoice-detail-format";

/** 明细卡片与头部共用的小标签。 */
export function DetailTag({ label, tone }: { label: string; tone: TagTone }) {
  const color = TAG_COLORS[tone];
  return (
    <View style={[styles.tag, { backgroundColor: color.bg }]}>
      <Text style={[styles.text, { color: color.fg }]} numberOfLines={1}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  tag: { paddingHorizontal: 7, paddingVertical: 1, borderRadius: 9, alignSelf: "flex-start" },
  text: { fontSize: 11, lineHeight: 17, fontWeight: "700" },
});

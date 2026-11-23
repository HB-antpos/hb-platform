import { StyleSheet, Text, View } from "react-native";
import QRCode from "react-native-qrcode-svg";
import { splitPackNumber, type LabelPreviewContent } from "../packing-logic";
import { MONO_FONT } from "./pack-ui";

// 预览按 570×208 打印点等比缩小；二维码是真实可扫的（箱 / 托盘标签不再印一维码）。
const SCALE = 0.54;
const WIDTH = Math.round(570 * SCALE);
const HEIGHT = Math.round(208 * SCALE);
const KIND_BAR_WIDTH = Math.round(70 * SCALE);

/** 黑条里的竖排文字：先给一个「窄而高」的盒子占位，再把横排文字居中旋转 −90°（自下而上读），与打印出来的 VTEXT 一致。 */
function VerticalText({ value, length, fontSize, style }: { value: string; length: number; fontSize: number; style: object }) {
  return (
    <View style={{ width: fontSize + 2, height: length, alignItems: "center", justifyContent: "center" }}>
      <Text numberOfLines={1} style={[style, { width: length, textAlign: "center", transform: [{ rotate: "-90deg" }] }]}>
        {value}
      </Text>
    </View>
  );
}

/** 封箱 / 封托弹层里的标签预览：版式与打印出来的英文标签一致，标签上固定是英文，不随界面语言变化。 */
export function LabelPreview({ content, accessibilityLabel }: { content: LabelPreviewContent; accessibilityLabel: string }) {
  const { prefix, tail } = splitPackNumber(content.number);
  const framed = content.kind === "PLT";
  const longDestination = content.destinationName.length > 13;
  return (
    <View accessible accessibilityRole="image" accessibilityLabel={accessibilityLabel} style={[styles.label, framed ? styles.framed : null]}>
      <View style={styles.kind}>
        {/* 竖排自下而上读：CARTON / PALLET 在上、BOX / PLT 在下，与打印出来的版式一致。 */}
        <VerticalText value={framed ? "PALLET" : "CARTON"} length={46} fontSize={7} style={styles.kindCaption} />
        <VerticalText value={content.kind} length={44} fontSize={15} style={styles.kindText} />
      </View>
      <View style={styles.main}>
        <View style={styles.top}>
          <View style={styles.texts}>
            <Text numberOfLines={1} style={styles.from}>
              {content.fromLine}
            </Text>
            <View style={styles.toRow}>
              <Text style={styles.toCaption}>TO</Text>
              {content.toRule ? (
                // 空箱标没有去向：TO 后面留一条横线，贴标签后手写。
                <View style={styles.toRule} />
              ) : (
                <Text numberOfLines={1} style={[styles.to, longDestination ? styles.toLong : null]}>
                  {content.destinationName}
                </Text>
              )}
            </View>
            <View style={styles.codeRow}>
              {prefix ? <Text style={styles.codePrefix}>{prefix}</Text> : null}
              <Text style={styles.codeTail}>{tail}</Text>
              {content.statLine ? (
                <Text numberOfLines={1} style={styles.stat}>
                  {content.statLine}
                </Text>
              ) : null}
            </View>
          </View>
          <QRCode value={content.number || "-"} size={Math.round(105 * SCALE)} backgroundColor="#FFFFFF" color="#000000" />
        </View>
        <View style={styles.bottom}>
          <View style={styles.meta}>
            {content.referenceLine ? (
              <Text numberOfLines={1} style={styles.metaText}>
                {content.referenceLine}
              </Text>
            ) : null}
            <Text numberOfLines={1} style={styles.metaText}>
              {content.timeLine}
              {content.sheetText ? <Text style={styles.sheet}>{`  ${content.sheetText}`}</Text> : null}
            </Text>
          </View>
        </View>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  label: {
    width: WIDTH,
    height: HEIGHT,
    alignSelf: "center",
    flexDirection: "row",
    backgroundColor: "#FFFFFF",
    borderRadius: 4,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: "#98A2B3",
    overflow: "hidden",
  },
  framed: { borderWidth: 3, borderColor: "#000000" },
  kind: { width: KIND_BAR_WIDTH, backgroundColor: "#000000", alignItems: "center", justifyContent: "center", gap: 6 },
  kindText: { color: "#FFFFFF", fontSize: 15, fontWeight: "900" },
  kindCaption: { color: "#FFFFFF", fontSize: 7, fontWeight: "700" },
  main: { flex: 1, minWidth: 0, paddingHorizontal: 6, paddingVertical: 4, justifyContent: "space-between" },
  top: { flexDirection: "row", gap: 4 },
  texts: { flex: 1, minWidth: 0 },
  from: { fontSize: 7, lineHeight: 10, fontWeight: "600", color: "#000000" },
  toRow: { flexDirection: "row", alignItems: "baseline", gap: 3 },
  toCaption: { fontSize: 7, fontWeight: "800", color: "#000000" },
  to: { flexShrink: 1, fontSize: 20, lineHeight: 24, fontWeight: "900", color: "#000000" },
  toLong: { fontSize: 14 },
  toRule: { flex: 1, height: 1, backgroundColor: "#000000", alignSelf: "flex-end", marginBottom: 4 },
  codeRow: { flexDirection: "row", alignItems: "baseline", gap: 3 },
  codePrefix: { fontFamily: MONO_FONT, fontSize: 10, fontWeight: "700", color: "#000000" },
  codeTail: { fontFamily: MONO_FONT, fontSize: 20, lineHeight: 24, fontWeight: "800", color: "#000000" },
  stat: { flexShrink: 1, fontSize: 8, fontWeight: "800", color: "#000000", marginLeft: 2 },
  bottom: { flexDirection: "row", alignItems: "flex-end" },
  meta: { flex: 1, minWidth: 0, alignItems: "flex-end" },
  metaText: { fontSize: 7, lineHeight: 10, fontWeight: "600", color: "#000000" },
  sheet: { fontFamily: MONO_FONT, fontSize: 10, fontWeight: "900" },
});

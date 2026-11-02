import { memo, useMemo } from "react";
import { StyleSheet, Text, View, useWindowDimensions } from "react-native";
import QRCode from "react-native-qrcode-svg";
import Svg, { Rect } from "react-native-svg";
import { ORDER_MONO_FONT } from "@/components/order/order-fonts";
import { encodeCode128 } from "@/modules/sales-orders/receipt-code128";
import {
  RECEIPT_BARCODE_HEIGHT_DOTS,
  RECEIPT_BARCODE_MODULE_DOTS,
  RECEIPT_COLUMNS,
  RECEIPT_QR_DOTS,
  receiptBarRects,
  receiptDotsToPixels,
  receiptFontSize,
} from "@/modules/sales-orders/receipt-layout";
import type { SalesOrderReceiptDocument, SalesOrderReceiptLine } from "@/modules/sales-orders/types";

/** 等宽字体字宽 / 字号，与 receipt-layout 的换算保持同一个比例。 */
const CHAR_WIDTH_RATIO = 0.62;
const PAPER_PADDING_H = 14;
/** 订单详情面板左右留白 + 小票画布左右留白：与面板的 padding 保持一致，字号才算得准。 */
const SURROUNDING_H = 12 * 2 + 12 * 2;

function Barcode({ value, fontSize }: { value: string; fontSize: number }) {
  const encoding = useMemo(() => {
    try {
      return encodeCode128(value);
    } catch {
      return null;
    }
  }, [value]);
  if (!encoding) return null;
  const width = receiptDotsToPixels(encoding.moduleCount * RECEIPT_BARCODE_MODULE_DOTS, fontSize);
  const height = receiptDotsToPixels(RECEIPT_BARCODE_HEIGHT_DOTS, fontSize);
  return (
    <Svg
      width={width}
      height={height}
      viewBox={`0 0 ${encoding.moduleCount} 1`}
      preserveAspectRatio="none"
      accessibilityLabel={value}
    >
      {receiptBarRects(encoding.runs).map((bar) => (
        <Rect key={bar.x} x={bar.x} y={0} width={bar.width} height={1} fill="#111111" />
      ))}
    </Svg>
  );
}

function Row({
  line,
  fontSize,
  textWidth,
}: {
  line: SalesOrderReceiptLine;
  fontSize: number;
  textWidth: number;
}) {
  const lineHeight = Math.round(fontSize * 1.5);
  if (line.kind === "blank") return <View style={{ height: lineHeight }} />;
  if (line.kind === "barcode") {
    return (
      <View style={[styles.machine, { width: textWidth }]}>
        <Barcode value={line.text} fontSize={fontSize} />
      </View>
    );
  }
  if (line.kind === "qr") {
    return (
      <View style={[styles.machine, { width: textWidth }]}>
        <QRCode
          value={line.text}
          size={receiptDotsToPixels(RECEIPT_QR_DOTS, fontSize)}
          backgroundColor="#FFFFFF"
          color="#111111"
        />
      </View>
    );
  }
  return (
    <Text
      // 系统「字体放大」会把 42 列撑出屏幕，纸面固定按版式换算的字号，不跟随系统字号。
      allowFontScaling={false}
      numberOfLines={1}
      style={[
        styles.text,
        {
          width: textWidth,
          fontSize,
          lineHeight,
          textAlign: line.align === "center" ? "center" : "left",
          fontWeight: line.bold ? "700" : "400",
        },
      ]}
    >
      {line.text}
    </Text>
  );
}

interface ReceiptPaperProps {
  document: SalesOrderReceiptDocument;
}

/**
 * 小票纸面：后端已把整张小票排成逐行内容，这里只按行画。
 * 纸宽固定 42 个等宽字符；屏幕不够宽时整张纸缩小字号而不是折行，保证金额列不错位。
 */
function ReceiptPaperComponent({ document }: ReceiptPaperProps) {
  const { width: screenWidth } = useWindowDimensions();
  const fontSize = receiptFontSize(screenWidth - SURROUNDING_H - PAPER_PADDING_H * 2);
  const textWidth = RECEIPT_COLUMNS * fontSize * CHAR_WIDTH_RATIO;
  return (
    <View
      style={[styles.paper, { paddingHorizontal: PAPER_PADDING_H }]}
      accessibilityLabel={document.shortCode}
    >
      {document.lines.map((line, index) => (
        <Row key={`${index}-${line.kind}`} line={line} fontSize={fontSize} textWidth={textWidth} />
      ))}
    </View>
  );
}

export const ReceiptPaper = memo(ReceiptPaperComponent);

const styles = StyleSheet.create({
  paper: {
    alignSelf: "center",
    backgroundColor: "#FFFFFF",
    paddingVertical: 16,
    borderRadius: 4,
    shadowColor: "#101828",
    shadowOffset: { width: 0, height: 4 },
    shadowOpacity: 0.12,
    shadowRadius: 10,
    elevation: 3,
  },
  text: { fontFamily: ORDER_MONO_FONT, color: "#17181B", includeFontPadding: false },
  machine: { alignItems: "center", paddingVertical: 6 },
});

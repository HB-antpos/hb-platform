import { Pressable, StyleSheet, View } from "react-native";
import QRCode from "react-native-qrcode-svg";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { ORDER_MONO_FONT } from "@/components/order/order-fonts";
import type { SalesOrderReceiptLink } from "@/modules/sales-orders/types";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

export interface ReceiptQrOverlayProps {
  /** 当前有效链接；没有时按 busy / failed 显示生成中或失败。 */
  link: SalesOrderReceiptLink | null;
  busy: boolean;
  failed: boolean;
  /** 复制 / 分享 / 重新生成后的一句提示，由父级几秒后清掉。 */
  notice: string | null;
  labels: {
    title: string;
    hint: string;
    creating: string;
    createFailed: string;
    retry: string;
    copy: string;
    share: string;
    regenerate: string;
    close: string;
    meta: string;
  };
  onClose: () => void;
  onCopy: () => void;
  onShare: () => void;
  onRegenerate: () => void;
  onRetry: () => void;
}

/**
 * 在线小票二维码面板：直接盖在订单详情面板里（不再嵌套 Modal，避免 iOS 上嵌套 Modal 的呈现问题）。
 * 客人扫码免登录打开只读小票；店员也可以复制链接或用系统分享发给客人。
 */
export function ReceiptQrOverlay({
  link,
  busy,
  failed,
  notice,
  labels,
  onClose,
  onCopy,
  onShare,
  onRegenerate,
  onRetry,
}: ReceiptQrOverlayProps) {
  return (
    <View style={styles.overlay} pointerEvents="box-none">
      <Pressable style={styles.scrim} onPress={onClose} accessibilityLabel={labels.close} />
      <View style={styles.panel}>
        <View style={styles.header}>
          <View style={styles.heading}>
            <Text style={styles.title}>{labels.title}</Text>
            <Text style={styles.hint}>{labels.hint}</Text>
          </View>
          <Button compact onPress={onClose}>
            {labels.close}
          </Button>
        </View>

        {link ? (
          <>
            <View style={styles.qrWrap}>
              <QRCode value={link.url} size={188} backgroundColor="#FFFFFF" color="#111111" />
            </View>
            <Text selectable style={styles.url}>
              {link.url}
            </Text>
            <View style={styles.actions}>
              <Button mode="contained-tonal" icon="content-copy" style={styles.action} onPress={onCopy}>
                {labels.copy}
              </Button>
              <Button mode="contained" icon="export-variant" style={styles.action} onPress={onShare}>
                {labels.share}
              </Button>
            </View>
            <View style={styles.metaRow}>
              <Text style={styles.meta}>{labels.meta}</Text>
              <Button compact textColor={HB_COLORS.danger} loading={busy} disabled={busy} onPress={onRegenerate}>
                {labels.regenerate}
              </Button>
            </View>
          </>
        ) : failed ? (
          <View style={styles.state}>
            <Text style={styles.stateText}>{labels.createFailed}</Text>
            <Button mode="outlined" compact onPress={onRetry}>
              {labels.retry}
            </Button>
          </View>
        ) : (
          <View style={styles.state}>
            <ActivityIndicator color={HB_COLORS.brand} />
            <Text style={styles.stateText}>{labels.creating}</Text>
          </View>
        )}
        {notice ? <Text style={styles.notice}>{notice}</Text> : null}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  overlay: { ...StyleSheet.absoluteFillObject, justifyContent: "flex-end", zIndex: 10 },
  scrim: { ...StyleSheet.absoluteFillObject, backgroundColor: "rgba(16,24,40,0.5)" },
  panel: {
    backgroundColor: HB_COLORS.white,
    borderTopLeftRadius: HB_RADIUS.sheet,
    borderTopRightRadius: HB_RADIUS.sheet,
    padding: HB_SPACING.md,
    gap: HB_SPACING.sm,
  },
  header: { flexDirection: "row", alignItems: "flex-start", gap: HB_SPACING.xs },
  heading: { flex: 1, minWidth: 0 },
  title: { fontSize: 16, fontWeight: "800", color: HB_COLORS.textPrimary },
  hint: { fontSize: 12, color: HB_COLORS.textSecondary, marginTop: 2 },
  qrWrap: {
    alignSelf: "center",
    padding: 14,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  url: {
    fontFamily: ORDER_MONO_FONT,
    fontSize: 12,
    color: HB_COLORS.textSecondary,
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: HB_RADIUS.control,
    paddingHorizontal: HB_SPACING.xs,
    paddingVertical: 8,
  },
  actions: { flexDirection: "row", gap: HB_SPACING.xs },
  action: { flex: 1 },
  metaRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between" },
  meta: { flex: 1, fontSize: 12, color: HB_COLORS.textSecondary },
  state: { alignItems: "center", gap: HB_SPACING.xs, paddingVertical: HB_SPACING.md },
  stateText: { fontSize: 13, color: HB_COLORS.textSecondary, textAlign: "center" },
  notice: { fontSize: 12, color: HB_COLORS.success, textAlign: "center" },
});

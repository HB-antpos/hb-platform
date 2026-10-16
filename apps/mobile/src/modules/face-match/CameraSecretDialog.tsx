// 一次性展示新增 / 重新生成令牌后的摄像头地址（含 token）。
//
// 约定（由 source-contract / camera-secret 测试守住）：
// 1. 地址只存在于父组件的局部 state（SecretRevealState）和本组件的 props 里；这里不写任何存储（AsyncStorage / SecureStore / 文件）、
//    不进 react-query、不进路由参数、不进日志，也不放进提示 / 通知文案；
// 2. 关闭（按钮）、应用离开前台（切后台 / 下拉通知栏 / 应用切换器）、组件卸载都会清空——关闭由父组件把状态换成已清空常量；
// 3. 默认用掩码显示，点「显示」才露出完整地址；复制后 60 秒内若剪贴板还是这条地址，就把剪贴板清空（尽力而为，应用被挂起时不保证）；
// 4. 用原生 Modal 承载（不用 Paper 的 Portal 弹层）：它出现在新增表单 / 确认弹层关闭的同一刻，Portal 会被正在关闭的原生 Modal 盖住；
// 5. 安卓返回键不关闭：地址只显示这一次，误触返回就丢了，只能点「我已保存，关闭」。
import { useEffect, useRef, useState } from "react";
import { AppState, Modal, ScrollView, StyleSheet, View } from "react-native";
import * as Clipboard from "expo-clipboard";
import { Button, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { maskCameraUrl, type SecretRevealState } from "./camera-secret";
import { MONO, Notice } from "./ui";

/** 复制后多久清空剪贴板（只在剪贴板里还是这条地址时才清）。 */
export const CLIPBOARD_CLEAR_DELAY_MS = 60_000;

let clipboardClearTimer: ReturnType<typeof setTimeout> | null = null;

/** 复制后定时清空剪贴板；用模块级定时器，这样关闭弹窗后仍会执行。重复复制只保留最新一个定时器。 */
function scheduleClipboardClear(copied: string) {
  if (clipboardClearTimer) clearTimeout(clipboardClearTimer);
  clipboardClearTimer = setTimeout(() => {
    clipboardClearTimer = null;
    void Clipboard.getStringAsync()
      .then((current) => (current === copied ? Clipboard.setStringAsync("") : undefined))
      .catch(() => undefined);
  }, CLIPBOARD_CLEAR_DELAY_MS);
}

interface Props {
  /** 只在 state.open 为 true 时由父组件挂载。 */
  state: Extract<SecretRevealState, { open: true }>;
  onClose: () => void;
}

export function CameraSecretDialog({ state, onClose }: Props) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const [shown, setShown] = useState(false);
  const [feedback, setFeedback] = useState<"copied" | "failed" | null>(null);
  const closeRef = useRef(onClose);
  closeRef.current = onClose;

  // 应用离开前台就关闭并清空：地址不能留在应用切换器的快照 / 后台内存里。
  useEffect(() => {
    const subscription = AppState.addEventListener("change", (next) => {
      if (next !== "active") closeRef.current();
    });
    return () => subscription.remove();
  }, []);

  const copy = async () => {
    try {
      await Clipboard.setStringAsync(state.cameraUrl);
      scheduleClipboardClear(state.cameraUrl);
      setFeedback("copied");
    } catch {
      // 吞掉异常，不记录异常对象（避免地址进入日志）；剪贴板不可用时把地址显示出来，让用户手动长按复制。
      setShown(true);
      setFeedback("failed");
    }
  };

  const title = state.kind === "created" ? t("settings.cameras.secret.titleCreated") : t("settings.cameras.secret.titleRotated");
  const note =
    state.kind === "created"
      ? t("settings.cameras.secret.createdNote", { cameraId: state.cameraId })
      : t("settings.cameras.secret.rotatedNote", { cameraId: state.cameraId });

  return (
    <Modal visible transparent animationType="fade" statusBarTranslucent onRequestClose={() => undefined}>
      <View style={styles.backdrop}>
        <View style={styles.card}>
          <ScrollView contentContainerStyle={styles.body} keyboardShouldPersistTaps="handled">
            <Text style={styles.title}>{title}</Text>
            <Notice tone="warning" text={t("settings.cameras.secret.onceOnly")} icon="shield-lock-outline" />
            <Text style={styles.note}>{note}</Text>
            <Text style={styles.label}>{t("settings.cameras.secret.urlLabel")}</Text>
            <View style={styles.urlBox}>
              <Text selectable={shown} style={[styles.url, MONO]}>
                {shown ? state.cameraUrl : maskCameraUrl(state.cameraUrl)}
              </Text>
            </View>
            <View style={styles.row}>
              <Button mode="outlined" icon={shown ? "eye-off-outline" : "eye-outline"} onPress={() => setShown((value) => !value)}>
                {shown ? t("settings.cameras.secret.hide") : t("settings.cameras.secret.show")}
              </Button>
              <Button mode="contained-tonal" icon="content-copy" onPress={() => void copy()}>
                {t("settings.cameras.secret.copy")}
              </Button>
            </View>
            {feedback === "copied" ? <Text style={styles.ok}>{t("settings.cameras.secret.copied")}</Text> : null}
            {feedback === "failed" ? <Text style={styles.fail}>{t("settings.cameras.secret.copyFailed")}</Text> : null}
            <Text style={styles.note}>{t("settings.cameras.secret.usage")}</Text>
            <Button mode="contained" onPress={onClose}>
              {t("settings.cameras.secret.close")}
            </Button>
          </ScrollView>
        </View>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  backdrop: {
    flex: 1,
    justifyContent: "center",
    padding: HB_SPACING.md,
    backgroundColor: "rgba(0,0,0,0.5)",
  },
  card: {
    maxHeight: "90%",
    borderRadius: HB_RADIUS.sheet,
    backgroundColor: HB_COLORS.surface,
  },
  body: { gap: HB_SPACING.sm, padding: HB_SPACING.md },
  title: { fontSize: 18, fontWeight: "700", color: HB_COLORS.textPrimary },
  note: { fontSize: 13, lineHeight: 19, color: HB_COLORS.textSecondary },
  label: { fontSize: 13, fontWeight: "700", color: HB_COLORS.textPrimary },
  urlBox: {
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.background,
  },
  url: { fontSize: 13, lineHeight: 19, color: HB_COLORS.textPrimary },
  row: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  ok: { fontSize: 13, color: HB_COLORS.success },
  fail: { fontSize: 13, color: HB_COLORS.danger },
});

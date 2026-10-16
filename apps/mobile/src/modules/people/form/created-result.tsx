/**
 * 创建完成页（设计稿 A3③）：整页展示（不是弹窗）。
 * 结果先说清：账号可复制、归属一行、初始密码发到哪（仅在确实发了邮件时才出绿色提示）；
 * 下一步动作就近：打印个人码、重新发送、继续新增、完成。
 */
import { type ReactNode } from "react";
import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { Button, Icon, IconButton, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";

import { InfoCard, KvRow, NoticeBox, StickyActionBar, type PeopleTone } from "../ui";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

export interface CreatedNotice {
  tone: PeopleTone;
  icon: string;
  text: string;
  /** 提示条下方紧跟的操作（如发送失败时的「重新发送初始密码」）。 */
  action?: { label: string; onPress: () => void; loading?: boolean; icon?: string };
}

export interface CreatedResultProps {
  title: string;
  headline: string;
  description: string;
  accountLabel: string;
  username: string;
  /** 「归属」区的若干行（店长版一行：分店 · 店员 · 临时工；管理员版两行：角色、分店）。 */
  assignmentRows: { label: string; value: string }[];
  copyLabel: string;
  onCopy: () => void;
  notices: CreatedNotice[];
  steps?: { title: string; items: string[] };
  /** 卡片下方居中的文字操作，如「重新发送初始密码」「继续新增」。 */
  links: { label: string; onPress: () => void; loading?: boolean; testID?: string }[];
  /** 没有打印资格时不传：没有权限的按钮直接不显示，不灰着占位。 */
  print?: { label: string; onPress: () => void };
  doneLabel: string;
  onDone: () => void;
  closeLabel: string;
  snackbarMessage: string;
  onDismissSnackbar: () => void;
  /** 额外的整页内容（如打印个人码弹层），由调用方放进来以便共用同一个 SafeArea。 */
  overlay?: ReactNode;
}

export function CreatedResult(props: CreatedResultProps) {
  return (
    <SafeAreaView style={styles.screen} edges={["top", "left", "right", "bottom"]}>
      <View style={styles.header}>
        <IconButton icon="close" size={22} accessibilityLabel={props.closeLabel} onPress={props.onDone} />
        <Text accessibilityRole="header" style={styles.headerTitle}>{props.title}</Text>
      </View>
      <ScrollView contentContainerStyle={styles.content}>
        <View style={styles.intro}>
          <View style={styles.checkCircle}>
            <Icon source="check-bold" size={30} color={HB_COLORS.success} />
          </View>
          <Text style={styles.headline}>{props.headline}</Text>
          <Text style={styles.description}>{props.description}</Text>
        </View>

        <InfoCard>
          <KvRow
            first
            mono
            label={props.accountLabel}
            value={props.username}
            trailing={<IconButton icon="content-copy" size={18} accessibilityLabel={props.copyLabel} onPress={props.onCopy} style={styles.copyButton} />}
          />
          {props.assignmentRows.map((row) => (
            // 归属可能很长（多个角色 / 分店），允许换行而不是被截断。
            <KvRow key={row.label} label={row.label} value={<Text style={styles.rowValue}>{row.value}</Text>} />
          ))}
        </InfoCard>

        {props.notices.map((notice, index) => (
          <View key={`${notice.tone}-${index}`} style={styles.noticeGroup}>
            <NoticeBox tone={notice.tone} icon={notice.icon}>{notice.text}</NoticeBox>
            {notice.action ? (
              <Button
                mode="text"
                compact
                icon={notice.action.icon}
                onPress={notice.action.onPress}
                loading={notice.action.loading}
                disabled={notice.action.loading}
                style={styles.noticeAction}
              >
                {notice.action.label}
              </Button>
            ) : null}
          </View>
        ))}

        {props.steps ? (
          <InfoCard style={styles.stepsCard}>
            <Text style={styles.stepsTitle}>{props.steps.title}</Text>
            {props.steps.items.map((item, index) => (
              <View key={item} style={styles.stepRow}>
                <Text style={styles.stepIndex}>{`${index + 1}.`}</Text>
                <Text style={styles.stepText}>{item}</Text>
              </View>
            ))}
          </InfoCard>
        ) : null}

        {props.links.length ? (
          <View style={styles.links}>
            {props.links.map((link) => (
              <Pressable
                key={link.label}
                accessibilityRole="button"
                accessibilityState={{ disabled: link.loading }}
                disabled={link.loading}
                onPress={link.onPress}
                hitSlop={8}
                testID={link.testID}
              >
                <Text style={[styles.link, link.loading && styles.linkDisabled]}>{link.label}</Text>
              </Pressable>
            ))}
          </View>
        ) : null}
      </ScrollView>

      <StickyActionBar>
        {props.print ? (
          <Button mode="contained-tonal" icon="qrcode" onPress={props.print.onPress} style={styles.footerButton} contentStyle={styles.footerButtonContent}>
            {props.print.label}
          </Button>
        ) : null}
        <Button mode="contained" buttonColor={HB_COLORS.action} onPress={props.onDone} style={styles.footerButton} contentStyle={styles.footerButtonContent}>
          {props.doneLabel}
        </Button>
      </StickyActionBar>
      {props.overlay}
      <Snackbar visible={Boolean(props.snackbarMessage)} onDismiss={props.onDismissSnackbar} duration={2000}>{props.snackbarMessage}</Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  screen: { backgroundColor: HB_COLORS.background, flex: 1 },
  header: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    minHeight: 56,
    paddingRight: HB_SPACING.md,
  },
  headerTitle: { color: HB_COLORS.textPrimary, flex: 1, fontSize: 18, fontWeight: "800" },
  content: { gap: HB_SPACING.sm, padding: HB_SPACING.md, paddingBottom: HB_SPACING.xl },
  intro: { alignItems: "center", gap: 6, paddingBottom: 4, paddingTop: 14 },
  checkCircle: { alignItems: "center", backgroundColor: "#ECFDF3", borderRadius: 28, height: 56, justifyContent: "center", width: 56 },
  headline: { color: HB_COLORS.textPrimary, fontSize: 19, fontWeight: "800" },
  description: { color: HB_COLORS.textSecondary, fontSize: 12.5, lineHeight: 18, textAlign: "center" },
  copyButton: { margin: 0 },
  rowValue: { color: HB_COLORS.textPrimary, flexShrink: 1, fontSize: 14, paddingVertical: 10 },
  noticeGroup: { gap: 2 },
  noticeAction: { alignSelf: "flex-start" },
  stepsCard: { gap: 6, padding: 14 },
  stepsTitle: { color: HB_COLORS.textPrimary, fontSize: 13.5, fontWeight: "800" },
  stepRow: { flexDirection: "row", gap: 6 },
  stepIndex: { color: HB_COLORS.textSecondary, fontSize: 12.5, lineHeight: 20, width: 18 },
  stepText: { color: HB_COLORS.textSecondary, flex: 1, fontSize: 12.5, lineHeight: 20 },
  links: { flexDirection: "row", gap: 20, justifyContent: "center", paddingTop: 2 },
  link: { color: HB_COLORS.action, fontSize: 13, fontWeight: "700" },
  linkDisabled: { opacity: 0.5 },
  footerButton: { flex: 1 },
  footerButtonContent: { minHeight: 48 },
});

import type { ReactNode } from "react";
import { KeyboardAvoidingView, Platform, ScrollView, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { SafeAreaView } from "react-native-safe-area-context";
import { Button, Icon, IconButton, Text, TextInput, TouchableRipple } from "react-native-paper";
import { HB_COLORS as C } from "@/shared/theme/tokens";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { useBufferedSearchText } from "./use-buffered-search-text";

export function AdminScreen({ title, children, action, footer, onBack }: {
  title: string; children: ReactNode; action?: ReactNode; footer?: ReactNode; onBack?: () => void;
}) {
  const router = useRouter();
  const { language } = useAppTranslation();
  return <SafeAreaView style={styles.screen} edges={["top", "left", "right", "bottom"]}>
    <KeyboardAvoidingView style={{ flex: 1 }} behavior={Platform.OS === "ios" ? "padding" : undefined}>
      <View style={styles.header}>
        <IconButton icon="chevron-left" accessibilityLabel={language === "zh" ? "返回" : "Back"} onPress={onBack ?? (() => router.canGoBack() ? router.back() : router.replace("/(shell)/home"))} />
        <Text accessibilityRole="header" style={styles.title}>{title}</Text>
        <View style={styles.headerAction}>{action}</View>
      </View>
      <View style={{ flex: 1 }}>{children}</View>
      {footer ? <View style={styles.footer}>{footer}</View> : null}
    </KeyboardAvoidingView>
  </SafeAreaView>;
}

export function AdminTabs({ items, value, onChange }: {
  items: { key: string; label: string }[]; value: string; onChange: (key: string) => void;
}) {
  return <View style={styles.tabs}>
    {items.map(item => <TouchableRipple key={item.key} onPress={() => onChange(item.key)}
      accessibilityRole="tab" accessibilityState={{ selected: item.key === value }}
      style={[styles.tab, value === item.key && styles.tabSelected]}>
      <Text style={[styles.tabLabel, value === item.key && { color: C.action, fontWeight: "700" }]}>{item.label}</Text>
    </TouchableRipple>)}
  </View>;
}

export function StatusTag({ active }: { active: boolean }) {
  const { language } = useAppTranslation();
  return <View style={[styles.status, { backgroundColor: active ? "#ECFDF3" : "#FEF3F2" }]}>
    <Text style={{ color: active ? C.success : C.danger, fontSize: 12, fontWeight: "600" }}>
      {language === "zh" ? active ? "启用" : "停用" : active ? "Active" : "Disabled"}
    </Text>
  </View>;
}

/**
 * 搜索框。文字由输入框自己持有（见 useBufferedSearchText），上层 onChange 默认每个按键都收到；
 * 传 emitDelayMs 则停手后才通知上层（选人弹层用它，避免每个按键都重绘整个页面）。
 * 右侧「清除」图标位置始终占着、只是空输入时看不见：输入框的布局不再随「有没有字」变化。
 */
export function SearchField({ value, onChange, placeholder, emitDelayMs = 0 }: {
  value: string; onChange: (value: string) => void; placeholder: string; emitDelayMs?: number;
}) {
  const { text, handleChange, clear } = useBufferedSearchText(value, onChange, emitDelayMs);
  return <TextInput mode="outlined" value={text} onChangeText={handleChange} placeholder={placeholder}
    accessibilityLabel={placeholder} left={<TextInput.Icon icon="magnify" />} autoCapitalize="none"
    autoCorrect={false} dense style={styles.search} outlineStyle={{ borderRadius: 8, borderColor: C.outlineMuted }}
    returnKeyType="search"
    right={<TextInput.Icon icon="close" onPress={text ? clear : undefined} style={text ? undefined : styles.clearHidden} />} />;
}

export function AdminError({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const { language } = useAppTranslation();
  const zh = language === "zh";
  // 不渲染服务端原始响应，避免将调试详情或凭据带进管理页面。
  const status = (error as { response?: { status?: number }; status?: number } | null)?.response?.status
    ?? (error as { status?: number } | null)?.status;
  const code = (error as { code?: string } | null)?.code ?? "";
  const knownErrors: Record<string, string> = {
    USERNAME_EXISTS: zh ? "用户名已被使用，请更换后保存" : "This username is already in use. Choose another username.",
    EMAIL_EXISTS: zh ? "邮箱已被使用，请核对后保存" : "This email is already in use. Check the email address.",
    ROLE_NAME_EXISTS: zh ? "角色名称已存在，请更换后保存" : "This role name already exists. Choose another name.",
    ADMIN_REQUIRED: zh ? "此操作需要管理员权限" : "Administrator access is required.",
    SELF_PROFILE_FIELDS_DENIED: zh ? "不能修改本人用户名或账号状态" : "You cannot change your own username or account status here.",
  };
  return <View style={styles.error} accessibilityRole="alert">
    <Text style={{ color: C.danger }}>{knownErrors[code] ?? (status === 403 ? zh ? "无权执行此操作" : "You do not have permission for this action"
      : status === 404 ? zh ? "记录已不存在，请返回刷新" : "This record no longer exists. Refresh the list."
      : zh ? "请求未完成，请检查网络后刷新核对" : "Request not completed. Check your connection and refresh.")}</Text>
    {onRetry ? <Button onPress={onRetry}>{zh ? "刷新" : "Refresh"}</Button> : null}
  </View>;
}

export function AdminEmpty({ text }: { text: string }) {
  return <View style={styles.empty}><Icon source="magnify" size={28} color={C.textSecondary} /><Text style={styles.muted}>{text}</Text></View>;
}

export function AdminRow({ title, subtitle, trailing, onPress, icon }: {
  title: string; subtitle?: string; trailing?: ReactNode; onPress?: () => void; icon?: string;
}) {
  const content = <View style={styles.row}>
    {icon ? <View style={styles.rowIcon}><Icon source={icon} size={22} color={C.action} /></View> : null}
    <View style={{ flex: 1, gap: 4 }}><Text style={styles.value}>{title}</Text>{subtitle ? <Text style={styles.muted}>{subtitle}</Text> : null}</View>
    {trailing}{onPress ? <Icon source="chevron-right" size={20} color={C.textSecondary} /> : null}
  </View>;
  return onPress ? <TouchableRipple accessibilityRole="button" onPress={onPress}>{content}</TouchableRipple> : content;
}

export function AdminScroll({ children }: { children: ReactNode }) {
  return <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">{children}</ScrollView>;
}

export const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: C.surface },
  header: { flexDirection: "row", alignItems: "center", minHeight: 60, paddingRight: 12, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: C.outlineMuted },
  headerAction: { minWidth: 44, alignItems: "flex-end", justifyContent: "center" },
  title: { flex: 1, fontSize: 22, lineHeight: 30, fontWeight: "700", color: C.textPrimary },
  content: { padding: 16, gap: 16, paddingBottom: 24 },
  label: { fontSize: 14, color: C.textSecondary },
  value: { fontSize: 16, lineHeight: 23, fontWeight: "600", color: C.textPrimary },
  muted: { fontSize: 13, lineHeight: 19, color: C.textSecondary },
  section: { backgroundColor: C.white, borderTopWidth: StyleSheet.hairlineWidth, borderBottomWidth: StyleSheet.hairlineWidth, borderColor: C.outlineMuted },
  footer: { paddingHorizontal: 16, paddingVertical: 12, gap: 8, borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: C.outlineMuted, backgroundColor: C.white },
  button: { borderRadius: 8 },
  row: { flexDirection: "row", alignItems: "center", paddingHorizontal: 16, paddingVertical: 14, gap: 12, minHeight: 64, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: C.outlineMuted },
  rowIcon: { width: 36, height: 36, alignItems: "center", justifyContent: "center", borderRadius: 10, backgroundColor: "#EFF6FF" },
  tabs: { flexDirection: "row", marginHorizontal: 16, marginVertical: 12, borderWidth: 1, borderColor: C.outlineMuted, borderRadius: 8, overflow: "hidden" },
  tab: { flex: 1, minHeight: 44, alignItems: "center", justifyContent: "center", paddingHorizontal: 4, paddingVertical: 8 },
  tabSelected: { backgroundColor: "#EFF6FF", borderBottomWidth: 2, borderBottomColor: C.brand },
  tabLabel: { textAlign: "center", fontSize: 14, color: C.textSecondary },
  status: { paddingHorizontal: 7, paddingVertical: 4, borderRadius: 5 },
  search: { backgroundColor: C.white, fontSize: 14, minHeight: 44 },
  clearHidden: { opacity: 0 },
  error: { padding: 16, margin: 16, gap: 8, backgroundColor: "#FEF3F2", borderRadius: 8 },
  empty: { padding: 32, alignItems: "center", gap: 12 },
});

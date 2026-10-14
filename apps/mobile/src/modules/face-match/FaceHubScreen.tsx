// 「人脸比对」合并页：原「抓拍与比对 / 不受欢迎人员名单 / 人脸比对设置」三个入口合并成一个，进来是「抓拍 / 名单 / 设置」三个一级页签。
// 合并的是入口，不是权限：页签按各自权限显示（见 navigation/face-hub.ts 的 resolveFaceHubTabs），页签里的区域仍按各自的码显示。
// 红色数字 = 当前登录人还没看的「黑名单命中」通知数（来自 AppNavigationAccess.faceHitUnreadCount）：
// 挂在「抓拍」页签上；没有抓拍权限而有名单页签的人（持 Review）挂在「名单」页签上。
//
// 路由参数（均可选）：tab=capture|list|settings、sub=entries|review（名单页签的二级分段）、
// filter=blacklist、hitStatus=unread|pending|…（抓拍页签，透传给 CapturePanel 的 initialFilter / initialHitStatus）。
//
// 页签切换时各面板的处理（取舍）：
// - 抓拍：切走即卸载。抓拍页的缩略图只放内存、离开页面立即销毁（产品决定），不能因为「保留筛选」让照片字节留在隐藏的面板里；
//   代价是切回来筛选条件回到默认，列表数据由 react-query 缓存兜底，不会白屏。
// - 名单 / 设置：访问过一次后常驻（display: none 隐藏而不卸载），筛选、分页位置和设置页未保存的编辑值都保留；
//   设置页有未保存修改时，离开本页面仍由 SettingsPanel 里的 useLeaveGuard 拦截确认。
import { useCallback, useEffect, useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import { Text } from "react-native-paper";
import { useAppNavigationAccess } from "@/modules/navigation/access-context";
import {
  canViewFaceHub,
  parseFaceHubParams,
  resolveFaceHubActiveTab,
  resolveFaceHubBadgeTab,
  resolveFaceHubTabs,
  type FaceHubTabKey,
} from "@/modules/navigation/face-hub";
import { formatUnreadBadgeText } from "@/modules/navigation/primary-navigation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { CapturePanel } from "./FaceCaptureResultsScreen";
import { SettingsPanel } from "./FaceMatchSettingsScreen";
import { UnwelcomePanel } from "./FaceUnwelcomeScreen";
import { FaceScreenFrame, FaceScreenMessage } from "./ui";

/** 一级页签的文案键（页签名各自独立，不复用旧三个页面的标题）。 */
const TAB_LABEL_KEYS = {
  capture: "hub.tabs.capture",
  list: "hub.tabs.list",
  settings: "hub.tabs.settings",
} as const satisfies Record<FaceHubTabKey, string>;

/** 合并页入口守卫：审核模式与没有任何页签权限直接给出说明，不发请求。返回提示文案，null 表示放行。 */
function useFaceHubGuard(): string | null {
  const { t } = useAppTranslation("faceMatch");
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive) || sessionKind === "iosReview";
  if (review) return t("messages.reviewUnavailable");
  if (!canViewFaceHub(isAuthenticated, hasPermission, review)) return t("messages.notAllowed");
  return null;
}

export function FaceHubScreen() {
  const router = useRouter();
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const blocked = useFaceHubGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));
  if (blocked) return <FaceScreenMessage message={blocked} backLabel={t("common:actions.back")} onBack={goBack} />;
  // 账号变化时重建页面状态，避免把上一个账号的筛选、编辑值和结果留在屏幕上。
  return <FaceHubContent key={userGuid} onBack={goBack} />;
}

function FaceHubContent({ onBack }: { onBack: () => void }) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const { faceHitUnreadCount } = useAppNavigationAccess();
  const { tab, sub, filter, hitStatus } = useLocalSearchParams<{ tab?: string; sub?: string; filter?: string; hitStatus?: string }>();
  const params = useMemo(() => parseFaceHubParams({ tab, sub, filter, hitStatus }), [tab, sub, filter, hitStatus]);
  const tabs = useMemo(() => resolveFaceHubTabs(hasPermission), [hasPermission]);

  const [requestedTab, setRequestedTab] = useState<FaceHubTabKey | null>(() => resolveFaceHubActiveTab(tabs, params.tab));
  // 访问过的页签（含首次进入时的页签）：名单 / 设置访问过后常驻（隐藏不卸载）；抓拍切走即卸载，不依赖这个集合（见文件头说明）。
  const [visited, setVisited] = useState<ReadonlySet<FaceHubTabKey>>(() => new Set(requestedTab ? [requestedTab] : []));
  const activeTab = resolveFaceHubActiveTab(tabs, requestedTab);

  const selectTab = useCallback((next: FaceHubTabKey) => {
    setRequestedTab(next);
    setVisited((previous) => (previous.has(next) ? previous : new Set(previous).add(next)));
  }, []);

  // 路由参数变化（同一页面实例被深链 / 通知再次指向）时跟着切页签。
  useEffect(() => {
    if (params.tab) selectTab(params.tab);
  }, [params.tab, selectTab]);

  if (!activeTab) {
    // 守卫已保证至少有一个页签；权限在页面打开期间被收回时给出同一句无权限提示。
    return <FaceScreenMessage message={t("messages.notAllowed")} backLabel={t("common:actions.back")} onBack={onBack} />;
  }

  const badgeTab = resolveFaceHubBadgeTab(tabs);
  const renders = (key: FaceHubTabKey) => tabs.includes(key) && (key === activeTab || (key !== "capture" && visited.has(key)));
  const panelStyle = (key: FaceHubTabKey) => [styles.panel, key === activeTab ? null : styles.hidden];

  return (
    <FaceScreenFrame title={t("hub.title")} onBack={onBack}>
      <View style={styles.strip} accessibilityRole="tablist">
        {tabs.map((key) => {
          const selected = key === activeTab;
          const label = t(TAB_LABEL_KEYS[key]);
          const badgeText = badgeTab === key ? formatUnreadBadgeText(faceHitUnreadCount) : null;
          return (
            <Pressable
              key={key}
              accessibilityRole="tab"
              accessibilityLabel={badgeText ? t("hub.unreadBadge", { label, count: faceHitUnreadCount }) : label}
              accessibilityState={{ selected }}
              onPress={() => selectTab(key)}
              style={styles.tab}
            >
              <Text style={[styles.tabText, selected ? styles.tabTextOn : null]} numberOfLines={1}>
                {label}
              </Text>
              {badgeText ? (
                <View style={styles.badge}>
                  <Text allowFontScaling={false} style={styles.badgeText}>
                    {badgeText}
                  </Text>
                </View>
              ) : null}
              {selected ? <View style={styles.indicator} /> : null}
            </Pressable>
          );
        })}
      </View>
      <View style={styles.body}>
        {renders("capture") ? (
          <View style={panelStyle("capture")}>
            {/* 筛选 / 状态参数变化时以它们作 key 重建面板（CapturePanel 只读取初始值）。 */}
            <CapturePanel key={`${params.filter ?? ""}|${params.hitStatus ?? ""}`} initialFilter={params.filter} initialHitStatus={params.hitStatus} />
          </View>
        ) : null}
        {renders("list") ? (
          <View style={panelStyle("list")}>
            <UnwelcomePanel initialTab={params.sub} />
          </View>
        ) : null}
        {renders("settings") ? (
          <View style={panelStyle("settings")}>
            <SettingsPanel />
          </View>
        ) : null}
      </View>
    </FaceScreenFrame>
  );
}

const styles = StyleSheet.create({
  // 一级页签条（设计稿 02）：整宽均分，选中项粗体蓝字 + 底部 3px 蓝条（宽 30），红色数字徽标紧跟文字。
  strip: {
    flexDirection: "row",
    marginBottom: HB_SPACING.xs,
    backgroundColor: HB_COLORS.surface,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  tab: {
    flex: 1,
    minHeight: 46,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 6,
    paddingHorizontal: HB_SPACING.xs,
  },
  tabText: { fontSize: 16, color: HB_COLORS.textSecondary },
  tabTextOn: { fontWeight: "700", color: HB_COLORS.action },
  indicator: {
    position: "absolute",
    bottom: -StyleSheet.hairlineWidth,
    alignSelf: "center",
    width: 30,
    height: 3,
    borderRadius: 2,
    backgroundColor: HB_COLORS.brand,
  },
  // 红色数字徽标：红底白字（#D92D20）、11px 加粗、最小 18×18、99+ 封顶。
  badge: {
    minWidth: 18,
    height: 18,
    paddingHorizontal: 5,
    borderRadius: 9,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "#D92D20",
  },
  badgeText: {
    color: HB_COLORS.white,
    fontSize: 11,
    lineHeight: 14,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
    includeFontPadding: false,
  },
  body: { flex: 1 },
  panel: { flex: 1 },
  // 隐藏而不卸载：保留面板内的筛选、分页位置与未保存的编辑值。
  hidden: { display: "none" },
});

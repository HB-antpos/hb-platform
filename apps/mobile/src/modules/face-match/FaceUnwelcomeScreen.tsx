// 「不受欢迎人员名单」入口页：名单条目 / 命中复核两个分区。
// 分区与按钮由 GET /face-match/config 返回的权限标志精确控制（入口本身只要求名单 Manage / Approve / Review 任一权限）。
import { useEffect, useMemo, useState } from "react";
import { StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { ActivityIndicator, Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { getVisibleUnwelcomeTabs, type FaceUnwelcomeTabKey } from "./access";
import { getDisabledReasonKey } from "./errors";
import { EntriesPanel } from "./EntriesPanel";
import { ReviewPanel } from "./ReviewPanel";
import { useFaceErrorMessage, useFaceUnwelcomeGuard } from "./shared";
import { useFaceMatchConfig, useFaceStoreOptions } from "./use-face-match";
import { FaceScreenFrame, FaceScreenMessage, Notice, SegmentTabs, faceStyles } from "./ui";

export function FaceUnwelcomeScreen() {
  const router = useRouter();
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const blocked = useFaceUnwelcomeGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));
  if (blocked) return <FaceScreenMessage message={blocked} backLabel={t("common:actions.back")} onBack={goBack} />;
  // 账号变化时重建页面状态，避免把上一个账号的筛选与结果留在屏幕上。
  return <FaceUnwelcomeContent key={userGuid} onBack={goBack} />;
}

function FaceUnwelcomeContent({ onBack }: { onBack: () => void }) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const describeError = useFaceErrorMessage();
  const configQuery = useFaceMatchConfig(true);
  const { stores } = useFaceStoreOptions();
  const config = configQuery.data;
  const tabs = useMemo(() => (config ? getVisibleUnwelcomeTabs(config.permissions) : []), [config]);
  const [tab, setTab] = useState<FaceUnwelcomeTabKey>("entries");
  const activeTab: FaceUnwelcomeTabKey = tabs.includes(tab) ? tab : (tabs[0] ?? "entries");

  // 权限标志变化后当前分区不再可见时，退回第一个可见分区。
  useEffect(() => {
    if (tabs.length > 0 && !tabs.includes(tab)) setTab(tabs[0]);
  }, [tab, tabs]);

  let body;
  if (configQuery.isPending) {
    body = (
      <View style={faceStyles.state}>
        <ActivityIndicator color={HB_COLORS.brand} />
        <Text style={faceStyles.muted}>{t("states.loading")}</Text>
      </View>
    );
  } else if (!config) {
    body = (
      <View style={faceStyles.state}>
        <EmptyState
          title={t("states.loadFailed")}
          description={describeError(configQuery.error)}
          actionLabel={t("common:actions.retry")}
          onAction={() => void configQuery.refetch()}
        />
      </View>
    );
  } else if (tabs.length === 0) {
    body = (
      <View style={faceStyles.state}>
        <EmptyState title={t("messages.noTabPermission")} />
      </View>
    );
  } else {
    body = (
      <>
        {!config.enabled ? (
          <Notice
            tone="warning"
            style={styles.notice}
            text={t("disabled.notice", { reason: t(getDisabledReasonKey(config.disabledReason)) })}
          />
        ) : null}
        {tabs.length > 1 ? (
          <View style={styles.tabs}>
            <SegmentTabs
              value={activeTab}
              options={tabs.map((key) => ({ key, label: t(key === "entries" ? "tabs.entries" : "tabs.review") }))}
              onChange={setTab}
            />
          </View>
        ) : null}
        {activeTab === "entries" ? <EntriesPanel config={config} stores={stores} /> : <ReviewPanel config={config} stores={stores} />}
      </>
    );
  }

  return (
    <FaceScreenFrame title={t("unwelcome.title")} onBack={onBack}>
      {body}
    </FaceScreenFrame>
  );
}

const styles = StyleSheet.create({
  notice: { marginHorizontal: HB_SPACING.md, marginBottom: HB_SPACING.xs },
  tabs: { marginBottom: HB_SPACING.xs },
});

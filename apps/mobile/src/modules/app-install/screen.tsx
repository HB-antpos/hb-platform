import { useState } from "react";
import {
  Linking,
  Platform,
  RefreshControl,
  ScrollView,
  Share,
  StyleSheet,
  useWindowDimensions,
  View,
} from "react-native";
import * as Clipboard from "expo-clipboard";
import { useQuery } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { ActivityIndicator, Button, Portal, Snackbar, Text } from "react-native-paper";
import QRCode from "react-native-qrcode-svg";
import { SafeAreaView } from "react-native-safe-area-context";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { formatDateTime } from "@/modules/app-downloads/copy";
import {
  Panel,
  PrimaryButton,
  SecondaryButton,
  SegmentedControl,
  ui,
} from "@/modules/app-downloads/ui";
import { apiClient } from "@/shared/api/client";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { canViewAppInstall } from "./access";
import { getAppInstallLinks } from "./api";
import {
  defaultInstallPlatform,
  formatArtifactSize,
  resolveAndroidInstallUrl,
  type AppInstallLinks,
  type AppInstallPlatform,
} from "./logic";

interface InstallTarget {
  url: string;
  version: string;
  build: string | null;
  source: string;
  meta: string[];
  hint: string;
}

function resolveTarget(
  data: AppInstallLinks | undefined,
  platform: AppInstallPlatform,
  t: (key: string, values?: Record<string, string>) => string,
): InstallTarget | null {
  if (platform === "ios") {
    const ios = data?.ios;
    if (!ios) return null;
    return {
      url: ios.appStoreUrl,
      version: ios.version,
      build: ios.buildNumber || null,
      source: t("labels.iosSource"),
      meta: ios.verifiedAt
        ? [t("labels.verifiedAt", { time: formatDateTime(ios.verifiedAt) })]
        : [],
      hint: t("hints.ios"),
    };
  }
  const android = data?.android;
  if (!android) return null;
  const size = formatArtifactSize(android.artifactSize);
  return {
    url: resolveAndroidInstallUrl(android, apiClient.defaults.baseURL),
    version: android.appVersion ?? "—",
    build: android.appBuildVersion,
    source: t("labels.androidSource"),
    meta: [
      ...(android.completedAt
        ? [t("labels.builtAt", { time: formatDateTime(android.completedAt) })]
        : []),
      ...(size ? [t("labels.size", { size })] : []),
    ],
    hint: t("hints.android"),
  };
}

export function AppInstallScreen() {
  const { t } = useAppTranslation("appInstall");
  const router = useRouter();
  const { width } = useWindowDimensions();
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const isReview = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const allowed = canViewAppInstall(isAuthenticated, hasPermission, isReview);
  const [platform, setPlatform] = useState<AppInstallPlatform>(() =>
    defaultInstallPlatform(Platform.OS),
  );
  const [snack, setSnack] = useState("");
  const query = useQuery({
    queryKey: ["app-install-links"],
    queryFn: getAppInstallLinks,
    enabled: allowed,
  });
  const goBack = () =>
    router.canGoBack() ? router.back() : router.replace("/(shell)/workbench");
  const target = resolveTarget(query.data, platform, t);
  // 二维码尽量大，方便隔着柜台让别人扫；窄屏按宽度收缩。
  const qrSize = Math.min(240, Math.max(168, width - 150));
  const fail = () => setSnack(t("messages.actionFailed"));

  let body;
  if (!allowed) {
    body = <Text style={styles.centerText}>{t("messages.notAllowed")}</Text>;
  } else if (query.isLoading) {
    body = (
      <View style={styles.center}>
        <ActivityIndicator color={HB_COLORS.action} />
        <Text style={ui.muted}>{t("states.loading")}</Text>
      </View>
    );
  } else if (query.isError) {
    body = (
      <View style={styles.center}>
        <Text style={ui.error}>{t("states.loadFailed")}</Text>
        <Button onPress={() => void query.refetch()}>{t("actions.retry")}</Button>
      </View>
    );
  } else if (!target) {
    body = (
      <Panel dashed>
        <View style={[ui.cardBody, styles.emptyBody]}>
          <MaterialCommunityIcons
            name="package-variant"
            size={32}
            color={HB_COLORS.textSecondary}
          />
          <Text style={ui.muted}>
            {platform === "ios" ? t("states.iosEmpty") : t("states.androidEmpty")}
          </Text>
        </View>
      </Panel>
    );
  } else {
    body = (
      <Panel>
        <View style={[ui.cardBody, styles.cardBody]}>
          <View style={styles.headRow}>
            <MaterialCommunityIcons
              name={platform === "ios" ? "apple" : "android"}
              size={22}
              color={HB_COLORS.action}
            />
            <Text style={styles.source}>{target.source}</Text>
          </View>
          <View style={styles.qrBox} accessibilityLabel={target.url}>
            <QRCode
              value={target.url}
              size={qrSize}
              backgroundColor="#FFFFFF"
              color="#101828"
            />
          </View>
          <View style={styles.versionBlock}>
            <Text style={styles.version}>
              {t("labels.version", { version: target.version })}
            </Text>
            {target.build ? (
              <Text style={ui.muted}>{t("labels.build", { build: target.build })}</Text>
            ) : null}
            {target.meta.map((line) => (
              <Text key={line} style={ui.caption}>
                {line}
              </Text>
            ))}
          </View>
          <Text style={styles.hint}>{target.hint}</Text>
          <PrimaryButton
            icon="open-in-new"
            label={t("actions.openHere")}
            onPress={() => void Linking.openURL(target.url).catch(fail)}
          />
          <View style={styles.actionRow}>
            <SecondaryButton
              icon="content-copy"
              label={t("actions.copy")}
              style={styles.action}
              onPress={() =>
                void Clipboard.setStringAsync(target.url)
                  .then(() => setSnack(t("messages.copied")))
                  .catch(fail)
              }
            />
            <SecondaryButton
              icon="share-variant"
              label={t("actions.share")}
              style={styles.action}
              onPress={() => void Share.share({ message: target.url }).catch(fail)}
            />
          </View>
        </View>
      </Panel>
    );
  }

  return (
    <SafeAreaView style={styles.safe} edges={["top"]}>
      <View style={styles.header}>
        <Button
          compact
          onPress={goBack}
          icon="chevron-left"
          labelStyle={styles.backLabel}
        >
          {t("actions.back")}
        </Button>
        <Text accessibilityRole="header" style={styles.title}>
          {t("title")}
        </Text>
        <View style={styles.headerSpacer} />
      </View>
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={
          allowed ? (
            <RefreshControl
              refreshing={query.isRefetching}
              onRefresh={() => void query.refetch()}
            />
          ) : undefined
        }
      >
        {allowed ? (
          <>
            <Text style={ui.muted}>{t("subtitle")}</Text>
            <SegmentedControl
              tone="strong"
              options={[
                { value: "ios", label: t("platforms.ios") },
                { value: "android", label: t("platforms.android") },
              ]}
              value={platform}
              onChange={setPlatform}
            />
          </>
        ) : null}
        {body}
      </ScrollView>
      <Portal>
        <Snackbar visible={Boolean(snack)} onDismiss={() => setSnack("")} duration={2000}>
          {snack}
        </Snackbar>
      </Portal>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  header: {
    minHeight: 56,
    backgroundColor: HB_COLORS.white,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    borderBottomWidth: 1,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  backLabel: { color: HB_COLORS.action, fontSize: 16 },
  title: { fontSize: 18, lineHeight: 26, fontWeight: "700", color: HB_COLORS.textPrimary },
  headerSpacer: { width: 80 },
  content: { padding: HB_SPACING.md, gap: HB_SPACING.md },
  center: { alignItems: "center", justifyContent: "center", padding: 48, gap: 12 },
  centerText: { textAlign: "center", padding: 48, color: HB_COLORS.textSecondary },
  emptyBody: { alignItems: "center", paddingVertical: HB_SPACING.xl },
  cardBody: { gap: HB_SPACING.md },
  headRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  source: { fontSize: 15, lineHeight: 22, fontWeight: "600", color: HB_COLORS.textPrimary },
  qrBox: {
    alignSelf: "center",
    padding: 14,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.white,
  },
  versionBlock: { alignItems: "center", gap: 2 },
  version: { fontSize: 20, lineHeight: 28, fontWeight: "700", color: HB_COLORS.textPrimary },
  hint: {
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
    fontSize: 13,
    lineHeight: 20,
    color: HB_COLORS.textSecondary,
  },
  actionRow: { flexDirection: "row", gap: HB_SPACING.sm },
  action: { flex: 1 },
});

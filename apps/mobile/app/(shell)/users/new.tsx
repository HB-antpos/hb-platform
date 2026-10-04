import { useCallback, useMemo, useState } from "react";
import { KeyboardAvoidingView, Platform, ScrollView, StyleSheet, View } from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import * as Clipboard from "expo-clipboard";
import * as Crypto from "expo-crypto";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, Chip, HelperText, IconButton, Snackbar, Switch, Text, TextInput } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";

import { EmptyState } from "@/components/ui/EmptyState";
import { getDeviceBoundStoreCode } from "@/modules/shop/device-bound-store-filter";
import { getManageableStoresForSession, getPosEnabledStores, isStoreManageable } from "@/modules/shop/store-scope";
import { useStores } from "@/modules/shop/use-stores";
import {
  STORE_STAFF_ROLE,
  toSafeStoreUserErrorLog,
  useStoreUserMutations,
  type StoreUserListItem,
} from "@/modules/users";
import { buildLoginCredentialText, generateInitialPassword } from "@/modules/users/initial-password";
import { StaffBarcodeDialog } from "@/modules/users/staff-barcode/StaffBarcodeDialogs";
import { canManageStaffBarcode } from "@/modules/users/staff-barcode/eligibility";
import { validateNewStaffPhone, validatePasswordValue, validateStoreUserForm } from "@/modules/users/validation";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { PERMISSIONS } from "@/shared/utils/access";
import { useAuthStore } from "@/store/auth-store";

const createPassword = () => generateInitialPassword((count) => Crypto.getRandomBytes(count));

interface CreatedCredential {
  user: StoreUserListItem;
  password: string;
}

/**
 * 店长新建本店店员（整页）。成功后在同一页展示一次性登录信息，离开即无法再查看初始密码。
 * 账号固定为本店店员、临时工，前后端都强制；页面只做只读说明，不提供角色或分店选择。
 */
export default function CreateStaffScreen() {
  const router = useRouter();
  const params = useLocalSearchParams<{ storeCode?: string }>();
  const requestedStoreCode = typeof params.storeCode === "string" ? params.storeCode : "";
  const { t, language } = useAppTranslation(["userManagement", "common"]);
  const access = useAuthStore((state) => state.access);
  const currentUser = useAuthStore((state) => state.user);
  const authenticated = useAuthStore((state) => state.isAuthenticated);
  const actorGuid = currentUser?.userGUID || currentUser?.userGuid || "";
  const { stores, selectedStoreCode, isDeviceMode } = useStores();

  const [username, setUsername] = useState("");
  const [password, setPassword] = useState(createPassword);
  const [passwordVisible, setPasswordVisible] = useState(true);
  const [requirePasswordChange, setRequirePasswordChange] = useState(true);
  const [fullName, setFullName] = useState("");
  const [phone, setPhone] = useState("");
  const [email, setEmail] = useState("");
  const [enabled, setEnabled] = useState(true);
  const [errorMessage, setErrorMessage] = useState("");
  const [snackbarMessage, setSnackbarMessage] = useState("");
  const [created, setCreated] = useState<CreatedCredential | null>(null);
  const [barcodeVisible, setBarcodeVisible] = useState(false);

  const canCreateUsers = access.isAdmin
    || access.hasPermission(PERMISSIONS.Users.Create)
    || access.hasPermission(PERMISSIONS.Users.CreateStoreStaff);
  const canEditUsers = access.isAdmin
    || access.hasPermission(PERMISSIONS.Users.Edit)
    || access.hasPermission(PERMISSIONS.Users.EditStoreStaff);
  const deviceBoundStoreCode = getDeviceBoundStoreCode({ isDeviceMode, selectedStoreCode });
  const manageableStores = useMemo(() => getManageableStoresForSession({
    stores,
    isDeviceMode,
    deviceBoundStore: deviceBoundStoreCode ? stores.find((store) => store.storeCode === deviceBoundStoreCode) ?? null : null,
    isAdmin: access.isAdmin,
  }), [access.isAdmin, deviceBoundStoreCode, isDeviceMode, stores]);
  const posEnabledStores = useMemo(() => getPosEnabledStores(stores), [stores]);
  const store = stores.find((item) => item.storeCode === requestedStoreCode) ?? null;
  const storeAllowed = canCreateUsers && Boolean(requestedStoreCode) && isStoreManageable(requestedStoreCode, manageableStores);
  const { createMutation } = useStoreUserMutations(requestedStoreCode, "");

  const handleClose = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.replace("/(shell)/users" as unknown as Parameters<typeof router.replace>[0]);
  }, [router]);

  const handleSubmit = async () => {
    setErrorMessage("");
    const formMessage = validateStoreUserForm({ username, fullName, email, phone, status: enabled }, t)
      ?? validatePasswordValue(password, t)
      ?? validateNewStaffPhone(phone, t);
    if (formMessage) {
      setErrorMessage(formMessage);
      return;
    }
    try {
      const user = await createMutation.mutateAsync({
        username: username.trim(),
        fullName: fullName.trim() || undefined,
        email: email.trim() || undefined,
        phone: phone.trim() || undefined,
        status: enabled ? 1 : 0,
        storeCode: requestedStoreCode,
        roleNames: [STORE_STAFF_ROLE],
        password: password.trim(),
        passwordFormat: "raw",
        employmentType: "casual",
        requirePasswordChange,
      });
      // 关键逻辑：初始密码只保存在本页内存，用于这一次交付；离开页面后无法再次查看。
      setCreated({ user, password: password.trim() });
    } catch (error) {
      console.warn("[store-users] save failed", toSafeStoreUserErrorLog(error));
      setErrorMessage(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "messages.saveFailed" }));
    }
  };

  const copyText = async (text: string, messageKey: string) => {
    await Clipboard.setStringAsync(text);
    setSnackbarMessage(t(messageKey));
  };

  if (!storeAllowed) {
    return (
      <SafeAreaView style={styles.screen} edges={["top", "left", "right"]}>
        <EmptyState
          title={t("messages.storeReadOnly")}
          description={t("currentStore.readOnlyHelper")}
          primaryAction={{ label: t("common:actions.back"), icon: "arrow-left", onPress: handleClose }}
        />
      </SafeAreaView>
    );
  }

  if (created) {
    const createdName = created.user.fullName || created.user.username;
    const canPrintBarcode = canManageStaffBarcode({
      authenticated,
      deviceOnly: isDeviceMode,
      canEditUsers,
      canManagePosStore: isStoreManageable(requestedStoreCode, manageableStores)
        && posEnabledStores.some((item) => item.storeCode === requestedStoreCode),
      actorGuid,
      actorRoles: currentUser?.roleNames ?? [],
      targetGuid: created.user.userGUID,
      targetStatus: created.user.status,
      targetRoles: created.user.roleNames,
    });
    const credentialText = buildLoginCredentialText({
      heading: t("create.credentialHeading"),
      usernameLabel: t("fields.username"),
      passwordLabel: t("fields.initialPassword"),
      username: created.user.username,
      password: created.password,
      footer: requirePasswordChange ? t("create.credentialFooterForced") : t("create.credentialFooter"),
    });

    return (
      <SafeAreaView style={styles.screen} edges={["top", "left", "right", "bottom"]}>
        <View style={styles.header}>
          <IconButton icon="close" size={22} accessibilityLabel={t("create.done")} onPress={handleClose} />
          <Text variant="titleLarge" style={styles.headerTitle}>{t("create.successTitle")}</Text>
          <View style={styles.headerSpacer} />
        </View>
        <ScrollView contentContainerStyle={styles.content}>
          <View style={styles.successIntro}>
            <View style={styles.successIcon}>
              <MaterialCommunityIcons name="check" size={32} color={HB_COLORS.success} />
            </View>
            <Text variant="headlineSmall" style={styles.successTitle}>{t("create.successHeadline")}</Text>
            <Text variant="bodyMedium" style={styles.centerSecondary}>{t("create.successDescription", { name: createdName })}</Text>
          </View>

          <View style={styles.card}>
            <View style={styles.credentialRow}>
              <Text variant="bodyMedium" style={styles.rowLabel}>{t("fields.username")}</Text>
              <Text variant="titleMedium" style={styles.credentialValue} selectable>{created.user.username}</Text>
              <IconButton icon="content-copy" size={20} accessibilityLabel={t("create.copyUsername")} onPress={() => void copyText(created.user.username, "create.copiedUsername")} />
            </View>
            <View style={[styles.credentialRow, styles.rowDivider]}>
              <Text variant="bodyMedium" style={styles.rowLabel}>{t("fields.initialPassword")}</Text>
              <Text variant="titleMedium" style={styles.credentialValue} selectable>{created.password}</Text>
              <IconButton icon="content-copy" size={20} accessibilityLabel={t("create.copyPassword")} onPress={() => void copyText(created.password, "create.copiedPassword")} />
            </View>
            <View style={[styles.credentialRow, styles.rowDivider]}>
              <Text variant="bodyMedium" style={styles.rowLabel}>{t("create.assignment")}</Text>
              <Text variant="bodyMedium" style={styles.flex}>{t("create.assignmentValue", { store: store?.storeName || requestedStoreCode })}</Text>
            </View>
          </View>

          <View style={styles.warning}>
            <MaterialCommunityIcons name="alert-outline" size={20} color="#7A2E0E" />
            <Text variant="bodySmall" style={styles.warningText}>{t("create.passwordOnceWarning")}</Text>
          </View>

          {requirePasswordChange ? (
            <View style={styles.card}>
              <Text variant="titleSmall" style={styles.cardTitle}>{t("create.nextStepsTitle")}</Text>
              {["create.nextStepPassword", "create.nextStepBasic", "create.nextStepSensitive"].map((key, index) => (
                <View key={key} style={styles.stepRow}>
                  <View style={styles.stepBadge}><Text variant="labelSmall" style={styles.stepBadgeText}>{index + 1}</Text></View>
                  <Text variant="bodyMedium" style={styles.flex}>{t(key)}</Text>
                </View>
              ))}
            </View>
          ) : null}
        </ScrollView>
        <View style={styles.footerColumn}>
          <Button mode="contained-tonal" icon="content-copy" onPress={() => void copyText(credentialText, "create.copiedAll")} contentStyle={styles.buttonContent}>
            {t("create.copyAll")}
          </Button>
          <View style={styles.footerRow}>
            {canPrintBarcode ? (
              <Button mode="outlined" icon="printer-outline" onPress={() => setBarcodeVisible(true)} style={styles.flex} contentStyle={styles.buttonContent}>
                {t("create.printBarcode")}
              </Button>
            ) : null}
            <Button mode="contained" buttonColor={HB_COLORS.action} onPress={handleClose} style={styles.flex} contentStyle={styles.buttonContent}>
              {t("create.done")}
            </Button>
          </View>
        </View>
        <StaffBarcodeDialog
          actorGuid={actorGuid}
          storeCode={requestedStoreCode}
          user={barcodeVisible ? created.user : null}
          visible={barcodeVisible}
          onDismiss={() => setBarcodeVisible(false)}
        />
        <Snackbar visible={Boolean(snackbarMessage)} onDismiss={() => setSnackbarMessage("")} duration={2000}>{snackbarMessage}</Snackbar>
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView style={styles.screen} edges={["top", "left", "right", "bottom"]}>
      <View style={styles.header}>
        <IconButton icon="close" size={22} accessibilityLabel={t("actions.cancel")} onPress={handleClose} />
        <Text variant="titleLarge" style={styles.headerTitle}>{t("dialogs.createTitle")}</Text>
        <View style={styles.headerSpacer} />
      </View>
      <KeyboardAvoidingView style={styles.flex} behavior={Platform.OS === "ios" ? "padding" : undefined}>
        <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
          <Text variant="labelLarge" style={styles.sectionTitle}>{t("create.assignmentSection")}</Text>
          <View style={styles.card}>
            <View style={styles.infoRow}>
              <Text variant="bodyMedium" style={styles.rowLabel}>{t("create.store")}</Text>
              <Text variant="bodyMedium" style={styles.flex}>{store?.storeName || requestedStoreCode}</Text>
              <Chip compact style={styles.storeChip}>{t("currentStore.manageableBadge")}</Chip>
            </View>
            <View style={[styles.infoRow, styles.rowDivider]}>
              <Text variant="bodyMedium" style={styles.rowLabel}>{t("create.role")}</Text>
              <Text variant="bodyMedium" style={styles.flex}>{t("create.roleValue")}</Text>
            </View>
            <View style={[styles.infoRow, styles.rowDivider]}>
              <Text variant="bodyMedium" style={styles.rowLabel}>{t("create.employmentType")}</Text>
              <Text variant="bodyMedium" style={styles.flex}>{t("detail.employmentTypes.casual")}</Text>
            </View>
            <Text variant="bodySmall" style={styles.cardHint}>{t("create.assignmentHint")}</Text>
          </View>

          <Text variant="labelLarge" style={styles.sectionTitle}>{t("create.loginSection")}</Text>
          <View style={[styles.card, styles.cardPadded]}>
            <TextInput
              mode="outlined"
              label={t("fields.username")}
              value={username}
              onChangeText={setUsername}
              autoCapitalize="none"
              autoCorrect={false}
              maxLength={50}
            />
            <HelperText type="info" visible style={styles.helper}>{t("create.usernameHelper")}</HelperText>
            <TextInput
              mode="outlined"
              label={t("fields.initialPassword")}
              value={password}
              onChangeText={setPassword}
              secureTextEntry={!passwordVisible}
              autoCapitalize="none"
              autoCorrect={false}
              maxLength={100}
              style={styles.monoInput}
              right={<TextInput.Icon icon={passwordVisible ? "eye-off-outline" : "eye-outline"} accessibilityLabel={t("create.togglePassword")} onPress={() => setPasswordVisible((value) => !value)} />}
            />
            <View style={styles.passwordHelperRow}>
              <HelperText type="info" visible style={[styles.helper, styles.flex]}>{t("create.passwordHelper")}</HelperText>
              <Button compact mode="text" icon="refresh" onPress={() => setPassword(createPassword())}>{t("create.regeneratePassword")}</Button>
            </View>
            <View style={styles.switchRow}>
              <View style={styles.flex}>
                <Text variant="bodyLarge">{t("create.requirePasswordChange")}</Text>
                <Text variant="bodySmall" style={styles.secondary}>{t("create.requirePasswordChangeHelper")}</Text>
              </View>
              <Switch value={requirePasswordChange} onValueChange={setRequirePasswordChange} />
            </View>
          </View>

          <Text variant="labelLarge" style={styles.sectionTitle}>{t("create.contactSection")}</Text>
          <View style={[styles.card, styles.cardPadded]}>
            <TextInput mode="outlined" label={t("fields.fullName")} value={fullName} onChangeText={setFullName} maxLength={100} />
            <TextInput mode="outlined" label={t("fields.phone")} value={phone} onChangeText={setPhone} keyboardType="phone-pad" placeholder="04xx xxx xxx" />
            <HelperText type="info" visible style={styles.helper}>{t("create.phoneHelper")}</HelperText>
            <TextInput mode="outlined" label={t("fields.email")} value={email} onChangeText={setEmail} keyboardType="email-address" autoCapitalize="none" autoCorrect={false} placeholder={t("create.emailPlaceholder")} />
          </View>

          <View style={[styles.card, styles.switchCard]}>
            <View style={styles.flex}>
              <Text variant="bodyLarge">{t("create.enableNow")}</Text>
              <Text variant="bodySmall" style={styles.secondary}>{t("create.enableNowHelper")}</Text>
            </View>
            <Switch value={enabled} onValueChange={setEnabled} />
          </View>

          <HelperText type="error" visible={Boolean(errorMessage)}>{errorMessage}</HelperText>
        </ScrollView>
      </KeyboardAvoidingView>
      <View style={styles.footerRowBar}>
        <Button mode="outlined" onPress={handleClose} disabled={createMutation.isPending} style={styles.cancelButton} contentStyle={styles.buttonContent}>
          {t("actions.cancel")}
        </Button>
        <Button mode="contained" buttonColor={HB_COLORS.action} onPress={() => void handleSubmit()} loading={createMutation.isPending} disabled={createMutation.isPending} style={styles.flex} contentStyle={styles.buttonContent}>
          {t("create.submit")}
        </Button>
      </View>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: HB_COLORS.background },
  flex: { flex: 1 },
  header: {
    minHeight: 52,
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  headerTitle: { flex: 1, textAlign: "center", fontWeight: "700", color: HB_COLORS.textPrimary },
  headerSpacer: { width: 48 },
  content: { padding: HB_SPACING.md, gap: HB_SPACING.sm, paddingBottom: HB_SPACING.xl },
  sectionTitle: { color: HB_COLORS.textSecondary, marginTop: HB_SPACING.xs, marginLeft: HB_SPACING.xxs },
  card: {
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.surface,
    borderColor: HB_COLORS.outlineMuted,
    borderWidth: StyleSheet.hairlineWidth,
  },
  cardPadded: { padding: HB_SPACING.md, gap: HB_SPACING.xs },
  cardTitle: { fontWeight: "700", padding: HB_SPACING.md, paddingBottom: HB_SPACING.xs },
  cardHint: { color: HB_COLORS.textSecondary, paddingHorizontal: HB_SPACING.md, paddingBottom: HB_SPACING.sm },
  infoRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm, minHeight: 48, paddingHorizontal: HB_SPACING.md },
  rowDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted },
  rowLabel: { width: 80, color: HB_COLORS.textSecondary },
  storeChip: { backgroundColor: "#EAF2FF" },
  helper: { paddingHorizontal: 0, marginTop: -2 },
  passwordHelperRow: { flexDirection: "row", alignItems: "center" },
  monoInput: { fontFamily: Platform.select({ ios: "Menlo", default: "monospace" }) },
  switchRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingTop: HB_SPACING.sm,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  switchCard: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm, padding: HB_SPACING.md },
  secondary: { color: HB_COLORS.textSecondary },
  footerRowBar: {
    flexDirection: "row",
    gap: HB_SPACING.sm,
    padding: HB_SPACING.md,
    backgroundColor: HB_COLORS.white,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  footerColumn: {
    gap: HB_SPACING.sm,
    padding: HB_SPACING.md,
    backgroundColor: HB_COLORS.white,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  footerRow: { flexDirection: "row", gap: HB_SPACING.sm },
  cancelButton: { minWidth: 104 },
  buttonContent: { minHeight: 48 },
  successIntro: { alignItems: "center", gap: HB_SPACING.xs, paddingVertical: HB_SPACING.sm },
  successIcon: {
    width: 64,
    height: 64,
    borderRadius: 32,
    backgroundColor: "#E7F6EC",
    alignItems: "center",
    justifyContent: "center",
  },
  successTitle: { fontWeight: "700", color: HB_COLORS.textPrimary },
  centerSecondary: { color: HB_COLORS.textSecondary, textAlign: "center" },
  credentialRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm, minHeight: 52, paddingLeft: HB_SPACING.md, paddingRight: HB_SPACING.xxs },
  credentialValue: { flex: 1, fontWeight: "700", fontFamily: Platform.select({ ios: "Menlo", default: "monospace" }) },
  warning: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: "#FFFAEB",
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: "#FEDF89",
  },
  warningText: { flex: 1, color: "#7A2E0E" },
  stepRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm, paddingHorizontal: HB_SPACING.md, paddingBottom: HB_SPACING.sm },
  stepBadge: { width: 22, height: 22, borderRadius: 11, backgroundColor: "#EAF2FF", alignItems: "center", justifyContent: "center" },
  stepBadgeText: { color: "#073B83", fontWeight: "700" },
});

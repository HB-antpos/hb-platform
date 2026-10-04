import { useEffect, useMemo, useRef, useState } from "react";
import { Alert, ScrollView, View } from "react-native";
import { useLocalSearchParams, useNavigation, useRouter } from "expo-router";
import { usePreventRemove } from "@react-navigation/native";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ActivityIndicator, Avatar, Button, Checkbox, Dialog, Portal, Snackbar, Switch, Text, TextInput, TouchableRipple } from "react-native-paper";
import { isStoreManagerRoleName } from "@/modules/users/access-management";
import { HB_COLORS as C } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { assignUserAccessStores, createIdentityUser, fetchAccessRoleCatalog, fetchAccessStoreCatalog, fetchIdentityUserDetail, fetchIdentityUsers, fetchUserAccessStores, getIdentityAdminErrorMeta, updateIdentityUser, updateIdentityUserPassword } from "./api";
import { AdminEmpty, AdminError, AdminRow, AdminScreen, AdminTabs, SearchField, StatusTag, styles } from "./ui";
import { canModifyIdentityUser, EMPTY_IDENTITY_USER_FORM, isUncertainIdentityWrite, managedIdentityStores, sameIdentityUserForm, selectableIdentityRoles, validateIdentityUserForm, type IdentityUserForm } from "./user-logic";
import { identityDate, useIdentitySession, useIdentityUserCopy } from "./user-hooks";
import type { IdentityUserDetail } from "./types";
import { IdentitySelectionSheet } from "./selection-sheet";

const first = (value: string | string[] | undefined) => Array.isArray(value) ? value[0] ?? "" : value ?? "";
function userForm(user: IdentityUserDetail): IdentityUserForm {
  return { ...EMPTY_IDENTITY_USER_FORM, username: user.username, email: user.email, fullName: user.fullName ?? "", isActive: user.isActive };
}

type UncertainWriteKind = "create" | "profile" | "password";

export default function IdentityUserDetailScreen() {
  const params = useLocalSearchParams<{ userGuid?: string | string[]; partial?: string | string[] }>();
  const actor = useAuthStore(s => s.user?.userGUID);
  const userGuid = first(params.userGuid);
  // 更换账号或目标用户时销毁整份草稿，避免上一个编辑会话继续提交。
  return <UserEditor key={`${actor}:${userGuid}`} userGuid={userGuid} partial={first(params.partial)} />;
}

function UserEditor({ userGuid, partial }: { userGuid: string; partial: string }) {
  const c = useIdentityUserCopy();
  const router = useRouter();
  const navigation = useNavigation();
  const queryClient = useQueryClient();
  const { user, access, allowed, actorKey, isAuthenticated } = useIdentitySession();
  const creating = userGuid === "new";
  const [section, setSection] = useState("basic");
  const [form, setForm] = useState<IdentityUserForm>({ ...EMPTY_IDENTITY_USER_FORM });
  const [baseline, setBaseline] = useState<IdentityUserForm>({ ...EMPTY_IDENTITY_USER_FORM });
  const [roleGuids, setRoleGuids] = useState<string[]>([]);
  const [assignments, setAssignments] = useState<{ storeGUID: string; isPrimary: boolean }[]>([]);
  const [optionSearch, setOptionSearch] = useState("");
  const [busy, setBusy] = useState(false);
  const inFlight = useRef(false);
  const [initialized, setInitialized] = useState(creating);
  const [error, setError] = useState<unknown>(null);
  const [validation, setValidation] = useState("");
  const [notice, setNotice] = useState(partial ? c.createPartial : "");
  const [snack, setSnack] = useState("");
  const [uncertainWrite, setUncertainWrite] = useState<UncertainWriteKind | null>(null);
  const [forbidden, setForbidden] = useState(false);
  const [passwordDialog, setPasswordDialog] = useState(false);
  const [newPassword, setNewPassword] = useState("");
  const [passwordVisible, setPasswordVisible] = useState(false);
  const [cashierPicker, setCashierPicker] = useState(false);
  const [departing, setDeparting] = useState(false);
  const pendingRoute = useRef<() => void>(() => undefined);
  const scoped = access.isStoreLevelManager;
  const detail = useQuery({ queryKey: ["identity-admin", actorKey, "user", userGuid], queryFn: ({ signal }) => fetchIdentityUserDetail(userGuid, actorKey, signal), enabled: allowed && !creating && !!userGuid, retry: false });
  const linkedStores = useQuery({ queryKey: ["identity-admin", actorKey, "user-stores", userGuid], queryFn: ({ signal }) => fetchUserAccessStores(userGuid, actorKey, signal), enabled: allowed && !creating && !!userGuid, retry: false });
  const catalogRoles = useQuery({ queryKey: ["identity-admin", actorKey, "role-options"], queryFn: ({ signal }) => fetchAccessRoleCatalog(actorKey, signal), enabled: allowed && creating && (access.canReadRole || access.hasPermission("Users.ManageRoles")), retry: false });
  const catalogStores = useQuery({ queryKey: ["identity-admin", actorKey, "store-options"], queryFn: ({ signal }) => fetchAccessStoreCatalog(actorKey, signal), enabled: allowed && creating && !scoped, retry: false });
  const stores = useMemo(() => scoped ? managedIdentityStores(user) : catalogStores.data ?? [], [scoped, user, catalogStores.data]);
  const roles = useMemo(() => selectableIdentityRoles(catalogRoles.data ?? [], scoped).filter(role => !isStoreManagerRoleName(role.roleName)), [catalogRoles.data, scoped]);
  const target = detail.data;
  const isSelf = !creating && actorKey.toLowerCase() === userGuid.toLowerCase();
  const canEdit = allowed && !forbidden && (creating
    ? access.isAdmin && access.hasPermission("Users.Create")
    : !!target && canModifyIdentityUser(user, access, target, "Users.Edit"));
  const dirty = !sameIdentityUserForm(form, baseline) || (creating && (roleGuids.length > 0 || assignments.length > 0));
  const profileUncertain = uncertainWrite === "create" || uncertainWrite === "profile";
  const passwordUncertain = uncertainWrite === "password";

  useEffect(() => {
    if (!initialized && detail.data) {
      const next = userForm(detail.data); setForm(next); setBaseline(next); setInitialized(true);
    }
  }, [detail.data, initialized]);
  useEffect(() => { if (departing) pendingRoute.current(); }, [departing]);
  usePreventRemove(isAuthenticated && !departing && (dirty || busy), ({ data }) => {
    if (inFlight.current) { Alert.alert(c.busy); return; }
    Alert.alert(c.leaveTitle, c.leaveMessage, [
      { text: c.keepEditing, style: "cancel" },
      { text: c.leave, style: "destructive", onPress: () => { pendingRoute.current = () => navigation.dispatch(data.action); setDeparting(true); } },
    ]);
  });
  const goToCreated = (guid: string, partialStores = false) => {
    pendingRoute.current = () => router.replace({ pathname: "/(shell)/user-admin/[userGuid]", params: { userGuid: guid, ...(partialStores ? { partial: "stores" } : {}) } });
    setForm(current => ({ ...current, password: "", confirmPassword: "" }));
    setDeparting(true);
  };
  const isCurrentSession = (permission = "Users.View", requireAdmin = false) => {
    const state = useAuthStore.getState();
    return state.user?.userGUID === actorKey
      && state.sessionKind === "account"
      && state.isAuthenticated
      && !state.iosReviewOfflineGuardActive
      && (!requireAdmin || state.access.isAdmin)
      && state.access.hasPermission(permission);
  };
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey] });
  const handleError = (failure: unknown, kind: UncertainWriteKind) => {
    setError(failure);
    const status = getIdentityAdminErrorMeta(failure).status;
    if (status === 401 || status === 403) setForbidden(true);
    if (isUncertainIdentityWrite(failure)) setUncertainWrite(kind);
  };
  const save = async () => {
    const requiredPermission = creating ? "Users.Create" : "Users.Edit";
    if (inFlight.current || !canEdit || profileUncertain || !isCurrentSession(requiredPermission, creating)) return;
    const invalid = validateIdentityUserForm(form, creating);
    if (invalid) { setValidation(c[invalid]); setSection("basic"); return; }
    if (creating && scoped && !assignments.length) { setValidation(c.scopeRequired); setSection("stores"); return; }
    setValidation(""); setError(null); inFlight.current = true; setBusy(true);
    try {
      if (creating) {
        const created = await createIdentityUser({ username: form.username.trim(), email: form.email.trim(), fullName: form.fullName.trim(), isActive: form.isActive,
          password: form.password, passwordFormat: "raw", roleGuids, storeGuids: assignments.map(item => item.storeGUID) }, actorKey);
        if (!isCurrentSession("Users.Create", true)) return;
        let partialStores = false;
        if (assignments.some(item => item.isPrimary)) {
          try { await assignUserAccessStores({ userGuid: created.userGUID, assignments }, actorKey); }
          catch { partialStores = true; }
          if (!isCurrentSession("Users.Create", true)) return;
        }
        // 创建已返回有效身份后，无论后续分店赋权结果如何，都不再次发送创建请求。
        await invalidate();
        if (!isCurrentSession("Users.Create", true)) return;
        goToCreated(created.userGUID, partialStores);
      } else {
        await updateIdentityUser(userGuid, { username: form.username.trim(), email: form.email.trim(), fullName: form.fullName.trim(), isActive: form.isActive }, actorKey);
        if (!isCurrentSession("Users.Edit")) return;
        const refreshed = await fetchIdentityUserDetail(userGuid, actorKey);
        if (!isCurrentSession("Users.Edit")) return;
        const next = userForm(refreshed); setForm(next); setBaseline(next);
        queryClient.setQueryData(["identity-admin", actorKey, "user", userGuid], refreshed);
        await invalidate();
        if (!isCurrentSession("Users.Edit")) return;
        setSnack(c.saved);
      }
    } catch (failure) { if (isCurrentSession(requiredPermission, creating)) handleError(failure, creating ? "create" : "profile"); }
    finally { inFlight.current = false; setBusy(false); }
  };
  const verify = async () => {
    if (inFlight.current || !isCurrentSession("Users.View", creating)) return;
    inFlight.current = true; setBusy(true);
    try {
      if (creating) {
        const result = await fetchIdentityUsers({ search: form.username.trim(), page: 1, pageSize: 50 }, actorKey);
        if (!isCurrentSession("Users.View", true)) return;
        const matches = result.items.filter(item => item.username.toLowerCase() === form.username.trim().toLowerCase());
        // 同名只能证明账号存在，不能证明它由本次不确定 POST 创建；保持冻结并交由管理员人工核对。
        if (matches.length === 1) { setError(null); setNotice(c.createdCheck); }
        else setNotice(c.notConfirmed);
      } else {
        const refreshed = await fetchIdentityUserDetail(userGuid, actorKey);
        if (!isCurrentSession("Users.View")) return;
        const next = userForm(refreshed); setForm(next); setBaseline(next); setUncertainWrite(null); setError(null); setNotice("");
        queryClient.setQueryData(["identity-admin", actorKey, "user", userGuid], refreshed);
      }
    } catch (failure) { if (isCurrentSession("Users.View", creating)) setError(failure); }
    finally { inFlight.current = false; setBusy(false); }
  };
  const resetPassword = async () => {
    if (inFlight.current || passwordUncertain || !target || !canModifyIdentityUser(user, access, target, "Users.ResetPassword") || !isCurrentSession("Users.ResetPassword")) return;
    if (newPassword.length < 6) { setValidation(c.passwordInvalid); return; }
    inFlight.current = true; setBusy(true); setError(null); setValidation("");
    try {
      await updateIdentityUserPassword(userGuid, { newPassword, passwordFormat: "raw" }, actorKey);
      if (!isCurrentSession("Users.ResetPassword")) return;
      setNewPassword(""); setPasswordDialog(false); setSnack(c.resetDone);
    } catch (failure) {
      if (!isCurrentSession("Users.ResetPassword")) return;
      handleError(failure, "password");
      if (isUncertainIdentityWrite(failure)) { setPasswordDialog(false); setNotice(c.resetUncertain); }
    }
    finally { inFlight.current = false; setBusy(false); }
  };
  const openAccess = (nextSection: string) => router.push({ pathname: "/(shell)/user-admin/[userGuid]/access", params: { userGuid, section: nextSection } });
  const textField = (key: "username" | "email" | "fullName" | "password" | "confirmPassword") => {
    const secret = key === "password" || key === "confirmPassword";
    const required = key !== "fullName";
    return <View key={key} style={{ gap: 6 }}><Text style={styles.label}>{c[key]}{required ? " *" : ""}</Text>
      <TextInput mode="outlined" dense value={form[key]} onChangeText={value => setForm(current => ({ ...current, [key]: value }))}
        disabled={!canEdit || busy || profileUncertain || (isSelf && key === "username")} accessibilityLabel={c[key]} autoCapitalize={key === "fullName" ? "words" : "none"} autoCorrect={false}
        keyboardType={key === "email" ? "email-address" : "default"} secureTextEntry={secret && !passwordVisible}
        style={{ backgroundColor: C.white }} outlineStyle={{ borderRadius: 8, borderColor: C.outlineMuted }}
        right={secret ? <TextInput.Icon icon={passwordVisible ? "eye-off" : "eye"} onPress={() => setPasswordVisible(value => !value)} /> : undefined} />
    </View>;
  };
  if (!allowed || (creating && (!access.isAdmin || !access.hasPermission("Users.Create")))) return <AdminScreen title={c.users}><AdminEmpty text={c.noAccess} /></AdminScreen>;
  if (!creating && detail.isPending) return <AdminScreen title={c.details}><ActivityIndicator style={{ margin: 32 }} /></AdminScreen>;
  if (!creating && detail.isError) return <AdminScreen title={c.details}><AdminError error={detail.error} onRetry={() => void detail.refetch()} /></AdminScreen>;
  const canManageRoles = !!target && canModifyIdentityUser(user, access, target, "Users.ManageRoles");
  const canManageStores = !!target && canModifyIdentityUser(user, access, target, "Users.ManageStores");
  const canManagePermissions = canManageRoles || (!!target && scoped && canModifyIdentityUser(user, access, target, "Users.ManagePosTerminalPermissions"));
  const canManageCashier = !!target && canModifyIdentityUser(user, access, target, "Users.ManagePosTerminalPermissions") && actorKey.toLowerCase() !== userGuid.toLowerCase();
  const linked = linkedStores.data ?? [];
  return <AdminScreen title={creating ? c.create : canEdit ? c.edit : c.details} footer={canEdit ? <>
    {profileUncertain ? <><Text style={styles.muted}>{c.uncertain}</Text><Button mode="outlined" onPress={() => void verify()} loading={busy}>{creating ? c.verifyCreate : c.refresh}</Button></> : null}
    <Button mode="contained" style={styles.button} contentStyle={{ minHeight: 46 }} loading={busy} disabled={busy || profileUncertain || (!creating && !dirty)} onPress={() => void save()}>{creating ? c.createSave : c.save}</Button>
  </> : undefined}>
    {!creating && target ? <View style={[styles.row, { borderBottomWidth: 0 }]}><Avatar.Text size={44} label={(target.fullName || target.username).slice(0, 1)} color={C.action} style={{ backgroundColor: "#EFF6FF" }} />
      <View style={{ flex: 1 }}><Text style={styles.value}>{target.fullName || target.username}</Text><Text style={styles.muted}>{target.username}</Text></View><StatusTag active={target.isActive} />
    </View> : null}
    <AdminTabs value={section} onChange={value => { setSection(value); setOptionSearch(""); }} items={creating
      ? [{ key: "basic", label: c.basic }, ...(catalogRoles.isEnabled ? [{ key: "roles", label: c.rolesTab }] : []), { key: "stores", label: c.storesAssign }]
      : [{ key: "basic", label: c.basic }, { key: "authorization", label: c.authorization }]} />
    <ScrollView keyboardShouldPersistTaps="handled" contentContainerStyle={styles.content}>
      {notice ? <Text style={{ color: C.warning }}>{notice}</Text> : null}
      {validation ? <Text style={{ color: C.danger }} accessibilityRole="alert">{validation}</Text> : null}
      {error ? <AdminError error={error} /> : null}
      {section === "basic" ? <>
        {textField("username")}{textField("email")}{textField("fullName")}
        {creating ? <>{textField("password")}{textField("confirmPassword")}</> : null}
        <View style={[styles.row, { paddingHorizontal: 0 }]}><Text style={[styles.label, { flex: 1 }]}>{c.status}</Text><Switch value={form.isActive} disabled={!canEdit || busy || profileUncertain || isSelf} onValueChange={isActive => setForm(current => ({ ...current, isActive }))} /><Text>{form.isActive ? c.active : c.inactive}</Text></View>
        {creating ? <Text style={styles.muted}>{c.createHint}</Text> : <>
          <View style={styles.section}>
            <AdminRow title={c.rolesAssign} subtitle={target?.roleNames.join("、") || "—"} onPress={canManageRoles ? () => openAccess("roles") : undefined} />
            <AdminRow title={c.storesAssign} subtitle={`${linked.length} ${c.storesCount} · ${linked.filter(store => store.isManageable).length} ${c.managedCount}`} onPress={canManageStores ? () => openAccess("stores") : undefined} />
          </View>
          <View style={{ flexDirection: "row", flexWrap: "wrap" }}>
            {target && canModifyIdentityUser(user, access, target, "Users.ResetPassword") ? <Button icon="key-outline" disabled={busy || passwordUncertain || forbidden} onPress={() => { setValidation(""); setPasswordDialog(true); }}>{c.resetPassword}</Button> : null}
            <Button icon="history" onPress={() => router.push({ pathname: "/(shell)/user-admin/[userGuid]/login-records", params: { userGuid } })}>{c.loginRecords}</Button>
          </View>
          <View style={styles.section}>
            <AdminRow title={c.lastLogin} subtitle={identityDate(target?.lastLoginAt)} /><AdminRow title={c.lastIp} subtitle={target?.lastLoginIp || "—"} />
            {target?.phone ? <AdminRow title={c.phone} subtitle={target.phone} /> : null}
            <AdminRow title={c.createdAt} subtitle={identityDate(target?.createdAt)} /><AdminRow title={c.updatedAt} subtitle={identityDate(target?.updatedAt)} />
          </View>
        </>}
      </> : section === "authorization" ? <View style={styles.section}>
        <AdminRow title={c.rolesAssign} subtitle={target?.roleNames.join("、") || "—"} onPress={canManageRoles ? () => openAccess("roles") : undefined} />
        <AdminRow title={c.storesAssign} subtitle={linked.map(store => `${store.storeName} · ${store.isManageable ? c.manageable : c.linked}`).join("\n") || "—"} onPress={canManageStores ? () => openAccess("stores") : undefined} />
        {canManagePermissions ? <><AdminRow title={c.permissions} icon="shield-check-outline" onPress={() => openAccess("permissions")} /><AdminRow title={c.mobileMenu} icon="cellphone" onPress={() => openAccess("mobile")} /></> : null}
        {canManageCashier ? <AdminRow title={c.cashier} icon="cash-register" onPress={() => setCashierPicker(true)} /> : null}
        {linkedStores.isError ? <AdminError error={linkedStores.error} onRetry={() => void linkedStores.refetch()} /> : null}
      </View> : <>
        <SearchField value={optionSearch} onChange={setOptionSearch} placeholder={c.filterSearch} />
        {section === "roles" ? <>
          <Text style={styles.muted}>{c.roleDerived}</Text>
          {catalogRoles.isPending ? <ActivityIndicator /> : catalogRoles.isError ? <AdminError error={catalogRoles.error} onRetry={() => void catalogRoles.refetch()} /> : roles.filter(role => role.roleName.toLowerCase().includes(optionSearch.toLowerCase())).map(role => <TouchableRipple key={role.roleGUID} disabled={busy || profileUncertain} onPress={() => setRoleGuids(current => current.includes(role.roleGUID) ? current.filter(guid => guid !== role.roleGUID) : [...current, role.roleGUID])}>
            <AdminRow title={role.roleName} subtitle={role.description} trailing={<Checkbox status={roleGuids.includes(role.roleGUID) ? "checked" : "unchecked"} />} />
          </TouchableRipple>)}
        </> : <>
          {scoped ? <Text style={styles.muted}>{c.storeScopeHint}</Text> : null}
          {catalogStores.isLoading ? <ActivityIndicator /> : catalogStores.isError && !scoped ? <AdminError error={catalogStores.error} onRetry={() => void catalogStores.refetch()} /> : stores.filter(store => `${store.storeName} ${store.storeCode}`.toLowerCase().includes(optionSearch.toLowerCase())).map(store => {
            const selected = assignments.find(item => item.storeGUID === store.storeGUID);
            return <View key={store.storeGUID}>
              <TouchableRipple disabled={busy || profileUncertain} onPress={() => setAssignments(current => selected ? current.filter(item => item.storeGUID !== store.storeGUID) : [...current, { storeGUID: store.storeGUID, isPrimary: false }])}>
                <AdminRow title={store.storeName} subtitle={store.storeCode} trailing={<Checkbox status={selected ? "checked" : "unchecked"} />} />
              </TouchableRipple>
              {selected ? <View style={[styles.row, { minHeight: 44, paddingVertical: 0 }]}><Text style={[styles.muted, { flex: 1 }]}>{selected.isPrimary ? c.manageable : c.linked}</Text><Switch value={selected.isPrimary} disabled={busy || profileUncertain || !access.hasPermission("Users.ManageStores")} onValueChange={value => setAssignments(current => current.map(item => item.storeGUID === store.storeGUID ? { ...item, isPrimary: value } : item))} /></View> : null}
            </View>;
          })}
        </>}
      </>}
    </ScrollView>
    <Portal><Dialog visible={passwordDialog} dismissable={!busy} onDismiss={() => !busy && setPasswordDialog(false)}><Dialog.Title>{c.resetPassword}</Dialog.Title><Dialog.Content>
      <Text>{c.resetPrompt}</Text><TextInput mode="outlined" label={c.newPassword} secureTextEntry value={newPassword} onChangeText={setNewPassword} disabled={busy} autoCapitalize="none" />
      {validation ? <Text style={{ color: C.danger }}>{validation}</Text> : null}{error ? <AdminError error={error} /> : null}
    </Dialog.Content><Dialog.Actions><Button disabled={busy} onPress={() => setPasswordDialog(false)}>{c.cancel}</Button><Button disabled={busy || forbidden || passwordUncertain} loading={busy} onPress={() => void resetPassword()}>{c.confirm}</Button></Dialog.Actions></Dialog></Portal>
    {cashierPicker ? <IdentitySelectionSheet title={c.chooseStore} options={linked.filter(store => !scoped || managedIdentityStores(user).some(managed => managed.storeGUID === store.storeGUID)).map(store => ({ value: store.storeGUID, label: `${store.storeName} · ${store.storeCode}` }))}
      onDismiss={() => setCashierPicker(false)} onSelect={storeGuid => { const store = linked.find(item => item.storeGUID === storeGuid); setCashierPicker(false); router.push({ pathname: "/(shell)/users/[userGuid]/pos-terminal-permissions", params: { userGuid, storeGuid, storeCode: store?.storeCode, userName: target?.fullName || target?.username, storeName: store?.storeName, roleNames: target?.roleNames?.join("|") } }); }} /> : null}
    <Snackbar visible={!!snack} onDismiss={() => setSnack("")}>{snack}</Snackbar>
  </AdminScreen>;
}

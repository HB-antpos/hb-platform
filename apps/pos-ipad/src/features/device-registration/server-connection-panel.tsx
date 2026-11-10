import { useEffect, useRef, useState, type ReactNode } from "react";
import { Text, View } from "react-native";

import { PosButton } from "@/ui/controls/pos-button";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { createPosStyles, posColors, usePosThemeVersion } from "@/ui/theme";

type ConnectionState =
  | "idle"
  | "testing"
  | "passed"
  | "failed"
  | "saving"
  | "saved"
  | "save-failed";

export type ServerConnectionPanelCopy = Readonly<{
  title: string;
  eyebrow: string;
  currentAddress: string;
  edit: string;
  addressLabel: string;
  addressPlaceholder: string;
  test: string;
  testing: string;
  save: string;
  cancel: string;
  confirm: string;
  confirmationTitle: string;
  confirmationHint: string;
  emptyAddress: string;
  testPassed: string;
  testFailed: string;
  saveBlocked: string;
  saving: string;
  saved: string;
  saveFailed: string;
}>;

const DEFAULT_COPY: ServerConnectionPanelCopy = Object.freeze({
  title: "服务器连接",
  eyebrow: "网络检查",
  currentAddress: "当前服务器",
  edit: "修改",
  addressLabel: "服务器地址",
  addressPlaceholder: "https://example.com/pos-api",
  test: "测试连接",
  testing: "正在检查",
  save: "保存地址",
  cancel: "取消",
  confirm: "确认保存",
  confirmationTitle: "确认切换服务器？",
  confirmationHint: "应用将保存已测试的地址，并按安全流程重新载入。",
  emptyAddress: "请输入服务器地址。",
  testPassed: "连接成功，可以保存此地址。",
  testFailed: "连接失败，请检查地址和网络后重试。",
  saveBlocked: "本机账本无法检查，暂不能切换服务器。",
  saving: "正在保存服务器地址…",
  saved: "服务器地址已保存。",
  saveFailed: "保存失败，当前服务器地址未改变。",
});

export type ServerConnectionPanelProps = Readonly<{
  canSave: boolean;
  copy?: Partial<ServerConnectionPanelCopy>;
  currentAddress: string;
  saveAddress(address: string): Promise<void>;
  testAddress(address: string): Promise<boolean>;
  /** 标题行右侧的附加入口（例如注册页的「网络检测」），与「修改 / 检查」并排。 */
  actions?: ReactNode;
}>;

/**
 * 预登录页只负责编辑与呈现；地址校验、健康检查和账本安全门禁均由注入端口负责。
 * 平时收成一条：标题 + 当前服务器地址 + 右侧入口；点「修改 / 检查」后才展开地址输入、测试与保存。
 */
export function ServerConnectionPanel({
  actions,
  canSave,
  copy: copyOverride,
  currentAddress,
  saveAddress,
  testAddress,
}: ServerConnectionPanelProps) {
  usePosThemeVersion();
  const copy = { ...DEFAULT_COPY, ...copyOverride };
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(currentAddress);
  const [state, setState] = useState<ConnectionState>("idle");
  const [testedAddress, setTestedAddress] = useState<string | null>(null);
  const [confirmationVisible, setConfirmationVisible] = useState(false);
  const requestGeneration = useRef(0);

  useEffect(() => {
    if (!editing) setDraft(currentAddress);
  }, [currentAddress, editing]);

  useEffect(
    () => () => {
      requestGeneration.current += 1;
    },
    [],
  );

  const candidate = draft.trim();
  const busy = state === "testing" || state === "saving";
  const saveEnabled =
    canSave &&
    !busy &&
    state === "passed" &&
    testedAddress === candidate;

  const updateDraft = (value: string) => {
    requestGeneration.current += 1;
    setDraft(value);
    setState("idle");
    setTestedAddress(null);
    setConfirmationVisible(false);
  };

  const runTest = async () => {
    if (!candidate || busy) {
      if (!candidate) setState("failed");
      return;
    }
    const generation = ++requestGeneration.current;
    setConfirmationVisible(false);
    setTestedAddress(null);
    setState("testing");
    try {
      const reachable = await testAddress(candidate);
      if (generation !== requestGeneration.current) return;
      setTestedAddress(reachable ? candidate : null);
      setState(reachable ? "passed" : "failed");
    } catch {
      if (generation !== requestGeneration.current) return;
      setTestedAddress(null);
      setState("failed");
    }
  };

  const confirmSave = async () => {
    if (!saveEnabled) return;
    const generation = ++requestGeneration.current;
    setConfirmationVisible(false);
    setState("saving");
    try {
      await saveAddress(candidate);
      if (generation !== requestGeneration.current) return;
      setState("saved");
    } catch {
      if (generation !== requestGeneration.current) return;
      setState("save-failed");
    }
  };

  const status = statusText(state, candidate, copy);

  return (
    <PosKeyboardAwareScrollView
      contentContainerStyle={styles.panelContent}
      nestedScrollEnabled
      style={styles.panel}
      testID="server-connection-panel"
    >
      <View style={styles.headerRow}>
        <View style={styles.headingCopy}>
          <View style={styles.titleRow}>
            <Text style={styles.title}>{copy.title}</Text>
            <Text numberOfLines={1} style={styles.eyebrow}>
              {copy.eyebrow}
            </Text>
          </View>
          <View style={styles.currentAddressRow}>
            <Text style={styles.currentAddressLabel}>
              {copy.currentAddress}
            </Text>
            <Text
              numberOfLines={2}
              selectable
              style={styles.currentAddress}
              testID="server-connection-current"
            >
              {currentAddress}
            </Text>
          </View>
          {/* 只读阶段（不能保存）在收起时就说明原因，展开后由编辑区的提示条承担。 */}
          {!canSave && !editing ? (
            <Text style={styles.readOnlyHint}>{copy.saveBlocked}</Text>
          ) : null}
        </View>
        {!editing ? (
          <PosButton
            label={copy.edit}
            onPress={() => {
              setDraft(currentAddress);
              setState("idle");
              setTestedAddress(null);
              setEditing(true);
            }}
            size="medium"
            testID="server-connection-edit"
            tone="quiet"
          />
        ) : null}
        {actions}
      </View>

      {editing ? (
        <View style={styles.editor}>
          <Text style={styles.fieldLabel}>{copy.addressLabel}</Text>
          <PosKeyboardAwareTextInput
            accessibilityLabel={copy.addressLabel}
            autoCapitalize="none"
            autoCorrect={false}
            editable={!busy && !confirmationVisible}
            onChangeText={updateDraft}
            placeholder={copy.addressPlaceholder}
            placeholderTextColor={posColors.placeholder}
            style={[
              styles.input,
              (busy || confirmationVisible) && styles.inputDisabled,
            ]}
            testID="server-connection-input"
            value={draft}
          />

          <View style={styles.actionRow}>
            <PosButton
              disabled={!candidate || busy || confirmationVisible}
              fill
              label={state === "testing" ? copy.testing : copy.test}
              loading={state === "testing"}
              onPress={() => void runTest()}
              size="medium"
              testID="server-connection-test"
            />
            <PosButton
              disabled={!saveEnabled}
              fill
              label={copy.save}
              onPress={() => setConfirmationVisible(true)}
              size="medium"
              testID="server-connection-save"
              tone="inverse"
            />
          </View>

          {!canSave ? (
            <View style={styles.blockedBanner}>
              <Text
                accessibilityRole="alert"
                style={styles.blockedText}
                testID="server-connection-save-disabled-reason"
              >
                {copy.saveBlocked}
              </Text>
            </View>
          ) : null}

          {status ? (
            <Text
              accessibilityRole="alert"
              style={[
                styles.statusText,
                (state === "failed" || state === "save-failed") &&
                  styles.statusError,
                (state === "passed" || state === "saved") &&
                  styles.statusSuccess,
              ]}
              testID="server-connection-status"
            >
              {status}
            </Text>
          ) : null}

          {confirmationVisible ? (
            <View
              accessibilityRole="alert"
              style={styles.confirmation}
              testID="server-connection-confirmation"
            >
              <Text style={styles.confirmationTitle}>
                {copy.confirmationTitle}
              </Text>
              <Text style={styles.confirmationHint}>
                {copy.confirmationHint}
              </Text>
              <Text numberOfLines={2} style={styles.confirmationAddress}>
                {candidate}
              </Text>
              <View style={styles.confirmationActions}>
                <PosButton
                  label={copy.cancel}
                  onPress={() => setConfirmationVisible(false)}
                  size="medium"
                  testID="server-connection-cancel"
                />
                <PosButton
                  label={copy.confirm}
                  onPress={() => void confirmSave()}
                  size="medium"
                  testID="server-connection-confirm"
                  tone="inverse"
                />
              </View>
            </View>
          ) : null}
        </View>
      ) : null}
    </PosKeyboardAwareScrollView>
  );
}

function statusText(
  state: ConnectionState,
  candidate: string,
  copy: ServerConnectionPanelCopy,
): string | null {
  switch (state) {
    case "testing":
      return copy.testing;
    case "passed":
      return copy.testPassed;
    case "failed":
      return candidate ? copy.testFailed : copy.emptyAddress;
    case "saving":
      return copy.saving;
    case "saved":
      return copy.saved;
    case "save-failed":
      return copy.saveFailed;
    case "idle":
      return null;
  }
}

const styles = createPosStyles((colors) => ({
  panel: {
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderRadius: 12,
    borderWidth: 1,
    // 嵌在其他滚动容器里时按内容高度排版，不抢占剩余空间。
    flexGrow: 0,
  },
  panelContent: {
    gap: 12,
    paddingHorizontal: 16,
    paddingVertical: 12,
  },
  headerRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
  },
  headingCopy: {
    flex: 1,
    gap: 2,
    minWidth: 0,
  },
  titleRow: {
    alignItems: "baseline",
    flexDirection: "row",
    gap: 8,
  },
  title: {
    color: colors.ink,
    fontSize: 17,
    fontWeight: "900",
  },
  eyebrow: {
    color: colors.mutedInk,
    flexShrink: 1,
    fontSize: 12.5,
    fontWeight: "600",
  },
  currentAddressRow: {
    alignItems: "flex-start",
    flexDirection: "row",
    gap: 6,
  },
  currentAddressLabel: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
  },
  currentAddress: {
    color: colors.mutedInk,
    flex: 1,
    fontSize: 13,
    fontWeight: "700",
    minWidth: 0,
  },
  readOnlyHint: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
    lineHeight: 18,
  },
  editor: {
    borderTopColor: colors.border,
    borderTopWidth: 1,
    gap: 10,
    paddingTop: 12,
  },
  fieldLabel: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
  },
  input: {
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: 10,
    borderWidth: 1.5,
    color: colors.ink,
    fontSize: 17,
    fontWeight: "700",
    minHeight: 60,
    paddingHorizontal: 14,
    paddingVertical: 9,
  },
  inputDisabled: {
    backgroundColor: colors.disabledSurface,
    borderColor: colors.disabledBorder,
    color: colors.disabledInk,
  },
  actionRow: {
    flexDirection: "row",
    gap: 10,
  },
  blockedBanner: {
    backgroundColor: colors.yellowSoft,
    borderColor: colors.yellowBorder,
    borderRadius: 10,
    borderWidth: 1.5,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  blockedText: {
    color: colors.yellowInk,
    fontSize: 14,
    fontWeight: "700",
    lineHeight: 20,
  },
  statusText: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "700",
    lineHeight: 20,
  },
  statusError: {
    color: colors.red,
  },
  statusSuccess: {
    color: colors.green,
  },
  confirmation: {
    backgroundColor: colors.blueSoft,
    borderColor: colors.blueBorder,
    borderRadius: 10,
    borderWidth: 1.5,
    gap: 6,
    padding: 14,
  },
  confirmationTitle: {
    color: colors.ink,
    fontSize: 17,
    fontWeight: "900",
  },
  confirmationHint: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 19,
  },
  confirmationAddress: {
    color: colors.ink,
    fontSize: 14,
    fontWeight: "800",
  },
  confirmationActions: {
    flexDirection: "row",
    gap: 10,
    justifyContent: "flex-end",
    marginTop: 6,
  },
}));

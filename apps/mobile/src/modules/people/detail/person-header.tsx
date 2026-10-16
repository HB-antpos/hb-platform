import type { ReactNode } from "react";
import { StyleSheet, View } from "react-native";
import { PeopleTag, resolvePersonName, type PeopleTone } from "@/modules/people";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { buildHeaderStatusTags, type DetailLayout, type HeaderTagKey, type StatusIntent } from "./detail-rules";
import { PersonHeaderCard, QuickActionTile } from "./detail-ui";
import type { PersonView } from "./person-view";

const TAG_TONES: Record<HeaderTagKey, PeopleTone> = {
  active: "green",
  disabled: "red",
  pendingFirstLogin: "blue",
  loginDisabled: "amber",
};

/** 头部状态标签文案复用员工管理命名空间里已有的 statuses.*。 */
export function HeaderStatusTags({ person }: { person: PersonView }) {
  const { t } = useAppTranslation(["userManagement"]);
  return (
    <>
      {buildHeaderStatusTags({
        active: person.active,
        mustChangePassword: person.mustChangePassword,
        loginEnabled: person.loginEnabled,
      }).map((key) => (
        <PeopleTag key={key} label={t(`statuses.${key}`)} tone={TAG_TONES[key]} />
      ))}
    </>
  );
}

/** 店长版 / 管理员版共用的头部卡片：头像 + 名字（昵称小字）+ 元信息 + 标签；传了 layout 时底部带快捷操作行。 */
export function PersonHeader({
  person,
  meta,
  extraTags,
  quick,
}: {
  person: PersonView;
  meta: string;
  extraTags?: ReactNode;
  quick?: {
    layout: DetailLayout;
    statusPending: boolean;
    onEdit: () => void;
    onPassword: () => void;
    onBarcode: () => void;
    onStatus: (intent: StatusIntent) => void;
  };
}) {
  const { t } = useAppTranslation(["peopleDetail", "userManagement"]);
  const name = resolvePersonName(person);
  return (
    <PersonHeaderCard
      initials={name.initials}
      seed={person.userGuid}
      active={person.active}
      name={name.primary}
      nickname={name.nickname}
      meta={meta}
      tags={(
        <>
          <HeaderStatusTags person={person} />
          {extraTags}
        </>
      )}
    >
      {quick && quick.layout.quickActions.length ? (
        <View style={styles.quickRow} testID="person-quick-actions">
          {quick.layout.quickActions.map((action) => {
            switch (action.key) {
              case "edit":
                return <QuickActionTile key="edit" icon="pencil-outline" label={t("peopleDetail:quick.edit")} onPress={quick.onEdit} testID="quick-edit" />;
              case "password":
                return <QuickActionTile key="password" icon="email-fast-outline" label={t("peopleDetail:quick.password")} onPress={quick.onPassword} testID="quick-password" />;
              case "barcode":
                return <QuickActionTile key="barcode" icon="qrcode" label={t("peopleDetail:quick.barcode")} onPress={quick.onBarcode} testID="quick-barcode" />;
              case "status":
                return (
                  <QuickActionTile
                    key="status"
                    icon={action.intent === "disable" ? "account-off-outline" : "account-check-outline"}
                    label={action.intent === "disable" ? t("userManagement:actions.disable") : t("userManagement:actions.enable")}
                    tone={action.danger ? "red" : "green"}
                    loading={quick.statusPending}
                    onPress={() => quick.onStatus(action.intent ?? "disable")}
                    testID="quick-status"
                  />
                );
              default:
                return null;
            }
          })}
        </View>
      ) : null}
    </PersonHeaderCard>
  );
}

const styles = StyleSheet.create({
  quickRow: { flexDirection: "row", gap: 4, paddingBottom: 14, paddingHorizontal: 6, paddingTop: 4 },
});

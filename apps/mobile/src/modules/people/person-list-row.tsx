import type { ReactNode } from "react";
import { StyleSheet, View } from "react-native";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { ListCardSegment } from "./people-frame";
import type { ListRowPosition } from "./list-position";
import type { PersonListItem, PersonTagKey } from "./person-list-item";
import { PeopleTag, PersonAvatar, PersonRow, type PeopleTone } from "./ui";

/** 例外标签的颜色与图标（文案在 people.json 的 tags.*）。 */
const TAG_STYLE: Record<PersonTagKey, { tone: PeopleTone; icon: string }> = {
  pendingFirstLogin: { tone: "amber", icon: "clock-outline" },
  cannotLogin: { tone: "gray", icon: "lock-outline" },
  noEmail: { tone: "amber", icon: "email-off-outline" },
  disabled: { tone: "gray", icon: "account-off-outline" },
};

/**
 * 人员列表的一行：店长的「本店店员」与管理员的「全局用户」共用，数据先经 person-list-item.ts 适配成 PersonListItem。
 * 头像 + 名字（昵称小字）+ 一行元信息 + 只给例外标签；右侧由调用方放二维码图标 / 箭头等。
 */
export function PersonListRow({
  item,
  position,
  onPress,
  leading,
  right,
  testID,
}: {
  item: PersonListItem;
  position: ListRowPosition;
  /** 不传 = 不可点（本人行）。 */
  onPress?: () => void;
  /** 头像左侧（批量打印的勾选框）。 */
  leading?: ReactNode;
  right?: ReactNode;
  testID?: string;
}) {
  const { t } = useAppTranslation("people");
  const avatar = (
    <View style={styles.avatarWrap}>
      {leading}
      <PersonAvatar
        initials={item.initials}
        seed={item.key}
        disabled={item.inactive}
        presence={item.inactive ? "off" : "on"}
      />
    </View>
  );
  const nameBadges = item.isSelf || item.badge ? (
    <>
      {item.isSelf ? <PeopleTag label={t("row.you")} tone="blue" /> : null}
      {item.badge ? <PeopleTag label={item.badge.label} tone={item.badge.tone} /> : null}
    </>
  ) : undefined;
  const tags = item.tags.length ? (
    <>
      {item.tags.map((key) => (
        <PeopleTag key={key} label={t(`tags.${key}`)} tone={TAG_STYLE[key].tone} icon={TAG_STYLE[key].icon} />
      ))}
    </>
  ) : undefined;

  return (
    <ListCardSegment position={position}>
      <PersonRow
        avatar={avatar}
        name={item.name}
        nickname={item.nickname}
        nameBadges={nameBadges}
        meta={item.meta}
        tags={tags}
        right={right}
        onPress={onPress}
        first={position === "first" || position === "only"}
        accessibilityLabel={`${item.name} ${item.meta}`}
        testID={testID}
      />
    </ListCardSegment>
  );
}

const styles = StyleSheet.create({
  avatarWrap: { alignItems: "center", flexDirection: "row", gap: 4 },
});

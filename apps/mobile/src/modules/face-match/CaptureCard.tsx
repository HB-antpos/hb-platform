// 抓拍列表的一行（抓拍流的行与「黑名单」命中流的行共用同一个渲染）：
// 左侧 56×56 头像位（缩略图 / 骨架 / 占位，命中行带红框与盾牌角标）；
// 命中的整卡淡红，显示「黑名单命中 N%」实心红标签、复核状态标签（待复核 / 自动确认 / 已确认 / 已驳回）与「已通知 N 人 · 已读 M」；
// 我还没看的命中（unread）左侧有 4px 红条、时间加粗。相似度只写「命中 N%」，不写成概率。
import { Pressable, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { CaptureThumb } from "./CaptureThumb";
import {
  captureScoreParts,
  describeDecision,
  describeHitNotify,
  describeHitScoreTag,
  describeHitStatusTag,
  formatCaptureListTime,
  type CaptureRow,
  type ThumbDisplay,
} from "./capture-logic";
import { FACE_HIT, FACE_SOFT, FaceCard, MONO, Tag, faceStyles } from "./ui";

export function CaptureCard({ row, storeLabel, thumb, onOpen }: { row: CaptureRow; storeLabel: string | null; thumb: ThumbDisplay; onOpen: () => void }) {
  const { t } = useAppTranslation("faceMatch");
  const decision = describeDecision(row);
  const scores = captureScoreParts(row);
  const { hit } = row;
  const notify = hit ? describeHitNotify(hit) : null;
  const scoreTag = hit ? describeHitScoreTag(hit) : null;
  const statusTag = hit ? describeHitStatusTag(hit) : null;
  const unread = hit?.unread === true;
  return (
    <Pressable accessibilityRole="button" onPress={onOpen}>
      <FaceCard style={hit ? styles.hitCard : undefined}>
        {unread ? <View pointerEvents="none" style={styles.unreadBar} /> : null}
        <View style={styles.cardRow}>
          <CaptureThumb display={thumb} hit={Boolean(hit)} />
          <View style={styles.cardBody}>
            <View style={faceStyles.rowBetween}>
              <Text style={[styles.time, unread ? styles.timeUnread : styles.timeRead, MONO]}>{formatCaptureListTime(row.capturedAtUtc, new Date())}</Text>
              <Text style={[faceStyles.muted, styles.camera]} numberOfLines={1}>
                {row.cameraId}
              </Text>
            </View>
            {storeLabel ? <Text style={faceStyles.muted}>{storeLabel}</Text> : null}
            {scores.length > 0 ? (
              <Text style={[faceStyles.muted, MONO]}>{scores.map((part) => t(part.labelKey, { percent: part.percent })).join(" · ")}</Text>
            ) : null}
            <View style={faceStyles.rowWrap}>
              {/* 命中流的行不显示判定标签（命中流按任务查，Match 模式没有抓拍评估；设计图 03 同样不放）。 */}
              {row.source === "capture" ? <Tag label={t(decision.labelKey)} tone={decision.tone} /> : null}
              {scoreTag ? (
                <Tag
                  label={t("capture.hit.tag", { percent: scoreTag.percent })}
                  tone="danger"
                  icon="shield-alert"
                  solid={scoreTag.solid}
                  style={scoreTag.solid ? undefined : styles.hitLightTag}
                />
              ) : null}
              {statusTag ? <Tag label={t(statusTag.labelKey)} tone={statusTag.tone} icon={statusTag.icon ?? undefined} /> : null}
              {row.isDuplicate ? <Tag label={t("capture.duplicate")} /> : null}
            </View>
            {notify ? (
              <View style={styles.notifyRow}>
                <MaterialCommunityIcons name={notify.icon} size={14} color={notify.tone === "success" ? HB_COLORS.success : HB_COLORS.textSecondary} />
                <Text style={faceStyles.muted}>{t(notify.labelKey, notify.params)}</Text>
              </View>
            ) : null}
          </View>
        </View>
      </FaceCard>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  hitCard: { backgroundColor: FACE_SOFT.danger, borderColor: FACE_HIT.border },
  // 左侧 4px 红条：贴着卡片左边缘，上下各留 14px，右侧圆角（设计稿 .card.unread:before）。
  unreadBar: {
    position: "absolute",
    left: 0,
    top: 14,
    bottom: 14,
    width: 4,
    borderTopRightRadius: 3,
    borderBottomRightRadius: 3,
    backgroundColor: FACE_HIT.solid,
  },
  cardRow: { flexDirection: "row", gap: HB_SPACING.sm },
  cardBody: { flex: 1, gap: 4 },
  time: { fontSize: 15, fontVariant: ["tabular-nums"] },
  timeUnread: { fontWeight: "700", color: HB_COLORS.textPrimary },
  timeRead: { fontWeight: "600", color: "#344054" },
  camera: { flexShrink: 1 },
  hitLightTag: { backgroundColor: HB_COLORS.white },
  notifyRow: { flexDirection: "row", alignItems: "center", gap: 4 },
});

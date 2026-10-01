import { View } from "react-native";
import { Icon, Text } from "react-native-paper";
import { activeReview, operationTone } from "@/modules/legacy-employee-logs/logic";
import type { LegacyLogItem } from "@/modules/legacy-employee-logs/types";
import { HB_COLORS } from "@/shared/theme/tokens";
import { LEGACY_UI, OPERATION_TONES, RISK } from "./ui";

export function OperationTag({ operation }: { operation: string | null }) {
  const tone = OPERATION_TONES[operationTone(operation)];
  return (
    <View style={[LEGACY_UI.tag, { backgroundColor: tone.background, borderColor: tone.border }]}>
      <Text style={[LEGACY_UI.tagText, { color: tone.text }]}>{operation || "-"}</Text>
    </View>
  );
}

export function DangerBadge({ label }: { label: string }) {
  return (
    <View style={[LEGACY_UI.pill, { backgroundColor: RISK.danger }]}>
      <Icon source="alert-octagon-outline" size={12} color={HB_COLORS.white} />
      <Text style={[LEGACY_UI.pillText, { color: HB_COLORS.white }]}>{label}</Text>
    </View>
  );
}

export function RuleBadge({ label }: { label: string }) {
  return (
    <View style={[LEGACY_UI.pill, { backgroundColor: "#FFE3A3", borderWidth: 1, borderColor: RISK.abnormalBorder }]}>
      <Icon source="pulse" size={12} color={RISK.abnormalText} />
      <Text style={[LEGACY_UI.pillText, { color: RISK.abnormalText }]}>{label}</Text>
    </View>
  );
}

/** 核查结论徽标：确认正常灰色、需跟进紫色；未核查或已撤销不显示。 */
export function ReviewBadge({ item, reviewedLabel, followUpLabel }: { item: Pick<LegacyLogItem, "review">; reviewedLabel: string; followUpLabel: string }) {
  const review = activeReview(item.review);
  if (!review) return null;
  const followUp = review.result === "followUp";
  return (
    <View style={[LEGACY_UI.pill, { backgroundColor: followUp ? RISK.followUp : "#E9ECEF" }]}>
      <Icon source={followUp ? "flag-outline" : "check"} size={12} color={followUp ? HB_COLORS.white : "#2F3742"} />
      <Text style={[LEGACY_UI.pillText, { color: followUp ? HB_COLORS.white : "#2F3742" }]}>{followUp ? followUpLabel : reviewedLabel}</Text>
    </View>
  );
}

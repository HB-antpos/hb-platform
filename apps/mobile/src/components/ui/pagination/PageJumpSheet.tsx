import { useEffect, useState } from "react";
import { Pressable, StyleSheet, TextInput, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { getPageGridNumbers, parsePageInput } from "./pagination-logic";

export interface PageJumpSheetProps {
  visible: boolean;
  page: number;
  pageCount: number;
  total: number;
  pageSize: number;
  pageSizeOptions: readonly number[];
  /** 输入页码确认或点页码格子时调用；调用方负责关闭面板与翻页 */
  onJump: (page: number) => void;
  onPageSizeChange: (pageSize: number) => void;
  onDismiss: () => void;
  /** 覆盖「每页条数」下方的说明文案（如「本机会记住你的选择」） */
  pageSizeHint?: string;
  /** 测试标识前缀，默认 "page-jump" */
  testID?: string;
}

/** 跳页 + 每页条数：输入页码或点页码格子跳转；页数多于 40 页时只保留输入框。 */
export function PageJumpSheet({ visible, page, pageCount, total, pageSize, pageSizeOptions, onJump, onPageSizeChange, onDismiss, pageSizeHint, testID = "page-jump" }: PageJumpSheetProps) {
  const { t } = useAppTranslation("common");
  const [input, setInput] = useState("");
  // 每次打开都从当前页码开始，上次没提交的输入不残留
  useEffect(() => { if (visible) setInput(String(page)); }, [visible, page]);
  const target = parsePageInput(input, pageCount);
  // 输入了内容但不在 1..pageCount 内才提示；清空输入时不报错，只是确认按钮置灰
  const invalid = input.length > 0 && target === null;
  const pageNumbers = getPageGridNumbers(pageCount);
  const submit = () => { if (target) onJump(target); };

  return <BusinessSheet visible={visible} title={t("pagination.jumpTitle")} subtitle={t("pagination.jumpSubtitle", { total: total.toLocaleString("en-AU"), page, pageCount })} onDismiss={onDismiss}>
    <View style={styles.gotoRow}>
      <View style={[styles.gotoBox, invalid && styles.gotoBoxInvalid]}>
        <TextInput
          testID={`${testID}-input`}
          value={input}
          onChangeText={(value) => setInput(value.replace(/[^\d]/g, ""))}
          placeholder={t("pagination.jumpPlaceholder")}
          placeholderTextColor={HB_COLORS.outline}
          keyboardType="number-pad"
          returnKeyType="go"
          selectTextOnFocus
          accessibilityLabel={t("pagination.jumpPlaceholder")}
          onSubmitEditing={submit}
          style={styles.gotoInput}
        />
        <Text style={styles.gotoOf}>{t("pagination.jumpOf", { pageCount })}</Text>
      </View>
      <Button testID={`${testID}-confirm`} mode="contained" disabled={!target} onPress={submit} contentStyle={styles.gotoButtonContent}>{t("pagination.confirm")}</Button>
    </View>
    {invalid ? <Text testID={`${testID}-invalid`} style={styles.invalid}>{t("pagination.invalidPage", { pageCount })}</Text> : null}
    {pageNumbers.length > 0 ? <View style={styles.pageGrid}>
      {pageNumbers.map((number) => <Pressable
        key={number}
        testID={`${testID}-page-${number}`}
        accessibilityRole="button"
        accessibilityState={{ selected: number === page }}
        onPress={() => onJump(number)}
        style={[styles.pageCell, number === page && styles.pageCellCurrent]}
      >
        <Text style={[styles.pageCellText, number === page && styles.pageCellTextCurrent]}>{number}</Text>
      </Pressable>)}
    </View> : null}

    <Text style={styles.sectionLabel}>{t("pagination.pageSizeTitle")}</Text>
    <View style={styles.segment}>
      {pageSizeOptions.map((option, index) => {
        const selected = option === pageSize;
        return <Pressable
          key={option}
          testID={`${testID}-size-${option}`}
          accessibilityRole="radio"
          accessibilityLabel={t("pagination.perPageOption", { count: option })}
          accessibilityState={{ checked: selected }}
          onPress={() => onPageSizeChange(option)}
          style={[styles.segmentButton, index > 0 && styles.segmentDivider, selected && styles.segmentSelected]}
        >
          <Text style={[styles.segmentText, selected && styles.segmentTextSelected]}>{option}</Text>
        </Pressable>;
      })}
    </View>
    <Text style={styles.hint}>{pageSizeHint ?? t("pagination.pageSizeHint")}</Text>
  </BusinessSheet>;
}

const styles = StyleSheet.create({
  sectionLabel: { color: HB_COLORS.textSecondary, fontSize: 13, fontWeight: "600", marginTop: HB_SPACING.xxs },
  gotoRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  gotoBox: { flex: 1, height: 50, flexDirection: "row", alignItems: "center", gap: 6, paddingHorizontal: HB_SPACING.sm, borderWidth: 2, borderColor: HB_COLORS.action, borderRadius: HB_RADIUS.surface },
  gotoBoxInvalid: { borderColor: HB_COLORS.danger },
  gotoInput: { flex: 1, minWidth: 0, fontSize: 20, fontWeight: "700", color: HB_COLORS.textPrimary, paddingVertical: 0 },
  gotoOf: { color: HB_COLORS.textSecondary, fontSize: 16 },
  gotoButtonContent: { minHeight: 50, paddingHorizontal: HB_SPACING.xs },
  invalid: { color: HB_COLORS.danger, fontSize: 12 },
  pageGrid: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  pageCell: { width: 56, height: 44, alignItems: "center", justifyContent: "center", borderWidth: 1, borderColor: HB_COLORS.outline, borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.white },
  pageCellCurrent: { backgroundColor: HB_COLORS.action, borderColor: HB_COLORS.action },
  pageCellText: { color: HB_COLORS.textPrimary, fontSize: 15 },
  pageCellTextCurrent: { color: HB_COLORS.white, fontWeight: "700" },
  segment: { flexDirection: "row", borderWidth: 1, borderColor: HB_COLORS.outline, borderRadius: HB_RADIUS.surface, overflow: "hidden" },
  segmentButton: { flex: 1, minHeight: 46, alignItems: "center", justifyContent: "center", paddingHorizontal: 4, backgroundColor: HB_COLORS.white },
  segmentDivider: { borderLeftWidth: 1, borderLeftColor: HB_COLORS.outline },
  segmentSelected: { backgroundColor: HB_COLORS.action },
  segmentText: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "600" },
  segmentTextSelected: { color: HB_COLORS.white },
  hint: { color: HB_COLORS.textSecondary, fontSize: 12 },
});

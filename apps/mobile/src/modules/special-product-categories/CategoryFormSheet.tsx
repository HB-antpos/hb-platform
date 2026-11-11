import { useEffect, useState } from "react";
import { Alert, Pressable, StyleSheet, View } from "react-native";
import { Button, Icon, Text, TextInput } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { CATEGORY_COLORS, CATEGORY_NAME_MAX_LENGTH, validateCategoryName } from "./logic";
import { SC_COLORS } from "./shared";
import type { SpecialCategory } from "./types";

interface CategoryFormSheetProps {
  visible: boolean;
  /** 传入 = 编辑（可改名 / 换颜色 / 删除）；不传 = 新建。 */
  category?: SpecialCategory | null;
  /** 本店全部分类，用来做「同店不重名」的即时校验（后端仍会再校验）。 */
  existing: readonly SpecialCategory[];
  /** 提交保存；抛错由本组件显示 errorText 转出的文案。 */
  onSubmit: (input: { name: string; color: string | null }) => Promise<void>;
  onDelete?: () => Promise<void>;
  onDismiss: () => void;
  errorText: (cause: unknown, fallbackKey: string) => string;
}

/** 新建 / 编辑分类：名称（1–30 字符）+ 颜色；编辑时多一个「删除分类」（二次确认，说明商品回到未分类）。 */
export function CategoryFormSheet({ visible, category, existing, onSubmit, onDelete, onDismiss, errorText }: CategoryFormSheetProps) {
  const { t } = useAppTranslation("specialProductCategories");
  const [name, setName] = useState("");
  const [color, setColor] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [message, setMessage] = useState("");
  const editing = Boolean(category);

  // 每次打开重置为当前分类（或空白）的值。
  useEffect(() => {
    if (!visible) return;
    setName(category?.name ?? "");
    setColor(category?.color ?? null);
    setMessage("");
    setSubmitting(false);
  }, [visible, category]);

  const validation = validateCategoryName(name, existing, category?.id);
  const issueText = !validation.ok && name.length > 0 ? t(`form.issues.${validation.issue}`) : "";
  const unchanged = editing && validation.ok && validation.name === category?.name && color === (category?.color ?? null);

  const submit = async () => {
    if (!validation.ok || submitting) return;
    setSubmitting(true);
    setMessage("");
    try {
      await onSubmit({ name: validation.name, color });
    } catch (cause) {
      setMessage(errorText(cause, editing ? "form.saveFailed" : "form.createFailed"));
      setSubmitting(false);
    }
  };

  const confirmDelete = () => {
    if (!category || !onDelete || submitting) return;
    Alert.alert(
      t("form.deleteConfirmTitle", { name: category.name }),
      t("form.deleteConfirmBody", { count: category.itemCount }),
      [
        { text: t("actions.cancel"), style: "cancel" },
        {
          text: t("form.deleteAction"),
          style: "destructive",
          onPress: () => {
            setSubmitting(true);
            setMessage("");
            onDelete().catch((cause) => {
              setMessage(errorText(cause, "form.deleteFailed"));
              setSubmitting(false);
            });
          },
        },
      ],
    );
  };

  return (
    <BusinessSheet
      visible={visible}
      title={t(editing ? "form.editTitle" : "form.createTitle")}
      subtitle={t("form.subtitle")}
      onDismiss={onDismiss}
      dismissable={!submitting}
      footer={
        <View style={styles.footer}>
          {message ? (
            <Text variant="bodySmall" style={styles.error} accessibilityLiveRegion="polite">
              {message}
            </Text>
          ) : null}
          <Button
            mode="contained"
            loading={submitting}
            disabled={submitting || !validation.ok || unchanged}
            onPress={() => void submit()}
            contentStyle={styles.buttonContent}
          >
            {t("actions.save")}
          </Button>
          {editing && onDelete ? (
            <Button mode="text" textColor={HB_COLORS.danger} icon="trash-can-outline" disabled={submitting} onPress={confirmDelete}>
              {t("form.deleteAction")}
            </Button>
          ) : null}
        </View>
      }
    >
      <TextInput
        mode="outlined"
        label={t("form.nameLabel")}
        value={name}
        onChangeText={setName}
        maxLength={CATEGORY_NAME_MAX_LENGTH + 10}
        autoFocus={!editing}
        returnKeyType="done"
        onSubmitEditing={() => void submit()}
        error={Boolean(issueText)}
        right={<TextInput.Affix text={`${Array.from(name.trim()).length}/${CATEGORY_NAME_MAX_LENGTH}`} />}
        accessibilityLabel={t("form.nameLabel")}
      />
      {issueText ? (
        <Text variant="bodySmall" style={styles.error}>
          {issueText}
        </Text>
      ) : null}

      <Text style={styles.sectionLabel}>{t("form.colorLabel")}</Text>
      <View style={styles.colors}>
        <Pressable
          accessibilityRole="radio"
          accessibilityLabel={t("form.noColor")}
          accessibilityState={{ selected: color === null }}
          onPress={() => setColor(null)}
          style={[styles.swatch, styles.swatchNone, color === null ? styles.swatchSelected : null]}
        >
          <Icon source="cancel" size={18} color={HB_COLORS.textSecondary} />
        </Pressable>
        {CATEGORY_COLORS.map((value) => (
          <Pressable
            key={value}
            accessibilityRole="radio"
            accessibilityLabel={value}
            accessibilityState={{ selected: color === value }}
            onPress={() => setColor(value)}
            style={[styles.swatch, { backgroundColor: value }, color === value ? styles.swatchSelected : null]}
          >
            {color === value ? <Icon source="check" size={18} color={HB_COLORS.white} /> : null}
          </Pressable>
        ))}
      </View>
      {editing ? <Text style={styles.hint}>{t("form.editHint")}</Text> : null}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  footer: { gap: HB_SPACING.xs },
  buttonContent: { minHeight: 44 },
  error: { color: HB_COLORS.danger },
  sectionLabel: { fontSize: 13, fontWeight: "700", color: HB_COLORS.textPrimary, marginTop: HB_SPACING.xs },
  colors: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.sm },
  swatch: { width: 40, height: 40, borderRadius: 20, alignItems: "center", justifyContent: "center", borderWidth: 2, borderColor: "transparent" },
  swatchNone: { backgroundColor: HB_COLORS.surfaceMuted, borderColor: HB_COLORS.outline },
  swatchSelected: { borderColor: HB_COLORS.textPrimary },
  hint: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary, backgroundColor: SC_COLORS.blueBg, borderRadius: HB_RADIUS.control, padding: HB_SPACING.xs },
});

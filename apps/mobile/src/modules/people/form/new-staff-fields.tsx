/**
 * 新建员工共用的「电话 / 生日 / 监护人」界面（设计图 7、9）：店长「新增员工」与管理员「新增账号」两个整页复用。
 * 规则与错误码见 new-staff-profile.ts；这里只负责展示与把错误码翻成 peopleForm 文案。
 */
import { useMemo, useState } from "react";
import { StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";

import { FormField } from "./form-ui";
import {
  NEW_STAFF_GUARDIAN_RELATIONSHIPS,
  NEW_STAFF_MIN_BIRTH_YEAR,
  resolveNewStaffAge,
  type NewStaffBirthdayError,
  type NewStaffGuardianErrors,
  type NewStaffGuardianInput,
  type NewStaffPhoneError,
} from "./new-staff-profile";
import { FilterChipRow, PeopleTag } from "../ui";
import { BirthdayPickerDialog } from "@/modules/employee-profile/BirthdayPickerField";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

/** 员工本人电话：必填，04 开头的澳洲手机号。 */
export function NewStaffPhoneField({
  value,
  onChange,
  error,
  disabled = false,
}: {
  value: string;
  onChange: (value: string) => void;
  error?: NewStaffPhoneError;
  disabled?: boolean;
}) {
  const { t } = useAppTranslation("peopleForm");
  return (
    <FormField
      label={t("name.phone")}
      required
      value={value}
      onChangeText={onChange}
      placeholder="04xx xxx xxx"
      keyboardType="phone-pad"
      textContentType="telephoneNumber"
      maxLength={50}
      error={error ? t(error === "required" ? "newStaff.errors.phoneRequired" : "newStaff.errors.phoneInvalid") : undefined}
      hint={t("newStaff.phoneHint")}
      disabled={disabled}
      testID="new-staff-phone"
    />
  );
}

/**
 * 生日：点开纯 JS 年/月/日弹窗（复用员工资料的 BirthdayPickerDialog），最早 1940 年、最晚悉尼今天。
 * 右侧显示年龄：成年灰色「N 岁」，未满 18 岁琥珀色「N 岁 · 未成年」。
 */
export function NewStaffBirthdayField({
  value,
  onChange,
  today,
  error,
  disabled = false,
}: {
  value: string;
  onChange: (value: string) => void;
  /** 悉尼当天（由页面算一次传入，年龄与可选范围都以它为准）。 */
  today: Date;
  error?: NewStaffBirthdayError;
  disabled?: boolean;
}) {
  const { t } = useAppTranslation("peopleForm");
  const [open, setOpen] = useState(false);
  const age = useMemo(() => resolveNewStaffAge(value, today), [today, value]);

  const ageTag = age ? (
    <PeopleTag
      label={age.minor ? t("newStaff.minorAge", { age: age.age }) : t("newStaff.age", { age: age.age })}
      tone={age.minor ? "amber" : "gray"}
      icon={age.minor ? "shield-alert-outline" : undefined}
    />
  ) : null;

  return (
    <>
      <FormField
        label={t("newStaff.birthday")}
        required
        value={value}
        placeholder={t("newStaff.birthdayPlaceholder")}
        onPress={() => setOpen(true)}
        error={error ? t(error === "required" ? "newStaff.errors.birthdayRequired" : "newStaff.errors.birthdayInvalid") : undefined}
        hint={t("newStaff.birthdayHint")}
        adornment={ageTag}
        trailing={<Icon source="calendar-month-outline" size={18} color="#98A2B3" />}
        disabled={disabled}
        testID="new-staff-birthday"
      />
      <BirthdayPickerDialog
        visible={open}
        value={value}
        today={today}
        minYear={NEW_STAFF_MIN_BIRTH_YEAR}
        onDismiss={() => setOpen(false)}
        onConfirm={(next) => { onChange(next); setOpen(false); }}
      />
    </>
  );
}

const GUARDIAN_ERROR_KEYS: Record<keyof NewStaffGuardianErrors, Record<string, string>> = {
  name: { required: "newStaff.errors.guardianNameRequired" },
  relationship: { required: "newStaff.errors.guardianRelationshipRequired" },
  phone: {
    required: "newStaff.errors.guardianPhoneRequired",
    invalid: "newStaff.errors.guardianPhoneInvalid",
    sameAsStaff: "newStaff.errors.guardianPhoneSame",
  },
  email: {
    required: "newStaff.errors.guardianEmailRequired",
    invalid: "newStaff.errors.guardianEmailInvalid",
    sameAsStaff: "newStaff.errors.guardianEmailSame",
  },
};

/**
 * 「监护人」一节（黄色边框卡片）：只在生日未满 18 岁时由页面渲染。
 * 姓名、关系（四个选项，提交英文值）、手机（04 开头）、邮箱四项必填。
 */
export function NewStaffGuardianSection({
  values,
  onChange,
  errors,
  disabled = false,
}: {
  values: NewStaffGuardianInput;
  onChange: (key: keyof NewStaffGuardianInput, value: string) => void;
  errors: NewStaffGuardianErrors;
  disabled?: boolean;
}) {
  const { t } = useAppTranslation("peopleForm");
  const message = (field: keyof NewStaffGuardianErrors) => {
    const code = errors[field];
    const key = code ? GUARDIAN_ERROR_KEYS[field][code] : undefined;
    return key ? t(key) : undefined;
  };
  const relationshipItems = NEW_STAFF_GUARDIAN_RELATIONSHIPS.map((option) => ({
    key: option as string,
    label: t(`newStaff.relationships.${option}`),
  }));

  return (
    <View style={styles.guardianCard} testID="new-staff-guardian">
      <View style={styles.guardianTitleRow}>
        <Icon source="shield-account-outline" size={18} color={HB_COLORS.warning} />
        <Text style={styles.guardianTitle} accessibilityRole="header">{t("newStaff.guardian.title")}</Text>
      </View>
      <Text style={styles.guardianDescription}>{t("newStaff.guardian.description")}</Text>

      <FormField
        label={t("newStaff.guardian.name")}
        required
        value={values.name}
        onChangeText={(value) => onChange("name", value)}
        autoCapitalize="words"
        maxLength={200}
        error={message("name")}
        disabled={disabled}
        testID="new-staff-guardian-name"
      />

      <View style={styles.relationship}>
        <Text style={styles.fieldLabel}>
          {t("newStaff.guardian.relationship")}
          <Text style={styles.required}>{" *"}</Text>
        </Text>
        <View pointerEvents={disabled ? "none" : "auto"} style={disabled ? styles.disabled : undefined}>
          <FilterChipRow items={relationshipItems} value={values.relationship} onChange={(key) => onChange("relationship", key)} />
        </View>
        {message("relationship") ? (
          <Text style={styles.errorText} accessibilityLiveRegion="polite">{message("relationship")}</Text>
        ) : null}
      </View>

      <FormField
        label={t("newStaff.guardian.phone")}
        required
        value={values.phone}
        onChangeText={(value) => onChange("phone", value)}
        placeholder="04xx xxx xxx"
        keyboardType="phone-pad"
        maxLength={50}
        error={message("phone")}
        hint={t("newStaff.guardian.phoneHint")}
        disabled={disabled}
        testID="new-staff-guardian-phone"
      />
      <FormField
        label={t("newStaff.guardian.email")}
        required
        value={values.email}
        onChangeText={(value) => onChange("email", value)}
        placeholder={t("newStaff.guardian.emailPlaceholder")}
        keyboardType="email-address"
        textContentType="emailAddress"
        maxLength={254}
        error={message("email")}
        disabled={disabled}
        testID="new-staff-guardian-email"
      />
    </View>
  );
}

const styles = StyleSheet.create({
  guardianCard: {
    backgroundColor: HB_COLORS.white,
    borderColor: "#FEC84B",
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    gap: HB_SPACING.sm,
    padding: 14,
  },
  guardianTitleRow: { alignItems: "center", flexDirection: "row", gap: 6 },
  guardianTitle: { color: HB_COLORS.textPrimary, fontSize: 15, fontWeight: "800" },
  guardianDescription: { color: HB_COLORS.textSecondary, fontSize: 12.5, lineHeight: 18, marginTop: -4 },
  relationship: { gap: 4 },
  fieldLabel: { color: HB_COLORS.textSecondary, fontSize: 12.5, fontWeight: "700" },
  required: { color: HB_COLORS.danger },
  errorText: { color: HB_COLORS.danger, fontSize: 12, lineHeight: 16 },
  disabled: { opacity: 0.5 },
});

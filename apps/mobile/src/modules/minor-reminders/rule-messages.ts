/**
 * 后端 MinorEmploymentComplianceCalculator 生成的提示正文只有中文；
 * 英文界面按 ruleId 显示对应英文，未知规则回退为后端原文。
 * 打卡页（TodayPunchCard）与提醒待办页（MinorRemindersScreen）共用这一份，避免两处各自漏翻。
 */
export const RULE_MESSAGES_EN: Record<string, string> = {
  // —— 档案资料不完整（incomplete）——
  PROFILE_MISSING:
    "No minor compliance profile found, so school, sign-off and outside-work arrangements can't be confirmed.",
  WORK_STATE_UNKNOWN:
    "The work state is unknown, so the applicable minor employment rules can't be determined.",
  DATE_OF_BIRTH_MISSING:
    "Date of birth is missing, so the applicable age range can't be confirmed.",
  CONSENT_NOT_READY:
    "Parent/guardian consent or sign-off is incomplete or has been returned.",
  HR_REVIEW_NOT_READY:
    "The compliance profile is awaiting HR review or has been returned. Rostering can still be published.",
  QLD_SCHOOL_AGED_UNKNOWN:
    "It isn't confirmed whether this is a QLD school-age child, so the QLD hour limits can't be confirmed.",
  EXTERNAL_WORK_UNKNOWN:
    "Outside work isn't confirmed, so total daily/weekly hours can't be confirmed.",
  SCHOOL_CALENDAR_MISSING:
    "No personal school calendar, so the 4-hour or 8-hour daily limit can't be confirmed.",
  SCHOOL_WEEK_UNKNOWN:
    "The personal school calendar is incomplete, so the 12-hour or 38-hour weekly limit can't be confirmed.",
  // —— 需核验 / 超限 ——
  QLD_ORDINARY_RETAIL_MINIMUM_AGE:
    "Under 13 for ordinary QLD retail work. HR must check for a special permit or exemption. Rostering can continue.",
  NSW_AWARD_REVIEW:
    "The QLD 4/8/12/38-hour limits don't apply to NSW retail. Check against school arrangements, the applicable award and company policy.",
  AWARD_BREAK_REVIEW:
    "Worked 4 hours in a row. Check breaks under the applicable award. No exemption has been assumed.",
  QLD_PARTICIPATION_REVIEW:
    "Not covered by the QLD school-age hour limits. School arrangements and the applicable award still need checking.",
  QLD_SCHOOL_DAY_DAILY_LIMIT:
    "Daily hours exceed the QLD limit for school-age children.",
  QLD_NON_SCHOOL_DAY_DAILY_LIMIT:
    "Daily hours exceed the QLD limit for school-age children.",
  QLD_SCHOOL_WEEKLY_LIMIT:
    "Weekly hours (week starting Sunday) exceed the QLD limit for school-age children.",
  QLD_NON_SCHOOL_WEEKLY_LIMIT:
    "Weekly hours (week starting Sunday) exceed the QLD limit for school-age children.",
  QLD_NIGHT_WORK: "Shift falls within the QLD restricted hours of 10 pm – 6 am.",
  QLD_SAME_EMPLOYER_REST:
    "Less than 12 hours between consecutive shifts with the same employer.",
  SCHOOL_ATTENDANCE_CONFLICT: "Shift overlaps the declared school hours.",
  COMMUTE_SCHOOL_CONFLICT:
    "Not enough travel time from school to the store. This is a safety reminder, not a cut to legal hours.",
  COMMUTE_LATEST_END_CONFLICT:
    "Shift ends after the declared latest safe finish time.",
  COMMUTE_HOMEWARD_CONFLICT:
    "The trip home after this shift misses the declared last transport option.",
  AWARD_UNKNOWN:
    "The applicable award or agreement is unknown. HR needs to check break and minimum shift rules.",
};

/** 英文界面且有对应 ruleId 时返回英文，否则回退后端原文（中文界面始终用后端原文）。 */
export function localizeMinorRuleMessage(
  finding: { ruleId?: string; message: string },
  language: string,
): string {
  if (language.startsWith("en") && finding.ruleId) {
    const english = RULE_MESSAGES_EN[finding.ruleId];
    if (english) return english;
  }
  return finding.message;
}

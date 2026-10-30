import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { RULE_MESSAGES_EN, localizeMinorRuleMessage } from "./rule-messages";

const directory = dirname(fileURLToPath(import.meta.url));

// 英文界面：已知 ruleId 显示英文；未知规则回退后端原文
assert.equal(
  localizeMinorRuleMessage({ ruleId: "DATE_OF_BIRTH_MISSING", message: "缺少出生日期" }, "en"),
  RULE_MESSAGES_EN.DATE_OF_BIRTH_MISSING,
);
assert.equal(localizeMinorRuleMessage({ ruleId: "UNKNOWN_RULE", message: "原文" }, "en"), "原文");
assert.equal(localizeMinorRuleMessage({ ruleId: "", message: "原文" }, "en"), "原文");
// 中文界面始终用后端原文
assert.equal(
  localizeMinorRuleMessage({ ruleId: "DATE_OF_BIRTH_MISSING", message: "缺少出生日期" }, "zh"),
  "缺少出生日期",
);

// 后端计算器里出现的每个 ruleId 都必须有英文，防止新增规则漏翻
const calculator = readFileSync(
  resolve(directory, "../../../../../services/backend/BlazorApp.Api/Services/Attendance/MinorEmploymentComplianceCalculator.cs"),
  "utf8",
);
const backendRuleIds = new Set<string>();
for (const match of calculator.matchAll(/(?:Incomplete|Review|Warning)\("([A-Z_]+)"/g)) backendRuleIds.add(match[1]);
for (const match of calculator.matchAll(/"(QLD_[A-Z_]+)"/g)) backendRuleIds.add(match[1]);
assert.ok(backendRuleIds.size >= 20, "应能从后端提取到全部规则");
for (const id of backendRuleIds) assert.ok(RULE_MESSAGES_EN[id], `缺少 ${id} 的英文文案`);

// 打卡页必须走共用翻译，不能直接显示后端中文原文
const card = readFileSync(resolve(directory, "../../components/attendance/TodayPunchCard.tsx"), "utf8");
assert.match(card, /localizeMinorRuleMessage\(finding, language\)/);
assert.doesNotMatch(card, /\{finding\.message\}/);

console.log("rule-messages.test.ts: ok");

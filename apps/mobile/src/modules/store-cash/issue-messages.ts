// 校验问题码 → i18n 键。纯映射，文案在 locales/*/screens/storeCash.json 的 errors.* 下。
import type { BalanceIssue } from "./balance-form";
import type { DepositIssue } from "./deposit-form";
import type { ExpenseIssue } from "./expense-form";
import type { MoneyParseIssue } from "./money";

export const MONEY_ISSUES: readonly MoneyParseIssue[] = [
  "empty",
  "invalid",
  "tooManyDecimals",
  "negative",
  "notPositive",
  "tooLarge",
];

export const DEPOSIT_ISSUE_CODES: readonly Exclude<DepositIssue["code"], "slipAmount">[] = [
  "dateInvalid",
  "dateOutOfRange",
  "coveredIncomplete",
  "coveredInvalid",
  "coveredReversed",
  "coveredFuture",
  "noteTooLong",
  "slipsEmpty",
  "slipsTooMany",
  "slipPhotosMissing",
  "slipPhotosTooMany",
  "slipNoTooLong",
  "overrideReasonRequired",
];

export const EXPENSE_ISSUE_CODES: readonly Exclude<ExpenseIssue["code"], "amount">[] = [
  "dateInvalid",
  "dateOutOfRange",
  "categoryMissing",
  "receiptRequired",
  "photosTooMany",
  "payeeTooLong",
  "noteTooLong",
];

export const BALANCE_ISSUE_CODES: readonly Exclude<BalanceIssue["code"], "amount">[] = [
  "dateInvalid",
  "dateOutOfRange",
  "noteTooLong",
];

export const moneyIssueKey = (issue: MoneyParseIssue) => `errors.money.${issue}`;

export const depositIssueKey = (issue: DepositIssue) =>
  issue.code === "slipAmount" ? moneyIssueKey(issue.issue) : `errors.deposit.${issue.code}`;

export const expenseIssueKey = (issue: ExpenseIssue) =>
  issue.code === "amount" ? moneyIssueKey(issue.issue) : `errors.expense.${issue.code}`;

export const balanceIssueKey = (issue: BalanceIssue) =>
  issue.code === "amount" ? moneyIssueKey(issue.issue) : `errors.balance.${issue.code}`;

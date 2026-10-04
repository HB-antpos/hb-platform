import { mapSummary } from "./contract";
import type { MinorEmploymentReviewSummary } from "./types";

export type HrReviewStatus = "Submitted" | "Approved" | "Returned";
export type HrReviewPage = {
  items: MinorEmploymentReviewSummary[];
  total: number;
  page: number;
  pageSize: number;
};
function unwrap(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) return {};
  const root = value as Record<string, unknown>;
  const data = root.data ?? root.Data;
  return data && typeof data === "object" && !Array.isArray(data)
    ? (data as Record<string, unknown>)
    : root;
}
function number(value: unknown, fallback: number) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}
export function mapHrPagePayload(
  raw: unknown,
  page: number,
  pageSize: number,
): HrReviewPage {
  const body = unwrap(raw);
  const values = body.items ?? body.Items ?? (Array.isArray(raw) ? raw : []);
  const items = Array.isArray(values) ? values.map(mapSummary) : [];
  return {
    items,
    total: number(body.total ?? body.Total, items.length),
    page: number(body.page ?? body.Page, page),
    pageSize: number(body.pageSize ?? body.PageSize, pageSize),
  };
}
export function mapHistoryPayload(raw: unknown): unknown[] {
  const body = unwrap(raw);
  const values =
    body.items ??
    body.Items ??
    body.versions ??
    body.Versions ??
    (Array.isArray(raw) ? raw : []);
  return Array.isArray(values) ? values : [];
}
export function toBackendHrStatus(status: HrReviewStatus) {
  return status === "Submitted"
    ? "pending_hr_review"
    : status === "Approved"
      ? "approved"
      : "returned";
}

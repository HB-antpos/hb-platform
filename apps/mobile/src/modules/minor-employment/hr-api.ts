import { apiClient } from "@/shared/api/client";
import { mapProfile, mapSummary } from "./contract";
import type {
  MinorEmploymentReviewDetail,
  MinorEmploymentReviewSummary,
  MinorReturnField,
} from "./types";
import * as hrContract from "./hr-contract";

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
export const minorEmploymentHrApi = {
  async getReviews(
    status: HrReviewStatus,
    page = 1,
    pageSize = 20,
  ): Promise<HrReviewPage> {
    const raw = (
      await apiClient.get("/minor-employment/hr", {
        params: {
          status: hrContract.toBackendHrStatus(status),
          page,
          pageSize,
        },
      })
    ).data;
    return hrContract.mapHrPagePayload(raw, page, pageSize);
  },
  async getReview(id: string): Promise<MinorEmploymentReviewDetail> {
    return mapProfile(
      (await apiClient.get(`/minor-employment/hr/${encodeURIComponent(id)}`))
        .data,
    ) as MinorEmploymentReviewDetail;
  },
  async approve(id: string, version: number) {
    return mapProfile(
      (
        await apiClient.post(
          `/minor-employment/hr/${encodeURIComponent(id)}/approve`,
          { version, returnFields: [], comment: null },
        )
      ).data,
    );
  },
  async returnForCorrection(
    id: string,
    version: number,
    fields: MinorReturnField[],
    comment: string,
  ) {
    return mapProfile(
      (
        await apiClient.post(
          `/minor-employment/hr/${encodeURIComponent(id)}/return`,
          { version, returnFields: fields, comment },
        )
      ).data,
    );
  },
  async getHistory(id: string): Promise<unknown[]> {
    return hrContract.mapHistoryPayload(
      (
        await apiClient.get(
          `/minor-employment/hr/${encodeURIComponent(id)}/history`,
        )
      ).data,
    );
  },
};

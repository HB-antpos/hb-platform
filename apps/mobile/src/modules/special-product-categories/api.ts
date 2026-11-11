import { apiClient } from "@/shared/api/client";
import { createSpecialCategoriesApi } from "./api-contract";

export const specialCategoriesApi = createSpecialCategoriesApi(apiClient);
export { SPECIAL_CATEGORIES_BASE_URL } from "./api-contract";

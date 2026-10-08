import { createPageSizeStorage } from "@/components/ui/pagination/page-size-storage";
import { CONTAINER_NEW_PRODUCTS_PAGE_SIZE, CONTAINER_NEW_PRODUCTS_PAGE_SIZE_OPTIONS } from "./pagination";

/** 记住 HB新品 每页条数；沿用旧 key，用户已保存的选择不会被重置。读写失败只是不记住，不影响页面。 */
const storage = createPageSizeStorage({
  key: "hb.containerNewProducts.pageSize",
  options: CONTAINER_NEW_PRODUCTS_PAGE_SIZE_OPTIONS,
  defaultSize: CONTAINER_NEW_PRODUCTS_PAGE_SIZE,
});

export const readRememberedPageSize = storage.read;
export const peekRememberedPageSize = storage.peek;
export const rememberPageSize = storage.remember;

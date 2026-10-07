import { CloudSyncOutlined, CopyOutlined, DownloadOutlined, EditOutlined, GiftOutlined, HistoryOutlined, LoadingOutlined, MoreOutlined, PictureOutlined, PlusOutlined, SearchOutlined, TagsOutlined, UploadOutlined } from '@ant-design/icons';
import { DndContext, PointerSensor, closestCenter, type DragEndEvent, useSensor, useSensors, } from '@dnd-kit/core';
import { SortableContext, horizontalListSortingStrategy, useSortable, } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { Alert, Button, Checkbox, Dropdown, Form, Input, InputNumber, Modal, Popconfirm, Select, Space, Switch, Tag, Tooltip, TreeSelect, Typography, message, notification, } from 'antd';
import type { MenuProps } from 'antd';
import type { DefaultOptionType } from 'antd/es/select';
import type { ColumnsType, TablePaginationConfig } from 'antd/es/table';
import type { FilterDropdownProps, FilterValue, SorterResult } from 'antd/es/table/interface';
import type { CSSProperties, HTMLAttributes, ReactNode } from 'react';
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import BarcodePreview from '../../../components/BarcodePreview';
import ProductListImage from '../../../components/ProductListImage';
import PageContainer from '../../../components/PageContainer';
import ActiveFilterBar from '../../../components/listToolbar/ActiveFilterBar';
import MoreFiltersButton from '../../../components/listToolbar/MoreFiltersButton';
import SelectionActionBar from '../../../components/listToolbar/SelectionActionBar';
import StatusPill from '../../../components/listToolbar/StatusPill';
import StatusTabs from '../../../components/listToolbar/StatusTabs';
import ToolbarMenuButton from '../../../components/listToolbar/ToolbarMenuButton';
import { requiresDelistSupplyNotice } from '../../../components/SupplyNotice/delistSupplyNoticeGate';
import { getSupplierOptions, } from '../../../services/domesticProductService';
import { exportDomesticProductsToExcel, type ExportResult } from '../../../services/exportService';
import { getActiveLocalSuppliers as getActiveAustralianSuppliers } from '../../../services/localSupplierService';
import { batchCreateSetCodes, batchDelete as batchDeleteSetCodes, batchUpdateBarcodes as batchUpdateSetBarcodes, batchUpdatePrices as batchUpdateSetPrices, batchUpdateStatus as batchUpdateSetStatus, getGridData as getSetCodeGridData, } from '../../../services/multiCodeSetService';
import { getPushToHqStoreOptions, pushProductsToHq } from '../../../services/posProductService';
import dayjs from 'dayjs';
import SupplyNoticeModal from '../../../components/SupplyNotice/SupplyNoticeModal';
import { formatSupplyExpected } from '../../../components/SupplyNotice/formatSupplyExpected';
import { queryWarehouseSupplyNotices, upsertWarehouseSupplyNotices } from '../../../services/supplyNoticeService';
import type { SupplyNoticeInput, WarehouseSupplyNotice } from '../../../types/supplyNotice';
import { HqProductSyncPollingCancelledError, HqProductSyncPollingTimeoutError, batchToggleWarehouseProductsActive, createWarehouseProductBatchUpdateJob, createWarehouseProductBatchUpdateJobPoller, getWarehouseProductBatchUpdateJob, getWarehouseProductsTable, patchWarehouseProduct, updateWarehouseProductFull, type PatchWarehouseProductPayload, type WarehouseProductBatchUpdateItem, type WarehouseProductBatchUpdateJobResult, type WarehouseProductBatchUpdateJobStatus, type WarehouseProductListItem, type WarehouseProductsTableQuery, } from '../../../services/warehouseProductService';
import { batchAssignProducts, getCategoryTree, type WarehouseCategoryNode, } from '../../../services/warehouseCategoryService';
import { useAuthStore } from '../../../store/auth';
import type { SupplierOption, } from '../../../types/domesticProduct';
import { ProductType, ProductTypeLabels } from '../../../types/domesticProduct';
import type { MulticodeSetItem } from '../../../types/multiCodeSet';
import type { PushProductsToHqResult, PushProductsToHqStoreOption, PushProductsToHqUpdateField, } from '../../../types/posProduct';
import { copyTextToClipboard } from '../../../utils/clipboard';
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard';
import { RequestError } from '../../../utils/request';
import { lookupSuggestedDiscounts, previewPriceNotification, setSuggestedDiscounts } from '../../../services/storePriceUpdateTaskService';
import { buildPriceNotificationPreviewText, buildPriceNotificationView, buildPriceUpdateTasksLink, computeSuggestedDiscountedPrice, createPriceNotificationCapture, suggestedDiscountPercentToRate, suggestedDiscountRateToPercent, type PriceNotificationPreview, type PriceNotificationSummary } from '../../../utils/priceNotification';
import { filterWarehouseProductVisibleColumnOrder, isWarehouseProductColumnOrderCustomized, isWarehouseProductColumnVisibilityCustomized, mergeWarehouseProductColumnOrder, moveWarehouseProductColumnOrder, normalizeWarehouseProductVisibleOptionalColumns, type WarehouseProductTableColumnKey, } from './columnOrder';
import CreateProductModal from './CreateProductModal';
import CategoryTreePicker from './CategoryTreePicker';
import CategoryFilterPanel from './CategoryFilterPanel';
import ColumnSettingsButton from './ColumnSettingsButton';
import { WAREHOUSE_PRODUCT_STATUS_TAB_KEYS, buildSupplyNoticeSummary, buildWarehouseProductMetaParts, buildWarehouseProductStatusCountQuery, formatWarehouseProductCount, isWarehouseProductRetailPriceMissing, planWarehouseProductStatusCounts, resolveWarehouseProductStatusTab, warehouseProductStatusTabToIsActive, type WarehouseProductStatusCounts, type WarehouseProductStatusTabKey, } from './warehouseProductsListView';
import ContainerCategoryManageModal from '../ContainerDetail/ContainerCategoryManageModal';
import { resolveContainerCategorySelectionAfterRefresh, resolveContainerCategoryTargetAfterMutation, type ContainerCategoryChange, } from '../ContainerDetail/containerCategoryManageLogic';
import ImportFromDomesticModal from './ImportFromDomesticModal';
import ImportNonHbModal from './ImportNonHbModal';
import { buildWarehouseCategoryLookup, formatWarehouseCategoryNodeName, getWarehouseProductCategoryTooltip, type WarehouseCategoryLookup, } from './categoryPath';
import { ALL_PRODUCTS_FILTER_KEY, buildFilterCategoryOptions, buildFilterCategoryTreeOptions, } from '../Categories/categoryProductFilters';
import { buildCategoryQueryValue, buildComparableFilterTokens, buildTextFilterTokens, getSingleFilterValue, keepHiddenColumnFilters, normalizeTableFilters, normalizeWarehouseProductSortField, parseComparableFilterTokens, parseTextFilterTokens, resolveCategoryFilterValueFromTableFilters, setFilterValues, type ComparableFilterMode, type TextFilterMode, type WarehouseProductColumnFilters, } from './columnFilters';
import { areWarehouseProductCodeSelectionsEqual, buildWarehouseProductHqPushPayload } from './hqPush';
import { ACTIVE_FILTER_CATEGORY_KEY, ACTIVE_FILTER_SEARCH_KEY, buildActiveFilterChips, buildActiveFilterRemovalOverrides, type ActiveFilterColumnMeta, } from './activeFilters';
import PosHqPushModal from '../../../components/posHqPush/PosHqPushModal';
import { createPushToHqStoreOptionsGuard } from '../../../components/posHqPush/storeSelection';
import WarehouseProductStorePriceSyncModal from './WarehouseProductStorePriceSyncModal';
import WarehouseProductChangeHistoryDrawer from './WarehouseProductChangeHistoryDrawer';
import { MeasuredTable } from '../../../components/MeasuredTable';
import { registerPageMessages } from '../../../i18n/registerPageMessages';
import priceNotificationMessagesEn from './priceNotificationMessages.en.json';
import priceNotificationMessagesZh from './priceNotificationMessages.zh.json';
import warehouseProductsMessagesEn from './warehouseProductsMessages.en.json';
import warehouseProductsMessagesZh from './warehouseProductsMessages.zh.json';
import './warehouseProducts.css';

// 分店价格通知文案只在仓库商品页使用，随本页代码块懒注册，不进入首屏 i18n 包。
registerPageMessages({ zh: priceNotificationMessagesZh, en: priceNotificationMessagesEn });
// 本页重设计新增的文案（warehouseUi.products）同样随页面代码块懒注册。
registerPageMessages({ zh: warehouseProductsMessagesZh, en: warehouseProductsMessagesEn });

function isPlainRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === 'object' && value !== null;
}

function normalizePushToHqPayload(raw: unknown, fallbackMessage?: string): PushProductsToHqResult | null {
    if (!isPlainRecord(raw)) return null;
    const errors = Array.isArray(raw.errors)
        ? raw.errors.filter((item): item is string => typeof item === 'string')
        : [];
    const affectedRowCount = Number(raw.affectedRowCount ?? 0)
        || Number(raw.productsAdded ?? 0)
            + Number(raw.productsUpdated ?? 0)
            + Number(raw.storeRetailPricesCreated ?? 0)
            + Number(raw.storeRetailPricesUpdated ?? 0)
            + Number(raw.productSetCodesCreated ?? raw.productSetCodesAdded ?? 0)
            + Number(raw.productSetCodesUpdated ?? 0)
            + Number(raw.storeMultiCodesCreated ?? 0)
            + Number(raw.storeMultiCodesUpdated ?? 0);

    return {
        ...(raw as Partial<PushProductsToHqResult>),
        successCount: Number(raw.successCount ?? raw.productsAdded ?? 0)
            + Number(raw.successCount === undefined ? raw.productsUpdated ?? 0 : 0),
        failedCount: Number(raw.failedCount ?? raw.errorCount ?? errors.length),
        totalCount: Number(raw.totalCount ?? 0),
        affectedRowCount,
        errors,
        message: typeof raw.message === 'string' ? raw.message : fallbackMessage,
    };
}

function extractPushToHqErrorResult(error: unknown): PushProductsToHqResult | null {
    if (!isPlainRecord(error) || !('payload' in error)) return null;
    const payload = error.payload;
    if (!isPlainRecord(payload)) return null;
    const fallbackMessage = typeof payload.message === 'string'
        ? payload.message
        : error instanceof Error
            ? error.message
            : undefined;
    return normalizePushToHqPayload(payload.data, fallbackMessage)
        ?? normalizePushToHqPayload(payload.details, fallbackMessage);
}

interface ProductFormValues {
    supplierCode?: string;
    productName: string;
    englishProductName?: string;
    hbProductNo?: string;
    barcode?: string;
    productSpecification?: string;
    productType: ProductType;
    domesticPrice?: number;
    oemPrice?: number;
    // 建议折扣 %（0~100）；留空 = 未设置。不随 full-update 提交，保存时单独走 suggested-discounts 接口。
    suggestedDiscountPercent?: number | null;
    importPrice?: number;
    packingQuantity?: number;
    unitVolume?: number;
    middlePackQuantity?: number;
    packingSize?: string;
    material?: string;
    remarks?: string;
    productImage?: string;
    isActive: boolean;
}
interface BatchEditFormValues {
    supplierCode?: string;
    domesticPrice?: number;
    oemPrice?: number;
    importPrice?: number;
    packingQuantity?: number;
    minOrderQuantity?: number;
    unitVolume?: number;
    isActive?: boolean;
    generateImageUrls?: boolean;
    imageBaseUrl?: string;
    syncImageToHq?: boolean;
    // 仅当勾选「同时设置建议折扣」时生效；任务完成后单独调用 suggested-discounts。
    setSuggestedDiscount?: boolean;
    suggestedDiscountPercent?: number | null;
}
// 建议折扣不属于批量任务的商品字段，不能计入「是否填写了修改字段」的判断。
const WAREHOUSE_PRODUCT_BATCH_SUGGESTED_DISCOUNT_FIELDS = new Set(['setSuggestedDiscount', 'suggestedDiscountPercent']);
interface EditingSuggestedDiscountState {
    // lookup 失败时 loaded=false：禁用字段并跳过保存，避免把未知的原值误清空。
    loaded: boolean;
    percent: number | null;
}
const WAREHOUSE_PRODUCT_IMAGE_DEFAULT_BASE_URL = 'https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/YW200/';
const WAREHOUSE_PRODUCT_IMAGE_EXAMPLE_FILE = 'MC164-3.jpg';
function normalizeWarehouseProductImageBaseUrl(value?: string): string | undefined {
    const trimmed = (value ?? '').trim();
    if (!trimmed) {
        return undefined;
    }
    return `${trimmed.replace(/\/+$/, '')}/`;
}
function isValidWarehouseProductImageBaseUrl(value?: string): boolean {
    const normalized = normalizeWarehouseProductImageBaseUrl(value);
    if (!normalized) return false;
    try {
        const url = new URL(normalized);
        return (url.protocol === 'http:' || url.protocol === 'https:')
            && !url.search
            && !url.hash
            && !url.username
            && !url.password;
    }
    catch {
        return false;
    }
}
function isWarehouseProductBatchImageField(field: string): boolean {
    return field === 'generateImageUrls' || field === 'imageBaseUrl' || field === 'syncImageToHq';
}
function getShelfStatusLabel(isActive: boolean, t: ReturnType<typeof useTranslation>['t']) {
    return isActive ? t('warehouse.onShelf', '上架') : t('warehouse.offShelf', '下架');
}
function getProductTypeLabel(value: ProductType, t: ReturnType<typeof useTranslation>['t']) {
    const keyMap: Record<ProductType, string> = {
        [0]: 'warehouse.normal',
        [1]: 'warehouse.hasSet',
        [2]: 'warehouse.hasMultiCode',
    };
    return t(keyMap[value]) || ProductTypeLabels[value] || '--';
}
function getProductTypeTagColor(value: ProductType) {
    if (value === ProductType.SET) return 'gold';
    if (value === ProductType.MULTICODE) return 'blue';
    return 'default';
}
function canManageProductDetails(productType: ProductType) {
    return productType === ProductType.SET || productType === ProductType.MULTICODE;
}
function getProductDetailsActionLabel(productType: ProductType, t: ReturnType<typeof useTranslation>['t']) {
    return productType === ProductType.MULTICODE
        ? t('warehouse.multiCodeManagement', '多码管理')
        : t('warehouse.setSubItems');
}
function getProductDetailsDisabledHint(t: ReturnType<typeof useTranslation>['t']) {
    return t('warehouse.normalProductNoDetails', '普通商品没有套装或多码明细');
}
function getProductDetailsModalTitle(product: WarehouseProductListItem | null, t: ReturnType<typeof useTranslation>['t']) {
    const name = product?.itemNumber || product?.name;
    if (!product) return t('warehouse.setDetails');
    return product.productType === ProductType.MULTICODE
        ? t('warehouse.multiCodeDetailsTitle', '多码管理 - {{name}}', { name })
        : t('warehouse.setDetailsTitle', { name });
}
function getProductDetailsHint(productType: ProductType | undefined, t: ReturnType<typeof useTranslation>['t']) {
    return productType === ProductType.MULTICODE
        ? t('warehouse.multiCodeEditHint', '多码商品可维护多码条码、价格和分店同步使用的明细。')
        : t('warehouse.setEditHint');
}
function getProductTypeOptions(t: ReturnType<typeof useTranslation>['t']) {
    return Object.keys(ProductTypeLabels).map((value) => ({
        value: Number(value),
        label: getProductTypeLabel(Number(value) as ProductType, t),
    }));
}
function formatDateTime(value?: string, language?: string) {
    if (!value) {
        return '--';
    }
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
        return value;
    }
    const locale = language === 'en' ? 'en-AU' : 'zh-CN';
    return date.toLocaleString(locale, { hour12: false });
}
function formatPrice(value: number | undefined, pricePrefix: '¥' | '$') {
    if (value === undefined || value === null) {
        return '--';
    }
    return `${pricePrefix}${value.toFixed(2)}`;
}
type WarehouseProductInlineEditField = 'minOrderQuantity' | 'domesticPrice' | 'importPrice' | 'labelPrice';
type WarehouseProductInlineEditCell = {
    productCode: string;
    field: WarehouseProductInlineEditField;
    value: number | null;
    originalValue?: number;
};
function getWarehouseProductPricePrefix(field: WarehouseProductInlineEditField): '¥' | '$' | undefined {
    if (field === 'minOrderQuantity') {
        return undefined;
    }
    return field === 'domesticPrice' ? '¥' : '$';
}
type SupplierSelectOption = DefaultOptionType & {
    searchText?: string;
};
type ActiveWarehouseProductBatchUpdateJob = {
    jobId: string;
    operationId: string;
    createdAt: string;
    status?: WarehouseProductBatchUpdateJobStatus | string;
    message?: string;
    submittedProductCodes: string[];
    // 勾选了「同时设置建议折扣」时随任务持久化，刷新页面恢复轮询后仍能在任务完成时补发。
    suggestedDiscount?: { rate: number | null };
};
const WAREHOUSE_PRODUCT_BATCH_UPDATE_ACTIVE_JOB_STORAGE_KEY = 'warehouse.products.activeBatchUpdateJob';
const WAREHOUSE_PRODUCT_BATCH_UPDATE_POLL_INTERVAL_MS = 2000;
const WAREHOUSE_PRODUCT_BATCH_UPDATE_TIMEOUT_MS = 10 * 60 * 1000;
// 仓库商品主表数据量大，默认展示 100 条并保留大分页选项，继续依赖现有虚拟表格和服务端分页。
const WAREHOUSE_PRODUCTS_DEFAULT_PAGE_SIZE = 100;
const WAREHOUSE_PRODUCTS_PAGE_SIZE_OPTIONS = ['50', '100', '200', '500', '1000'];
// 列结构在 2026-10 重设计中改为「商品 / 供应商」组合列，旧版 v1 列顺序缓存不再适用，换用 v2 键。
const WAREHOUSE_PRODUCT_COLUMN_ORDER_STORAGE_KEY = 'hbweb_rv.warehouseProducts.columnOrder.v2';
const WAREHOUSE_PRODUCT_VISIBLE_COLUMNS_STORAGE_KEY = 'hbweb_rv.warehouseProducts.visibleColumns.v1';
// 默认列顺序：常驻列与可选列按业务含义交错排列，可选列被勾选显示时出现在相关列旁边。
const WAREHOUSE_PRODUCT_DEFAULT_COLUMN_ORDER = [
    'product',
    'nameEn',
    'name',
    'barcode',
    'categoryName',
    'productType',
    'supplier',
    'localSupplierCode',
    'domesticPrice',
    'importPrice',
    'labelPrice',
    'minOrderQuantity',
    'packingQty',
    'volume',
    'locationCodes',
    'isActive',
    'supplyUpdatedAt',
    'updatedAt',
    'updatedBy',
    'action',
] as const;
// 低频列：默认隐藏，通过「列设置」勾选显示（localStorage 记忆）。其余列常驻，保证 1440 宽默认视图不横向滚动。
const WAREHOUSE_PRODUCT_OPTIONAL_COLUMN_KEYS = [
    'nameEn',
    'name',
    'barcode',
    'categoryName',
    'productType',
    'localSupplierCode',
    'minOrderQuantity',
    'packingQty',
    'volume',
    'supplyUpdatedAt',
    'updatedAt',
    'updatedBy',
] as const;
const WAREHOUSE_PRODUCT_DEFAULT_VISIBLE_OPTIONAL_COLUMNS: string[] = [];
const WAREHOUSE_PRODUCT_SELECTION_COLUMN_WIDTH = 36;
// 关键词输入后 300ms 自动查询；回车立即查询。
const WAREHOUSE_PRODUCTS_SEARCH_DEBOUNCE_MS = 300;
interface DraggableHeaderCellProps extends HTMLAttributes<HTMLTableCellElement> {
    'data-column-key'?: string;
}
function DraggableHeaderCell({ children, style, ...props }: DraggableHeaderCellProps) {
    const columnKey = props['data-column-key'];
    const { attributes, listeners, setNodeRef, transform, transition, isDragging, } = useSortable({
        id: columnKey ?? '__warehouse-product-static-column__',
        disabled: !columnKey,
    });
    if (!columnKey) {
        return <th style={style} {...props}>{children}</th>;
    }
    const headerStyle: CSSProperties = {
        ...style,
        transform: CSS.Translate.toString(transform),
        transition,
        cursor: 'move',
        zIndex: isDragging ? 3 : style?.zIndex,
        opacity: isDragging ? 0.85 : style?.opacity,
    };
    return (<th ref={setNodeRef} style={headerStyle} {...props} {...attributes} {...listeners}>
      <div className="warehouse-products-draggable-header">
        {children}
      </div>
    </th>);
}
function readActiveWarehouseProductBatchUpdateJob(): ActiveWarehouseProductBatchUpdateJob | null {
    if (typeof window === 'undefined') {
        return null;
    }
    try {
        const raw = window.localStorage.getItem(WAREHOUSE_PRODUCT_BATCH_UPDATE_ACTIVE_JOB_STORAGE_KEY);
        if (!raw) {
            return null;
        }
        const parsed = JSON.parse(raw) as Partial<ActiveWarehouseProductBatchUpdateJob>;
        if (!parsed.jobId || !parsed.operationId || !parsed.createdAt || !Array.isArray(parsed.submittedProductCodes)) {
            return null;
        }
        return {
            ...parsed,
            submittedProductCodes: parsed.submittedProductCodes.map(String).filter(Boolean),
        } as ActiveWarehouseProductBatchUpdateJob;
    }
    catch {
        return null;
    }
}
function saveActiveWarehouseProductBatchUpdateJob(job: ActiveWarehouseProductBatchUpdateJob | null): boolean {
    if (typeof window === 'undefined') {
        return false;
    }
    try {
        if (!job) {
            window.localStorage.removeItem(WAREHOUSE_PRODUCT_BATCH_UPDATE_ACTIVE_JOB_STORAGE_KEY);
            return true;
        }
        window.localStorage.setItem(WAREHOUSE_PRODUCT_BATCH_UPDATE_ACTIVE_JOB_STORAGE_KEY, JSON.stringify(job));
        return true;
    }
    catch {
        // 浏览器禁用存储时仍继续当前页面轮询，只失去跨刷新恢复能力。
        return false;
    }
}
function buildSupplierOptions(suppliers: SupplierOption[]): SupplierSelectOption[] {
    return suppliers.map((item) => ({
        value: item.code,
        label: `${item.code} - ${item.name}`,
        searchText: `${item.code} ${item.name} ${item.shopNumber ?? ''}`.toLowerCase(),
    }));
}
function filterSupplierOption(input: string, option?: DefaultOptionType) {
    return String((option as SupplierSelectOption | undefined)?.searchText ?? '')
        .includes(input.trim().toLowerCase());
}
function collectCategoryExpandedKeys(nodes: WarehouseCategoryNode[], maxLevel: number, level = 1): string[] {
    if (level > maxLevel) {
        return [];
    }
    return nodes.flatMap((node) => [
        node.categoryGUID,
        ...collectCategoryExpandedKeys(node.children || [], maxLevel, level + 1),
    ]);
}
function findWarehouseCategory(nodes: WarehouseCategoryNode[], targetGuid?: string): WarehouseCategoryNode | undefined {
    if (!targetGuid) {
        return undefined;
    }
    for (const node of nodes) {
        if (node.categoryGUID === targetGuid) {
            return node;
        }
        const matched = findWarehouseCategory(node.children || [], targetGuid);
        if (matched) {
            return matched;
        }
    }
    return undefined;
}
function renderWarehouseProductCategoryCell(record: WarehouseProductListItem, categoryLookup: WarehouseCategoryLookup, language?: string) {
    const displayName = record.categoryName || '--';
    const tooltipTitle = getWarehouseProductCategoryTooltip(record, categoryLookup, language);
    // 单元格只显示分类名称，完整多级路径放到 Tooltip，保持主表紧凑可扫读。
    return (<Tooltip title={tooltipTitle}>
      <div className="warehouse-products-category-cell">{displayName}</div>
    </Tooltip>);
}
function ProductFormModal({ open, saving, editingItem, suppliers, form, suggestedDiscount, onCancel, onSubmit, }: {
    open: boolean;
    saving: boolean;
    editingItem: WarehouseProductListItem | null;
    suppliers: SupplierOption[];
    form: ReturnType<typeof Form.useForm<ProductFormValues>>[0];
    suggestedDiscount: EditingSuggestedDiscountState;
    onCancel: () => void;
    onSubmit: () => void;
}) {
    const { t } = useTranslation();
    const productTypeOptions = getProductTypeOptions(t);
    const watchedRetailPrice = Form.useWatch('oemPrice', form);
    const watchedSuggestedDiscountPercent = Form.useWatch('suggestedDiscountPercent', form);
    const [pricePreview, setPricePreview] = useState<{ preview: PriceNotificationPreview; retailPriceChanged: boolean } | null>(null);
    const productCode = editingItem?.productCode;
    const originalRetailPrice = editingItem?.labelPrice ?? null;
    useEffect(() => {
        setPricePreview(null);
        if (!open || !productCode) return;
        const nextRetailPrice = typeof watchedRetailPrice === 'number' ? watchedRetailPrice : null;
        const nextPercent = typeof watchedSuggestedDiscountPercent === 'number' ? watchedSuggestedDiscountPercent : null;
        const retailPriceChanged = nextRetailPrice !== null && nextRetailPrice !== originalRetailPrice;
        const discountChanged = suggestedDiscount.loaded && nextPercent !== suggestedDiscount.percent;
        if (!retailPriceChanged && !discountChanged) return;
        const controller = new AbortController();
        // 输入过程中 500ms 防抖；字段再次变化或弹窗关闭时终止上一次预告请求。
        const timer = window.setTimeout(() => {
            previewPriceNotification({
                productCode,
                retailPrice: retailPriceChanged ? nextRetailPrice : undefined,
                suggestedDiscountSpecified: discountChanged,
                suggestedDiscountRate: suggestedDiscountPercentToRate(nextPercent),
            }, { signal: controller.signal })
                .then((preview) => {
                if (!controller.signal.aborted) setPricePreview({ preview, retailPriceChanged });
            })
                // 预告只是提示，失败时静默，不能妨碍保存。
                .catch(() => undefined);
        }, 500);
        return () => {
            window.clearTimeout(timer);
            controller.abort();
        };
    }, [open, productCode, originalRetailPrice, suggestedDiscount.loaded, suggestedDiscount.percent, watchedRetailPrice, watchedSuggestedDiscountPercent]);
    const pricePreviewText = pricePreview
        ? buildPriceNotificationPreviewText(pricePreview.preview, pricePreview.retailPriceChanged, t)
        : null;
    const suggestedDiscountedPrice = computeSuggestedDiscountedPrice(
        typeof watchedRetailPrice === 'number' ? watchedRetailPrice : null,
        typeof watchedSuggestedDiscountPercent === 'number' ? watchedSuggestedDiscountPercent : null,
    );
    return (<Modal title={editingItem ? t('warehouse.editProductTitle', { name: editingItem.itemNumber || editingItem.name }) : t('warehouse.editProduct')} open={open} width={920} destroyOnHidden okText={t('common.save')} cancelText={t('common.cancel')} confirmLoading={saving} onCancel={onCancel} onOk={onSubmit}>
      <Form form={form} layout="vertical" preserve={false}>
        <Space size={16} style={{ display: 'flex' }} align="start">
          <Form.Item name="supplierCode" label={t('domesticProducts.supplier')} style={{ flex: 1 }} rules={editingItem ? [] : [{ required: true, message: t('domesticProducts.selectSupplier') }]}>
            <Select placeholder={t('domesticProducts.selectSupplier')} showSearch filterOption={filterSupplierOption} options={buildSupplierOptions(suppliers)}/>
          </Form.Item>
          <Form.Item name="productType" label={t('warehouse.productType')} style={{ width: 180 }} rules={[{ required: true, message: t('warehouse.selectProductType') }]}>
            <Select placeholder={t('warehouse.selectProductType')} options={productTypeOptions}/>
          </Form.Item>
          <Form.Item name="isActive" label={t('warehouse.isListed')} valuePropName="checked" style={{ width: 120 }}>
            <Switch checkedChildren={getShelfStatusLabel(true, t)} unCheckedChildren={getShelfStatusLabel(false, t)}/>
          </Form.Item>
        </Space>

        <Space size={16} style={{ display: 'flex' }} align="start">
          <Form.Item name="productName" label={t('domesticProducts.productName')} style={{ flex: 1 }} rules={[{ required: true, message: t('warehouse.enterProductName') }]}>
            <Input placeholder={t('warehouse.enterProductName')}/>
          </Form.Item>
          <Form.Item name="englishProductName" label={t('warehouse.englishName')} style={{ flex: 1 }}>
            <Input placeholder={t('warehouse.enterEnglishName')}/>
          </Form.Item>
        </Space>

        <Space size={16} style={{ display: 'flex' }} align="start">
          <Form.Item name="hbProductNo" label={t('warehouse.hbProductNo')} style={{ flex: 1 }}>
            <Input disabled={Boolean(editingItem)} placeholder={t('warehouse.autoGenerate')}/>
          </Form.Item>
          <Form.Item name="barcode" label={t('domesticProducts.barcode')} style={{ flex: 1 }}>
            <Input placeholder={t('warehouse.autoGenerate')}/>
          </Form.Item>
          <Form.Item name="productSpecification" label={t('domesticProducts.specification')} style={{ flex: 1 }}>
            <Input placeholder={t('warehouse.enterSpec')}/>
          </Form.Item>
        </Space>

        <Space size={16} style={{ display: 'flex' }} align="start">
          <Form.Item name="domesticPrice" label={t('domesticProducts.domesticPrice')} style={{ flex: 1 }}>
            <InputNumber min={0} precision={2} style={{ width: '100%' }}/>
          </Form.Item>
          <Form.Item name="oemPrice" label={t('productCreation.privateLabelPrice')} style={{ flex: 1 }}>
            <InputNumber min={0} precision={2} style={{ width: '100%' }}/>
          </Form.Item>
          <Form.Item name="importPrice" label={t('warehouse.importPrice')} style={{ flex: 1 }}>
            <InputNumber min={0} precision={2} style={{ width: '100%' }}/>
          </Form.Item>
        </Space>

        {editingItem ? (<>
          <Space size={16} style={{ display: 'flex' }} align="start">
            <Form.Item name="suggestedDiscountPercent" label={t('warehouse.priceNotification.suggestedDiscount')} extra={t('warehouse.priceNotification.suggestedDiscountHint')} style={{ flex: 2 }}>
              <InputNumber min={0} max={100} precision={2} suffix="%" disabled={!suggestedDiscount.loaded} placeholder={t('warehouse.priceNotification.suggestedDiscountPlaceholder')} style={{ width: '100%' }}/>
            </Form.Item>
            <Form.Item label={t('warehouse.priceNotification.suggestedDiscountedPrice')} style={{ flex: 1 }}>
              <InputNumber value={suggestedDiscountedPrice} precision={2} prefix="$" readOnly controls={false} placeholder="--" style={{ width: '100%' }}/>
            </Form.Item>
          </Space>
          {pricePreviewText ? (<Alert type="info" showIcon message={pricePreviewText} style={{ marginBottom: 16 }}/>) : null}
        </>) : null}

        <Space size={16} style={{ display: 'flex' }} align="start">
          <Form.Item name="packingQuantity" label={t('warehouse.packingQuantity')} style={{ flex: 1 }}>
            <InputNumber min={0} precision={0} style={{ width: '100%' }}/>
          </Form.Item>
          <Form.Item name="middlePackQuantity" label={t('warehouse.middlePackQuantity')} style={{ flex: 1 }}>
            <InputNumber min={0} precision={0} style={{ width: '100%' }}/>
          </Form.Item>
          <Form.Item name="unitVolume" label={t('warehouse.volume')} style={{ flex: 1 }}>
            <InputNumber min={0} precision={4} style={{ width: '100%' }}/>
          </Form.Item>
        </Space>

        <Space size={16} style={{ display: 'flex' }} align="start">
          <Form.Item name="packingSize" label={t('warehouse.packingSize')} style={{ flex: 1 }}>
            <Input placeholder={t('warehouse.enterPackingSize')}/>
          </Form.Item>
          <Form.Item name="material" label={t('warehouse.material')} style={{ flex: 1 }}>
            <Input placeholder={t('warehouse.enterMaterial')}/>
          </Form.Item>
          <Form.Item name="productImage" label={t('warehouse.imageUrl')} style={{ flex: 1 }}>
            <Input placeholder={t('warehouse.enterImageUrl')}/>
          </Form.Item>
        </Space>

        <Form.Item name="remarks" label={t('common.remarks')}>
          <Input.TextArea rows={3} placeholder={t('common.enterRemarks')}/>
        </Form.Item>
      </Form>
    </Modal>);
}
function SetItemsModal({ open, loading, saving, product, items, canEdit, onCancel, onAddRow, onRemoveRow, onChangeField, onSubmit, }: {
    open: boolean;
    loading: boolean;
    saving: boolean;
    product: WarehouseProductListItem | null;
    items: MulticodeSetItem[];
    canEdit: boolean;
    onCancel: () => void;
    onAddRow: () => void;
    onRemoveRow: (rowId: string) => void;
    onChangeField: (rowId: string, field: keyof MulticodeSetItem, value: string | number | boolean | undefined) => void;
    onSubmit: () => void;
}) {
    const { t } = useTranslation();
    const isNewSetCode = (record: MulticodeSetItem) => record.id?.startsWith('new_') ?? false;
    const columns: ColumnsType<MulticodeSetItem> = [
        {
            title: t('warehouse.setProductNo'),
            dataIndex: 'setItemNumber',
            width: 180,
            render: (_, record) => (<Input value={record.setItemNumber} disabled={!canEdit || !isNewSetCode(record)} onChange={(event) => onChangeField(record.id!, 'setItemNumber', event.target.value)}/>),
        },
        {
            title: t('domesticProducts.barcode'),
            dataIndex: 'setBarcode',
            width: 180,
            render: (_, record) => (<Input value={record.setBarcode} disabled={!canEdit} onChange={(event) => onChangeField(record.id!, 'setBarcode', event.target.value)}/>),
        },
        {
            title: t('posAdmin.invoiceDetail.purchasePrice', '进货价'),
            dataIndex: 'setPurchasePrice',
            width: 120,
            render: (_, record) => (<InputNumber min={0} precision={2} value={record.setPurchasePrice} disabled={!canEdit} style={{ width: '100%' }} onChange={(value) => onChangeField(record.id!, 'setPurchasePrice', value ?? undefined)}/>),
        },
        {
            title: t('posAdmin.invoiceDetail.retailPrice', '零售价'),
            dataIndex: 'setRetailPrice',
            width: 120,
            render: (_, record) => (<InputNumber min={0} precision={2} value={record.setRetailPrice} disabled={!canEdit} style={{ width: '100%' }} onChange={(value) => onChangeField(record.id!, 'setRetailPrice', value ?? undefined)}/>),
        },
        {
            title: t('column.status', '状态'),
            dataIndex: 'isActive',
            width: 120,
            render: (_, record) => (<Switch checked={record.isActive ?? true} disabled={!canEdit} checkedChildren={getShelfStatusLabel(true, t)} unCheckedChildren={getShelfStatusLabel(false, t)} onChange={(checked) => onChangeField(record.id!, 'isActive', checked)}/>),
        },
        {
            title: t('common.action'),
            key: 'action',
            width: 90,
            render: (_, record) => canEdit ? (<Button danger type="link" onClick={() => onRemoveRow(record.id!)}>
            {t('common.delete')}
          </Button>) : null,
        },
    ];
    return (<Modal title={getProductDetailsModalTitle(product, t)} open={open} width={1100} destroyOnHidden onCancel={onCancel} onOk={onSubmit} okText={t('common.save')} cancelText={t('common.close')} confirmLoading={saving} okButtonProps={{ disabled: !canEdit }}>
      <Space style={{ marginBottom: 16 }}>
        <Typography.Text type="secondary">
          {getProductDetailsHint(product?.productType, t)}
        </Typography.Text>
        {canEdit ? (<Button type="dashed" onClick={onAddRow}>
            {product?.productType === ProductType.MULTICODE ? t('warehouse.addMultiCodeDetail', '新增多码') : t('warehouse.addSubItem')}
          </Button>) : null}
      </Space>
      <MeasuredTable metricId="warehouse.products.table-1" rowKey="id" loading={loading} columns={columns} dataSource={items} pagination={false} scroll={{ x: 980, y: 420 }}/>
    </Modal>);
}
export default function WarehouseProductsPage() {
    const { t, i18n } = useTranslation();
    const productTypeOptions = getProductTypeOptions(t);
    const [form] = Form.useForm<ProductFormValues>();
    const [batchEditForm] = Form.useForm<BatchEditFormValues>();
    const batchGenerateImageUrls = Form.useWatch('generateImageUrls', batchEditForm) === true;
    const batchImageBaseUrl = Form.useWatch('imageBaseUrl', batchEditForm);
    const batchSetSuggestedDiscount = Form.useWatch('setSuggestedDiscount', batchEditForm) === true;
    const [loading, setLoading] = useState(false);
    const [saving, setSaving] = useState(false);
    const [modalOpen, setModalOpen] = useState(false);
    const [createModalOpen, setCreateModalOpen] = useState(false);
    const [editingItem, setEditingItem] = useState<WarehouseProductListItem | null>(null);
    const [editingSuggestedDiscount, setEditingSuggestedDiscount] = useState<EditingSuggestedDiscountState>({ loaded: false, percent: null });
    const editingProductCodeRef = useRef<string | null>(null);
    const [suppliers, setSuppliers] = useState<SupplierOption[]>([]);
    const [localSupplierNameMap, setLocalSupplierNameMap] = useState<Record<string, string>>({});
    const [categories, setCategories] = useState<WarehouseCategoryNode[]>([]);
    const [categoryLoading, setCategoryLoading] = useState(false);
    const [categoryFilterValue, setCategoryFilterValue] = useState<string>(ALL_PRODUCTS_FILTER_KEY);
    const [categoryFilterExpandedKeys, setCategoryFilterExpandedKeys] = useState<string[]>([]);
    const [categoryFilterSearchText, setCategoryFilterSearchText] = useState('');
    const [columnFilters, setColumnFilters] = useState<WarehouseProductColumnFilters>({});
    const [categoryExpandedKeys, setCategoryExpandedKeys] = useState<string[]>([]);
    const [categoryManageOpen, setCategoryManageOpen] = useState(false);
    const [batchCategoryOpen, setBatchCategoryOpen] = useState(false);
    const [targetCategoryGuid, setTargetCategoryGuid] = useState<string>();
    const [batchCategorySaving, setBatchCategorySaving] = useState(false);
    const [data, setData] = useState<WarehouseProductListItem[]>([]);
    const [inlineEditingCell, setInlineEditingCell] = useState<WarehouseProductInlineEditCell | null>(null);
    const [inlineSavingCellKey, setInlineSavingCellKey] = useState<string | null>(null);
    const [selectedRowKeys, setSelectedRowKeys] = useState<React.Key[]>([]);
    const [searchText, setSearchText] = useState('');
    // 关键词输入后防抖提交（回车立即提交）；翻页、下拉即查等请求都用已提交的关键词，而不是输入框里尚未提交的草稿。
    const [submittedSearchText, setSubmittedSearchText] = useState('');
    // 最近一次成功返回的查询条件，已生效筛选条据此展示「表格数据实际是按什么条件查出来的」。
    const [appliedQuery, setAppliedQuery] = useState<WarehouseProductsTableQuery | null>(null);
    const [supplierCode, setSupplierCode] = useState<string>();
    const [productType, setProductType] = useState<ProductType>();
    const [isActive, setIsActive] = useState<boolean>();
    const [page, setPage] = useState(1);
    const [pageSize, setPageSize] = useState(WAREHOUSE_PRODUCTS_DEFAULT_PAGE_SIZE);
    const [total, setTotal] = useState(0);
    const [sortField, setSortField] = useState('createdAt');
    const [sortOrder, setSortOrder] = useState<'ascend' | 'descend'>('descend');
    const [exportConfigOpen, setExportConfigOpen] = useState(false);
    const [exporting, setExporting] = useState(false);
    const [includeLabelPrice, setIncludeLabelPrice] = useState(false);
    const [includeBarcodeImage, setIncludeBarcodeImage] = useState(true);
    const [includeProductImage, setIncludeProductImage] = useState(false);
    const [exportProgress, setExportProgress] = useState(0);
    const [exportMessage, setExportMessage] = useState('');
    const [importFromDomesticOpen, setImportFromDomesticOpen] = useState(false);
    const [importNonHbOpen, setImportNonHbOpen] = useState(false);
    const [setItemsOpen, setSetItemsOpen] = useState(false);
    const [setItemsLoading, setSetItemsLoading] = useState(false);
    const [setItemsSaving, setSetItemsSaving] = useState(false);
    const [currentSetProduct, setCurrentSetProduct] = useState<WarehouseProductListItem | null>(null);
    const [setItemsDraft, setSetItemsDraft] = useState<MulticodeSetItem[]>([]);
    const [deletedSetCodeIds, setDeletedSetCodeIds] = useState<string[]>([]);
    const [batchActionLoading, setBatchActionLoading] = useState(false);
    const [batchEditOpen, setBatchEditOpen] = useState(false);
    const [batchEditSaving, setBatchEditSaving] = useState(false);
    const [hqImageSyncFailOpen, setHqImageSyncFailOpen] = useState(false);
    const [hqImageSyncFailDetail, setHqImageSyncFailDetail] = useState<{
        localFailedCount: number;
        errors: string[];
        items: Array<{ productCode: string; message?: string }>;
    }>({ localFailedCount: 0, errors: [], items: [] });
    const [pushToHqLoading, setPushToHqLoading] = useState(false);
    const [pushToHqModalOpen, setPushToHqModalOpen] = useState(false);
    const [pushToHqStoreOptions, setPushToHqStoreOptions] = useState<PushProductsToHqStoreOption[]>([]);
    const [pushToHqStoreOptionsLoading, setPushToHqStoreOptionsLoading] = useState(false);
    const [pushToHqStoreOptionsError, setPushToHqStoreOptionsError] = useState<string | null>(null);
    const [pushToHqConfirmLoading, setPushToHqConfirmLoading] = useState(false);
    const [togglingProductCodes, setTogglingProductCodes] = useState<string[]>([]);
    // 供货说明按当前页商品批量查询，键为商品编码；下架成功、修改成功后就地更新。
    const [supplyNotices, setSupplyNotices] = useState<Record<string, WarehouseSupplyNotice>>({});
    // 下架 / 修改说明弹窗：target 为空即关闭。单个开关、批量下架、修改说明、编辑弹窗、批量修改共用。
    // origin 标记从哪个保存流程发起：editModal = 编辑弹窗把「是否上架」改为下架，batchEdit = 批量修改设为下架；
    // 不带 origin 的是原有的单个开关 / 批量下架路径。
    const [supplyNoticeTarget, setSupplyNoticeTarget] = useState<{ mode: 'delist' | 'edit'; productCodes: string[]; initial?: WarehouseSupplyNotice | null; origin?: 'editModal' | 'batchEdit' | 'rowToggle' | 'batchToggle' } | null>(null);
    const [supplyNoticeSaving, setSupplyNoticeSaving] = useState(false);
    const [exportFailDetailOpen, setExportFailDetailOpen] = useState(false);
    const [exportFailDetail, setExportFailDetail] = useState<ExportResult['failedProductImages']>([]);
    const [activeBatchUpdateJob, setActiveBatchUpdateJob] = useState<ActiveWarehouseProductBatchUpdateJob | null>(() => readActiveWarehouseProductBatchUpdateJob());
    const [storePriceSyncOpen, setStorePriceSyncOpen] = useState(false);
    const [changeHistoryProduct, setChangeHistoryProduct] = useState<WarehouseProductListItem | null>(null);
    const [columnOrder, setColumnOrder] = useState<WarehouseProductTableColumnKey[]>([]);
    // 列设置里勾选显示的低频列；首次读取 localStorage，读不到时用默认（全部隐藏）。
    const [visibleOptionalColumns, setVisibleOptionalColumns] = useState<WarehouseProductTableColumnKey[]>(() => {
        let saved: unknown = null;
        try {
            const raw = typeof window === 'undefined' ? null : window.localStorage.getItem(WAREHOUSE_PRODUCT_VISIBLE_COLUMNS_STORAGE_KEY);
            saved = raw ? JSON.parse(raw) : null;
        }
        catch {
            saved = null;
        }
        return normalizeWarehouseProductVisibleOptionalColumns(saved, WAREHOUSE_PRODUCT_OPTIONAL_COLUMN_KEYS, WAREHOUSE_PRODUCT_DEFAULT_VISIBLE_OPTIONAL_COLUMNS);
    });
    // 状态页签计数：接口支持按 isActive 过滤并返回 total，按降级规则并行发 pageSize=1 的计数请求；失败时不显示计数。
    const [statusCounts, setStatusCounts] = useState<WarehouseProductStatusCounts | null>(null);
    const stopBatchUpdateJobPollingRef = useRef<(() => void) | null>(null);
    const pushToHqLoadingRef = useRef(false);
    const pushToHqProductCodesRef = useRef<string[]>([]);
    const pushToHqConfirmLoadingRef = useRef(false);
    const pushToHqStoreOptionsGuardRef = useRef(createPushToHqStoreOptionsGuard());
    const isMountedRef = useRef(true);
    const dataRef = useRef<WarehouseProductListItem[]>([]);
    const selectedRowKeysRef = useRef<React.Key[]>([]);
    const loadDataRef = useRef<((overrides?: Partial<WarehouseProductsTableQuery>) => Promise<void>) | null>(null);
    const listRequestGuardRef = useRef(createLatestRequestGuard());
    // 计数请求单独一个守卫：只采纳最新一轮结果，不影响列表请求的竞态保护。
    const statusCountGuardRef = useRef(createLatestRequestGuard());
    const statusCountsRef = useRef<WarehouseProductStatusCounts | null>(null);
    // 写操作后列表经 refreshCurrentList 刷新时，即使筛选条件没变也要重新计数。
    const statusCountsStaleRef = useRef(false);
    const searchDebounceTimerRef = useRef<number | null>(null);
    const inlineSaveLockRef = useRef<string | null>(null);
    const inlineCancelledCellRef = useRef<string | null>(null);
    useLayoutEffect(() => {
        dataRef.current = data;
        selectedRowKeysRef.current = selectedRowKeys;
    }, [data, selectedRowKeys]);
    const columnDragSensors = useSensors(useSensor(PointerSensor, {
        activationConstraint: {
            distance: 6,
        },
    }));
    const { access, currentUser } = useAuthStore();
    const navigate = useNavigate();
    const canImportNonHbProducts = access.isAdmin || access.isWarehouseManager;
    const canManageWarehouseStorePriceSync = access.isAdmin || access.isWarehouseManager;
    const canInlineEditWarehouseProduct = currentUser?.roleNames?.some((roleName) =>
        roleName === 'Admin' || roleName === 'WarehouseManager') ?? false;
    const categoryFilterOptions = useMemo(() => buildFilterCategoryOptions(categories, t, i18n.language), [categories, i18n.language, t]);
    const categoryFilterTreeOptions = useMemo(() => buildFilterCategoryTreeOptions(categories, t, i18n.language), [categories, i18n.language, t]);
    const hasCategoryFilterSearchText = categoryFilterSearchText.trim().length > 0;
    const domesticSupplierFilterOptions = useMemo(() => buildSupplierOptions(suppliers).map((item) => ({
        text: String(item.label),
        value: String(item.value),
    })), [suppliers]);
    const localSupplierFilterOptions = useMemo(() => Object.entries(localSupplierNameMap)
        .sort(([leftCode, leftName], [rightCode, rightName]) => `${leftCode} ${leftName}`.localeCompare(`${rightCode} ${rightName}`))
        .map(([code, name]) => ({
        text: `${code} - ${name}`,
        value: code,
    })), [localSupplierNameMap]);
    const categoryColumnFilterOptions = useMemo(() => categoryFilterOptions
        .filter((option) => option.value !== ALL_PRODUCTS_FILTER_KEY)
        .map((option) => ({
        text: option.label,
        value: option.value,
    })), [categoryFilterOptions]);
    const categoryLookup = useMemo(() => buildWarehouseCategoryLookup(categories), [categories]);
    const selectedTargetCategory = useMemo(() => findWarehouseCategory(categories, targetCategoryGuid), [categories, targetCategoryGuid]);
    const selectedTargetCategoryPath = targetCategoryGuid ? getWarehouseProductCategoryTooltip({
        categoryName: selectedTargetCategory?.categoryName,
        warehouseCategoryGUID: targetCategoryGuid,
    }, categoryLookup, i18n.language) : undefined;
    const buildGridQuery = (overrides: Partial<WarehouseProductsTableQuery> = {}): WarehouseProductsTableQuery => {
        const categoryQuery = buildCategoryQueryValue(categoryFilterValue);
        const mergedFilters = overrides.filters ?? columnFilters;
        return {
            page,
            pageSize,
            searchText: submittedSearchText,
            supplierCode,
            productType,
            isActive,
            filters: Object.keys(mergedFilters).length ? mergedFilters : undefined,
            sortField,
            sortOrder,
            // 列头筛选走后端 Filters，分类仍走顶层字段，避免未分类语义被普通 Filters 吞掉。
            ...categoryQuery,
            ...overrides,
        };
    };
    const filterIcon = (filtered?: boolean) => <SearchOutlined style={{ color: filtered ? '#1677ff' : undefined }}/>;
    const textFilterModeOptions: Array<{ label: string; value: TextFilterMode; }> = [
        { label: t('warehouse.filterMode.contains', '包含'), value: 'contains' },
        { label: t('warehouse.filterMode.equals', '等于'), value: 'eq' },
        { label: t('warehouse.filterMode.startsWith', '开头是'), value: 'starts' },
        { label: t('warehouse.filterMode.endsWith', '结尾是'), value: 'ends' },
    ];
    const comparableFilterModeOptions: Array<{ label: string; value: ComparableFilterMode; }> = [
        { label: t('warehouse.filterMode.equals', '等于'), value: 'eq' },
        { label: t('warehouse.filterMode.range', '范围'), value: 'range' },
        { label: t('warehouse.filterMode.greaterOrEqual', '大于等于'), value: 'gte' },
        { label: t('warehouse.filterMode.lessOrEqual', '小于等于'), value: 'lte' },
    ];
    // 统一列头筛选面板骨架，文本/数字/日期只替换输入区，应用和重置语义保持一致。
    const renderColumnFilterPanel = (content: ReactNode, onApply: () => void, onReset: () => void) => (
        <div className="warehouse-products-column-filter-panel" onKeyDown={(event) => event.stopPropagation()} onMouseDown={(event) => event.stopPropagation()}>
          <div className="warehouse-products-column-filter-body">
            {content}
          </div>
          <div className="warehouse-products-column-filter-actions">
            <Button size="small" onClick={onReset}>{t('containers.actions.resetColumnFilter', '重置')}</Button>
            <Button size="small" type="primary" onClick={onApply}>{t('containers.actions.applyColumnFilter', '应用')}</Button>
          </div>
        </div>
    );
    const buildTextFilterDropdown = (filterKey: string, placeholder: string) => ({ confirm, selectedKeys, setSelectedKeys, clearFilters }: FilterDropdownProps) => {
        const values = (selectedKeys.length ? selectedKeys : columnFilters[filterKey] ?? []).map((value) => String(value));
        const parsedFilter = parseTextFilterTokens(values);
        const updateFilter = (mode: TextFilterMode, value?: string) => {
            setSelectedKeys(buildTextFilterTokens(mode, value));
        };
        return renderColumnFilterPanel((<>
            <Select size="small" value={parsedFilter.mode} options={textFilterModeOptions} onChange={(mode) => updateFilter(mode, parsedFilter.value)}/>
            <Input value={parsedFilter.value} placeholder={placeholder} allowClear onChange={(event) => {
                const nextValue = event.target.value;
                updateFilter(parsedFilter.mode, nextValue);
            }} onPressEnter={() => confirm()}/>
          </>), () => confirm(), () => {
                setSelectedKeys([]);
                clearFilters?.();
                confirm();
            });
    };
    const buildNumberRangeFilterDropdown = (filterKey: string) => ({ confirm, selectedKeys, setSelectedKeys, clearFilters }: FilterDropdownProps) => {
        const values = (selectedKeys.length ? selectedKeys : columnFilters[filterKey] ?? []).map((value) => String(value));
        const parsedFilter = parseComparableFilterTokens(values);
        const updateFilter = (mode: ComparableFilterMode, nextValue: { value?: string | number; min?: string | number; max?: string | number; }) => {
            setSelectedKeys(buildComparableFilterTokens(mode, nextValue));
        };
        return renderColumnFilterPanel((<>
            <Select size="small" value={parsedFilter.mode} options={comparableFilterModeOptions} onChange={(mode) => updateFilter(mode, {
                value: parsedFilter.value || parsedFilter.min || parsedFilter.max,
                min: parsedFilter.min,
                max: parsedFilter.max,
            })}/>
            {parsedFilter.mode === 'range' ? (<Space.Compact>
              <InputNumber value={parsedFilter.min !== '' ? Number(parsedFilter.min) : undefined} placeholder={t('containers.placeholders.minValue', '最小值')} controls={false} onChange={(nextValue) => updateFilter('range', {
                min: nextValue == null ? undefined : Number(nextValue),
                max: parsedFilter.max || undefined,
            })}/>
              <InputNumber value={parsedFilter.max !== '' ? Number(parsedFilter.max) : undefined} placeholder={t('containers.placeholders.maxValue', '最大值')} controls={false} onChange={(nextValue) => updateFilter('range', {
                min: parsedFilter.min || undefined,
                max: nextValue == null ? undefined : Number(nextValue),
            })}/>
            </Space.Compact>) : (<InputNumber value={parsedFilter.value !== '' ? Number(parsedFilter.value) : undefined} placeholder={t('containers.placeholders.filterValue', '筛选值')} controls={false} onChange={(nextValue) => updateFilter(parsedFilter.mode, {
                value: nextValue == null ? undefined : Number(nextValue),
            })}/>)}
          </>), () => confirm(), () => {
                setSelectedKeys([]);
                clearFilters?.();
                confirm();
            });
    };
    const buildDateRangeFilterDropdown = (filterKey: string) => ({ confirm, selectedKeys, setSelectedKeys, clearFilters }: FilterDropdownProps) => {
        const values = (selectedKeys.length ? selectedKeys : columnFilters[filterKey] ?? []).map((value) => String(value));
        const parsedFilter = parseComparableFilterTokens(values);
        const updateFilter = (mode: ComparableFilterMode, nextValue: { value?: string; min?: string; max?: string; }) => {
            setSelectedKeys(buildComparableFilterTokens(mode, nextValue));
        };
        return renderColumnFilterPanel((<>
            <Select size="small" value={parsedFilter.mode} options={comparableFilterModeOptions} onChange={(mode) => updateFilter(mode, {
                value: parsedFilter.value || parsedFilter.min || parsedFilter.max,
                min: parsedFilter.min,
                max: parsedFilter.max,
            })}/>
            {parsedFilter.mode === 'range' ? (<>
            <Input type="date" value={parsedFilter.min} onChange={(event) => updateFilter('range', {
                min: event.target.value || undefined,
                max: parsedFilter.max || undefined,
            })}/>
            <Input type="date" value={parsedFilter.max} onChange={(event) => updateFilter('range', {
                min: parsedFilter.min || undefined,
                max: event.target.value || undefined,
            })}/>
            </>) : (<Input type="date" value={parsedFilter.value} onChange={(event) => updateFilter(parsedFilter.mode, {
                value: event.target.value || undefined,
            })}/>)}
          </>), () => confirm(), () => {
                setSelectedKeys([]);
                clearFilters?.();
                confirm();
            });
    };
    const textFilterProps = (filterKey: string, placeholder: string) => ({
        filterDropdown: buildTextFilterDropdown(filterKey, placeholder),
        filterIcon,
        filtered: Boolean(columnFilters[filterKey]?.[0]?.trim()),
        filteredValue: columnFilters[filterKey] ?? null,
    });
    const numberRangeFilterProps = (filterKey: string) => ({
        filterDropdown: buildNumberRangeFilterDropdown(filterKey),
        filterIcon,
        filtered: Boolean(columnFilters[filterKey]?.length),
        filteredValue: columnFilters[filterKey] ?? null,
    });
    const dateRangeFilterProps = (filterKey: string) => ({
        filterDropdown: buildDateRangeFilterDropdown(filterKey),
        filterIcon,
        filtered: Boolean(columnFilters[filterKey]?.length),
        filteredValue: columnFilters[filterKey] ?? null,
    });
    const enumFilterProps = (filterKey: string, options: Array<{ text: string; value: string; }>) => ({
        filters: options,
        filterIcon,
        filtered: Boolean(columnFilters[filterKey]?.length),
        filteredValue: columnFilters[filterKey] ?? null,
        filterMultiple: true,
    });
    // 状态页签计数：同一组「状态以外」的条件只计数一次；当前页签直接用列表 total，其余页签各发一个 pageSize=1 请求。
    // 计数失败只是不显示数量，不影响列表；条件变化时先清掉旧数量，避免把上一组条件的数量挂在新列表上。
    const loadStatusCounts = (query: WarehouseProductsTableQuery, currentTotal?: number) => {
        const plan = planWarehouseProductStatusCounts({
            query,
            currentTotal,
            cached: statusCountsRef.current,
            stale: statusCountsStaleRef.current,
        });
        if (plan.skip) {
            return;
        }
        statusCountsStaleRef.current = false;
        const cached = statusCountsRef.current;
        const pending: WarehouseProductStatusCounts = {
            key: plan.key,
            // 同一组条件重新计数时先保留旧数量，等新结果返回再替换，避免数字闪烁。
            counts: cached?.key === plan.key ? { ...cached.counts, ...plan.knownCounts } : { ...plan.knownCounts },
        };
        statusCountsRef.current = pending;
        setStatusCounts(pending);
        if (!plan.tabsToFetch.length) {
            return;
        }
        void runLatestGuardedRequest(statusCountGuardRef.current, () => Promise.all(plan.tabsToFetch.map((tab) => getWarehouseProductsTable(buildWarehouseProductStatusCountQuery(query, tab)))), {
            onSuccess: (results) => {
                const next: WarehouseProductStatusCounts = { key: plan.key, counts: { ...pending.counts } };
                plan.tabsToFetch.forEach((tab, index) => {
                    next.counts[tab] = results[index].total;
                });
                statusCountsRef.current = next;
                setStatusCounts(next);
            },
            onError: (error) => {
                console.error(error);
                statusCountsRef.current = null;
                setStatusCounts(null);
            },
        });
    };
    const loadData = async (overrides: Partial<WarehouseProductsTableQuery> = {}) => {
        if (!isMountedRef.current) {
            return;
        }
        const query = buildGridQuery(overrides);
        await runLatestGuardedRequest(listRequestGuardRef.current, async () => {
            const result = await getWarehouseProductsTable(query);
            return result;
        }, {
            onStart: () => setLoading(true),
            onSuccess: (result) => {
                setData(result.items);
                void loadSupplyNotices(result.items);
                setTotal(result.total);
                setPage(result.page);
                setPageSize(result.pageSize);
                setSelectedRowKeys([]);
                setAppliedQuery(query);
                loadStatusCounts(query, result.total);
            },
            onError: (error) => {
                console.error(error);
                message.error(error instanceof Error ? error.message : t('warehouse.loadProductsFailed'));
            },
            onSettled: () => setLoading(false),
        });
    };
    useLayoutEffect(() => {
        loadDataRef.current = loadData;
    });
    // 只查下架商品的说明；在架商品不会有未关闭说明。查询失败不影响列表本身。
    const loadSupplyNotices = async (items: WarehouseProductListItem[]) => {
        const codes = items.filter((item) => !item.isActive).map((item) => item.productCode);
        if (!codes.length) {
            setSupplyNotices({});
            return;
        }
        try {
            const notices = await queryWarehouseSupplyNotices(codes);
            if (!isMountedRef.current) {
                return;
            }
            setSupplyNotices(Object.fromEntries(notices.map((notice) => [notice.productCode, notice])));
        }
        catch (error) {
            console.error(error);
        }
    };
    const refreshCurrentList = useCallback((overrides: Partial<WarehouseProductsTableQuery> = {}) => {
        if (!isMountedRef.current) {
            return Promise.resolve();
        }
        // 写操作后的刷新可能改变上架/下架数量，筛选条件不变也要重新计数。
        statusCountsStaleRef.current = true;
        return loadDataRef.current?.(overrides) ?? Promise.resolve();
    }, []);
    const stopBatchUpdateJobPolling = useCallback(() => {
        stopBatchUpdateJobPollingRef.current?.();
        stopBatchUpdateJobPollingRef.current = null;
    }, []);
    const saveActiveBatchUpdateJob = useCallback((job: ActiveWarehouseProductBatchUpdateJob) => {
        const persisted = saveActiveWarehouseProductBatchUpdateJob(job);
        if (isMountedRef.current) {
            setActiveBatchUpdateJob(job);
        }
        return persisted;
    }, []);
    const clearActiveBatchUpdateJob = useCallback(() => {
        saveActiveWarehouseProductBatchUpdateJob(null);
        if (isMountedRef.current) {
            setActiveBatchUpdateJob(null);
        }
    }, []);
    const refreshBatchUpdateListWithSelection = useCallback((productCodes: string[]) => {
        const keysToKeep = Array.from(new Set(productCodes));
        setSelectedRowKeys(keysToKeep);
        void refreshCurrentList().finally(() => {
            if (isMountedRef.current) {
                setSelectedRowKeys(keysToKeep);
            }
        });
    }, [refreshCurrentList]);
    const clearSubmittedBatchUpdateSelection = useCallback((submittedProductCodes: string[]) => {
        const submittedCodeSet = new Set(submittedProductCodes);
        const keysToKeep = selectedRowKeysRef.current
            .map(String)
            .filter((productCode) => !submittedCodeSet.has(productCode));
        refreshBatchUpdateListWithSelection(keysToKeep);
    }, [refreshBatchUpdateListWithSelection]);
    const restoreSubmittedBatchUpdateSelection = useCallback((submittedProductCodes: string[]) => {
        const keysToKeep = Array.from(new Set([
            ...selectedRowKeysRef.current.map(String),
            ...submittedProductCodes,
        ]));
        refreshBatchUpdateListWithSelection(keysToKeep);
    }, [refreshBatchUpdateListWithSelection]);
    const showPriceNotification = useCallback((summary: PriceNotificationSummary | null, context: { lines?: string[]; keyword?: string } = {}) => {
        const view = buildPriceNotificationView(summary, t);
        if (!view) return;
        notification[view.tone]({
            message: view.title,
            description: (<Space direction="vertical" size={4}>
              {[...(context.lines ?? []), ...view.lines].map((line) => (<div key={line}>{line}</div>))}
              {view.tags.length ? (<Space size={4} wrap>
                {view.tags.map((tag) => (<Tag key={tag.kind} color={tag.kind === 'priceUpdate' ? 'warning' : 'processing'}>{tag.text}</Tag>))}
              </Space>) : null}
              {view.showTasksLink ? (<Typography.Link onClick={() => navigate(buildPriceUpdateTasksLink({ tab: 'by-product', keyword: context.keyword }))}>
                {t('warehouse.priceNotification.viewTasks')}
              </Typography.Link>) : null}
            </Space>),
            duration: view.tone === 'success' ? 8 : 5,
            placement: 'topRight',
        });
    }, [navigate, t]);
    const finishBatchPriceNotification = useCallback(async (
        result: WarehouseProductBatchUpdateJobResult,
        job: ActiveWarehouseProductBatchUpdateJob,
    ) => {
        // 后台任务拿不到响应头，汇总来自任务快照；若随后还补发了建议折扣，以那次（最后一次）响应头为准，不相加。
        const priceNotificationCapture = createPriceNotificationCapture();
        priceNotificationCapture.accept(result.priceNotification);
        // 任务信息可能来自 localStorage 恢复，rate 形状不可信时宁可跳过也不能误清空建议折扣。
        const suggestedRate = job.suggestedDiscount?.rate;
        const hasSuggestedDiscount = job.suggestedDiscount !== undefined
            && (suggestedRate === null || (typeof suggestedRate === 'number' && suggestedRate >= 0 && suggestedRate <= 1));
        if (hasSuggestedDiscount && result.status !== 'Failed') {
            try {
                await setSuggestedDiscounts({
                    productCodes: job.submittedProductCodes,
                    suggestedDiscountRate: suggestedRate ?? null,
                    source: 'BatchUpdate',
                }, { onResponse: priceNotificationCapture.onResponse });
            }
            catch (error) {
                console.error(error);
                notification.error({
                    message: t('warehouse.priceNotification.batchSuggestedDiscountFailed'),
                    description: error instanceof Error ? error.message : undefined,
                    duration: 0,
                    placement: 'topRight',
                });
            }
        }
        if (!isMountedRef.current) return;
        showPriceNotification(priceNotificationCapture.getSummary(), {
            lines: [t('warehouse.priceNotification.productCountLine', { count: job.submittedProductCodes.length })],
        });
    }, [showPriceNotification, t]);
    const showBatchUpdateJobResult = useCallback((
        result: WarehouseProductBatchUpdateJobResult,
        submittedProductCodes: string[],
    ) => {
        const batchResult = result.result;
        const localFailedCount = Number(batchResult?.failedCount ?? batchResult?.FailedCount ?? batchResult?.failed ?? batchResult?.Failed ?? 0);
        const localErrors = batchResult?.errors ?? batchResult?.Errors ?? [];
        const localFailedItems = batchResult?.results
            ?.filter((item) => !item.success)
            .map((item) => ({ productCode: item.productCode, message: item.message })) ?? [];
        const hqSync = batchResult?.hqImageSync;
        const hqFailedCount = Number(hqSync?.failedCount ?? 0);
        const hqFailedItems = hqSync?.items
            ?.filter((item) => !item.success)
            .map((item) => ({ productCode: item.productCode, message: item.message })) ?? [];
        const hqErrors = hqSync?.errors ?? [];
        const hasHqFailure = Boolean(hqSync?.success === false
            || hqFailedCount > 0
            || hqFailedItems.length > 0
            || hqErrors.length > 0);
        const isPartial = result.status === 'PartiallySucceeded';
        const isFailed = result.status === 'Failed';
        if (isPartial || isFailed || localFailedCount > 0 || hasHqFailure) {
            setHqImageSyncFailDetail({
                localFailedCount,
                errors: [...localErrors, ...hqErrors, ...(!batchResult && result.message ? [result.message] : [])],
                items: [...localFailedItems, ...hqFailedItems],
            });
            setHqImageSyncFailOpen(true);
            restoreSubmittedBatchUpdateSelection(submittedProductCodes);
            notification[isFailed ? 'error' : 'warning']({
                message: isFailed
                    ? t('warehouse.batchUpdateJobFailed', '仓库商品批量修改失败')
                    : t('warehouse.batchUpdateJobPartialSucceeded', '仓库商品批量修改部分完成'),
                description: result.message
                    ?? batchResult?.message
                    ?? t('warehouse.batchUpdateJobRetryHint', '失败商品已保留选择，请查看明细后重试。'),
                duration: 0,
                placement: 'topRight',
            });
            return;
        }
        clearSubmittedBatchUpdateSelection(submittedProductCodes);
        notification.success({
            message: t('warehouse.batchUpdateJobSucceeded', '仓库商品批量修改完成'),
            description: batchResult?.message ?? result.message ?? t('warehouse.batchEditSuccess', '批量修改成功'),
            duration: 6,
            placement: 'topRight',
        });
        if (batchResult?.imageUpdatedCount != null) {
            message.info(t('warehouse.batchImageUpdatedCount', '本地图片地址更新 {{count}} 条', { count: batchResult.imageUpdatedCount }));
        }
    }, [clearSubmittedBatchUpdateSelection, restoreSubmittedBatchUpdateSelection, t]);
    const startBatchUpdateJobPolling = useCallback((job: ActiveWarehouseProductBatchUpdateJob) => {
        stopBatchUpdateJobPolling();
        const persisted = saveActiveBatchUpdateJob(job);
        if (!persisted) {
            notification.warning({
                message: t('warehouse.batchUpdateJobStorageUnavailable', '无法持久化后台任务状态'),
                description: t('warehouse.batchUpdateJobStorageUnavailableDescription', '任务仍会在当前页面继续跟踪，但刷新页面后无法自动恢复。'),
                duration: 0,
                placement: 'topRight',
            });
        }
        const poller = createWarehouseProductBatchUpdateJobPoller({
            jobId: job.jobId,
            getJob: async (jobId) => {
                const result = await getWarehouseProductBatchUpdateJob(jobId);
                saveActiveBatchUpdateJob({
                    ...job,
                    status: result.status,
                    message: result.message,
                });
                return result;
            },
            pollIntervalMs: WAREHOUSE_PRODUCT_BATCH_UPDATE_POLL_INTERVAL_MS,
            timeoutMs: WAREHOUSE_PRODUCT_BATCH_UPDATE_TIMEOUT_MS,
        });
        stopBatchUpdateJobPollingRef.current = poller.stop;
        void poller.promise
            .then((result) => {
            if (!isMountedRef.current) {
                return;
            }
            clearActiveBatchUpdateJob();
            stopBatchUpdateJobPollingRef.current = null;
            showBatchUpdateJobResult(result, job.submittedProductCodes);
            void finishBatchPriceNotification(result, job);
        })
            .catch((error) => {
            if (!isMountedRef.current || error instanceof HqProductSyncPollingCancelledError) {
                return;
            }
            stopBatchUpdateJobPollingRef.current = null;
            if (error instanceof RequestError && error.status === 404) {
                clearActiveBatchUpdateJob();
                restoreSubmittedBatchUpdateSelection(job.submittedProductCodes);
                notification.error({
                    message: t('warehouse.batchUpdateJobQueryFailed', '查询批量修改任务失败'),
                    description: t('warehouse.batchUpdateJobMissing', '任务不存在、已过期或服务已重启；已保留商品选择。'),
                    duration: 0,
                    placement: 'topRight',
                });
                return;
            }
            // 超时、临时网络错误与 5xx 不删除 jobId，刷新页面后可以继续查询现有任务。
            saveActiveBatchUpdateJob(job);
            if (error instanceof HqProductSyncPollingTimeoutError) {
                notification.warning({
                    message: t('warehouse.batchUpdateJobTimeoutTitle', '批量修改任务仍可能在后台执行'),
                    description: t('warehouse.batchUpdateJobTimeout', '前端轮询已超时，已保留商品选择。请稍后刷新页面确认结果后再决定是否重试。'),
                    duration: 0,
                    placement: 'topRight',
                });
                return;
            }
            notification.warning({
                message: t('warehouse.batchUpdateJobQueryFailed', '查询批量修改任务失败'),
                description: (<Space direction="vertical" size={4}>
              <div>{error instanceof Error ? error.message : t('warehouse.batchUpdateJobQueryFailed', '查询批量修改任务失败')}</div>
              <div>{t('warehouse.batchUpdateJobQueryInterrupted', '任务 ID 已保留，刷新页面可继续跟踪。')}</div>
            </Space>),
                duration: 0,
                placement: 'topRight',
            });
        });
    }, [clearActiveBatchUpdateJob, finishBatchPriceNotification, restoreSubmittedBatchUpdateSelection, saveActiveBatchUpdateJob, showBatchUpdateJobResult, stopBatchUpdateJobPolling, t]);
    const showActiveBatchUpdateJobStatus = useCallback((job: ActiveWarehouseProductBatchUpdateJob | null = activeBatchUpdateJob) => {
        if (!job) {
            return;
        }
        notification.info({
            message: t('warehouse.batchUpdateJobStatusTitle', '仓库商品批量修改正在后台执行'),
            description: (<Space direction="vertical" size={4}>
          <div>{t('warehouse.batchUpdateJobId', '任务 ID')}: {job.jobId}</div>
          <div>{t('warehouse.batchUpdateJobStatus', '任务状态')}: {job.status ?? 'Running'}</div>
          <div>{t('warehouse.batchUpdateJobStartedAt', '提交时间')}: {formatDateTime(job.createdAt, i18n.language)}</div>
          <div>{t('warehouse.batchUpdateJobProductCount', '商品数量')}: {job.submittedProductCodes.length}</div>
          {job.message ? <div>{job.message}</div> : null}
        </Space>),
            duration: 5,
            placement: 'topRight',
        });
    }, [activeBatchUpdateJob, i18n.language, t]);
    useEffect(() => {
        setCategoryLoading(true);
        void Promise.all([
            loadData({ page: 1 }),
            getSupplierOptions()
                .then(setSuppliers)
                .catch((error) => {
                console.error(error);
                message.error(t('productCreation.loadSupplierListFailed'));
            }),
            getActiveAustralianSuppliers()
                .then((items) => {
                const map: Record<string, string> = {};
                items.forEach((item) => {
                    if (item.localSupplierCode && item.name) {
                        map[item.localSupplierCode] = item.name;
                    }
                });
                setLocalSupplierNameMap(map);
            })
                .catch((error) => {
                console.error(error);
                message.error(t('warehouse.loadLocalSupplierFailed', '加载澳洲供应商失败'));
            }),
            getCategoryTree()
                .then((tree) => {
                setCategories(tree);
                const firstLevelExpandedKeys = collectCategoryExpandedKeys(tree, 1);
                setCategoryExpandedKeys(firstLevelExpandedKeys);
                setCategoryFilterExpandedKeys(firstLevelExpandedKeys);
            })
                .catch((error) => {
                console.error(error);
                message.error(error instanceof Error ? error.message : t('warehouse.categories.loadTreeFailed', '加载分类树失败'));
            })
                .finally(() => setCategoryLoading(false)),
        ]);
    }, []);
    useLayoutEffect(() => {
        isMountedRef.current = true;
        return () => {
            isMountedRef.current = false;
            listRequestGuardRef.current.invalidate();
            statusCountGuardRef.current.invalidate();
            if (searchDebounceTimerRef.current !== null) {
                window.clearTimeout(searchDebounceTimerRef.current);
                searchDebounceTimerRef.current = null;
            }
        };
    }, []);
    useEffect(() => {
        const restoredJob = readActiveWarehouseProductBatchUpdateJob();
        if (restoredJob?.jobId) {
            startBatchUpdateJobPolling(restoredJob);
        }
        return () => {
            stopBatchUpdateJobPolling();
        };
    }, [startBatchUpdateJobPolling, stopBatchUpdateJobPolling]);
    const handleOpenCreate = () => {
        setCreateModalOpen(true);
    };
    const formatSuggestedDiscountPercent = (percent: number | null) => percent === null
        ? t('warehouse.priceNotification.notSet')
        : `${percent}%`;
    const handleOpenEdit = (record: WarehouseProductListItem) => {
        setEditingItem(record);
        editingProductCodeRef.current = record.productCode;
        setEditingSuggestedDiscount({ loaded: false, percent: null });
        lookupSuggestedDiscounts([record.productCode])
            .then((items) => {
            // 期间用户可能已关闭弹窗或改开了另一个商品，过期结果不能回填。
            if (!isMountedRef.current || editingProductCodeRef.current !== record.productCode) return;
            const percent = suggestedDiscountRateToPercent(items.find((item) => item.productCode === record.productCode)?.suggestedDiscountRate);
            setEditingSuggestedDiscount({ loaded: true, percent });
            form.setFieldsValue({ suggestedDiscountPercent: percent });
        })
            .catch((error) => console.error(error));
        form.setFieldsValue({
            supplierCode: record.domesticSupplierCode,
            productName: record.name,
            englishProductName: record.nameEn,
            hbProductNo: record.itemNumber,
            barcode: record.barcode,
            productType: record.productType,
            domesticPrice: record.domesticPrice,
            oemPrice: record.labelPrice,
            importPrice: record.importPrice,
            packingQuantity: record.packingQty,
            unitVolume: record.volume,
            middlePackQuantity: record.minOrderQuantity,
            productImage: record.productImage,
            isActive: record.isActive,
        });
        setModalOpen(true);
    };
    const handleCloseModal = () => {
        setModalOpen(false);
        setEditingItem(null);
        editingProductCodeRef.current = null;
        setEditingSuggestedDiscount({ loaded: false, percent: null });
        form.resetFields();
    };
    const handleSave = async (supplyNotice?: SupplyNoticeInput) => {
        if (!editingItem) {
            return;
        }
        try {
            const values = await form.validateFields();
            // 只有从「上架」改为「下架」才要求先填写供货说明，说明随 full-update 同一请求提交；
            // 原本就是下架且保持下架的不强制（不传说明，后端保留已有说明）。弹窗取消则不保存。
            if (!supplyNotice && requiresDelistSupplyNotice(values.isActive, editingItem.isActive)) {
                setSupplyNoticeTarget({ mode: 'delist', productCodes: [editingItem.productCode], origin: 'editModal' });
                return;
            }
            setSaving(true);
            // 一次保存可能连发两个请求，通知汇总以最后一个带头的响应为准（不相加）。
            const priceNotificationCapture = createPriceNotificationCapture();
            await updateWarehouseProductFull(editingItem.productCode, {
                productName: values.productName,
                englishName: values.englishProductName,
                productSpecification: values.productSpecification,
                productType: values.productType,
                domesticPrice: values.domesticPrice,
                oemPrice: values.oemPrice,
                importPrice: values.importPrice,
                packingQuantity: values.packingQuantity,
                unitVolume: values.unitVolume,
                minOrderQuantity: values.middlePackQuantity,
                packingSize: values.packingSize,
                material: values.material,
                remark: values.remarks,
                productImage: values.productImage,
                isActive: values.isActive,
                // 说明只在下架时有意义；上架时即使残留也不提交。
                ...(supplyNotice && !values.isActive ? { supplyNotice } : {}),
                supplierCode: values.supplierCode,
            }, { onResponse: priceNotificationCapture.onResponse });
            const nextSuggestedDiscountPercent = values.suggestedDiscountPercent ?? null;
            const suggestedDiscountChanged = editingSuggestedDiscount.loaded
                && nextSuggestedDiscountPercent !== editingSuggestedDiscount.percent;
            if (suggestedDiscountChanged) {
                try {
                    await setSuggestedDiscounts({
                        productCodes: [editingItem.productCode],
                        suggestedDiscountRate: suggestedDiscountPercentToRate(nextSuggestedDiscountPercent),
                        source: 'WarehouseProducts',
                    }, { onResponse: priceNotificationCapture.onResponse });
                }
                catch (error) {
                    // 商品主体已保存成功，建议折扣失败只单独提示，不回滚也不阻断后续刷新。
                    console.error(error);
                    message.warning(`${t('warehouse.priceNotification.suggestedDiscountSaveFailed')}${error instanceof Error ? `: ${error.message}` : ''}`);
                }
            }
            message.success(t('warehouse.updateProductSuccess'));
            const retailPriceChanged = typeof values.oemPrice === 'number' && values.oemPrice !== (editingItem.labelPrice ?? null);
            showPriceNotification(priceNotificationCapture.getSummary(), {
                keyword: editingItem.itemNumber,
                lines: [
                    [editingItem.itemNumber, values.productName].filter(Boolean).join(' · '),
                    ...(retailPriceChanged ? [t('warehouse.priceNotification.retailPriceLine', {
                            from: editingItem.labelPrice == null ? '--' : `$${editingItem.labelPrice.toFixed(2)}`,
                            to: `$${(values.oemPrice as number).toFixed(2)}`,
                        })] : []),
                    ...(suggestedDiscountChanged ? [t('warehouse.priceNotification.suggestedDiscountLine', {
                            from: formatSuggestedDiscountPercent(editingSuggestedDiscount.percent),
                            to: formatSuggestedDiscountPercent(nextSuggestedDiscountPercent),
                        })] : []),
                ],
            });
            handleCloseModal();
            void refreshCurrentList();
        }
        catch (error) {
            if (typeof error === 'object' && error !== null && 'errorFields' in error) {
                return;
            }
            console.error(error);
            message.error(error instanceof Error ? error.message : t('warehouse.saveProductFailed'));
        }
        finally {
            setSaving(false);
        }
    };
    const getInlineCellKey = (cell: Pick<WarehouseProductInlineEditCell, 'productCode' | 'field'>) => `${cell.productCode}:${cell.field}`;
    const handleStartInlineEdit = (record: WarehouseProductListItem, field: WarehouseProductInlineEditField) => {
        if (!canInlineEditWarehouseProduct || inlineSaveLockRef.current) return;
        inlineCancelledCellRef.current = null;
        const value = record[field];
        setInlineEditingCell({
            productCode: record.productCode,
            field,
            value: value ?? null,
            originalValue: value,
        });
    };
    const handleInlineValueChange = (cell: WarehouseProductInlineEditCell, value: number | null) => {
        const cellKey = getInlineCellKey(cell);
        setInlineEditingCell((current) => current && getInlineCellKey(current) === cellKey
            ? { ...current, value }
            : current);
    };
    const handleCancelInlineEdit = (cell: WarehouseProductInlineEditCell) => {
        inlineCancelledCellRef.current = getInlineCellKey(cell);
        setInlineEditingCell(null);
    };
    const handleInlineSave = async (cell: WarehouseProductInlineEditCell) => {
        const cellKey = getInlineCellKey(cell);
        if (inlineCancelledCellRef.current === cellKey) {
            inlineCancelledCellRef.current = null;
            return;
        }
        if (inlineSaveLockRef.current) return;
        const value = cell.value;
        if (value === null || value === undefined) {
            setInlineEditingCell(null);
            return;
        }
        if (value < 0) {
            message.error(t('warehouse.nonNegativeValueRequired', '请输入非负数'));
            return;
        }
        if (value === cell.originalValue) {
            setInlineEditingCell(null);
            return;
        }

        const payload: PatchWarehouseProductPayload = cell.field === 'minOrderQuantity'
            ? { minOrderQuantity: value }
            : cell.field === 'domesticPrice'
                ? { domesticPrice: value }
                : cell.field === 'importPrice'
                    ? { importPrice: value }
                    : { oemPrice: value };

        // Enter 可能紧接着触发 blur；ref 锁必须在第一个 await 前占用，确保只发一次 PATCH。
        inlineSaveLockRef.current = cellKey;
        setInlineSavingCellKey(cellKey);
        try {
            const priceNotificationCapture = createPriceNotificationCapture();
            await patchWarehouseProduct(cell.productCode, payload, { onResponse: priceNotificationCapture.onResponse });
            if (!isMountedRef.current) return;
            if (cell.field === 'labelPrice') {
                const record = data.find((item) => item.productCode === cell.productCode);
                showPriceNotification(priceNotificationCapture.getSummary(), {
                    keyword: record?.itemNumber,
                    lines: [
                        [record?.itemNumber, record?.name].filter(Boolean).join(' · '),
                        t('warehouse.priceNotification.retailPriceLine', {
                            from: cell.originalValue == null ? '--' : `$${Number(cell.originalValue).toFixed(2)}`,
                            to: `$${value.toFixed(2)}`,
                        }),
                    ].filter(Boolean),
                });
            }
            setData((current) => current.map((item) => item.productCode === cell.productCode
                ? { ...item, [cell.field]: value }
                : item));
            setInlineEditingCell(null);
            message.success(t('common.saveSuccess', '保存成功'));
            await refreshCurrentList();
        }
        catch (error) {
            if (!isMountedRef.current) return;
            setData((current) => current.map((item) => item.productCode === cell.productCode
                ? { ...item, [cell.field]: cell.originalValue }
                : item));
            inlineCancelledCellRef.current = cellKey;
            setInlineEditingCell(null);
            console.error(error);
            message.error(error instanceof Error ? error.message : t('warehouse.saveProductFailed'));
        }
        finally {
            inlineSaveLockRef.current = null;
            if (isMountedRef.current) {
                setInlineSavingCellKey(null);
            }
        }
    };
    const renderInlineEditableNumberCell = (record: WarehouseProductListItem, field: WarehouseProductInlineEditField) => {
        const cellKey = getInlineCellKey({ productCode: record.productCode, field });
        const cell = inlineEditingCell && getInlineCellKey(inlineEditingCell) === cellKey
            ? inlineEditingCell
            : null;
        const isSaving = inlineSavingCellKey === cellKey;
        const pricePrefix = getWarehouseProductPricePrefix(field);
        if (cell) {
            return (<InputNumber
              className="warehouse-products-inline-editor"
              value={cell.value}
              min={0}
              precision={field === 'minOrderQuantity' ? 0 : 2}
              prefix={pricePrefix}
              controls={false}
              autoFocus
              disabled={isSaving}
              onChange={(value) => handleInlineValueChange(cell, value)}
              onBlur={() => void handleInlineSave(cell)}
              onKeyDown={(event) => {
                if (event.key === 'Enter') {
                    event.preventDefault();
                    void handleInlineSave(cell);
                }
                else if (event.key === 'Escape') {
                    event.preventDefault();
                    handleCancelInlineEdit(cell);
                }
            }}/>)
        }
        const value = record[field];
        // 零售价为空或为 0 视为缺失：显示琥珀色「—」，双击仍可直接补价。
        const retailPriceMissing = field === 'labelPrice' && isWarehouseProductRetailPriceMissing(value);
        return (<div className={`warehouse-products-inline-cell warehouse-products-inline-value-${field}${retailPriceMissing ? ' is-missing' : ''}`}>
          <div
            className={`warehouse-products-inline-value${canInlineEditWarehouseProduct ? ' is-editable' : ''}`}
            title={canInlineEditWarehouseProduct ? t('warehouse.doubleClickToEdit', '双击编辑') : undefined}
            onDoubleClick={canInlineEditWarehouseProduct ? () => handleStartInlineEdit(record, field) : undefined}>
            {retailPriceMissing
              ? <span title={t('warehouseUi.products.retailPriceMissing')}>—</span>
              : field === 'minOrderQuantity'
                ? value ?? '--'
                : formatPrice(value, pricePrefix ?? '$')}
          </div>
        </div>);
    };
    const showPushToHqResult = useCallback((result: PushProductsToHqResult) => {
        const errors = result.errors ?? [];
        const detailStats = [
            { label: t('posAdmin.products.pushToHqAffectedRows', 'HQ影响记录'), value: result.affectedRowCount ?? 0 },
            { label: t('posAdmin.products.productsAdded', '商品新增'), value: result.productsAdded ?? 0 },
            { label: t('posAdmin.products.productsUpdated', '商品更新'), value: result.productsUpdated ?? 0 },
            { label: t('posAdmin.products.storeRetailPricesCreated', '门店零售价新增'), value: result.storeRetailPricesCreated ?? 0 },
            { label: t('posAdmin.products.storeRetailPricesUpdated', '门店零售价更新'), value: result.storeRetailPricesUpdated ?? 0 },
            { label: t('posAdmin.products.productSetCodesCreated', '套装编码新增'), value: result.productSetCodesCreated ?? 0 },
            { label: t('posAdmin.products.productSetCodesUpdated', '套装编码更新'), value: result.productSetCodesUpdated ?? 0 },
            { label: t('posAdmin.products.storeMultiCodesCreated', '门店多码新增'), value: result.storeMultiCodesCreated ?? 0 },
            { label: t('posAdmin.products.storeMultiCodesUpdated', '门店多码更新'), value: result.storeMultiCodesUpdated ?? 0 },
        ].filter((item) => item.value > 0);
        const content = (<Space direction="vertical" size={6}>
            {result.message ? <div>{result.message}</div> : null}
            <div>
              {t('posAdmin.products.pushToHqResult', '发送完成：商品成功 {{success}}，失败 {{failed}}，合计 {{total}}', {
                success: result.successCount ?? 0,
                failed: result.failedCount ?? 0,
                total: result.totalCount ?? (result.successCount ?? 0) + (result.failedCount ?? 0),
            })}
            </div>
            {detailStats.map((item) => (<div key={item.label}>{item.label}: {item.value}</div>))}
            {errors.length ? (<div style={{ whiteSpace: 'pre-wrap' }}>
              {t('posAdmin.products.partialSyncError', '部分同步错误')}：{errors.join('\n')}
            </div>) : null}
          </Space>);

        if (errors.length || (result.failedCount ?? 0) > 0) {
            Modal.warning({
                title: t('posAdmin.products.pushToHqPartialSucceeded', '发送到 HQ 部分成功'),
                content,
            });
            return false;
        }

        Modal.success({
            title: t('posAdmin.products.pushToHqSucceeded', '发送到 HQ 完成'),
            content,
        });
        return true;
    }, [t]);
    // 每次打开发送到 HQ 弹窗都重新获取最新 HQ 分店选项；失败时由弹窗保留并可重试。
    const loadPushToHqStoreOptions = async (): Promise<boolean> => {
        const guard = pushToHqStoreOptionsGuardRef.current;
        const requestId = guard.begin();
        // 单飞守卫：同一时刻只允许一个选项请求，避免连续点击重试造成重复 GET。
        if (requestId < 0) return false;
        setPushToHqStoreOptionsLoading(true);
        setPushToHqStoreOptionsError(null);
        try {
            const options = await getPushToHqStoreOptions();
            if (!isMountedRef.current || !guard.isLatest(requestId)) return false;
            setPushToHqStoreOptions(options);
            return true;
        }
        catch (error) {
            if (!isMountedRef.current || !guard.isLatest(requestId)) return false;
            setPushToHqStoreOptionsError(
                error instanceof Error
                    ? error.message
                    : t('posAdmin.products.pushToHqStoreOptionsLoadFailed', '获取 HQ 分店选项失败，请重试'),
            );
            return false;
        }
        finally {
            guard.complete(requestId);
            if (isMountedRef.current && guard.isLatest(requestId)) {
                setPushToHqStoreOptionsLoading(false);
            }
        }
    };
    const handlePushToHq = async () => {
        if (!access.canManagePosProducts) {
            message.warning(t('posAdmin.products.noManagePermission', '无权限管理商品'));
            return;
        }
        if (!selectedRowKeys.length) {
            message.warning(t('warehouse.selectProductsFirst', '请先选择商品'));
            return;
        }
        // 使用 ref 作为即时锁并覆盖选项获取，避免状态尚未刷新时连续点击打开多个弹窗。
        if (pushToHqLoadingRef.current || pushToHqModalOpen) return;
        const productCodes = selectedRowKeys.map(String);
        pushToHqProductCodesRef.current = productCodes;
        pushToHqLoadingRef.current = true;
        setPushToHqLoading(true);
        setPushToHqModalOpen(true);
        setPushToHqStoreOptions([]);

        try {
            await loadPushToHqStoreOptions();
        }
        finally {
            pushToHqLoadingRef.current = false;
            if (isMountedRef.current) {
                setPushToHqLoading(false);
            }
        }
    };
    const handlePushToHqConfirm = async (
        updateFields: PushProductsToHqUpdateField[],
        targetStoreCodes: string[],
    ) => {
        // 选项请求在途或提交进行中时拒绝确认；通过后再同步占用两个锁，
        // 避免同 tick 内取消在状态刷新前释放锁，也避免提前占用锁导致永不释放。
        if (pushToHqLoadingRef.current || pushToHqConfirmLoadingRef.current) return;
        pushToHqLoadingRef.current = true;
        pushToHqConfirmLoadingRef.current = true;
        const productCodes = pushToHqProductCodesRef.current;
        setPushToHqLoading(true);
        setPushToHqConfirmLoading(true);

        try {
            if (!isMountedRef.current) return;
            const currentProductCodes = selectedRowKeysRef.current.map(String);
            if (!areWarehouseProductCodeSelectionsEqual(productCodes, currentProductCodes)) {
                message.warning(t('warehouse.pushToHqSelectionChanged', '选中商品数据已变化，请重新选择后再试'));
                return;
            }
            const payload = buildWarehouseProductHqPushPayload(dataRef.current, currentProductCodes, updateFields, targetStoreCodes);
            if (payload.items?.length !== currentProductCodes.length) {
                message.warning(t('warehouse.pushToHqSelectionChanged', '选中商品数据已变化，请重新选择后再试'));
                return;
            }
            const result = await pushProductsToHq(payload);
            if (!isMountedRef.current) return;
            if (!showPushToHqResult(result)) return;
            setPushToHqModalOpen(false);
            setSelectedRowKeys([]);
            await refreshCurrentList();
        }
        catch (error) {
            if (!isMountedRef.current) return;
            const errorResult = extractPushToHqErrorResult(error);
            if (errorResult) {
                Modal.error({
                    title: t('posAdmin.products.pushToHqFailed', '发送到 HQ 失败'),
                    content: (<div>
                        <div>{errorResult.message || (error instanceof Error ? error.message : t('posAdmin.products.pushToHqFailed', '发送到 HQ 失败'))}</div>
                        {errorResult.errors.length ? (<div style={{ marginTop: 8, whiteSpace: 'pre-wrap' }}>
                            {errorResult.errors.join('\n')}
                          </div>) : null}
                      </div>),
                });
            }
            else {
                message.error(error instanceof Error ? error.message : t('posAdmin.products.pushToHqFailed', '发送到 HQ 失败'));
            }
        }
        finally {
            pushToHqConfirmLoadingRef.current = false;
            pushToHqLoadingRef.current = false;
            if (isMountedRef.current) {
                setPushToHqConfirmLoading(false);
                setPushToHqLoading(false);
            }
        }
    };
    const handlePushToHqCancel = () => {
        // 提交进行中忽略取消，避免释放锁后立即重开导致重复提交。
        if (pushToHqConfirmLoadingRef.current) return;
        pushToHqLoadingRef.current = false;
        pushToHqConfirmLoadingRef.current = false;
        // 使仍在途的选项请求过期，防止取消后旧响应覆盖下一次打开的状态。
        pushToHqStoreOptionsGuardRef.current.invalidate();
        if (isMountedRef.current) {
            setPushToHqModalOpen(false);
            setPushToHqConfirmLoading(false);
            setPushToHqLoading(false);
        }
    };
    const handleBatchToggleActive = async (nextIsActive: boolean, supplyNotice?: SupplyNoticeInput) => {
        if (!selectedRowKeys.length) {
            return;
        }
        // 下架必须先登记供货说明（后续计划必选），说明随下架同一请求提交。
        if (!nextIsActive && !supplyNotice) {
            setSupplyNoticeTarget({ mode: 'delist', productCodes: selectedRowKeys.map(String), origin: 'batchToggle' });
            return;
        }
        try {
            setBatchActionLoading(true);
            const result = await batchToggleWarehouseProductsActive({
                productCodes: selectedRowKeys.map(String),
                isActive: nextIsActive,
                supplyNotice,
            });
            if (!result.success) {
                message.error(result.message || t('warehouse.batchStatusUpdateFailed'));
                return;
            }
            message.success(result.message || t('warehouse.batchStatusUpdated', { status: getShelfStatusLabel(nextIsActive, t), count: selectedRowKeys.length }));
            void refreshCurrentList();
        }
        catch (error) {
            console.error(error);
            message.error(error instanceof Error ? error.message : t('warehouse.batchStatusUpdateFailed'));
        }
        finally {
            setBatchActionLoading(false);
        }
    };
    const openBatchEdit = () => {
        if (activeBatchUpdateJob) {
            showActiveBatchUpdateJobStatus(activeBatchUpdateJob);
            return;
        }
        if (!selectedRowKeys.length) {
            message.warning(t('warehouse.selectProductsFirst', '请先选择商品'));
            return;
        }
        batchEditForm.resetFields();
        // 每次打开批量修改都重置图片生成选项：默认关闭生成，基础地址回到默认值，HQ 同步默认关闭。
        batchEditForm.setFieldsValue({
            generateImageUrls: false,
            imageBaseUrl: WAREHOUSE_PRODUCT_IMAGE_DEFAULT_BASE_URL,
            syncImageToHq: false,
        });
        setBatchEditOpen(true);
    };
    const handleCategoryMutationCommitted = (change: ContainerCategoryChange) => {
        setTargetCategoryGuid((current) => resolveContainerCategoryTargetAfterMutation(current, change));
    };
    const handleCategoriesChanged = (tree: WarehouseCategoryNode[], change: ContainerCategoryChange) => {
        setCategories(tree);
        const firstLevelExpandedKeys = collectCategoryExpandedKeys(tree, 1);
        setCategoryExpandedKeys(firstLevelExpandedKeys);
        setCategoryFilterExpandedKeys(firstLevelExpandedKeys);
        setTargetCategoryGuid((current) => resolveContainerCategorySelectionAfterRefresh(tree, undefined, current, change).activeTargetCategoryGuid);
    };
    const openBatchCategory = () => {
        if (!selectedRowKeys.length) {
            message.warning(t('warehouse.selectProductsFirst', '请先选择商品'));
            return;
        }
        setTargetCategoryGuid((current) => findWarehouseCategory(categories, current)?.categoryGUID);
        // 每次打开批量分类弹窗都只展开到一级分类，避免默认露出过深的子分类。
        setCategoryExpandedKeys(collectCategoryExpandedKeys(categories, 1));
        setBatchCategoryOpen(true);
    };
    const handleBatchCategorySave = async () => {
        if (!selectedRowKeys.length) {
            message.warning(t('warehouse.selectProductsFirst', '请先选择商品'));
            return;
        }
        if (!targetCategoryGuid) {
            message.warning(t('warehouse.categories.selectTargetFirst', '请选择目标分类'));
            return;
        }
        try {
            setBatchCategorySaving(true);
            const selectedProductCodes = selectedRowKeys.map(String);
            const selectedProductCodeSet = new Set(selectedProductCodes);
            // 批量分类只提交商品编码和目标分类，避免误改价格、状态等其他业务字段。
            await batchAssignProducts(targetCategoryGuid, selectedProductCodes);
            // 保存成功后只更新当前页分类显示，不触发表格重新查询。
            setData((items) => items.map((item) => selectedProductCodeSet.has(item.productCode)
                ? {
                    ...item,
                    warehouseCategoryGUID: targetCategoryGuid,
                    categoryName: selectedTargetCategory
                        ? formatWarehouseCategoryNodeName(selectedTargetCategory, i18n.language)
                        : item.categoryName,
                    categoryPath: selectedTargetCategoryPath || item.categoryPath,
                }
                : item));
            message.success(t('warehouse.categories.batchUpdateSuccess', {
                count: selectedRowKeys.length,
                categoryName: selectedTargetCategory?.categoryName || t('warehouse.categories.targetCategory', '目标分类'),
            }));
            setBatchCategoryOpen(false);
            setTargetCategoryGuid(undefined);
            setSelectedRowKeys([]);
        }
        catch (error) {
            console.error(error);
            message.error(error instanceof Error ? error.message : t('warehouse.categories.batchUpdateFailed', '批量更新商品分类失败'));
        }
        finally {
            setBatchCategorySaving(false);
        }
    };
    const submitBatchEdit = async (values: BatchEditFormValues, options: {
        generateImageUrls?: boolean;
        imageBaseUrl?: string;
        syncImageToHq?: boolean;
        // 批量设为下架时的供货说明，随后台任务同一请求提交，只登记显式设为下架的商品。
        supplyNotice?: SupplyNoticeInput;
    }, suggestedDiscount?: { rate: number | null }) => {
        if (!selectedRowKeys.length) {
            message.warning(t('warehouse.selectProductsFirst', '请先选择商品'));
            return;
        }
        try {
            setBatchEditSaving(true);
            const submittedProductCodes = selectedRowKeys.map(String);
            const items = submittedProductCodes.map((code) => {
                const rawItem: WarehouseProductBatchUpdateItem = {
                    ProductCode: code,
                    SupplierCode: values.supplierCode,
                    DomesticPrice: values.domesticPrice,
                    OEMPrice: values.oemPrice,
                    ImportPrice: values.importPrice,
                    PackingQuantity: values.packingQuantity,
                    // 只传用户填写的字段，避免批量误覆盖；中包数来源为 WarehouseProduct.MinOrderQuantity。
                    MinOrderQuantity: values.minOrderQuantity,
                    Volume: values.unitVolume,
                    IsActive: values.isActive,
                };
                return Object.fromEntries(Object.entries(rawItem).filter(([, value]) => value !== undefined && value !== null)) as WarehouseProductBatchUpdateItem;
            });
            const job = await createWarehouseProductBatchUpdateJob(items, options);
            if (!job.jobId) {
                notification.error({
                    message: t('warehouse.batchUpdateJobCreateFailed', '创建仓库商品批量修改任务失败'),
                    description: job.message ?? t('warehouse.batchEditFailed', '批量修改失败'),
                    duration: 0,
                    placement: 'topRight',
                });
                return;
            }
            const activeJob: ActiveWarehouseProductBatchUpdateJob = {
                jobId: job.jobId,
                operationId: job.operationId ?? job.jobId,
                createdAt: job.createdAt ?? new Date().toISOString(),
                status: job.status,
                message: job.message,
                submittedProductCodes,
                ...(suggestedDiscount ? { suggestedDiscount } : {}),
            };
            setBatchEditOpen(false);
            batchEditForm.resetFields();
            notification.info({
                message: t('warehouse.batchUpdateJobSubmitted', '批量修改任务已提交，正在后台执行'),
                description: job.isDuplicateRequest
                    ? t('warehouse.batchUpdateJobDuplicate', '相同任务已在后台执行，已继续跟踪现有任务。')
                    : t('warehouse.batchUpdateJobSubmittedDescription', '完成后会在右上角通知结果；执行期间保留当前商品选择。'),
                duration: 4,
                placement: 'topRight',
            });
            startBatchUpdateJobPolling(activeJob);
        }
        catch (error) {
            if (typeof error === 'object' && error !== null && 'errorFields' in error) {
                return;
            }
            console.error(error);
            message.error(error instanceof Error ? error.message : t('warehouse.batchEditFailed', '批量修改失败'));
        }
        finally {
            setBatchEditSaving(false);
        }
    };
    const submitBatchSuggestedDiscountOnly = async (suggestedDiscount: { rate: number | null }) => {
        // 只勾选了建议折扣、没有其它商品字段：无需创建后台任务，直接设置并按响应头提示。
        const productCodes = selectedRowKeys.map(String);
        try {
            setBatchEditSaving(true);
            const priceNotificationCapture = createPriceNotificationCapture();
            await setSuggestedDiscounts({ productCodes, suggestedDiscountRate: suggestedDiscount.rate, source: 'BatchUpdate' }, { onResponse: priceNotificationCapture.onResponse });
            setBatchEditOpen(false);
            batchEditForm.resetFields();
            message.success(t('warehouse.batchEditSuccess', '批量修改成功'));
            showPriceNotification(priceNotificationCapture.getSummary(), {
                lines: [t('warehouse.priceNotification.productCountLine', { count: productCodes.length })],
            });
        }
        catch (error) {
            console.error(error);
            message.error(error instanceof Error ? error.message : t('warehouse.priceNotification.batchSuggestedDiscountFailed'));
        }
        finally {
            setBatchEditSaving(false);
        }
    };
    const handleBatchEditSave = async (supplyNotice?: SupplyNoticeInput) => {
        if (!selectedRowKeys.length) {
            message.warning(t('warehouse.selectProductsFirst', '请先选择商品'));
            return;
        }
        let values: BatchEditFormValues;
        try {
            values = await batchEditForm.validateFields();
        }
        catch (error) {
            if (typeof error === 'object' && error !== null && 'errorFields' in error) {
                return;
            }
            throw error;
        }
        const generateImageUrls = values.generateImageUrls === true;
        const suggestedDiscount = values.setSuggestedDiscount === true
            ? { rate: suggestedDiscountPercentToRate(values.suggestedDiscountPercent) }
            : undefined;
        const hasChanges = Object.entries(values).some(([field, value]) => {
            if (WAREHOUSE_PRODUCT_BATCH_SUGGESTED_DISCOUNT_FIELDS.has(field)) {
                return false;
            }
            if (isWarehouseProductBatchImageField(field)) {
                if (generateImageUrls) {
                    return field === 'generateImageUrls';
                }
                // 图片关闭时默认基础地址不算变更。
                return false;
            }
            return value !== undefined && value !== null;
        });
        if (!hasChanges && !suggestedDiscount) {
            message.warning(t('warehouse.batchEditNoChanges', '请至少填写一个修改字段'));
            return;
        }
        const imageBaseUrl = generateImageUrls
            ? normalizeWarehouseProductImageBaseUrl(values.imageBaseUrl)
            : undefined;
        if (generateImageUrls && !imageBaseUrl) {
            message.warning(t('warehouse.batchImageBaseUrlRequired', '请输入基础图片地址'));
            return;
        }
        if (generateImageUrls && !isValidWarehouseProductImageBaseUrl(imageBaseUrl)) {
            message.warning(t('warehouse.batchImageBaseUrlInvalid', '请输入不含查询参数或片段的 HTTP(S) 目录地址'));
            return;
        }
        const syncImageToHq = generateImageUrls && values.syncImageToHq === true;
        // 批量把「是否上架」设为下架时，与批量上下架一致先登记供货说明；填写完成后回到这里继续确认提交。
        if (!supplyNotice && requiresDelistSupplyNotice(values.isActive)) {
            setSupplyNoticeTarget({ mode: 'delist', productCodes: selectedRowKeys.map(String), origin: 'batchEdit' });
            return;
        }
        Modal.confirm({
            title: t('warehouse.batchEditConfirmTitle', '确认批量修改'),
            content: (<Space direction="vertical" size={8}>
            <div>{t('warehouse.batchImageConfirmCount', '商品数量：{{count}} 个', { count: selectedRowKeys.length })}</div>
            {generateImageUrls ? (<>
              <div>{t('warehouse.batchImageConfirmBaseUrl', '基础图片地址：{{url}}', { url: imageBaseUrl })}</div>
              <div>{t('warehouse.batchImageConfirmOverwrite', '本地覆盖：是（将覆盖选中商品现有图片地址）')}</div>
              <div>{t('warehouse.batchImageConfirmHq', '同步 HQ：{{value}}', { value: syncImageToHq ? t('common.yes') : t('common.no') })}</div>
            </>) : null}
            {suggestedDiscount ? (<div>{t('warehouse.priceNotification.batchSuggestedDiscountConfirm', {
                value: suggestedDiscount.rate === null ? t('warehouse.priceNotification.notSet') : `${suggestedDiscountRateToPercent(suggestedDiscount.rate)}%`,
            })}</div>) : null}
          </Space>),
            okText: t('warehouse.batchEditConfirmOk', '确认保存'),
            cancelText: t('common.cancel'),
            onOk: () => hasChanges
                ? submitBatchEdit(values, {
                    generateImageUrls,
                    imageBaseUrl,
                    syncImageToHq,
                    ...(supplyNotice && values.isActive === false ? { supplyNotice } : {}),
                }, suggestedDiscount)
                : submitBatchSuggestedDiscountOnly(suggestedDiscount!),
        });
    };
    const handleToggleSingleActive = async (record: WarehouseProductListItem, nextIsActive: boolean, supplyNotice?: SupplyNoticeInput) => {
        if (!nextIsActive && !supplyNotice) {
            setSupplyNoticeTarget({ mode: 'delist', productCodes: [record.productCode], origin: 'rowToggle' });
            return;
        }
        try {
            setTogglingProductCodes((current) => [...current, record.productCode]);
            const result = await batchToggleWarehouseProductsActive({
                productCodes: [record.productCode],
                isActive: nextIsActive,
                supplyNotice,
            });
            if (!result.success) {
                message.error(result.message || t('warehouse.toggleStatusFailed'));
                return;
            }
            setData((current) => current.map((item) => (item.productCode === record.productCode ? { ...item, isActive: nextIsActive } : item)));
            // 上架后说明已被后端关闭；下架后重新拉一次拿到服务端归一化后的说明。
            if (nextIsActive) {
                setSupplyNotices((current) => { const next = { ...current }; delete next[record.productCode]; return next; });
            }
            else {
                void loadSupplyNotices([{ ...record, isActive: false }]).then(() => undefined);
            }
            // 行内上下架只就地更新当前行、不重查列表，状态页签数量单独按当前条件重新计数。
            if (appliedQuery) {
                statusCountsStaleRef.current = true;
                loadStatusCounts(appliedQuery);
            }
            message.success(result.message || t('warehouse.statusToggled', { status: getShelfStatusLabel(nextIsActive, t) }));
        }
        catch (error) {
            console.error(error);
            message.error(error instanceof Error ? error.message : t('warehouse.toggleStatusFailed'));
        }
        finally {
            setTogglingProductCodes((current) => current.filter((code) => code !== record.productCode));
        }
    };
    // 说明弹窗提交：下架路径转交上下架处理器；修改路径直接写说明接口。
    const handleSupplyNoticeSubmit = async (notice: SupplyNoticeInput) => {
        if (!supplyNoticeTarget) {
            return;
        }
        const { mode, productCodes, origin } = supplyNoticeTarget;
        setSupplyNoticeSaving(true);
        try {
            if (mode === 'delist' && origin === 'editModal') {
                // 编辑弹窗：带说明重新走保存，说明随 full-update 同一请求提交。
                await handleSave(notice);
            }
            else if (mode === 'delist' && origin === 'batchEdit') {
                // 批量修改：带说明回到批量保存流程，继续二次确认并提交后台任务。
                await handleBatchEditSave(notice);
            }
            else if (mode === 'delist' && origin === 'rowToggle') {
                // 行上开关：只下架被点击的那一行。必须按来源分发，不能按勾选数量推断，
                // 否则恰好勾选了另一行时会把勾选行下架、被点击的行反而不动。
                const record = data.find((item) => item.productCode === productCodes[0]);
                if (record) {
                    await handleToggleSingleActive(record, false, notice);
                }
            }
            else if (mode === 'delist') {
                await handleBatchToggleActive(false, notice);
            }
            else {
                const result = await upsertWarehouseSupplyNotices(productCodes, notice);
                if (!result.success) {
                    message.error(result.message || t('warehouse.toggleStatusFailed'));
                    return;
                }
                message.success(result.message);
                if (result.skippedProductCodes.length) {
                    message.warning(t('supplyNotice.form.skippedHint', { count: result.skippedProductCodes.length }));
                }
                void loadSupplyNotices(data.filter((item) => productCodes.includes(item.productCode)));
            }
            setSupplyNoticeTarget(null);
        }
        finally {
            setSupplyNoticeSaving(false);
        }
    };
    const handleOpenSetItems = async (record: WarehouseProductListItem) => {
        setCurrentSetProduct(record);
        setSetItemsOpen(true);
        setSetItemsLoading(true);
        setSetItemsDraft([]);
        setDeletedSetCodeIds([]);
        try {
            const result = await getSetCodeGridData({ productCode: record.productCode, pageIndex: 1, pageSize: 200 });
            setSetItemsDraft(result.items ?? []);
        }
        catch (error) {
            console.error(error);
            message.error(error instanceof Error ? error.message : t('warehouse.loadSetItemsFailed'));
            setSetItemsOpen(false);
            setCurrentSetProduct(null);
        }
        finally {
            setSetItemsLoading(false);
        }
    };
    const handleSaveSetItems = async () => {
        if (!currentSetProduct) {
            return;
        }
        const invalidSetCodeItem = setItemsDraft.find((item) => !item.setBarcode?.trim() ||
            item.setPurchasePrice === undefined ||
            item.setPurchasePrice === null ||
            item.setRetailPrice === undefined ||
            item.setRetailPrice === null);
        if (invalidSetCodeItem) {
            message.error(t('warehouse.invalidSetCodeDetail', '套装明细条码、进货价和零售价不能为空'));
            return;
        }
        try {
            setSetItemsSaving(true);
            const newItems = setItemsDraft.filter((item) => item.id?.startsWith('new_'));
            const existingItems = setItemsDraft.filter((item) => item.id && !item.id.startsWith('new_'));
            // 仓库套装明细按 product-set-codes 分批保存，避免继续写入国内采购商品的 set-items 表。
            if (newItems.length) {
                await batchCreateSetCodes({
                    items: newItems.map((item) => ({
                        productCode: currentSetProduct.productCode,
                        setItemNumber: item.setItemNumber?.trim() || undefined,
                        setBarcode: item.setBarcode!.trim(),
                        setPurchasePrice: item.setPurchasePrice,
                        setRetailPrice: item.setRetailPrice,
                        isActive: item.isActive ?? true,
                    })),
                });
            }
            if (existingItems.length) {
                await batchUpdateSetBarcodes({
                    items: existingItems.map((item) => ({
                        id: item.id!,
                        setBarcode: item.setBarcode!.trim(),
                    })),
                });
                await batchUpdateSetPrices({
                    items: existingItems.map((item) => ({
                        id: item.id!,
                        setPurchasePrice: item.setPurchasePrice,
                        setRetailPrice: item.setRetailPrice,
                    })),
                });
                await batchUpdateSetStatus({
                    items: existingItems.map((item) => ({
                        id: item.id!,
                        isActive: item.isActive ?? true,
                    })),
                });
            }
            if (deletedSetCodeIds.length) {
                await batchDeleteSetCodes({ ids: deletedSetCodeIds });
            }
            message.success(t('warehouse.setItemsUpdated'));
            setSetItemsOpen(false);
            setCurrentSetProduct(null);
            setSetItemsDraft([]);
            setDeletedSetCodeIds([]);
            void refreshCurrentList();
        }
        catch (error) {
            console.error(error);
            message.error(error instanceof Error ? error.message : t('warehouse.saveSetItemsFailed'));
        }
        finally {
            setSetItemsSaving(false);
        }
    };
    const handleExport = async () => {
        try {
            setExporting(true);
            setExportProgress(0);
            setExportMessage(t('warehouse.preparingExport'));
            const selectedProducts = selectedRowKeys.length
                ? data.filter((item) => selectedRowKeys.includes(item.id))
                : [];
            let productsToExport = selectedProducts;
            if (!productsToExport.length) {
                if (!total) {
                    message.warning(t('warehouse.noDataToExport'));
                    return;
                }
                const exportQuery = buildGridQuery({
                    page: 1,
                    pageSize: Math.max(total, 1),
                });
                const result = await getWarehouseProductsTable(exportQuery);
                productsToExport = result.items;
            }
            if (!productsToExport.length) {
                message.warning(t('warehouse.noDataToExport'));
                return;
            }
            const exportResult = await exportDomesticProductsToExcel(productsToExport.map((item) => ({
                itemNumber: item.itemNumber,
                barcode: item.barcode,
                name: item.name,
                labelPrice: item.labelPrice,
                productImage: item.productImage,
            })), {
                includeLabelPrice,
                includeBarcodeImage,
                includeProductImage,
                fileName: t('warehouse.warehouseProducts'),
                onProgress: (progress, nextMessage) => {
                    setExportProgress(progress);
                    setExportMessage(nextMessage);
                },
            });
            if (exportResult.failedProductImages.length > 0) {
                setExportFailDetail(exportResult.failedProductImages);
                setExportFailDetailOpen(true);
            }
            else {
                message.success(t('warehouse.exportSuccess'));
            }
            setExportConfigOpen(false);
            setExportProgress(0);
            setExportMessage('');
        }
        catch (error) {
            console.error(error);
            message.error(error instanceof Error ? error.message : t('warehouse.exportFailed'));
        }
        finally {
            setExporting(false);
        }
    };
    // 商品组合列：缩略图 + 货号（可复制）+ 套装/多码标签 + 名称；第二行 条码 · 分类末级 · 中包 N。
    const renderProductCell = (record: WarehouseProductListItem) => {
        const typeTagClassName = record.productType === ProductType.SET
            ? 'warehouse-products-type-tag-set'
            : record.productType === ProductType.MULTICODE
                ? 'warehouse-products-type-tag-multi'
                : null;
        const formatMiddlePack = (count: number) => t('warehouseUi.products.middlePack', { count });
        const metaParts = buildWarehouseProductMetaParts(record, formatMiddlePack);
        // 单元格只显示分类末级，悬浮提示里换成完整多级路径。
        const categoryPath = record.categoryName ? getWarehouseProductCategoryTooltip(record, categoryLookup, i18n.language) : undefined;
        const metaTitle = buildWarehouseProductMetaParts({ ...record, categoryName: categoryPath || record.categoryName }, formatMiddlePack).join(' · ');
        const displayName = record.name || record.nameEn || '--';
        return (<div className="warehouse-products-product-cell">
          <span className="warehouse-products-thumb">
            {/* 表格内用 COS 缩略图并懒加载，预览时才取原图。 */}
            {record.productImage ? (<ProductListImage src={record.productImage} size={40} radius={6} fit="cover" fallback="data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs="/>) : (<PictureOutlined aria-hidden/>)}
          </span>
          <div className="warehouse-products-product-text">
            <div className="warehouse-products-product-line">
              <span className="warehouse-products-item-number">{record.itemNumber || '--'}</span>
              {record.itemNumber ? (<Tooltip title={t('common.copy')}>
                <Button size="small" type="text" className="warehouse-products-copy-button" icon={<CopyOutlined />} aria-label={t('common.copy')} onClick={() => void copyTextToClipboard(record.itemNumber)}/>
              </Tooltip>) : null}
              {typeTagClassName ? (<span className={`warehouse-products-type-tag ${typeTagClassName}`}>{getProductTypeLabel(record.productType, t)}</span>) : null}
              <span className="warehouse-products-product-name" title={displayName}>{displayName}</span>
            </div>
            {metaParts.length ? (<div className="warehouse-products-product-meta" title={metaTitle}>{metaParts.join(' · ')}</div>) : null}
          </div>
        </div>);
    };
    // 下架行状态单元格第二行：供货计划摘要（逾期红色、可带关注门店数）；有写权限时点击修改说明。
    const renderSupplyNoticeSummary = (record: WarehouseProductListItem) => {
        const notice = supplyNotices[record.productCode];
        const summary = buildSupplyNoticeSummary(notice, {
            none: t('supplyNotice.column.none'),
            plan: (plan) => t(`supplyNotice.plan.${plan}`),
            expected: () => (notice ? formatSupplyExpected(notice, t) : ''),
            overdue: t('supplyNotice.overdueTag'),
            watchers: (count) => t('supplyNotice.column.watchers', { count }),
        });
        const className = `warehouse-products-supply-summary${summary.tone === 'overdue' ? ' is-overdue' : summary.tone === 'none' ? ' is-none' : ''}`;
        if (!access.canWriteProduct) {
            return <span className={className} title={summary.text}>{summary.text}</span>;
        }
        return (<Tooltip title={t('supplyNotice.column.edit')}>
          <button type="button" className={className} onClick={() => setSupplyNoticeTarget({ mode: 'edit', productCodes: [record.productCode], initial: notice ?? null })}>
            {summary.text}
          </button>
        </Tooltip>);
    };
    // 行操作收敛为「编辑 + ⋯」：修改记录、数据查询、套装子项/多码管理、上架/下架都在 ⋯ 菜单里，权限判断与原按钮一致。
    const buildRowActionItems = (record: WarehouseProductListItem): NonNullable<MenuProps['items']> => {
        const toggling = togglingProductCodes.includes(record.productCode);
        const items: NonNullable<MenuProps['items']> = [];
        if (access.canManageWarehouseProducts) {
            items.push({ key: 'changeHistory', icon: <HistoryOutlined />, label: t('warehouse.changeHistory.action', '修改记录') });
        }
        if (access.canManageWarehouseProducts && (access.canViewContainers || access.canViewProductSalesAnalysis)) {
            items.push({ key: 'records', icon: <SearchOutlined />, label: t('warehouseProductRecords.entry', '数据查询') });
        }
        items.push(canManageProductDetails(record.productType)
            ? { key: 'productDetails', icon: <GiftOutlined />, label: getProductDetailsActionLabel(record.productType, t) }
            : {
                key: 'productDetails',
                icon: <GiftOutlined />,
                disabled: true,
                label: (<Tooltip title={getProductDetailsDisabledHint(t)} placement="left">
                  <span>{getProductDetailsActionLabel(ProductType.SET, t)}</span>
                </Tooltip>),
            });
        if (access.canWriteProduct) {
            items.push({ type: 'divider' });
            // 下架仍先弹供货说明（handleToggleSingleActive 内拦截），上架直接生效，与原行内开关一致。
            items.push(record.isActive
                ? { key: 'deactivate', disabled: toggling, label: t('warehouseUi.products.deactivate') }
                : { key: 'activate', disabled: toggling, label: t('warehouseUi.products.activate') });
        }
        return items;
    };
    const handleRowAction = (record: WarehouseProductListItem, key: string) => {
        if (key === 'changeHistory') {
            setChangeHistoryProduct(record);
        }
        else if (key === 'records') {
            navigate(`/warehouse/products/${encodeURIComponent(record.productCode)}/records`);
        }
        else if (key === 'productDetails' && canManageProductDetails(record.productType)) {
            void handleOpenSetItems(record);
        }
        else if (key === 'activate' || key === 'deactivate') {
            void handleToggleSingleActive(record, key === 'activate');
        }
    };
    const baseColumns = useMemo<ColumnsType<WarehouseProductListItem>>(() => [
        {
            key: 'product',
            title: t('warehouseUi.products.columnProduct'),
            // 商品组合列沿用原「HB货号」列的排序与列头筛选：按货号排序、按货号筛选。
            dataIndex: 'itemNumber',
            width: 248,
            fixed: 'left',
            sorter: true,
            ...textFilterProps('itemNumber', t('warehouse.searchProductByItemNumber', '按货号筛选')),
            render: (_value, record) => renderProductCell(record),
        },
        {
            key: 'nameEn',
            title: t('column.englishName'),
            dataIndex: 'nameEn',
            width: 176,
            ...textFilterProps('nameEn', t('warehouse.searchProductByEnglishName', '按英文名筛选')),
            render: (value: string | undefined) => value ? <div className="warehouse-products-text-2line">{value}</div> : '--',
        },
        {
            key: 'name',
            title: t('column.productName'),
            dataIndex: 'name',
            width: 176,
            sorter: true,
            ...textFilterProps('productName', t('warehouse.searchProductByName', '按商品名筛选')),
            render: (value: string | undefined) => value ? <div className="warehouse-products-text-2line">{value}</div> : '--',
        },
        {
            key: 'barcode',
            title: t('column.barcode'),
            dataIndex: 'barcode',
            width: 156,
            ...textFilterProps('barcode', t('warehouse.searchProductByBarcode', '按条码筛选')),
            render: (value: string | undefined) => value ? (<div className="warehouse-products-barcode-cell">
              <BarcodePreview value={value} textMaxWidth={150} compactCopy/>
            </div>) : ('--'),
        },
        {
            key: 'categoryName',
            title: t('column.category'),
            dataIndex: 'categoryName',
            width: 128,
            filters: categoryColumnFilterOptions,
            filterIcon,
            filtered: categoryFilterValue !== ALL_PRODUCTS_FILTER_KEY,
            filteredValue: categoryFilterValue === ALL_PRODUCTS_FILTER_KEY ? null : [categoryFilterValue],
            filterMultiple: false,
            render: (_value, record) => renderWarehouseProductCategoryCell(record, categoryLookup, i18n.language),
        },
        {
            key: 'productType',
            title: t('column.productType'),
            dataIndex: 'productType',
            width: 104,
            ...enumFilterProps('productType', productTypeOptions.map((option) => ({
                text: String(option.label),
                value: String(option.value),
            }))),
            render: (value: ProductType) => <Tag color={getProductTypeTagColor(value)}>{getProductTypeLabel(value, t)}</Tag>,
        },
        {
            key: 'supplier',
            title: t('column.supplier'),
            // 供应商组合列：第一行国内、第二行澳洲；排序与列头筛选沿用原「国内供应商」列。
            dataIndex: 'domesticSupplierCode',
            width: 108,
            sorter: true,
            ...enumFilterProps('domesticSupplierCode', domesticSupplierFilterOptions),
            render: (_value, record) => {
                const supplierDisplayName = record.domesticSupplierName || record.domesticSupplierCode;
                const localSupplierDisplayName = record.localSupplierName || localSupplierNameMap[record.localSupplierCode || ''] || record.localSupplierCode;
                return (<div className="warehouse-products-supplier-lines">
                  <div className="warehouse-products-supplier-line" title={supplierDisplayName || undefined}>
                    <span className="warehouse-products-supplier-label">{t('warehouse.domestic')}</span>{supplierDisplayName || '—'}
                  </div>
                  <div className="warehouse-products-supplier-line" title={localSupplierDisplayName || undefined}>
                    <span className="warehouse-products-supplier-label">{t('warehouseUi.products.supplierAustralia')}</span>{localSupplierDisplayName || '—'}
                  </div>
                </div>);
            },
        },
        {
            key: 'localSupplierCode',
            title: t('column.australianSupplier', '澳洲供应商'),
            dataIndex: 'localSupplierCode',
            width: 140,
            sorter: true,
            ...enumFilterProps('localSupplierCode', localSupplierFilterOptions),
            render: (_value, record) => {
                // 表格接口只返回澳洲供应商代码时，用活跃供应商列表补齐名称。
                const supplierDisplayName = record.localSupplierName || localSupplierNameMap[record.localSupplierCode || ''] || record.localSupplierCode;
                return supplierDisplayName ? (<div className="warehouse-products-ellipsis" title={supplierDisplayName}>{supplierDisplayName}</div>) : ('--');
            },
        },
        {
            key: 'domesticPrice',
            title: `${t('column.domesticPrice')} ¥`,
            dataIndex: 'domesticPrice',
            width: 80,
            align: 'right',
            className: 'warehouse-products-numeric-column',
            ...numberRangeFilterProps('domesticPrice'),
            render: (_value, record) => renderInlineEditableNumberCell(record, 'domesticPrice'),
        },
        {
            key: 'importPrice',
            title: `${t('column.importPrice')} $`,
            dataIndex: 'importPrice',
            width: 88,
            align: 'right',
            className: 'warehouse-products-numeric-column',
            sorter: true,
            ...numberRangeFilterProps('importPrice'),
            render: (_value, record) => renderInlineEditableNumberCell(record, 'importPrice'),
        },
        {
            key: 'labelPrice',
            title: `${t('column.oemPrice')} $`,
            dataIndex: 'labelPrice',
            width: 88,
            align: 'right',
            className: 'warehouse-products-numeric-column',
            sorter: true,
            ...numberRangeFilterProps('oemPrice'),
            render: (_value, record) => renderInlineEditableNumberCell(record, 'labelPrice'),
        },
        {
            key: 'minOrderQuantity',
            title: t('warehouse.middlePackQuantity', '中包数'),
            dataIndex: 'minOrderQuantity',
            width: 96,
            align: 'right',
            className: 'warehouse-products-numeric-column',
            ...numberRangeFilterProps('minOrderQuantity'),
            render: (_value, record) => renderInlineEditableNumberCell(record, 'minOrderQuantity'),
        },
        {
            key: 'packingQty',
            title: t('column.packingQuantity'),
            dataIndex: 'packingQty',
            width: 108,
            ...numberRangeFilterProps('packingQty'),
            render: (value: number | undefined, record) => value !== undefined && value !== null ? (<Space size={4}>
              <span className="warehouse-products-numeric">{value}</span>
              {/* 装箱数优先显示国内商品值；仅在国内值缺失时回退到仓库值。 */}
              {record.isPackingQtyFallback ? <Tag color="green">{t('warehouse.warehouse')}</Tag> : <Tag color="gold">{t('warehouse.domestic')}</Tag>}
            </Space>) : ('--'),
        },
        {
            key: 'volume',
            title: t('column.volume'),
            dataIndex: 'volume',
            width: 108,
            ...numberRangeFilterProps('volume'),
            render: (value: number | undefined, record) => value !== undefined && value !== null ? (<Space size={4}>
              <span className="warehouse-products-numeric">{value}</span>
              {record.isVolumeFallback ? <Tag color="gold">{t('warehouse.domestic')}</Tag> : <Tag color="green">{t('warehouse.warehouse')}</Tag>}
            </Space>) : ('--'),
        },
        {
            key: 'locationCodes',
            title: t('location.location', '货位'),
            dataIndex: 'locationCodes',
            width: 96,
            ...textFilterProps('locationCodes', t('warehouse.filterLocation', '按货位筛选')),
            render: (value: string[] | undefined) => value?.length ? (<div className="warehouse-products-location-list" title={value.join(', ')}>
              {value.map((locationCode) => <span key={locationCode} className="warehouse-products-location-tag">{locationCode}</span>)}
            </div>) : (<span className="warehouse-products-location-empty">{t('warehouseUi.products.locationUnbound')}</span>),
        },
        {
            key: 'isActive',
            title: t('column.status'),
            dataIndex: 'isActive',
            width: 104,
            ...enumFilterProps('isActive', [
                { text: getShelfStatusLabel(true, t), value: 'true' },
                { text: getShelfStatusLabel(false, t), value: 'false' },
            ]),
            // 状态只读展示：上架绿色、下架灰色；上下架改在勾选条与 ⋯ 菜单里操作，下架仍需先填供货说明。
            render: (value: boolean, record) => (<div className="warehouse-products-status-cell">
              <StatusPill tone={value ? 'green' : 'gray'}>{getShelfStatusLabel(value, t)}</StatusPill>
              {value ? null : renderSupplyNoticeSummary(record)}
            </div>),
        },
        {
            key: 'supplyUpdatedAt',
            title: t('supplyNotice.column.updatedAt'),
            width: 104,
            render: (_value, record) => {
                const notice = supplyNotices[record.productCode];
                return record.isActive || !notice ? null : dayjs(notice.updatedAtUtc).format('YYYY-MM-DD');
            },
        },
        {
            key: 'updatedAt',
            title: t('column.updateTime'),
            dataIndex: 'updatedAt',
            width: 156,
            sorter: true,
            ...dateRangeFilterProps('updatedAt'),
            render: (value: string | undefined) => formatDateTime(value, i18n.language),
        },
        {
            key: 'updatedBy',
            title: t('column.updater'),
            dataIndex: 'updatedBy',
            width: 96,
            render: (value: string | undefined) => value || '--',
        },
        {
            key: 'action',
            title: t('column.action'),
            width: 60,
            fixed: 'right',
            align: 'right',
            render: (_, record) => {
                const actionItems = buildRowActionItems(record);
                return (<div className="warehouse-products-row-actions">
                  {access.canWriteProduct ? (<Tooltip title={t('common.edit')}>
                    <Button type="text" size="small" icon={<EditOutlined />} aria-label={t('warehouseUi.products.rowEditAria', { code: record.itemNumber || record.productCode })} onClick={() => handleOpenEdit(record)}/>
                  </Tooltip>) : null}
                  {actionItems.length ? (<Dropdown trigger={['click']} placement="bottomRight" menu={{ items: actionItems, onClick: ({ key }) => handleRowAction(record, String(key)) }}>
                    <Button type="text" size="small" icon={<MoreOutlined />} aria-label={t('warehouseUi.products.rowMoreAria', { code: record.itemNumber || record.productCode })}/>
                  </Dropdown>) : null}
                </div>);
            },
        },
    ], [access.canManageWarehouseProducts, access.canViewContainers, access.canViewProductSalesAnalysis, access.canWriteProduct, canInlineEditWarehouseProduct, categoryColumnFilterOptions, categoryFilterValue, categoryLookup, columnFilters, domesticSupplierFilterOptions, i18n.language, inlineEditingCell, inlineSavingCellKey, localSupplierFilterOptions, localSupplierNameMap, navigate, productTypeOptions, supplyNotices, t, togglingProductCodes]);
    const draggableColumnKeys = [...WAREHOUSE_PRODUCT_DEFAULT_COLUMN_ORDER];
    useEffect(() => {
        setColumnOrder((current) => {
            let savedOrder: unknown[] | null = null;
            if (!current.length && typeof window !== 'undefined') {
                try {
                    const raw = localStorage.getItem(WAREHOUSE_PRODUCT_COLUMN_ORDER_STORAGE_KEY);
                    savedOrder = raw ? JSON.parse(raw) : null;
                }
                catch {
                    savedOrder = null;
                }
            }
            // 列顺序只管理业务列；选择列继续由 rowSelection 管理，新增/删除列在这里自动兼容。
            const nextOrder = mergeWarehouseProductColumnOrder(current.length ? current : savedOrder, WAREHOUSE_PRODUCT_DEFAULT_COLUMN_ORDER);
            if (current.length === nextOrder.length && current.every((key, index) => key === nextOrder[index])) {
                return current;
            }
            return nextOrder;
        });
    }, [draggableColumnKeys.join('|')]);
    const handleColumnDragEnd = ({ active, over }: DragEndEvent) => {
        if (!over || active.id === over.id)
            return;
        setColumnOrder((current) => {
            const nextOrder = moveWarehouseProductColumnOrder(current, active.id, over.id);
            try {
                localStorage.setItem(WAREHOUSE_PRODUCT_COLUMN_ORDER_STORAGE_KEY, JSON.stringify(nextOrder));
            }
            catch {
                // localStorage 不可用时不影响当前页面内拖拽排序。
            }
            return nextOrder;
        });
    };
    const isColumnOrderCustomized = isWarehouseProductColumnOrderCustomized(columnOrder, WAREHOUSE_PRODUCT_DEFAULT_COLUMN_ORDER);
    const isColumnVisibilityCustomized = isWarehouseProductColumnVisibilityCustomized(visibleOptionalColumns, WAREHOUSE_PRODUCT_DEFAULT_VISIBLE_OPTIONAL_COLUMNS);
    // 列设置：勾选显示低频列并记住选择；列头筛选条件不随列隐藏而清除（见表格 onChange）。
    const handleVisibleOptionalColumnsChange = (keys: string[]) => {
        const nextKeys = normalizeWarehouseProductVisibleOptionalColumns(keys, WAREHOUSE_PRODUCT_OPTIONAL_COLUMN_KEYS);
        setVisibleOptionalColumns(nextKeys);
        try {
            localStorage.setItem(WAREHOUSE_PRODUCT_VISIBLE_COLUMNS_STORAGE_KEY, JSON.stringify(nextKeys));
        }
        catch {
            // localStorage 不可用时只影响跨刷新记忆，当前页面内仍按勾选显示。
        }
    };
    const handleResetColumnOrder = () => {
        try {
            localStorage.removeItem(WAREHOUSE_PRODUCT_COLUMN_ORDER_STORAGE_KEY);
            localStorage.removeItem(WAREHOUSE_PRODUCT_VISIBLE_COLUMNS_STORAGE_KEY);
        }
        catch {
            // localStorage 不可用时仍恢复当前页面内的默认列顺序。
        }
        // 重置只影响业务列顺序与显示列；选择列仍由 Ant Design rowSelection 管理。
        setColumnOrder([...WAREHOUSE_PRODUCT_DEFAULT_COLUMN_ORDER]);
        setVisibleOptionalColumns([...WAREHOUSE_PRODUCT_DEFAULT_VISIBLE_OPTIONAL_COLUMNS]);
    };
    // 实际渲染的列顺序：完整列顺序去掉未勾选的低频列；拖拽排序也只在这些可见列之间进行。
    const visibleColumnOrder = useMemo(
        () => filterWarehouseProductVisibleColumnOrder(columnOrder.length ? columnOrder : draggableColumnKeys, WAREHOUSE_PRODUCT_OPTIONAL_COLUMN_KEYS, visibleOptionalColumns),
        [columnOrder, draggableColumnKeys.join('|'), visibleOptionalColumns],
    );
    const columnSettingsOptions = useMemo(() => {
        const columnMap = new Map(baseColumns.map((column) => [String(column.key), column]));
        return WAREHOUSE_PRODUCT_OPTIONAL_COLUMN_KEYS.map((key) => ({ key, label: columnMap.get(key)?.title as ReactNode }));
    }, [baseColumns]);
    const orderedColumns = useMemo(() => {
        const activeOrder = visibleColumnOrder;
        const columnMap = new Map(baseColumns.map((column) => [String(column.key), column]));
        return activeOrder
            .map((key) => columnMap.get(key))
            .filter((column): column is ColumnsType<WarehouseProductListItem>[number] => Boolean(column))
            .map((column) => ({
            ...column,
            onHeaderCell: () => ({
                'data-column-key': String(column.key),
            }),
        })) as ColumnsType<WarehouseProductListItem>;
    }, [baseColumns, visibleColumnOrder]);
    // 虚拟表格需要数值 scroll.x：取可见列宽之和。容器更宽时 rc-table 会按比例放大各列，更窄时才出现横向滚动。
    const tableScrollX = useMemo(() => orderedColumns.reduce(
        (sum, column) => sum + (typeof column.width === 'number' ? column.width : 0),
        WAREHOUSE_PRODUCT_SELECTION_COLUMN_WIDTH,
    ), [orderedColumns]);
    const clearSearchDebounce = () => {
        if (searchDebounceTimerRef.current !== null) {
            window.clearTimeout(searchDebounceTimerRef.current);
            searchDebounceTimerRef.current = null;
        }
    };
    // 回车立即提交关键词，并取消尚未触发的防抖查询，避免同一关键词请求两次；下拉类筛选在各自 onChange 里立即请求。
    const handleSubmitSearch = () => {
        clearSearchDebounce();
        setSubmittedSearchText(searchText);
        void loadData({ page: 1, searchText });
    };
    // 输入即查：关键词防抖 300ms 后提交。定时器触发时经 loadDataRef 取最新 loader，
    // 这期间若又改了其他筛选，也按最新条件查询，而不是输入那一刻的旧条件。
    const handleSearchTextChange = (value: string) => {
        setSearchText(value);
        clearSearchDebounce();
        searchDebounceTimerRef.current = window.setTimeout(() => {
            searchDebounceTimerRef.current = null;
            if (!isMountedRef.current) {
                return;
            }
            setSubmittedSearchText(value);
            void loadDataRef.current?.({ page: 1, searchText: value });
        }, WAREHOUSE_PRODUCTS_SEARCH_DEBOUNCE_MS);
    };
    // 重置筛选：清空顶部栏与列头条件并恢复默认排序，不动列顺序。「清空全部」与之等价。
    const handleResetFilters = () => {
        clearSearchDebounce();
        setSearchText('');
        setSubmittedSearchText('');
        setSupplierCode(undefined);
        setCategoryFilterValue(ALL_PRODUCTS_FILTER_KEY);
        setProductType(undefined);
        setIsActive(undefined);
        setColumnFilters({});
        setSortField('createdAt');
        setSortOrder('descend');
        void loadData({
            page: 1,
            searchText: '',
            supplierCode: undefined,
            filters: {},
            categoryGuid: undefined,
            uncategorizedOnly: false,
            productType: undefined,
            isActive: undefined,
            sortField: 'createdAt',
            sortOrder: 'descend',
        });
    };
    // 左侧分类面板选中即查第 1 页：「全部商品」「未分类」是面板顶部的固定入口，替代原「只看未分类」开关。
    const handleCategoryFilterChange = (value: string) => {
        setCategoryFilterValue(value);
        void loadData({ page: 1, ...buildCategoryQueryValue(value) });
    };
    const statusTabKey = resolveWarehouseProductStatusTab(isActive);
    // 状态页签即状态筛选：与列头「状态」筛选共用 isActive 并镜像进 columnFilters，选中即查第 1 页。
    const handleStatusTabChange = (key: WarehouseProductStatusTabKey) => {
        const value = warehouseProductStatusTabToIsActive(key);
        const nextFilters = setFilterValues(columnFilters, 'isActive', value === undefined ? undefined : [String(value)]);
        setIsActive(value);
        setColumnFilters(nextFilters);
        void loadData({ page: 1, isActive: value, filters: nextFilters });
    };
    // 计数缺失（加载中、失败）时不传 count，页签只显示文字。
    const statusTabItems = WAREHOUSE_PRODUCT_STATUS_TAB_KEYS.map((key) => ({
        key,
        label: key === 'all' ? t('common.all', '全部') : getShelfStatusLabel(key === 'active', t),
        count: statusCounts?.counts[key],
    }));
    // 列头筛选键 → 标签名与取值格式，键名与 normalizeTableFilters 输出一致（name→productName、labelPrice→oemPrice）。
    const activeFilterColumns = useMemo<Record<string, ActiveFilterColumnMeta>>(() => ({
        itemNumber: { label: t('column.hbItemNumber'), kind: 'text' },
        domesticSupplierCode: { label: t('warehouse.domesticSupplier', '国内供应商'), kind: 'enum', options: domesticSupplierFilterOptions },
        nameEn: { label: t('column.englishName'), kind: 'text' },
        minOrderQuantity: { label: t('warehouse.middlePackQuantity', '中包数'), kind: 'comparable' },
        domesticPrice: { label: t('column.domesticPrice'), kind: 'comparable' },
        importPrice: { label: t('column.importPrice'), kind: 'comparable' },
        oemPrice: { label: t('column.oemPrice'), kind: 'comparable' },
        isActive: {
            label: t('column.status'),
            kind: 'enum',
            options: [
                { text: getShelfStatusLabel(true, t), value: 'true' },
                { text: getShelfStatusLabel(false, t), value: 'false' },
            ],
        },
        productType: {
            label: t('column.productType'),
            kind: 'enum',
            options: productTypeOptions.map((option) => ({ text: String(option.label), value: String(option.value) })),
        },
        barcode: { label: t('column.barcode'), kind: 'text' },
        locationCodes: { label: t('location.location', '货位'), kind: 'text' },
        productName: { label: t('column.productName'), kind: 'text' },
        packingQty: { label: t('column.packingQuantity'), kind: 'comparable' },
        volume: { label: t('column.volume'), kind: 'comparable' },
        localSupplierCode: { label: t('column.australianSupplier', '澳洲供应商'), kind: 'enum', options: localSupplierFilterOptions },
        updatedAt: { label: t('column.updateTime'), kind: 'comparable' },
    }), [domesticSupplierFilterOptions, localSupplierFilterOptions, productTypeOptions, t]);
    const activeFilterChips = useMemo(() => buildActiveFilterChips({
        query: appliedQuery,
        labels: {
            searchText: t('warehouse.activeFilter.keyword', '关键词'),
            category: t('warehouse.categories.category', '分类'),
            uncategorized: t('warehouseUi.products.uncategorized'),
        },
        columns: activeFilterColumns,
        categoryLabel: appliedQuery?.categoryGuid
            ? getWarehouseProductCategoryTooltip({ warehouseCategoryGUID: appliedQuery.categoryGuid, categoryName: findWarehouseCategory(categories, appliedQuery.categoryGuid)?.categoryName }, categoryLookup, i18n.language)
            : undefined,
        textModeLabels: {
            contains: t('warehouse.filterMode.contains', '包含'),
            eq: t('warehouse.filterMode.equals', '等于'),
            starts: t('warehouse.filterMode.startsWith', '开头是'),
            ends: t('warehouse.filterMode.endsWith', '结尾是'),
        },
    }), [activeFilterColumns, appliedQuery, categories, categoryLookup, i18n.language, t]);
    // 移除单个已生效条件：同步清掉对应界面 state，并立即按剩余条件请求第 1 页。
    const handleRemoveActiveFilter = (chipKey: string) => {
        const overrides = buildActiveFilterRemovalOverrides(chipKey, columnFilters);
        if (chipKey === ACTIVE_FILTER_SEARCH_KEY) {
            clearSearchDebounce();
            setSearchText('');
            setSubmittedSearchText('');
        }
        else if (chipKey === ACTIVE_FILTER_CATEGORY_KEY) {
            setCategoryFilterValue(ALL_PRODUCTS_FILTER_KEY);
        }
        else if (chipKey === 'domesticSupplierCode') {
            setSupplierCode(undefined);
        }
        else if (chipKey === 'productType') {
            setProductType(undefined);
        }
        else if (chipKey === 'isActive') {
            setIsActive(undefined);
        }
        setColumnFilters(overrides.filters);
        void loadData(overrides);
    };
    return (<>
      <PageContainer compact title={t('menu.warehouseProducts')} subtitle={t('warehouseUi.products.totalCount', { total: formatWarehouseProductCount(total) })} extra={<Space wrap>
          {exporting ? (<Typography.Text type="secondary">
              {exportMessage} ({exportProgress}%)
            </Typography.Text>) : null}
          {/* 页头只放低频入口：价格与同步、导入导出收进菜单；批量操作在勾选后操作条，管理分类在左侧分类面板头部。
              「从HQ同步库存」（HQ → HBweb）已于 2026-09-29 停用，菜单只保留写 HQ 的「更新分店价格」。 */}
          <ToolbarMenuButton label={t('warehouseUi.products.priceAndSync')} actions={[
                {
                    key: 'retailPriceChanges',
                    label: t('warehouse.retailPriceChanges.entry'),
                    icon: <HistoryOutlined />,
                    visible: access.canManageWarehouseProducts,
                    onClick: () => navigate('/warehouse/products/retail-price-changes'),
                },
                {
                    key: 'priceUpdateTasks',
                    label: t('warehouse.priceUpdateTasks.entry'),
                    icon: <TagsOutlined />,
                    visible: access.canManageWarehouseProducts,
                    onClick: () => navigate('/warehouse/products/price-update-tasks'),
                },
                {
                    key: 'storePriceSync',
                    label: t('warehouse.storePriceSync.title', '更新分店价格'),
                    icon: <CloudSyncOutlined />,
                    visible: canManageWarehouseStorePriceSync,
                    disabled: storePriceSyncOpen,
                    onClick: () => setStorePriceSyncOpen(true),
                },
            ]}/>
          <ToolbarMenuButton label={t('common.listToolbar.importExport', '导入 / 导出')} icon={exporting ? <LoadingOutlined /> : undefined} actions={[
                {
                    key: 'exportExcel',
                    label: t('warehouse.exportExcel'),
                    icon: <DownloadOutlined />,
                    disabled: exporting,
                    onClick: () => setExportConfigOpen(true),
                },
                {
                    key: 'importFromDomestic',
                    label: t('warehouse.importFromDomestic'),
                    icon: <UploadOutlined />,
                    onClick: () => setImportFromDomesticOpen(true),
                },
                {
                    key: 'importNonHb',
                    label: t('warehouse.importNonHb.title'),
                    icon: <UploadOutlined />,
                    visible: canImportNonHbProducts,
                    onClick: () => setImportNonHbOpen(true),
                },
            ]}/>
          {access.canWriteProduct ? (<Button type="primary" icon={<PlusOutlined />} onClick={handleOpenCreate}>
              {t('warehouse.createProduct')}
            </Button>) : null}
          </Space>}>
        <div className="warehouse-products-layout">
          <CategoryFilterPanel
            categories={categories}
            loading={categoryLoading}
            value={categoryFilterValue}
            expandedKeys={categoryFilterExpandedKeys}
            onExpand={setCategoryFilterExpandedKeys}
            onChange={handleCategoryFilterChange}
            onManage={access.canManageWarehouseCategories ? () => setCategoryManageOpen(true) : undefined}
            language={i18n.language}
            t={t}
          />
          <section className="warehouse-products-list" aria-label={t('menu.warehouseProducts')}>
          <StatusTabs items={statusTabItems} activeKey={statusTabKey} onChange={handleStatusTabChange} ariaLabel={t('warehouseUi.products.statusTabsLabel')}/>
          <div className="warehouse-products-toolbar">
          <div className="list-toolbar-filter-row">
          <Input className="warehouse-products-search" value={searchText} onChange={(event) => handleSearchTextChange(event.target.value)} onPressEnter={handleSubmitSearch} prefix={<SearchOutlined />} placeholder={t('warehouse.searchProductFull')} aria-label={t('warehouse.searchProductFull')} allowClear/>
          {/* 下拉选完即查：用 loadData 覆盖参数立即请求第 1 页，不依赖 setState 之后才生效的值。 */}
          <Select value={supplierCode} onChange={(value) => {
            const nextFilters = setFilterValues(columnFilters, 'domesticSupplierCode', value ? [value] : undefined);
            setSupplierCode(value);
            setColumnFilters(nextFilters);
            void loadData({ page: 1, supplierCode: value, filters: nextFilters });
        }} options={buildSupplierOptions(suppliers)} prefix={<span className="warehouse-products-select-prefix">{t('warehouse.domesticSupplier', '国内供应商')}</span>} placeholder={t('common.all', '全部')} style={{ width: 220 }} popupMatchSelectWidth={320} showSearch filterOption={filterSupplierOption} allowClear/>
          {/* 窄屏（列表容器 < 1080px）收起左侧分类面板，改用这里的分类树下拉；宽屏时由 CSS 隐藏。 */}
          <span className="warehouse-products-category-select">
          <TreeSelect
            value={categoryFilterValue}
            onChange={(value) => {
              const nextCategoryFilterValue = value || ALL_PRODUCTS_FILTER_KEY;
              setCategoryFilterValue(value || ALL_PRODUCTS_FILTER_KEY);
              setCategoryFilterSearchText('');
              void loadData({ page: 1, ...buildCategoryQueryValue(nextCategoryFilterValue) });
            }}
            treeData={categoryFilterTreeOptions}
            placeholder={t('warehouse.categories.category', '分类')}
            style={{ width: 220 }}
            showSearch
            searchValue={categoryFilterSearchText}
            onSearch={setCategoryFilterSearchText}
            treeNodeFilterProp="searchText"
            treeExpandedKeys={hasCategoryFilterSearchText ? undefined : categoryFilterExpandedKeys}
            onTreeExpand={(keys) => {
              // 搜索时交给 TreeSelect 自动展开命中路径；非搜索时保留用户手动展开状态。
              if (!hasCategoryFilterSearchText) {
                setCategoryFilterExpandedKeys(keys.map(String));
              }
            }}
            popupMatchSelectWidth={320}
            listHeight={360}
            allowClear
            notFoundContent={categoryLoading ? t('common.loading', '加载中') : t('warehouse.categories.noCategoryData', '暂无分类数据')}
          />
          </span>
          <Select value={productType} onChange={(value) => {
            const nextFilters = setFilterValues(columnFilters, 'productType', value === undefined ? undefined : [String(value)]);
            setProductType(value);
            setColumnFilters(nextFilters);
            void loadData({ page: 1, productType: value, filters: nextFilters });
        }} options={productTypeOptions} prefix={<span className="warehouse-products-select-prefix">{t('warehouseUi.products.typePrefix')}</span>} placeholder={t('common.all', '全部')} style={{ width: 150 }} popupMatchSelectWidth={160} allowClear/>
          {/* 低频的澳洲供应商收进「更多筛选」，角标为弹层内生效条件数；与澳洲供应商列头筛选共用同一条件。 */}
          <MoreFiltersButton activeCount={columnFilters.localSupplierCode?.length ? 1 : 0}>
            <div className="warehouse-products-more-field">
              <span className="warehouse-products-more-field-label">{t('column.australianSupplier', '澳洲供应商')}</span>
              <Select mode="multiple" value={columnFilters.localSupplierCode ?? []} onChange={(values: string[]) => {
                const nextFilters = setFilterValues(columnFilters, 'localSupplierCode', values);
                setColumnFilters(nextFilters);
                void loadData({ page: 1, filters: nextFilters });
            }} options={localSupplierFilterOptions.map((option) => ({ value: option.value, label: option.text }))} placeholder={t('warehouseUi.products.allAustralianSuppliers')} optionFilterProp="label" maxTagCount="responsive" allowClear/>
            </div>
          </MoreFiltersButton>
          <span className="list-toolbar-filter-spacer"/>
          <ColumnSettingsButton options={columnSettingsOptions} visibleKeys={visibleOptionalColumns} onChange={handleVisibleOptionalColumnsChange} canReset={isColumnOrderCustomized || isColumnVisibilityCustomized} onReset={handleResetColumnOrder} t={t}/>
          </div>
          {/* 没有生效条件时整行隐藏，不再占一行显示空提示（与其他仓库列表页一致）。 */}
          {activeFilterChips.length ? (<ActiveFilterBar items={activeFilterChips.map((chip) => ({
                ...chip,
                onRemove: () => handleRemoveActiveFilter(chip.key),
            }))} onClearAll={handleResetFilters}/>) : null}
          <SupplyNoticeModal open={Boolean(supplyNoticeTarget)} mode={supplyNoticeTarget?.mode ?? 'delist'} productCount={supplyNoticeTarget?.productCodes.length ?? 0} initialNotice={supplyNoticeTarget?.initial} confirmLoading={supplyNoticeSaving} onCancel={() => setSupplyNoticeTarget(null)} onSubmit={handleSupplyNoticeSubmit}/>
          {/* 勾选后操作条：只对选中行生效的批量操作，未勾选时整条隐藏。 */}
          <SelectionActionBar selectedCount={selectedRowKeys.length} onClearSelection={() => setSelectedRowKeys([])}>
            {access.canWriteProduct ? (<Button size="small" loading={batchEditSaving || Boolean(activeBatchUpdateJob)} disabled={!selectedRowKeys.length || batchEditSaving} onClick={openBatchEdit}>
                {t('warehouse.batchEdit', '批量修改')}
              </Button>) : null}
            {access.canWriteProduct ? (<Button size="small" loading={batchCategorySaving} disabled={!selectedRowKeys.length || batchCategorySaving} onClick={openBatchCategory}>
                {t('warehouseUi.products.selectionSetCategory')}
              </Button>) : null}
            {access.canWriteProduct ? (<Popconfirm title={t('warehouse.confirmBatchActivate')} okText={getShelfStatusLabel(true, t)} cancelText={t('common.cancel')} disabled={!selectedRowKeys.length} onConfirm={() => void handleBatchToggleActive(true)}>
                <Button size="small" loading={batchActionLoading} disabled={!selectedRowKeys.length || batchActionLoading}>
                  {t('warehouseUi.products.activate')}
                </Button>
              </Popconfirm>) : null}
            {/* 批量下架不用 Popconfirm：先弹供货说明（后续计划必选），说明弹窗本身就是确认步骤。 */}
            {access.canWriteProduct ? (<Button size="small" loading={batchActionLoading} disabled={!selectedRowKeys.length || batchActionLoading} onClick={() => void handleBatchToggleActive(false)}>
                {t('warehouseUi.products.deactivate')}
              </Button>) : null}
            {access.canManagePosProducts ? (<Button size="small" loading={pushToHqLoading} disabled={!selectedRowKeys.length || pushToHqLoading || pushToHqModalOpen} onClick={() => void handlePushToHq()}>
                {t('warehouseUi.products.pushToHq')}
              </Button>) : null}
          </SelectionActionBar>
          </div>
          <DndContext sensors={columnDragSensors} collisionDetection={closestCenter} onDragEnd={handleColumnDragEnd}>
            <SortableContext items={visibleColumnOrder} strategy={horizontalListSortingStrategy}>
              <MeasuredTable metricId="warehouse.products.table-2" className="warehouse-products-table" rowKey="productCode" virtual loading={loading} components={{ header: { cell: DraggableHeaderCell } }} columns={orderedColumns} dataSource={data} rowSelection={{
                fixed: true,
                columnWidth: WAREHOUSE_PRODUCT_SELECTION_COLUMN_WIDTH,
                selectedRowKeys,
                onChange: setSelectedRowKeys,
            }} scroll={{ x: tableScrollX, y: 620 }} pagination={{
                current: page,
                pageSize,
                total,
                showSizeChanger: true,
                pageSizeOptions: WAREHOUSE_PRODUCTS_PAGE_SIZE_OPTIONS,
                showTotal: (count: number) => t('warehouseUi.products.totalCount', { total: formatWarehouseProductCount(count) }),
            }} onChange={(pagination: TablePaginationConfig, filters: Record<string, FilterValue | null>, sorter: SorterResult<WarehouseProductListItem> | SorterResult<WarehouseProductListItem>[], extra) => {
                const nextSorter = Array.isArray(sorter) ? sorter[0] : sorter;
                const nextSortField = normalizeWarehouseProductSortField(nextSorter?.field, sortField);
                const nextSortOrder = nextSorter?.order === 'ascend' || nextSorter?.order === 'descend'
                    ? nextSorter.order
                    : sortOrder;
                // filters 只含当前渲染的列；被「列设置」隐藏的列，其筛选条件与分类筛选都沿用原值。
                const nextColumnFilters = keepHiddenColumnFilters(normalizeTableFilters(filters), filters, columnFilters);
                const nextCategoryFilterValue = resolveCategoryFilterValueFromTableFilters(filters, categoryFilterValue);
                const nextSupplierCode = getSingleFilterValue(nextColumnFilters.domesticSupplierCode);
                const nextProductType = getSingleFilterValue(nextColumnFilters.productType);
                const nextIsActive = getSingleFilterValue(nextColumnFilters.isActive);
                // 列头筛选统一走后端 filters，分类列头则继续映射成顶层分类字段。
                const categoryQuery = buildCategoryQueryValue(nextCategoryFilterValue);
                setColumnFilters(nextColumnFilters);
                setCategoryFilterValue(nextCategoryFilterValue);
                setSupplierCode(nextSupplierCode);
                setProductType(nextProductType === undefined ? undefined : Number(nextProductType) as ProductType);
                setIsActive(nextIsActive === undefined ? undefined : nextIsActive === 'true');
                setSortField(nextSortField);
                setSortOrder(nextSortOrder);
                void loadData({
                    page: extra.action === 'paginate' ? pagination.current || 1 : 1,
                    pageSize: pagination.pageSize || pageSize,
                    supplierCode: nextSupplierCode,
                    productType: nextProductType === undefined ? undefined : Number(nextProductType) as ProductType,
                    isActive: nextIsActive === undefined ? undefined : nextIsActive === 'true',
                    sortField: nextSortField,
                    sortOrder: nextSortOrder,
                    filters: nextColumnFilters,
                    ...categoryQuery,
                });
            }}/>
            </SortableContext>
          </DndContext>
          </section>
        </div>

      <ProductFormModal open={modalOpen} saving={saving} editingItem={editingItem} suppliers={suppliers} form={form} suggestedDiscount={editingSuggestedDiscount} onCancel={handleCloseModal} onSubmit={() => void handleSave()}/>

      <CreateProductModal open={createModalOpen} suppliers={suppliers} onCancel={() => setCreateModalOpen(false)} onSuccess={() => {
            setCreateModalOpen(false);
            void refreshCurrentList({ page: 1 });
        }}/>

      <WarehouseProductChangeHistoryDrawer
        open={Boolean(changeHistoryProduct)}
        productCode={changeHistoryProduct?.productCode}
        itemNumber={changeHistoryProduct?.itemNumber}
        productName={changeHistoryProduct?.name}
        onClose={() => setChangeHistoryProduct(null)}
      />

      <SetItemsModal open={setItemsOpen} loading={setItemsLoading} saving={setItemsSaving} product={currentSetProduct} items={setItemsDraft} canEdit={access.canWriteProduct} onCancel={() => {
            setSetItemsOpen(false);
            setCurrentSetProduct(null);
            setSetItemsDraft([]);
            setDeletedSetCodeIds([]);
        }} onAddRow={() => {
            setSetItemsDraft((current) => [
                ...current,
                {
                    id: `new_${Date.now()}_${Math.random()}`,
                    productCode: currentSetProduct?.productCode || '',
                    setItemNumber: '',
                    setBarcode: '',
                    setPurchasePrice: currentSetProduct?.importPrice,
                    setRetailPrice: currentSetProduct?.labelPrice,
                    isActive: true,
                },
            ]);
        }} onRemoveRow={(rowId) => {
            if (!rowId.startsWith('new_')) {
                setDeletedSetCodeIds((current) => [...current, rowId]);
            }
            setSetItemsDraft((current) => current.filter((item) => item.id !== rowId));
        }} onChangeField={(rowId, field, value) => {
            setSetItemsDraft((current) => current.map((item) => (item.id === rowId ? { ...item, [field]: value } : item)));
        }} onSubmit={() => void handleSaveSetItems()}/>

      <Modal title={t('warehouse.batchEditTitle', '批量修改 ({{count}} 个商品)', { count: selectedRowKeys.length })} open={batchEditOpen} width={680} destroyOnHidden okText={t('common.save')} cancelText={t('common.cancel')} confirmLoading={batchEditSaving} onCancel={() => {
            setBatchEditOpen(false);
            batchEditForm.resetFields();
        }} onOk={() => void handleBatchEditSave()}>
        <Form form={batchEditForm} layout="vertical" preserve={false}>
          <Typography.Paragraph type="secondary">
            {t('warehouse.batchEditEmptyHint', '留空不修改')}
          </Typography.Paragraph>
          <Form.Item name="supplierCode" label={t('warehouse.domesticSupplier', '国内供应商')}>
            <Select placeholder={t('warehouse.batchEditKeepEmpty', '留空不修改')} options={buildSupplierOptions(suppliers)} showSearch filterOption={filterSupplierOption} allowClear/>
          </Form.Item>
          <Space size={16} style={{ display: 'flex' }} align="start">
            <Form.Item name="domesticPrice" label={t('domesticProducts.domesticPrice')} style={{ flex: 1 }}>
              <InputNumber min={0} precision={2} style={{ width: '100%' }} placeholder={t('warehouse.batchEditKeepEmpty', '留空不修改')}/>
            </Form.Item>
            <Form.Item name="oemPrice" label={t('productCreation.privateLabelPrice')} style={{ flex: 1 }}>
              <InputNumber min={0} precision={2} style={{ width: '100%' }} placeholder={t('warehouse.batchEditKeepEmpty', '留空不修改')}/>
            </Form.Item>
            <Form.Item name="importPrice" label={t('warehouse.importPrice')} style={{ flex: 1 }}>
              <InputNumber min={0} precision={2} style={{ width: '100%' }} placeholder={t('warehouse.batchEditKeepEmpty', '留空不修改')}/>
            </Form.Item>
          </Space>
          <Space size={16} style={{ display: 'flex' }} align="start">
            <Form.Item name="setSuggestedDiscount" valuePropName="checked" label={t('warehouse.priceNotification.suggestedDiscount')} style={{ flex: 1 }}>
              <Checkbox>{t('warehouse.priceNotification.batchSetSuggestedDiscount')}</Checkbox>
            </Form.Item>
            <Form.Item name="suggestedDiscountPercent" label=" " colon={false} extra={t('warehouse.priceNotification.batchSuggestedDiscountHint')} style={{ flex: 2 }}>
              <InputNumber min={0} max={100} precision={2} suffix="%" disabled={!batchSetSuggestedDiscount} style={{ width: '100%' }} placeholder={t('warehouse.priceNotification.suggestedDiscountPlaceholder')}/>
            </Form.Item>
          </Space>
          <Space size={16} style={{ display: 'flex' }} align="start">
            <Form.Item name="packingQuantity" label={t('warehouse.packingQuantity')} style={{ flex: 1 }}>
              <InputNumber min={0} precision={0} style={{ width: '100%' }} placeholder={t('warehouse.batchEditKeepEmpty', '留空不修改')}/>
            </Form.Item>
            <Form.Item name="minOrderQuantity" label={t('warehouse.middlePackQuantity', '中包数')} style={{ flex: 1 }}>
              <InputNumber min={0} precision={0} style={{ width: '100%' }} placeholder={t('warehouse.batchEditKeepEmpty', '留空不修改')}/>
            </Form.Item>
            <Form.Item name="unitVolume" label={t('warehouse.volume')} style={{ flex: 1 }}>
              <InputNumber min={0} precision={4} style={{ width: '100%' }} placeholder={t('warehouse.batchEditKeepEmpty', '留空不修改')}/>
            </Form.Item>
          </Space>
          <Form.Item name="isActive" label={t('warehouse.isListed')}>
            <Select placeholder={t('warehouse.batchEditKeepEmpty', '留空不修改')} allowClear>
              <Select.Option value={true}>{getShelfStatusLabel(true, t)}</Select.Option>
              <Select.Option value={false}>{getShelfStatusLabel(false, t)}</Select.Option>
            </Select>
          </Form.Item>
          <Form.Item name="generateImageUrls" label={t('warehouse.batchImageGenerate', '按货号生成图片地址')} valuePropName="checked">
            <Switch/>
          </Form.Item>
          {batchGenerateImageUrls ? (<Space direction="vertical" size={8} style={{ width: '100%', marginBottom: 16 }}>
            <Form.Item name="imageBaseUrl" label={t('warehouse.batchImageBaseUrl', '基础图片地址')} style={{ marginBottom: 0 }}>
              <Input placeholder={WAREHOUSE_PRODUCT_IMAGE_DEFAULT_BASE_URL}/>
            </Form.Item>
            <Typography.Text type="secondary">
              {t('warehouse.batchImageExample', '示例：{{url}}', {
                  url: `${normalizeWarehouseProductImageBaseUrl(batchImageBaseUrl) ?? WAREHOUSE_PRODUCT_IMAGE_DEFAULT_BASE_URL}${WAREHOUSE_PRODUCT_IMAGE_EXAMPLE_FILE}`,
              })}
            </Typography.Text>
            <Typography.Text type="warning">
              {t('warehouse.batchImageOverwriteWarning', '开启后将用「{{baseUrl}}货号.jpg」覆盖选中商品现有图片地址。', {
                  baseUrl: normalizeWarehouseProductImageBaseUrl(batchImageBaseUrl) ?? WAREHOUSE_PRODUCT_IMAGE_DEFAULT_BASE_URL,
              })}
            </Typography.Text>
            {access.isAdmin || access.isWarehouseManager ? (<Form.Item name="syncImageToHq" label={t('warehouse.batchImageSyncHq', '同步 HQ 数据库（仅 H商品图片）')} valuePropName="checked" style={{ marginBottom: 0 }}>
              <Switch disabled={!batchGenerateImageUrls}/>
            </Form.Item>) : null}
          </Space>) : null}
        </Form>
      </Modal>

      <Modal title={hqImageSyncFailDetail.localFailedCount > 0 ? t('warehouse.batchUpdateFailDetailTitle', '批量修改失败明细') : t('warehouse.batchImageHqFailTitle', 'HQ 图片同步失败')} open={hqImageSyncFailOpen} footer={null} onCancel={() => setHqImageSyncFailOpen(false)}>
        <Space direction="vertical" size={8} style={{ width: '100%' }}>
          {hqImageSyncFailDetail.errors.map((error, index) => (
            <Typography.Text key={`hq-error-${index}`} type="danger">
              {error}
            </Typography.Text>
          ))}
          {hqImageSyncFailDetail.items.map((item) => (
            <div key={item.productCode}>
              <Typography.Text strong>
                {item.productCode}
              </Typography.Text>
              {item.message ? (<Typography.Text type="danger">
                ：{item.message}
              </Typography.Text>) : null}
            </div>
          ))}
          {!hqImageSyncFailDetail.errors.length && !hqImageSyncFailDetail.items.length ? (
            <Typography.Text type="secondary">
              {t('warehouse.batchImageHqFailNoItems', '无失败明细')}
            </Typography.Text>
          ) : null}
        </Space>
      </Modal>

      <Modal title={t('warehouse.batchCategoryTitle', '批量设置分类 ({{count}} 个商品)', { count: selectedRowKeys.length })} open={batchCategoryOpen} width={640} destroyOnHidden okText={t('common.save')} cancelText={t('common.cancel')} confirmLoading={batchCategorySaving} okButtonProps={{ disabled: !targetCategoryGuid || !categories.length }} onCancel={() => {
            setBatchCategoryOpen(false);
            setTargetCategoryGuid(undefined);
        }} onOk={() => void handleBatchCategorySave()}>
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>
            {t('warehouse.batchCategoryHint', '请选择目标分类，确认后会把已选商品设置到该分类。')}
          </Typography.Paragraph>
          {selectedTargetCategory ? (<Tag color="blue">
              {t('warehouse.categories.targetCategory', '目标分类')}: {selectedTargetCategoryPath || formatWarehouseCategoryNodeName(selectedTargetCategory, i18n.language)}
            </Tag>) : null}
          <CategoryTreePicker categories={categories} selectedKey={targetCategoryGuid} expandedKeys={categoryExpandedKeys} onExpand={setCategoryExpandedKeys} onSelect={setTargetCategoryGuid} language={i18n.language} t={t} maxHeight={420}/>
        </Space>
      </Modal>

      {access.canManageWarehouseCategories ? (<ContainerCategoryManageModal
          open={categoryManageOpen}
          categories={categories}
          language={i18n.language}
          activeTargetCategoryGuid={targetCategoryGuid}
          onCancel={() => setCategoryManageOpen(false)}
          onMutationCommitted={handleCategoryMutationCommitted}
          onCategoriesChanged={handleCategoriesChanged}/>) : null}

      <ImportFromDomesticModal open={importFromDomesticOpen} onCancel={() => setImportFromDomesticOpen(false)} onSuccess={() => void refreshCurrentList({ page: 1 })}/>

      <ImportNonHbModal open={importNonHbOpen} onCancel={() => setImportNonHbOpen(false)} onSuccess={() => void refreshCurrentList({ page: 1 })}/>

      <Modal title={t('warehouse.exportExcel')} open={exportConfigOpen} okText={t('warehouse.startExport')} cancelText={t('common.cancel')} confirmLoading={exporting} onCancel={() => {
            if (!exporting) {
                setExportConfigOpen(false);
            }
        }} onOk={() => void handleExport()}>
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Typography.Text>
            {selectedRowKeys.length
            ? t('warehouse.exportSelected', { count: selectedRowKeys.length })
            : t('warehouse.exportFiltered', { count: total })}
          </Typography.Text>
          <Checkbox checked={includeLabelPrice} onChange={(event) => setIncludeLabelPrice(event.target.checked)}>
            {t('warehouse.includeLabelPrice')}
          </Checkbox>
          <Checkbox checked={includeBarcodeImage} onChange={(event) => setIncludeBarcodeImage(event.target.checked)}>
            {t('warehouse.includeBarcodeImage')}
          </Checkbox>
          <Checkbox checked={includeProductImage} onChange={(event) => setIncludeProductImage(event.target.checked)}>
            {t('warehouse.includeProductImage')}
          </Checkbox>
          {exporting ? (<Typography.Text type="secondary">
              {exportMessage} ({exportProgress}%)
            </Typography.Text>) : null}
        </Space>
      </Modal>

      <Modal title={t('warehouse.exportCompleteFailed', { count: exportFailDetail.length })} open={exportFailDetailOpen} width={700} footer={null} onCancel={() => setExportFailDetailOpen(false)}>
        <Typography.Paragraph type="secondary" style={{ marginBottom: 12 }}>
          {t('warehouse.imageDownloadFailedMsg')}
        </Typography.Paragraph>
        <MeasuredTable metricId="warehouse.products.table-3" size="small" pagination={false} scroll={{ y: 360 }} dataSource={exportFailDetail} rowKey="itemNumber" columns={[
            { title: t('productImport.hbProductNoCol'), dataIndex: 'itemNumber', width: 120 },
            { title: t('warehouse.failureReason'), dataIndex: 'reason', width: 200 },
            {
                title: t('warehouse.imageUrl'),
                dataIndex: 'url',
                ellipsis: true,
                render: (val: string) => (<Tooltip title={val}>
                  <span style={{ fontSize: 12, wordBreak: 'break-all' }}>{val}</span>
                </Tooltip>),
            },
        ]}/>
      </Modal>

      <PosHqPushModal
        open={pushToHqModalOpen}
        selectedCount={selectedRowKeys.length}
        storeOptions={pushToHqStoreOptions}
        storeOptionsLoading={pushToHqStoreOptionsLoading}
        storeOptionsError={pushToHqStoreOptionsError}
        onRetryStoreOptions={() => void loadPushToHqStoreOptions()}
        confirmLoading={pushToHqConfirmLoading}
        onConfirm={handlePushToHqConfirm}
        onCancel={handlePushToHqCancel}
      />
      {canManageWarehouseStorePriceSync ? (<WarehouseProductStorePriceSyncModal
        open={storePriceSyncOpen}
        selectedProductCodes={selectedRowKeys.map(String)}
        onCancel={() => setStorePriceSyncOpen(false)}
        onSuccess={() => {
            setSelectedRowKeys([]);
            void refreshCurrentList({ page: 1 });
        }}
      />) : null}
      </PageContainer>
    </>);
}

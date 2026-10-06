export interface ProductImportItem {
  id: string
  selected: boolean
  sentToContainer?: boolean
  imageUrl: string
  customImage: boolean
  imageLoadStatus: 'loading' | 'success' | 'error'
  newProduct: {
    quantity: number
    productCode: string
    barcode?: string
    productName: string
    englishName?: string
    domesticPrice?: number
    oemPrice?: number
    midPackQuantity?: number
    casePackQuantity?: number
    volume?: number
    notes?: string
  }
  matchedProduct?: {
    productCode?: string
    hbProductNo?: string
    productName?: string
    englishProductName?: string
    domesticPrice?: number
    oemPrice?: number
    middlePackQuantity?: number
    packingQuantity?: number
    unitVolume?: number
    barcode?: string
    productImage?: string
  }
  status: 'duplicate' | 'new' | 'updated' | 'unchanged' | 'error' | 'dbDuplicate'
  isDuplicate: boolean
  duplicateGroup?: string
  mergedFrom?: number
  diffFields?: string[]
  errors?: { [field: string]: string }
  calculated: {
    totalProducts: number
    totalVolume: number
  }
}

export type ProductStatus = 'duplicate' | 'new' | 'updated' | 'unchanged' | 'error' | 'dbDuplicate'

export interface Statistics {
  total: number
  duplicateCount: number
  newCount: number
  updateCount: number
  unchangedCount: number
  errorCount: number
  dbDuplicateCount: number
  selectedCount: number
  totalQuantity: number
  totalProducts: number
  totalVolume: number
}

export interface DuplicateGroup {
  productCode: string
  count: number
  rows: number[]
  items: ProductImportItem[]
  merged: {
    quantity: number
    casePackQuantity: number
    volume: number
  }
  invalidFields: Array<'quantity' | 'casePackQuantity' | 'volume'>
  isMergeable: boolean
}

export interface MergeDuplicateProductsResult {
  products: ProductImportItem[]
  invalidGroups: DuplicateGroup[]
  mergedGroupCount: number
}

import { useState, type FC } from 'react'
import styles from './styles.module.css'

export type Tr = (key: string, params?: Record<string, string | number>) => string

const SUPPLIER_TAG: Record<string, string> = { '257': 'KFC', '243': 'Brazco' }

/** 供应商小标签：243 = Brazco（紫），其余按 KFC（蓝）；编码不认识就显示编码本身。 */
export function supplierTag(code: string): { label: string; className: string } {
  return code === '243' ? { label: SUPPLIER_TAG[code], className: styles.tagBrazco } : { label: SUPPLIER_TAG[code] ?? code, className: styles.tagKfc }
}

/** 商品图：没有图或加载失败时显示占位，不显示破图。 */
export const ProductImage: FC<{ src: string | null; label: string; className?: string }> = ({ src, label, className }) => {
  const [failed, setFailed] = useState(false)
  if (!src || failed) return <div className={`${styles.thumb} ${styles.thumbEmpty} ${className ?? ''}`}>{label}</div>
  return <img className={`${styles.thumb} ${className ?? ''}`} src={src} alt="" loading="lazy" onError={() => setFailed(true)} />
}

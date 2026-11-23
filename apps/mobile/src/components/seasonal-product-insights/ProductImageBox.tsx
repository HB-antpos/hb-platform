import { PreviewableImage } from "@/components/ui/PreviewableImage";

/** 季节商品查询里的商品图：有图时可点开全屏预览，没有图或加载失败时显示占位图标，不显示破图。 */
export function ProductImageBox({
  uri,
  size,
  label,
  caption,
  introHint,
}: {
  uri: string | null;
  size: number;
  label: string;
  /** 大图预览底部副标题，例如「货号 XM2405 · 条码」。 */
  caption?: string | null;
  /** 详情页主商品图传 true，首次出现「点图片可全屏查看」气泡。 */
  introHint?: boolean;
}) {
  return <PreviewableImage uri={uri} size={size} label={label} caption={caption} introHint={introHint} />;
}

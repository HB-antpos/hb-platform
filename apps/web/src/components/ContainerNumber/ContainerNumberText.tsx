import { useContainerNumberDisplay } from './useContainerNumberDisplay'

interface ContainerNumberTextProps {
  value: string | null | undefined
  /** 编号为空时显示的兜底文本（如货柜 GUID），兜底文本不遮罩 */
  fallback?: string
}

/** 按当前权限与「显示完整编号」开关渲染货柜编号；默认只显示后 4 位。 */
export default function ContainerNumberText({ value, fallback }: ContainerNumberTextProps) {
  const { format } = useContainerNumberDisplay()
  if (!value) return <>{fallback ?? ''}</>
  return <>{format(value)}</>
}

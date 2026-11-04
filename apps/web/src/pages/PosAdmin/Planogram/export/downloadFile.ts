/** 把一段文本存成本地文件（浏览器下载）：Blob + 临时 <a download>，随后立即释放对象地址。 */
export function downloadTextFile(fileName: string, content: string, mime = 'application/json;charset=utf-8'): void {
  const blob = new Blob([content], { type: mime })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  link.style.display = 'none'
  document.body.appendChild(link)
  link.click()
  link.remove()
  // 立刻 revoke 在个别浏览器里会取消下载：延后释放
  window.setTimeout(() => URL.revokeObjectURL(url), 10_000)
}

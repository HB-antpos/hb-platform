import en from '../planogramMessages.en.json'
import zh from '../planogramMessages.zh.json'
import { createPdfTranslator, type MessageTree } from './sheetLogic'

/**
 * PDF 按导出语言（中文 / English / 双语）取词：直接读页面消息文件里的 planogram.pdf.*，
 * 与界面当前语言无关（中文界面也能导出英文版给门店）。
 */
export const translatePdf = createPdfTranslator(zh as unknown as MessageTree, en as unknown as MessageTree)

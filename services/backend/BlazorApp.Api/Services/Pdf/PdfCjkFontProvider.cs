using iTextSharp.text.pdf;

namespace BlazorApp.Api.Services.Pdf;

/// <summary>
/// 后端 iTextSharp PDF 生成共用的中文字体（STSong-Light + UniGB-UCS2-H，不嵌入）。
/// 需要中文字体的 PDF 生成一律从这里取，不要直接调用 BaseFont.CreateFont 创建 CJK 字体。
/// </summary>
/// <remarks>
/// iTextSharp.LGPLv2.Core 首次创建 CJK 字体时，CjkFont 构造函数对静态字典 AllCMaps / AllFonts
/// 做"先查是否存在，再 Add"且不加锁（BaseFont.FontCache 那一层有锁，但保护不到构造函数内部）。
/// 进程冷启动后多个线程同时首次 CreateFont，会抛
/// ArgumentException: An item with the same key has already been added（Key: STSong-Light 或 UniGB-UCS2-H）。
/// 这里把首次创建收口到一把进程内锁里。创建成功后 iTextSharp 对 cached=true 的字体本来就返回同一个缓存实例，
/// 所以各处复用同一个 BaseFont 与原来每次 CreateFont 的行为等价。
/// </remarks>
public static class PdfCjkFontProvider
{
    private const string SimplifiedChineseFontName = "STSong-Light";
    private const string SimplifiedChineseEncoding = "UniGB-UCS2-H";

    private static BaseFont? _simplifiedChinese;
    private static object? _simplifiedChineseLock;

    /// <summary>简体中文宋体（STSong-Light，横排 Unicode 编码，不嵌入）。首次访问时线程安全地创建，之后复用。</summary>
    /// <remarks>
    /// 带 syncLock 的 EnsureInitialized 会在锁内做双重检查，保证工厂只由一个线程执行。
    /// 不用 Lazy&lt;T&gt;：它会缓存工厂抛出的异常，首次创建一旦失败就永久失败；这里失败时字段仍为 null，下次调用会重试。
    /// </remarks>
    public static BaseFont SimplifiedChinese =>
        LazyInitializer.EnsureInitialized(
            ref _simplifiedChinese,
            ref _simplifiedChineseLock,
            static () => BaseFont.CreateFont(
                SimplifiedChineseFontName,
                SimplifiedChineseEncoding,
                BaseFont.NOT_EMBEDDED
            )
        );
}

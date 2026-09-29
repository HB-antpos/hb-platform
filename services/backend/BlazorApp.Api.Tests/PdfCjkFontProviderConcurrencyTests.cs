using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BlazorApp.Api.Services.Pdf;
using iTextSharp.text.pdf;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 回归：iTextSharp 冷启动时多个线程同时首次创建 CJK 字体，会因 CjkFont 静态缓存"先查后 Add"不加锁而抛
/// ArgumentException: An item with the same key has already been added（Key: STSong-Light / UniGB-UCS2-H）。
/// 2026-09-29 PR #387 的 CI 中 ExportContainerProducts_Query_应返回Pdf文件 偶发拿到 500 即此。
/// </summary>
public sealed class PdfCjkFontProviderConcurrencyTests
{
    private const int ColdRounds = 10;
    private const int ThreadsPerRound = 16;
    private static readonly TimeSpan ThreadJoinTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 同一测试进程里 iTextSharp 的静态缓存早被其他 PDF 测试预热，直接多线程调用复现不了冷启动。
    /// 这里每一轮都新建一个可回收的 AssemblyLoadContext，重新加载 iTextSharp 与 BlazorApp.Api：
    /// 程序集重新加载后静态字段全部是新的，等价于一次冷启动，再让 16 个线程同时放行首次取字体。
    /// </summary>
    [Fact]
    public void SimplifiedChinese_冷缓存下多线程同时首次获取_不抛异常且返回同一实例()
    {
        for (var round = 1; round <= ColdRounds; round++)
        {
            RunColdRound(round);
        }
    }

    [Fact]
    public void SimplifiedChinese_与BaseFont缓存返回的实例一致()
    {
        // 先经提供者完成首次创建（锁内），之后直接 CreateFont 只会命中 iTextSharp 的 FontCache，不会再并发构造 CjkFont。
        var fromProvider = PdfCjkFontProvider.SimplifiedChinese;
        var fromITextCache = BaseFont.CreateFont("STSong-Light", "UniGB-UCS2-H", BaseFont.NOT_EMBEDDED);

        Assert.Same(fromProvider, fromITextCache);
        Assert.Same(fromProvider, PdfCjkFontProvider.SimplifiedChinese);
    }

    /// <summary>
    /// 契约：后端源码里除提供者本身外，不能再出现直接按 STSong-Light / UniGB-UCS2 创建字体的代码，
    /// 否则冷启动并发时会绕过提供者的锁、重新触发静态缓存竞态。
    /// </summary>
    [Fact]
    public void 后端源码_中文PDF字体只能经PdfCjkFontProvider创建()
    {
        var apiRoot = Path.Combine(FindRepoRoot(), "services", "backend", "BlazorApp.Api");
        var providerPath = Path.GetFullPath(Path.Combine(apiRoot, "Services", "Pdf", "PdfCjkFontProvider.cs"));
        Assert.True(File.Exists(providerPath), $"找不到字体提供者源码：{providerPath}");

        var violations = Directory
            .EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(path => !IsBuildOutput(apiRoot, path))
            .Where(path => !string.Equals(path, providerPath, StringComparison.Ordinal))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("STSong-Light", StringComparison.Ordinal)
                    || source.Contains("UniGB-UCS2", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(apiRoot, path))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "中文 PDF 字体必须通过 PdfCjkFontProvider.SimplifiedChinese 获取，以下文件直接创建了 CJK 字体：\n"
                + string.Join("\n", violations)
        );
    }

    // 不内联：保证隔离上下文里的对象只被这一帧引用，方法返回后 Unload 才可能真正回收。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunColdRound(int round)
    {
        var context = new ColdITextLoadContext();
        try
        {
            // 前置条件：隔离上下文里是一份新加载的 iTextSharp，静态缓存确实是冷的。
            Assert.NotSame(typeof(BaseFont).Assembly, context.ITextAssembly);
            Assert.NotSame(typeof(PdfCjkFontProvider).Assembly, context.ApiAssembly);

            var getFont = context.CreateSimplifiedChineseGetter();
            var results = new object?[ThreadsPerRound];
            var errors = new ConcurrentQueue<Exception>();
            var threads = new Thread[ThreadsPerRound];
            using var ready = new CountdownEvent(ThreadsPerRound);
            using var gate = new ManualResetEventSlim(false);

            for (var i = 0; i < ThreadsPerRound; i++)
            {
                var index = i;
                threads[i] = new Thread(() =>
                {
                    ready.Signal();
                    gate.Wait();
                    try
                    {
                        results[index] = getFont();
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }
                })
                {
                    IsBackground = true,
                    Name = $"pdf-cjk-font-cold-{round}-{index}",
                };
                threads[i].Start();
            }

            // 所有线程都就位后再同时放行，让首次创建尽量真正重叠。
            ready.Wait();
            gate.Set();

            foreach (var thread in threads)
            {
                // 普通 Dictionary 并发写坏后也可能卡死而不是抛异常，设上限避免整个测试进程挂住。
                Assert.True(
                    thread.Join(ThreadJoinTimeout),
                    $"第 {round} 轮线程 {thread.Name} 在 {ThreadJoinTimeout.TotalSeconds} 秒内未结束，疑似字体静态缓存被并发写坏"
                );
            }

            Assert.True(
                errors.IsEmpty,
                $"第 {round} 轮冷缓存并发首次取中文字体失败（{errors.Count}/{ThreadsPerRound} 个线程）：\n"
                    + string.Join("\n---\n", errors.Select(error => error.ToString()))
            );

            var first = results[0];
            Assert.NotNull(first);
            // 返回的字体类型来自隔离上下文里的 iTextSharp，说明这一轮确实走了冷缓存的创建路径。
            Assert.Same(context.ITextAssembly, first!.GetType().Assembly);
            Assert.All(results, font => Assert.Same(first, font));
        }
        finally
        {
            context.Unload();
        }
    }

    private static bool IsBuildOutput(string apiRoot, string path)
    {
        var relative = Path.GetRelativePath(apiRoot, path);
        var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return firstSegment is "bin" or "obj";
    }

    private static string FindRepoRoot([CallerFilePath] string sourcePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory != null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root.");
    }

    /// <summary>
    /// 只隔离 iTextSharp 与 BlazorApp.Api 两个程序集（提供者必须和新的 iTextSharp 绑定在一起），其余依赖回落默认上下文。
    /// </summary>
    private sealed class ColdITextLoadContext : AssemblyLoadContext
    {
        public ColdITextLoadContext()
            : base($"pdf-cjk-font-cold-{Guid.NewGuid():N}", isCollectible: true)
        {
            ITextAssembly = LoadFromAssemblyPath(typeof(BaseFont).Assembly.Location);
            ApiAssembly = LoadFromAssemblyPath(typeof(PdfCjkFontProvider).Assembly.Location);
        }

        public Assembly ITextAssembly { get; }

        public Assembly ApiAssembly { get; }

        public Func<object> CreateSimplifiedChineseGetter()
        {
            var providerType = ApiAssembly.GetType(typeof(PdfCjkFontProvider).FullName!, throwOnError: true)!;
            var getter = providerType
                .GetProperty(nameof(PdfCjkFontProvider.SimplifiedChinese), BindingFlags.Public | BindingFlags.Static)!
                .GetMethod!;
            // 引用类型返回值协变，直接绑成 Func<object>，异常不会被反射包成 TargetInvocationException。
            return getter.CreateDelegate<Func<object>>();
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, ITextAssembly?.GetName().Name, StringComparison.Ordinal))
            {
                return ITextAssembly;
            }

            if (string.Equals(assemblyName.Name, ApiAssembly?.GetName().Name, StringComparison.Ordinal))
            {
                return ApiAssembly;
            }

            return null;
        }
    }
}

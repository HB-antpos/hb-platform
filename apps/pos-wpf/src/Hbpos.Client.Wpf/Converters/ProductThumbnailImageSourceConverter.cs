using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.Converters;

public sealed class ProductThumbnailImageSourceConverter : IValueConverter
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> CacheInsertionOrder = new();
    private static readonly Dictionary<string, DateTimeOffset> FailedCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> FailedCacheInsertionOrder = new();
    private static readonly Dictionary<string, InFlightImageLoadState> InFlightImageLoads = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, byte> LoggedDiagnostics = new(StringComparer.Ordinal);
    private static readonly Queue<string> LoggedDiagnosticInsertionOrder = new();
    private static readonly HttpClient RemoteImageHttpClient = CreateRemoteImageHttpClient();
    private static readonly object RemoteImageLoaderLock = new();
    private static readonly SemaphoreSlim ImageDecodeGate = new(4);
    private static Func<Uri, CancellationToken, Task<byte[]>> RemoteImageBytesLoader = DownloadRemoteImageBytesAsync;
    private static Func<Uri> ApiBaseAddressProvider = ServiceRegistration.GetApiBaseAddress;
    private static Action? DataImageDecodeStartingForTests;
    private static Action? DataImageBytesAllocatedForTests;
    private static Action<int>? CacheKeyUtf8ChunkForTests;
    private static int nextAsyncLoadVersion;
    private const string DataImagePrefix = "data:image/";
    private const int MaxLoggedValueLength = 300;
    private const int MaxCacheKeyLength = 512;
    private const int CacheKeyHashBufferSize = 4 * 1024;
    private const int DefaultImageCacheCapacity = 256;
    private const int DefaultFailedCacheCapacity = 1024;
    private const int DefaultLoggedDiagnosticCapacity = 512;
    private const int MaxDataImageMetadataLength = 256;
    // 商品原图 JPEG/PNG 可能超过 2 MiB；8 MiB 兼容常见上传，同时把 4 路流式读取与返回 byte[] 的峰值限定在约 64 MiB。
    private const int DefaultImageInputByteLimit = 8 * 1024 * 1024;
    private static readonly TimeSpan RemoteImageDownloadTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan FailedCacheDuration = TimeSpan.FromMinutes(10);
    private static int imageCacheCapacity = DefaultImageCacheCapacity;
    private static int failedCacheCapacity = DefaultFailedCacheCapacity;
    private static int loggedDiagnosticCapacity = DefaultLoggedDiagnosticCapacity;
    private static int imageInputByteLimit = DefaultImageInputByteLimit;
    // 远程下载结果只做周期汇总（一屏可能几百张图），状态由 DownloadStatsLock 保护，任何线程都可能完成下载。
    private static readonly TimeSpan DownloadSummaryInterval = TimeSpan.FromMinutes(5);
    private static readonly object DownloadStatsLock = new();
    private static Func<DateTimeOffset> DownloadStatsClock = () => DateTimeOffset.UtcNow;
    private static DateTimeOffset? downloadStatsWindowStartUtc;
    private static int downloadSuccessCount;
    private static int downloadFailureCount;
    private static long downloadElapsedTotalMs;
    private static long downloadBytesTotal;

    public int DecodePixelWidth { get; set; } = 72;

    public static readonly DependencyProperty AsyncSourceTextProperty =
        DependencyProperty.RegisterAttached(
            "AsyncSourceText",
            typeof(string),
            typeof(ProductThumbnailImageSourceConverter),
            new PropertyMetadata(null, OnAsyncSourceTextChanged));

    public static readonly DependencyProperty AsyncDecodePixelWidthProperty =
        DependencyProperty.RegisterAttached(
            "AsyncDecodePixelWidth",
            typeof(int),
            typeof(ProductThumbnailImageSourceConverter),
            new PropertyMetadata(72, OnAsyncDecodePixelWidthChanged));

    public static readonly DependencyProperty AsyncIsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "AsyncIsEnabled",
            typeof(bool),
            typeof(ProductThumbnailImageSourceConverter),
            new PropertyMetadata(true, OnAsyncIsEnabledChanged));

    private static readonly DependencyProperty AsyncLoadVersionProperty =
        DependencyProperty.RegisterAttached(
            "AsyncLoadVersion",
            typeof(int),
            typeof(ProductThumbnailImageSourceConverter),
            new PropertyMetadata(0));

    private static readonly DependencyProperty AsyncLoadCancellationProperty =
        DependencyProperty.RegisterAttached(
            "AsyncLoadCancellation",
            typeof(CancellationTokenSource),
            typeof(ProductThumbnailImageSourceConverter),
            new PropertyMetadata(null));

    public static string? GetAsyncSourceText(DependencyObject element)
    {
        return (string?)element.GetValue(AsyncSourceTextProperty);
    }

    public static void SetAsyncSourceText(DependencyObject element, string? value)
    {
        element.SetValue(AsyncSourceTextProperty, value);
    }

    public static int GetAsyncDecodePixelWidth(DependencyObject element)
    {
        return (int)element.GetValue(AsyncDecodePixelWidthProperty);
    }

    public static void SetAsyncDecodePixelWidth(DependencyObject element, int value)
    {
        element.SetValue(AsyncDecodePixelWidthProperty, value);
    }

    public static bool GetAsyncIsEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(AsyncIsEnabledProperty);
    }

    public static void SetAsyncIsEnabled(DependencyObject element, bool value)
    {
        element.SetValue(AsyncIsEnabledProperty, value);
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string uriText || string.IsNullOrWhiteSpace(uriText))
        {
            return null;
        }

        var sourceText = uriText.Trim();
        var imageRequest = TryCreateImageRequest(sourceText);
        if (imageRequest is null)
        {
            return null;
        }

        if (!imageRequest.CanCache)
        {
            return CreateImageSource(imageRequest);
        }

        var cacheKey = CreateCacheKey(DecodePixelWidth, imageRequest);
        var cacheDecision = GetCacheLoadDecision(cacheKey, registerProducerOnMiss: true);
        if (cacheDecision.Disposition == CacheLoadDisposition.Cached)
        {
            return cacheDecision.CachedImage;
        }

        if (cacheDecision.Disposition != CacheLoadDisposition.Producer)
        {
            // 同步转换器不能等待后台任务；生产 XAML 使用异步 attached-property 路径。
            return null;
        }

        ImageSource? publishedImage = null;
        try
        {
            var imageSource = CreateImageSource(imageRequest);
            if (imageSource is not null)
            {
                TryCacheImage(cacheKey, imageSource);
                publishedImage = imageSource;
            }
            else if (imageRequest.IsRemoteUri)
            {
                MarkFailedCache(cacheKey);
            }

            return imageSource;
        }
        finally
        {
            CompleteInFlightImageLoad(cacheKey, cacheDecision.ProducerState!, publishedImage);
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    public static async Task<int> PreloadAsync(
        IEnumerable<string?> sourceTexts,
        int decodePixelWidth = 72,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceTexts);

        var uniqueSourceTexts = sourceTexts
            .Where(sourceText => !string.IsNullOrWhiteSpace(sourceText))
            .Select(sourceText => sourceText!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (uniqueSourceTexts.Length == 0)
        {
            return 0;
        }

        var normalizedDecodePixelWidth = Math.Max(1, decodePixelWidth);
        var preloadTasks = uniqueSourceTexts.Select(sourceText =>
            PreloadSingleAsync(sourceText, normalizedDecodePixelWidth, cancellationToken));
        var results = await Task.WhenAll(preloadTasks).ConfigureAwait(false);
        return results.Count(preloaded => preloaded);
    }

    private ImageSource? CreateImageSource(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (imageRequest.ImageBytes is not null)
            {
                return CreateBitmapImageFromBytes(
                    imageRequest.ImageBytes,
                    applyDecodePixelWidth: string.Equals(imageRequest.SourceKind, "data", StringComparison.Ordinal),
                    Math.Max(1, DecodePixelWidth),
                    cancellationToken);
            }

            if (imageRequest.Uri is null)
            {
                return null;
            }

            if (imageRequest.IsRemoteUri)
            {
                return TryDownloadRemoteImageBytes(imageRequest, cancellationToken, out var remoteBytes)
                    ? CreateBitmapImageFromBytes(
                        remoteBytes,
                        applyDecodePixelWidth: true,
                        Math.Max(1, DecodePixelWidth),
                        cancellationToken)
                    : null;
            }

            if (imageRequest.IsLocalFileUri && !File.Exists(imageRequest.Uri.LocalPath))
            {
                LogRejectedRequest(imageRequest.CacheKey, "file-missing", "file");
                return null;
            }

            if (string.Equals(imageRequest.Uri.Scheme, "pack", StringComparison.OrdinalIgnoreCase))
            {
                if (TryReadPackResourceBytes(imageRequest.Uri, out var packBytes))
                {
                    return CreateBitmapImageFromBytes(
                        packBytes,
                        applyDecodePixelWidth: false,
                        Math.Max(1, DecodePixelWidth),
                        cancellationToken);
                }
            }

            var uriImage = new BitmapImage();
            uriImage.BeginInit();
            uriImage.UriSource = imageRequest.Uri;
            uriImage.DecodePixelWidth = Math.Max(1, DecodePixelWidth);
            uriImage.CacheOption = BitmapCacheOption.OnLoad;
            cancellationToken.ThrowIfCancellationRequested();
            uriImage.EndInit();
            cancellationToken.ThrowIfCancellationRequested();

            if (uriImage.CanFreeze)
            {
                uriImage.Freeze();
            }

            return uriImage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogImageDiagnosticOnce(
                $"create-failed|{imageRequest.SourceKind}|{imageRequest.CacheKey}",
                "image source create failed " +
                $"sourceKind={imageRequest.SourceKind} " +
                $"cacheKey={FormatLogValue(imageRequest.CacheKey)} " +
                $"exception={ex.GetType().Name}: {FormatLogValue(ex.Message)}");
            return null;
        }
    }

    private static async Task<bool> PreloadSingleAsync(
        string sourceText,
        int decodePixelWidth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var imageRequest = TryCreateImageRequest(sourceText, decodeDataImageBytes: false);
        if (imageRequest is null || !imageRequest.CanCache)
        {
            return false;
        }

        var cacheKey = RequiresHashedCacheKey(decodePixelWidth, imageRequest)
            ? await CreateHashedCacheKeyOnWorkerAsync(decodePixelWidth, imageRequest, cancellationToken).ConfigureAwait(false)
            : CreateCacheKey(decodePixelWidth, imageRequest);
        var cacheDecision = GetCacheLoadDecision(cacheKey, registerProducerOnMiss: true);
        if (cacheDecision.Disposition != CacheLoadDisposition.Producer)
        {
            return false;
        }

        ImageSource? publishedImage = null;
        try
        {
            ImageSource? imageSource;
            await ImageDecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                imageSource = await Task.Run(
                    () => CreateImageSourceOnWorker(imageRequest, decodePixelWidth, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ImageDecodeGate.Release();
            }

            if (imageSource is not null)
            {
                var added = TryCacheImage(cacheKey, imageSource);
                publishedImage = imageSource;
                return added;
            }

            if (imageRequest.IsRemoteUri)
            {
                MarkFailedCache(cacheKey);
            }

            return false;
        }
        finally
        {
            CompleteInFlightImageLoad(cacheKey, cacheDecision.ProducerState!, publishedImage);
        }
    }

    private static string CreateCacheKey(
        int decodePixelWidth,
        ImageRequest imageRequest,
        CancellationToken cancellationToken = default)
    {
        var decodePrefix = $"{Math.Max(1, decodePixelWidth)}|";
        return imageRequest.CacheKey.Length <= MaxCacheKeyLength - decodePrefix.Length
            ? decodePrefix + imageRequest.CacheKey
            : CreateSha256CacheKey(decodePrefix, imageRequest.CacheKey, cancellationToken);
    }

    private static bool RequiresHashedCacheKey(int decodePixelWidth, ImageRequest imageRequest)
    {
        var decodePrefixLength = $"{Math.Max(1, decodePixelWidth)}|".Length;
        return imageRequest.CacheKey.Length > MaxCacheKeyLength - decodePrefixLength;
    }

    private static async Task<string> CreateHashedCacheKeyOnWorkerAsync(
        int decodePixelWidth,
        ImageRequest imageRequest,
        CancellationToken cancellationToken)
    {
        // 长键先占用共享槽位再转交后台，避免批量预加载在 Dispatcher 同步 SHA-256。
        await ImageDecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => CreateCacheKey(decodePixelWidth, imageRequest, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ImageDecodeGate.Release();
        }
    }

    private static bool IsFailureCachedUnsafe(string cacheKey)
    {
        if (!FailedCache.TryGetValue(cacheKey, out var failedAt))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - failedAt <= FailedCacheDuration)
        {
            return true;
        }

        FailedCache.Remove(cacheKey);
        RemoveFailedCacheInsertionOrderEntry(cacheKey);
        return false;
    }

    private static void MarkFailedCache(string cacheKey)
    {
        CacheFailedImage(cacheKey, DateTimeOffset.UtcNow);
    }

    private static CacheLoadDecision GetCacheLoadDecision(string cacheKey, bool registerProducerOnMiss)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(cacheKey, out var cachedImage))
            {
                return new CacheLoadDecision(CacheLoadDisposition.Cached, cachedImage, null, null);
            }

            if (IsFailureCachedUnsafe(cacheKey))
            {
                return InFlightImageLoads.TryGetValue(cacheKey, out var inFlightState) &&
                       inFlightState.ActiveProducerCount > 0
                    ? new CacheLoadDecision(
                        CacheLoadDisposition.WaitForInFlight,
                        null,
                        inFlightState.Completion.Task,
                        null)
                    : new CacheLoadDecision(CacheLoadDisposition.Failed, null, null, null);
            }

            if (!registerProducerOnMiss)
            {
                return new CacheLoadDecision(CacheLoadDisposition.Miss, null, null, null);
            }

            if (!InFlightImageLoads.TryGetValue(cacheKey, out var producerState))
            {
                producerState = new InFlightImageLoadState();
                InFlightImageLoads.Add(cacheKey, producerState);
            }

            producerState.ActiveProducerCount++;
            return new CacheLoadDecision(CacheLoadDisposition.Producer, null, null, producerState);
        }
    }

    private static void CompleteInFlightImageLoad(
        string cacheKey,
        InFlightImageLoadState producerState,
        ImageSource? publishedImage)
    {
        lock (CacheLock)
        {
            if (publishedImage is not null)
            {
                producerState.Completion.TrySetResult(publishedImage);
            }
            else if (Cache.TryGetValue(cacheKey, out var cachedImage))
            {
                producerState.Completion.TrySetResult(cachedImage);
            }

            producerState.ActiveProducerCount--;
            if (producerState.ActiveProducerCount > 0)
            {
                return;
            }

            if (!producerState.Completion.Task.IsCompleted)
            {
                producerState.Completion.TrySetResult(
                    Cache.TryGetValue(cacheKey, out var finalCachedImage) ? finalCachedImage : null);
            }

            if (InFlightImageLoads.TryGetValue(cacheKey, out var currentState) &&
                ReferenceEquals(currentState, producerState))
            {
                InFlightImageLoads.Remove(cacheKey);
            }
        }
    }

    private static bool TryCacheImage(string cacheKey, ImageSource imageSource)
    {
        lock (CacheLock)
        {
            if (Cache.ContainsKey(cacheKey))
            {
                if (FailedCache.Remove(cacheKey))
                {
                    RemoveFailedCacheInsertionOrderEntry(cacheKey);
                }

                return false;
            }

            EnsureCacheCapacity(Cache, CacheInsertionOrder, imageCacheCapacity);
            Cache.Add(cacheKey, imageSource);
            CacheInsertionOrder.Enqueue(cacheKey);
            if (FailedCache.Remove(cacheKey))
            {
                RemoveFailedCacheInsertionOrderEntry(cacheKey);
            }

            return true;
        }
    }

    private static void CacheFailedImage(string cacheKey, DateTimeOffset failedAt)
    {
        lock (CacheLock)
        {
            if (Cache.ContainsKey(cacheKey))
            {
                return;
            }

            if (FailedCache.ContainsKey(cacheKey))
            {
                FailedCache[cacheKey] = failedAt;
                return;
            }

            EnsureCacheCapacity(FailedCache, FailedCacheInsertionOrder, failedCacheCapacity);
            FailedCache.Add(cacheKey, failedAt);
            FailedCacheInsertionOrder.Enqueue(cacheKey);
        }
    }

    private static void RemoveFailedCacheInsertionOrderEntry(string cacheKey)
    {
        var queuedCount = FailedCacheInsertionOrder.Count;
        for (var index = 0; index < queuedCount; index++)
        {
            var queuedKey = FailedCacheInsertionOrder.Dequeue();
            if (!string.Equals(queuedKey, cacheKey, StringComparison.OrdinalIgnoreCase))
            {
                FailedCacheInsertionOrder.Enqueue(queuedKey);
            }
        }
    }

    private static bool TryRememberDiagnostic(string key)
    {
        var normalizedKey = NormalizeCacheKey(key);
        lock (CacheLock)
        {
            if (LoggedDiagnostics.ContainsKey(normalizedKey))
            {
                return false;
            }

            EnsureCacheCapacity(
                LoggedDiagnostics,
                LoggedDiagnosticInsertionOrder,
                loggedDiagnosticCapacity);
            LoggedDiagnostics.Add(normalizedKey, 0);
            LoggedDiagnosticInsertionOrder.Enqueue(normalizedKey);
            return true;
        }
    }

    private static void EnsureCacheCapacity<TValue>(
        Dictionary<string, TValue> cache,
        Queue<string> insertionOrder,
        int capacity)
    {
        // 过期失败项会在队列中留下陈旧键，持续出队才能确保字典始终受容量限制。
        while (cache.Count >= capacity && insertionOrder.Count > 0)
        {
            cache.Remove(insertionOrder.Dequeue());
        }
    }

    private static void ClearCachesUnsafe()
    {
        Cache.Clear();
        CacheInsertionOrder.Clear();
        FailedCache.Clear();
        FailedCacheInsertionOrder.Clear();
        LoggedDiagnostics.Clear();
        LoggedDiagnosticInsertionOrder.Clear();
    }

    private static string NormalizeCacheKey(string key)
    {
        if (key.Length <= MaxCacheKeyLength)
        {
            return key;
        }

        return CreateSha256CacheKey(key);
    }

    private static string CreateSha256CacheKey(
        string first,
        string? second = null,
        CancellationToken cancellationToken = default)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        AppendUtf8CacheKeyPart(hash, first, cancellationToken);
        if (second is not null)
        {
            AppendUtf8CacheKeyPart(hash, second, cancellationToken);
        }

        return $"sha256:{System.Convert.ToHexString(hash.GetHashAndReset())}";
    }

    private static void AppendUtf8CacheKeyPart(
        System.Security.Cryptography.IncrementalHash hash,
        string value,
        CancellationToken cancellationToken)
    {
        var encoder = System.Text.Encoding.UTF8.GetEncoder();
        var remainingChars = value.AsSpan();
        var cacheKeyUtf8ChunkForTests = Volatile.Read(ref CacheKeyUtf8ChunkForTests);
        Span<byte> buffer = stackalloc byte[CacheKeyHashBufferSize];
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            encoder.Convert(
                remainingChars,
                buffer,
                flush: true,
                out var charsUsed,
                out var bytesUsed,
                out var completed);
            if (bytesUsed > 0)
            {
                cacheKeyUtf8ChunkForTests?.Invoke(bytesUsed);
                hash.AppendData(buffer[..bytesUsed]);
            }

            remainingChars = remainingChars[charsUsed..];
            if (completed)
            {
                return;
            }
        }
        while (true);
    }

    private static void OnAsyncSourceTextChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (!GetAsyncIsEnabled(target))
        {
            CancelAsyncImageLoad(target);
            return;
        }

        BeginAsyncImageLoad(target, e.NewValue as string);
    }

    private static void OnAsyncDecodePixelWidthChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (!GetAsyncIsEnabled(target))
        {
            CancelAsyncImageLoad(target);
            return;
        }

        BeginAsyncImageLoad(target, GetAsyncSourceText(target));
    }

    private static void OnAsyncIsEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            BeginAsyncImageLoad(target, GetAsyncSourceText(target));
            return;
        }

        CancelAsyncImageLoad(target);
    }

    internal static IDisposable UseRemoteImageBytesLoaderForTests(Func<Uri, CancellationToken, Task<byte[]>> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);

        lock (RemoteImageLoaderLock)
        {
            var previous = RemoteImageBytesLoader;
            RemoteImageBytesLoader = loader;
            return new RemoteImageLoaderScope(previous);
        }
    }

    internal static IDisposable UseCacheLimitsForTests(
        int imageCapacity,
        int failureCapacity,
        int diagnosticCapacity)
    {
        if (imageCapacity <= 0 || failureCapacity <= 0 || diagnosticCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageCapacity), "缓存容量必须大于零。");
        }

        lock (CacheLock)
        {
            var scope = new CacheLimitsScope(
                imageCacheCapacity,
                failedCacheCapacity,
                loggedDiagnosticCapacity);
            imageCacheCapacity = imageCapacity;
            failedCacheCapacity = failureCapacity;
            loggedDiagnosticCapacity = diagnosticCapacity;
            ClearCachesUnsafe();
            return scope;
        }
    }

    internal static IDisposable UseImageInputByteLimitForTests(int byteLimit)
    {
        if (byteLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLimit), "图像输入上限必须大于零。");
        }

        lock (RemoteImageLoaderLock)
        {
            var previous = imageInputByteLimit;
            imageInputByteLimit = byteLimit;
            return new ImageInputByteLimitScope(previous);
        }
    }

    internal static IDisposable UseDataImageDecodeStartingForTests(
        Action callback,
        Action? bytesAllocated = null)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (RemoteImageLoaderLock)
        {
            var previous = DataImageDecodeStartingForTests;
            var previousBytesAllocated = DataImageBytesAllocatedForTests;
            DataImageDecodeStartingForTests = callback;
            DataImageBytesAllocatedForTests = bytesAllocated;
            return new DataImageDecodeStartingScope(previous, previousBytesAllocated);
        }
    }

    internal static IDisposable UseCacheKeyUtf8ChunkForTests(Action<int> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (RemoteImageLoaderLock)
        {
            var previous = CacheKeyUtf8ChunkForTests;
            CacheKeyUtf8ChunkForTests = callback;
            return new CacheKeyUtf8ChunkScope(previous);
        }
    }

    internal static Task<byte[]> ReadRemoteImageContentForTestsAsync(
        HttpContent content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return ReadRemoteImageContentAsync(content, cancellationToken);
    }

    internal static void ClearCachesForTests()
    {
        lock (CacheLock)
        {
            ClearCachesUnsafe();
        }
    }

    internal static (int ImageCount, int FailedCount, int DiagnosticCount) GetCacheCountsForTests()
    {
        lock (CacheLock)
        {
            return (Cache.Count, FailedCache.Count, LoggedDiagnostics.Count);
        }
    }

    internal static int GetInFlightImageLoadCountForTests()
    {
        lock (CacheLock)
        {
            return InFlightImageLoads.Count;
        }
    }

    internal static IReadOnlyList<string> GetImageCacheKeysForTests()
    {
        lock (CacheLock)
        {
            return Cache.Keys.ToArray();
        }
    }

    internal static void SetFailedCacheEntryForTests(string sourceText, DateTimeOffset failedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);

        var imageRequest = TryCreateImageRequest(sourceText.Trim())
            ?? throw new ArgumentException("测试失败缓存源无效。", nameof(sourceText));
        CacheFailedImage(CreateCacheKey(72, imageRequest), failedAt);
    }

    internal static void ConfigureApiBaseAddressProvider(Func<Uri> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Volatile.Write(ref ApiBaseAddressProvider, provider);
    }

    internal static IDisposable UseApiBaseAddressProviderForTests(Func<Uri> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (RemoteImageLoaderLock)
        {
            var previous = ApiBaseAddressProvider;
            ApiBaseAddressProvider = provider;
            return new ApiBaseAddressProviderScope(previous);
        }
    }

    private static void BeginAsyncImageLoad(DependencyObject target, string? sourceText)
    {
        CancelActiveAsyncImageLoad(target);
        var version = Interlocked.Increment(ref nextAsyncLoadVersion);
        target.SetValue(AsyncLoadVersionProperty, version);
        SetTargetImageSource(target, null);

        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return;
        }

        var trimmedSourceText = sourceText.Trim();
        var imageRequest = TryCreateImageRequest(trimmedSourceText, decodeDataImageBytes: false);
        if (imageRequest is null)
        {
            return;
        }

        var decodePixelWidth = Math.Max(1, GetAsyncDecodePixelWidth(target));
        string? cacheKey = null;
        if (!RequiresHashedCacheKey(decodePixelWidth, imageRequest))
        {
            cacheKey = CreateCacheKey(decodePixelWidth, imageRequest);
            if (imageRequest.CanCache)
            {
                var cacheDecision = GetCacheLoadDecision(cacheKey, registerProducerOnMiss: false);
                if (cacheDecision.Disposition == CacheLoadDisposition.Cached)
                {
                    SetTargetImageSource(target, cacheDecision.CachedImage);
                    return;
                }

                if (cacheDecision.Disposition == CacheLoadDisposition.Failed)
                {
                    return;
                }
            }
        }

        var cts = new CancellationTokenSource();
        target.SetValue(AsyncLoadCancellationProperty, cts);
        _ = LoadImageForTargetAsync(
            target,
            imageRequest,
            trimmedSourceText,
            decodePixelWidth,
            cacheKey,
            version,
            cts);
    }

    private static void CancelAsyncImageLoad(DependencyObject target)
    {
        CancelActiveAsyncImageLoad(target);
        var version = Interlocked.Increment(ref nextAsyncLoadVersion);
        target.SetValue(AsyncLoadVersionProperty, version);
        SetTargetImageSource(target, null);
    }

    private static void CancelActiveAsyncImageLoad(DependencyObject target)
    {
        if (target.GetValue(AsyncLoadCancellationProperty) is not CancellationTokenSource cts)
        {
            return;
        }

        target.ClearValue(AsyncLoadCancellationProperty);
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task LoadImageForTargetAsync(
        DependencyObject target,
        ImageRequest imageRequest,
        string sourceText,
        int decodePixelWidth,
        string? cacheKey,
        int version,
        CancellationTokenSource cts)
    {
        CancellationToken cancellationToken;
        try
        {
            cancellationToken = cts.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        string resolvedCacheKey;
        try
        {
            resolvedCacheKey = cacheKey ?? await CreateHashedCacheKeyOnWorkerAsync(
                decodePixelWidth,
                imageRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        InFlightImageLoadState? producerState = null;
        if (imageRequest.CanCache)
        {
            var cacheDecision = GetCacheLoadDecision(resolvedCacheKey, registerProducerOnMiss: true);
            switch (cacheDecision.Disposition)
            {
                case CacheLoadDisposition.Cached:
                    await ApplyImageToTargetAsync(
                        target,
                        imageRequest,
                        sourceText,
                        cacheDecision.CachedImage,
                        resolvedCacheKey,
                        version,
                        cts);
                    return;

                case CacheLoadDisposition.Failed:
                    await ApplyImageToTargetAsync(
                        target,
                        imageRequest,
                        sourceText,
                        imageSource: null,
                        resolvedCacheKey,
                        version,
                        cts);
                    return;

                case CacheLoadDisposition.WaitForInFlight:
                    ImageSource? inFlightImage;
                    try
                    {
                        inFlightImage = await cacheDecision.InFlightCompletion!
                            .WaitAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    await ApplyImageToTargetAsync(
                        target,
                        imageRequest,
                        sourceText,
                        inFlightImage,
                        resolvedCacheKey,
                        version,
                        cts);
                    return;

                case CacheLoadDisposition.Producer:
                    producerState = cacheDecision.ProducerState;
                    break;
            }
        }

        ImageSource? imageSource = null;
        ImageSource? publishedImage = null;
        var gateEntered = false;
        try
        {
            await ImageDecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            imageSource = await Task.Run(
                () => CreateImageSourceOnWorker(imageRequest, decodePixelWidth, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested && imageRequest.CanCache && imageSource is not null)
            {
                TryCacheImage(resolvedCacheKey, imageSource);
                publishedImage = imageSource;
            }
            else if (!cancellationToken.IsCancellationRequested && imageRequest.CanCache && imageRequest.IsRemoteUri)
            {
                MarkFailedCache(resolvedCacheKey);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (gateEntered)
            {
                ImageDecodeGate.Release();
            }

            if (producerState is not null)
            {
                CompleteInFlightImageLoad(resolvedCacheKey, producerState, publishedImage);
            }
        }

        await ApplyImageToTargetAsync(
            target,
            imageRequest,
            sourceText,
            imageSource,
            resolvedCacheKey,
            version,
            cts);
    }

    private static async Task ApplyImageToTargetAsync(
        DependencyObject target,
        ImageRequest imageRequest,
        string sourceText,
        ImageSource? imageSource,
        string cacheKey,
        int version,
        CancellationTokenSource cts)
    {
        var dispatcher = target.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            cts.Dispose();
            return;
        }

        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(target.GetValue(AsyncLoadCancellationProperty), cts))
                {
                    target.ClearValue(AsyncLoadCancellationProperty);
                    cts.Dispose();
                }

                if ((int)target.GetValue(AsyncLoadVersionProperty) != version)
                {
                    return;
                }

                if (!string.Equals(GetAsyncSourceText(target)?.Trim(), sourceText, StringComparison.Ordinal))
                {
                    return;
                }

                SetTargetImageSource(target, imageSource);
            });
        }
        catch (TaskCanceledException)
        {
            cts.Dispose();
        }
        catch (InvalidOperationException)
        {
            cts.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            cts.Dispose();
            LogImageDiagnosticOnce(
                $"async-dispatch:{cacheKey}",
                $"thumbnail UI apply failed sourceKind={imageRequest.SourceKind} error={ex.GetType().Name}");
        }
    }

    private static ImageSource? CreateImageSourceOnWorker(
        ImageRequest imageRequest,
        int decodePixelWidth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(imageRequest.SourceKind, "data", StringComparison.Ordinal) && imageRequest.ImageBytes is null)
        {
            // 异步路径只在共享闸门内解码 data URI，避免首屏同时分配多个原图 byte[]。
            var decodedDataRequest = TryCreateImageRequest(
                imageRequest.CacheKey,
                cancellationToken: cancellationToken);
            if (decodedDataRequest is null)
            {
                return null;
            }

            imageRequest = decodedDataRequest;
        }

        var converter = new ProductThumbnailImageSourceConverter { DecodePixelWidth = decodePixelWidth };
        return converter.CreateImageSource(imageRequest, cancellationToken);
    }

    private static void SetTargetImageSource(DependencyObject target, ImageSource? imageSource)
    {
        switch (target)
        {
            case Image image:
                image.Source = imageSource;
                break;
            case ImageBrush imageBrush:
                imageBrush.ImageSource = imageSource;
                break;
        }
    }

    private static BitmapImage CreateBitmapImageFromBytes(
        byte[] imageBytes,
        bool applyDecodePixelWidth,
        int decodePixelWidth,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(imageBytes, writable: false);
        var streamImage = new BitmapImage();
        streamImage.BeginInit();
        streamImage.StreamSource = stream;
        streamImage.CacheOption = BitmapCacheOption.OnLoad;
        streamImage.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        if (applyDecodePixelWidth)
        {
            streamImage.DecodePixelWidth = decodePixelWidth;
        }

        cancellationToken.ThrowIfCancellationRequested();
        streamImage.EndInit();
        cancellationToken.ThrowIfCancellationRequested();
        if (streamImage.CanFreeze)
        {
            streamImage.Freeze();
        }

        return streamImage;
    }

    private static ImageRequest? TryCreateImageRequest(
        string sourceText,
        bool decodeDataImageBytes = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryCreateDataImageRequest(
                sourceText,
                out var dataRequest,
                decodeDataImageBytes,
                cancellationToken))
        {
            return dataRequest;
        }

        if (sourceText.StartsWith(DataImagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (TryCreatePackResourceRequest(sourceText, out var packRequest))
        {
            return packRequest;
        }

        if (TryCreateLocalFileRequest(sourceText, out var fileRequest))
        {
            return fileRequest;
        }

        if (Path.IsPathRooted(sourceText) && !IsRootRelativeApiPath(sourceText))
        {
            return null;
        }

        if (TryCreateHttpImageRequest(sourceText, out var httpRequest))
        {
            return httpRequest;
        }

        if (Uri.TryCreate(sourceText, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.IsFile)
            {
                return TryCreateLocalFileRequest(absoluteUri.LocalPath, out fileRequest)
                    ? fileRequest
                    : null;
            }

            if (string.Equals(absoluteUri.Scheme, "pack", StringComparison.OrdinalIgnoreCase))
            {
                return new ImageRequest(absoluteUri.AbsoluteUri, absoluteUri, null, "pack");
            }

            LogRejectedRequest(sourceText, "unsupported-uri-scheme", "unsupported");
            return null;
        }

        if (TryResolveRelativeApiPath(sourceText, out var relativeUri))
        {
            LogImageDiagnosticOnce(
                $"relative-resolved|{sourceText}",
                "image request resolved " +
                "sourceKind=relative " +
                $"value={FormatLogValue(sourceText)} " +
                $"resolvedUri={FormatLogValue(relativeUri.AbsoluteUri)}");
            return new ImageRequest(relativeUri.AbsoluteUri, relativeUri, null, "relative");
        }

        LogRejectedRequest(sourceText, "unrecognized-source", InferSourceKind(sourceText));
        return null;
    }

    private static bool TryCreateHttpImageRequest(string sourceText, out ImageRequest? imageRequest)
    {
        imageRequest = null;
        if (!sourceText.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !sourceText.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedText = sourceText.Replace("#", "%23", StringComparison.Ordinal);
        if (!Uri.TryCreate(normalizedText, UriKind.Absolute, out var normalizedUri) ||
            !string.Equals(normalizedUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalizedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            LogRejectedRequest(sourceText, "invalid-http-uri", "http");
            return true;
        }

        Uri.TryCreate(sourceText, UriKind.Absolute, out var originalUri);
        LogRemoteUriDiagnostics(sourceText, normalizedUri, originalUri);
        imageRequest = new ImageRequest(normalizedUri.AbsoluteUri, normalizedUri, null, "http");
        return true;
    }

    private static bool TryCreateDataImageRequest(
        string sourceText,
        out ImageRequest? imageRequest,
        bool decodeImageBytes = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        imageRequest = null;
        if (!sourceText.StartsWith(DataImagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commaIndex = sourceText.IndexOf(',');
        if (commaIndex <= 0)
        {
            return false;
        }

        var dataLength = sourceText.Length - commaIndex - 1;
        var byteLimit = Volatile.Read(ref imageInputByteLimit);
        if (commaIndex > MaxDataImageMetadataLength)
        {
            LogImageDiagnosticOnce(
                $"data-metadata-too-large|{commaIndex}|{dataLength}",
                "image request rejected " +
                "sourceKind=data " +
                "reason=data-metadata-too-large " +
                $"metadataLength={commaIndex} " +
                $"dataLength={dataLength}");
            return false;
        }

        if (dataLength > GetMaxBase64Length(byteLimit))
        {
            LogDataImageRejected("data-payload-too-large", sourceText[..commaIndex], dataLength);
            return false;
        }

        var metadata = sourceText[..commaIndex];
        if (!metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!decodeImageBytes)
        {
            imageRequest = new ImageRequest(sourceText, null, null, "data");
            return true;
        }

        try
        {
            // 先按 Base64 文本长度拒绝，避免为超大 data URI 分配完整 byte[]；解码后仍复核边界。
            // 直接在原始字符串 Span 上解码，避免复制完整 Base64 载荷。
            var base64Chars = sourceText.AsSpan(commaIndex + 1);
            if (!TryGetDecodedBase64Length(base64Chars, out var decodedByteLength, cancellationToken))
            {
                LogDataImageRejected("invalid-data-base64", metadata, dataLength);
                return false;
            }

            if (decodedByteLength > byteLimit)
            {
                LogDataImageRejected("data-decoded-too-large", metadata, dataLength);
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Read(ref DataImageDecodeStartingForTests)?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = new byte[decodedByteLength];
            Volatile.Read(ref DataImageBytesAllocatedForTests)?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (!System.Convert.TryFromBase64Chars(base64Chars, bytes, out var bytesWritten) ||
                bytesWritten != decodedByteLength)
            {
                LogDataImageRejected("invalid-data-base64", metadata, dataLength);
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            imageRequest = new ImageRequest(sourceText, null, bytes, "data");
            return true;
        }
        catch (FormatException)
        {
            LogDataImageRejected("invalid-data-base64", metadata, dataLength);
            return false;
        }
    }

    private static int GetMaxBase64Length(int byteLimit)
    {
        return (int)Math.Min(int.MaxValue, ((long)byteLimit + 2) / 3 * 4);
    }

    private static bool TryGetDecodedBase64Length(
        ReadOnlySpan<char> base64Chars,
        out int decodedByteLength,
        CancellationToken cancellationToken = default)
    {
        // 与 Convert.TryFromBase64Chars 的空白兼容规则一致，先求精确输出长度再分配。
        var nonWhitespaceCount = 0;
        var lastNonWhitespace = '\0';
        var previousNonWhitespace = '\0';
        for (var index = 0; index < base64Chars.Length; index++)
        {
            if ((index & 0x0fff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var character = base64Chars[index];
            if (character is ' ' or '\t' or '\r' or '\n')
            {
                continue;
            }

            previousNonWhitespace = lastNonWhitespace;
            lastNonWhitespace = character;
            nonWhitespaceCount++;
        }

        if (nonWhitespaceCount == 0)
        {
            decodedByteLength = 0;
            return true;
        }

        if (nonWhitespaceCount % 4 != 0)
        {
            decodedByteLength = 0;
            return false;
        }

        var paddingCount = lastNonWhitespace == '='
            ? previousNonWhitespace == '=' ? 2 : 1
            : 0;
        decodedByteLength = checked(nonWhitespaceCount / 4 * 3 - paddingCount);
        return decodedByteLength >= 0;
    }

    private static void LogDataImageRejected(string reason, string metadata, int dataLength)
    {
        LogImageDiagnosticOnce(
            $"{reason}|{metadata}|{dataLength}",
            "image request rejected " +
            "sourceKind=data " +
            $"reason={reason} " +
            $"metadata={FormatLogValue(metadata)} " +
            $"dataLength={dataLength}");
    }

    private static bool TryCreatePackResourceRequest(string sourceText, out ImageRequest? imageRequest)
    {
        imageRequest = null;
        if (!sourceText.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryReadPackResourceBytes(sourceText, out var bytes))
        {
            imageRequest = new ImageRequest(sourceText, null, bytes, "pack");
            return true;
        }

        if (!Uri.TryCreate(sourceText, UriKind.Absolute, out var packUri) ||
            !string.Equals(packUri.Scheme, "pack", StringComparison.OrdinalIgnoreCase))
        {
            LogPackResourceFailure(sourceText, "invalid-pack-uri");
            return false;
        }

        LogPackResourceFailure(sourceText, "pack-resource-not-found");
        imageRequest = new ImageRequest(packUri.OriginalString, packUri, null, "pack");
        return true;
    }

    private static bool TryCreateLocalFileRequest(string sourceText, out ImageRequest? imageRequest)
    {
        imageRequest = null;
        if (!Path.IsPathRooted(sourceText) || IsRootRelativeApiPath(sourceText))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(sourceText);
            if (!Uri.TryCreate(fullPath, UriKind.Absolute, out var fileUri) || !fileUri.IsFile)
            {
                LogRejectedRequest(sourceText, "invalid-file-uri", "file");
                return false;
            }

            // 本地文件只在此建立地址，实际读取和解码统一由后台共享闸门执行。
            imageRequest = new ImageRequest(fullPath, fileUri, null, "file");
            return true;
        }
        catch (IOException)
        {
            LogRejectedRequest(sourceText, "file-io-error", "file");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            LogRejectedRequest(sourceText, "file-unauthorized", "file");
            return false;
        }
    }

    private static bool IsRootRelativeApiPath(string sourceText)
    {
        return sourceText.StartsWith("/", StringComparison.Ordinal) &&
                !sourceText.StartsWith("//", StringComparison.Ordinal) ||
            sourceText.StartsWith("\\", StringComparison.Ordinal) &&
                !sourceText.StartsWith("\\\\", StringComparison.Ordinal);
    }

    private static bool TryReadPackResourceBytes(Uri packUri, out byte[] bytes)
    {
        return TryReadPackResourceBytes(packUri.OriginalString, out bytes);
    }

    private static bool TryReadPackResourceBytes(string packUriText, out byte[] bytes)
    {
        bytes = [];
        try
        {
            if (Uri.TryCreate(packUriText, UriKind.Absolute, out var packUri))
            {
                var resourceStream = Application.GetResourceStream(packUri);
                if (resourceStream?.Stream is not null)
                {
                    using var stream = resourceStream.Stream;
                    bytes = ReadAllBytes(stream);
                    return bytes.Length > 0;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }

        return TryReadManifestPackResourceBytes(packUriText, out bytes);
    }

    private static bool TryReadManifestPackResourceBytes(string packUriText, out byte[] bytes)
    {
        bytes = [];
        if (!TryGetPackResourceParts(packUriText, out var assemblyName, out var resourcePath))
        {
            return false;
        }

        var assembly = ResolvePackResourceAssembly(assemblyName);
        if (assembly is null)
        {
            return false;
        }

        var resourceName = $"{assembly.GetName().Name}.g.resources";
        using var resourceStream = assembly.GetManifestResourceStream(resourceName);
        if (resourceStream is null)
        {
            return false;
        }

        var resourceKey = resourcePath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        using var reader = new ResourceReader(resourceStream);
        foreach (System.Collections.DictionaryEntry entry in reader)
        {
            if (!string.Equals(entry.Key as string, resourceKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bytes = entry.Value switch
            {
                byte[] byteArray => byteArray,
                Stream stream => ReadAllBytes(stream),
                _ => []
            };
            return bytes.Length > 0;
        }

        return false;
    }

    private static bool TryGetPackResourceParts(string packUriText, out string? assemblyName, out string resourcePath)
    {
        assemblyName = null;
        resourcePath = string.Empty;
        const string applicationPackPrefix = "pack://application:,,,/";
        if (!packUriText.StartsWith(applicationPackPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = Uri.UnescapeDataString(packUriText[applicationPackPrefix.Length..]).TrimStart('/');
        var componentIndex = path.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
        if (componentIndex >= 0)
        {
            assemblyName = path[..componentIndex];
            resourcePath = path[(componentIndex + ";component/".Length)..];
        }
        else
        {
            resourcePath = path;
        }

        return !string.IsNullOrWhiteSpace(resourcePath);
    }

    private static Assembly? ResolvePackResourceAssembly(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            return Application.ResourceAssembly ?? typeof(ProductThumbnailImageSourceConverter).Assembly;
        }

        var loadedAssembly = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(
                assembly.GetName().Name,
                assemblyName,
                StringComparison.OrdinalIgnoreCase));
        if (loadedAssembly is not null)
        {
            return loadedAssembly;
        }

        try
        {
            return Assembly.Load(new AssemblyName(assemblyName));
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static bool TryResolveRelativeApiPath(string sourceText, out Uri resolvedUri)
    {
        resolvedUri = default!;
        var normalizedPath = sourceText.Trim();
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        if (normalizedPath.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        normalizedPath = normalizedPath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        var baseUri = Volatile.Read(ref ApiBaseAddressProvider)();
        if (!Uri.TryCreate(baseUri, normalizedPath, out var createdUri) || createdUri is null)
        {
            return false;
        }

        resolvedUri = createdUri;
        return true;
    }

    private static void LogRemoteUriDiagnostics(string sourceText, Uri normalizedUri, Uri? originalUri)
    {
        var containsUnescapedHash = sourceText.Contains('#', StringComparison.Ordinal);
        var containsSpace = sourceText.Contains(' ', StringComparison.Ordinal);
        var hasFragment = !string.IsNullOrEmpty(originalUri?.Fragment);
        if (!containsUnescapedHash && !containsSpace && !hasFragment)
        {
            // 普通 URL 不再逐张记录"已解析"，只有需要转义/含片段的异常 URL 才值得留痕。
            return;
        }

        LogImageDiagnosticOnce(
            $"remote-uri|{sourceText}",
            "image uri parsed " +
            "sourceKind=http " +
            $"containsUnescapedHash={containsUnescapedHash.ToString().ToLowerInvariant()} " +
            $"containsSpace={containsSpace.ToString().ToLowerInvariant()} " +
            $"hasFragment={hasFragment.ToString().ToLowerInvariant()} " +
            $"value={FormatLogValue(sourceText)} " +
            $"resolvedUri={FormatLogValue(normalizedUri.AbsoluteUri)}");
    }

    private static bool TryDownloadRemoteImageBytes(ImageRequest imageRequest, out byte[] bytes)
    {
        return TryDownloadRemoteImageBytes(imageRequest, CancellationToken.None, out bytes);
    }

    private static bool TryDownloadRemoteImageBytes(
        ImageRequest imageRequest,
        CancellationToken cancellationToken,
        out byte[] bytes)
    {
        bytes = [];
        if (imageRequest.Uri is null)
        {
            return false;
        }

        Func<Uri, CancellationToken, Task<byte[]>> loader;
        lock (RemoteImageLoaderLock)
        {
            loader = RemoteImageBytesLoader;
        }

        using var timeout = new CancellationTokenSource(RemoteImageDownloadTimeout);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            bytes = loader(imageRequest.Uri, cancellation.Token).GetAwaiter().GetResult();
            if (bytes.Length <= 0)
            {
                LogRemoteDownloadFailure(imageRequest, "empty-response", null, stopwatch.ElapsedMilliseconds);
                return false;
            }

            LogRemoteDownloadSuccess(imageRequest, bytes.Length, stopwatch.ElapsedMilliseconds);
            return true;
        }
        catch (ImageInputTooLargeException ex)
        {
            LogRemoteDownloadFailure(imageRequest, ex.Reason, null, stopwatch.ElapsedMilliseconds);
            return false;
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            LogRemoteDownloadFailure(imageRequest, "timeout", ex, stopwatch.ElapsedMilliseconds);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            var reason = ex.StatusCode is null
                ? "http-request-failed"
                : $"http-{(int)ex.StatusCode.Value}";
            LogRemoteDownloadFailure(imageRequest, reason, ex, stopwatch.ElapsedMilliseconds);
            return false;
        }
        catch (IOException ex)
        {
            LogRemoteDownloadFailure(imageRequest, "io-error", ex, stopwatch.ElapsedMilliseconds);
            return false;
        }
        catch (Exception ex)
        {
            LogRemoteDownloadFailure(imageRequest, "download-exception", ex, stopwatch.ElapsedMilliseconds);
            return false;
        }
    }

    private static async Task<byte[]> DownloadRemoteImageBytesAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await RemoteImageHttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await ReadRemoteImageContentAsync(response.Content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadRemoteImageContentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var byteLimit = Volatile.Read(ref imageInputByteLimit);
        if (content.Headers.ContentLength is long contentLength && contentLength > byteLimit)
        {
            throw new ImageInputTooLargeException("content-length-too-large");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bufferLength = (int)Math.Min(81920L, (long)byteLimit + 1);
        using var bufferOwner = MemoryPool<byte>.Shared.Rent(bufferLength);
        using var memory = new MemoryStream();
        var totalRead = 0;
        while (true)
        {
            // 对未声明或错误声明 Content-Length 的响应也只读取到上限加一个探测字节。
            var readLength = (int)Math.Min(bufferOwner.Memory.Length, (long)byteLimit - totalRead + 1);
            var read = await stream.ReadAsync(bufferOwner.Memory[..readLength], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return memory.ToArray();
            }

            if (read > byteLimit - totalRead)
            {
                throw new ImageInputTooLargeException("stream-too-large");
            }

            memory.Write(bufferOwner.Memory.Span[..read]);
            totalRead += read;
        }
    }

    private static HttpClient CreateRemoteImageHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HBPOS-Client/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("image/*");
        return client;
    }

    private static void LogRemoteDownloadSuccess(ImageRequest imageRequest, int byteCount, long elapsedMilliseconds)
    {
        // 逐张成功不再单独记日志，只计入 5 分钟汇总。
        RecordRemoteDownloadOutcome(succeeded: true, byteCount, elapsedMilliseconds);
    }

    private static void LogRemoteDownloadFailure(
        ImageRequest imageRequest,
        string reason,
        Exception? exception,
        long elapsedMilliseconds)
    {
        RecordRemoteDownloadOutcome(succeeded: false, byteCount: 0, elapsedMilliseconds);
        // 去重键用 reason + host：同一 CDN 同一原因只记一次，避免每张图一条；URL 只记路径部分，不主动带查询串（COS 签名）。
        var host = imageRequest.Uri?.Host ?? "<none>";
        var uriWithoutQuery = imageRequest.Uri is null
            ? imageRequest.CacheKey
            : imageRequest.Uri.GetLeftPart(UriPartial.Path);
        if (!TryRememberDiagnostic($"download-failed|{reason}|{host}"))
        {
            return;
        }

        ConsoleLog.WriteWarning(
            "ProductImage",
            "image download failed " +
            $"sourceKind={imageRequest.SourceKind} " +
            $"reason={reason} " +
            $"host={host} " +
            $"uri={FormatLogValue(uriWithoutQuery)} " +
            $"elapsedMs={elapsedMilliseconds} " +
            $"error={exception?.GetType().Name ?? "<none>"}",
            new ApplicationLogContext(
                StatusCode: exception is HttpRequestException { StatusCode: { } status } ? (int)status : null,
                Properties: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["reason"] = reason,
                    ["elapsedMs"] = elapsedMilliseconds
                }),
            exception);
    }

    private static void RecordRemoteDownloadOutcome(bool succeeded, int byteCount, long elapsedMilliseconds)
    {
        int successCount;
        int failureCount;
        long elapsedTotal;
        long bytesTotal;
        long windowSeconds;
        lock (DownloadStatsLock)
        {
            var now = DownloadStatsClock();
            downloadStatsWindowStartUtc ??= now;
            if (succeeded)
            {
                downloadSuccessCount++;
                downloadBytesTotal += byteCount;
            }
            else
            {
                downloadFailureCount++;
            }

            downloadElapsedTotalMs += elapsedMilliseconds;
            if (now - downloadStatsWindowStartUtc.Value < DownloadSummaryInterval)
            {
                return;
            }

            successCount = downloadSuccessCount;
            failureCount = downloadFailureCount;
            elapsedTotal = downloadElapsedTotalMs;
            bytesTotal = downloadBytesTotal;
            windowSeconds = (long)(now - downloadStatsWindowStartUtc.Value).TotalSeconds;
            downloadStatsWindowStartUtc = now;
            downloadSuccessCount = 0;
            downloadFailureCount = 0;
            downloadElapsedTotalMs = 0;
            downloadBytesTotal = 0;
        }

        var totalCount = successCount + failureCount;
        var averageElapsedMs = totalCount == 0 ? 0 : elapsedTotal / totalCount;
        ConsoleLog.WriteInformation(
            "ProductImage",
            $"image download summary windowSeconds={windowSeconds} succeeded={successCount} failed={failureCount} avgElapsedMs={averageElapsedMs} bytes={bytesTotal}",
            new ApplicationLogContext(Properties: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["itemCount"] = totalCount,
                ["elapsedMs"] = averageElapsedMs
            }));
    }

    internal static IDisposable UseDownloadStatsClockForTests(Func<DateTimeOffset> clock)
    {
        lock (DownloadStatsLock)
        {
            var previous = DownloadStatsClock;
            DownloadStatsClock = clock;
            downloadStatsWindowStartUtc = null;
            downloadSuccessCount = 0;
            downloadFailureCount = 0;
            downloadElapsedTotalMs = 0;
            downloadBytesTotal = 0;
            return new DownloadStatsClockScope(previous);
        }
    }

    private static void LogRejectedRequest(string sourceText, string reason, string sourceKind)
    {
        LogImageDiagnosticOnce(
            $"{reason}|{sourceText}",
            "image request rejected " +
            $"sourceKind={sourceKind} " +
            $"reason={reason} " +
            $"value={FormatLogValue(sourceText)}");
    }

    private static void LogPackResourceFailure(string sourceText, string reason)
    {
        var hasParts = TryGetPackResourceParts(sourceText, out var assemblyName, out var resourcePath);
        LogImageDiagnosticOnce(
            $"{reason}|{sourceText}",
            "image request rejected " +
            "sourceKind=pack " +
            $"reason={reason} " +
            $"assembly={FormatLogValue(hasParts ? assemblyName ?? "<application>" : "<unknown>")} " +
            $"resource={FormatLogValue(hasParts ? resourcePath : "<unknown>")} " +
            $"value={FormatLogValue(sourceText)}");
    }

    private static string InferSourceKind(string sourceText)
    {
        if (sourceText.StartsWith(DataImagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "data";
        }

        if (sourceText.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
        {
            return "pack";
        }

        if (Uri.TryCreate(sourceText, UriKind.Absolute, out var uri))
        {
            return uri.Scheme switch
            {
                "http" or "https" => "http",
                "file" => "file",
                _ => "unsupported"
            };
        }

        return "unknown";
    }

    private static void LogImageDiagnosticOnce(string key, string message)
    {
        if (TryRememberDiagnostic(key))
        {
            ConsoleLog.Write("ProductImage", message);
        }
    }

    private static string FormatLogValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var normalized = value.Replace(Environment.NewLine, " ", StringComparison.Ordinal);
        if (normalized.Length > MaxLoggedValueLength)
        {
            normalized = normalized[..MaxLoggedValueLength] + "...";
        }

        return $"\"{normalized}\"";
    }

    private enum CacheLoadDisposition
    {
        Miss,
        Cached,
        Failed,
        WaitForInFlight,
        Producer
    }

    private readonly record struct CacheLoadDecision(
        CacheLoadDisposition Disposition,
        ImageSource? CachedImage,
        Task<ImageSource?>? InFlightCompletion,
        InFlightImageLoadState? ProducerState);

    private sealed class InFlightImageLoadState
    {
        public int ActiveProducerCount { get; set; }

        public TaskCompletionSource<ImageSource?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record ImageRequest(string CacheKey, Uri? Uri, byte[]? ImageBytes, string SourceKind)
    {
        public bool CanCache =>
            ImageBytes is not null ||
            string.Equals(SourceKind, "data", StringComparison.Ordinal) ||
            IsRemoteUri ||
            IsLocalFileUri;

        public bool IsLocalFileUri =>
            Uri is not null &&
            string.Equals(Uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase);

        public bool IsRemoteUri =>
            Uri is not null &&
            (string.Equals(Uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CacheLimitsScope(
        int previousImageCacheCapacity,
        int previousFailedCacheCapacity,
        int previousLoggedDiagnosticCapacity) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (CacheLock)
            {
                imageCacheCapacity = previousImageCacheCapacity;
                failedCacheCapacity = previousFailedCacheCapacity;
                loggedDiagnosticCapacity = previousLoggedDiagnosticCapacity;
                ClearCachesUnsafe();
            }
        }
    }

    private sealed class RemoteImageLoaderScope(Func<Uri, CancellationToken, Task<byte[]>> previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            lock (RemoteImageLoaderLock)
            {
                RemoteImageBytesLoader = previous;
            }

            _disposed = true;
        }
    }

    private sealed class ImageInputTooLargeException(string reason) : IOException
    {
        public string Reason { get; } = reason;
    }

    private sealed class ImageInputByteLimitScope(int previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (RemoteImageLoaderLock)
            {
                imageInputByteLimit = previous;
            }
        }
    }

    private sealed class DataImageDecodeStartingScope(
        Action? previous,
        Action? previousBytesAllocated) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (RemoteImageLoaderLock)
            {
                DataImageDecodeStartingForTests = previous;
                DataImageBytesAllocatedForTests = previousBytesAllocated;
            }
        }
    }

    private sealed class CacheKeyUtf8ChunkScope(Action<int>? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (RemoteImageLoaderLock)
            {
                CacheKeyUtf8ChunkForTests = previous;
            }
        }
    }

    private sealed class DownloadStatsClockScope(Func<DateTimeOffset> previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (DownloadStatsLock)
            {
                DownloadStatsClock = previous;
                downloadStatsWindowStartUtc = null;
                downloadSuccessCount = 0;
                downloadFailureCount = 0;
                downloadElapsedTotalMs = 0;
                downloadBytesTotal = 0;
            }
        }
    }

    private sealed class ApiBaseAddressProviderScope(Func<Uri> previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (RemoteImageLoaderLock)
            {
                ApiBaseAddressProvider = previous;
            }
        }
    }
}

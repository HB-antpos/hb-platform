namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>设置分区的保存状态。</summary>
public enum SettingsSaveState
{
    /// <summary>没有未保存的修改。</summary>
    Saved,

    /// <summary>已修改，正在等防抖时间到了再保存（例如文本框停顿 1.5 秒）。</summary>
    Pending,

    /// <summary>正在写入。</summary>
    Saving,

    /// <summary>自动保存失败，可以重试。</summary>
    Failed,

    /// <summary>
    /// 已修改但不会自动保存，要用户在本页点显式按钮（例如「启用 Linkly」「保存地址」）才生效：
    /// 这类设置会影响收款或切换服务器，必须经过测试 / 授权，不能在失焦时悄悄写入。
    /// </summary>
    Draft
}

/// <summary>一次自动保存的结果。</summary>
public readonly record struct SettingsSaveResult(bool Succeeded, string? Message)
{
    public static SettingsSaveResult Success { get; } = new(true, null);

    public static SettingsSaveResult Failure(string? message) => new(false, message);
}

/// <summary>某个分区在某一时刻的保存状态快照（用于页面状态、导航角标和离开提示）。</summary>
public sealed record SettingsSaveEntry(
    string Key,
    SettingsCategory Category,
    string LabelKey,
    SettingsSaveState State,
    string? Message);

/// <summary>
/// 设置页自动保存的状态机（不依赖 WPF，便于单测）。
/// 每个分区（Key）登记一个保存委托：改动后按防抖时间调度保存；保存进行中又有改动时，等这次写完再补存一次，
/// 保证最后一次修改一定落盘。显式保存类分区只记「草稿」，由页面上的按钮负责真正保存。
/// 所有公开方法都应在同一个线程（WPF UI 线程）上调用；内部仍加锁，测试里在线程池上调用也安全。
/// </summary>
public sealed class SettingsAutoSaveTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Section> _sections = new(StringComparer.Ordinal);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public SettingsAutoSaveTracker(Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _delay = delay ?? Task.Delay;
    }

    /// <summary>任一分区状态变化时触发（可能在保存完成的续体里触发）。</summary>
    public event EventHandler? Changed;

    /// <summary>登记一个自动保存分区。</summary>
    public void Register(
        string key,
        SettingsCategory category,
        string labelKey,
        Func<CancellationToken, Task<SettingsSaveResult>> save)
    {
        lock (_gate)
        {
            _sections[key] = new Section(key, category, labelKey, save);
        }
    }

    /// <summary>登记一个显式保存分区（只跟踪草稿，不自动写入）。</summary>
    public void RegisterExplicit(string key, SettingsCategory category, string labelKey)
    {
        lock (_gate)
        {
            _sections[key] = new Section(key, category, labelKey, save: null);
        }
    }

    /// <summary>
    /// 某分区有修改：自动保存分区在 <paramref name="debounce"/> 后保存（期间再改会重新计时）；
    /// 显式保存分区记为草稿。
    /// </summary>
    public void MarkChanged(string key, TimeSpan debounce)
    {
        Section section;
        CancellationTokenSource? previousDelay;
        lock (_gate)
        {
            if (!_sections.TryGetValue(key, out section!))
            {
                return;
            }

            if (section.Save is null)
            {
                section.State = SettingsSaveState.Draft;
                section.Message = null;
                previousDelay = null;
            }
            else
            {
                section.Version++;
                previousDelay = section.DelayCts;
                section.DelayCts = null;
                if (section.Running is not null)
                {
                    // 正在写入：写完后由 RunAsync 发现版本变了再补存一次，这里只标记。
                    section.RerunRequested = true;
                }
                else
                {
                    section.State = SettingsSaveState.Pending;
                    section.Message = null;
                    if (debounce <= TimeSpan.Zero)
                    {
                        section.Running = RunAsync(section);
                    }
                    else
                    {
                        var cts = new CancellationTokenSource();
                        section.DelayCts = cts;
                        _ = DelayThenRunAsync(section, debounce, cts.Token);
                    }
                }
            }
        }

        previousDelay?.Cancel();
        previousDelay?.Dispose();
        RaiseChanged();
    }

    /// <summary>显式保存成功、或数据已从存储重新加载：该分区回到「已保存」，并丢弃还没执行的防抖保存。</summary>
    public void MarkSaved(string key)
    {
        CancellationTokenSource? delay;
        lock (_gate)
        {
            if (!_sections.TryGetValue(key, out var section))
            {
                return;
            }

            delay = section.DelayCts;
            section.DelayCts = null;
            section.RerunRequested = false;
            section.State = section.Running is not null ? SettingsSaveState.Saving : SettingsSaveState.Saved;
            section.Message = null;
        }

        delay?.Cancel();
        delay?.Dispose();
        RaiseChanged();
    }

    /// <summary>放弃全部未保存修改（调用方负责把界面恢复成存储里的值）。正在写入的会照常写完。</summary>
    public void ResetAll()
    {
        foreach (var key in Keys())
        {
            MarkSaved(key);
        }
    }

    public SettingsSaveState GetState(string key)
    {
        lock (_gate)
        {
            return _sections.TryGetValue(key, out var section) ? section.State : SettingsSaveState.Saved;
        }
    }

    public string? GetMessage(string key)
    {
        lock (_gate)
        {
            return _sections.TryGetValue(key, out var section) ? section.Message : null;
        }
    }

    /// <summary>所有分区的当前状态（按登记顺序）。</summary>
    public IReadOnlyList<SettingsSaveEntry> Snapshot()
    {
        lock (_gate)
        {
            return _sections.Values
                .Select(s => new SettingsSaveEntry(s.Key, s.Category, s.LabelKey, s.State, s.Message))
                .ToArray();
        }
    }

    /// <summary>离开时要提示的分区：保存失败或草稿。</summary>
    public IReadOnlyList<SettingsSaveEntry> Unsaved() =>
        Snapshot().Where(e => e.State is SettingsSaveState.Failed or SettingsSaveState.Draft).ToArray();

    /// <summary>是否还有在等防抖或正在写入的保存。</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _sections.Values.Any(s => s.State is SettingsSaveState.Pending or SettingsSaveState.Saving);
            }
        }
    }

    /// <summary>
    /// 立即执行所有在等防抖的保存，并等待在途保存完成（最多 <paramref name="timeout"/>）。
    /// 返回 true 表示在时限内全部结束（成功或失败都算结束）。
    /// </summary>
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var waiting = new List<Task>();
            var toCancel = new List<CancellationTokenSource>();
            lock (_gate)
            {
                foreach (var section in _sections.Values)
                {
                    if (section.State == SettingsSaveState.Pending && section.Running is null && section.Save is not null)
                    {
                        if (section.DelayCts is { } cts)
                        {
                            toCancel.Add(cts);
                            section.DelayCts = null;
                        }

                        section.Running = RunAsync(section);
                    }

                    if (section.Running is { } running)
                    {
                        waiting.Add(running);
                    }
                }
            }

            foreach (var cts in toCancel)
            {
                cts.Cancel();
                cts.Dispose();
            }

            if (waiting.Count == 0)
            {
                return true;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            var all = Task.WhenAll(waiting);
            var finished = await Task.WhenAny(all, Task.Delay(remaining)).ConfigureAwait(true);
            if (finished != all)
            {
                return false;
            }
        }
    }

    /// <summary>重试所有保存失败的分区；返回 true 表示重试后没有失败项。</summary>
    public async Task<bool> RetryFailedAsync()
    {
        var running = new List<Task>();
        lock (_gate)
        {
            foreach (var section in _sections.Values.Where(s => s.State == SettingsSaveState.Failed && s.Save is not null))
            {
                section.Version++;
                section.State = SettingsSaveState.Pending;
                section.Message = null;
                section.Running ??= RunAsync(section);
                running.Add(section.Running);
            }
        }

        RaiseChanged();
        await Task.WhenAll(running).ConfigureAwait(true);
        lock (_gate)
        {
            return !_sections.Values.Any(s => s.State == SettingsSaveState.Failed);
        }
    }

    private IReadOnlyList<string> Keys()
    {
        lock (_gate)
        {
            return _sections.Keys.ToArray();
        }
    }

    private async Task DelayThenRunAsync(Section section, TimeSpan debounce, CancellationToken cancellationToken)
    {
        try
        {
            await _delay(debounce, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested || section.Running is not null || section.State != SettingsSaveState.Pending)
            {
                return;
            }

            section.DelayCts?.Dispose();
            section.DelayCts = null;
            section.Running = RunAsync(section);
        }
    }

    private async Task RunAsync(Section section)
    {
        // 让出一次，保证调用方先拿到 Running 再进入保存（避免同步完成的保存在锁内重入）。
        await Task.Yield();
        while (true)
        {
            int version;
            lock (_gate)
            {
                version = section.Version;
                section.RerunRequested = false;
                section.State = SettingsSaveState.Saving;
            }

            RaiseChanged();
            SettingsSaveResult result;
            try
            {
                result = await section.Save!(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                result = SettingsSaveResult.Failure(ex.Message);
            }

            lock (_gate)
            {
                if (section.Version != version || section.RerunRequested)
                {
                    // 写入期间又改过：无论这次成败，都要按最新内容再写一次。
                    continue;
                }

                section.State = result.Succeeded ? SettingsSaveState.Saved : SettingsSaveState.Failed;
                section.Message = result.Succeeded ? null : result.Message;
                section.Running = null;
            }

            RaiseChanged();
            return;
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed class Section(
        string key,
        SettingsCategory category,
        string labelKey,
        Func<CancellationToken, Task<SettingsSaveResult>>? save)
    {
        public string Key { get; } = key;

        public SettingsCategory Category { get; } = category;

        public string LabelKey { get; } = labelKey;

        public Func<CancellationToken, Task<SettingsSaveResult>>? Save { get; } = save;

        public SettingsSaveState State { get; set; } = SettingsSaveState.Saved;

        public string? Message { get; set; }

        public int Version { get; set; }

        public bool RerunRequested { get; set; }

        public CancellationTokenSource? DelayCts { get; set; }

        public Task? Running { get; set; }
    }
}

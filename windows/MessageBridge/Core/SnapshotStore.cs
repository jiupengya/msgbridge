using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace MessageBridge.Core;

/// <summary>
/// 手机推来的一次通知变化（新增 / 更新 / 取消）。
/// 和 <see cref="MessageItem"/> 分开：这个只服务于「实时快照面板」，不落盘。
/// </summary>
public sealed class NotificationUpdate
{
    /// <summary>手机端通知 key，快照用它判断新增还是更新。</summary>
    public string Key { get; init; } = "";
    public bool IsCancel { get; init; }
    /// <summary>连接建立后的批量补发（不弹 Toast、不写历史）。</summary>
    public bool IsSync { get; init; }
    /// <summary>常驻通知（cancelAllNotifications 清不掉，清空后仍留在快照里）。</summary>
    public bool IsOngoing { get; init; }

    /// <summary>
    /// 来源显示名。连接后批量补发时手机端偶尔拿不到应用名，会退化成包名，
    /// 这时由电脑端用之前正常推送时记住的「包名 → 应用名」补全（所以可写）。
    /// </summary>
    public string Source { get; set; } = "";

    public string Title { get; init; } = "";
    public string Content { get; init; } = "";
    public long Time { get; init; }

    /// <summary>原始 base64 图标；补发时可能为空，电脑端会退回上次记住的图标文件。</summary>
    public string IconBase64 { get; set; } = "";

    /// <summary>应用包名，用来查「包名 → 应用名/图标」的缓存。</summary>
    public string PackageName { get; init; } = "";
}

/// <summary>快照面板里的一条「手机当前存活通知」。</summary>
public sealed class SnapshotItem : INotifyPropertyChanged
{
    public string Key { get; init; } = "";
    /// <summary>常驻通知标记，用于面板上的「常驻」角标（当前手机端不推常驻通知，恒为 false）。</summary>
    public bool IsOngoing { get; init; }

    private string _source = "";
    public string Source { get => _source; set => Set(ref _source, value); }

    private string _title = "";
    public string Title { get => _title; set => Set(ref _title, value); }

    private string _content = "";
    public string Content { get => _content; set => Set(ref _content, value); }

    private string _timeText = "";
    public string TimeText { get => _timeText; set => Set(ref _timeText, value); }

    private ImageSource? _iconImage;
    public ImageSource? IconImage { get => _iconImage; set => Set(ref _iconImage, value); }

    /// <summary>图标文件名（icons 目录下），给 MessageStore 的裁剪逻辑保留用。</summary>
    public string IconFile { get; set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// 实时通知快照：只保存手机「此刻还挂着」的通知，断连即清空，不写磁盘。
/// 和 messages.json 的历史记录完全独立 —— 历史只增不删，快照随通知存亡。
/// </summary>
public sealed class SnapshotStore
{
    public ObservableCollection<SnapshotItem> Items { get; } = new();

    private readonly Dictionary<string, SnapshotItem> _index = new(StringComparer.Ordinal);

    /// <summary>快照内容发生任何变化时触发（面板据此刷新标题/空态）。</summary>
    public event Action? Changed;

    /// <summary>快照里引用的图标文件名，供 IconStore 裁剪时保留。</summary>
    public IEnumerable<string> IconFiles => _index.Values.Select(i => i.IconFile);

    public int Count => _index.Count;

    /// <summary>新增或更新一条通知（同 key 就地更新，不改变列表位置）。</summary>
    /// <param name="fallbackIconFile">
    /// 补发包里没带图标时用的兜底图标文件名（电脑端按包名记下的上次图标）。
    /// </param>
    public void Upsert(NotificationUpdate update, Services.IconStore icons, string? fallbackIconFile = null)
    {
        if (string.IsNullOrEmpty(update.Key)) return;

        if (!_index.TryGetValue(update.Key, out var item))
        {
            item = new SnapshotItem
            {
                Key = update.Key,
                IsOngoing = update.IsOngoing
            };
            _index[update.Key] = item;
            // 最新的排在最上面
            Items.Insert(0, item);
        }

        item.Source = update.Source;
        item.Title = update.Title;
        item.Content = update.Content;
        item.TimeText = DateTimeOffset.FromUnixTimeMilliseconds(
            update.Time > 0 ? update.Time : DateTimeOffset.Now.ToUnixTimeMilliseconds())
            .ToLocalTime().ToString("HH:mm:ss");

        // 更新包通常也会带图标；没带就沿用上一次的，再不行用兜底图标
        if (!string.IsNullOrEmpty(update.IconBase64))
        {
            var file = icons.Save(update.IconBase64);
            if (!string.IsNullOrEmpty(file))
            {
                item.IconFile = file;
                item.IconImage = Services.IconStore.LoadBitmap(icons.PathOf(file));
            }
        }
        else if (item.IconImage == null && !string.IsNullOrEmpty(fallbackIconFile))
        {
            item.IconFile = fallbackIconFile;
            item.IconImage = Services.IconStore.LoadBitmap(icons.PathOf(fallbackIconFile));
        }

        Changed?.Invoke();
    }

    /// <summary>手机端通知被移除（isCancel）。</summary>
    public void Remove(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!_index.Remove(key, out var item)) return;
        Items.Remove(item);
        Changed?.Invoke();
    }

    /// <summary>断连 / 手动清空。</summary>
    public void Clear()
    {
        if (_index.Count == 0)
        {
            Items.Clear();
            return;
        }
        _index.Clear();
        Items.Clear();
        Changed?.Invoke();
    }
}

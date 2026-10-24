using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MessageBridge.Services;

namespace MessageBridge.Core;

/// <summary>
/// 一条推送到电脑的通知消息。
/// </summary>
public sealed class MessageItem
{
    public string Kind { get; init; } = "notification";
    public long Time { get; init; }
    public string Source { get; init; } = "";
    public string Title { get; init; } = "";
    public string Content { get; init; } = "";

    /// <summary>手机端标记的静默通知（不响铃、不弹横幅的那类）。</summary>
    public bool Silent { get; init; }

    /// <summary>图标文件名（icons 目录下），持久化用。</summary>
    public string IconFile { get; set; } = "";

    /// <summary>图标绝对路径（运行时用，不持久化）。</summary>
    [JsonIgnore]
    public string IconPath { get; private set; } = "";

    /// <summary>收到该消息的时间（用于合并连续通知，不持久化）。</summary>
    [JsonIgnore]
    public DateTime ReceivedAt { get; set; } = DateTime.Now;

    /// <summary>手机传来的原始图标 base64，仅在落盘前短暂使用。</summary>
    [JsonIgnore]
    public string IconBase64 { get; set; } = "";

    private BitmapImage? _iconImage;
    private bool _iconLoaded;

    public string TypeText => "通知";

    [JsonIgnore]
    public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(Time).ToLocalTime().ToString("MM-dd HH:mm:ss");

    /// <summary>
    /// 通知应用图标：只从文件解码一次并缓存。
    /// 之前每次访问都重新 base64 解码 + 新建 BitmapImage，列表滚动一次就重复解码一次。
    /// </summary>
    [JsonIgnore]
    public BitmapImage? IconImage
    {
        get
        {
            if (_iconLoaded) return _iconImage;
            _iconLoaded = true;
            _iconImage = LoadIcon(IconPath);
            return _iconImage;
        }
    }

    /// <summary>设置图标文件路径；路径变化时让缓存失效。</summary>
    public void SetIconPath(string path)
    {
        if (string.Equals(IconPath, path, StringComparison.OrdinalIgnoreCase)) return;
        IconPath = path;
        _iconLoaded = false;
        _iconImage = null;
    }

    private static BitmapImage? LoadIcon(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 读完即释放文件，之后可被清理
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 64;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static MessageItem FromNotification(JsonObject body)
    {
        return new MessageItem
        {
            Kind = "notification",
            Time = body["time"]?.GetValue<long>() ?? DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            Source = body["appName"]?.GetValue<string>() ?? body["packageName"]?.GetValue<string>() ?? "未知应用",
            Title = body["title"]?.GetValue<string>() ?? "",
            Content = body["text"]?.GetValue<string>() ?? "",
            Silent = body["silent"]?.GetValue<bool>() ?? false,
            IconBase64 = body["icon"]?.GetValue<string>() ?? ""
        };
    }
}

/// <summary>
/// 消息存储：内存集合 + 持久化到 messages.json（保留最近 MaxCount 条）。
/// 图标只存文件名，写盘做 600ms 防抖，避免通知密集时反复全量重写。
/// </summary>
public sealed class MessageStore
{
    /// <summary>只保留最近的通知条数（历史文件加载时也会按此截断）。</summary>
    public const int MaxCount = 50;

    private readonly string _path;
    private readonly IconStore _icons;
    private DispatcherTimer? _saveTimer;
    private bool _dirty;

    public ObservableCollection<MessageItem> Messages { get; } = new();

    public MessageStore(string dataDir, IconStore icons)
    {
        _path = Path.Combine(dataDir, "messages.json");
        _icons = icons;
        Load();
    }

    /// <summary>持久化结构（和运行时对象分开，避免把 base64 再写回文件）。</summary>
    private sealed class StoredItem
    {
        public string Kind { get; set; } = "notification";
        public long Time { get; set; }
        public string Source { get; set; } = "";
        public string Title { get; set; } = "";
        public string Content { get; set; } = "";
        public bool Silent { get; set; }
        public string IconFile { get; set; } = "";

        /// <summary>旧版本把图标 base64 直接存在 JSON 里，读取时迁移到 icons 目录。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? IconBase64 { get; set; }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<StoredItem>>(File.ReadAllText(_path));
            if (list == null) return;

            // 旧版本可能存了很多条，只保留最新的 MaxCount 条
            var start = Math.Max(0, list.Count - MaxCount);
            var migrated = false;

            foreach (var s in list.Skip(start))
            {
                var item = new MessageItem
                {
                    Kind = string.IsNullOrEmpty(s.Kind) ? "notification" : s.Kind,
                    Time = s.Time,
                    Source = s.Source,
                    Title = s.Title,
                    Content = s.Content,
                    Silent = s.Silent,
                    IconFile = s.IconFile,
                    ReceivedAt = DateTimeOffset.FromUnixTimeMilliseconds(s.Time).LocalDateTime
                };

                // 旧数据的图标是大段 base64：落盘一次，之后只留文件名
                if (string.IsNullOrEmpty(item.IconFile) && !string.IsNullOrEmpty(s.IconBase64))
                {
                    item.IconFile = _icons.Save(s.IconBase64) ?? "";
                    migrated = true;
                }

                item.SetIconPath(_icons.PathOf(item.IconFile));
                Messages.Add(item);
            }

            if (start > 0 || migrated) ScheduleSave();
        }
        catch
        {
            // 损坏的历史文件忽略
        }
    }

    public void Add(MessageItem item)
    {
        Messages.Add(item);
        while (Messages.Count > MaxCount)
            Messages.RemoveAt(0);
        ScheduleSave();
    }

    public void Clear()
    {
        Messages.Clear();
        _dirty = true;
        Save(); // 立刻清空文件并顺手清理图标
    }

    /// <summary>立即落盘（退出前调用）。</summary>
    public void Flush()
    {
        _saveTimer?.Stop();
        if (_dirty) Save();
    }

    private void ScheduleSave()
    {
        _dirty = true;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            Save();
            return;
        }

        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer?.Stop();
                Save();
            };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save()
    {
        try
        {
            var snapshot = Messages.Select(m => new StoredItem
            {
                Kind = m.Kind,
                Time = m.Time,
                Source = m.Source,
                Title = m.Title,
                Content = m.Content,
                Silent = m.Silent,
                IconFile = m.IconFile
            }).ToList();

            var json = JsonSerializer.Serialize(snapshot);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
            _dirty = false;

            // 只保留仍被消息引用的图标
            _icons.Trim(Messages.Select(m => m.IconFile));
        }
        catch
        {
            // 写失败不影响运行
        }
    }
}

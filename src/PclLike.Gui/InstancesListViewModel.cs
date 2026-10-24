using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using PclLike.Core.Storage;

namespace PclLike.Gui;

/// <summary>
/// 实例列表里的一行。
///
/// 为什么要包一层：Core 的 GameInstance 是纯数据，而这一屏要额外给界面三样东西 ——
/// 版本/加载器两枚芯片的文字、按加载器上色的方块图标、以及搜索用的小写字串。
/// 把画刷塞进 Core 的类型里是不行的（Core 不引用 WPF）。
/// </summary>
public sealed class InstanceRow
{
    /// <summary>从版本号里挑出游戏版本。取最后一个 1.x —— 因为
    /// `fabric-loader-0.20.1-1.20.1` 里第一个 1.x 其实不存在，而
    /// `forge-1.20.1-47.4.0` 第一个就是对的，取最后一个对这两种都成立。</summary>
    private static readonly Regex McVersionPattern =
        new(@"(?<!\d)(1\.\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private static readonly Dictionary<string, Brush> BrushCache = new();

    public InstanceRow(GameInstance instance)
    {
        Instance = instance;

        var matches = McVersionPattern.Matches(instance.VersionId ?? "");
        McVersion = matches.Count > 0 ? matches[^1].Value : (instance.VersionId ?? "未知");

        Loader = SniffLoader(instance);
        LoaderText = Loader switch
        {
            "fabric" => "Fabric",
            "quilt" => "Quilt",
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            _ => "原版",
        };

        (Top, Left, Right) = Loader switch
        {
            "fabric" => (Pick("#D8BF8A"), Pick("#B79A63"), Pick("#96794A")),
            "quilt" => (Pick("#B9A0E2"), Pick("#9678C6"), Pick("#7A5CA6")),
            "forge" => (Pick("#8E96A8"), Pick("#697185"), Pick("#4E5564")),
            "neoforge" => (Pick("#E8A45E"), Pick("#C87F39"), Pick("#A3621F")),
            _ => (Pick("#7FCB8C"), Pick("#57AA6A"), Pick("#3E8C50")),
        };

        Blob = string.Join(' ',
            instance.Name, instance.VersionId, LoaderText, instance.GameDir, instance.Note ?? "")
            .ToLowerInvariant();
    }

    /// <summary>底下的 GameInstance。要拿 Id / GameDir 这类真实字段时用它。</summary>
    public GameInstance Instance { get; }

    public string Name => Instance.Name;

    /// <summary>芯片一：游戏版本，例如 1.20.1。</summary>
    public string McVersion { get; }

    /// <summary>芯片二：加载器，认不出来就是"原版"。</summary>
    public string LoaderText { get; }

    public string Loader { get; }

    /// <summary>
    /// 同名实例才填：写上实例目录名。
    ///
    /// 为什么需要它：复制实例出来的那份默认叫「X - 副本」，但用户手动改名之后
    /// 完全可能两条都叫「Fabric测试」—— 界面上就成了两行一模一样的东西，
    /// 用户根本点不准自己要的那个。目录名在实例范围内唯一，拿来区分最合适。
    /// </summary>
    public string? DuplicateId { get; set; }

    public Visibility DuplicateVisibility
        => DuplicateId is null ? Visibility.Collapsed : Visibility.Visible;

    public Brush Top { get; }
    public Brush Left { get; }
    public Brush Right { get; }

    /// <summary>搜索用：小写拼起来的一整串。</summary>
    internal string Blob { get; }

    /// <summary>
    /// 认加载器。
    ///
    /// 不能只看 GameInstance.Loader —— 那个字段是创建实例那一刻猜的，之后用户拿
    /// HMCL 装了个 Fabric 版本再指过来，它还是写着 vanilla（实测就是这样：一个
    /// `fabric-loader-0.20.1-1.20.1` 的实例，Loader 字段是 vanilla）。
    /// 版本号是实打实的，所以两个都看，版本号优先。
    /// </summary>
    private static string SniffLoader(GameInstance instance)
    {
        var hay = $"{instance.Loader} {instance.VersionId}".ToLowerInvariant();

        if (hay.Contains("neoforge")) return "neoforge";
        if (hay.Contains("forge")) return "forge";        // 放在 neoforge 后面
        if (hay.Contains("fabric")) return "fabric";
        if (hay.Contains("quilt")) return "quilt";
        return "vanilla";
    }

    /// <summary>颜色串只解析一次，之后所有行共用同一批冻结画刷。</summary>
    private static Brush Pick(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var cached)) return cached;

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        BrushCache[hex] = brush;
        return brush;
    }
}

/// <summary>
/// 一组实例（独立 / 共享）。PCL CE 的实例列表是分组带折叠的，这里照做。
/// </summary>
public sealed class InstanceGroup : ObservableObject
{
    private readonly Action<InstanceGroup, InstanceRow?> _onSelected;
    private InstanceRow? _selected;
    private bool _expanded = true;
    private bool _muteSelection;

    public InstanceGroup(string title, string subtitle, Action<InstanceGroup, InstanceRow?> onSelected)
    {
        Title = title;
        Subtitle = subtitle;
        _onSelected = onSelected;
    }

    public string Title { get; }

    /// <summary>组标题右边那句小字，说明这一组到底是什么意思。</summary>
    public string Subtitle { get; }

    public ObservableCollection<InstanceRow> Rows { get; } = new();

    public int Count => Rows.Count;

    public string HeaderText => $"{Title} ({Count})";

    /// <summary>空组整块藏起来，包括那条标题。</summary>
    public Visibility GroupVisibility => Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (!Set(ref _expanded, value)) return;
            OnPropertyChanged(nameof(RowsVisibility));
            OnPropertyChanged(nameof(Chevron));
        }
    }

    public Visibility RowsVisibility => _expanded ? Visibility.Visible : Visibility.Collapsed;

    public string Chevron => _expanded ? "\uE70D" : "\uE76C";

    public InstanceRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            if (!_muteSelection) _onSelected(this, value);
        }
    }

    /// <summary>不触发回调地清掉选中 —— 列表重建时用，否则会被当成"用户取消选择"。</summary>
    internal void ClearSelectionSilently()
    {
        _muteSelection = true;
        Selected = null;
        _muteSelection = false;
    }

    public void Reset(IEnumerable<InstanceRow> rows)
    {
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(GroupVisibility));
    }
}

/// <summary>
/// 左栏那一整块：搜索 + 两组实例。
///
/// 选中状态放在这里而不是两个 ListBox 里 —— 列表分成两个 ListBox（一组一个），
/// 各自独立选中就会出现"两组同时高亮"，所以由这里统一裁决。
/// </summary>
public sealed class InstancesListViewModel : ObservableObject
{
    private string _filter = "";
    private InstanceRow? _selectedRow;
    private IReadOnlyList<GameInstance> _source = Array.Empty<GameInstance>();

    public InstancesListViewModel()
    {
        Isolated = new InstanceGroup("独立实例", "只属于这个实例", OnGroupSelected);
        Shared = new InstanceGroup("共享实例", "与其它版本共用", OnGroupSelected);

        Groups = new ObservableCollection<InstanceGroup> { Isolated, Shared };
    }

    public InstanceGroup Isolated { get; }
    public InstanceGroup Shared { get; }
    public ObservableCollection<InstanceGroup> Groups { get; }

    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value)) return;
            Rebuild();
        }
    }

    public InstanceRow? SelectedRow
    {
        get => _selectedRow;
        private set => Set(ref _selectedRow, value);
    }

    public Visibility EmptyVisibility
        => Isolated.Count + Shared.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string EmptyText
        => _filter.Trim() is { Length: > 0 } q ? $"没有匹配「{q}」的实例" : "还没有实例，先在下面新建一个";

    /// <summary>
    /// 重建列表。传参就给新数据，不传就按现有数据重来一遍（搜索框变了走这条）。
    /// </summary>
    public void Rebuild(IReadOnlyList<GameInstance>? instances = null)
    {
        if (instances is not null) _source = instances;

        var keepId = SelectedRow?.Instance.Id ?? _lastId;
        _lastId = keepId;

        var query = _filter.Trim().ToLowerInvariant();

        var rows = new List<InstanceRow>(_source.Count);
        foreach (var inst in _source)
        {
            if (query.Length > 0)
            {
                var blob = $"{inst.Name} {inst.VersionId} {inst.GameDir} {inst.Note}".ToLowerInvariant();
                if (!blob.Contains(query)) continue;
            }

            rows.Add(new InstanceRow(inst));
        }

        Isolated.ClearSelectionSilently();
        Shared.ClearSelectionSilently();
        SelectedRow = null;

        // 重名的行补上实例目录名，否则两行长得一模一样、点不准。
        // 目录名跟显示名相同的那个不补 —— 重复写一遍反而更吵。
        foreach (var dup in rows.GroupBy(r => r.Name).Where(g => g.Count() > 1))
            foreach (var r in dup)
                if (!string.Equals(r.Instance.Id, r.Name, StringComparison.OrdinalIgnoreCase))
                    r.DuplicateId = r.Instance.Id;

        Isolated.Reset(rows.Where(r => r.Instance.Isolated));
        Shared.Reset(rows.Where(r => !r.Instance.Isolated));

        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(EmptyText));

        SelectById(keepId);
    }

    /// <summary>按 id 选中某一行；找不到就退回第一行。</summary>
    public void SelectById(string? id)
    {
        var row = id is null
            ? null
            : Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Instance.Id == id);

        row ??= Groups.SelectMany(g => g.Rows).FirstOrDefault();
        if (row is null) return;

        Select(row);
    }

    public void Select(InstanceRow row)
    {
        if (row.Instance.Isolated) Isolated.Selected = row;
        else Shared.Selected = row;
    }

    public InstanceRow? Find(string? id)
        => id is null ? null : Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Instance.Id == id);

    private string? _lastId;

    private void OnGroupSelected(InstanceGroup group, InstanceRow? row)
    {
        if (row is null)
        {
            // 点空白把这一组的选中清掉了。另一组还选着的话别跟着一起清。
            if (SelectedRow is not null && group.Rows.Contains(SelectedRow)) SelectedRow = null;
            return;
        }

        foreach (var g in Groups)
            if (!ReferenceEquals(g, group)) g.ClearSelectionSilently();

        SelectedRow = row;
        _lastId = row.Instance.Id;
    }
}
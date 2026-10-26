using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace PclLike.Gui
{
    /// <summary>一步的状态。</summary>
    public enum StepState
    {
        /// <summary>还没轮到它。</summary>
        Waiting,
        /// <summary>正在做（或部分完成，右侧显示百分比）。</summary>
        Running,
        /// <summary>做完了。</summary>
        Done,
        /// <summary>这一步失败了。</summary>
        Failed,
    }

    /// <summary>
    /// 任务清单里的一步（对应 PCL 任务管理窗里那一行：
    /// 「✓ 下载额外文件」「27% 解压整合包文件」「⋯ 安装 NeoForge」）。
    /// </summary>
    public sealed class InstallStepVm : ObservableObject
    {
        private int _percent;
        private StepState _state = StepState.Waiting;

        public InstallStepVm(string name)
        {
            Name = name;
        }

        /// <summary>步骤名，显示在右侧。</summary>
        public string Name { get; }

        /// <summary>0-100；<see cref="StepState.Waiting"/> 时忽略。</summary>
        public int Percent
        {
            get => _percent;
            set
            {
                if (!Set(ref _percent, value)) return;
                OnPropertyChanged(nameof(Glyph));
            }
        }

        public StepState State
        {
            get => _state;
            set
            {
                if (!Set(ref _state, value)) return;
                OnPropertyChanged(nameof(Glyph));
                OnPropertyChanged(nameof(GlyphBrush));
            }
        }

        /// <summary>左侧那一个小图标位：✓ / ⋯ / 百分比数字 / ✗。</summary>
        public string Glyph => _state switch
        {
            StepState.Done => "\uE73E",                       // ✓
            StepState.Failed => "\uE711",                     // ✗
            StepState.Running => _percent > 0 && _percent < 100
                ? $"{_percent}%"
                : "\uE712",                                   // ⋯（正在做，还没数字）
            _ => "",
        };

        public Brush GlyphBrush => _state switch
        {
            StepState.Done => Brushes.SeaGreen,
            StepState.Failed => Brushes.IndianRed,
            StepState.Running => Brushes.SteelBlue,
            _ => Brushes.Transparent,
        };
    }

    /// <summary>
    /// 「任务管理」窗的数据（照 PCL CE 那个窗做的）：
    /// 左边三块统计（总进度 / 下载速度 / 剩余文件），右边一张任务卡，
    /// 卡片里是这个任务的**分步清单**。
    ///
    /// 它不直接调下载器 —— 所有进度都从启动器已有的日志流里推出来
    /// （见 <see cref="OnLog"/>）。这样安装代码一行都不用改，
    /// 而且以后任何新步骤只要打日志就会自动出现在这份清单里。
    /// </summary>
    public sealed class TaskProgressViewModel : ObservableObject
    {
        private string _title = "";
        private double _overall;
        private string _speedText = "-";
        private string _remainingText = "-";
        private bool _isBusy;
        private DateTime _startedAt = DateTime.Now;

        public ObservableCollection<InstallStepVm> Steps { get; } = new();

        /// <summary>任务标题，例如「CurseForge 整合包安装：AFoP Continue-1.5.1-fix」。</summary>
        public string Title
        {
            get => _title;
            private set => Set(ref _title, value);
        }

        /// <summary>0-100。</summary>
        public double Overall
        {
            get => _overall;
            private set
            {
                if (!Set(ref _overall, value)) return;
                OnPropertyChanged(nameof(OverallText));
            }
        }

        public string OverallText => $"{_overall:0.00} %";

        /// <summary>「2.32 MB/s」。</summary>
        public string SpeedText
        {
            get => _speedText;
            private set => Set(ref _speedText, value);
        }

        /// <summary>还剩多少个文件。</summary>
        public string RemainingText
        {
            get => _remainingText;
            private set => Set(ref _remainingText, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (!Set(ref _isBusy, value)) return;
                OnPropertyChanged(nameof(StateText));
            }
        }

        public string StateText => _isBusy ? "进行中" : (Steps.Count > 0 ? "已结束" : "空闲");

        public bool HasTask => Steps.Count > 0;

        // ── 驱动 ──────────────────────────────────────────────────────────

        /// <summary>开一个新任务（会清掉上一次的清单）。</summary>
        public void Begin(string title)
        {
            Title = title;
            Steps.Clear();
            Overall = 0;
            SpeedText = "-";
            RemainingText = "-";
            IsBusy = true;
            _startedAt = DateTime.Now;
            OnPropertyChanged(nameof(HasTask));
        }

        /// <summary>把清单里的某一步标成「正在做」；没有就插到末尾。</summary>
        public InstallStepVm Step(string name)
        {
            var exist = Steps.FirstOrDefault(s => s.Name == name);
            if (exist is null)
            {
                exist = new InstallStepVm(name);
                Steps.Add(exist);
                OnPropertyChanged(nameof(HasTask));
            }

            // 之前的「正在做」都算做完了 —— PCL 那个清单也是这个行为
            foreach (var s in Steps)
            {
                if (ReferenceEquals(s, exist)) continue;
                if (s.State == StepState.Running) s.State = StepState.Done;
            }

            if (exist.State != StepState.Done) exist.State = StepState.Running;
            Recalc();
            return exist;
        }

        /// <summary>给某一步设百分比（没有就新建）。</summary>
        public void SetPercent(string name, int percent)
        {
            var s = Step(name);
            s.Percent = Math.Clamp(percent, 0, 100);
            Recalc();
        }

        /// <summary>某一步完成。</summary>
        public void Done(string name)
        {
            var s = Steps.FirstOrDefault(x => x.Name == name);
            if (s is null) return;
            s.State = StepState.Done;
            s.Percent = 100;
            Recalc();
        }

        /// <summary>整件事成功收尾：剩下的步骤全部标完成。</summary>
        public void Finish()
        {
            foreach (var s in Steps)
            {
                if (s.State is StepState.Running or StepState.Waiting) s.State = StepState.Done;
            }

            Overall = 100;
            IsBusy = false;
            SpeedText = "-";
            RemainingText = "0";
        }

        /// <summary>整件事失败：正在做的那步标红。</summary>
        public void Fail()
        {
            var running = Steps.LastOrDefault(s => s.State == StepState.Running);
            if (running is not null) running.State = StepState.Failed;
            IsBusy = false;
        }

        /// <summary>
        /// 从启动器的日志流里认出进度。
        ///
        /// 认的句式就是安装代码本来就在打的那几种：
        ///   「正在下载资源 (12/345)」「已下 189/264」「下载 Mod 189/264」
        ///   「装 Forge 47.4.0」「铺 overrides」「解压整合包」
        /// 认不出来就什么都不做（新步骤不会凭空冒出来）。
        /// </summary>
        public void OnLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            var m = ProgressRe.Match(message);
            if (m.Success)
            {
                var done = int.Parse(m.Groups["done"].Value);
                var total = int.Parse(m.Groups["total"].Value);
                if (total > 0)
                {
                    var pct = (int)Math.Round(done * 100.0 / total);
                    SetPercent(StepNameFor(message), pct);
                    RemainingText = Math.Max(0, total - done).ToString();
                    SpeedText = EstimateSpeed(done);
                }

                return;
            }

            // 「装 Forge / 装 NeoForge / 安装加载器」
            if (message.Contains("装 Forge", StringComparison.Ordinal) ||
                message.Contains("装 NeoForge", StringComparison.Ordinal) ||
                message.Contains("Forge 主文件", StringComparison.Ordinal))
            {
                Step("安装 Forge");
                return;
            }

            if (message.Contains("overrides", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("铺", StringComparison.Ordinal))
            {
                Step("解压整合包文件");
                return;
            }

            if (message.Contains("读清单", StringComparison.Ordinal) ||
                message.Contains("解析整合包", StringComparison.Ordinal))
            {
                Step("解压整合包文件");
            }
        }

        // ── 内部 ──────────────────────────────────────────────────────────

        private static readonly Regex ProgressRe = new(
            @"(?<done>\d+)\s*/\s*(?<total>\d+)", RegexOptions.Compiled);

        private long _lastDone = -1;
        private DateTime _lastAt = DateTime.MinValue;

        /// <summary>
        /// 用「距上次报进度之间新增了几个文件」估速度。
        /// 估不准就说不知道 —— 不编一个好看的数字出来。
        /// </summary>
        private string EstimateSpeed(int done)
        {
            var now = DateTime.Now;
            var text = SpeedText;

            if (_lastDone >= 0 && done > _lastDone)
            {
                var seconds = (now - _lastAt).TotalSeconds;
                if (seconds > 0.5)
                {
                    var perSec = (done - _lastDone) / seconds;
                    text = perSec >= 1 ? $"{perSec:0.00} 个文件/s" : $"{1 / Math.Max(perSec, 0.001):0.0} 秒/个";
                }
            }

            _lastDone = done;
            _lastAt = now;
            return text;
        }

        private static string StepNameFor(string message)
        {
            if (message.Contains("资源", StringComparison.Ordinal)) return "下载原版资源文件";
            if (message.Contains("支持库", StringComparison.Ordinal) ||
                message.Contains("类库", StringComparison.Ordinal)) return "下载游戏支持库文件";
            if (message.Contains("Mod", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("整合包", StringComparison.Ordinal)) return "下载额外文件";
            return "下载文件";
        }

        /// <summary>总体进度 = 已完成步占比 + 正在做那步的百分比折算。</summary>
        private void Recalc()
        {
            if (Steps.Count == 0)
            {
                Overall = 0;
                return;
            }

            // 总进度是「按步数」算的，不是按文件数 —— 跟 PCL 一样是粗粒度感觉。
            var per = 100.0 / Steps.Count;
            var sum = 0.0;
            foreach (var s in Steps)
            {
                sum += s.State == StepState.Done ? per : per * s.Percent / 100.0;
            }

            Overall = Math.Clamp(sum, 0, 100);
        }
    }
}
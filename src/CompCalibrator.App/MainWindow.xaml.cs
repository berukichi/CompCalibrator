using System.Diagnostics;
using System.Text;
using CompCalibrator.Analysis;
using CompCalibrator.App.Services;
using CompCalibrator.App.Views;
using CompCalibrator.Compression;
using CompCalibrator.Profiles;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;

namespace CompCalibrator.App;

public sealed partial class MainWindow : Window
{
    private sealed record Stage(double Seconds, string Title, string Body);

    /// <summary>
    /// 無音でノイズ床を、声の 3 段階で音量の幅を測る。発話の長さも足りるように配分している。
    /// </summary>
    private static readonly Stage[] Stages =
    [
        new(2, "静かに", "環境ノイズを測ります。息も止めて、そのままお待ちください。"),
        new(11, "普段の声で話す", "今日あったことを、配信のつもりで話してください。内容は何でもかまいません。"),
        new(5, "盛り上がった声で", "笑う、驚く、叫ぶなど、配信でいちばん大きくなるときの声を出してください。"),
        new(4, "小さな声で", "つぶやくような、配信でいちばん小さくなるときの声で話してください。"),
    ];

    private enum CaptureGoal { None, Measure, Verify }

    private readonly UserSettings _settings = UserSettings.Load();
    private readonly MicCapture _capture = new();
    private readonly PreviewPlayer _player = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly double _totalSeconds = Stages.Sum(s => s.Seconds);

    private IReadOnlyList<DeviceProfile> _profiles = [];
    private TuningStyle _style = TuningStyle.Balanced;
    private bool _loading = true;
    private double _meterDb = -60;
    private double _peakHoldDb = -60;

    private CaptureGoal _goal = CaptureGoal.None;
    private Stopwatch? _elapsed;

    private Recording? _recording;
    private AnalysisResult? _analysis;
    private TuningResult? _tuning;
    private VerificationResult? _verification;
    private int _solveVersion;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureWindow();

        _capture.Failed += message => DispatcherQueue.TryEnqueue(() => SetStatus($"入力が止まりました: {message}", false));

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += (_, _) => OnTick();

        Root.SizeChanged += (_, _) => DrawGrid();
        Root.Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _settings.Save();
            _player.Dispose();
            _capture.Dispose();
        };
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);

        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1240 * scale), (int)(900 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(860 * scale);
            presenter.PreferredMinimumHeight = (int)(560 * scale);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private async Task InitializeAsync()
    {
        var errors = new List<string>();
        _profiles = ProfileLibrary.LoadAll(AppPaths.Profiles, errors);
        foreach (var error in errors)
        {
            AddLine(WarningPanel, $"プロファイルを読めませんでした: {error}", "WarnBrush");
        }

        if (Enum.TryParse<TuningStyle>(_settings.Style, out var style))
        {
            _style = style;
        }
        UpdateStyleButtons();

        DeviceBox.ItemsSource = _profiles;
        DeviceBox.SelectedItem = _profiles.FirstOrDefault(p => p.Id == _settings.DeviceId) ?? _profiles[0];

        try
        {
            var inputs = await MicCapture.ListAsync();
            InputBox.ItemsSource = inputs;
            InputBox.SelectedItem = inputs.FirstOrDefault(d => d.Id == _settings.InputId)
                ?? inputs.FirstOrDefault(d => d.IsDefault)
                ?? inputs.FirstOrDefault();
            if (inputs.Count == 0)
            {
                SetStatus("録音デバイスが見つかりません", false);
                MeasureButton.IsEnabled = false;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            SetStatus($"録音デバイスを読めません: {ex.Message}", false);
            MeasureButton.IsEnabled = false;
        }

        _loading = false;
        await StartInputAsync();
        _timer.Start();
        await AnalyzeFileFromCommandLineAsync();
    }

    /// <summary>--analyze &lt;wav&gt; で、マイクの代わりに録音ファイルを解析する(開発と再現の確認用)。</summary>
    private async Task AnalyzeFileFromCommandLineAsync()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--analyze");
        if (index < 0 || index + 1 >= args.Length)
        {
            return;
        }

        var recording = await Task.Run(() => LoadWave(args[index + 1]));
        _recording = recording;
        _analysis = await Task.Run(() => VoiceAnalyzer.Analyze(recording.Samples, recording.SampleRate));
        ShowVoice();
        await SolveAsync();
    }

    private static Recording LoadWave(string path)
    {
        using var reader = new NAudio.Wave.WaveFileReader(path);
        var provider = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader);
        var channels = reader.WaveFormat.Channels;
        var buffer = new float[reader.SampleCount * channels];
        var read = provider.Read(buffer, 0, buffer.Length);
        var mono = new float[read / channels];
        for (var i = 0; i < mono.Length; i++)
        {
            mono[i] = buffer[i * channels];
        }
        return new Recording(mono, reader.WaveFormat.SampleRate);
    }

    private async Task StartInputAsync()
    {
        if (InputBox.SelectedItem is not MicDevice device)
        {
            return;
        }
        try
        {
            await _capture.StartAsync(device.Id);
            SetStatus("入力をモニタリング中", true);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
            or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            SetStatus($"入力を開けません: {ex.Message}", false);
        }
    }

    private void OnTick()
    {
        var (rmsDb, peakDb) = _capture.ReadLevel();
        var target = Math.Max(-60, rmsDb);
        _meterDb += (target - _meterDb) * 0.3;
        _peakHoldDb = Math.Max(Math.Max(-60, peakDb), _peakHoldDb - 0.4);

        MeterFillScale.ScaleX = (_meterDb + 60) / 60;
        MeterPeakShift.X = Math.Max(0, MeterTrack.ActualWidth - 4) * (_peakHoldDb + 60) / 60;
        MeterText.Text = $"{_meterDb:0.0} dBFS";

        if (_goal != CaptureGoal.None && _elapsed is not null)
        {
            UpdateCapture(_elapsed.Elapsed.TotalSeconds);
        }
    }

    private void UpdateCapture(double seconds)
    {
        CaptureProgress.Value = Math.Min(1, seconds / _totalSeconds);

        var edge = 0.0;
        foreach (var stage in Stages)
        {
            edge += stage.Seconds;
            if (seconds < edge)
            {
                PromptTitle.Text = stage.Title;
                PromptBody.Text = stage.Body;
                return;
            }
        }
        _ = FinishCaptureAsync();
    }

    private void BeginCapture(CaptureGoal goal)
    {
        _player.Stop();
        _goal = goal;
        _elapsed = Stopwatch.StartNew();
        _capture.BeginRecording();
        PromptPanel.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        MeasureButton.IsEnabled = false;
        VerifyButton.IsEnabled = false;
        WarningPanel.Children.Clear();
        SetStatus(goal == CaptureGoal.Measure ? "測定中" : "確認中", true);
        Scroller.ChangeView(null, 0, null);
    }

    private void EndCaptureUi()
    {
        _goal = CaptureGoal.None;
        _elapsed = null;
        PromptPanel.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        MeasureButton.IsEnabled = true;
        VerifyButton.IsEnabled = true;
    }

    private async Task FinishCaptureAsync()
    {
        var goal = _goal;
        if (goal == CaptureGoal.None)
        {
            return;
        }
        var recording = _capture.EndRecording();
        EndCaptureUi();

        if (recording is null)
        {
            SetStatus("音が届きませんでした。入力デバイスを確認してください", false);
            return;
        }

        SetStatus("解析中", true);
        var analysis = await Task.Run(() => VoiceAnalyzer.Analyze(recording.Samples, recording.SampleRate));

        foreach (var warning in analysis.Warnings)
        {
            AddLine(WarningPanel, warning, "WarnBrush");
        }

        // 発話が足りない録音からは計算しない。前回の結果はそのまま残す。
        if (!analysis.IsUsable)
        {
            SetStatus("測定をやり直してください", false);
            return;
        }

        if (goal == CaptureGoal.Measure)
        {
            _recording = recording;
            _analysis = analysis;
            _verification = null;
            ShowVoice();
            await SolveAsync();
        }
        else if (_tuning is not null)
        {
            _verification = Verifier.Evaluate(_tuning, analysis.Metrics);
            ShowVerification();
        }
        SetStatus("入力をモニタリング中", true);
    }

    private async Task SolveAsync()
    {
        if (_analysis is not { } analysis || ModeBox.SelectedItem is not CompressorMode mode)
        {
            return;
        }
        var version = ++_solveVersion;
        var style = _style;
        var result = await Task.Run(() => CompressorSolver.Solve(analysis, mode, style));
        if (version != _solveVersion)
        {
            return;
        }
        _tuning = result;
        ClearVerification();
        ShowSettings();
    }

    private void ShowVoice()
    {
        if (_analysis is null)
        {
            return;
        }
        var m = _analysis.Metrics;
        VoiceSection.Visibility = Visibility.Visible;
        VoiceSummaryText.Text = VoiceDescription.Summarize(m);

        MetricsPanel.Children.Clear();
        void Metric(string label, string value)
        {
            var row = new Grid { Padding = new Thickness(0, 7, 0, 7), BorderBrush = Res("GridBrush"), BorderThickness = new Thickness(0, 0, 0, 1) };
            row.Children.Add(new TextBlock { Text = label, Style = Style("Label"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock
            {
                Text = value,
                Style = Style("Body"),
                Foreground = Res("InkBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                TextWrapping = TextWrapping.NoWrap,
            });
            MetricsPanel.Children.Add(row);
        }

        Metric("PITCH 声の高さ", m.HasPitch ? $"{m.PitchHz:0} Hz ({m.PitchLowHz:0}–{m.PitchHighHz:0})" : "測定不能");
        Metric("FORMANT F1 / F2 / F3", m.HasFormants ? $"{m.F1Hz:0} / {m.F2Hz:0} / {(m.F3Hz > 0 ? m.F3Hz.ToString("0") : "--")} Hz" : "測定不能");
        Metric("CENTROID 明るさ", $"{m.CentroidHz:0} Hz");
        Metric("TILT 高域の傾き", $"{m.TiltDbPerOctave:0.0} dB/oct");
        Metric("BOOM / PRESENCE / SIBILANCE", $"{m.BoomDb:0} / {m.PresenceDb:0} / {m.SibilanceDb:0} dB");
        Metric("RATE 話す速さ", $"{m.SyllablesPerSecond:0.0} 音節/秒");
        Metric("ONSET 立ち上がり", $"{m.OnsetMs:0} ms");
        Metric("LEVEL 発話レベル", $"{m.SpeechLevelDb:0.0} dBFS / {m.LoudnessLufs:0.0} LUFS");
        Metric("SPREAD 音量の幅", $"{m.SpreadDb:0.0} dB");
        Metric("CREST ピークと実効値の差", $"{m.CrestDb:0.0} dB");
        Metric("NOISE ノイズ床 / SN 比", $"{m.NoiseFloorDb:0.0} dBFS / {m.SnrDb:0} dB");

        Charts.Spectrum(SpectrumCanvas, m);
    }

    private void ShowSettings()
    {
        if (_tuning is not { } tuning || _analysis is null)
        {
            return;
        }

        SettingsSection.Visibility = Visibility.Visible;
        VerifySection.Visibility = tuning.Mode.IsMacro ? Visibility.Collapsed : Visibility.Visible;
        ResultModeText.Text = tuning.Mode.Label;

        MacroText.Text = tuning.MacroInstruction ?? "";
        MacroText.Visibility = tuning.MacroInstruction is null ? Visibility.Collapsed : Visibility.Visible;

        SettingsPanel.Children.Clear();
        foreach (var item in tuning.Settings)
        {
            SettingsPanel.Children.Add(BuildRow(item));
        }

        while (RelatedPanel.Children.Count > 1)
        {
            RelatedPanel.Children.RemoveAt(1);
        }
        foreach (var item in tuning.Related)
        {
            RelatedPanel.Children.Add(BuildRow(item));
        }
        RelatedPanel.Visibility = tuning.Related.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var s = tuning.Simulation;
        OutcomePanel.Children.Clear();
        AddOutcome(OutcomePanel, "音量の幅", $"{tuning.InputSpreadDb:0.0} → {s.SpreadDb:0.0} dB");
        AddOutcome(OutcomePanel, "GR 普段 / 山", $"{s.MedianReductionDb:0.0} / {s.PeakReductionDb:0.0} dB");
        AddOutcome(OutcomePanel, "ラウドネス", $"{tuning.InputLoudnessLufs:0.0} → {s.LoudnessLufs:0.0} LUFS");
        AddOutcome(OutcomePanel, "ピーク", $"{s.PeakDb:0.0} dBFS");

        NotesPanel.Children.Clear();
        foreach (var note in tuning.Notes)
        {
            AddLine(NotesPanel, note, "InkBrush");
        }

        DrawResultCharts();
    }

    private FrameworkElement BuildRow(ParameterRecommendation item)
    {
        var spec = item.Spec;
        var row = new Grid
        {
            ColumnSpacing = 20,
            RowSpacing = 4,
            Padding = new Thickness(0, 12, 0, 12),
            BorderBrush = Res("GridBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        row.RowDefinitions.Add(new RowDefinition());
        row.RowDefinitions.Add(new RowDefinition());

        var label = new TextBlock { Text = spec.Label, Style = Style("Label"), Foreground = Res("InkBrush"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        row.Children.Add(label);

        var value = new TextBlock
        {
            Text = item.Display,
            Style = Style("SectionTitle"),
            FontSize = 24,
            Foreground = Res(spec.Fixed ? "MutedBrush" : "LimeBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(value, 1);
        row.Children.Add(value);

        var bar = BuildBar(spec, item.Position);
        Grid.SetColumn(bar, 2);
        row.Children.Add(bar);

        var tag = BuildTag(spec.Confidence);
        Grid.SetColumn(tag, 3);
        row.Children.Add(tag);

        var details = new StringBuilder(item.Reason);
        if (!string.IsNullOrEmpty(spec.Note))
        {
            details.Append(' ').Append(spec.Note);
        }
        var reason = new TextBlock { Text = details.ToString(), Style = Style("Small") };
        Grid.SetRow(reason, 1);
        Grid.SetColumnSpan(reason, 4);
        row.Children.Add(reason);
        return row;
    }

    /// <summary>実機のスライダーのどのあたりに来るかを見せる。</summary>
    private FrameworkElement BuildBar(ParameterSpec spec, double position)
    {
        var host = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var track = new Grid { Height = 3, Background = Res("GridBrush") };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(position, 0.0001), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - position, 0.0001), GridUnitType.Star) });
        track.Children.Add(new Rectangle { Fill = Res(spec.Fixed ? "MutedBrush" : "LimeBrush") });
        host.Children.Add(track);
        host.Children.Add(new TextBlock { Text = spec.RangeText(), Style = Style("Label"), FontSize = 10 });
        return host;
    }

    private FrameworkElement BuildTag(Confidence confidence)
    {
        var (text, brush) = confidence switch
        {
            Confidence.Verified => ("VERIFIED", "LimeBrush"),
            Confidence.Device => ("APP", "LimeBrush"),
            Confidence.Inferred => ("INFERRED", "InkBrush"),
            Confidence.Unverified => ("UNVERIFIED", "WarnBrush"),
            _ => ("GENERIC", "MutedBrush"),
        };
        var border = new Border
        {
            BorderBrush = Res(brush),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 2, 5, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, Style = Style("Label"), FontSize = 9, Foreground = Res(brush) },
        };
        ToolTipService.SetToolTip(border, confidence switch
        {
            Confidence.Verified => "公開された資料やソースコードで確認した値です",
            Confidence.Device => "実機のアプリの表示で確認した値です",
            Confidence.Inferred => "画面の位置などから推定した値です",
            Confidence.Unverified => "可動域を確認できていない仮置きの値です",
            _ => "特定の機器に依らない範囲です",
        });
        return border;
    }

    private void AddOutcome(StackPanel panel, string label, string value)
    {
        var item = new StackPanel { Spacing = 2 };
        item.Children.Add(new TextBlock { Text = label, Style = Style("Label") });
        item.Children.Add(new TextBlock { Text = value, Style = Style("Body"), Foreground = Res("InkBrush"), TextWrapping = TextWrapping.NoWrap });
        panel.Children.Add(item);
    }

    private void DrawResultCharts()
    {
        if (_tuning is { } tuning && _analysis is { } analysis)
        {
            Charts.Transfer(TransferCanvas, tuning.Params, analysis.Metrics);
            Charts.Reduction(ReductionCanvas, tuning.Simulation, tuning.Params.MakeupDb);
        }
    }

    private void ShowVerification()
    {
        if (_verification is not { } result)
        {
            return;
        }
        VerifyVerdict.Text = result.Verdict;
        VerifyVerdict.Visibility = Visibility.Visible;
        VerifyPanel.Children.Clear();
        AddOutcome(VerifyPanel, "音量の幅 予測 / 実測", $"{result.PredictedSpreadDb:0.0} / {result.MeasuredSpreadDb:0.0} dB");
        AddOutcome(VerifyPanel, "ラウドネス 予測 / 実測", $"{result.PredictedLufs:0.0} / {result.MeasuredLufs:0.0} LUFS");
        ApplyButton.Visibility = result.Converged ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearVerification()
    {
        _verification = null;
        VerifyVerdict.Visibility = Visibility.Collapsed;
        VerifyPanel.Children.Clear();
        ApplyButton.Visibility = Visibility.Collapsed;
    }

    private void ShowModeInfo()
    {
        var profile = DeviceBox.SelectedItem as DeviceProfile;
        var mode = ModeBox.SelectedItem as CompressorMode;
        ModeNoteText.Text = mode?.Note ?? "";
        ModeNoteText.Visibility = string.IsNullOrEmpty(mode?.Note) ? Visibility.Collapsed : Visibility.Visible;
        CaptureHintText.Text = profile?.CaptureHint ?? "";

        SourcesPanel.Children.Clear();
        foreach (var source in profile?.Sources ?? [])
        {
            if (string.IsNullOrEmpty(source.Url) || !Uri.TryCreate(source.Url, UriKind.Absolute, out var uri))
            {
                SourcesPanel.Children.Add(new TextBlock { Text = source.Title, Style = Style("Small") });
                continue;
            }
            SourcesPanel.Children.Add(new HyperlinkButton
            {
                Content = source.Title,
                NavigateUri = uri,
                Padding = new Thickness(0),
                FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
                FontSize = 12,
                Foreground = Res("MutedBrush"),
            });
        }
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not DeviceProfile profile)
        {
            return;
        }
        ModeBox.ItemsSource = profile.Modes;
        ModeBox.SelectedItem = profile.Modes.FirstOrDefault(m => m.Id == _settings.ModeId) ?? profile.Modes[0];
        _settings.DeviceId = profile.Id;
        ShowModeInfo();
    }

    private async void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeBox.SelectedItem is not CompressorMode mode)
        {
            return;
        }
        _settings.ModeId = mode.Id;
        ShowModeInfo();
        if (!_loading)
        {
            await SolveAsync();
        }
    }

    private async void OnInputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InputBox.SelectedItem is not MicDevice device)
        {
            return;
        }
        _settings.InputId = device.Id;
        if (!_loading)
        {
            await StartInputAsync();
        }
    }

    private async void OnStyleClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<TuningStyle>(tag, out var style))
        {
            _style = style;
            _settings.Style = tag;
            UpdateStyleButtons();
            await SolveAsync();
        }
    }

    private void UpdateStyleButtons()
    {
        foreach (var (button, style) in new[]
        {
            (NaturalButton, TuningStyle.Natural),
            (BalancedButton, TuningStyle.Balanced),
            (TightButton, TuningStyle.Tight),
        })
        {
            var selected = style == _style;
            button.BorderBrush = Res(selected ? "LimeBrush" : "GridBrush");
            button.Foreground = Res(selected ? "LimeBrush" : "InkBrush");
        }
    }

    private void OnMeasureClick(object sender, RoutedEventArgs e) => BeginCapture(CaptureGoal.Measure);

    private void OnVerifyClick(object sender, RoutedEventArgs e) => BeginCapture(CaptureGoal.Verify);

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _capture.CancelRecording();
        EndCaptureUi();
        SetStatus("入力をモニタリング中", true);
    }

    private void OnApplyCorrection(object sender, RoutedEventArgs e)
    {
        if (_tuning is null || _analysis is null || _verification is null)
        {
            return;
        }
        _tuning = CompressorSolver.WithParams(_tuning, _analysis, _verification.Corrected);
        ClearVerification();
        ShowSettings();
    }

    private async void OnPlayBefore(object sender, RoutedEventArgs e)
    {
        if (_recording is { } recording)
        {
            await PlayAsync(recording.Samples, recording.SampleRate);
        }
    }

    private async void OnPlayAfter(object sender, RoutedEventArgs e)
    {
        if (_recording is { } recording && _tuning is { } tuning)
        {
            var rendered = await Task.Run(() =>
                CompressorSimulator.Render(recording.Samples, recording.SampleRate, tuning.Simulation.GainDb));
            await PlayAsync(rendered, recording.SampleRate);
        }
    }

    private async Task PlayAsync(float[] samples, int sampleRate)
    {
        try
        {
            await _player.PlayAsync(samples, sampleRate);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            SetStatus($"再生できません: {ex.Message}", false);
        }
    }

    private void OnStopPlayback(object sender, RoutedEventArgs e) => _player.Stop();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_tuning is not { } tuning)
        {
            return;
        }
        var text = new StringBuilder();
        text.AppendLine($"{(DeviceBox.SelectedItem as DeviceProfile)?.Name} / {tuning.Mode.Label}");
        if (tuning.MacroInstruction is { } macro)
        {
            text.AppendLine(macro);
        }
        foreach (var item in tuning.Settings.Concat(tuning.Related))
        {
            text.AppendLine($"{item.Spec.Label}: {item.Display}");
        }
        var package = new DataPackage();
        package.SetText(text.ToString());
        Clipboard.SetContent(package);
        SetStatus("値をコピーしました", true);
    }

    private void OnOpenProfiles(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Profiles);
        Process.Start(new ProcessStartInfo(AppPaths.Profiles) { UseShellExecute = true });
    }

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_analysis is { } analysis)
        {
            Charts.Spectrum(SpectrumCanvas, analysis.Metrics);
        }
        DrawResultCharts();
    }

    private void DrawGrid()
    {
        GridLayer.Children.Clear();
        const double step = 64;
        var width = Root.ActualWidth;
        var height = Root.ActualHeight;
        var brush = Res("GridBrush");
        for (var x = step; x < width; x += step)
        {
            GridLayer.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = height, Stroke = brush, StrokeThickness = 1, Opacity = 0.55 });
        }
        for (var y = step; y < height; y += step)
        {
            GridLayer.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = brush, StrokeThickness = 1, Opacity = 0.55 });
        }
    }

    private void SetStatus(string text, bool live)
    {
        StatusText.Text = text;
        LiveDot.Fill = Res(live ? "LimeBrush" : "WarnBrush");
    }

    private void AddLine(StackPanel panel, string text, string brush) =>
        panel.Children.Add(new TextBlock { Text = text, Style = Style("Body"), Foreground = Res(brush) });

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    private static Style Style(string key) => (Style)Application.Current.Resources[key];
}

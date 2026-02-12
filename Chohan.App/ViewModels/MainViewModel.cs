using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Chohan.Core.Capture;
using Chohan.Core.Config;
using Chohan.Core.Recognition;
using Chohan.Core.State;
using Chohan.Core.Twitch;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace Chohan.App.ViewModels;

/// <summary>
/// メインウィンドウのViewModel。
/// ProfileManager + HistoryService + StateMachine + Twitch を統合管理。
/// </summary>
public class MainViewModel : ViewModelBase, IDisposable
{
    // --- Core ---
    private readonly CaptureEngine _captureEngine = new();
    private readonly TemplateMatchingEngine _matchingEngine = new();
    private readonly StateMachine _stateMachine = new();

    // --- 設定 ---
    private readonly ProfileManager _profileManager = new();
    private readonly ConfigService _configService = new();
    private readonly HistoryService _historyService;

    // --- Twitch ---
    private TwitchOAuthConfig _twitchConfig = new();
    private TwitchOAuthService _twitchOAuthService;
    private TwitchPredictionService _twitchService;

    // --- マッチング ---
    private CancellationTokenSource? _matchingCts;
    private readonly Dispatcher _dispatcher;

    // --- トリガー ---
    private Dictionary<string, RoiSettings> _triggers = new()
    {
        ["start"] = new RoiSettings { Threshold = 0.80 },
        ["win"] = new RoiSettings { Threshold = 0.80 },
        ["lose"] = new RoiSettings { Threshold = 0.80 },
    };

    private string? _currentPredictionId;
    private PredictionStatus _currentPredictionStatus = PredictionStatus.None;
    private double _lastMatchConfidence;

    // -------------------------------------------------------
    // バインディング: 映像 / 一致率 / ステータス
    // -------------------------------------------------------

    private BitmapSource? _previewImage;
    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        set => SetProperty(ref _previewImage, value);
    }

    private double _currentMatchScore;
    public double CurrentMatchScore
    {
        get => _currentMatchScore;
        set => SetProperty(ref _currentMatchScore, value);
    }

    private string _currentMatchScoreText = "0%";
    public string CurrentMatchScoreText
    {
        get => _currentMatchScoreText;
        set => SetProperty(ref _currentMatchScoreText, value);
    }

    private string _statusText = "停止中";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private GameState _currentState = GameState.Stopped;
    public GameState CurrentState
    {
        get => _currentState;
        set
        {
            if (SetProperty(ref _currentState, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(StateColorHex));
                // CanExecute再評価を即座にトリガー（設定ボタンのグレーアウト等）
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsRunning => _currentState != GameState.Stopped;

    public string StateColorHex => _currentState switch
    {
        GameState.Stopped => "#888888",
        GameState.Idle => "#4CAF50",
        GameState.Voting => "#FF9800",
        GameState.Resolved => "#2196F3",
        _ => "#888888"
    };

    // -------------------------------------------------------
    // バインディング: デバイス
    // -------------------------------------------------------

    private List<string> _deviceNames = [];
    public List<string> DeviceNames
    {
        get => _deviceNames;
        set => SetProperty(ref _deviceNames, value);
    }

    private int _selectedDeviceIndex = -1;
    public int SelectedDeviceIndex
    {
        get => _selectedDeviceIndex;
        set => SetProperty(ref _selectedDeviceIndex, value);
    }

    // -------------------------------------------------------
    // バインディング: Twitch
    // -------------------------------------------------------

    private bool _isTwitchConnected;
    public bool IsTwitchConnected
    {
        get => _isTwitchConnected;
        set => SetProperty(ref _isTwitchConnected, value);
    }

    private string _twitchStatusText = "Twitch: 未接続";
    public string TwitchStatusText
    {
        get => _twitchStatusText;
        set => SetProperty(ref _twitchStatusText, value);
    }

    // -------------------------------------------------------
    // バインディング: プロファイル
    // -------------------------------------------------------

    private List<string> _profileNames = [];
    public List<string> ProfileNames
    {
        get => _profileNames;
        set => SetProperty(ref _profileNames, value);
    }

    private string _activeProfileName = "Default";
    public string ActiveProfileName
    {
        get => _activeProfileName;
        set => SetProperty(ref _activeProfileName, value);
    }

    // -------------------------------------------------------
    // バインディング: 常時投票モード
    // -------------------------------------------------------

    private bool _isAlwaysVotingMode;
    public bool IsAlwaysVotingMode
    {
        get => _isAlwaysVotingMode;
        set
        {
            if (SetProperty(ref _isAlwaysVotingMode, value))
            {
                _stateMachine.IsAlwaysVotingMode = value;
                _profileManager.ActiveConfig.AlwaysVotingMode = value;
                _profileManager.SaveActiveConfig();
                OnPropertyChanged(nameof(VotingModeText));
            }
        }
    }

    public string VotingModeText => IsAlwaysVotingMode ? "常時投票モード ON" : "通常モード";

    // -------------------------------------------------------
    // バインディング: 履歴
    // -------------------------------------------------------

    private ObservableCollection<HistoryEntry> _recentHistory = [];
    public ObservableCollection<HistoryEntry> RecentHistory
    {
        get => _recentHistory;
        set => SetProperty(ref _recentHistory, value);
    }

    private string _historyStatsText = "W:0 / L:0";
    public string HistoryStatsText
    {
        get => _historyStatsText;
        set => SetProperty(ref _historyStatsText, value);
    }

    private string _totalEntriesText = "全 0 件";
    public string TotalEntriesText
    {
        get => _totalEntriesText;
        set => SetProperty(ref _totalEntriesText, value);
    }

    // -------------------------------------------------------
    // コマンド
    // -------------------------------------------------------

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand<string> SwitchProfileCommand { get; }
    public RelayCommand ToggleAlwaysVotingCommand { get; }

    // -------------------------------------------------------
    // コンストラクタ
    // -------------------------------------------------------

    private List<CameraDeviceInfo> _devices = [];

    public MainViewModel()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _configService.Load();
        _profileManager.Load();

        _historyService = new HistoryService(_profileManager);
        _historyService.Load();
        _historyService.EntryAdded += OnHistoryEntryAdded;
        RefreshHistoryDisplay();

        var (twitchConfig, _) = _configService.LoadTwitchAuth();
        _twitchConfig = twitchConfig;
        _twitchOAuthService = new TwitchOAuthService(_twitchConfig);
        _twitchService = new TwitchPredictionService(_twitchOAuthService);

        _twitchService.ConnectionChanged += c =>
            _dispatcher.BeginInvoke(() => IsTwitchConnected = c);
        _twitchOAuthService.AuthStateChanged += (isAuth, msg) =>
        {
            _dispatcher.BeginInvoke(() => TwitchStatusText = $"Twitch: {msg}");
            _configService.SaveTwitchAuth(_twitchConfig, _twitchOAuthService.CurrentToken);
        };

        LoadTriggersFromProfile();
        ActiveProfileName = _profileManager.Index.ActiveProfile;
        ProfileNames = _profileManager.AvailableProfiles;

        _isAlwaysVotingMode = _profileManager.ActiveConfig.AlwaysVotingMode;
        _stateMachine.IsAlwaysVotingMode = _isAlwaysVotingMode;
        _stateMachine.ResolvedDelaySeconds = _profileManager.ActiveConfig.ResolvedDelaySeconds;

        StartCommand = new RelayCommand(ExecuteStart, () => !IsRunning);
        StopCommand = new RelayCommand(ExecuteStop, () => IsRunning);
        RefreshDevicesCommand = new RelayCommand(_ => RefreshDevices());
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings(), _ => !IsRunning);
        SwitchProfileCommand = new RelayCommand<string>(ExecuteSwitchProfile);
        ToggleAlwaysVotingCommand = new RelayCommand(_ => IsAlwaysVotingMode = !IsAlwaysVotingMode);

        _stateMachine.StateChanged += OnStateChanged;

        RefreshDevices();
        AutoSelectLastCamera();
        _ = _twitchOAuthService.InitializeAsync();
    }

    // -------------------------------------------------------
    // プロファイル
    // -------------------------------------------------------

    private void LoadTriggersFromProfile()
    {
        var triggerConfigs = _profileManager.LoadTriggers();
        _triggers = new Dictionary<string, RoiSettings>();
        foreach (var (key, config) in triggerConfigs)
        {
            _triggers[key] = config.ToRoiSettings();
            config.Dispose();
        }
    }

    private void ExecuteSwitchProfile(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName) || profileName == ActiveProfileName) return;

        _profileManager.SwitchProfile(profileName);
        LoadTriggersFromProfile();
        ActiveProfileName = profileName;
        ProfileNames = _profileManager.AvailableProfiles;

        IsAlwaysVotingMode = _profileManager.ActiveConfig.AlwaysVotingMode;
        _stateMachine.ResolvedDelaySeconds = _profileManager.ActiveConfig.ResolvedDelaySeconds;

        _historyService.Load();
        RefreshHistoryDisplay();

        StatusText = $"プロファイル切替: {profileName}";
    }

    public void CreateAndSwitchProfile(string name)
    {
        _profileManager.CreateProfile(name);
        ExecuteSwitchProfile(name);
    }

    public void DeleteCurrentProfile()
    {
        if (ActiveProfileName == "Default") return;
        _profileManager.DeleteProfile(ActiveProfileName);
        ProfileNames = _profileManager.AvailableProfiles;
        ActiveProfileName = _profileManager.Index.ActiveProfile;
        LoadTriggersFromProfile();
        _historyService.Load();
        RefreshHistoryDisplay();
    }

    // -------------------------------------------------------
    // 履歴
    // -------------------------------------------------------

    private void OnHistoryEntryAdded(HistoryEntry entry)
    {
        _dispatcher.BeginInvoke(() =>
        {
            RecentHistory.Insert(0, entry);
            while (RecentHistory.Count > 20) RecentHistory.RemoveAt(RecentHistory.Count - 1);
            RefreshHistoryStats();
        });
    }

    private void RefreshHistoryDisplay()
    {
        RecentHistory = new ObservableCollection<HistoryEntry>(_historyService.GetRecent(20));
        RefreshHistoryStats();
    }

    private void RefreshHistoryStats()
    {
        var w = _historyService.WinCount;
        var l = _historyService.LoseCount;
        var rate = _historyService.WinRate;
        HistoryStatsText = double.IsNaN(rate) ? $"W:{w} / L:{l}" : $"W:{w} / L:{l} ({rate:P0})";
        TotalEntriesText = $"全 {_historyService.Current.Entries.Count} 件";
    }

    // -------------------------------------------------------
    // デバイス
    // -------------------------------------------------------

    public void RefreshDevices()
    {
        _devices = CaptureEngine.EnumerateVideoDevices();
        DeviceNames = _devices.Select(d => d.Name).ToList();
        AutoSelectLastCamera();
    }

    private void AutoSelectLastCamera()
    {
        var lastCamera = _profileManager.ActiveConfig.CameraDevice;
        if (string.IsNullOrEmpty(lastCamera))
            lastCamera = _configService.Config.LastCameraDevice;

        if (!string.IsNullOrEmpty(lastCamera))
        {
            var idx = _devices.FindIndex(d => d.Name == lastCamera);
            if (idx >= 0) { SelectedDeviceIndex = idx; return; }
        }

        var obsIdx = _devices.FindIndex(d =>
            d.Name.Contains("OBS", StringComparison.OrdinalIgnoreCase));
        SelectedDeviceIndex = obsIdx >= 0 ? obsIdx : (_devices.Count > 0 ? 0 : -1);
    }

    // -------------------------------------------------------
    // 開始 / 停止
    // -------------------------------------------------------

    private void ExecuteStart()
    {
        if (SelectedDeviceIndex < 0 || SelectedDeviceIndex >= _devices.Count)
        { StatusText = "カメラが選択されていません"; return; }

        var device = _devices[SelectedDeviceIndex];
        if (device == null) { StatusText = "デバイス情報が無効です"; return; }

        try
        {
            _captureEngine.Start(device);
            _captureEngine.FrameCaptured += OnFrameCaptured;
            _stateMachine.Start();

            _matchingCts = new CancellationTokenSource();
            _ = MatchingLoopAsync(_matchingCts.Token);

            _configService.SaveLastCamera(device.Name);
            _profileManager.ActiveConfig.CameraDevice = device.Name;
            _profileManager.SaveActiveConfig();

            StatusText = IsAlwaysVotingMode ? "常時投票モード: 監視中..." : "監視中...";
            CurrentState = _stateMachine.CurrentState;
        }
        catch (Exception ex) { StatusText = $"エラー: {ex.Message}"; }
    }

    /// <summary>
    /// 監視を停止する。
    /// ★ 機能4: 実行中のTwitch Predictionがあればキャンセル（ポイント返還）。
    /// Twitch API: PATCH /helix/predictions { status: "CANCELED" }
    /// </summary>
    private async void ExecuteStop()
    {
        _matchingCts?.Cancel();
        _captureEngine.FrameCaptured -= OnFrameCaptured;
        _captureEngine.Stop();
        _stateMachine.Stop();

        if (!string.IsNullOrEmpty(_currentPredictionId))
        {
            try
            {
                await _twitchService.CancelPredictionAsync(_currentPredictionId);
                await _dispatcher.BeginInvoke(() => StatusText = "停止中（投票をキャンセルしました）");
            }
            catch
            {
                await _dispatcher.BeginInvoke(() => StatusText = "停止中（投票キャンセル失敗）");
            }
            _currentPredictionId = null;
        }
        else
        {
            StatusText = "停止中";
        }

        CurrentState = GameState.Stopped;
        CurrentMatchScore = 0;
        CurrentMatchScoreText = "0%";
        PreviewImage = null;
    }

    // -------------------------------------------------------
    // フレーム受信
    // -------------------------------------------------------

    private void OnFrameCaptured(Mat frame)
    {
        if (frame == null || frame.Empty()) { frame?.Dispose(); return; }
        try
        {
            var bitmap = OpenCvSharp.WpfExtensions.BitmapSourceConverter.ToBitmapSource(frame);
            bitmap.Freeze();
            _dispatcher.BeginInvoke(() => PreviewImage = bitmap);
        }
        catch { }
        finally { frame.Dispose(); }
    }

    // -------------------------------------------------------
    // マッチングループ
    // -------------------------------------------------------

    private async Task MatchingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var frame = _captureEngine.GetLatestFrame();
                if (frame != null && !frame.Empty()) ProcessFrame(frame);
                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private void ProcessFrame(Mat frame)
    {
        var state = _stateMachine.CurrentState;
        double displayScore = 0;
        MatchResult result = MatchResult.None;

        switch (state)
        {
            case GameState.Idle when !_stateMachine.IsAlwaysVotingMode:
                if (_triggers.TryGetValue("start", out var startS) && startS.TemplateImage != null)
                {
                    var s = _matchingEngine.Match(frame, startS);
                    displayScore = s;
                    if (s >= startS.Threshold) result = MatchResult.Start;
                }
                break;
            case GameState.Voting:
                double maxS = 0;
                if (_triggers.TryGetValue("win", out var winS) && winS.TemplateImage != null)
                {
                    var s = _matchingEngine.Match(frame, winS);
                    if (s > maxS) maxS = s;
                    if (s >= winS.Threshold) result = MatchResult.Win;
                }
                if (result == MatchResult.None
                    && _triggers.TryGetValue("lose", out var loseS) && loseS.TemplateImage != null)
                {
                    var s = _matchingEngine.Match(frame, loseS);
                    if (s > maxS) maxS = s;
                    if (s >= loseS.Threshold) result = MatchResult.Lose;
                }
                displayScore = maxS;
                break;
        }

        _lastMatchConfidence = displayScore;

        _dispatcher.BeginInvoke(() =>
        {
            CurrentMatchScore = displayScore;
            CurrentMatchScoreText = $"{displayScore:P0}";
        });

        if (result != MatchResult.None) _stateMachine.Feed(result);
    }

    // -------------------------------------------------------
    // 状態遷移ハンドラ
    // -------------------------------------------------------

    private async void OnStateChanged(GameState newState, MatchResult result)
    {
        await _dispatcher.BeginInvoke(() =>
        {
            CurrentState = newState;
            // ステータステキスト更新処理 (既存のまま)
            StatusText = newState switch
            {
                GameState.Idle => "待機中：開始画面を監視...",
                GameState.Voting => _stateMachine.IsAlwaysVotingMode
                                        ? "常時投票モード：結果を監視中..."
                                        : "投票中：結果を監視...",
                GameState.Resolved => result == MatchResult.Win ? "結果：勝利!" : "結果：敗北",
                GameState.Stopped => "停止中",
                _ => StatusText
            };
        });

        var pc = _profileManager.ActiveConfig;
        try
        {
            switch (newState)
            {
                // --- 投票開始時 ---
                case GameState.Voting when result == MatchResult.None || result == MatchResult.Start:
                    try
                    {
                        // 1. Twitch連携が有効ならPrediction作成を試みる
                        if (IsTwitchConnected)
                        {
                            _currentPredictionId = await _twitchService.CreatePredictionAsync(
                                pc.PredictionTitle, [pc.OutcomeWinLabel, pc.OutcomeLoseLabel],
                                pc.PredictionDurationSeconds);

                            _currentPredictionStatus = PredictionStatus.Created;
                        }
                        else
                        {
                            _currentPredictionId = null;
                            _currentPredictionStatus = PredictionStatus.None;
                        }
                    }
                    catch (Exception ex)
                    {
                        // 作成失敗時
                        _currentPredictionId = null;
                        _currentPredictionStatus = PredictionStatus.Failed;
                        await _dispatcher.BeginInvoke(() => StatusText = $"Twitchエラー: {ex.Message}");
                    }

                    // 2. 履歴に「開始」を記録 (ステータス付き)
                    _historyService.RecordStart(_lastMatchConfidence, _currentPredictionId, _currentPredictionStatus);
                    break;

                // --- 結果確定時 (ここが修正のメイン) ---
                case GameState.Resolved:
                    // 1. Twitchの結果にかかわらず、必ず履歴を保存する
                    if (result == MatchResult.Win)
                    {
                        _historyService.RecordWin(_lastMatchConfidence, _currentPredictionId, _currentPredictionStatus);
                    }
                    else if (result == MatchResult.Lose)
                    {
                        _historyService.RecordLose(_lastMatchConfidence, _currentPredictionId, _currentPredictionStatus);
                    }

                    // 2. もしPredictionが進行中なら、Twitch上で清算する
                    if (!string.IsNullOrEmpty(_currentPredictionId))
                    {
                        int idx = result == MatchResult.Win ? 0 : 1;
                        await _twitchService.ResolvePredictionAsync(_currentPredictionId, idx);
                        _currentPredictionId = null;
                    }

                    // ステータスリセット
                    _currentPredictionStatus = PredictionStatus.None;
                    break;
            }
        }
        catch (Exception ex)
        {
            await _dispatcher.BeginInvoke(() => StatusText = $"処理エラー: {ex.Message}");
        }
    }

    // -------------------------------------------------------
    // 設定画面
    // -------------------------------------------------------

    private void OpenSettings()
    {
        // フェイルセーフ: 万が一、認識中に呼ばれた場合は警告して中断
        if (IsRunning)
        {
            System.Windows.MessageBox.Show(
                "画面認識の実行中は設定画面を開けません。\n先に「停止」してから設定を開いてください。",
                "Chohan - 設定",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var triggerConfigs = _profileManager.LoadTriggers();
        var settingsVm = new SettingsViewModel(
            _captureEngine, triggerConfigs, _profileManager,
            _twitchConfig, _twitchOAuthService, _configService, _historyService);
        var settingsWindow = new Views.SettingsWindow { DataContext = settingsVm };
        settingsWindow.ShowDialog();

        // 設定の反映
        var updatedConfigs = settingsVm.GetTriggerConfigs();
        _profileManager.SaveTriggers(updatedConfigs);
        _triggers.Clear();
        foreach (var (key, config) in updatedConfigs) _triggers[key] = config.ToRoiSettings();

        // Twitch設定の保存
        var twitchConfig = settingsVm.GetTwitchConfig();
        if (twitchConfig != null)
        {
            _twitchConfig.ClientId = twitchConfig.ClientId;
            _twitchConfig.ClientSecret = twitchConfig.ClientSecret;
            _configService.SaveTwitchAuth(_twitchConfig, _twitchOAuthService.CurrentToken);
            IsTwitchConnected = _twitchOAuthService.IsAuthenticated;
        }

        ProfileNames = _profileManager.AvailableProfiles;
        ActiveProfileName = _profileManager.Index.ActiveProfile;
        IsAlwaysVotingMode = _profileManager.ActiveConfig.AlwaysVotingMode;
        _historyService.Load();
        RefreshHistoryDisplay();
    }

    public void UpdateTrigger(string name, RoiSettings settings) => _triggers[name] = settings;

    // -------------------------------------------------------
    // ウィンドウ位置
    // -------------------------------------------------------

    public void SaveWindowBounds(double x, double y, double w, double h)
        => _configService.SaveWindowPosition(x, y, w, h);

    public (double X, double Y, double Width, double Height) GetSavedWindowBounds()
    {
        var c = _configService.Config;
        return (c.WindowX, c.WindowY, c.WindowWidth, c.WindowHeight);
    }

    public ProfileManager ProfileManager => _profileManager;
    public ConfigService ConfigService => _configService;
    public HistoryService HistoryService => _historyService;

    // -------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------

    public void Dispose()
    {
        ExecuteStop();
        _captureEngine.Dispose();
        _matchingEngine.Dispose();
        _twitchService.Dispose();
        _twitchOAuthService.Dispose();
    }
}

/// <summary>型パラメータ付きRelayCommand。</summary>
public class RelayCommand<T> : System.Windows.Input.ICommand
{
    private readonly Action<T> _execute;
    private readonly Func<T, bool>? _canExecute;

    public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute; _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => System.Windows.Input.CommandManager.RequerySuggested += value;
        remove => System.Windows.Input.CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) =>
        _canExecute == null || (parameter is T t && _canExecute(t));

    public void Execute(object? parameter) { if (parameter is T t) _execute(t); }
}

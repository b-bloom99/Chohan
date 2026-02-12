using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Chohan.Core.Capture;
using Chohan.Core.Config;
using Chohan.Core.Recognition;
using Chohan.Core.Twitch;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

using CvRect = OpenCvSharp.Rect;
using WpfRect = System.Windows.Rect;

namespace Chohan.App.ViewModels;

/// <summary>
/// 統合設定画面のViewModel。
/// 3タブ構成: 認識設定 / Twitch設定 / 運用・履歴
/// </summary>
public class SettingsViewModel : ViewModelBase, IDisposable
{
    private CaptureEngine? _captureEngine;
    private readonly TemplateMatchingEngine _matchingEngine = new();
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _matchingCts;
    private readonly ProfileManager? _profileManager;

    // Twitch
    private TwitchOAuthConfig? _twitchConfig;
    private TwitchOAuthService? _twitchOAuthService;
    private readonly ConfigService? _configService;
    private CancellationTokenSource? _authCts;

    // 履歴
    private HistoryService? _historyService;

    private Mat? _frozenFrame;
    private bool _isFrozen;

    // -------------------------------------------------------
    // トリガー
    // -------------------------------------------------------

    private Dictionary<string, TriggerConfig> _triggers;

    // -------------------------------------------------------
    // バインディング: プロファイル
    // -------------------------------------------------------

    private List<string> _profileNames = [];
    public List<string> ProfileNames
    {
        get => _profileNames;
        set => SetProperty(ref _profileNames, value);
    }

    private string _selectedProfileName = "Default";
    public string SelectedProfileName
    {
        get => _selectedProfileName;
        set
        {
            if (SetProperty(ref _selectedProfileName, value) && _profileManager != null)
            {
                // プロファイル切替
                _profileManager.SwitchProfile(value);
                ReloadTriggersFromProfile();
                OnPropertyChanged(nameof(IsAlwaysVotingMode));
            }
        }
    }

    // -------------------------------------------------------
    // バインディング: 常時投票モード
    // -------------------------------------------------------

    public bool IsAlwaysVotingMode
    {
        get => _profileManager?.ActiveConfig.AlwaysVotingMode ?? false;
        set
        {
            if (_profileManager != null)
            {
                _profileManager.ActiveConfig.AlwaysVotingMode = value;
                _profileManager.SaveActiveConfig();
                OnPropertyChanged();
            }
        }
    }

    // -------------------------------------------------------
    // バインディング: プレビュー
    // -------------------------------------------------------

    private BitmapSource? _previewImage;
    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        set => SetProperty(ref _previewImage, value);
    }

    // -------------------------------------------------------
    // バインディング: フリーズ
    // -------------------------------------------------------

    public bool IsFrozen
    {
        get => _isFrozen;
        set
        {
            if (SetProperty(ref _isFrozen, value))
            {
                OnPropertyChanged(nameof(FreezeButtonText));
                if (!value) { _frozenFrame?.Dispose(); _frozenFrame = null; }
            }
        }
    }

    public string FreezeButtonText => IsFrozen ? "⏵ 再開" : "⏸ 静止";

    // -------------------------------------------------------
    // バインディング: 選択中トリガー
    // -------------------------------------------------------

    private string _selectedTriggerKey = "start";
    public string SelectedTriggerKey
    {
        get => _selectedTriggerKey;
        set
        {
            if (SetProperty(ref _selectedTriggerKey, value))
            {
                OnPropertyChanged(nameof(SelectedTriggerName));
                OnPropertyChanged(nameof(CurrentThreshold));
                OnPropertyChanged(nameof(CurrentThresholdText));
                OnPropertyChanged(nameof(HasTemplate));
                OnPropertyChanged(nameof(TemplateStatusText));
                OnPropertyChanged(nameof(TemplatePreviewImage));
                OnPropertyChanged(nameof(RegisterButtonText));
            }
        }
    }

    public string SelectedTriggerName => _selectedTriggerKey switch
    {
        "start" => "開始", "win" => "勝利", "lose" => "敗北", _ => _selectedTriggerKey
    };
    public string RegisterButtonText => $"🎯 「{SelectedTriggerName}」として登録";

    // -------------------------------------------------------
    // バインディング: 閾値
    // -------------------------------------------------------

    public double CurrentThreshold
    {
        get => CurrentTrigger?.Threshold ?? 0.80;
        set
        {
            if (CurrentTrigger != null)
            {
                CurrentTrigger.Threshold = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentThresholdText));
            }
        }
    }

    public string CurrentThresholdText => $"{CurrentThreshold:P0}";

    // -------------------------------------------------------
    // バインディング: リアルタイム一致率
    // -------------------------------------------------------

    private double _liveMatchScore;
    public double LiveMatchScore
    {
        get => _liveMatchScore;
        set
        {
            if (SetProperty(ref _liveMatchScore, value))
            {
                OnPropertyChanged(nameof(LiveMatchScoreText));
                OnPropertyChanged(nameof(MatchStateText));
                OnPropertyChanged(nameof(MatchStateColorHex));
            }
        }
    }
    public string LiveMatchScoreText => $"{LiveMatchScore:P1}";
    public string MatchStateText => LiveMatchScore >= CurrentThreshold ? "✓ 検知" : "— 未検知";
    public string MatchStateColorHex => LiveMatchScore >= CurrentThreshold ? "#4CAF50" : "#FF4533";

    // -------------------------------------------------------
    // バインディング: テンプレートプレビュー
    // -------------------------------------------------------

    public bool HasTemplate => CurrentTrigger?.HasTemplate ?? false;
    public string TemplateStatusText => HasTemplate
        ? $"登録済み ({CurrentTrigger!.RoiRect.Width}×{CurrentTrigger.RoiRect.Height})"
        : "(未登録)";

    private BitmapSource? _startTemplatePreview;
    private BitmapSource? _winTemplatePreview;
    private BitmapSource? _loseTemplatePreview;

    public BitmapSource? TemplatePreviewImage => _selectedTriggerKey switch
    {
        "start" => _startTemplatePreview, "win" => _winTemplatePreview,
        "lose" => _loseTemplatePreview, _ => null
    };
    public BitmapSource? StartTemplatePreview { get => _startTemplatePreview; set => SetProperty(ref _startTemplatePreview, value); }
    public BitmapSource? WinTemplatePreview { get => _winTemplatePreview; set => SetProperty(ref _winTemplatePreview, value); }
    public BitmapSource? LoseTemplatePreview { get => _loseTemplatePreview; set => SetProperty(ref _loseTemplatePreview, value); }

    // ROI
    private WpfRect _canvasRoi;
    public WpfRect CanvasRoi { get => _canvasRoi; set => SetProperty(ref _canvasRoi, value); }

    // -------------------------------------------------------
    // バインディング: Prediction 設定（Twitch API仕様準拠）
    // -------------------------------------------------------

    /// <summary>投票タイトル（最大45文字）</summary>
    public string PredictionTitle
    {
        get => _profileManager?.ActiveConfig.PredictionTitle ?? "次の結果は？";
        set
        {
            if (_profileManager != null)
            {
                // Twitch API仕様: 最大45文字
                _profileManager.ActiveConfig.PredictionTitle = value?.Length > 45 ? value[..45] : value ?? "";
                _profileManager.SaveActiveConfig();
                OnPropertyChanged();
                OnPropertyChanged(nameof(PredictionTitleLength));
            }
        }
    }

    public string PredictionTitleLength => $"{(PredictionTitle?.Length ?? 0)}/45";

    /// <summary>勝利時の選択肢ラベル（最大25文字）</summary>
    public string OutcomeWinLabel
    {
        get => _profileManager?.ActiveConfig.OutcomeWinLabel ?? "勝利";
        set
        {
            if (_profileManager != null)
            {
                _profileManager.ActiveConfig.OutcomeWinLabel = value?.Length > 25 ? value[..25] : value ?? "";
                _profileManager.SaveActiveConfig();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>敗北時の選択肢ラベル（最大25文字）</summary>
    public string OutcomeLoseLabel
    {
        get => _profileManager?.ActiveConfig.OutcomeLoseLabel ?? "敗北";
        set
        {
            if (_profileManager != null)
            {
                _profileManager.ActiveConfig.OutcomeLoseLabel = value?.Length > 25 ? value[..25] : value ?? "";
                _profileManager.SaveActiveConfig();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>投票受付時間（秒）。Twitch API仕様: 30～1800</summary>
    public int PredictionDurationSeconds
    {
        get => _profileManager?.ActiveConfig.PredictionDurationSeconds ?? 60;
        set
        {
            if (_profileManager != null)
            {
                _profileManager.ActiveConfig.PredictionDurationSeconds = Math.Clamp(value, 30, 1800);
                _profileManager.SaveActiveConfig();
                OnPropertyChanged();
                OnPropertyChanged(nameof(PredictionDurationText));
            }
        }
    }

    public string PredictionDurationText
    {
        get
        {
            var sec = PredictionDurationSeconds;
            return sec >= 60 ? $"{sec / 60}分{sec % 60}秒" : $"{sec}秒";
        }
    }

    /// <summary>結果確定後の待機秒数</summary>
    public int ResolvedDelaySeconds
    {
        get => _profileManager?.ActiveConfig.ResolvedDelaySeconds ?? 5;
        set
        {
            if (_profileManager != null)
            {
                _profileManager.ActiveConfig.ResolvedDelaySeconds = Math.Max(1, value);
                _profileManager.SaveActiveConfig();
                OnPropertyChanged();
            }
        }
    }

    // -------------------------------------------------------
    // コマンド
    // -------------------------------------------------------

    public RelayCommand ToggleFreezeCommand { get; }
    public RelayCommand RegisterTemplateCommand { get; }
    public RelayCommand ClearTemplateCommand { get; }
    public RelayCommand SelectStartCommand { get; }
    public RelayCommand SelectWinCommand { get; }
    public RelayCommand SelectLoseCommand { get; }
    public RelayCommand CreateProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand RenameProfileCommand { get; }

    // Twitch認証コマンド
    public RelayCommand AuthenticateCommand { get; }
    public RelayCommand LogoutCommand { get; }
    public RelayCommand CancelAuthCommand { get; }

    // 運用・履歴コマンド
    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }

    // ヘルパー
    private TriggerConfig? CurrentTrigger =>
        _triggers.TryGetValue(_selectedTriggerKey, out var t) ? t : null;

    // -------------------------------------------------------
    // コンストラクタ
    // -------------------------------------------------------

    public SettingsViewModel()
        : this(null, null, null, null, null, null, null) { }

    public SettingsViewModel(
        CaptureEngine? captureEngine,
        Dictionary<string, TriggerConfig>? triggers,
        ProfileManager? profileManager,
        TwitchOAuthConfig? twitchConfig = null,
        TwitchOAuthService? twitchOAuthService = null,
        ConfigService? configService = null,
        HistoryService? historyService = null)
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _captureEngine = captureEngine;
        _profileManager = profileManager;
        _twitchConfig = twitchConfig;
        _twitchOAuthService = twitchOAuthService;
        _configService = configService;
        _historyService = historyService;

        _triggers = triggers ?? new Dictionary<string, TriggerConfig>
        {
            ["start"] = new() { Name = "start", DisplayName = "開始", Threshold = 0.80 },
            ["win"]   = new() { Name = "win",   DisplayName = "勝利", Threshold = 0.80 },
            ["lose"]  = new() { Name = "lose",  DisplayName = "敗北", Threshold = 0.80 },
        };

        // プロファイル一覧
        if (_profileManager != null)
        {
            ProfileNames = _profileManager.AvailableProfiles;
            _selectedProfileName = _profileManager.Index.ActiveProfile;
        }

        // Twitch認証状態
        if (_twitchOAuthService != null)
        {
            _twitchOAuthService.AuthStateChanged += OnTwitchAuthStateChanged;
            UpdateTwitchAuthStatus();
            TwitchClientId = _twitchConfig?.ClientId ?? "";
            TwitchClientSecret = _twitchConfig?.ClientSecret ?? "";
        }

        // 履歴
        if (_historyService != null)
        {
            _historyService.EntryAdded += OnHistoryEntryAdded;
            RefreshHistoryDisplay();
        }

        // デバイス
        RefreshDevicesInternal();

        // コマンド
        ToggleFreezeCommand = new RelayCommand(ExecuteToggleFreeze);
        RegisterTemplateCommand = new RelayCommand(
            _ => { }, _ => CanvasRoi.Width > 0 && CanvasRoi.Height > 0);
        ClearTemplateCommand = new RelayCommand(_ => ExecuteClearTemplate());
        SelectStartCommand = new RelayCommand(_ => SelectedTriggerKey = "start");
        SelectWinCommand = new RelayCommand(_ => SelectedTriggerKey = "win");
        SelectLoseCommand = new RelayCommand(_ => SelectedTriggerKey = "lose");
        CreateProfileCommand = new RelayCommand(_ => ExecuteCreateProfile());
        DeleteProfileCommand = new RelayCommand(_ => ExecuteDeleteProfile(),
            _ => _selectedProfileName != "Default");
        RenameProfileCommand = new RelayCommand(_ => ExecuteRenameProfile(),
            _ => _selectedProfileName != "Default");

        // Twitch認証コマンド
        AuthenticateCommand = new RelayCommand(
            async () => await ExecuteAuthenticateAsync(),
            () => !IsAuthenticating && !string.IsNullOrWhiteSpace(TwitchClientId) && !string.IsNullOrWhiteSpace(TwitchClientSecret));
        LogoutCommand = new RelayCommand(
            async () => await ExecuteLogoutAsync(),
            () => IsTwitchAuthenticated);
        CancelAuthCommand = new RelayCommand(
            () => _authCts?.Cancel(),
            () => IsAuthenticating);

        // 運用コマンド
        RefreshDevicesCommand = new RelayCommand(_ => RefreshDevicesInternal());
        ClearHistoryCommand = new RelayCommand(_ => { }, _ => false); // View側でコールバック使用

        if (_captureEngine != null)
        {
            // 設定画面は停止中にのみ開かれる前提。
            // CaptureEngineを直接Startしてプレビュー用にフレームを取得する。
            StartPreviewCapture();
            _captureEngine.FrameCaptured += OnFrameCaptured;
        }

        RefreshAllTemplatePreviews();
        _matchingCts = new CancellationTokenSource();
        _ = MatchingLoopAsync(_matchingCts.Token);
    }

    // -------------------------------------------------------
    // プロファイルCRUD
    // -------------------------------------------------------

    /// <summary>
    /// プロフィール名入力のコールバック。
    /// View側（SettingsWindow）でInputDialogを表示して名前を返す。
    /// 戻り値: 入力された名前。キャンセル時はnull。
    /// </summary>
    public Func<List<string>, string?>? RequestProfileName { get; set; }

    private void ExecuteCreateProfile()
    {
        if (_profileManager == null) return;

        string? name = null;

        // コールバックが設定されていればInputDialogで名前を取得
        if (RequestProfileName != null)
        {
            name = RequestProfileName(_profileManager.AvailableProfiles);
        }

        // コールバック未設定またはキャンセルされた場合はスキップ
        if (string.IsNullOrWhiteSpace(name)) return;

        _profileManager.CreateProfile(name);
        ProfileNames = _profileManager.AvailableProfiles;
        SelectedProfileName = name;
    }

    /// <summary>
    /// プロフィール削除確認のコールバック。
    /// View側でMessageBoxを表示してYes/Noを返す。
    /// </summary>
    public Func<string, bool>? ConfirmDeleteProfile { get; set; }

    private void ExecuteDeleteProfile()
    {
        if (_profileManager == null || _selectedProfileName == "Default") return;

        // 確認ダイアログ
        if (ConfirmDeleteProfile != null && !ConfirmDeleteProfile(_selectedProfileName))
            return;

        _profileManager.DeleteProfile(_selectedProfileName);
        ProfileNames = _profileManager.AvailableProfiles;
        _selectedProfileName = _profileManager.Index.ActiveProfile;
        ReloadTriggersFromProfile();
        OnPropertyChanged(nameof(SelectedProfileName));
    }

    /// <summary>
    /// プロフィールリネーム用コールバック。
    /// View側でInputDialogを表示し、新しい名前を返す。
    /// 引数: (現在の名前, 除外する既存名一覧)。戻り値: 新名前 or null。
    /// </summary>
    public Func<string, List<string>, string?>? RequestRenameProfile { get; set; }

    private void ExecuteRenameProfile()
    {
        if (_profileManager == null || _selectedProfileName == "Default") return;

        string? newName = null;
        if (RequestRenameProfile != null)
        {
            var others = _profileManager.AvailableProfiles
                .Where(n => n != _selectedProfileName).ToList();
            newName = RequestRenameProfile(_selectedProfileName, others);
        }

        if (string.IsNullOrWhiteSpace(newName)) return;

        if (_profileManager.RenameProfile(_selectedProfileName, newName))
        {
            ProfileNames = _profileManager.AvailableProfiles;
            _selectedProfileName = newName;
            OnPropertyChanged(nameof(SelectedProfileName));
        }
    }

    private void ReloadTriggersFromProfile()
    {
        if (_profileManager == null) return;
        foreach (var t in _triggers.Values) t.Dispose();
        _triggers = _profileManager.LoadTriggers();
        RefreshAllTemplatePreviews();
        OnPropertyChanged(nameof(CurrentThreshold));
        OnPropertyChanged(nameof(CurrentThresholdText));
        OnPropertyChanged(nameof(HasTemplate));
        OnPropertyChanged(nameof(TemplateStatusText));
        OnPropertyChanged(nameof(TemplatePreviewImage));
        // Prediction設定も更新
        OnPropertyChanged(nameof(PredictionTitle));
        OnPropertyChanged(nameof(PredictionTitleLength));
        OnPropertyChanged(nameof(OutcomeWinLabel));
        OnPropertyChanged(nameof(OutcomeLoseLabel));
        OnPropertyChanged(nameof(PredictionDurationSeconds));
        OnPropertyChanged(nameof(PredictionDurationText));
        OnPropertyChanged(nameof(ResolvedDelaySeconds));
        OnPropertyChanged(nameof(IsAlwaysVotingMode));
        // 履歴・デバイスも更新
        _historyService?.Load();
        RefreshHistoryDisplay();
        AutoSelectCamera();
    }

    // -------------------------------------------------------
    // フレーム受信
    // -------------------------------------------------------

    private void OnFrameCaptured(Mat frame)
    {
        if (_isFrozen) { frame?.Dispose(); return; }
        if (frame == null || frame.Empty()) { frame?.Dispose(); return; }
        try
        {
            var bmp = BitmapSourceConverter.ToBitmapSource(frame);
            bmp.Freeze();
            _dispatcher.BeginInvoke(() => PreviewImage = bmp);
        }
        catch { }
        finally { frame.Dispose(); }
    }

    // -------------------------------------------------------
    // フリーズ
    // -------------------------------------------------------

    private void ExecuteToggleFreeze()
    {
        if (IsFrozen) { IsFrozen = false; return; }

        var frame = _captureEngine?.GetLatestFrame();
        if (frame != null)
        {
            _frozenFrame?.Dispose();
            _frozenFrame = frame;
            try
            {
                var bmp = BitmapSourceConverter.ToBitmapSource(frame);
                bmp.Freeze();
                PreviewImage = bmp;
            }
            catch { }
        }
        IsFrozen = true;
    }

    // -------------------------------------------------------
    // テンプレート登録
    // -------------------------------------------------------

    public void RegisterCurrentRoi(WpfRect frameRoi)
    {
        var trigger = CurrentTrigger;
        if (trigger == null) return;

        Mat? frame = _isFrozen ? _frozenFrame?.Clone() : _captureEngine?.GetLatestFrame();
        if (frame == null || frame.Empty()) { frame?.Dispose(); return; }

        try
        {
            var cvRect = new OpenCvSharp.Rect(
                (int)frameRoi.X, (int)frameRoi.Y,
                (int)frameRoi.Width, (int)frameRoi.Height);

            // 保存先: プロファイルフォルダ内
            string savePath = _profileManager?.GetTemplatePath(trigger.Name)
                ?? Path.Combine(Path.GetTempPath(), $"template_{trigger.Name}.png");

            trigger.CaptureFromFrame(frame, cvRect, savePath);
            RefreshTemplatePreview(trigger.Name);

            OnPropertyChanged(nameof(HasTemplate));
            OnPropertyChanged(nameof(TemplateStatusText));
            OnPropertyChanged(nameof(TemplatePreviewImage));
        }
        finally
        {
            if (!_isFrozen) frame.Dispose();
        }
    }

    private void ExecuteClearTemplate()
    {
        var trigger = CurrentTrigger;
        if (trigger == null) return;

        trigger.Dispose();
        _triggers[_selectedTriggerKey] = new TriggerConfig
        {
            Name = trigger.Name, DisplayName = trigger.DisplayName, Threshold = trigger.Threshold
        };

        RefreshTemplatePreview(_selectedTriggerKey);
        OnPropertyChanged(nameof(HasTemplate));
        OnPropertyChanged(nameof(TemplateStatusText));
        OnPropertyChanged(nameof(TemplatePreviewImage));
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
                var trigger = CurrentTrigger;
                if (trigger is { HasTemplate: true, HasRoi: true })
                {
                    Mat? frame = _isFrozen ? _frozenFrame?.Clone() : _captureEngine?.GetLatestFrame();
                    if (frame != null && !frame.Empty())
                    {
                        try
                        {
                            var roiSettings = trigger.ToRoiSettings();
                            double score = _matchingEngine.Match(frame, roiSettings);
                            await _dispatcher.BeginInvoke(() => LiveMatchScore = score);
                        }
                        finally { if (!_isFrozen) frame.Dispose(); }
                    }
                }
                else
                {
                    await _dispatcher.BeginInvoke(() => LiveMatchScore = 0.0);
                }
                await Task.Delay(150, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    // -------------------------------------------------------
    // テンプレートプレビュー
    // -------------------------------------------------------

    private void RefreshAllTemplatePreviews()
    {
        foreach (var key in _triggers.Keys)
            RefreshTemplatePreview(key);
    }

    private void RefreshTemplatePreview(string triggerName)
    {
        BitmapSource? bmp = null;
        if (_triggers.TryGetValue(triggerName, out var trigger) && trigger.HasTemplate)
        {
            try { bmp = BitmapSourceConverter.ToBitmapSource(trigger.TemplateImage!); bmp.Freeze(); }
            catch { bmp = null; }
        }
        _dispatcher.BeginInvoke(() =>
        {
            switch (triggerName)
            {
                case "start": StartTemplatePreview = bmp; break;
                case "win":   WinTemplatePreview = bmp;   break;
                case "lose":  LoseTemplatePreview = bmp;  break;
            }
            OnPropertyChanged(nameof(TemplatePreviewImage));
        });
    }

    // -------------------------------------------------------
    // 公開
    // -------------------------------------------------------

    public Dictionary<string, TriggerConfig> GetTriggerConfigs() => _triggers;

    // -------------------------------------------------------
    // Twitch認証
    // -------------------------------------------------------

    private bool _isTwitchAuthenticated;
    public bool IsTwitchAuthenticated
    {
        get => _isTwitchAuthenticated;
        set
        {
            if (SetProperty(ref _isTwitchAuthenticated, value))
            {
                OnPropertyChanged(nameof(IsTwitchNotAuthenticated));
                OnPropertyChanged(nameof(TwitchConnectionStatusText));
                OnPropertyChanged(nameof(TwitchStatusColorHex));
            }
        }
    }
    public bool IsTwitchNotAuthenticated => !_isTwitchAuthenticated;

    private string _twitchUserDisplayName = "";
    public string TwitchUserDisplayName
    {
        get => _twitchUserDisplayName;
        set => SetProperty(ref _twitchUserDisplayName, value);
    }

    private string _twitchUserLogin = "";
    public string TwitchUserLogin
    {
        get => _twitchUserLogin;
        set => SetProperty(ref _twitchUserLogin, value);
    }

    public string TwitchConnectionStatusText => IsTwitchAuthenticated
        ? $"✓ 接続済み: {TwitchUserDisplayName} (@{TwitchUserLogin})"
        : "✗ 未接続";
    public string TwitchStatusColorHex => IsTwitchAuthenticated ? "#4CAF50" : "#FF4533";

    private string _twitchClientId = "";
    public string TwitchClientId
    {
        get => _twitchClientId;
        set => SetProperty(ref _twitchClientId, value);
    }

    private string _twitchClientSecret = "";
    public string TwitchClientSecret
    {
        get => _twitchClientSecret;
        set => SetProperty(ref _twitchClientSecret, value);
    }

    private bool _isAuthenticating;
    public bool IsAuthenticating
    {
        get => _isAuthenticating;
        set
        {
            if (SetProperty(ref _isAuthenticating, value))
                OnPropertyChanged(nameof(TwitchAuthButtonText));
        }
    }
    public string TwitchAuthButtonText => IsAuthenticating ? "認証中... (ブラウザを確認)" : "🔗 Twitchで認証する";

    private string _twitchStatusMessage = "";
    public string TwitchStatusMessage
    {
        get => _twitchStatusMessage;
        set => SetProperty(ref _twitchStatusMessage, value);
    }

    private async Task ExecuteAuthenticateAsync()
    {
        if (_twitchConfig == null || _twitchOAuthService == null) return;

        _twitchConfig.ClientId = TwitchClientId.Trim();
        _twitchConfig.ClientSecret = TwitchClientSecret.Trim();

        if (!_twitchConfig.IsValid)
        {
            TwitchStatusMessage = "Client IDとClient Secretを入力してください。";
            return;
        }

        IsAuthenticating = true;
        TwitchStatusMessage = "ブラウザでTwitchの認証ページを開いています...";
        _authCts = new CancellationTokenSource();

        try
        {
            var success = await _twitchOAuthService.AuthenticateAsync(_authCts.Token);
            if (success) TwitchStatusMessage = "認証に成功しました！";
        }
        catch (OperationCanceledException) { TwitchStatusMessage = "認証がキャンセルされました。"; }
        catch (Exception ex) { TwitchStatusMessage = $"認証エラー: {ex.Message}"; }
        finally
        {
            IsAuthenticating = false;
            _authCts?.Dispose();
            _authCts = null;
        }
    }

    private async Task ExecuteLogoutAsync()
    {
        if (_twitchOAuthService == null) return;
        TwitchStatusMessage = "ログアウト中...";
        await _twitchOAuthService.LogoutAsync();
        TwitchStatusMessage = "ログアウトしました。";
    }

    private void OnTwitchAuthStateChanged(bool isAuth, string message)
    {
        _dispatcher.BeginInvoke(() =>
        {
            UpdateTwitchAuthStatus();
            TwitchStatusMessage = message;
        });
    }

    private void UpdateTwitchAuthStatus()
    {
        if (_twitchOAuthService == null) return;
        IsTwitchAuthenticated = _twitchOAuthService.IsAuthenticated;
        var token = _twitchOAuthService.CurrentToken;
        TwitchUserDisplayName = token?.UserDisplayName ?? "";
        TwitchUserLogin = token?.UserLogin ?? "";
    }

    /// <summary>Twitch設定を返す（保存用）</summary>
    public TwitchOAuthConfig? GetTwitchConfig()
    {
        if (_twitchConfig == null) return null;
        _twitchConfig.ClientId = TwitchClientId.Trim();
        _twitchConfig.ClientSecret = TwitchClientSecret.Trim();
        return _twitchConfig;
    }

    public TwitchOAuthService? TwitchOAuthService => _twitchOAuthService;

    // -------------------------------------------------------
    // 運用・履歴
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

    private void OnHistoryEntryAdded(HistoryEntry entry)
    {
        _dispatcher.BeginInvoke(() =>
        {
            RecentHistory.Insert(0, entry);
            while (RecentHistory.Count > 50) RecentHistory.RemoveAt(RecentHistory.Count - 1);
            RefreshHistoryStats();
        });
    }

    private void RefreshHistoryDisplay()
    {
        if (_historyService == null) return;
        RecentHistory = new ObservableCollection<HistoryEntry>(_historyService.GetRecent(50));
        RefreshHistoryStats();
    }

    private void RefreshHistoryStats()
    {
        if (_historyService == null) return;
        var w = _historyService.WinCount;
        var l = _historyService.LoseCount;
        var rate = _historyService.WinRate;
        HistoryStatsText = double.IsNaN(rate) ? $"W:{w} / L:{l}" : $"W:{w} / L:{l} ({rate:P0})";
        TotalEntriesText = $"全 {_historyService.Current.Entries.Count} 件";
    }

    /// <summary>全履歴をクリアし、UIを更新する。</summary>
    public void ClearHistory()
    {
        _historyService?.Clear();
        RefreshHistoryDisplay();
    }

    // -------------------------------------------------------
    // デバイス管理（運用タブ用）
    // -------------------------------------------------------

    private List<CameraDeviceInfo> _devices = [];

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
        set
        {
            if (SetProperty(ref _selectedDeviceIndex, value) && value >= 0 && value < _devices.Count)
            {
                // プロファイルにカメラデバイス名を保存
                if (_profileManager != null)
                {
                    _profileManager.ActiveConfig.CameraDevice = _devices[value].Name;
                    _profileManager.SaveActiveConfig();
                }
            }
        }
    }

    private void RefreshDevicesInternal()
    {
        _devices = CaptureEngine.EnumerateVideoDevices();
        DeviceNames = _devices.Select(d => d.Name).ToList();
        AutoSelectCamera();
    }

    private void AutoSelectCamera()
    {
        var lastCamera = _profileManager?.ActiveConfig.CameraDevice ?? "";
        if (!string.IsNullOrEmpty(lastCamera))
        {
            var idx = _devices.FindIndex(d => d.Name == lastCamera);
            if (idx >= 0) { SelectedDeviceIndex = idx; return; }
        }
        var obsIdx = _devices.FindIndex(d => d.Name.Contains("OBS", StringComparison.OrdinalIgnoreCase));
        SelectedDeviceIndex = obsIdx >= 0 ? obsIdx : (_devices.Count > 0 ? 0 : -1);
    }

    /// <summary>選択中のデバイス情報を返す</summary>
    public CameraDeviceInfo? SelectedDevice =>
        _selectedDeviceIndex >= 0 && _selectedDeviceIndex < _devices.Count
            ? _devices[_selectedDeviceIndex] : null;

    // -------------------------------------------------------
    // 設定画面用キャプチャ（MainVMが未開始時に独自で開始）
    // -------------------------------------------------------

    /// <summary>
    /// 設定画面のプレビュー用にCaptureEngineを開始する。
    /// 設定画面は停止中にのみ開かれるため、直接Startして良い。
    /// </summary>
    private void StartPreviewCapture()
    {
        if (_captureEngine == null || _captureEngine.IsRunning) return;

        var device = SelectedDevice;
        if (device == null && _devices.Count > 0)
            device = _devices[0];
        if (device == null) return;

        try { _captureEngine.Start(device); }
        catch { /* デバイスが開けない場合はプレビューなしで続行 */ }
    }

    /// <summary>
    /// 設定画面が開始したプレビュー用キャプチャを停止する。
    /// </summary>
    private void StopPreviewCapture()
    {
        if (_captureEngine != null && _captureEngine.IsRunning)
        {
            _captureEngine.FrameCaptured -= OnFrameCaptured;
            _captureEngine.Stop();
        }
    }

    public void Dispose()
    {
        _matchingCts?.Cancel();
        StopPreviewCapture();
        _frozenFrame?.Dispose();
        _matchingEngine.Dispose();
        if (_twitchOAuthService != null)
            _twitchOAuthService.AuthStateChanged -= OnTwitchAuthStateChanged;
        if (_historyService != null)
            _historyService.EntryAdded -= OnHistoryEntryAdded;
        _authCts?.Cancel();
        _authCts?.Dispose();
    }
}

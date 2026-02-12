using System.Windows;
using System.Windows.Threading;
using Chohan.Core.Twitch;

namespace Chohan.App.ViewModels;

/// <summary>
/// Twitch設定画面のViewModel。
/// OAuth認証フロー、接続状態表示、ログアウトを管理する。
/// </summary>
public class TwitchSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly TwitchOAuthService _oauthService;
    private readonly TwitchOAuthConfig _config;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _authCts;

    // -------------------------------------------------------
    // バインディングプロパティ: 接続状態
    // -------------------------------------------------------

    private bool _isAuthenticated;
    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        set
        {
            if (SetProperty(ref _isAuthenticated, value))
            {
                OnPropertyChanged(nameof(IsNotAuthenticated));
                OnPropertyChanged(nameof(ConnectionStatusText));
                OnPropertyChanged(nameof(StatusColorHex));
            }
        }
    }

    public bool IsNotAuthenticated => !_isAuthenticated;

    private string _userDisplayName = "";
    public string UserDisplayName
    {
        get => _userDisplayName;
        set => SetProperty(ref _userDisplayName, value);
    }

    private string _userLogin = "";
    public string UserLogin
    {
        get => _userLogin;
        set => SetProperty(ref _userLogin, value);
    }

    public string ConnectionStatusText => IsAuthenticated
        ? $"✓ 接続済み: {UserDisplayName} (@{UserLogin})"
        : "✗ 未接続";

    public string StatusColorHex => IsAuthenticated ? "#4CAF50" : "#FF4533";

    // -------------------------------------------------------
    // バインディングプロパティ: 設定入力
    // -------------------------------------------------------

    private string _clientId = "";
    public string ClientId
    {
        get => _clientId;
        set => SetProperty(ref _clientId, value);
    }

    private string _clientSecret = "";
    public string ClientSecret
    {
        get => _clientSecret;
        set => SetProperty(ref _clientSecret, value);
    }

    // -------------------------------------------------------
    // バインディングプロパティ: 操作状態
    // -------------------------------------------------------

    private bool _isAuthenticating;
    public bool IsAuthenticating
    {
        get => _isAuthenticating;
        set
        {
            if (SetProperty(ref _isAuthenticating, value))
                OnPropertyChanged(nameof(AuthButtonText));
        }
    }

    public string AuthButtonText => IsAuthenticating ? "認証中... (ブラウザを確認)" : "🔗 Twitchで認証する";

    private string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    // -------------------------------------------------------
    // コマンド
    // -------------------------------------------------------

    public RelayCommand AuthenticateCommand { get; }
    public RelayCommand LogoutCommand { get; }
    public RelayCommand CancelAuthCommand { get; }

    // -------------------------------------------------------
    // コンストラクタ
    // -------------------------------------------------------

    public TwitchSettingsViewModel()
        : this(new TwitchOAuthConfig(), new TwitchOAuthService(new TwitchOAuthConfig()))
    {
    }

    public TwitchSettingsViewModel(TwitchOAuthConfig config, TwitchOAuthService oauthService)
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _config = config;
        _oauthService = oauthService;

        // 初期値の反映
        ClientId = _config.ClientId;
        ClientSecret = _config.ClientSecret;

        // 認証状態の反映
        _oauthService.AuthStateChanged += OnAuthStateChanged;
        UpdateAuthStatus();

        // コマンド
        AuthenticateCommand = new RelayCommand(
            async () => await ExecuteAuthenticateAsync(),
            () => !IsAuthenticating && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret));
        LogoutCommand = new RelayCommand(
            async () => await ExecuteLogoutAsync(),
            () => IsAuthenticated);
        CancelAuthCommand = new RelayCommand(
            () => _authCts?.Cancel(),
            () => IsAuthenticating);
    }

    // -------------------------------------------------------
    // 認証実行
    // -------------------------------------------------------

    private async Task ExecuteAuthenticateAsync()
    {
        // 設定を反映
        _config.ClientId = ClientId.Trim();
        _config.ClientSecret = ClientSecret.Trim();

        if (!_config.IsValid)
        {
            StatusMessage = "Client IDとClient Secretを入力してください。";
            return;
        }

        IsAuthenticating = true;
        StatusMessage = "ブラウザでTwitchの認証ページを開いています...";

        _authCts = new CancellationTokenSource();

        try
        {
            var success = await _oauthService.AuthenticateAsync(_authCts.Token);
            if (success)
            {
                StatusMessage = "認証に成功しました！";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "認証がキャンセルされました。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"認証エラー: {ex.Message}";
        }
        finally
        {
            IsAuthenticating = false;
            _authCts?.Dispose();
            _authCts = null;
        }
    }

    // -------------------------------------------------------
    // ログアウト
    // -------------------------------------------------------

    private async Task ExecuteLogoutAsync()
    {
        StatusMessage = "ログアウト中...";
        await _oauthService.LogoutAsync();
        StatusMessage = "ログアウトしました。";
    }

    // -------------------------------------------------------
    // 認証状態変更
    // -------------------------------------------------------

    private void OnAuthStateChanged(bool isAuth, string message)
    {
        _dispatcher.BeginInvoke(() =>
        {
            UpdateAuthStatus();
            StatusMessage = message;
        });
    }

    private void UpdateAuthStatus()
    {
        IsAuthenticated = _oauthService.IsAuthenticated;
        var token = _oauthService.CurrentToken;
        if (token != null)
        {
            UserDisplayName = token.UserDisplayName;
            UserLogin = token.UserLogin;
        }
        else
        {
            UserDisplayName = "";
            UserLogin = "";
        }
    }

    // -------------------------------------------------------
    // 設定の取得
    // -------------------------------------------------------

    /// <summary>現在の設定を返す（保存用）</summary>
    public TwitchOAuthConfig GetConfig()
    {
        _config.ClientId = ClientId.Trim();
        _config.ClientSecret = ClientSecret.Trim();
        return _config;
    }

    public void Dispose()
    {
        _oauthService.AuthStateChanged -= OnAuthStateChanged;
        _authCts?.Cancel();
        _authCts?.Dispose();
    }
}

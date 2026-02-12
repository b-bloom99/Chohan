using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace Chohan.Core.Twitch;

/// <summary>
/// Twitch認可コードフロー (Authorization Code Flow) を実行するサービス。
/// 
/// フロー:
/// 1. ブラウザでTwitch認証URLを開く
/// 2. HttpListenerでlocalhostへのリダイレクトを待ち受け
/// 3. 認可コードを受け取り、Twitchサーバーとトークン交換
/// 4. アクセストークン・リフレッシュトークンをDPAPIで暗号化保存
/// 5. 期限切れ時にリフレッシュトークンで自動更新
/// </summary>
public class TwitchOAuthService : IDisposable
{
    private readonly TwitchOAuthConfig _config;
    private readonly TwitchTokenStore _tokenStore;
    private readonly HttpClient _httpClient;
    private TwitchTokenData? _currentToken;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>認証状態が変化したとき</summary>
    public event Action<bool, string>? AuthStateChanged;

    /// <summary>現在のトークンデータ</summary>
    public TwitchTokenData? CurrentToken => _currentToken;

    /// <summary>認証済みか</summary>
    public bool IsAuthenticated => _currentToken?.HasValidToken == true;

    /// <summary>ユーザー表示名</summary>
    public string? UserDisplayName => _currentToken?.UserDisplayName;

    /// <summary>ユーザーID (Broadcaster ID)</summary>
    public string? UserId => _currentToken?.UserId;

    public TwitchOAuthService(TwitchOAuthConfig config)
    {
        _config = config;
        _tokenStore = new TwitchTokenStore();
        _httpClient = new HttpClient();
    }

    // -------------------------------------------------------
    // 初期化（起動時に呼ぶ）
    // -------------------------------------------------------

    /// <summary>
    /// アプリ起動時に呼び出す。保存済みトークンの読み込みと検証を行う。
    /// 期限切れの場合は自動でリフレッシュを試みる。
    /// </summary>
    public async Task<bool> InitializeAsync()
    {
        _currentToken = _tokenStore.Load();
        if (_currentToken == null)
        {
            AuthStateChanged?.Invoke(false, "未認証");
            return false;
        }

        // 有効期限内ならバリデーションで確認
        if (_currentToken.HasValidToken)
        {
            var valid = await ValidateTokenAsync(_currentToken.AccessToken);
            if (valid)
            {
                AuthStateChanged?.Invoke(true, $"認証済み: {_currentToken.UserDisplayName}");
                return true;
            }
        }

        // 期限切れまたはバリデーション失敗 → リフレッシュ試行
        if (!string.IsNullOrEmpty(_currentToken.RefreshToken))
        {
            var refreshed = await RefreshAccessTokenAsync();
            if (refreshed)
            {
                AuthStateChanged?.Invoke(true, $"認証済み: {_currentToken!.UserDisplayName}");
                return true;
            }
        }

        // リフレッシュも失敗
        _currentToken = null;
        AuthStateChanged?.Invoke(false, "トークン期限切れ（再認証が必要）");
        return false;
    }

    // -------------------------------------------------------
    // 認可コードフロー（ブラウザ認証）
    // -------------------------------------------------------

    /// <summary>
    /// ブラウザでTwitch認証を開始し、トークンを取得する。
    /// CancellationTokenでタイムアウトやキャンセルが可能。
    /// </summary>
    public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        if (!_config.IsValid)
            throw new InvalidOperationException("Client IDまたはClient Secretが設定されていません。");

        // CSRF対策のstateパラメータを生成
        var state = Guid.NewGuid().ToString("N");

        // 認証URLを構築
        var authUrl = $"{TwitchOAuthConfig.AuthorizeUrl}" +
                      $"?client_id={Uri.EscapeDataString(_config.ClientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(_config.RedirectUri)}" +
                      $"&response_type=code" +
                      $"&scope={Uri.EscapeDataString(TwitchOAuthConfig.ScopeString)}" +
                      $"&state={state}" +
                      $"&force_verify=true";

        // HttpListenerでリダイレクトを待ち受け
        string? authCode = null;
        using var listener = new HttpListener();
        listener.Prefixes.Add(_config.RedirectUri);

        try
        {
            listener.Start();

            // ブラウザを開く
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            // コールバックを待つ（タイムアウト付き）
            var contextTask = listener.GetContextAsync();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5)); // 5分タイムアウト

            var completedTask = await Task.WhenAny(
                contextTask,
                Task.Delay(Timeout.Infinite, timeoutCts.Token));

            if (completedTask != contextTask)
            {
                AuthStateChanged?.Invoke(false, "認証がタイムアウトしました");
                return false;
            }

            var context = await contextTask;

            // クエリパラメータの解析
            var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "");
            var receivedState = query["state"];
            authCode = query["code"];
            var error = query["error"];

            // レスポンスをブラウザに返す
            string responseHtml;
            if (!string.IsNullOrEmpty(error))
            {
                responseHtml = BuildResponseHtml("認証エラー",
                    $"Twitchからエラーが返されました: {error}", false);
            }
            else if (receivedState != state)
            {
                responseHtml = BuildResponseHtml("セキュリティエラー",
                    "不正なstateパラメータが検出されました。認証を中止しました。", false);
                authCode = null;
            }
            else if (string.IsNullOrEmpty(authCode))
            {
                responseHtml = BuildResponseHtml("認証エラー",
                    "認可コードが取得できませんでした。", false);
            }
            else
            {
                responseHtml = BuildResponseHtml("認証成功！",
                    "Chohanとの連携が完了しました。このタブは閉じて構いません。", true);
            }

            var buffer = System.Text.Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentLength64 = buffer.Length;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(buffer, ct);
            context.Response.Close();
        }
        catch (OperationCanceledException)
        {
            AuthStateChanged?.Invoke(false, "認証がキャンセルされました");
            return false;
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }

        if (string.IsNullOrEmpty(authCode))
        {
            AuthStateChanged?.Invoke(false, "認証に失敗しました");
            return false;
        }

        // --- トークン交換 ---
        return await ExchangeCodeForTokenAsync(authCode, ct);
    }

    // -------------------------------------------------------
    // トークン交換
    // -------------------------------------------------------

    /// <summary>認可コードをアクセストークン・リフレッシュトークンに交換する。</summary>
    private async Task<bool> ExchangeCodeForTokenAsync(string authCode, CancellationToken ct)
    {
        try
        {
            var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _config.ClientId,
                ["client_secret"] = _config.ClientSecret,
                ["code"] = authCode,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = _config.RedirectUri
            });

            var response = await _httpClient.PostAsync(TwitchOAuthConfig.TokenUrl, requestBody, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                AuthStateChanged?.Invoke(false, $"トークン交換失敗: {response.StatusCode}");
                return false;
            }

            var tokenResponse = JsonSerializer.Deserialize<TwitchTokenResponse>(json);
            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                AuthStateChanged?.Invoke(false, "トークンレスポンスの解析に失敗しました");
                return false;
            }

            // ユーザー情報を取得
            var userInfo = await GetUserInfoAsync(tokenResponse.AccessToken, ct);

            // トークンデータを構築
            _currentToken = new TwitchTokenData
            {
                AccessToken = tokenResponse.AccessToken,
                RefreshToken = tokenResponse.RefreshToken ?? "",
                ExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn),
                UserId = userInfo?.UserId ?? "",
                UserLogin = userInfo?.Login ?? "",
                UserDisplayName = userInfo?.DisplayName ?? ""
            };

            // DPAPI暗号化して保存
            _tokenStore.Save(_currentToken);

            AuthStateChanged?.Invoke(true, $"認証成功: {_currentToken.UserDisplayName}");
            return true;
        }
        catch (Exception ex)
        {
            AuthStateChanged?.Invoke(false, $"トークン交換エラー: {ex.Message}");
            return false;
        }
    }

    // -------------------------------------------------------
    // トークンリフレッシュ
    // -------------------------------------------------------

    /// <summary>
    /// リフレッシュトークンを使って新しいアクセストークンを取得する。
    /// APIエラー (401) 発生時や起動時に自動で呼ばれる。
    /// </summary>
    public async Task<bool> RefreshAccessTokenAsync()
    {
        if (_currentToken == null || string.IsNullOrEmpty(_currentToken.RefreshToken))
            return false;

        // 多重リフレッシュ防止
        await _refreshLock.WaitAsync();
        try
        {
            // ロック取得中に別スレッドがリフレッシュ済みかチェック
            if (_currentToken.HasValidToken)
                return true;

            var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _config.ClientId,
                ["client_secret"] = _config.ClientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _currentToken.RefreshToken
            });

            var response = await _httpClient.PostAsync(TwitchOAuthConfig.TokenUrl, requestBody);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                // リフレッシュトークン自体が無効化された場合
                AuthStateChanged?.Invoke(false, "リフレッシュ失敗（再認証が必要）");
                return false;
            }

            var tokenResponse = JsonSerializer.Deserialize<TwitchTokenResponse>(json);
            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
                return false;

            // トークンを更新（ユーザー情報はそのまま保持）
            _currentToken.AccessToken = tokenResponse.AccessToken;
            _currentToken.RefreshToken = tokenResponse.RefreshToken ?? _currentToken.RefreshToken;
            _currentToken.ExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

            _tokenStore.Save(_currentToken);

            AuthStateChanged?.Invoke(true, $"トークン更新完了: {_currentToken.UserDisplayName}");
            return true;
        }
        catch (Exception ex)
        {
            AuthStateChanged?.Invoke(false, $"リフレッシュエラー: {ex.Message}");
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    // -------------------------------------------------------
    // トークン検証
    // -------------------------------------------------------

    /// <summary>アクセストークンが有効か検証する。</summary>
    private async Task<bool> ValidateTokenAsync(string accessToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, TwitchOAuthConfig.ValidateUrl);
            request.Headers.Add("Authorization", $"OAuth {accessToken}");

            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // -------------------------------------------------------
    // ユーザー情報取得
    // -------------------------------------------------------

    /// <summary>アクセストークンを使ってTwitchユーザー情報を取得する。</summary>
    private async Task<TwitchUserInfo?> GetUserInfoAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{TwitchOAuthConfig.HelixBaseUrl}/users");
            request.Headers.Add("Authorization", $"Bearer {accessToken}");
            request.Headers.Add("Client-Id", _config.ClientId);

            var response = await _httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return null;

            var usersResponse = JsonSerializer.Deserialize<TwitchUsersResponse>(json);
            return usersResponse?.Data?.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    // -------------------------------------------------------
    // API呼び出しヘルパー（自動リフレッシュ付き）
    // -------------------------------------------------------

    /// <summary>
    /// Twitch Helix APIを呼び出す汎用メソッド。
    /// 401エラー時に自動でリフレッシュをリトライする。
    /// </summary>
    public async Task<HttpResponseMessage> CallHelixApiAsync(
        HttpMethod method, string endpoint, HttpContent? content = null, CancellationToken ct = default)
    {
        if (_currentToken == null)
            throw new InvalidOperationException("Twitchに認証されていません。");

        // 期限切れチェック → 事前リフレッシュ
        if (_currentToken.IsExpired)
        {
            var refreshed = await RefreshAccessTokenAsync();
            if (!refreshed)
                throw new InvalidOperationException("トークンのリフレッシュに失敗しました。再認証してください。");
        }

        // 1回目のAPI呼び出し
        var response = await SendHelixRequestAsync(method, endpoint, content, ct);

        // 401 → リフレッシュして再試行
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var refreshed = await RefreshAccessTokenAsync();
            if (!refreshed)
                throw new InvalidOperationException("トークンのリフレッシュに失敗しました。再認証してください。");

            // リトライ
            response = await SendHelixRequestAsync(method, endpoint, content, ct);
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendHelixRequestAsync(
        HttpMethod method, string endpoint, HttpContent? content, CancellationToken ct)
    {
        var url = endpoint.StartsWith("http")
            ? endpoint
            : $"{TwitchOAuthConfig.HelixBaseUrl}{endpoint}";

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Authorization", $"Bearer {_currentToken!.AccessToken}");
        request.Headers.Add("Client-Id", _config.ClientId);

        if (content != null)
            request.Content = content;

        return await _httpClient.SendAsync(request, ct);
    }

    // -------------------------------------------------------
    // ログアウト
    // -------------------------------------------------------

    /// <summary>トークンを失効させてログアウトする。</summary>
    public async Task LogoutAsync()
    {
        if (_currentToken != null && !string.IsNullOrEmpty(_currentToken.AccessToken))
        {
            try
            {
                var revokeBody = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _config.ClientId,
                    ["token"] = _currentToken.AccessToken
                });
                await _httpClient.PostAsync(TwitchOAuthConfig.RevokeUrl, revokeBody);
            }
            catch { /* ベストエフォート */ }
        }

        _currentToken = null;
        _tokenStore.Delete();
        AuthStateChanged?.Invoke(false, "ログアウトしました");
    }

    // -------------------------------------------------------
    // ブラウザレスポンスHTML
    // -------------------------------------------------------

    private static string BuildResponseHtml(string title, string message, bool success)
    {
        var color = success ? "#4CAF50" : "#FF4533";
        var icon = success ? "✓" : "✗";
        return $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"><title>Chohan - {{title}}</title>
            <style>
                body { font-family: 'Segoe UI', sans-serif; background: #1E1E2E; color: #EEE;
                       display: flex; justify-content: center; align-items: center;
                       height: 100vh; margin: 0; }
                .card { text-align: center; background: #2D2D44; padding: 40px 60px;
                        border-radius: 12px; box-shadow: 0 4px 20px rgba(0,0,0,0.3); }
                .icon { font-size: 48px; color: {{color}}; margin-bottom: 16px; }
                h1 { margin: 0 0 8px; font-size: 22px; color: {{color}}; }
                p { color: #AAA; font-size: 14px; }
            </style></head>
            <body><div class="card">
                <div class="icon">{{icon}}</div>
                <h1>{{title}}</h1>
                <p>{{message}}</p>
            </div></body></html>
            """;
    }

    // -------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------

    public void Dispose()
    {
        _httpClient.Dispose();
        _refreshLock.Dispose();
    }
}

// -------------------------------------------------------
// JSONデシリアライズ用モデル
// -------------------------------------------------------

internal class TwitchTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string[]? Scope { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }
}

internal class TwitchUsersResponse
{
    [JsonPropertyName("data")]
    public List<TwitchUserInfo>? Data { get; set; }
}

internal class TwitchUserInfo
{
    [JsonPropertyName("id")]
    public string UserId { get; set; } = "";

    [JsonPropertyName("login")]
    public string Login { get; set; } = "";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";
}

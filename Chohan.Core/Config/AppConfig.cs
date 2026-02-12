using System.Text.Json.Serialization;

namespace Chohan.Core.Config;

/// <summary>
/// アプリ全体の共通設定 (config.json)。
/// 認証情報、環境設定、アクティブプロフィール名を保持する。
/// 
/// トークンはDPAPIで暗号化したBase64文字列として保存し、
/// JSONだけ盗んでも復号できない状態を保つ。
/// </summary>
public class AppConfig
{
    // -------------------------------------------------------
    // 認証情報（DPAPI暗号化された文字列として保存）
    // -------------------------------------------------------

    /// <summary>Twitch Client ID（平文で保存）</summary>
    [JsonPropertyName("twitch_client_id")]
    public string TwitchClientId { get; set; } = string.Empty;

    /// <summary>Twitch Client Secret（DPAPI暗号化Base64）</summary>
    [JsonPropertyName("twitch_client_secret_encrypted")]
    public string TwitchClientSecretEncrypted { get; set; } = string.Empty;

    /// <summary>Twitch Access Token（DPAPI暗号化Base64）</summary>
    [JsonPropertyName("twitch_access_token_encrypted")]
    public string TwitchAccessTokenEncrypted { get; set; } = string.Empty;

    /// <summary>Twitch Refresh Token（DPAPI暗号化Base64）</summary>
    [JsonPropertyName("twitch_refresh_token_encrypted")]
    public string TwitchRefreshTokenEncrypted { get; set; } = string.Empty;

    /// <summary>Twitchトークン有効期限 (UTC)</summary>
    [JsonPropertyName("twitch_token_expires_at")]
    public DateTime TwitchTokenExpiresAt { get; set; }

    /// <summary>Twitch ユーザーID</summary>
    [JsonPropertyName("twitch_user_id")]
    public string TwitchUserId { get; set; } = string.Empty;

    /// <summary>Twitch ユーザーログイン名</summary>
    [JsonPropertyName("twitch_user_login")]
    public string TwitchUserLogin { get; set; } = string.Empty;

    /// <summary>Twitch ユーザー表示名</summary>
    [JsonPropertyName("twitch_user_display_name")]
    public string TwitchUserDisplayName { get; set; } = string.Empty;

    // -------------------------------------------------------
    // 環境設定
    // -------------------------------------------------------

    /// <summary>最後に使用したカメラデバイス名</summary>
    [JsonPropertyName("last_camera_device")]
    public string LastCameraDevice { get; set; } = string.Empty;

    /// <summary>ウィンドウ表示位置 X</summary>
    [JsonPropertyName("window_x")]
    public double WindowX { get; set; } = double.NaN;

    /// <summary>ウィンドウ表示位置 Y</summary>
    [JsonPropertyName("window_y")]
    public double WindowY { get; set; } = double.NaN;

    /// <summary>ウィンドウ幅</summary>
    [JsonPropertyName("window_width")]
    public double WindowWidth { get; set; } = 280;

    /// <summary>ウィンドウ高さ</summary>
    [JsonPropertyName("window_height")]
    public double WindowHeight { get; set; } = 120;

    // -------------------------------------------------------
    // アクティブプロフィール
    // -------------------------------------------------------

    /// <summary>前回終了時に読み込んでいたプロフィール名（拡張子なし）</summary>
    [JsonPropertyName("active_profile")]
    public string ActiveProfile { get; set; } = "Default";
}

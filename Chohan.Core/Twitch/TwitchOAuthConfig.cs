namespace Chohan.Core.Twitch;

/// <summary>
/// Twitch OAuth認証に必要な設定値を保持するクラス。
/// Client IDはTwitch Developer Consoleで取得したものを設定する。
/// </summary>
public class TwitchOAuthConfig
{
    /// <summary>Twitch Developer Consoleで発行したClient ID</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Twitch Developer Consoleで発行したClient Secret</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>ローカルリダイレクトURI (HttpListenerで受け取る)</summary>
    public string RedirectUri { get; set; } = "http://localhost:8080/callback/";

    /// <summary>必要なスコープ</summary>
    public static readonly string[] RequiredScopes =
    [
        "channel:read:predictions",
        "channel:manage:predictions"
    ];

    /// <summary>スコープをスペース区切り文字列にしたもの</summary>
    public static string ScopeString => string.Join(" ", RequiredScopes);

    /// <summary>Twitch認証エンドポイント</summary>
    public const string AuthorizeUrl = "https://id.twitch.tv/oauth2/authorize";

    /// <summary>Twitchトークンエンドポイント</summary>
    public const string TokenUrl = "https://id.twitch.tv/oauth2/token";

    /// <summary>Twitchトークン検証エンドポイント</summary>
    public const string ValidateUrl = "https://id.twitch.tv/oauth2/validate";

    /// <summary>Twitchトークン失効エンドポイント</summary>
    public const string RevokeUrl = "https://id.twitch.tv/oauth2/revoke";

    /// <summary>Twitch Helix APIベースURL</summary>
    public const string HelixBaseUrl = "https://api.twitch.tv/helix";

    /// <summary>設定が有効か（最低限ClientIdが設定されているか）</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

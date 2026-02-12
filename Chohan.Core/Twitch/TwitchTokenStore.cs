using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chohan.Core.Twitch;

/// <summary>
/// Twitchのアクセストークン・リフレッシュトークンを
/// Windows DPAPI (DataProtectionScope.CurrentUser) で暗号化してローカルに保存する。
/// そのPCの、そのWindowsユーザーだけが復号可能。
/// </summary>
public class TwitchTokenStore
{
    private readonly string _filePath;

    public TwitchTokenStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Chohan", "twitch_tokens.dat"))
    {
    }

    /// <summary>保存先パスを指定可能なコンストラクタ（ポータブル対応）。</summary>
    public TwitchTokenStore(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// トークンを暗号化して保存する。
    /// </summary>
    public void Save(TwitchTokenData tokenData)
    {
        var json = JsonSerializer.Serialize(tokenData);
        var plainBytes = Encoding.UTF8.GetBytes(json);

        // DPAPIで暗号化（CurrentUserスコープ = このWindowsユーザーのみ復号可能）
        var encryptedBytes = ProtectedData.Protect(
            plainBytes, null, DataProtectionScope.CurrentUser);

        File.WriteAllBytes(_filePath, encryptedBytes);
    }

    /// <summary>
    /// 保存されたトークンを復号して読み出す。
    /// 保存データがない場合や復号失敗時はnullを返す。
    /// </summary>
    public TwitchTokenData? Load()
    {
        if (!File.Exists(_filePath))
            return null;

        try
        {
            var encryptedBytes = File.ReadAllBytes(_filePath);
            var plainBytes = ProtectedData.Unprotect(
                encryptedBytes, null, DataProtectionScope.CurrentUser);

            var json = Encoding.UTF8.GetString(plainBytes);
            return JsonSerializer.Deserialize<TwitchTokenData>(json);
        }
        catch (CryptographicException)
        {
            // 別ユーザーのデータ、または改竄 → 破棄
            Delete();
            return null;
        }
        catch (JsonException)
        {
            Delete();
            return null;
        }
    }

    /// <summary>
    /// 保存されたトークンを削除する。
    /// </summary>
    public void Delete()
    {
        if (File.Exists(_filePath))
        {
            try { File.Delete(_filePath); } catch { }
        }
    }

    /// <summary>保存済みトークンが存在するか</summary>
    public bool Exists() => File.Exists(_filePath);
}

/// <summary>
/// 保存するトークンデータ。
/// </summary>
public class TwitchTokenData
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserLogin { get; set; } = string.Empty;
    public string UserDisplayName { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }

    /// <summary>トークンが有効期限切れか（30秒のマージン付き）</summary>
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt.AddSeconds(-30);

    /// <summary>有効なアクセストークンを持っているか</summary>
    public bool HasValidToken => !string.IsNullOrEmpty(AccessToken) && !IsExpired;
}

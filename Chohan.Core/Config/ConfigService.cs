using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chohan.Core.Recognition;
using Chohan.Core.Twitch;
using OpenCvSharp;

namespace Chohan.Core.Config;

/// <summary>
/// アプリの全設定を管理するサービス。
/// 
/// ディレクトリ構成（実行ファイルと同一フォルダ = ポータブル形式）:
///   Chohan.exe
///   config.json          ← AppConfig
///   Profiles/
///     Default.json       ← GameProfile
///     StreetFighter6.json
///   Templates/
///     default_start.png  ← ROI切り抜き画像
/// </summary>
public class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    // -------------------------------------------------------
    // パス
    // -------------------------------------------------------

    /// <summary>アプリのルートディレクトリ（EXEと同階層）</summary>
    public string RootDir { get; }

    /// <summary>config.json のパス</summary>
    public string ConfigPath => Path.Combine(RootDir, "config.json");

    /// <summary>Profiles/ フォルダのパス</summary>
    public string ProfilesDir => Path.Combine(RootDir, "Profiles");

    /// <summary>Templates/ フォルダのパス</summary>
    public string TemplatesDir => Path.Combine(RootDir, "Templates");

    // -------------------------------------------------------
    // 現在の設定
    // -------------------------------------------------------

    /// <summary>共通設定</summary>
    public AppConfig Config { get; private set; } = new();

    /// <summary>現在アクティブなプロフィール</summary>
    public GameProfile ActiveProfile { get; private set; } = new();

    /// <summary>利用可能なプロフィール名一覧</summary>
    public List<string> AvailableProfiles { get; private set; } = [];

    /// <summary>設定変更イベント</summary>
    public event Action? ConfigChanged;

    /// <summary>プロフィール変更イベント</summary>
    public event Action<GameProfile>? ProfileChanged;

    // -------------------------------------------------------
    // コンストラクタ
    // -------------------------------------------------------

    public ConfigService()
        : this(AppContext.BaseDirectory)
    {
    }

    public ConfigService(string rootDir)
    {
        RootDir = rootDir;
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(TemplatesDir);
    }

    // -------------------------------------------------------
    // 起動時ロード
    // -------------------------------------------------------

    /// <summary>
    /// アプリ起動時に呼ぶ。config.json とアクティブプロフィールを読み込む。
    /// </summary>
    public void Load()
    {
        // config.json 読み込み
        Config = LoadJson<AppConfig>(ConfigPath) ?? new AppConfig();

        // プロフィール一覧を更新
        RefreshProfileList();

        // アクティブプロフィールをロード
        var profileName = string.IsNullOrWhiteSpace(Config.ActiveProfile)
            ? "Default" : Config.ActiveProfile;
        LoadProfile(profileName);
    }

    // -------------------------------------------------------
    // config.json の保存
    // -------------------------------------------------------

    /// <summary>共通設定を即座にconfig.jsonに保存する（オートセーブ）。</summary>
    public void SaveConfig()
    {
        SaveJson(ConfigPath, Config);
        ConfigChanged?.Invoke();
    }

    // -------------------------------------------------------
    // Twitch認証情報の読み書き（DPAPI暗号化）
    // -------------------------------------------------------

    /// <summary>TwitchOAuthConfigとトークンをconfig.jsonに保存する。</summary>
    public void SaveTwitchAuth(TwitchOAuthConfig oauthConfig, TwitchTokenData? tokenData)
    {
        Config.TwitchClientId = oauthConfig.ClientId;
        Config.TwitchClientSecretEncrypted = DpapiHelper.Encrypt(oauthConfig.ClientSecret);

        if (tokenData != null)
        {
            Config.TwitchAccessTokenEncrypted = DpapiHelper.Encrypt(tokenData.AccessToken);
            Config.TwitchRefreshTokenEncrypted = DpapiHelper.Encrypt(tokenData.RefreshToken);
            Config.TwitchTokenExpiresAt = tokenData.ExpiresAt;
            Config.TwitchUserId = tokenData.UserId;
            Config.TwitchUserLogin = tokenData.UserLogin;
            Config.TwitchUserDisplayName = tokenData.UserDisplayName;
        }
        else
        {
            Config.TwitchAccessTokenEncrypted = string.Empty;
            Config.TwitchRefreshTokenEncrypted = string.Empty;
            Config.TwitchTokenExpiresAt = default;
            Config.TwitchUserId = string.Empty;
            Config.TwitchUserLogin = string.Empty;
            Config.TwitchUserDisplayName = string.Empty;
        }

        SaveConfig();
    }

    /// <summary>config.jsonからTwitch認証情報を復元する。</summary>
    public (TwitchOAuthConfig Config, TwitchTokenData? Token) LoadTwitchAuth()
    {
        var oauthConfig = new TwitchOAuthConfig
        {
            ClientId = Config.TwitchClientId,
            ClientSecret = DpapiHelper.Decrypt(Config.TwitchClientSecretEncrypted)
        };

        TwitchTokenData? token = null;
        var accessToken = DpapiHelper.Decrypt(Config.TwitchAccessTokenEncrypted);
        var refreshToken = DpapiHelper.Decrypt(Config.TwitchRefreshTokenEncrypted);

        if (!string.IsNullOrEmpty(accessToken) || !string.IsNullOrEmpty(refreshToken))
        {
            token = new TwitchTokenData
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = Config.TwitchTokenExpiresAt,
                UserId = Config.TwitchUserId,
                UserLogin = Config.TwitchUserLogin,
                UserDisplayName = Config.TwitchUserDisplayName
            };
        }

        return (oauthConfig, token);
    }

    // -------------------------------------------------------
    // プロフィール管理
    // -------------------------------------------------------

    /// <summary>プロフィール一覧を再読み込みする。</summary>
    public void RefreshProfileList()
    {
        AvailableProfiles = Directory.Exists(ProfilesDir)
            ? Directory.GetFiles(ProfilesDir, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(n => n)
                .ToList()
            : [];

        // Defaultが無ければ作成
        if (!AvailableProfiles.Contains("Default"))
        {
            SaveProfile(new GameProfile { Name = "Default" });
            AvailableProfiles.Insert(0, "Default");
        }
    }

    /// <summary>指定名のプロフィールをロードしてアクティブにする。</summary>
    public void LoadProfile(string profileName)
    {
        var path = GetProfilePath(profileName);
        var profile = LoadJson<GameProfile>(path);

        if (profile == null)
        {
            profile = new GameProfile { Name = profileName };
            SaveProfile(profile);
        }

        ActiveProfile = profile;
        Config.ActiveProfile = profileName;
        SaveConfig();

        ProfileChanged?.Invoke(ActiveProfile);
    }

    /// <summary>プロフィールを保存する（オートセーブ）。</summary>
    public void SaveProfile(GameProfile profile)
    {
        var path = GetProfilePath(profile.Name);
        SaveJson(path, profile);
    }

    /// <summary>アクティブプロフィールを保存する。</summary>
    public void SaveActiveProfile()
    {
        SaveProfile(ActiveProfile);
    }

    /// <summary>新しいプロフィールを作成する。</summary>
    public GameProfile CreateProfile(string name)
    {
        var profile = new GameProfile { Name = name };
        SaveProfile(profile);
        RefreshProfileList();
        return profile;
    }

    /// <summary>プロフィールを削除する。Defaultは削除不可。</summary>
    public bool DeleteProfile(string name)
    {
        if (name == "Default") return false;

        var path = GetProfilePath(name);
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { return false; }
        }

        // そのプロフィールに紐づくテンプレート画像も削除
        DeleteProfileTemplates(name);

        RefreshProfileList();

        // アクティブだった場合はDefaultに戻す
        if (Config.ActiveProfile == name)
            LoadProfile("Default");

        return true;
    }

    /// <summary>プロフィール名を変更する。</summary>
    public bool RenameProfile(string oldName, string newName)
    {
        if (oldName == "Default" || string.IsNullOrWhiteSpace(newName)) return false;

        var oldPath = GetProfilePath(oldName);
        var newPath = GetProfilePath(newName);
        if (!File.Exists(oldPath) || File.Exists(newPath)) return false;

        try
        {
            File.Move(oldPath, newPath);

            // テンプレート画像のリネーム
            RenameProfileTemplates(oldName, newName);

            RefreshProfileList();

            if (Config.ActiveProfile == oldName)
            {
                Config.ActiveProfile = newName;
                SaveConfig();
            }

            return true;
        }
        catch { return false; }
    }

    // -------------------------------------------------------
    // プロフィール ↔ TriggerConfig 変換
    // -------------------------------------------------------

    /// <summary>
    /// アクティブプロフィールからトリガー設定を生成する。
    /// テンプレート画像のロードも行う。
    /// </summary>
    public Dictionary<string, TriggerConfig> LoadTriggersFromProfile()
    {
        var profile = ActiveProfile;
        var triggers = new Dictionary<string, TriggerConfig>
        {
            ["start"] = EntryToTriggerConfig("start", "開始", profile.TriggerStart),
            ["win"]   = EntryToTriggerConfig("win",   "勝利", profile.TriggerWin),
            ["lose"]  = EntryToTriggerConfig("lose",  "敗北", profile.TriggerLose),
        };

        return triggers;
    }

    /// <summary>
    /// トリガー設定をアクティブプロフィールに書き戻して保存する。
    /// </summary>
    public void SaveTriggersToProfile(Dictionary<string, TriggerConfig> triggers)
    {
        if (triggers.TryGetValue("start", out var start))
            ActiveProfile.TriggerStart = TriggerConfigToEntry(start);
        if (triggers.TryGetValue("win", out var win))
            ActiveProfile.TriggerWin = TriggerConfigToEntry(win);
        if (triggers.TryGetValue("lose", out var lose))
            ActiveProfile.TriggerLose = TriggerConfigToEntry(lose);

        SaveActiveProfile();
    }

    /// <summary>テンプレート画像の保存パスを生成する。</summary>
    public string GetTemplatePath(string profileName, string triggerName)
    {
        var safeName = SanitizeFileName(profileName).ToLowerInvariant();
        return Path.Combine(TemplatesDir, $"{safeName}_{triggerName}.png");
    }

    // -------------------------------------------------------
    // ヘルパー: TriggerEntry ↔ TriggerConfig 変換
    // -------------------------------------------------------

    private TriggerConfig EntryToTriggerConfig(string name, string displayName, TriggerEntry entry)
    {
        // テンプレートパスを絶対パスに解決
        var templateAbsPath = string.IsNullOrEmpty(entry.TemplatePath)
            ? string.Empty
            : Path.IsPathRooted(entry.TemplatePath)
                ? entry.TemplatePath
                : Path.Combine(RootDir, entry.TemplatePath);

        var config = new TriggerConfig
        {
            Name = name,
            DisplayName = displayName,
            RoiRect = new OpenCvSharp.Rect(entry.RoiX, entry.RoiY, entry.RoiWidth, entry.RoiHeight),
            Threshold = entry.Threshold,
            TemplatePath = templateAbsPath
        };

        // テンプレート画像をロード
        if (!string.IsNullOrEmpty(templateAbsPath))
            config.LoadTemplate();

        return config;
    }

    private TriggerEntry TriggerConfigToEntry(TriggerConfig config)
    {
        // パスを相対パスに変換（ポータブル対応）
        var relativePath = string.IsNullOrEmpty(config.TemplatePath)
            ? string.Empty
            : MakeRelativePath(config.TemplatePath);

        return new TriggerEntry
        {
            RoiX = config.RoiRect.X,
            RoiY = config.RoiRect.Y,
            RoiWidth = config.RoiRect.Width,
            RoiHeight = config.RoiRect.Height,
            Threshold = config.Threshold,
            TemplatePath = relativePath
        };
    }

    // -------------------------------------------------------
    // ウィンドウ位置の保存/復元
    // -------------------------------------------------------

    /// <summary>ウィンドウ位置を保存する。</summary>
    public void SaveWindowPosition(double x, double y, double width, double height)
    {
        Config.WindowX = x;
        Config.WindowY = y;
        Config.WindowWidth = width;
        Config.WindowHeight = height;
        SaveConfig();
    }

    /// <summary>最後に使用したカメラデバイス名を保存する。</summary>
    public void SaveLastCamera(string deviceName)
    {
        Config.LastCameraDevice = deviceName;
        SaveConfig();
    }

    // -------------------------------------------------------
    // JSON I/O
    // -------------------------------------------------------

    private static T? LoadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveJson<T>(string path, T obj)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(obj, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // ファイル書き込み失敗は安全に無視（ログに出すべきだが簡易版）
        }
    }

    // -------------------------------------------------------
    // ファイルパスユーティリティ
    // -------------------------------------------------------

    private string GetProfilePath(string profileName)
    {
        var safeName = SanitizeFileName(profileName);
        return Path.Combine(ProfilesDir, $"{safeName}.json");
    }

    private string MakeRelativePath(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath)) return string.Empty;

        try
        {
            var rootUri = new Uri(RootDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var fileUri = new Uri(absolutePath);

            if (rootUri.IsBaseOf(fileUri))
            {
                return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString())
                    .Replace('/', Path.DirectorySeparatorChar);
            }
        }
        catch { }

        return absolutePath; // 相対化できなければ絶対パスのまま
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private void DeleteProfileTemplates(string profileName)
    {
        var prefix = SanitizeFileName(profileName).ToLowerInvariant();
        if (!Directory.Exists(TemplatesDir)) return;

        foreach (var file in Directory.GetFiles(TemplatesDir, $"{prefix}_*.png"))
        {
            try { File.Delete(file); } catch { }
        }
    }

    private void RenameProfileTemplates(string oldName, string newName)
    {
        var oldPrefix = SanitizeFileName(oldName).ToLowerInvariant();
        var newPrefix = SanitizeFileName(newName).ToLowerInvariant();
        if (!Directory.Exists(TemplatesDir)) return;

        foreach (var file in Directory.GetFiles(TemplatesDir, $"{oldPrefix}_*.png"))
        {
            var newFile = file.Replace(oldPrefix, newPrefix);
            try { File.Move(file, newFile); } catch { }
        }
    }
}

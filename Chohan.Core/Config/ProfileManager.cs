using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chohan.Core.Recognition;
using OpenCvSharp;

namespace Chohan.Core.Config;

/// <summary>
/// プロファイル管理サービス。
/// 
/// フォルダ構造:
///   %AppData%/Chohan/
///     Profiles.json               ← プロファイル一覧・アクティブ管理
///     Profiles/
///       MarioMaker/
///         config.json             ← ROI座標、閾値、投票設定
///         template_start.png
///         template_win.png
///         template_lose.png
///       StreetFighter/
///         config.json
///         ...
/// 
/// 責務:
/// ① プロファイルのCRUD（フォルダ作成/読み込み/削除）
/// ② config.json のシリアライズ/デシリアライズ（ROI永続化）
/// ③ アクティブプロファイルの切り替え
/// </summary>
public class ProfileManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    // -------------------------------------------------------
    // パス
    // -------------------------------------------------------

    /// <summary>AppData/Roaming/Chohan/</summary>
    public string RootDir { get; }

    /// <summary>Profiles.json のパス</summary>
    public string IndexPath => Path.Combine(RootDir, "Profiles.json");

    /// <summary>Profiles/ フォルダのパス</summary>
    public string ProfilesDir => Path.Combine(RootDir, "Profiles");

    // -------------------------------------------------------
    // 状態
    // -------------------------------------------------------

    /// <summary>プロファイル管理情報 (Profiles.json)</summary>
    public ProfileIndex Index { get; private set; } = new();

    /// <summary>現在アクティブなプロファイル設定</summary>
    public ProfileConfig ActiveConfig { get; private set; } = new();

    /// <summary>アクティブプロファイルのフォルダパス</summary>
    public string ActiveProfileDir => Path.Combine(ProfilesDir, SanitizeName(Index.ActiveProfile));

    /// <summary>利用可能なプロファイル名一覧</summary>
    public List<string> AvailableProfiles { get; private set; } = [];

    /// <summary>プロファイル変更イベント</summary>
    public event Action<string, ProfileConfig>? ProfileChanged;

    // -------------------------------------------------------
    // コンストラクタ
    // -------------------------------------------------------

    public ProfileManager()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chohan"))
    {
    }

    public ProfileManager(string rootDir)
    {
        RootDir = rootDir;
        Directory.CreateDirectory(ProfilesDir);
    }

    // -------------------------------------------------------
    // 初期化（起動時）
    // -------------------------------------------------------

    /// <summary>起動時に呼ぶ。Profiles.jsonとアクティブプロファイルを読み込む。</summary>
    public void Load()
    {
        Index = LoadJson<ProfileIndex>(IndexPath) ?? new ProfileIndex();
        RefreshList();

        var name = string.IsNullOrWhiteSpace(Index.ActiveProfile) ? "Default" : Index.ActiveProfile;
        SwitchProfile(name);
    }

    // -------------------------------------------------------
    // プロファイル一覧
    // -------------------------------------------------------

    /// <summary>Profiles/配下のサブフォルダを走査して一覧を更新する。</summary>
    public void RefreshList()
    {
        AvailableProfiles = Directory.Exists(ProfilesDir)
            ? Directory.GetDirectories(ProfilesDir)
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .Select(n => n!)
                .OrderBy(n => n)
                .ToList()
            : [];

        // Defaultが無ければ作る
        if (!AvailableProfiles.Contains("Default"))
        {
            CreateProfile("Default");
        }
    }

    // -------------------------------------------------------
    // プロファイル切替
    // -------------------------------------------------------

    /// <summary>指定プロファイルをロードしてアクティブにする。</summary>
    public void SwitchProfile(string profileName)
    {
        var dir = GetProfileDir(profileName);
        var configPath = Path.Combine(dir, "config.json");

        // フォルダが無ければ新規作成
        if (!Directory.Exists(dir))
            CreateProfile(profileName);

        ActiveConfig = LoadJson<ProfileConfig>(configPath) ?? new ProfileConfig { Name = profileName };
        ActiveConfig.Name = profileName; // 万が一ずれていたら修正

        Index.ActiveProfile = profileName;
        SaveIndex();

        ProfileChanged?.Invoke(profileName, ActiveConfig);
    }

    // -------------------------------------------------------
    // プロファイル CRUD
    // -------------------------------------------------------

    /// <summary>新規プロファイルを作成する。</summary>
    public ProfileConfig CreateProfile(string name)
    {
        var dir = GetProfileDir(name);
        Directory.CreateDirectory(dir);

        var config = new ProfileConfig { Name = name };
        SaveProfileConfig(name, config);
        RefreshList();
        return config;
    }

    /// <summary>プロファイルを削除する。Defaultは削除不可。</summary>
    public bool DeleteProfile(string name)
    {
        if (name == "Default") return false;

        var dir = GetProfileDir(name);
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, true); } catch { return false; }
        }

        RefreshList();

        if (Index.ActiveProfile == name)
            SwitchProfile("Default");

        return true;
    }

    /// <summary>プロファイル名を変更する。</summary>
    public bool RenameProfile(string oldName, string newName)
    {
        if (oldName == "Default" || string.IsNullOrWhiteSpace(newName)) return false;

        var oldDir = GetProfileDir(oldName);
        var newDir = GetProfileDir(newName);
        if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return false;

        try
        {
            Directory.Move(oldDir, newDir);

            // config.json内のNameも更新
            var config = LoadJson<ProfileConfig>(Path.Combine(newDir, "config.json"));
            if (config != null)
            {
                config.Name = newName;
                SaveJson(Path.Combine(newDir, "config.json"), config);
            }

            RefreshList();

            if (Index.ActiveProfile == oldName)
            {
                Index.ActiveProfile = newName;
                SaveIndex();
            }
            return true;
        }
        catch { return false; }
    }

    // -------------------------------------------------------
    // config.json 永続化
    // -------------------------------------------------------

    /// <summary>アクティブプロファイルのconfig.jsonを保存する。</summary>
    public void SaveActiveConfig()
    {
        SaveProfileConfig(Index.ActiveProfile, ActiveConfig);
    }

    /// <summary>指定プロファイルのconfig.jsonを保存する。</summary>
    public void SaveProfileConfig(string profileName, ProfileConfig config)
    {
        var dir = GetProfileDir(profileName);
        Directory.CreateDirectory(dir);
        SaveJson(Path.Combine(dir, "config.json"), config);
    }

    // -------------------------------------------------------
    // TriggerConfig ↔ ProfileConfig 変換
    // -------------------------------------------------------

    /// <summary>アクティブプロファイルからTriggerConfig辞書を復元する。テンプレート画像もロード。</summary>
    public Dictionary<string, TriggerConfig> LoadTriggers()
    {
        var dir = ActiveProfileDir;
        return new Dictionary<string, TriggerConfig>
        {
            ["start"] = EntryToTrigger("start", "開始", ActiveConfig.TriggerStart, dir),
            ["win"]   = EntryToTrigger("win",   "勝利", ActiveConfig.TriggerWin,   dir),
            ["lose"]  = EntryToTrigger("lose",  "敗北", ActiveConfig.TriggerLose,  dir),
        };
    }

    /// <summary>TriggerConfig辞書をアクティブプロファイルに書き戻して保存する。</summary>
    public void SaveTriggers(Dictionary<string, TriggerConfig> triggers)
    {
        if (triggers.TryGetValue("start", out var s))
            ActiveConfig.TriggerStart = TriggerToEntry(s);
        if (triggers.TryGetValue("win", out var w))
            ActiveConfig.TriggerWin = TriggerToEntry(w);
        if (triggers.TryGetValue("lose", out var l))
            ActiveConfig.TriggerLose = TriggerToEntry(l);

        SaveActiveConfig();
    }

    /// <summary>テンプレート画像の保存パスを生成する。</summary>
    public string GetTemplatePath(string triggerName)
    {
        return Path.Combine(ActiveProfileDir, $"template_{triggerName}.png");
    }

    // -------------------------------------------------------
    // 内部ヘルパー
    // -------------------------------------------------------

    private TriggerConfig EntryToTrigger(string name, string displayName, TriggerEntry entry, string profileDir)
    {
        var templatePath = string.IsNullOrEmpty(entry.TemplatePath)
            ? string.Empty
            : Path.IsPathRooted(entry.TemplatePath)
                ? entry.TemplatePath
                : Path.Combine(profileDir, entry.TemplatePath);

        var config = new TriggerConfig
        {
            Name = name,
            DisplayName = displayName,
            RoiRect = new OpenCvSharp.Rect(entry.RoiX, entry.RoiY, entry.RoiWidth, entry.RoiHeight),
            Threshold = entry.Threshold,
            TemplatePath = templatePath
        };

        if (!string.IsNullOrEmpty(templatePath))
            config.LoadTemplate();

        return config;
    }

    private static TriggerEntry TriggerToEntry(TriggerConfig config)
    {
        // テンプレートパスをファイル名のみにして保存（プロファイルフォルダ基準の相対）
        var relPath = string.IsNullOrEmpty(config.TemplatePath)
            ? string.Empty
            : Path.GetFileName(config.TemplatePath);

        return new TriggerEntry
        {
            RoiX = config.RoiRect.X,
            RoiY = config.RoiRect.Y,
            RoiWidth = config.RoiRect.Width,
            RoiHeight = config.RoiRect.Height,
            Threshold = config.Threshold,
            TemplatePath = relPath
        };
    }

    private void SaveIndex()
    {
        SaveJson(IndexPath, Index);
    }

    private string GetProfileDir(string profileName)
    {
        return Path.Combine(ProfilesDir, SanitizeName(profileName));
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static T? LoadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch { return null; }
    }

    private static void SaveJson<T>(string path, T obj)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(obj, JsonOpts));
        }
        catch { }
    }
}

// -------------------------------------------------------
// データモデル
// -------------------------------------------------------

/// <summary>Profiles.json — プロファイル一覧管理</summary>
public class ProfileIndex
{
    [JsonPropertyName("active_profile")]
    public string ActiveProfile { get; set; } = "Default";

    [JsonPropertyName("profiles")]
    public List<string> Profiles { get; set; } = [];
}

/// <summary>Profiles/{Name}/config.json — プロファイルごとの全設定</summary>
public class ProfileConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Default";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    // --- トリガー ---
    [JsonPropertyName("trigger_start")]
    public TriggerEntry TriggerStart { get; set; } = new();

    [JsonPropertyName("trigger_win")]
    public TriggerEntry TriggerWin { get; set; } = new();

    [JsonPropertyName("trigger_lose")]
    public TriggerEntry TriggerLose { get; set; } = new();

    // --- 投票設定 ---
    [JsonPropertyName("prediction_title")]
    public string PredictionTitle { get; set; } = "次の結果は？";

    [JsonPropertyName("outcome_win_label")]
    public string OutcomeWinLabel { get; set; } = "勝利";

    [JsonPropertyName("outcome_lose_label")]
    public string OutcomeLoseLabel { get; set; } = "敗北";

    [JsonPropertyName("prediction_duration_seconds")]
    public int PredictionDurationSeconds { get; set; } = 60;

    // --- デバイス ---
    [JsonPropertyName("camera_device")]
    public string CameraDevice { get; set; } = string.Empty;

    // --- 常時投票モード ---
    [JsonPropertyName("always_voting_mode")]
    public bool AlwaysVotingMode { get; set; }

    // --- 結果確定→次の投票再開までの待機秒数 ---
    [JsonPropertyName("resolved_delay_seconds")]
    public int ResolvedDelaySeconds { get; set; } = 5;
}

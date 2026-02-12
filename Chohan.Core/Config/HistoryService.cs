using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chohan.Core.Config;

/// <summary>
/// 判定履歴の管理サービス。
/// 各プロファイルフォルダ内の history.json に判定ログを蓄積する。
/// </summary>
public class HistoryService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() } // グローバルにEnumを文字列変換
    };

    private readonly ProfileManager _profileManager;
    private HistoryData _current = new();

    /// <summary>新しいエントリ追加時に発火</summary>
    public event Action<HistoryEntry>? EntryAdded;

    public HistoryData Current => _current;

    public HistoryService(ProfileManager profileManager)
    {
        _profileManager = profileManager;
    }

    private string HistoryPath =>
        Path.Combine(_profileManager.ActiveProfileDir, "history.json");

    public void Load()
    {
        _current = LoadJson(HistoryPath) ?? new HistoryData();
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(HistoryPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(_current, JsonOpts));
        }
        catch { }
    }

    // --- 記録メソッド (引数に status を追加) ---

    public void RecordStart(double confidence, string? predictionId, PredictionStatus status)
        => AddEntry(HistoryEventType.VoteStarted, confidence, predictionId, status);

    public void RecordWin(double confidence, string? predictionId, PredictionStatus status)
        => AddEntry(HistoryEventType.Win, confidence, predictionId, status);

    public void RecordLose(double confidence, string? predictionId, PredictionStatus status)
        => AddEntry(HistoryEventType.Lose, confidence, predictionId, status);

    // --- 内部処理 ---

    private void AddEntry(HistoryEventType eventType, double confidence, string? predictionId, PredictionStatus status)
    {
        var entry = new HistoryEntry
        {
            Timestamp = DateTime.UtcNow,
            EventType = eventType,
            Confidence = Math.Round(confidence, 4),
            PredictionId = predictionId ?? string.Empty,
            PredictionStatus = status // ステータスを保存
        };
        _current.Entries.Add(entry);
        Save();
        EntryAdded?.Invoke(entry);
    }

    // --- 統計 ---
    public int WinCount => _current.Entries.Count(e => e.EventType == HistoryEventType.Win);
    public int LoseCount => _current.Entries.Count(e => e.EventType == HistoryEventType.Lose);
    public int TotalMatches => WinCount + LoseCount;
    public double WinRate => TotalMatches > 0 ? (double)WinCount / TotalMatches : double.NaN;

    public List<HistoryEntry> GetRecent(int count = 20)
        => _current.Entries.OrderByDescending(e => e.Timestamp).Take(count).ToList();

    public void Clear() { _current.Entries.Clear(); Save(); }

    private static HistoryData? LoadJson(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<HistoryData>(File.ReadAllText(path), JsonOpts); }
        catch { return null; }
    }
}

public class HistoryData
{
    [JsonPropertyName("entries")]
    public List<HistoryEntry> Entries { get; set; } = [];
}

/// <summary>
/// Twitch Predictionの作成状況
/// </summary>
public enum PredictionStatus
{
    None,       // Twitch未接続など
    Created,    // 作成成功
    Failed,     // 作成失敗
    Canceled    // キャンセル
}

public class HistoryEntry
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("event_type")]
    public HistoryEventType EventType { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("prediction_id")]
    public string PredictionId { get; set; } = string.Empty;

    // JSONに保存するための属性を追加
    [JsonPropertyName("prediction_status")]
    public PredictionStatus PredictionStatus { get; set; } = PredictionStatus.None;

    [JsonIgnore]
    public string EventDisplayName => EventType switch
    {
        HistoryEventType.VoteStarted => "📊 投票開始",
        HistoryEventType.Win => "🏆 勝利",
        HistoryEventType.Lose => "💀 敗北",
        _ => EventType.ToString()
    };

    [JsonIgnore]
    public string TimestampLocal => Timestamp.ToLocalTime().ToString("HH:mm:ss");

    [JsonIgnore]
    public string ConfidenceText => $"{Confidence:P0}";

    [JsonIgnore]
    public string PredictionIdShort => string.IsNullOrEmpty(PredictionId)
        ? ""
        : PredictionId.Length > 12 ? PredictionId[..12] + "…" : PredictionId;

    /// <summary>画面表示用のステータス文字列</summary>
    [JsonIgnore]
    public string PredictionStatusText => PredictionStatus switch
    {
        PredictionStatus.None => "Twitch未接続",
        PredictionStatus.Created => "作成済",
        PredictionStatus.Failed => "失敗",
        PredictionStatus.Canceled => "キャンセル",
        _ => ""
    };

    /// <summary>画面表示用のステータス色</summary>
    [JsonIgnore]
    public string PredictionStatusColor => PredictionStatus switch
    {
        PredictionStatus.Created => "#4CAF50", // 緑
        PredictionStatus.Failed => "#F44336", // 赤
        PredictionStatus.Canceled => "#FF9800", // オレンジ
        _ => "#888888" // グレー
    };
}

public enum HistoryEventType
{
    VoteStarted,
    Win,
    Lose
}
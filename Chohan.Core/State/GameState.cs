namespace Chohan.Core.State;

/// <summary>
/// アプリケーションの状態遷移を表す列挙型。
/// 待機 → 投票中 → 結果確定 → 待機 のサイクル。
/// </summary>
public enum GameState
{
    /// <summary>停止中（監視していない）</summary>
    Stopped,

    /// <summary>待機中：「開始画面」のテンプレートを監視している</summary>
    Idle,

    /// <summary>投票中：開始を検知し、Twitch投票を作成済み。勝利/敗北を監視中</summary>
    Voting,

    /// <summary>結果確定：勝利または敗北を検知し、投票を確定済み</summary>
    Resolved
}

/// <summary>
/// 検知結果の種別
/// </summary>
public enum MatchResult
{
    None,
    Start,
    Win,
    Lose
}

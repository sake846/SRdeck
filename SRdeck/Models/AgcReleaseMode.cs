namespace SRdeck.Models;

/// <summary>
/// ホスト側 RF AGC の利得回復（リリース）速度です。
/// アタックはすべてのモードで即時に動作します。
/// </summary>
public enum AgcReleaseMode
{
    Fast,
    Medium,
    Slow,
    AttackOnly
}

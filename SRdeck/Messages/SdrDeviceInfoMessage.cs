namespace SRdeck.Messages;

/// <summary>
/// SDRデバイスが認識された際に、デバイス情報をUI層へ通知するためのメッセージです。
/// </summary>
/// <param name="ModelName">モデル名 (例: RSP1A)</param>
/// <param name="SerialNumber">シリアル番号</param>
/// <param name="InitialRfGain">初期RFゲイン値</param>
public record SdrDeviceInfoMessage(string ModelName, string SerialNumber, int InitialRfGain);

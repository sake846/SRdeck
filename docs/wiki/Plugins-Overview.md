# 公式プラグイン一覧

公開版の`with-plugins`パッケージには次の8プラグインが含まれます。リリースごとの正確な同梱内容は`PACKAGE-MANIFEST.json`で確認してください。

| ID | 表示名 | 用途 |
|---|---|---|
| `acars` | ACARS | VHF ACARS AM-MSK／ARINC 618 |
| `adsb` | ADS-B | 1090 MHz Mode S Extended Squitter |
| `ais` | AIS | 2チャネルの海上AIS GMSK |
| `analog` | アナログ復調 | AM、FM、SSB音声受信 |
| `ft8` | FT8 | FT8、FT4、JT65Aの弱信号解析 |
| `hfdl` | HFDL | HF Data Link／ARINC 635 |
| `vdl` | VDL Mode 2 | D8PSK／AVLC航空データリンク |
| `wisun` | Wi-SUN | 920 MHz帯IEEE 802.15.4g SUN FSK |

## 任意／開発中のプラグイン

ソースツリーや`SRdeckPlugins`ソーススナップショットに存在しても、公開ZIPへ含まれないプラグインがあります。`Meshtastic`はソースのみ公開し、DLLを公式公開ZIPへ含めません。個別にビルドまたは配布する場合は、対象地域と用途に応じた権利・ライセンスを確認してください。

## 関連ページ

- [Meshtastic（ソース公開・バイナリ配布対象外）](Plugins-Meshtastic)
- [Wi-SUN](Plugins-WiSun)
- [アナログ復調](Plugins-Analog)
- [プラグイン管理](Plugin-Management)

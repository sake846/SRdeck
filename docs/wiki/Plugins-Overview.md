# 公式プラグイン一覧

公開版の`with-plugins`パッケージには、次の8プラグインが含まれます。リリースごとの正確な同梱物はZIP内の`PACKAGE-MANIFEST.json`を確認してください。

## 一覧

| ID | 表示名 | 主な信号 | 主な結果 | 個別ガイド |
|---|---|---|---|---|
| `acars` | ACARS | VHF ACARS、AM-MSK、2400 bit/s | 機体、ラベル、本文、位置、履歴 | [ACARS](Plugins-Acars) |
| `adsb` | ADS-B | 1090 MHz Mode S DF17／DF18 | ICAO、Callsign、高度、速度、CPR位置 | [ADS-B](Plugins-AdsB) |
| `ais` | AIS | 161.975／162.025 MHz、GMSK | MMSI、船名、位置、速度、航跡 | [AIS](Plugins-Ais) |
| `analog` | アナログ復調 | AM、FM、USB／LSB | 48 kHz PCM音声、信号診断 | [アナログ復調](Plugins-Analog) |
| `ft8` | FT8 | FT8、FT4、JT65A | Callsign、Locator、SNR、メッセージ | [FT8／FT4／JT65A](Plugins-Ft8) |
| `hfdl` | HFDL | HF Data Link、BPSK／QPSK／8PSK | SPDU／LPDU、Flight ID、位置、Payload | [HFDL](Plugins-Hfdl) |
| `vdl` | VDL Mode 2 | 136.725～136.975 MHz、D8PSK | AVLC、ACARS上位層、Callsign、位置 | [VDL Mode 2](Plugins-Vdl) |
| `wisun` | Wi-SUN | 920 MHz帯IEEE 802.15.4g SUN FSK | MACフレーム、PAN、ノード、通信履歴 | [Wi-SUN](Plugins-WiSun) |

## 目的から選ぶ

### まず音を確認したい

[アナログ復調](Plugins-Analog)を使います。方式、帯域、周波数を直接選べるため、アンテナ、ゲイン、PPM、音声経路の初期確認にも向いています。

### 航空通信を観測したい

- ACARS: VHFの短いテキスト／データメッセージ
- ADS-B: 1090 MHzで航空機の識別、位置、高度、速度
- HFDL: HF帯の長距離航空データリンク
- VDL Mode 2: VHF帯のD8PSK航空データリンク

同じ航空用途でも周波数、アンテナ、伝搬、変調、必要RATEが異なります。対象地域とアンテナ帯域に合わせて選んでください。

### 船舶を観測したい

[AIS](Plugins-Ais)はAIS 1とAIS 2を同時に監視し、船舶／局を一覧と地図へ集約します。VHF海上帯に適したアンテナと見通しが重要です。

### 弱信号通信を観測したい

[FT8／FT4／JT65A](Plugins-Ft8)を使います。UTCスロットに同期するため、PC時計の正確さと、選択モードに対応するバンド設定が重要です。

### 920 MHz帯のデータを解析したい

[Wi-SUN](Plugins-WiSun)はFAN、HAN、Custom PHYを選択できます。地域の周波数割当、対象機器、チャネル、ビットレートを確認してください。

## 共通ワークスペース

多くのデジタルプラグインは共通した情報構造を持ちます。

- `受信設定`: 周波数、地域、プロファイル、チャネル、音声
- `プラグイン設定`: 履歴件数、表示対象数、保持時間、IQキャプチャ
- `概要`: 受信件数、対象数、成功率、最近の活動
- `一覧`: 対象別にまとめた最新状態
- `時系列`: 個々のフレーム、解析結果、生データ
- `地図`: 位置が得られた対象と航跡
- `診断`: 入力、チャネル、同期、復調、CRC／FEC

Wi-SUNは`概要`、`一覧`、`時系列`、`診断`、Analogは`概要`と`診断`が中心です。タブ名が同じでも項目の意味は方式ごとに異なるため、個別ガイドを参照してください。

## 保存とエクスポート

ACARS、ADS-B、AIS、FT8、HFDL、VDL Mode 2は復号履歴をJSONLへ保持し、CSV／JSONエクスポートを提供します。Wi-SUNもCSV／JSONエクスポートに対応します。保存先、最大件数、フィールドは方式ごとに異なります。

IQプリトリガーキャプチャはACARS、ADS-B、Analog、FT8、HFDL、VDL Mode 2で利用できます。詳細は[音声出力とIQキャプチャ](Audio-and-Recording)を参照してください。

## ソース公開・バイナリ配布対象外

[Meshtastic](Plugins-Meshtastic)は`SRdeckPlugins`ソーススナップショットへ含まれますが、公式SRdeck実行ZIPへDLLを含めません。ソースツリーにある他の開発中／任意配布モジュールも、`PACKAGE-MANIFEST.json`にない限り公式パッケージの一部ではありません。

## 受信時の共通注意

- 無線設備、受信、復号、保存、公開に関する地域の法令を確認する
- 個人情報、位置情報、通信内容をむやみに公開しない
- RATEは必要帯域が収まる最小値から始める
- ゲインを上げすぎず、オーバーレイ全体が帯域内にあることを確認する
- 結果が出ないときは`診断`を入力から順に読む

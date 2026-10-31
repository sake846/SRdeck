# 公式プラグイン一覧

公開版の`with-plugins`パッケージには、次の15プラグインが含まれます。リリースごとの正確な同梱物はZIP内の`PACKAGE-MANIFEST.json`を確認してください。

## 一覧

選択欄の表示名は `用途（規格・方式）` にそろえます。長い規格名は選択欄に収まるよう略記します。「受信解析」「解析・試験」などの開発状態は名称に含めず、対応範囲は個別ガイドで説明します。

| ID | 表示名 | 主な信号 | 主な結果 | 個別ガイド |
|---|---|---|---|---|
| `acars` | 航空データ通信（ACARS） | VHF ACARS、AM-MSK、2400 bit/s | 機体、ラベル、本文、位置、履歴 | [ACARS](Plugins-Acars) |
| `adsb` | 航空機監視（ADS-B） | 1090 MHz Mode S DF17／DF18 | ICAO、Callsign、高度、速度、CPR位置 | [ADS-B](Plugins-AdsB) |
| `ais` | 船舶監視（AIS） | 161.975／162.025 MHz、GMSK | MMSI、船名、位置、速度、航跡 | [AIS](Plugins-Ais) |
| `aprs` | アマチュア無線（APRS） | NFM／Bell 202 AFSK 1200／AX.25 | 局名、位置、メッセージ、地図、履歴 | [APRS](Plugins-Aprs) |
| `analog` | アナログ音声（AM/FM/SSB） | AM、FM、USB／LSB | 48 kHz PCM音声、信号診断 | [アナログ復調](Plugins-Analog) |
| `arib-t98` | 簡易無線（ARIB STD-T98） | デジタル簡易無線、4FSK／π/4 QPSK | 制御情報、FEC、秘話解析、IQ録音、4FSK音声（AMBEドングル必須） | [T98](https://github.com/sake846/SRdeckPlugins/blob/main/SRdeckPlugin.AribT98/README.ja.md) |
| `arib-t102` | 業務無線（T102/B54/NXDN） | 狭帯域業務無線・放送連絡・NXDN 6.25 kHz通常通信、4FSK | 制御情報、呼出・GPS・状態・テキスト、NXDNのRAN・発着信ID、IQ録音、音声（AMBE接続必須） | [T102/B54・NXDN](https://github.com/sake846/SRdeckPlugins/blob/main/SRdeckPlugin.AribT102/README.ja.md) |
| `cw` | アマチュア無線（モールス通信） | 10／20／50 kHz帯域のCW | モールス復号、Callsign、SNR、注釈 | [CW Skimmer](Plugins-Cw) |
| `signal-workbench` | 未知信号の解析ワークベンチ | 選択した6～200 kHz帯域 | 未同期IQ、周波数偏移分布、FSK解析、シンボルレート候補 | [信号解析](Plugins-Signal-Workbench) |
| `activity-heatmap` | 周波数ごとの活動ヒートマップ | 現在の受信帯域全体 | dBmで活動判定、1・6・24時間の推移 | [活動ヒートマップ](Plugins-Activity-Heatmap) |
| `maritime-vhf` | 国際海上無線（VHF/DSC） | VHF音声NFM、CH 70 DSC | 音声、DSC履歴、作業CH自動追尾 | [国際VHF](Plugins-Maritime-VHF) |
| `hffax` | 気象ファクシミリ（HF-FAX） | HF気象無線模写、F3C | グレースケール画像、PNG、同期診断 | [HF-FAX](Plugins-HfFax) |
| `sstv` | アマチュア無線（SSTV） | USB／FM、Martin・Scottie・Robot 36・PD120 | カラー画像、VIS自動判別、PNG | [SSTV](Plugins-Sstv) |
| `ft8` | アマチュア無線（FT8/FT4/JT65） | FT8、FT4、JT65A | Callsign、Locator、SNR、メッセージ | [FT8／FT4／JT65A](Plugins-Ft8) |
| `hfdl` | 航空データ通信（HFDL） | HF Data Link、BPSK／QPSK／8PSK | SPDU／LPDU、Flight ID、位置、Payload | [HFDL](Plugins-Hfdl) |
| `vdl` | 航空データ通信（VDL Mode 2） | 136.725～136.975 MHz、D8PSK | AVLC、ACARS上位層、Callsign、位置 | [VDL Mode 2](Plugins-Vdl) |
| `wisun` | IoT通信（Wi-SUN） | 920 MHz帯IEEE 802.15.4g SUN FSK | MACフレーム、PAN、ノード、通信履歴 | [Wi-SUN](Plugins-WiSun) |

## 目的から選ぶ

### まず音を確認したい

[アナログ復調](Plugins-Analog)を使います。方式、帯域、周波数を直接選べるため、アンテナ、ゲイン、PPM、音声経路の初期確認にも向いています。

### デジタル簡易無線を受信したい

ARIB STD-T98は4FSKとπ/4 QPSKを受信します。4FSKの音声にはDVStickなどのAMBEドングルを接続し、
プラグインのCOMポートとボーレートを設定してください。ドングルがない場合は音声を出力しません。
PSKの音声は未対応です。[T98のガイド](https://github.com/sake846/SRdeckPlugins/blob/main/SRdeckPlugin.AribT98/README.ja.md)を参照してください。

### 狭帯域業務無線・放送連絡を受信したい

ARIB STD-T102/B54はT102第1編・第2編とB54を選択し、対象局の周波数とPN9初期値を設定して使用します。
タクシー無線、業務無線、アマチュア無線（NXDN）、放送連絡（B54）の用途別に受信設定を保存・復元できます。
NXDNは方式「NXDN 6.25 kHz（通常通信）」でRAN・発着信ID・通話/終話とAMBE+2音声を復号します。
12.5 kHz・トランキング追従・暗号音声は未対応です。
音声にはDVStickなどのAMBEドングルが必須です。未接続時も制御情報・メッセージの受信とIQ録音は利用できます。
詳細は[T102/B54のガイド](https://github.com/sake846/SRdeckPlugins/blob/main/SRdeckPlugin.AribT102/README.ja.md)を参照してください。

### 航空通信を観測したい

- ACARS: VHFの短いテキスト／データメッセージ
- ADS-B: 1090 MHzで航空機の識別、位置、高度、速度
- HFDL: HF帯の長距離航空データリンク
- VDL Mode 2: VHF帯のD8PSK航空データリンク

同じ航空用途でも周波数、アンテナ、伝搬、変調、必要RATEが異なります。対象地域とアンテナ帯域に合わせて選んでください。

### 船舶を観測したい

[国際VHF](Plugins-Maritime-VHF)は音声を受信しながらDSC CH 70を監視し、有効な呼出の作業CHへ自動追尾します。[AIS](Plugins-Ais)はAIS 1とAIS 2を同時に監視し、船舶／局を一覧と地図へ集約します。用途に応じて別々のプラグインを選んでください。VHF海上帯に適したアンテナと見通しが重要です。

### 弱信号通信やモールス通信を観測したい

アマチュア無線のデジタル弱信号方式には[FT8／FT4／JT65A](Plugins-Ft8)、モールス通信には[CW Skimmer](Plugins-Cw)を使います。FT8系はUTCスロットに同期するため、PC時計の正確さと、選択モードに対応するバンド設定が重要です。

### HF気象ファクシミリを受信したい

[HF-FAX](Plugins-HfFax)はAPT開始／停止を検出し、IOC 576／288、60／90／120 LPMの画像を生成してPNGへ保存します。放送局の周波数とスケジュール、短波伝搬を確認してください。

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

Wi-SUNは`概要`、`一覧`、`時系列`、`診断`、アナログ復調は`概要`と`診断`が中心です。CW Skimmer、HF-FAX、国際VHFは方式固有の結果画面を持ちます。タブ名が同じでも項目の意味は方式ごとに異なるため、個別ガイドを参照してください。

## 保存とエクスポート

ACARS、ADS-B、AIS、CW Skimmer、FT8、HFDL、VDL Mode 2は復号履歴をJSONLへ保持し、CSV／JSONエクスポートを提供します。Wi-SUNもCSV／JSONエクスポートに対応し、HF-FAXは画像履歴とPNG保存に対応します。保存先、最大件数、フィールドは方式ごとに異なります。

IQプリトリガーキャプチャはACARS、ADS-B、アナログ復調、CW Skimmer、FT8、HFDL、VDL Mode 2で利用できます。詳細は[音声出力とIQキャプチャ](Audio-and-Recording)を参照してください。

## ソース公開・バイナリ配布対象外

[Meshtastic](Plugins-Meshtastic)と[ワンセグ](Plugins-OneSeg)のソースは、`SRdeckPlugins`のソーススナップショットに含まれます。公式SRdeck実行ZIPには、これらのDLLを含めていません。

ほかの開発中・任意配布モジュールも、ZIP内の`PACKAGE-MANIFEST.json`に記載がなければ公式パッケージには含まれません。

## 受信時の共通注意

- 無線設備、受信、復号、保存、公開に関する地域の法令を確認する
- 個人情報、位置情報、通信内容をむやみに公開しない
- RATEは必要帯域が収まる最小値から始める
- ゲインを上げすぎず、オーバーレイ全体が帯域内にあることを確認する
- 結果が出ないときは`診断`を入力から順に読む

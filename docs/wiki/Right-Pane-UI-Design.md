# 右ペインUI設計

WPF UIを持つプラグインは`SRdeckPlugin.Wpf`を参照し、`IPluginViewProvider`を実装します。右ペインは利用者が受信中に繰り返し見る運用画面です。装飾より、状態、設定、結果、診断の順序と更新の安定性を優先します。

## ビュー契約

```csharp
public interface IPluginViewProvider
{
    FrameworkElement CreateMainView();
    FrameworkElement? CreateSettingsView();
}
```

Descriptorには返すビューに対応する`MainView`／`SettingsView`能力を設定します。ビューを返さない能力を宣言したり、能力を宣言せずビューを実装したりしないでください。

## 右ペインの制約

- ホストの幅は700 px
- ルート要素に固定`Width`、`MinWidth`、`MaxWidth`を設定しない
- 高さはホストへ追従し、必要な領域だけスクロール
- 横方向へ溢れるDataGridは列の優先度を設計
- 頻繁に更新される値で全体の大きさを変えない
- 低頻度設定をメイン結果領域へ詰め込まない
- ホストのテーマ、フォント、余白、状態色を使用

700 pxで収まることだけでなく、Windows表示スケール125／150%、長い日本語／英語ラベル、空状態、最大値を確認します。

## 推奨情報構造

### 設定領域

- ヘッダー: プラグイン名と短い状態
- `受信設定`: 周波数、プロファイル、チャネル、音声など高頻度設定
- `プラグイン設定`: 保持件数、リセット、IQキャプチャなど低頻度設定

外枠は`PluginSettingsPanelStyle`、ヘッダーは`PluginSettingsHeaderStyle`を使います。単一ページは`PluginSettingsBodyStyle`、複数ページは`PluginSettingsTabControlStyle`を使います。

### メイン領域

多くのデジタル方式では次の順序が有効です。

1. 状態ヘッダー
2. `概要`
3. `一覧`
4. `時系列`
5. `地図`
6. `診断`

すべての方式へ不要なタブを強制しません。Analogのように結果一覧を持たない方式は概要と診断だけで構いません。

## 概要

概要は数秒で「動いているか」を判断できる量に絞ります。

- 対象数
- 有効フレーム累計
- 合格率
- 最終受信
- 最近の対象
- 1行の推奨／状態

メトリックカードを増やしすぎず、方式固有の重要指標を4～6個程度に整理します。累計、直近、毎秒をラベルで区別してください。

## 一覧と詳細

DataGridの各行にすべてのフィールドを置かず、識別、最新時刻、主状態を表示し、選択詳細ペインへ残りを出します。

- 検索と件数を一覧上部へ置く
- 時刻列の書式を揃える
- 識別子と表示名を区別
- ソート時に更新が選択を飛ばさない
- 空状態を中央に明示
- 詳細ペインは未選択時の案内を持つ
- Raw HEXは折り返し可能なExpanderへ置く

横スクロールを前提にせず、重要度の低い列を詳細へ移します。

## 時系列

時系列は個々のイベントを監査できる画面です。

- 受信時刻
- 対象／チャネル
- 種別
- 検証状態
- 解析済み概要
- 生フレームまたはPayload
- 選択イベントの詳細

一覧の「最新状態」と時系列の「そのフレームが含む値」を混同しないラベルにします。

## 診断

診断は結果0件のときにも価値がある必要があります。処理順に分けます。

1. リアルタイム処理
2. 入力・選局
3. 信号・チャネル
4. 検出・同期・復調
5. 検証・復号
6. 詳細・デバッグ

各段階に件数、レート、最終状態、失敗理由を表示します。「受信できません」だけで終わらず、どこまで到達したかを示します。内部実装名だけでなく、利用者が次に確認できる説明を添えます。

## 共通スタイル

代表的なリソース:

- `PluginButtonStyle`、`PluginToggleButtonStyle`
- `PluginComboBoxStyle`、`PluginTextBoxStyle`
- `PluginCheckBoxStyle`、`PluginRadioButtonStyle`、`PluginSliderStyle`
- `PluginTabControlStyle`
- `PluginDataGridStyle`、`PluginTimelineDataGridStyle`
- `PluginMetricCardStyle`
- `PluginStatusHeaderStyle`
- `PluginDiagnosticsSectionStyle`
- `PluginResetView`
- `SelectionDetailPane`

独自コントロールを作る前に、WPFパッケージのThemesとControlsを検索します。

## 色と意味

色をハードコードせず、`DynamicResource`を使います。

表示階層:

- `PluginDisplayAccentPrimaryBrush`
- `PluginDisplayAccentSecondaryBrush`
- `PluginDisplayAccentTertiaryBrush`

動作状態:

- `PluginStatusRunning...Brush`
- `Success`、`Info`、`Warning`、`Error`、`Critical`

通常の面と文字:

- `TextPrimaryBrush`、`TextSecondaryBrush`、`TextDimBrush`
- `PanelSurfaceBrush`
- `ControlBorderBrush`

アクセント色を警告の代用にせず、状態色をカテゴリ分けへ流用しません。色だけで状態を伝えず、ラベル、値、アイコンを併用します。

```xml
<Button Content="適用"
        Style="{DynamicResource PluginButtonStyle}" />

<TextBlock Text="受信状態"
           Foreground="{DynamicResource PluginDisplayAccentPrimaryBrush}" />
```

## 入力コントロール

- 二値設定: CheckBoxまたはToggleButton
- 排他的な少数選択: RadioButton
- 選択肢が多い: ComboBox
- 連続値: Sliderと数値表示
- 正確な数値: TextBox、範囲検証、単位
- 即時コマンド: Button
- 長時間処理: 実行中状態、二重実行防止、完了／失敗表示

単位をラベルか値に含めます。範囲外入力は設定型の`Normalize`でも防御し、UI検証だけに依存しません。

## アクセシビリティ

- Tab順序が視覚順序と一致
- フォーカス表示を消さない
- ラベルと入力の関連を明確にする
- ToolTipだけへ必須情報を置かない
- AutomationProperties.Nameを必要に応じて設定
- 色以外でも成功／警告／エラーを識別
- 高DPIとキーボードだけで操作確認
- DataGrid選択、スクロール、Expanderへ到達可能

## 更新とスレッド

WPFオブジェクトはDispatcherスレッドで操作します。IQコールバックから直接ViewModelコレクションを更新しません。

推奨パターン:

1. DSPスレッドで不変スナップショットを作る
2. UI更新を一定間隔へ間引く
3. Dispatcherへ1回で渡す
4. ObservableCollectionを差分更新する
5. Stop／Dispose時にTimerと購読を解除する

毎フレーム`PropertyChanged`を大量発火したり、タブ全体を作り直したりすると、WPFが復調処理を圧迫します。

## 空、エラー、停止状態

最低限次を設計します。

- 未初期化
- 待機中
- 受信中
- 信号なし
- 結果なし
- 設定拒否
- 同調失敗
- エクスポート中／完了／失敗
- 停止中
- 破棄済み

空のDataGridだけを見せず、「何がまだないか」を表示します。エラーは操作可能な次の手順を示し、詳細例外はログ／診断へ分離します。

## レビューチェックリスト

- 700 pxと高DPIで切れない
- 設定と結果の境界が分かる
- 概要だけで受信状態を判断できる
- 一覧と時系列の意味が重複していない
- 診断が処理順になっている
- 長い識別子、最大値、空値で崩れない
- テーマ色をハードコードしていない
- キーボードで全操作へ到達できる
- UIを閉じてもDSPが動く
- 高頻度更新で`WPF`や`PRC`を悪化させない
- Stop／MODE切替後にTimerやイベントが残らない

詳細な規範は[右ペインUIデザイン指針](https://github.com/sake846/SRdeck/blob/main/docs/plugin-right-pane-design-guidelines.md)を参照してください。

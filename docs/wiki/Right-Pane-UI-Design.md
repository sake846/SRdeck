# 右ペインUI設計

WPF UIを提供するプラグインは`SRdeckPlugin.Wpf`を参照し、`IPluginViewProvider`を実装します。

```csharp
public interface IPluginViewProvider
{
    FrameworkElement CreateMainView();
    FrameworkElement? CreateSettingsView();
}
```

Descriptorには、返すビューに対応する`MainView`／`SettingsView`能力を設定します。

## レイアウト契約

- ホストの右ペインは幅700 pxです。
- プラグインのルート要素に`Width`、`MinWidth`、`MaxWidth`を設定しません。
- 横方向へ伸びる一覧は列幅を設計し、必要なら詳細ペインや縦スクロールへ分割します。
- 状態、設定、主結果、一覧／詳細、診断の情報階層を保ちます。
- 設定ビューは`CreateSettingsView()`へ分離し、低頻度設定を主画面へ詰め込みません。

設定外枠には`PluginSettingsPanelStyle`、ヘッダーには`PluginSettingsHeaderStyle`を使用します。単一ページの本文は`PluginSettingsBodyStyle`、複数ページは`PluginSettingsTabControlStyle`を使います。

## 共通スタイル

代表的なテーマリソース:

- `PluginButtonStyle`、`PluginToggleButtonStyle`
- `PluginComboBoxStyle`、`PluginTextBoxStyle`
- `PluginCheckBoxStyle`、`PluginRadioButtonStyle`、`PluginSliderStyle`
- `PluginTabControlStyle`
- `PluginDataGridStyle`、`PluginTimelineDataGridStyle`
- `PluginMetricCardStyle`
- `PluginStatusHeaderStyle`、`PluginDiagnosticsSectionStyle`

色はハードコードせず、`DynamicResource`を使います。

- 表示上の階層: `PluginDisplayAccentPrimaryBrush`、`Secondary`、`Tertiary`
- 動作状態: `PluginStatusRunning...Brush`、`Success`、`Info`、`Warning`、`Error`、`Critical`
- 通常本文／面／枠: ホストの`TextPrimaryBrush`、`TextSecondaryBrush`、`PanelSurfaceBrush`、`ControlBorderBrush`など

アクセント色を警告やエラーの意味で流用せず、状態色も単なるカテゴリ分けには使いません。

```xml
<Button Content="適用"
        Style="{DynamicResource PluginButtonStyle}" />

<TextBlock Text="受信状態"
           Foreground="{DynamicResource PluginDisplayAccentPrimaryBrush}" />
```

## 操作性

- 色だけで状態を伝えず、ラベル、値、アイコンを併用します。
- キーボードで到達できる順序とフォーカス表示を保ちます。
- 検索欄やボタンには意味の分かるラベル／アクセシビリティ名を付けます。
- 頻繁に更新する値でレイアウト全体を再生成しません。
- ビュー生成と更新はWPF Dispatcherの規則に従います。

詳細とチェックリストは[右ペインUIデザイン指針](https://github.com/sake846/SRdeck/blob/main/docs/plugin-right-pane-design-guidelines.md)を参照してください。

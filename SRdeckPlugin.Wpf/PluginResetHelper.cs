using System;
using System.Threading.Tasks;
using System.Windows;

namespace SRdeckPlugin.Wpf;

public static class PluginResetHelper
{
    public static async Task<bool> ConfirmAndResetSettingsAsync(
        string pluginDisplayName,
        Func<ValueTask> resetSettingsAction,
        Action applyDefaultsAction)
    {
        var result = MessageBox.Show(
            $"{pluginDisplayName} の設定を初期状態に戻しますか？\n（この操作は取り消せません）",
            "設定の初期化",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return false;

        try
        {
            await resetSettingsAction();
            applyDefaultsAction();
            MessageBox.Show(
                $"{pluginDisplayName} の設定を初期化しました。",
                "初期化完了",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"初期化中にエラーが発生しました:\n{ex.Message}",
                "エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    public static bool ConfirmAndClearData(
        string pluginDisplayName,
        Action clearAction)
    {
        var result = MessageBox.Show(
            $"{pluginDisplayName} の受信データ・履歴をクリアしますか？",
            "データのクリア",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return false;

        try
        {
            clearAction();
            MessageBox.Show(
                $"{pluginDisplayName} の受信データをクリアしました。",
                "クリア完了",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"クリア中にエラーが発生しました:\n{ex.Message}",
                "エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    public static async Task<bool> ConfirmAndResetAllAsync(
        string pluginDisplayName,
        Func<ValueTask> resetSettingsAction,
        Action applyDefaultsAction,
        Action? clearDataAction = null)
    {
        var result = MessageBox.Show(
            $"{pluginDisplayName} のすべての設定および保存データを削除して初期状態に戻しますか？\n（この操作は取り消せません）",
            "完全初期化",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return false;

        try
        {
            await resetSettingsAction();
            applyDefaultsAction();
            clearDataAction?.Invoke();
            MessageBox.Show(
                $"{pluginDisplayName} を初期化しました。",
                "初期化完了",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"初期化中にエラーが発生しました:\n{ex.Message}",
                "エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }
}

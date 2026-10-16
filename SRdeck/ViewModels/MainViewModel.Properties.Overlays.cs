using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // --- SDR Error Overlay ---
    [ObservableProperty] private Visibility _sdrErrorOverlayVisibility = Visibility.Collapsed;
    [ObservableProperty] private string _sdrErrorMessageText = string.Empty;

    [RelayCommand]
    private void CloseSdrError()
    {
        SdrErrorOverlayVisibility = Visibility.Collapsed;
        SdrErrorMessageText = string.Empty;
    }

    // --- Common Overlay ---
    [ObservableProperty] private Visibility _commonOverlayVisibility = Visibility.Collapsed;
    [ObservableProperty] private string _commonOverlayMessageText = string.Empty;
    [ObservableProperty] private string _commonOverlayTitle = string.Empty;

    [RelayCommand]
    private void CloseCommonOverlay()
    {
        CommonOverlayVisibility = Visibility.Collapsed;
    }

    // --- Settings Reset Confirm ---
    [ObservableProperty] private Visibility _settingsResetConfirmVisibility = Visibility.Collapsed;
    [ObservableProperty] private string _settingsResetConfirmTitle = "リセットの確認";
    [ObservableProperty] private string _settingsResetConfirmMessage = string.Empty;
    [ObservableProperty] private System.Windows.Input.ICommand? _settingsResetConfirmOkCommand;

    [RelayCommand]
    private void CloseSettingsResetConfirm()
    {
        SettingsResetConfirmVisibility = Visibility.Collapsed;
    }

    // --- System Overlays ---
    [ObservableProperty] private Visibility _exportingOverlayVisibility = Visibility.Hidden;
    [ObservableProperty] private Visibility _shuttingDownOverlayVisibility = Visibility.Hidden;
}

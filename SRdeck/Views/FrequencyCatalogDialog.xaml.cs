using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SRdeck.Configuration;
using SRdeck.Models;

namespace SRdeck.Views;

public partial class FrequencyCatalogDialog : Window
{
    internal static readonly string[] Modes =
        ["不明", "CW", "CW-R", "DATA12k", "USB", "LSB", "AM-N", "AM-W", "FM-N", "FM-W"];

    private readonly FrequencyCatalogStore _store;
    private readonly long _currentFrequencyHz;
    private readonly ObservableCollection<StationItem> _stations;
    private readonly ObservableCollection<BandPlanItem> _bandPlans;
    private StationItem? _pendingDeleteStation;
    private BandPlanItem? _pendingDeleteBand;

    internal FrequencyCatalogDialog(
        FrequencyCatalogStore store,
        long currentFrequencyHz,
        bool showBandPlans)
    {
        InitializeComponent();
        _store = store;
        _currentFrequencyHz = currentFrequencyHz;
        _stations = new(store.LoadStations().OrderBy(item => item.FrequencyHz));
        _bandPlans = new(store.LoadBandPlans().OrderBy(item => item.StartHz));
        StationListBox.ItemsSource = _stations;
        BandPlanListBox.ItemsSource = _bandPlans;

        StationPanel.Visibility = showBandPlans ? Visibility.Collapsed : Visibility.Visible;
        BandPlanPanel.Visibility = showBandPlans ? Visibility.Visible : Visibility.Collapsed;
        Width = showBandPlans ? 560 : 380;
        Height = showBandPlans ? 400 : 340;
        SetResourceReference(TitleProperty, showBandPlans ? "Title_BandPlan" : "Title_Stations");
    }

    public long? ResultFrequencyHz { get; private set; }

    internal static string NormalizeMode(string? mode) => mode switch
    {
        "USB_Wide" => "USB",
        "LSB_Wide" => "LSB",
        "AM" => "AM-N",
        "AM_Wide" => "AM-W",
        "FM_Narrow" => "FM-N",
        "FM_Wide" => "FM-W",
        string value when Modes.Contains(value) => value,
        _ => "不明"
    };

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowTheme.ApplyDarkTitleBar(this);
    }

    private void StationListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StationListBox.SelectedItem is not StationItem station) return;
        ResultFrequencyHz = station.FrequencyHz;
        DialogResult = true;
    }

    private void BandPlanListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BandPlanListBox.SelectedItem is not BandPlanItem band) return;
        ResultFrequencyHz = band.StartHz + (band.EndHz - band.StartHz) / 2;
        DialogResult = true;
    }

    private void AddStation_OnClick(object sender, RoutedEventArgs e) => EditStation(null);

    private void AddCurrentStation_OnClick(object sender, RoutedEventArgs e)
    {
        EditStation(new StationItem
        {
            FrequencyHz = _currentFrequencyHz,
            Name = DateTime.Now.ToString("yyyyMMdd"),
            Comment = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")
        });
    }

    private void EditStation_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StationItem station) EditStation(station);
        e.Handled = true;
    }

    private void EditStation(StationItem? station)
    {
        int index = station is null ? -1 : _stations.IndexOf(station);
        var editor = new StationEditorDialog(station) { Owner = this };
        if (editor.ShowDialog() != true) return;

        if (index < 0)
            _stations.Add(editor.Station);
        else
            _stations[index] = editor.Station;
        SortAndSaveStations();
    }

    private void DeleteStation_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StationItem station)
        {
            _pendingDeleteStation = station;
            _pendingDeleteBand = null;
            ConfirmMessageTextBlock.Text = $"「{station.Name}」を局リストから削除してよろしいですか？";
            ConfirmOverlay.Visibility = Visibility.Visible;
            System.Media.SystemSounds.Exclamation.Play();
        }
        e.Handled = true;
    }

    private void AddBandPlan_OnClick(object sender, RoutedEventArgs e) => EditBandPlan(null);

    private void EditBandPlan_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BandPlanItem band) EditBandPlan(band);
        e.Handled = true;
    }

    private void EditBandPlan(BandPlanItem? band)
    {
        var editor = new BandPlanEditorDialog(band) { Owner = this };
        if (editor.ShowDialog() != true) return;

        if (band is null)
            _bandPlans.Add(editor.BandPlan);
        else
            _bandPlans[_bandPlans.IndexOf(band)] = editor.BandPlan;
        SortAndSaveBandPlans();
    }

    private void DeleteBandPlan_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BandPlanItem band)
        {
            _pendingDeleteBand = band;
            _pendingDeleteStation = null;
            ConfirmMessageTextBlock.Text = $"「{band.Label}」をバンドプランから削除してよろしいですか？";
            ConfirmOverlay.Visibility = Visibility.Visible;
            System.Media.SystemSounds.Exclamation.Play();
        }
        e.Handled = true;
    }

    private void ConfirmDelete_OnClick(object sender, RoutedEventArgs e)
    {
        if (_pendingDeleteStation is not null)
        {
            _stations.Remove(_pendingDeleteStation);
            SortAndSaveStations();
        }
        else if (_pendingDeleteBand is not null)
        {
            _bandPlans.Remove(_pendingDeleteBand);
            SortAndSaveBandPlans();
        }
        CloseConfirm_OnClick(sender, e);
    }

    private void CloseConfirm_OnClick(object sender, RoutedEventArgs e)
    {
        _pendingDeleteStation = null;
        _pendingDeleteBand = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    private void DirectSelect_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new FrequencyInputDialog
        {
            Owner = this,
            ReceiverName = "Center Frequency Input",
            ThemeBrush = System.Windows.Media.Brushes.Orange
        };
        if (dialog.ShowDialog() != true) return;
        ResultFrequencyHz = dialog.ResultFrequencyHz;
        DialogResult = true;
    }

    private void SortAndSaveStations()
    {
        Reset(_stations, _stations.OrderBy(item => item.FrequencyHz).ToArray());
        _store.SaveStations(_stations);
    }

    private void SortAndSaveBandPlans()
    {
        Reset(_bandPlans, _bandPlans.OrderBy(item => item.StartHz).ToArray());
        _store.SaveBandPlans(_bandPlans);
    }

    private static void Reset<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (T item in items) collection.Add(item);
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}

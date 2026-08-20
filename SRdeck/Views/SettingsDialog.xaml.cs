using System;
using System.Windows;
using System.Windows.Input;
using SRdeck.Models;

namespace SRdeck.Views
{
    public partial class SettingsDialog : Window
    {
        public SettingsDialog(SdrDeviceKind? connectedDeviceKind = null)
        {
            InitializeComponent();
            BuildSettingsTabs(connectedDeviceKind);
        }

        private void BuildSettingsTabs(SdrDeviceKind? connectedDeviceKind)
        {
            if (connectedDeviceKind == SdrDeviceKind.SdrPlay)
            {
                AddTab("Tab_General", new SettingsTabs.GeneralSettingsTab());
            }
#if ENABLE_RTLSDR
            else if (connectedDeviceKind == SdrDeviceKind.RtlSdr)
            {
                AddTab("Tab_RtlSdr", new SettingsTabs.RtlSdrSettingsTab());
            }
#endif

            AddTab("Tab_Startup", new SettingsTabs.StartupSettingsTab());
            AddTab("Tab_System", new SettingsTabs.SystemSettingsTab());
        }

        private void AddTab(string headerResourceKey, object content)
        {
            var tab = new System.Windows.Controls.TabItem { Content = content };
            tab.SetResourceReference(System.Windows.Controls.HeaderedContentControl.HeaderProperty, headerResourceKey);
            SettingsTabs.Items.Add(tab);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            
            // ダークモードタイトルの有効化 (DWMWA_USE_IMMERSIVE_DARK_MODE = 20)
            int useImmersiveDarkMode = 1;
            DwmSetWindowAttribute(handle, 20, ref useImmersiveDarkMode, sizeof(int));
        }
        
        private void Window_ContentRendered(object sender, System.EventArgs e)
        {
            this.Opacity = 1.0;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
            base.OnKeyDown(e);
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}

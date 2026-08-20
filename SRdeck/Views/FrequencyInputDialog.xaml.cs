using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace SRdeck.Views
{
    public partial class FrequencyInputDialog : Window, INotifyPropertyChanged
    {
        private string _inputText = "";
        private string _receiverName = "Receiver 1";
        private System.Windows.Media.Brush _themeBrush = System.Windows.Media.Brushes.Cyan;

        public string InputText
        {
            get => _inputText;
            set
            {
                if (_inputText != value)
                {
                    _inputText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ReceiverName
        {
            get => _receiverName;
            set
            {
                if (_receiverName != value)
                {
                    _receiverName = value;
                    OnPropertyChanged();
                }
            }
        }

        public System.Windows.Media.Brush ThemeBrush
        {
            get => _themeBrush;
            set
            {
                _themeBrush = value;
                OnPropertyChanged();
            }
        }

        private string _errorText = "";
        private Visibility _errorVisibility = Visibility.Collapsed;

        public string ErrorText
        {
            get => _errorText;
            set { if (_errorText != value) { _errorText = value; OnPropertyChanged(); } }
        }

        public Visibility ErrorVisibility
        {
            get => _errorVisibility;
            set { if (_errorVisibility != value) { _errorVisibility = value; OnPropertyChanged(); } }
        }

        private void CloseError_Click(object sender, RoutedEventArgs e)
        {
            ErrorVisibility = Visibility.Collapsed;
        }

        public long ResultFrequencyHz { get; private set; }

        public FrequencyInputDialog()
        {
            InitializeComponent();
            DataContext = this;
            InputText = "";
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int useImmersiveDarkMode = 1;
            DwmSetWindowAttribute(handle, 20, ref useImmersiveDarkMode, sizeof(int));
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private void InputChar(string c)
        {
            if (c == "." && InputText.Contains("."))
                return;

            InputText += c;
        }

        private void ConfirmInput(string unit)
        {
            if (double.TryParse(InputText, out double value))
            {
                double multiplier = 1.0;
                if (unit == "GHz") multiplier = 1000000000.0;
                else if (unit == "MHz") multiplier = 1000000.0;
                else if (unit == "kHz") multiplier = 1000.0;
                else if (unit == "Hz") multiplier = 1.0;

                ResultFrequencyHz = (long)Math.Round(value * multiplier);
                DialogResult = true;
                Close();
            }
            else
            {
                if (string.IsNullOrEmpty(InputText)) return;
                ErrorText = "無効な周波数形式です。";
                ErrorVisibility = Visibility.Visible;
                System.Media.SystemSounds.Hand.Play();
            }
        }

        private void NumButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is string content)
            {
                InputChar(content);
            }
        }

        private void UnitButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is string unit)
            {
                ConfirmInput(unit);
            }
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // 1. 基本コントロールキー
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                CancelButton_Click(this, new RoutedEventArgs());
                return;
            }
            else if (e.Key == System.Windows.Input.Key.Back)
            {
                BsButton_Click(this, new RoutedEventArgs());
                return;
            }
            else if (e.Key == System.Windows.Input.Key.Delete)
            {
                ClrButton_Click(this, new RoutedEventArgs());
                return;
            }

            // 2. 単位ショートカット（最優先判定して数字の誤入力を防ぐ）
            bool isShift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == System.Windows.Input.ModifierKeys.Shift;

            // * キー (テンキーの *, JISの:, USのShift+8) -> GHz
            if (e.Key == System.Windows.Input.Key.Multiply || 
                e.Key == System.Windows.Input.Key.Oem1 || 
                (e.Key == System.Windows.Input.Key.D8 && isShift))
            {
                ConfirmInput("GHz");
                e.Handled = true;
                return;
            }
            // - キー (テンキーの -, 標準の-) -> MHz
            else if (e.Key == System.Windows.Input.Key.Subtract || e.Key == System.Windows.Input.Key.OemMinus || e.Key == System.Windows.Input.Key.M)
            {
                ConfirmInput("MHz");
                e.Handled = true;
                return;
            }
            // + キー (テンキーの +, 標準の+ [JISならShift+;]) -> kHz
            else if (e.Key == System.Windows.Input.Key.Add || e.Key == System.Windows.Input.Key.OemPlus || e.Key == System.Windows.Input.Key.K)
            {
                ConfirmInput("kHz");
                e.Handled = true;
                return;
            }
            // Enter / Return / H キー -> Hz
            else if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Return || e.Key == System.Windows.Input.Key.H)
            {
                ConfirmInput("Hz");
                e.Handled = true;
                return;
            }

            // 3. 数字キー (修飾キーがない場合のみ入力)
            if (!isShift)
            {
                if (e.Key >= System.Windows.Input.Key.D0 && e.Key <= System.Windows.Input.Key.D9)
                {
                    InputChar((e.Key - System.Windows.Input.Key.D0).ToString());
                }
                else if (e.Key >= System.Windows.Input.Key.NumPad0 && e.Key <= System.Windows.Input.Key.NumPad9)
                {
                    InputChar((e.Key - System.Windows.Input.Key.NumPad0).ToString());
                }
                else if (e.Key == System.Windows.Input.Key.OemPeriod || e.Key == System.Windows.Input.Key.Decimal)
                {
                    InputChar(".");
                }
            }
        }

        private void BsButton_Click(object sender, RoutedEventArgs e)
        {
            if (InputText.Length > 0)
            {
                InputText = InputText.Substring(0, InputText.Length - 1);
            }
        }

        private void ClrButton_Click(object sender, RoutedEventArgs e)
        {
            InputText = "";
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

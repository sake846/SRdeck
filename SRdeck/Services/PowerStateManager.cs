using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SRdeck.Services
{
    public static class PowerStateManager
    {
        // --- Win32 APIs for Power & Sleep Prevention ---
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

        [Flags]
        private enum EXECUTION_STATE : uint
        {
            ES_AWAYMODE_REQUIRED = 0x00000040,
            ES_CONTINUOUS = 0x80000000,
            ES_DISPLAY_REQUIRED = 0x00000002,
            ES_SYSTEM_REQUIRED = 0x00000001
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus; // 0: Offline (battery), 1: Online (AC), 255: Unknown
            public byte BatteryFlag;
            public byte BatteryLifePercent; // 0-100, 255 if unknown
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        // --- State and Events ---
        private static bool _isMonitoring = false;
        private static EXECUTION_STATE _currentExecutionState = EXECUTION_STATE.ES_CONTINUOUS;

        /// <summary>
        /// 電源ソース（AC電源 vs バッテリー）の切り替えや状態変化が発生した際のイベント
        /// </summary>
        public static event EventHandler? PowerStatusChanged;

        /// <summary>
        /// 現在、AC電源に接続されているかどうかを取得します。
        /// </summary>
        public static bool IsAcPowerConnected()
        {
            if (GetSystemPowerStatus(out var status))
            {
                return status.ACLineStatus == 1;
            }
            return true; // 取得失敗時は安全のためAC電源接続中として扱う
        }

        /// <summary>
        /// 電源監視用のシステムイベントの購読を開始します。
        /// </summary>
        public static void StartMonitoring()
        {
            if (_isMonitoring) return;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _isMonitoring = true;
        }

        /// <summary>
        /// 電源監視用システムイベントの購読を解除します。
        /// </summary>
        public static void StopMonitoring()
        {
            if (!_isMonitoring) return;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _isMonitoring = false;
            RestoreNormalSleep(); // 終了時は安全にスリープを許可する
        }

        private static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs eventArgs)
        {
            if (eventArgs.Mode == PowerModes.StatusChange)
            {
                PowerStatusChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        // --- Sleep Prevention Methods ---

        /// <summary>
        /// システムのスリープ（および必要に応じてディスプレイの消灯）を防止します。
        /// </summary>
        /// <param name="preventSystemSleep">システム自体の自動スリープを防止するか</param>
        /// <param name="preventDisplayOff">ディスプレイの自動消灯を防止するか</param>
        public static void PreventSleep(bool preventSystemSleep, bool preventDisplayOff)
        {
            EXECUTION_STATE state = EXECUTION_STATE.ES_CONTINUOUS;
            if (preventSystemSleep) state |= EXECUTION_STATE.ES_SYSTEM_REQUIRED;
            if (preventDisplayOff) state |= EXECUTION_STATE.ES_DISPLAY_REQUIRED;

            _currentExecutionState = state;
            SetThreadExecutionState(state);
        }

        /// <summary>
        /// スリープ防止要求をすべて解除し、通常のWindowsの省電力動作に戻します。
        /// </summary>
        public static void RestoreNormalSleep()
        {
            _currentExecutionState = EXECUTION_STATE.ES_CONTINUOUS;
            SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        }
    }
}

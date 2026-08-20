using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using SRdeck.Behaviors;
using SRdeck.Services;
using SRdeck.Views;
using SRdeckPlugin.Wpf;

namespace SRdeck;

/// <summary>
/// WPFアプリケーションのエントリポイントとなるクラスです。
/// 起動時にMainWindowを生成して表示します。
/// </summary>
public partial class App : System.Windows.Application
{
    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        TextBoxCommitBehavior.Enable();
        ComfortableMouseWheelBehavior.Enable();
        this.DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (s, ev) => NormalizeProcessPriority();

        bool isHeadless = Environment.GetEnvironmentVariable("HEADLESS") == "true";
        SplashWindow? splash = null;
        long splashStarted = 0;
        if (!isHeadless)
        {
            splash = new SplashWindow();
            splash.SetCalibrationStatus("Channel Auto: 起動準備中…");
            splash.Show();
            splashStarted = Stopwatch.GetTimestamp();
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        IReadOnlyList<SRdeckPlugin.Contracts.IPluginModule> pluginModules =
            ApplicationStartupCoordinator.DiscoverPluginModules();
        var services = ApplicationServiceProviderFactory.Create(pluginModules);

        Ioc.Default.ConfigureServices(services);
        this.Properties["ServiceProvider"] = services;

        IApplicationStartupProgress startupProgress = isHeadless
            ? NullApplicationStartupProgress.Instance
            : new SplashApplicationStartupProgress(splash!);
        ApplicationStartupResult startupResult = await services
            .GetRequiredService<ApplicationStartupCoordinator>()
            .StartAsync(isHeadless, startupProgress);

        if (isHeadless)
        {
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Console.WriteLine("Headless mode active. Initializing MainViewModel without GUI...");
            Console.WriteLine("Headless mode initialization complete. Running server loop...");
            return;
        }

        var mainWindow = services.GetRequiredService<MainWindow>();
        this.MainWindow = mainWindow; // 明示的に MainWindow として登録

        // Keep the splash visible for at least 1.5 seconds, and keep the final
        // CPU/GPU selection readable for at least 750 ms.
        double elapsedSplashMs = Stopwatch.GetElapsedTime(splashStarted).TotalMilliseconds;
        double elapsedResultMs = Stopwatch.GetElapsedTime(
            startupResult.CalibrationStatusShownTimestamp).TotalMilliseconds;
        double remainingMs = Math.Max(
            1500 - elapsedSplashMs,
            750 - elapsedResultMs);
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = System.TimeSpan.FromMilliseconds(Math.Max(1, remainingMs))
        };

        timer.Tick += (s, ev) =>
        {
            timer.Stop();

            // 3. クロスフェードトランジション
            mainWindow.Opacity = 0.0;
            mainWindow.Show();

            // アニメーションパラメータ (1秒かけてイージング付きで遷移)
            var duration = new Duration(System.TimeSpan.FromMilliseconds(1000));
            var ease = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
            };

            // フェードイン・フェードアウトのアニメーション作成
            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, duration)
            {
                EasingFunction = ease
            };
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, duration)
            {
                EasingFunction = ease
            };

            fadeIn.Completed += (s2, ev2) =>
            {
                // アニメーションによる依存プロパティのロックを完全に解除し、
                // WPF本来の通常描画モード（タイトル画面導入前と完全に同じ描画ステート）へ復帰させます
                mainWindow.BeginAnimation(UIElement.OpacityProperty, null);
                mainWindow.Opacity = 1.0;
            };

            fadeOut.Completed += (s2, ev2) =>
            {
                splash!.Close();
            };

            // アニメーションを同時に開始
            mainWindow.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            splash!.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        };

        timer.Start();
    }

    private void App_DispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Console.Error.WriteLine("UI Exception: " + e.Exception.ToString());
        SplashWindow? splash = Windows.OfType<SplashWindow>().FirstOrDefault();
        if (splash is not null)
        {
            splash.Topmost = false;
            splash.Hide();
        }
        try
        {
            MessageBox.Show(
                "UI Exception: " + e.Exception.ToString(),
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { }
        e.Handled = true;
        if (splash is not null)
        {
            splash.Close();
            Shutdown(-1);
        }
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Console.Error.WriteLine("Domain Exception: " + e.ExceptionObject?.ToString());
        try
        {
            MessageBox.Show(
                "Domain Exception: " + e.ExceptionObject?.ToString(),
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { }
    }

    private static void NormalizeProcessPriority()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            process.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal;
        }
        catch
        {
            // Ignore policy/permissions errors during process exit
        }
    }

    private sealed class SplashApplicationStartupProgress(SplashWindow splash)
        : IApplicationStartupProgress
    {
        public void Report(string status) => splash.SetCalibrationStatus(status);
    }
}

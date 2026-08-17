using System.Windows;
using System.Windows.Threading;

namespace PairUp.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var splash = new SplashWindow();
        splash.Show();
        var splashShownAt = DateTime.UtcNow;

        // Let the splash actually paint a frame before MainWindow's constructor (which does
        // synchronous WASAPI device enumeration) blocks the UI thread.
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        var main = new MainWindow();
        MainWindow = main;

        void CloseSplash(object? sender, RoutedEventArgs args)
        {
            main.Loaded -= CloseSplash;

            // Keep the splash up for a minimum stretch so its entrance animation is always
            // visible, even when device enumeration finishes almost instantly.
            var minDuration = TimeSpan.FromMilliseconds(900);
            var elapsed = DateTime.UtcNow - splashShownAt;
            var remaining = minDuration - elapsed;

            if (remaining <= TimeSpan.Zero)
            {
                splash.Close();
            }
            else
            {
                var timer = new DispatcherTimer { Interval = remaining };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    splash.Close();
                };
                timer.Start();
            }
        }
        main.Loaded += CloseSplash;

        main.Show();
    }
}

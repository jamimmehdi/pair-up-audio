using System.Windows;
using System.Windows.Media.Animation;

namespace PairUp.App;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => StartAnimation();
    }

    private void StartAnimation()
    {
        var popIn = new DoubleAnimation
        {
            From = 0.7,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(450),
            EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut }
        };
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, popIn);
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, popIn);

        var wordFade = new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = TimeSpan.FromMilliseconds(400),
            BeginTime = TimeSpan.FromMilliseconds(200)
        };
        Wordmark.BeginAnimation(OpacityProperty, wordFade);

        var wordRise = new DoubleAnimation
        {
            From = 8, To = 0,
            Duration = TimeSpan.FromMilliseconds(400),
            BeginTime = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        WordmarkOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, wordRise);

        // Gentle continuous "breathing" pulse for as long as the splash stays on screen.
        var breathe = new DoubleAnimation
        {
            From = 1.0,
            To = 1.06,
            Duration = TimeSpan.FromMilliseconds(900),
            BeginTime = TimeSpan.FromMilliseconds(650),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, breathe);
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, breathe);
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PairUp.App.Services;

namespace PairUp.App.Controls;

/// <summary>
/// Several translucent sine ribbons drifting across the master bar at different speeds and
/// heights, each amplitude-driven by a different slice of the live FFT spectrum — the
/// "Layered Pulse Waves" design approved over a dome/sphere shape, which didn't suit a wide,
/// short rectangle.
/// </summary>
public sealed class LayeredWaveVisualizer
{
    private const int Bands = 32;
    private const int PointsPerLine = 100;

    private sealed record Layer(double Speed, double BaseAmpFraction, double YBaseFraction,
        double Frequency, bool UsesAccent2, double Alpha, double StrokeThickness, int BandStart, int BandEnd);

    private readonly Layer[] _layers =
    {
        new(0.5, 0.14, 0.35, 1.4, false, 0.55, 2.0, 0, 11),   // bass — slow, tall
        new(0.8, 0.10, 0.55, 2.1, false, 0.35, 1.6, 11, 22),  // mids — faster, shorter
        new(0.3, 0.18, 0.68, 0.9, true, 0.30, 2.0, 22, 32),   // treble — slowest, tallest, amber
    };

    private readonly Canvas _canvas;
    private readonly AudioEngine _engine;
    private readonly Polyline[] _polylines;
    private readonly SolidColorBrush[] _brushes;
    private readonly double[] _smoothedLevels;

    private double _time;
    private DateTime _lastFrame = DateTime.Now;
    private bool _running;

    public LayeredWaveVisualizer(Canvas canvas, AudioEngine engine)
    {
        _canvas = canvas;
        _engine = engine;
        _polylines = new Polyline[_layers.Length];
        _brushes = new SolidColorBrush[_layers.Length];
        _smoothedLevels = new double[_layers.Length];

        for (var i = 0; i < _layers.Length; i++)
        {
            var layer = _layers[i];
            var brush = new SolidColorBrush(GetThemeColor(layer.UsesAccent2)) { Opacity = layer.Alpha };
            var line = new Polyline
            {
                Stroke = brush,
                StrokeThickness = layer.StrokeThickness,
                StrokeLineJoin = PenLineJoin.Round
            };
            _brushes[i] = brush;
            _polylines[i] = line;
            _canvas.Children.Add(line);
        }
    }

    private static Color GetThemeColor(bool accent2)
    {
        var key = accent2 ? "Accent2Color" : "AccentColor";
        return Application.Current.TryFindResource(key) is Color color ? color : Colors.Cyan;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _lastFrame = DateTime.Now;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var width = _canvas.ActualWidth;
        var height = _canvas.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var now = DateTime.Now;
        var dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        _time += dt;

        var bands = _engine.GetSpectrum(Bands);

        for (var i = 0; i < _layers.Length; i++)
        {
            var layer = _layers[i];

            var rawLevel = 0f;
            for (var b = layer.BandStart; b < layer.BandEnd; b++)
                rawLevel = Math.Max(rawLevel, bands[b]);

            // Smooth so the ribbon flexes rather than jitters frame to frame.
            _smoothedLevels[i] += (rawLevel - _smoothedLevels[i]) * Math.Min(1.0, dt * 6.0);

            var amplitude = height * layer.BaseAmpFraction * (1.0 + _smoothedLevels[i] * 1.4);
            var yBase = height * layer.YBaseFraction;

            var points = new PointCollection(PointsPerLine + 1);
            for (var p = 0; p <= PointsPerLine; p++)
            {
                var x = width * p / PointsPerLine;
                var y = yBase + Math.Sin(p * 0.18 * layer.Frequency + _time * layer.Speed * 4) * amplitude;
                points.Add(new Point(x, y));
            }

            _polylines[i].Points = points;
            _brushes[i].Color = GetThemeColor(layer.UsesAccent2);
        }
    }
}

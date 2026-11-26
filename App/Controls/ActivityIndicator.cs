namespace Accel.App.Controls;

using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Accel.Metrics;

/// <summary>
/// The small working / idle badge shown on a session row in panel A and on its tab in panel C.
///
/// <list type="bullet">
/// <item><b>Working</b> - an accent-coloured three-quarter ring that turns.</item>
/// <item><b>Idle</b> - a still, hollow, muted ring (the session is waiting for its next prompt).</item>
/// <item><b>None</b> - collapsed, takes no room.</item>
/// </list>
///
/// <para>State is carried three ways - shape (open arc vs closed ring), motion, and colour - plus the tooltip and
/// automation name, so it is never colour-only. The turning is a single <see cref="RotateTransform"/> animation
/// that only runs while the element is actually visible and loaded (a tree row that is scrolled away or a hidden
/// panel costs nothing), and not at all when Windows' animations are switched off
/// (<see cref="SystemParameters.ClientAreaAnimation"/>): the arc then simply sits there, still distinct from the
/// ring.</para>
///
/// <para>Self-drawing (<see cref="OnRender"/>) rather than a template, like <see cref="EffortBarsControl"/>: a row
/// is created for every session on every rebuild, so each one stays as cheap as a single visual.</para>
/// </summary>
public sealed class ActivityIndicator : FrameworkElement
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(SessionActivity),
        typeof(ActivityIndicator),
        new FrameworkPropertyMetadata(SessionActivity.None, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));

    private const double Size = 12.0;
    private const double Thickness = 1.8;

    // Theme.xaml's AccentBrush (#F0A868) and TextMutedBrush (#6E6E6E), frozen once - same convention as EffortBarsControl.
    private static readonly Pen WorkingPen = MakePen(Color.FromRgb(0xF0, 0xA8, 0x68), roundCaps: true);
    private static readonly Pen IdlePen = MakePen(Color.FromRgb(0x6E, 0x6E, 0x6E), roundCaps: false);
    private static readonly Geometry RingGeometry = MakeRing();
    private static readonly Geometry ArcGeometry = MakeArc();

    private readonly RotateTransform _rotation = new();
    private bool _animating;

    public ActivityIndicator()
    {
        // No fixed Width/Height: an explicit size would override MeasureOverride and make a "none" indicator
        // reserve room. The default state is None, so it starts collapsed (OnStateChanged never runs for it).
        Visibility = Visibility.Collapsed;
        VerticalAlignment = VerticalAlignment.Center;
        SnapsToDevicePixels = false;
        IsHitTestVisible = true; // so the tooltip works
        RenderTransform = _rotation;
        RenderTransformOrigin = new Point(0.5, 0.5);

        IsVisibleChanged += (_, _) => UpdateAnimation();
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => UpdateAnimation();
    }

    public SessionActivity State
    {
        get => (SessionActivity)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        State == SessionActivity.None ? new Size(0, 0) : new Size(Size, Size);

    protected override void OnRender(DrawingContext drawingContext)
    {
        switch (State)
        {
            case SessionActivity.Working:
                drawingContext.DrawGeometry(null, WorkingPen, ArcGeometry);
                break;
            case SessionActivity.Idle:
                drawingContext.DrawGeometry(null, IdlePen, RingGeometry);
                break;
        }
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var indicator = (ActivityIndicator)d;
        var state = (SessionActivity)e.NewValue;

        indicator.Visibility = state == SessionActivity.None ? Visibility.Collapsed : Visibility.Visible;
        indicator.ToolTip = state switch
        {
            SessionActivity.Working => "Working",
            SessionActivity.Idle => "Idle - waiting for your next prompt",
            _ => null,
        };
        AutomationProperties.SetName(indicator, state switch
        {
            SessionActivity.Working => "Working",
            SessionActivity.Idle => "Idle",
            _ => string.Empty,
        });

        indicator.UpdateAnimation();
    }

    /// <summary>Runs the turning only while it can be seen and is wanted; stops it (and releases the clock)
    /// otherwise.</summary>
    private void UpdateAnimation()
    {
        bool shouldAnimate = State == SessionActivity.Working && IsVisible && IsLoaded && SystemParameters.ClientAreaAnimation;
        if (shouldAnimate == _animating)
        {
            return;
        }

        _animating = shouldAnimate;
        if (shouldAnimate)
        {
            _rotation.BeginAnimation(
                RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.0))) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _rotation.Angle = 0;
        }
    }

    private static Pen MakePen(Color color, bool roundCaps)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, Thickness);
        if (roundCaps)
        {
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
        }

        pen.Freeze();
        return pen;
    }

    private static Geometry MakeRing()
    {
        double r = (Size - Thickness) / 2.0;
        var geometry = new EllipseGeometry(new Point(Size / 2.0, Size / 2.0), r, r);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>A 270-degree arc starting at 12 o'clock, going clockwise.</summary>
    private static Geometry MakeArc()
    {
        double r = (Size - Thickness) / 2.0;
        var center = new Point(Size / 2.0, Size / 2.0);
        var start = new Point(center.X, center.Y - r);
        var end = new Point(center.X - r, center.Y); // 9 o'clock: 270 degrees clockwise from 12

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, isLargeArc: true, SweepDirection.Clockwise, isStroked: true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }
}

using System.Windows;
using System.Windows.Media;
using WinIDM.Core.Models;

namespace WinIDM.App.Views;

/// <summary>One part of a download as fractions of the whole file (0-1): where it starts, how long it is, how much is done.</summary>
public sealed record SegmentFill(double Start, double Length, double Done)
{
    /// <summary>Fractions for the live parts of a download of <paramref name="total"/> bytes; empty when unknown.</summary>
    public static IReadOnlyList<SegmentFill> From(IReadOnlyList<SegmentInfo> segments, long total)
    {
        if (total <= 0 || segments.Count <= 1) return [];
        return segments
            .Where(s => s.End >= s.Start)
            .Select(s =>
            {
                var length = s.End - s.Start + 1;
                return new SegmentFill((double)s.Start / total, (double)length / total,
                    Math.Clamp((double)s.Downloaded / length, 0, 1));
            })
            .ToList();
    }
}

/// <summary>
/// Thin bar that draws each part of a multi-part download in its place in the file (IDM+ "Show progress for each
/// segment"). A gap between parts makes the boundaries visible.
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<SegmentFill>), typeof(SegmentBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(SegmentBar),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(SegmentBar),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<SegmentFill>? Segments
    {
        get => (IReadOnlyList<SegmentFill>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, 4);

    protected override void OnRender(DrawingContext dc)
    {
        var segments = Segments;
        var width = ActualWidth;
        var height = ActualHeight;
        if (segments is not { Count: > 0 } || width <= 0 || height <= 0) return;

        const double Gap = 1;
        foreach (var s in segments)
        {
            var x = s.Start * width;
            var w = Math.Max(0, s.Length * width - Gap);
            if (w <= 0) continue;
            dc.DrawRectangle(TrackBrush, null, new Rect(x, 0, w, height));
            if (s.Done > 0) dc.DrawRectangle(FillBrush, null, new Rect(x, 0, w * s.Done, height));
        }
    }
}

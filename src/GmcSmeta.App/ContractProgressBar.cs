using System.Windows;
using System.Windows.Media;

namespace GmcSmeta.App;

/// <summary>Python ObjectPage.StackBar: signed blue, submitted amber, remaining track.</summary>
public sealed class ContractProgressBar : FrameworkElement
{
    public static readonly DependencyProperty SignedFractionProperty = DependencyProperty.Register(
        nameof(SignedFraction), typeof(double), typeof(ContractProgressBar), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PendingFractionProperty = DependencyProperty.Register(
        nameof(PendingFraction), typeof(double), typeof(ContractProgressBar), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double SignedFraction { get => (double)GetValue(SignedFractionProperty); set => SetValue(SignedFractionProperty, value); }
    public double PendingFraction { get => (double)GetValue(PendingFractionProperty); set => SetValue(PendingFractionProperty, value); }
    private static readonly Brush Track = Brush(0xE4, 0xE8, 0xEF), Signed = Brush(0x25, 0x63, 0xEB), Pending = Brush(0xF5, 0x9E, 0x0B);
    private static Brush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush;
    }
    protected override void OnRender(DrawingContext drawingContext)
    {
        var bounds = new Rect(RenderSize);
        drawingContext.PushClip(new RectangleGeometry(bounds, bounds.Height / 2, bounds.Height / 2));
        drawingContext.DrawRectangle(Track, null, bounds);
        var signed = bounds.Width * Math.Clamp(SignedFraction, 0, 1);
        var pending = Math.Min(bounds.Width - signed, bounds.Width * Math.Clamp(PendingFraction, 0, 1));
        drawingContext.DrawRectangle(Signed, null, new Rect(0, 0, signed, bounds.Height));
        drawingContext.DrawRectangle(Pending, null, new Rect(signed, 0, pending, bounds.Height));
        drawingContext.Pop();
    }
}

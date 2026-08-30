using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Sling.App.Editor;

/// <summary>
/// Puts a modal overlay on screen, and takes it off again.
/// </summary>
/// <remarks>
/// <para>
/// Sling's two modal things - the settings panel and the name prompt - are overlays inside
/// the window rather than separate windows, so nothing gives them the entrance a real
/// dialog gets for free. Appearing instantly, fully formed, on top of a window that has not
/// visibly changed is the thing that makes an in-window overlay read as a rendering glitch
/// rather than as a layer.
/// </para>
/// <para>
/// <b>Only the entrance is animated.</b> Closing is immediate, and that is a decision:
/// every close path here either hands an answer to a waiting task or is on the way out of
/// the application, and an exit animation puts a hundred and forty milliseconds of
/// half-transparent card between "the user pressed Escape" and "the work continues",
/// which is exactly the shape of the close-path bug this codebase has already shipped once.
/// </para>
/// </remarks>
internal static class Overlays
{
    private static readonly Duration Entrance = new(TimeSpan.FromMilliseconds(130));

    /// <summary>The scale the card grows from. Close to one: a hint of movement, not a zoom.</summary>
    private const double StartScale = 0.97;

    /// <summary>Shows <paramref name="scrim"/> and grows <paramref name="card"/> into place.</summary>
    internal static void Reveal(UIElement scrim, FrameworkElement card)
    {
        ArgumentNullException.ThrowIfNull(scrim);
        ArgumentNullException.ThrowIfNull(card);

        scrim.Visibility = Visibility.Visible;
        scrim.BeginAnimation(UIElement.OpacityProperty, Fade(0, 1));

        // A fresh transform each time rather than one declared in the style. A Freezable in
        // a Setter is shared by every element the style is applied to, and animating a
        // shared transform would move both overlays at once.
        var scale = new ScaleTransform(StartScale, StartScale);

        card.RenderTransformOrigin = new Point(0.5, 0.5);
        card.RenderTransform = scale;

        var grow = new DoubleAnimation(StartScale, 1, Entrance)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>Takes <paramref name="scrim"/> off screen.</summary>
    /// <remarks>
    /// The opacity animation is removed before the value is set. A finished animation still
    /// holds the property at its last value, so assigning <c>Opacity</c> without clearing it
    /// first is a write that silently does nothing - and the next <see cref="Reveal"/> would
    /// then animate from a value the animation, not this method, decided.
    /// </remarks>
    internal static void Hide(UIElement scrim)
    {
        ArgumentNullException.ThrowIfNull(scrim);

        scrim.BeginAnimation(UIElement.OpacityProperty, null);
        scrim.Opacity = 1;
        scrim.Visibility = Visibility.Collapsed;
    }

    private static DoubleAnimation Fade(double from, double to) => new(from, to, Entrance);
}

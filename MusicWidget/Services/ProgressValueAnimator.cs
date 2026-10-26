using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace MusicWidget.Services;

/// <summary>Smoothly animates ProgressBar / Slider values instead of jumping.</summary>
public static class ProgressValueAnimator
{
    private static readonly Duration DefaultDuration = new(TimeSpan.FromMilliseconds(360));

    public static void AnimateTo(ProgressBar bar, double target, bool snapBackwards = true)
    {
        if (bar is null) return;
        AnimateRange(bar, target, snapBackwards);
    }

    public static void AnimateTo(Slider slider, double target, bool snapBackwards = true)
    {
        if (slider is null) return;
        AnimateRange(slider, target, snapBackwards);
    }

    /// <summary>Stops any running value animation and sets the value immediately.</summary>
    public static void SnapTo(RangeBase control, double target)
    {
        if (control is null) return;
        control.BeginAnimation(RangeBase.ValueProperty, null);
        control.Value = target;
    }

    private static void AnimateRange(RangeBase control, double target, bool snapBackwards)
    {
        target = Math.Clamp(target, control.Minimum, control.Maximum);
        var from = control.Value;

        if (Math.Abs(from - target) < 0.05)
        {
            SnapTo(control, target);
            return;
        }

        // Resets (e.g. next download job) should jump, not reverse-animate.
        if (snapBackwards && target + 0.75 < from)
        {
            SnapTo(control, target);
            return;
        }

        // Large seek jumps (track change) — snap rather than crawl across the bar.
        var span = Math.Max(1.0, control.Maximum - control.Minimum);
        if (Math.Abs(target - from) > span * 0.35)
        {
            SnapTo(control, target);
            return;
        }

        control.BeginAnimation(RangeBase.ValueProperty, null);

        var anim = new DoubleAnimation(from, target, DefaultDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        anim.Completed += (_, _) =>
        {
            control.BeginAnimation(RangeBase.ValueProperty, null);
            control.Value = target;
        };
        control.BeginAnimation(RangeBase.ValueProperty, anim);
    }
}

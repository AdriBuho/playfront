using System;
using Avalonia.Animation.Easings;

namespace Playfront.App;

// Deceleration curve of the console's content transitions.
//
// Measured frame by frame at 119 fps: the moving element loses ~56% of its REMAINING distance
// every 142 ms, all the way down. That is exponential decay, and no built-in easing does it:
//
//     ms:   439   556   672   789   906  1030  ~1180
//     left: 140    63    28    12     5   1.7      0   (px to target)
//
// Why the built-ins fail here: CubicEaseOut is 87% done at half its duration and 98% at three
// quarters, so it lands early and hard. The console spends its last 250 ms travelling the final
// 9% - that long creep is the whole character of the animation, and dropping it makes the screen
// feel like it snaps.
//
// K is duration/time-constant. With Duration=0.79s that puts the constant at ~146 ms, which is
// what the console measures. Verified by recording our own transition the same way: 43.5% of the
// remaining distance per 117 ms against the console's 44.9%, settling at 821 ms against ~800.
// Normalizing by (1 - e^-K) matters: without it Ease(1) returns 0.9955 and every
// transition stops a fraction of a pixel short of its target, which accumulates visibly when
// transitions are re-targeted mid-flight (holding the D-pad through several categories).
public sealed class ConsoleSettleEasing : Easing
{
    private const double K = 5.4;
    private static readonly double Norm = 1.0 - Math.Exp(-K);

    public override double Ease(double progress) => (1.0 - Math.Exp(-K * progress)) / Norm;
}

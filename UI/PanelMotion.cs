using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace DesktopBar.UI
{
    internal sealed class PanelMotion
    {
        private double _from, _to, _initialSpeed, _speed, _duration;
        private TimeSpan? _started;

        internal double Progress { get; private set; }
        internal bool IsRunning { get; private set; }

        internal void Reset(double progress)
        {
            Progress = progress;
            _speed = 0;
            IsRunning = false;
        }

        internal void MoveTo(double target, double milliseconds)
        {
            double distance = Math.Abs(target - Progress);
            if (distance < .0001)
            {
                Reset(target);
                return;
            }

            _from = Progress;
            _to = target;
            _duration = Math.Max(85, milliseconds * Math.Sqrt(distance));
            // Near either endpoint, limit carry-through so a reversal cannot touch
            // the opposite boundary and briefly disappear or jump to full size.
            _initialSpeed = Math.Clamp(_speed * _duration, -8 * _from, 8 * (1 - _from));
            _started = null;
            IsRunning = true;
        }

        internal void Advance(TimeSpan renderingTime)
        {
            if (!IsRunning) return;
            var start = _started ??= renderingTime;
            double t = (renderingTime - start).TotalMilliseconds / _duration;
            if (t >= 1)
            {
                Reset(_to);
                return;
            }

            double remaining = 1 - t;
            double remainingSquared = remaining * remaining;
            double change = _to - _from;
            double angle = Math.PI * t;
            double eased = (1 - Math.Cos(angle)) / 2;
            // Carry velocity through reversals. Both terms reach the destination at rest.
            double value = _from + change * eased + _initialSpeed * t * remainingSquared * remainingSquared;
            Progress = Math.Clamp(value, 0, 1);
            _speed = value == Progress
                ? (change * (Math.PI / 2) * Math.Sin(angle)
                    + _initialSpeed * remainingSquared * remaining * (1 - 5 * t)) / _duration
                : 0;
        }

        internal static double Ease(double progress)
            => progress * progress * progress * (10 + progress * (6 * progress - 15));
    }

    internal sealed class PanelEase : EasingFunctionBase
    {
        protected override double EaseInCore(double normalizedTime) => PanelMotion.Ease(normalizedTime);
        protected override Freezable CreateInstanceCore() => new PanelEase();
    }
}

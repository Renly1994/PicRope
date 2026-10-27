using System;

namespace ScreenLine
{
    internal sealed class SpringMotion
    {
        readonly double frequency, decay;
        public double Position, Velocity, Target;
        public bool Settled { get { return Position == Target && Velocity == 0; } }

        public SpringMotion(double response, double damping, double initial)
        {
            double natural = 2 * Math.PI / response;
            decay = natural * damping;
            frequency = natural * Math.Sqrt(1 - damping * damping);
            Snap(initial);
        }

        public void Snap(double value) { Position = Target = value; Velocity = 0; }

        public void Step(double seconds)
        {
            if (seconds <= 0) return;
            // 直接求阻尼振子的解析解，帧率变化时仍保持相同轨迹与连续速度。
            double offset = Position - Target;
            double sine = Math.Sin(frequency * seconds), cosine = Math.Cos(frequency * seconds);
            double envelope = Math.Exp(-decay * seconds);
            double coefficient = (Velocity + decay * offset) / frequency;
            Position = Target + envelope * (offset * cosine + coefficient * sine);
            Velocity = envelope * (Velocity * cosine - (decay * coefficient + frequency * offset) * sine);
            if (Math.Abs(Position - Target) < 0.005 && Math.Abs(Velocity) < 0.01) Snap(Target);
        }
    }
}

using System;

namespace D2NG.Core.D2GS
{
    public class Point
    {
        public ushort X { get; }
        public ushort Y { get; }

        public Point(ushort x, ushort y) => (X, Y) = (x, y);

        public void Deconstruct(out ushort x, out ushort y) => (x, y) = (X, Y);

        public static bool operator ==(Point p1, Point p2)
        {
            if ((object)p1 == null)
                return (object)p2 == null;

            return p1.Equals(p2);
        }

        public static bool operator !=(Point p1, Point p2)
        {
            return !(p1 == p2);
        }

        public override bool Equals(object obj)
        {
            if (obj == null || GetType() != obj.GetType())
                return false;

            var p2 = (Point)obj;
            return (X == p2.X && Y == p2.Y);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = X.GetHashCode();
                hashCode = (hashCode * 397) ^ Y.GetHashCode();
                return hashCode;
            }
        }

        public double Distance(Point other)
        {
            return Math.Sqrt(Math.Pow(X - other.X, 2.0) + Math.Pow(Y - other.Y, 2.0));
        }

        public double DistanceSquared(Point other)
        {
            return Math.Pow(X - other.X, 2.0) + Math.Pow(Y - other.Y, 2.0);
        }

        private ushort Lerp(ushort first, ushort second, float alpha)
        {
            return (ushort)(first * (1 - alpha) + second * alpha);
        }

        public Point Lerp(Point other, float alpha)
        {
            return new Point(Lerp(X, other.X, alpha), Lerp(Y, other.Y, alpha));
        }

        public static (double, double) Normalize((double, double) point)
        {
            var length = Math.Sqrt(Math.Pow(point.Item1, 2.0) + Math.Pow(point.Item2, 2.0));
            return (Convert.ToDouble(point.Item1) / length, Convert.ToDouble(point.Item2) / length);
        }

        public (double, double) Multiply(float multiplier)
        {
            return ((X * multiplier), (Y * multiplier));
        }

        public Point Add(short x, short y)
        {
            int newX = X + x;
            int newY = Y + y;
            if (newX < 0 || newY < 0 || newX > ushort.MaxValue || newY > ushort.MaxValue)
            {
                throw new ArithmeticException();
            }

            return new Point((ushort)(newX), (ushort)(newY));
        }
        public (double, double) Substract((double, double) other)
        {
            return ((X - other.Item1), (Y - other.Item2));
        }

        public static Point operator -(Point a, Point b)
        {
            int x = a.X - b.X;
            int y = a.Y - b.Y;
            if (x < 0 || y < 0)
            {
                throw new ArithmeticException();
            }
            return new Point((ushort)x, (ushort)y);
        }

        public static Point operator +(Point a, Point b)
        {
            int x = a.X + b.X;
            int y = a.Y + b.Y;
            if (x > ushort.MaxValue || y > ushort.MaxValue)
            {
                throw new ArithmeticException();
            }
            return new Point((ushort)x, (ushort)y);
        }

        public Point GetPointPastPointInSameDirection(Point other, double distance)
        {
            var (decX, decY) = other;
            var difference = Substract((Convert.ToDouble(decX), Convert.ToDouble(decY)));
            var (nX, nY) = Normalize(difference);
            return new Point((ushort)(other.X - nX * distance), (ushort)(other.Y - nY * distance));
        }

        public Point GetPointBeforePointInSameDirection(Point other, double distance)
        {
            var (decX, decY) = other;
            var difference = Substract((Convert.ToDouble(decX), Convert.ToDouble(decY)));
            var (nX, nY) = Normalize(difference);
            return new Point((ushort)(other.X + nX * distance), (ushort)(other.Y + nY * distance));
        }

        public override string ToString() => $"({X}, {Y})";
    }
}

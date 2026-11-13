namespace Highball.World;

/// The square grid cells (Size metres a side) a box on the plan covers, for walking with foreach without making anything:
/// filing a whole route's segments by cell asks for this hundreds of thousands of times.
public readonly struct GridCells(double ax, double az, double bx, double bz, double size)
{
    private readonly int _x0 = (int)Math.Floor(Math.Min(ax, bx) / size);
    private readonly int _x1 = (int)Math.Floor(Math.Max(ax, bx) / size);
    private readonly int _z0 = (int)Math.Floor(Math.Min(az, bz) / size);
    private readonly int _z1 = (int)Math.Floor(Math.Max(az, bz) / size);

    public Enumerator GetEnumerator() => new(_x0, _x1, _z0, _z1);

    public struct Enumerator(int x0, int x1, int z0, int z1)
    {
        private int _x = x0 - 1, _z = z0;

        public readonly (int X, int Z) Current => (_x, _z);

        public bool MoveNext()
        {
            if (++_x <= x1)
                return _z <= z1;
            _x = x0;
            return ++_z <= z1;
        }
    }
}

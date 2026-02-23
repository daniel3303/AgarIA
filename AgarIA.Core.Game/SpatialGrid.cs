namespace AgarIA.Core.Game;

public class SpatialGrid<T>
{
    private const int CellSize = 200;

    private readonly Dictionary<long, List<T>> _cells = new();
    private readonly List<List<T>> _listPool = new();
    private readonly Func<T, double> _getX;
    private readonly Func<T, double> _getY;
    private readonly List<T> _queryBuffer = new();
    private List<T> _allItems = new();

    public SpatialGrid(Func<T, double> getX, Func<T, double> getY)
    {
        _getX = getX;
        _getY = getY;
    }

    public List<T> AllItems => _allItems;

    public void Rebuild(IEnumerable<T> items)
    {
        // Return lists to pool
        foreach (var list in _cells.Values)
        {
            list.Clear();
            _listPool.Add(list);
        }
        _cells.Clear();

        _allItems = items as List<T> ?? items.ToList();

        foreach (var item in _allItems)
        {
            var key = CellKey((int)(_getX(item) / CellSize), (int)(_getY(item) / CellSize));
            if (!_cells.TryGetValue(key, out var list))
            {
                if (_listPool.Count > 0)
                {
                    list = _listPool[_listPool.Count - 1];
                    _listPool.RemoveAt(_listPool.Count - 1);
                }
                else
                {
                    list = new List<T>();
                }
                _cells[key] = list;
            }
            list.Add(item);
        }
    }

    public List<T> Query(double x, double y, double radius)
    {
        _queryBuffer.Clear();
        var minCx = (int)((x - radius) / CellSize);
        var maxCx = (int)((x + radius) / CellSize);
        var minCy = (int)((y - radius) / CellSize);
        var maxCy = (int)((y + radius) / CellSize);

        for (var cx = minCx; cx <= maxCx; cx++)
        {
            for (var cy = minCy; cy <= maxCy; cy++)
            {
                if (_cells.TryGetValue(CellKey(cx, cy), out var list))
                {
                    _queryBuffer.AddRange(list);
                }
            }
        }

        return _queryBuffer;
    }

    private static long CellKey(int cx, int cy)
    {
        return ((long)cx << 32) | (uint)cy;
    }
}

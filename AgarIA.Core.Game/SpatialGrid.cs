namespace AgarIA.Core.Game;

public class SpatialGrid<T>
{
    private const int CellSize = 200;
    private const int GridWidth = 20; // MapSize (4000) / CellSize (200)
    private const int TotalCells = GridWidth * GridWidth;

    private readonly List<T>[] _cells;
    private readonly Func<T, double> _getX;
    private readonly Func<T, double> _getY;
    private List<T> _allItems = new();

    public SpatialGrid(Func<T, double> getX, Func<T, double> getY)
    {
        _getX = getX;
        _getY = getY;
        _cells = new List<T>[TotalCells];
        for (int i = 0; i < TotalCells; i++)
            _cells[i] = new List<T>();
    }

    public List<T> AllItems => _allItems;

    public void Rebuild(IEnumerable<T> items)
    {
        for (int i = 0; i < TotalCells; i++)
            _cells[i].Clear();

        _allItems = items as List<T> ?? items.ToList();

        foreach (var item in _allItems)
        {
            var cx = Math.Clamp((int)(_getX(item) / CellSize), 0, GridWidth - 1);
            var cy = Math.Clamp((int)(_getY(item) / CellSize), 0, GridWidth - 1);
            _cells[cy * GridWidth + cx].Add(item);
        }
    }

    public List<T> Query(double x, double y, double radius, List<T> buffer = null)
    {
        buffer ??= new List<T>();
        buffer.Clear();
        var minCx = Math.Max(0, (int)((x - radius) / CellSize));
        var maxCx = Math.Min(GridWidth - 1, (int)((x + radius) / CellSize));
        var minCy = Math.Max(0, (int)((y - radius) / CellSize));
        var maxCy = Math.Min(GridWidth - 1, (int)((y + radius) / CellSize));

        for (var cx = minCx; cx <= maxCx; cx++)
        {
            for (var cy = minCy; cy <= maxCy; cy++)
            {
                var cell = _cells[cy * GridWidth + cx];
                if (cell.Count > 0)
                    buffer.AddRange(cell);
            }
        }

        return buffer;
    }
}

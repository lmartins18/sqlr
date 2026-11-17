using System.Globalization;
using Sqlr.Db;
using Terminal.Gui;

namespace Sqlr.Tui;

/// <summary>
/// Adapts a <see cref="ResultSet"/> to the TableView, with a client-side sort and filter
/// layered on top (the underlying rows are never modified).
/// </summary>
internal sealed class ResultTableSource : ITableSource
{
    private int[] _view;

    public ResultTableSource(ResultSet data)
    {
        Data  = data;
        _view = Enumerable.Range(0, data.Rows.Count).ToArray();
    }

    public ResultSet Data          { get; }
    public int       SortColumn    { get; private set; } = -1;
    public bool      SortDescending { get; private set; }
    public string?   Filter        { get; private set; }

    // ── ITableSource ───────────────────────────────────────────────────────

    public object this[int row, int col] => Data.Rows[_view[row]][col]!;

    public string[] ColumnNames => Data.Columns
        .Select((c, i) => i == SortColumn ? c + (SortDescending ? " ▼" : " ▲") : c)
        .ToArray();

    public int Columns => Data.Columns.Length;
    public int Rows    => _view.Length;

    // ── Row access ─────────────────────────────────────────────────────────

    public string?[] RowAt(int viewRow) => Data.Rows[_view[viewRow]];

    /// <summary>1-based position of the row in the original (unsorted, unfiltered) result.</summary>
    public int OriginalRowNumber(int viewRow) => _view[viewRow] + 1;

    public IEnumerable<string?[]> VisibleRows => _view.Select(i => Data.Rows[i]);

    // ── Sort / filter ──────────────────────────────────────────────────────

    /// <summary>Cycles the sort on <paramref name="col"/>: ascending → descending → original order.</summary>
    public void CycleSort(int col)
    {
        if (SortColumn != col) { SortColumn = col; SortDescending = false; }
        else if (!SortDescending) SortDescending = true;
        else SortColumn = -1;
        Rebuild();
    }

    public void ApplyFilter(string? filter)
    {
        Filter = string.IsNullOrEmpty(filter) ? null : filter;
        Rebuild();
    }

    private void Rebuild()
    {
        IEnumerable<int> idx = Enumerable.Range(0, Data.Rows.Count);

        if (Filter is { } f)
            idx = idx.Where(i => Data.Rows[i].Any(v => v is not null && v.Contains(f, StringComparison.OrdinalIgnoreCase)));

        var arr = idx.ToArray();

        if (SortColumn >= 0)
        {
            int c = SortColumn;
            Comparison<int> cmp;
            if (Data.IsNumeric(c))
            {
                // Parse once per row, not once per comparison
                var keys = new Dictionary<int, double?>(arr.Length);
                foreach (var i in arr)
                    keys[i] = double.TryParse(Data.Rows[i][c], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
                cmp = (a, b) => Nullable.Compare(keys[a], keys[b]);
            }
            else
            {
                cmp = (a, b) => string.Compare(Data.Rows[a][c], Data.Rows[b][c], StringComparison.OrdinalIgnoreCase);
            }

            // Stable: ties keep their original order
            Comparison<int> stable = (a, b) => { int r = cmp(a, b); return r != 0 ? r : a.CompareTo(b); };
            Array.Sort(arr, SortDescending ? (a, b) => -stable(a, b) : stable);
        }

        _view = arr;
    }
}

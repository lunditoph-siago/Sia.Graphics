using System.Runtime.InteropServices;

namespace Sia.Graphics.UI;

internal static partial class GridLayout
{
    private static void PlaceItems(Node style, List<GridItem> items, out int columnCount, out int rowCount)
    {
        var explicitCols = System.Math.Max(style.GridTemplateColumns.Count, 1);
        var explicitRows = System.Math.Max(style.GridTemplateRows.Count, 1);
        var rowFlow = style.GridAutoFlow is GridAutoFlow.Row or GridAutoFlow.RowDense;

        var occupied = new HashSet<(int Row, int Col)>();
        var maxRow = explicitRows - 1;
        var maxCol = explicitCols - 1;

        var span = CollectionsMarshal.AsSpan(items);
        var resolved = new (bool RowDef, int RowStart, int RowSpan, bool ColDef, int ColStart, int ColSpan)[span.Length];

        for (var i = 0; i < span.Length; i++) {
            var (rowDef, rowStart, rowSpan) = ResolvePlacement(span[i].Style.GridRow);
            var (colDef, colStart, colSpan) = ResolvePlacement(span[i].Style.GridColumn);
            resolved[i] = (rowDef, rowStart, rowSpan, colDef, colStart, colSpan);

            if (rowDef && colDef) {
                Occupy(occupied, rowStart, rowSpan, colStart, colSpan);
                span[i].RowStart = rowStart;
                span[i].RowSpan = rowSpan;
                span[i].ColStart = colStart;
                span[i].ColSpan = colSpan;
                maxRow = System.Math.Max(maxRow, rowStart + rowSpan - 1);
                maxCol = System.Math.Max(maxCol, colStart + colSpan - 1);
            }
        }

        var cursorRow = 0;
        var cursorCol = 0;

        for (var i = 0; i < span.Length; i++) {
            var (rowDef, rowStart, rowSpan, colDef, colStart, colSpan) = resolved[i];
            if (rowDef && colDef) continue;

            if (rowDef && !colDef) {
                var c = FindFreeSecondary(occupied, primaryFixed: rowStart, primarySpan: rowSpan, secondarySpan: colSpan,
                    primaryIsRow: true);
                span[i].RowStart = rowStart; span[i].RowSpan = rowSpan;
                span[i].ColStart = c; span[i].ColSpan = colSpan;
                Occupy(occupied, rowStart, rowSpan, c, colSpan);
                maxRow = System.Math.Max(maxRow, rowStart + rowSpan - 1);
                maxCol = System.Math.Max(maxCol, c + colSpan - 1);
            }
            else if (!rowDef && colDef) {
                var r = FindFreeSecondary(occupied, primaryFixed: colStart, primarySpan: colSpan, secondarySpan: rowSpan,
                    primaryIsRow: false);
                span[i].RowStart = r; span[i].RowSpan = rowSpan;
                span[i].ColStart = colStart; span[i].ColSpan = colSpan;
                Occupy(occupied, r, rowSpan, colStart, colSpan);
                maxRow = System.Math.Max(maxRow, r + rowSpan - 1);
                maxCol = System.Math.Max(maxCol, colStart + colSpan - 1);
            }
            else {
                int r, c;
                if (rowFlow) {
                    var colBound = System.Math.Max(explicitCols, colSpan);
                    (r, c) = FindFreeCellScan(occupied, cursorRow, cursorCol, colSpan, rowSpan, colBound, rowMajor: true);
                    cursorRow = r; cursorCol = c + colSpan;
                }
                else {
                    var rowBound = System.Math.Max(explicitRows, rowSpan);
                    (r, c) = FindFreeCellScan(occupied, cursorRow, cursorCol, rowSpan, colSpan, rowBound, rowMajor: false);
                    cursorCol = c; cursorRow = r + rowSpan;
                }
                span[i].RowStart = r; span[i].RowSpan = rowSpan;
                span[i].ColStart = c; span[i].ColSpan = colSpan;
                Occupy(occupied, r, rowSpan, c, colSpan);
                maxRow = System.Math.Max(maxRow, r + rowSpan - 1);
                maxCol = System.Math.Max(maxCol, c + colSpan - 1);
            }
        }

        columnCount = maxCol + 1;
        rowCount = maxRow + 1;
    }

    private static (bool Definite, int Start0, int Span) ResolvePlacement(GridPlacement placement)
    {
        if (placement.Start.HasValue && placement.End.HasValue) {
            var s = placement.Start.Value - 1;
            var e = placement.End.Value - 1;
            return (true, s, System.Math.Max(1, e - s));
        }
        if (placement.Start.HasValue && placement.Span.HasValue)
            return (true, placement.Start.Value - 1, System.Math.Max(1, placement.Span.Value));
        if (placement.Start.HasValue)
            return (true, placement.Start.Value - 1, 1);
        if (placement.End.HasValue && placement.Span.HasValue) {
            var s = placement.End.Value - 1 - placement.Span.Value;
            return (true, System.Math.Max(0, s), System.Math.Max(1, placement.Span.Value));
        }
        return (false, 0, System.Math.Max(1, placement.Span ?? 1));
    }

    private static bool IsFree(HashSet<(int, int)> occupied, int rowStart, int rowSpan, int colStart, int colSpan)
    {
        for (var r = rowStart; r < rowStart + rowSpan; r++)
            for (var c = colStart; c < colStart + colSpan; c++)
                if (occupied.Contains((r, c)))
                    return false;
        return true;
    }

    private static void Occupy(HashSet<(int, int)> occupied, int rowStart, int rowSpan, int colStart, int colSpan)
    {
        for (var r = rowStart; r < rowStart + rowSpan; r++)
            for (var c = colStart; c < colStart + colSpan; c++)
                occupied.Add((r, c));
    }

    private static int FindFreeSecondary(HashSet<(int, int)> occupied, int primaryFixed, int primarySpan, int secondarySpan, bool primaryIsRow)
    {
        for (var s = 0; ; s++) {
            var free = primaryIsRow
                ? IsFree(occupied, primaryFixed, primarySpan, s, secondarySpan)
                : IsFree(occupied, s, secondarySpan, primaryFixed, primarySpan);
            if (free) return s;
        }
    }

    private static (int Outer, int Inner) FindFreeCellScan(
        HashSet<(int, int)> occupied, int startOuter, int startInner, int innerSpan, int outerSpan, int innerBound, bool rowMajor)
    {
        var outer = startOuter;
        var inner = startInner;
        while (true) {
            if (inner + innerSpan > innerBound) {
                inner = 0;
                outer++;
                continue;
            }
            var free = rowMajor
                ? IsFree(occupied, outer, outerSpan, inner, innerSpan)
                : IsFree(occupied, inner, innerSpan, outer, outerSpan);
            if (free) return (outer, inner);
            inner++;
        }
    }
}

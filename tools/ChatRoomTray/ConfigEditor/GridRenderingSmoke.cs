#if WINDOWS_TRAY
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Eto.Forms;
using Eto.WinForms.Forms.Controls;

namespace ChatRoomTray.ConfigEditor;

/// <summary>Exercises the native grid paint path without opening a window.</summary>
internal static class GridRenderingSmoke
{
    public static void Verify(GridView template)
    {
        const int rowCount = 6;
        using var view = new GridView { ShowHeader = true };
        for (var index = 0; index < template.Columns.Count; index++)
        {
            var column = template.Columns[index];
            view.Columns.Add(new GridColumn
            {
                HeaderText = column.HeaderText,
                Width = column.Width,
                MinWidth = column.MinWidth,
                AutoSize = column.AutoSize,
                Expand = column.Expand,
                DataCell = new TextBoxCell(index),
            });
        }

        var rows = new List<object>();
        for (var row = 0; row < rowCount; row++)
            rows.Add(new[] { $"Server {row + 1}", "stdio", $"server-{row + 1}.exe" });
        view.DataStore = rows;

        view.AttachNative();
        var grid = ((GridViewHandler)view.Handler).Control;
        grid.Size = new Size(920, 320);
        using var host = new System.Windows.Forms.Panel { Size = grid.Size };
        host.Controls.Add(grid);
        host.CreateControl();
        grid.CreateControl();
        var paintedRows = new HashSet<int>();
        grid.RowPostPaint += (_, e) => paintedRows.Add(e.RowIndex);

        // Paint native rows directly: a hidden grid can skip its top-level paint.
        // This still runs Eto's RowPostPaint handler and reports its exceptions
        // here instead of swallowing them in a native window procedure.
        var paintRow = typeof(DataGridViewRow).GetMethod(
            "Paint", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DataGridViewRow.Paint is unavailable.");

        void Paint(int width)
        {
            grid.Width = width;
            grid.PerformLayout();
            paintedRows.Clear();
            using var bitmap = new Bitmap(grid.Width, grid.Height);
            using var graphics = Graphics.FromImage(bitmap);
            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                var row = grid.Rows[rowIndex];
                paintRow.Invoke(row, new object[]
                {
                    graphics, grid.ClientRectangle,
                    grid.GetRowDisplayRectangle(rowIndex, false),
                    rowIndex, row.State, rowIndex == 0, rowIndex == rowCount - 1,
                });
            }
            if (paintedRows.Count != rowCount)
                throw new InvalidOperationException(
                    $"Expected {rowCount} painted rows, got {paintedRows.Count}.");

            for (var index = 0; index < template.Columns.Count; index++)
            {
                if (template.Columns[index].Expand
                    && grid.Columns[index].AutoSizeMode != DataGridViewAutoSizeColumnMode.Fill)
                    throw new InvalidOperationException("The expanding column lost fill sizing.");
            }
        }

        Paint(920);  // First paint, before any selection/click.
        Paint(920);  // Repaint, as when returning to the tab.
        Paint(1120); // Resize must retain fill sizing and paint all rows.
    }
}
#endif

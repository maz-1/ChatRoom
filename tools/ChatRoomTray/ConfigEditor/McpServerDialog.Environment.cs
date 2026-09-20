using System;
using System.Collections.Generic;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;

namespace ChatRoomTray.ConfigEditor;

public sealed partial class McpServerDialog
{
    private readonly GridView _environmentList = new()
    {
        ID = "McpEnvironmentList",
        AllowMultipleSelection = false,
        ShowHeader = true,
        Height = 96, // Compact preference; the table expands into the remaining height.
    };
    private readonly List<EnvironmentVariableRow> _environmentVariables = new();
    private readonly Button _environmentAddButton = new() { ID = "McpEnvironmentAddButton", Text = "添加" };
    private readonly Button _environmentEditButton = new() { ID = "McpEnvironmentEditButton", Text = "编辑" };
    private readonly Button _environmentDeleteButton = new() { ID = "McpEnvironmentDeleteButton", Text = "删除" };

    private Control BuildEnvironmentEditor()
    {
        _environmentList.Columns.Add(new GridColumn
        {
            HeaderText = "名称",
            Width = 220,
            DataCell = new TextBoxCell
            {
                Binding = Binding.Property<EnvironmentVariableRow, string>(row => row.Name),
            },
        });
        _environmentList.Columns.Add(new GridColumn
        {
            HeaderText = "值",
            // Keep fill sizing without Eto WinForms' first-paint auto-size conflict.
            AutoSize = false,
            Expand = true,
            MinWidth = 160,
            DataCell = new TextBoxCell
            {
                Binding = Binding.Property<EnvironmentVariableRow, string>(row => row.Value),
            },
        });

        foreach (var button in new[] { _environmentAddButton, _environmentEditButton, _environmentDeleteButton })
            button.MinimumSize = new Size(92, 0);

        _environmentAddButton.Click += (_, _) => AddEnvironmentVariable();
        _environmentEditButton.Click += (_, _) => EditEnvironmentVariable();
        _environmentDeleteButton.Click += (_, _) => DeleteEnvironmentVariable();
        _environmentList.CellDoubleClick += (_, _) => EditEnvironmentVariable();
        _environmentList.SelectionChanged += (_, _) => UpdateEnvironmentButtons();
        _environmentList.KeyDown += (_, e) =>
        {
            if (e.Key == Keys.Insert)
                AddEnvironmentVariable();
            else if (e.Key == Keys.F2)
                EditEnvironmentVariable();
            else if (e.Key == Keys.Delete)
                DeleteEnvironmentVariable();
            else
                return;
            e.Handled = true;
        };

        var layout = new DynamicLayout { Spacing = new Size(6, 6) };
        layout.Add(_environmentList, yscale: true);
        layout.AddRow(Horizontal(_environmentAddButton, _environmentEditButton, _environmentDeleteButton));
        RefreshEnvironmentVariables();
        return layout;
    }

    private void LoadEnvironmentVariables(Dictionary<string, string> environment)
    {
        _environmentVariables.Clear();
        foreach (var pair in environment)
            _environmentVariables.Add(new EnvironmentVariableRow { Name = pair.Key, Value = pair.Value });
        RefreshEnvironmentVariables();
    }

    private void AddEnvironmentVariable()
    {
        using var dialog = new EnvironmentVariableInputDialog(
            "新建环境变量", string.Empty, string.Empty, name => FindEnvironmentIndex(name) >= 0);
        if (!dialog.ShowModal(this)) return;
        if (!TrySetEnvironmentVariable(-1, dialog.VariableName, dialog.VariableValue, out var error))
            Reject(error);
    }

    private void EditEnvironmentVariable()
    {
        var index = _environmentList.SelectedRow;
        if (index < 0 || index >= _environmentVariables.Count) return;
        var item = _environmentVariables[index];
        using var dialog = new EnvironmentVariableInputDialog(
            "编辑环境变量", item.Name, item.Value, name => FindEnvironmentIndex(name, index) >= 0);
        if (!dialog.ShowModal(this)) return;
        if (!TrySetEnvironmentVariable(index, dialog.VariableName, dialog.VariableValue, out var error))
            Reject(error);
    }

    // Shared by the modal actions and UI smoke tests; editing never mutates the input config.
    internal bool TrySetEnvironmentVariable(int index, string name, string value, out string error)
    {
        if (!EnvironmentVariableInputDialog.TryValidateEntry(name, value, out error)) return false;
        if (index < -1 || index >= _environmentVariables.Count)
        {
            error = "请选择要编辑的环境变量。";
            return false;
        }
        if (FindEnvironmentIndex(name, index) >= 0)
        {
            error = $"环境变量“{name}”已存在。";
            return false;
        }

        var item = new EnvironmentVariableRow { Name = name, Value = value };
        if (index == -1)
        {
            _environmentVariables.Add(item);
            index = _environmentVariables.Count - 1;
        }
        else
            _environmentVariables[index] = item;
        RefreshEnvironmentVariables(index);
        Reject(string.Empty);
        return true;
    }

    internal void DeleteEnvironmentVariable()
    {
        var index = _environmentList.SelectedRow;
        if (index < 0 || index >= _environmentVariables.Count) return;
        _environmentVariables.RemoveAt(index);
        RefreshEnvironmentVariables(Math.Min(index, _environmentVariables.Count - 1));
        Reject(string.Empty);
        if (_environmentList.Loaded) _environmentList.Focus();
    }

    private int FindEnvironmentIndex(string name, int exceptIndex = -1)
    {
        for (var index = 0; index < _environmentVariables.Count; index++)
        {
            if (index != exceptIndex
                && string.Equals(_environmentVariables[index].Name, name, StringComparison.Ordinal))
                return index;
        }
        return -1;
    }

    private void RefreshEnvironmentVariables(int selectedIndex = -1)
    {
        _environmentList.DataStore = _environmentVariables.ToList();
        var nextSelection = selectedIndex >= 0 && selectedIndex < _environmentVariables.Count ? selectedIndex : -1;
        if (_environmentList.SelectedRow != nextSelection)
            _environmentList.SelectedRow = nextSelection;
        if (_environmentList.Loaded && _environmentList.SelectedRow >= 0)
            _environmentList.ScrollToRow(_environmentList.SelectedRow);
        UpdateEnvironmentButtons();
    }

    private void UpdateEnvironmentButtons()
    {
        var selected = _environmentList.SelectedRow >= 0
            && _environmentList.SelectedRow < _environmentVariables.Count;
        _environmentEditButton.Enabled = selected;
        _environmentDeleteButton.Enabled = selected;
    }

    internal bool TryCollectEnvironmentVariables(out Dictionary<string, string> environment, out string error)
    {
        // Retain the config's ordinal key semantics and preserve values verbatim.
        environment = new Dictionary<string, string>(StringComparer.Ordinal);
        error = string.Empty;
        foreach (var item in _environmentVariables)
        {
            if (!EnvironmentVariableInputDialog.TryValidateEntry(item.Name, item.Value, out error)) return false;
            if (!environment.TryAdd(item.Name, item.Value))
            {
                error = $"存在重复的环境变量“{item.Name}”。";
                return false;
            }
        }
        return true;
    }

    private sealed class EnvironmentVariableRow
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}

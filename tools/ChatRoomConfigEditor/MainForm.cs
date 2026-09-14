using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ChatRoomConfigEditor;

public partial class MainForm : Form
{
    private ChatRoomConfig _config = ConfigStore.CreateDefault();
    private string _path = ConfigPaths.DefaultConfigPath();
    private DateTime? _loadedLastWriteUtc;
    private string _snapshot = string.Empty;
    private List<ValidationIssue> _issues = new();

    public MainForm()
    {
        BuildUi();
        WireEvents();
        LoadConfiguration(_path, createWhenMissing: false);
    }

    private void WireEvents()
    {
        _newButton.Click += (_, _) => CreateNewConfiguration();
        _openButton.Click += (_, _) => OpenConfiguration();
        _reloadButton.Click += (_, _) => ReloadConfiguration();
        _saveButton.Click += (_, _) => SaveConfiguration();
        _saveAsButton.Click += (_, _) => SaveConfigurationAs();
        _checkButton.Click += (_, _) => RunValidation(showStatus: true);
        _folderButton.Click += (_, _) => OpenContainingFolder();

        _copyTokenButton.Click += (_, _) => CopyOwnerToken();
        _regenerateTokenButton.Click += (_, _) => RegenerateOwnerToken();
        _clearTokenButton.Click += (_, _) => ClearOwnerToken();

        _addServerButton.Click += (_, _) => AddServer();
        _editServerButton.Click += (_, _) => EditServer();
        _removeServerButton.Click += (_, _) => RemoveServer();

        FormClosing += OnFormClosing;
    }

    // ---------------------------------------------------------------- loading

    private void LoadConfiguration(string path, bool createWhenMissing)
    {
        _path = path;
        _pathBox.Text = path;

        if (!File.Exists(path))
        {
            if (!createWhenMissing)
            {
                _config = ConfigStore.CreateDefault();
                _loadedLastWriteUtc = null;
                _rootsList.Items.Clear();
                foreach (var root in _config.AllowedRoots) _rootsList.Items.Add(root);
                LoadIntoUi();
                Snapshot();
                SetStatus($"配置文件不存在，已载入默认值：{path}（点击“保存”即会创建）");
                RunValidation(showStatus: false);
                return;
            }
        }

        try
        {
            var loaded = ConfigStore.Load(path);
            _config = loaded.Config;
            _loadedLastWriteUtc = loaded.LastWriteTimeUtc;
            LoadIntoUi();
            Snapshot();
            var unknown = loaded.UnknownKeys.Count == 0
                ? string.Empty
                : $"（另有 {loaded.UnknownKeys.Count} 个未知字段）";
            SetStatus($"已加载 {path}{unknown}");
            RunValidation(showStatus: false);
        }
        catch (Exception error)
        {
            MessageBox.Show(
                this,
                $"{error.Message}\n\n已改为显示默认值，未修改磁盘上的文件。",
                "读取失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _config = ConfigStore.CreateDefault();
            _loadedLastWriteUtc = null;
            LoadIntoUi();
            Snapshot();
            SetStatus("读取失败，已载入默认值");
            RunValidation(showStatus: false);
        }
    }

    private void LoadIntoUi()
    {
        _dataDirBox.Text = _config.DataDir;
        _databasePathBox.Text = _config.DatabasePath ?? string.Empty;

        _hostBox.Text = _config.Server.Host;
        _portBox.Value = Clamp(_config.Server.Port, _portBox.Minimum, _portBox.Maximum);
        _localWebAuthBox.Checked = _config.Auth.LocalWebAuth;
        _mcpPublicBaseUrlBox.Text = _config.Auth.McpPublicBaseUrl ?? string.Empty;
        _webPublicBaseUrlBox.Text = _config.Auth.WebPublicBaseUrl ?? string.Empty;

        _redirectHostsList.Items.Clear();
        foreach (var host in _config.Auth.AllowedRedirectHosts) _redirectHostsList.Items.Add(host);

        _httpDefaultTimeout.Value = Clamp(_config.Http.DefaultTimeoutMs, _httpDefaultTimeout.Minimum, _httpDefaultTimeout.Maximum);
        _httpMaxTimeout.Value = Clamp(_config.Http.MaxTimeoutMs, _httpMaxTimeout.Minimum, _httpMaxTimeout.Maximum);
        _httpMaxResponseBytes.Value = Clamp(_config.Http.MaxResponseBytes, _httpMaxResponseBytes.Minimum, _httpMaxResponseBytes.Maximum);
        _operationsMaxPayload.Value = Clamp(_config.Operations.MaxPayloadBytes, _operationsMaxPayload.Minimum, _operationsMaxPayload.Maximum);
        _processMaxOutput.Value = Clamp(_config.Process.MaxOutputBytes, _processMaxOutput.Minimum, _processMaxOutput.Maximum);
        _processDefaultTimeout.Value = Clamp(_config.Process.DefaultTimeoutMs, _processDefaultTimeout.Minimum, _processDefaultTimeout.Maximum);
        _processMaxCompleted.Value = Clamp(_config.Process.MaxCompletedProcesses, _processMaxCompleted.Minimum, _processMaxCompleted.Maximum);
        _mcpCallTimeout.Value = Clamp(_config.Mcp.CallTimeoutMs, _mcpCallTimeout.Minimum, _mcpCallTimeout.Maximum);
        _mcpMaxResultBytes.Value = Clamp(_config.Mcp.MaxResultBytes, _mcpMaxResultBytes.Minimum, _mcpMaxResultBytes.Maximum);

        _rootsList.Items.Clear();
        foreach (var root in _config.AllowedRoots) _rootsList.Items.Add(root);

        RefreshServerList();
        RefreshTokenStatus();
    }

    private void ReadFromUi()
    {
        _config.DataDir = _dataDirBox.Text.Trim();
        _config.DatabasePath = NullIfBlank(_databasePathBox.Text);

        _config.Server.Host = _hostBox.Text.Trim();
        _config.Server.Port = (int)_portBox.Value;
        _config.Auth.LocalWebAuth = _localWebAuthBox.Checked;
        _config.Auth.McpPublicBaseUrl = NullIfBlank(_mcpPublicBaseUrlBox.Text);
        _config.Auth.WebPublicBaseUrl = NullIfBlank(_webPublicBaseUrlBox.Text);

        _config.Auth.AllowedRedirectHosts = _redirectHostsList.Items
            .Cast<object>()
            .Select(item => item.ToString() ?? string.Empty)
            .ToList();

        _config.AllowedRoots = _rootsList.Items
            .Cast<object>()
            .Select(item => item.ToString() ?? string.Empty)
            .ToList();

        _config.Http.DefaultTimeoutMs = (int)_httpDefaultTimeout.Value;
        _config.Http.MaxTimeoutMs = (int)_httpMaxTimeout.Value;
        _config.Http.MaxResponseBytes = (int)_httpMaxResponseBytes.Value;
        _config.Operations.MaxPayloadBytes = (int)_operationsMaxPayload.Value;
        _config.Process.MaxOutputBytes = (int)_processMaxOutput.Value;
        _config.Process.DefaultTimeoutMs = (int)_processDefaultTimeout.Value;
        _config.Process.MaxCompletedProcesses = (int)_processMaxCompleted.Value;
        _config.Mcp.CallTimeoutMs = (int)_mcpCallTimeout.Value;
        _config.Mcp.MaxResultBytes = (int)_mcpMaxResultBytes.Value;
    }

    // ------------------------------------------------------------- owner token

    private void RefreshTokenStatus()
    {
        _tokenStatus.Text = OwnerToken.Describe(_config.Auth.OwnerToken);
        var present = OwnerToken.IsPresent(_config.Auth.OwnerToken);
        _copyTokenButton.Enabled = present;
        _clearTokenButton.Enabled = present;
        _tokenStatus.ForeColor = present ? SystemColors.ControlText : Color.Firebrick;
    }

    private void CopyOwnerToken()
    {
        var token = _config.Auth.OwnerToken;
        if (!OwnerToken.IsPresent(token)) return;

        try
        {
            Clipboard.SetText(token!);
            SetStatus("ownerToken 已复制到剪贴板（界面不会显示它的内容）");
        }
        catch (Exception error)
        {
            MessageBox.Show(
                this,
                $"复制到剪贴板失败：{error.Message}",
                "复制失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void RegenerateOwnerToken()
    {
        var answer = MessageBox.Show(
            this,
            "重新生成 ownerToken 会立即让所有已授权的客户端失效：\n\n" +
            "· ChatGPT 的自定义 MCP App 需要重新用新令牌完成 OAuth 授权\n" +
            "· 已登录的 WebUI 会话需要重新登录\n\n" +
            "新令牌不会显示在界面上，只能复制或重新生成。是否继续？",
            "重新生成 ownerToken",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        _config.Auth.OwnerToken = OwnerToken.Generate();
        RefreshTokenStatus();
        SetStatus("已生成新的 ownerToken；保存后生效，重启 ChatRoom 前旧令牌仍然有效");
    }

    private void ClearOwnerToken()
    {
        var answer = MessageBox.Show(
            this,
            "清除 ownerToken 后，任何需要认证的入口都无法启动（ChatRoom 会拒绝启动）。\n\n是否继续？",
            "清除 ownerToken",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        _config.Auth.OwnerToken = null;
        RefreshTokenStatus();
        SetStatus("已清除 ownerToken（若启用了认证入口，保存会被校验拦下）");
    }

    // ------------------------------------------------------------------- mcp

    private void RefreshServerList()
    {
        var selected = SelectedServerName();
        _serversView.BeginUpdate();
        _serversView.Items.Clear();
        foreach (var pair in _config.Mcp.Servers.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var name = pair.Key;
            var server = pair.Value;
            var item = new ListViewItem(name) { Tag = name };
            item.SubItems.Add(server.TransportType);
            item.SubItems.Add(server.Describe());
            _serversView.Items.Add(item);
            if (name == selected) item.Selected = true;
        }
        _serversView.EndUpdate();
        _editServerButton.Enabled = _serversView.SelectedItems.Count > 0;
        _removeServerButton.Enabled = _serversView.SelectedItems.Count > 0;
    }

    private string? SelectedServerName() =>
        _serversView.SelectedItems.Count > 0 ? _serversView.SelectedItems[0].Tag as string : null;

    private void AddServer()
    {
        // .NET Framework 4.8 has no Enumerable.ToHashSet().
        var existing = new HashSet<string>(_config.Mcp.Servers.Keys, StringComparer.Ordinal);
        using var dialog = new McpServerDialog(null, null, existing);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;

        _config.Mcp.Servers[dialog.ServerName] = dialog.Result;
        RefreshServerList();
        SelectServer(dialog.ServerName);
        SetStatus($"已添加 MCP 服务“{dialog.ServerName}”，保存后生效");
    }

    private void EditServer()
    {
        var name = SelectedServerName();
        if (name is null || !_config.Mcp.Servers.TryGetValue(name, out var server)) return;

        var existing = new HashSet<string>(
            _config.Mcp.Servers.Keys.Where(key => key != name),
            StringComparer.Ordinal);
        using var dialog = new McpServerDialog(name, server, existing);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;

        if (dialog.ServerName != name) _config.Mcp.Servers.Remove(name);
        _config.Mcp.Servers[dialog.ServerName] = dialog.Result;
        RefreshServerList();
        SelectServer(dialog.ServerName);
        SetStatus($"已更新 MCP 服务“{dialog.ServerName}”，保存后生效");
    }

    private void RemoveServer()
    {
        var name = SelectedServerName();
        if (name is null) return;

        var answer = MessageBox.Show(
            this,
            $"确定要删除 MCP 服务“{name}”吗？",
            "删除 MCP 服务",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        _config.Mcp.Servers.Remove(name);
        RefreshServerList();
        SetStatus($"已删除 MCP 服务“{name}”，保存后生效");
    }

    private void SelectServer(string name)
    {
        foreach (ListViewItem item in _serversView.Items)
        {
            if ((item.Tag as string) != name) continue;
            item.Selected = true;
            item.Focused = true;
            item.EnsureVisible();
            break;
        }
        _editServerButton.Enabled = _serversView.SelectedItems.Count > 0;
        _removeServerButton.Enabled = _serversView.SelectedItems.Count > 0;
    }

    // -------------------------------------------------------------- list edits

    private void AddRoot()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择工作区根目录", ShowNewFolderButton = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (_rootsList.Items.Contains(dialog.SelectedPath)) return;
        _rootsList.Items.Add(dialog.SelectedPath);
    }

    private void EditRoot()
    {
        if (_rootsList.SelectedIndex < 0) return;
        using var dialog = new FolderBrowserDialog { Description = "选择工作区根目录", ShowNewFolderButton = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _rootsList.Items[_rootsList.SelectedIndex] = dialog.SelectedPath;
    }

    private void AddRedirectHost() => PromptForValue("添加重定向主机", "主机名：", string.Empty, value =>
    {
        if (!_redirectHostsList.Items.Contains(value)) _redirectHostsList.Items.Add(value);
    });

    private void EditRedirectHost()
    {
        if (_redirectHostsList.SelectedIndex < 0) return;
        var current = _redirectHostsList.SelectedItem?.ToString() ?? string.Empty;
        PromptForValue("编辑重定向主机", "主机名：", current, value =>
            _redirectHostsList.Items[_redirectHostsList.SelectedIndex] = value);
    }

    private void RemoveSelected(ListBox list, string label)
    {
        if (list.SelectedIndex < 0) return;
        var value = list.SelectedItem?.ToString() ?? string.Empty;
        var answer = MessageBox.Show(
            this,
            $"确定要删除{label}“{value}”吗？",
            $"删除{label}",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes) list.Items.RemoveAt(list.SelectedIndex);
    }

    private void PromptForValue(string title, string label, string initial, Action<string> apply)
    {
        using var dialog = new TextInputDialog(title, label, initial);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var value = dialog.Value.Trim();
        if (value.Length == 0) return;
        apply(value);
    }

    private void BrowseInto(TextBox target, bool saveFile)
    {
        if (saveFile)
        {
            using var dialog = new SaveFileDialog
            {
                Title = "选择数据库文件",
                Filter = "SQLite 数据库|*.sqlite;*.db|所有文件|*.*",
                FileName = Path.GetFileName(target.Text),
                InitialDirectory = SafeDirectory(target.Text),
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.FileName;
            return;
        }

        using var browser = new FolderBrowserDialog
        {
            Description = "选择目录",
            ShowNewFolderButton = true,
            SelectedPath = SafeDirectory(target.Text),
        };
        if (browser.ShowDialog(this) == DialogResult.OK) target.Text = browser.SelectedPath;
    }

    // ------------------------------------------------------------ file actions

    private void CreateNewConfiguration()
    {
        if (!ConfirmDiscardChanges("新建默认配置")) return;

        _config = ConfigStore.CreateDefault();
        _loadedLastWriteUtc = null;
        LoadIntoUi();
        _path = ConfigPaths.DefaultConfigPath();
        _pathBox.Text = _path;
        Snapshot();
        _issues = ConfigValidator.Validate(_config, Array.Empty<string>());
        RenderIssues();
        SetStatus("已载入默认配置；ownerToken 为空，需要生成或填写后才能保存");
    }

    private void OpenConfiguration()
    {
        if (!ConfirmDiscardChanges("打开其他配置文件")) return;

        using var dialog = new OpenFileDialog
        {
            Title = "打开 ChatRoom 配置文件",
            Filter = "JSON 配置|*.json|所有文件|*.*",
            InitialDirectory = SafeDirectory(_path),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        LoadConfiguration(dialog.FileName, createWhenMissing: false);
    }

    private void ReloadConfiguration()
    {
        if (!ConfirmDiscardChanges("重新加载")) return;
        LoadConfiguration(_path, createWhenMissing: false);
    }

    private void SaveConfiguration()
    {
        if (!ApplyUiToConfig()) return;

        try
        {
            var backup = ConfigStore.Save(_path, _config, _loadedLastWriteUtc);
            _loadedLastWriteUtc = File.GetLastWriteTimeUtc(_path);
            Snapshot();
            var message = backup is null
                ? $"已保存 {_path}\n\n请重启 ChatRoom 使其生效。"
                : $"已保存 {_path}\n备份：{backup}\n\n请重启 ChatRoom 使其生效。";
            SetStatus("已保存");
            MessageBox.Show(this, message, "保存成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (ConfigChangedOnDiskException error)
        {
            var answer = MessageBox.Show(
                this,
                $"{error.Message}\n\n是否重新加载磁盘上的内容？",
                "文件已被修改",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes) LoadConfiguration(_path, createWhenMissing: false);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveConfigurationAs()
    {
        if (!ApplyUiToConfig()) return;

        using var dialog = new SaveFileDialog
        {
            Title = "另存为",
            Filter = "JSON 配置|*.json|所有文件|*.*",
            FileName = Path.GetFileName(_path),
            InitialDirectory = SafeDirectory(_path),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            ConfigStore.Save(dialog.FileName, _config);
            _path = dialog.FileName;
            _pathBox.Text = _path;
            _loadedLastWriteUtc = File.GetLastWriteTimeUtc(_path);
            Snapshot();
            SetStatus($"已另存为 {_path}");
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Reads the form, validates, and blocks on errors. Returns false to abort.</summary>
    private bool ApplyUiToConfig()
    {
        ReadFromUi();
        _issues = ConfigValidator.Validate(_config, Array.Empty<string>());
        RenderIssues();

        var errors = _issues.Count(issue => issue.Severity == IssueSeverity.Error);
        if (errors > 0)
        {
            MessageBox.Show(
                this,
                $"存在 {errors} 项错误，已阻止保存。\n\nChatRoom 会拒绝启动这样的配置文件，请在下方“校验结果”中逐项修正。",
                "校验未通过",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        var warnings = _issues.Count;
        if (warnings > 0)
        {
            var answer = MessageBox.Show(
                this,
                $"有 {warnings} 条提醒（不影响 ChatRoom 启动），是否继续保存？",
                "确认保存",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return false;
        }

        return true;
    }

    private void RunValidation(bool showStatus)
    {
        ReadFromUi();
        _issues = ConfigValidator.Validate(_config, Array.Empty<string>());
        RenderIssues();

        var errors = _issues.Count(issue => issue.Severity == IssueSeverity.Error);
        var warnings = _issues.Count - errors;
        if (showStatus)
        {
            MessageBox.Show(
                this,
                errors == 0 && warnings == 0
                    ? "校验通过：配置与 ChatRoom 的校验规则一致。"
                    : $"错误 {errors} 项，警告 {warnings} 项，详见下方“校验结果”。",
                "校验结果",
                MessageBoxButtons.OK,
                errors > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        SetStatus(errors == 0 ? $"校验通过（警告 {warnings} 项）" : $"校验未通过：{errors} 项错误");
    }

    private void RenderIssues()
    {
        _issuesView.BeginUpdate();
        _issuesView.Items.Clear();
        foreach (var issue in _issues.OrderBy(issue => issue.Severity))
        {
            var item = new ListViewItem(issue.Severity == IssueSeverity.Error ? "错误" : "警告");
            item.SubItems.Add(issue.Path);
            item.SubItems.Add(issue.Message);
            item.ForeColor = issue.Severity == IssueSeverity.Error ? Color.Firebrick : Color.DarkGoldenrod;
            _issuesView.Items.Add(item);
        }
        _issuesView.EndUpdate();
    }

    private void OpenContainingFolder()
    {
        var directory = Path.GetDirectoryName(_path);
        if (directory is null || !Directory.Exists(directory)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "无法打开目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!IsDirty()) return;
        var answer = MessageBox.Show(
            this,
            "有未保存的修改，确定要放弃并退出吗？",
            "未保存的修改",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) e.Cancel = true;
    }

    // ----------------------------------------------------------------- helpers

    private bool ConfirmDiscardChanges(string action)
    {
        if (!IsDirty()) return true;
        var answer = MessageBox.Show(
            this,
            $"当前有未保存的修改，继续{action}将丢弃它们。是否继续？",
            "未保存的修改",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return answer == DialogResult.Yes;
    }

    private bool IsDirty()
    {
        try
        {
            ReadFromUi();
            return ConfigStore.Serialize(_config) != _snapshot;
        }
        catch
        {
            return false;
        }
    }

    private void Snapshot()
    {
        ReadFromUi();
        _snapshot = ConfigStore.Serialize(_config);
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var name = Path.GetFileName(_path);
        Text = $"{name} — ChatRoom 配置编辑器";
    }

    private void SetStatus(string message) => _statusLabel.Text = message;

    private static decimal Clamp(int value, decimal min, decimal max) =>
        Math.Min(Math.Max(value, min), max);

    private static string? NullIfBlank(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string SafeDirectory(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            return directory is not null && Directory.Exists(directory)
                ? directory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ChatRoomTray.ConfigEditor;

public partial class MainForm : Form
{
    private ChatRoomConfig _config = ConfigStore.CreateDefault();
    private string _path = ConfigPaths.DefaultConfigPath();
    private DateTime? _loadedLastWriteUtc;
    private string _snapshot = string.Empty;
    private List<ValidationIssue> _issues = new();
    private string? _ownerToken;

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
                _ownerToken = OwnerTokenStore.Read(path);
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
            _ownerToken = LoadAndMigrateOwnerToken(path, loaded);
            _loadedLastWriteUtc = File.GetLastWriteTimeUtc(path);
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
            _ownerToken = null;
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

    private static string? LoadAndMigrateOwnerToken(string path, ConfigLoadResult loaded)
    {
        var stored = OwnerTokenStore.Read(path);
        if (!loaded.HasLegacyOwnerToken) return stored;

        var legacy = loaded.LegacyOwnerToken;
        if (OwnerToken.IsPresent(legacy))
        {
            if (OwnerToken.IsPresent(stored) && !string.Equals(stored, legacy, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "config.json 中的旧 ownerToken 与 Windows 凭据管理器中的 ownerToken 不一致；为避免覆盖凭据，迁移已停止。");
            if (!OwnerToken.IsPresent(stored))
            {
                OwnerTokenStore.Write(path, legacy!);
                stored = OwnerTokenStore.Read(path);
                if (!string.Equals(stored, legacy, StringComparison.Ordinal))
                    throw new InvalidOperationException("ownerToken 写入 Windows 凭据管理器后校验失败。");
            }
        }

        ConfigStore.RemoveLegacyOwnerToken(path, loaded.LastWriteTimeUtc);
        return stored;
    }

    private void RefreshTokenStatus()
    {
        _tokenStatus.Text = OwnerToken.Describe(_ownerToken);
        var present = OwnerToken.IsPresent(_ownerToken);
        _copyTokenButton.Enabled = present;
        _clearTokenButton.Enabled = present;
        _tokenStatus.ForeColor = present ? SystemColors.ControlText : Color.Firebrick;
    }

    private void CopyOwnerToken()
    {
        try
        {
            _ownerToken = OwnerTokenStore.Read(_path);
            if (!OwnerToken.IsPresent(_ownerToken))
            {
                RefreshTokenStatus();
                return;
            }
            Clipboard.SetText(_ownerToken!);
            RefreshTokenStatus();
            SetStatus("ownerToken 已从 Windows 凭据管理器复制到剪贴板（界面不会显示它的内容）");
        }
        catch (Exception error)
        {
            MessageBox.Show(
                this,
                $"复制 ownerToken 失败：{error.Message}",
                "复制失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void RegenerateOwnerToken()
    {
        var answer = MessageBox.Show(
            this,
            "重新生成 ownerToken 会更换系统凭据中的所有者令牌：\n\n" +
            "· ChatGPT 的自定义 MCP App 需要重新用新令牌完成 OAuth 授权\n" +
            "· ChatRoom 重启后，旧 ownerToken 将不再可用于新的 WebUI 登录或 OAuth 授权\n" +
            "· 当前已经签发的 OAuth access/refresh token 和 WebUI session 不会因为本操作被数据库级联撤销\n\n" +
            "新令牌不会显示在界面上，只能复制或重新生成。是否继续？",
            "重新生成 ownerToken",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        try
        {
            var token = OwnerToken.Generate();
            OwnerTokenStore.Write(_path, token);
            _ownerToken = OwnerTokenStore.Read(_path);
            if (!string.Equals(_ownerToken, token, StringComparison.Ordinal))
                throw new InvalidOperationException("新 ownerToken 写入后校验失败。");
            RefreshTokenStatus();
            SetStatus("已重新生成 ownerToken 并写入 Windows 凭据管理器；重启 ChatRoom 后使用新令牌");
        }
        catch (Exception error)
        {
            MessageBox.Show(
                this,
                $"重新生成 ownerToken 失败：{error.Message}",
                "生成失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ClearOwnerToken()
    {
        ReadFromUi();
        if (IsDirty())
        {
            MessageBox.Show(
                this,
                "清除 ownerToken 会立即修改 Windows 凭据管理器。请先保存当前配置修改，再执行清除。",
                "请先保存配置",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        if (RequiresOwnerToken(_config))
        {
            MessageBox.Show(
                this,
                "当前配置启用了需要认证的入口。请先关闭 localWebAuth / 公网地址并保存配置，再清除 ownerToken。",
                "不能清除 ownerToken",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var answer = MessageBox.Show(
            this,
            "这会立即从 Windows 凭据管理器删除当前配置对应的 ownerToken。\n\n是否继续？",
            "清除 ownerToken",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        try
        {
            OwnerTokenStore.Delete(_path);
            _ownerToken = null;
            RefreshTokenStatus();
            SetStatus("已从 Windows 凭据管理器清除 ownerToken");
        }
        catch (Exception error)
        {
            MessageBox.Show(
                this,
                $"清除 ownerToken 失败：{error.Message}",
                "清除失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static bool RequiresOwnerToken(ChatRoomConfig config) =>
        config.Auth.LocalWebAuth
        || !string.IsNullOrEmpty(config.Auth.McpPublicBaseUrl)
        || !string.IsNullOrEmpty(config.Auth.WebPublicBaseUrl);

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
        _path = ConfigPaths.DefaultConfigPath();
        _pathBox.Text = _path;
        _ownerToken = OwnerTokenStore.Read(_path);
        _loadedLastWriteUtc = null;
        LoadIntoUi();
        Snapshot();
        _issues = ConfigValidator.Validate(
            _config,
            Array.Empty<string>(),
            OwnerToken.IsPresent(_ownerToken));
        RenderIssues();
        SetStatus(OwnerToken.IsPresent(_ownerToken)
            ? "已载入默认配置；ownerToken 已存在于 Windows 凭据管理器"
            : "已载入默认配置；ownerToken 尚未生成");
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

        string? targetPath = null;
        var rollbackTargetToken = false;
        try
        {
            targetPath = Path.GetFullPath(dialog.FileName);
            _ownerToken = OwnerTokenStore.Read(_path);
            var targetToken = OwnerTokenStore.Read(targetPath);
            if (OwnerToken.IsPresent(_ownerToken))
            {
                if (OwnerToken.IsPresent(targetToken)
                    && !string.Equals(targetToken, _ownerToken, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "目标配置路径已经绑定了不同的 ownerToken。为避免覆盖系统凭据，请选择其他文件名，或先打开目标配置后处理其令牌。");
                if (!OwnerToken.IsPresent(targetToken))
                {
                    OwnerTokenStore.Write(targetPath, _ownerToken!);
                    rollbackTargetToken = true;
                    targetToken = OwnerTokenStore.Read(targetPath);
                    if (!string.Equals(targetToken, _ownerToken, StringComparison.Ordinal))
                        throw new InvalidOperationException("另存为目标的 ownerToken 写入后校验失败。");
                }
            }

            ConfigStore.Save(targetPath, _config);
            rollbackTargetToken = false;
            _path = targetPath;
            _pathBox.Text = _path;
            _ownerToken = targetToken;
            _loadedLastWriteUtc = File.GetLastWriteTimeUtc(_path);
            RefreshTokenStatus();
            Snapshot();
            SetStatus($"已另存为 {_path}；ownerToken 由 Windows 凭据管理器按配置路径管理");
        }
        catch (Exception error)
        {
            if (rollbackTargetToken && targetPath is not null)
            {
                try { OwnerTokenStore.Delete(targetPath); }
                catch { /* Preserve the original save error; the target credential can be cleaned manually. */ }
            }
            MessageBox.Show(this, error.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Reads the form, validates, and blocks on errors. Returns false to abort.</summary>
    private bool ApplyUiToConfig()
    {
        ReadFromUi();
        _issues = ValidateCurrentConfig();
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

    private List<ValidationIssue> ValidateCurrentConfig()
    {
        var requiresToken = RequiresOwnerToken(_config);
        var tokenPresent = false;
        string? credentialError = null;
        try
        {
            _ownerToken = OwnerTokenStore.Read(_path);
            tokenPresent = OwnerToken.IsPresent(_ownerToken);
            RefreshTokenStatus();
        }
        catch (Exception error)
        {
            credentialError = error.Message;
        }

        // If the store itself cannot be read, replace the generic "missing token"
        // error with a more precise credential-store error. For a local-only
        // configuration this remains a warning, matching ChatRoom runtime behavior.
        var tokenPresentForRules = credentialError is not null && requiresToken
            ? true
            : tokenPresent;
        var issues = ConfigValidator.Validate(
            _config,
            Array.Empty<string>(),
            tokenPresentForRules);
        if (credentialError is not null)
        {
            issues.Add(new ValidationIssue(
                requiresToken ? IssueSeverity.Error : IssueSeverity.Warning,
                "ownerToken",
                requiresToken
                    ? $"无法读取 Windows 凭据管理器，而当前配置需要 ownerToken：{credentialError}"
                    : $"暂时无法读取 Windows 凭据管理器；当前纯本地配置不依赖 ownerToken：{credentialError}"));
        }
        return issues;
    }

    private void RunValidation(bool showStatus)
    {
        ReadFromUi();
        _issues = ValidateCurrentConfig();
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

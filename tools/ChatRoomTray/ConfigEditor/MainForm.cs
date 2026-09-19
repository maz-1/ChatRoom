using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;

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

        Closing += OnFormClosing;
    }

    // ---------------------------------------------------------------- loading

    private void LoadConfiguration(string path, bool createWhenMissing)
    {
        _path = path;
        _pathBox.Text = path;

        if (!File.Exists(path) && !createWhenMissing)
        {
            _config = ConfigStore.CreateDefault();
            try
            {
                _ownerToken = OwnerTokenStore.Read(path);
            }
            catch
            {
                _ownerToken = null;
            }

            _loadedLastWriteUtc = null;
            LoadIntoUi();
            Snapshot();
            SetStatus($"配置文件不存在，已载入默认值：{path}（点击“保存”即会创建）");
            RunValidation(showStatus: false);
            return;
        }

        try
        {
            var loaded = ConfigStore.Load(path);
            _config = loaded.Config;
            string? credentialWarning = null;
            try
            {
                _ownerToken = LoadAndMigrateOwnerToken(path, loaded);
            }
            catch (OwnerTokenStoreException error)
            {
                // Match ChatRoom runtime behavior: a temporary keychain/Secret
                // Service failure must not hide an otherwise valid local config.
                _ownerToken = null;
                credentialWarning = error.Message;
            }

            _loadedLastWriteUtc = File.GetLastWriteTimeUtc(path);
            LoadIntoUi();
            Snapshot();
            var unknown = loaded.UnknownKeys.Count == 0
                ? string.Empty
                : $"（另有 {loaded.UnknownKeys.Count} 个未知字段）";
            SetStatus(credentialWarning is null
                ? $"已加载 {path}{unknown}"
                : $"已加载 {path}{unknown}；系统凭据库暂不可用：{credentialWarning}");
            RunValidation(showStatus: false);
        }
        catch (Exception error)
        {
            MessageBox.Show(
                this,
                $"{error.Message}\n\n已改为显示默认值，未修改磁盘上的文件。",
                "读取失败",
                MessageBoxButtons.OK,
                MessageBoxType.Warning);
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
        _portBox.Value = Clamp(_config.Server.Port, _portBox.MinValue, _portBox.MaxValue);
        _localWebAuthBox.Checked = _config.Auth.LocalWebAuth;
        _mcpPublicBaseUrlBox.Text = _config.Auth.McpPublicBaseUrl ?? string.Empty;
        _webPublicBaseUrlBox.Text = _config.Auth.WebPublicBaseUrl ?? string.Empty;

        SetListValues(_redirectHostsList, _config.Auth.AllowedRedirectHosts);

        _httpDefaultTimeout.Value = Clamp(_config.Http.DefaultTimeoutMs, _httpDefaultTimeout.MinValue, _httpDefaultTimeout.MaxValue);
        _httpMaxTimeout.Value = Clamp(_config.Http.MaxTimeoutMs, _httpMaxTimeout.MinValue, _httpMaxTimeout.MaxValue);
        _httpMaxResponseBytes.Value = Clamp(_config.Http.MaxResponseBytes, _httpMaxResponseBytes.MinValue, _httpMaxResponseBytes.MaxValue);
        _operationsMaxPayload.Value = Clamp(_config.Operations.MaxPayloadBytes, _operationsMaxPayload.MinValue, _operationsMaxPayload.MaxValue);
        _processMaxOutput.Value = Clamp(_config.Process.MaxOutputBytes, _processMaxOutput.MinValue, _processMaxOutput.MaxValue);
        _processDefaultTimeout.Value = Clamp(_config.Process.DefaultTimeoutMs, _processDefaultTimeout.MinValue, _processDefaultTimeout.MaxValue);
        _processMaxCompleted.Value = Clamp(_config.Process.MaxCompletedProcesses, _processMaxCompleted.MinValue, _processMaxCompleted.MaxValue);
        _mcpCallTimeout.Value = Clamp(_config.Mcp.CallTimeoutMs, _mcpCallTimeout.MinValue, _mcpCallTimeout.MaxValue);
        _mcpMaxResultBytes.Value = Clamp(_config.Mcp.MaxResultBytes, _mcpMaxResultBytes.MinValue, _mcpMaxResultBytes.MaxValue);

        SetListValues(_rootsList, _config.AllowedRoots);

        RefreshServerList();
        RefreshTokenStatus();
    }

    private void ReadFromUi()
    {
        _config.DataDir = (_dataDirBox.Text ?? string.Empty).Trim();
        _config.DatabasePath = NullIfBlank(_databasePathBox.Text ?? string.Empty);

        _config.Server.Host = (_hostBox.Text ?? string.Empty).Trim();
        _config.Server.Port = (int)_portBox.Value;
        _config.Auth.LocalWebAuth = _localWebAuthBox.Checked == true;
        _config.Auth.McpPublicBaseUrl = NullIfBlank(_mcpPublicBaseUrlBox.Text ?? string.Empty);
        _config.Auth.WebPublicBaseUrl = NullIfBlank(_webPublicBaseUrlBox.Text ?? string.Empty);

        _config.Auth.AllowedRedirectHosts = ListValues(_redirectHostsList);
        _config.AllowedRoots = ListValues(_rootsList);

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
                    "config.json 中的旧 ownerToken 与系统凭据库中的 ownerToken 不一致；为避免覆盖凭据，迁移已停止。");
            if (!OwnerToken.IsPresent(stored))
            {
                OwnerTokenStore.Write(path, legacy!);
                stored = OwnerTokenStore.Read(path);
                if (!string.Equals(stored, legacy, StringComparison.Ordinal))
                    throw new InvalidOperationException("ownerToken 写入系统凭据库后校验失败。");
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
        _tokenStatus.TextColor = present ? SystemColors.ControlText : Colors.Firebrick;
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

            Clipboard.Instance.Text = _ownerToken!;
            RefreshTokenStatus();
            SetStatus("ownerToken 已从系统凭据库复制到剪贴板（界面不会显示它的内容）");
        }
        catch (Exception error)
        {
            MessageBox.Show(
                this,
                $"复制 ownerToken 失败：{error.Message}",
                "复制失败",
                MessageBoxButtons.OK,
                MessageBoxType.Warning);
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
            MessageBoxType.Warning,
            MessageBoxDefaultButton.No);
        if (answer != DialogResult.Yes) return;

        try
        {
            var token = OwnerToken.Generate();
            OwnerTokenStore.Write(_path, token);
            _ownerToken = OwnerTokenStore.Read(_path);
            if (!string.Equals(_ownerToken, token, StringComparison.Ordinal))
                throw new InvalidOperationException("新 ownerToken 写入后校验失败。");
            RefreshTokenStatus();
            SetStatus("已重新生成 ownerToken 并写入系统凭据库；重启 ChatRoom 后使用新令牌");
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"重新生成 ownerToken 失败：{error.Message}", "生成失败",
                MessageBoxButtons.OK, MessageBoxType.Warning);
        }
    }

    private void ClearOwnerToken()
    {
        ReadFromUi();
        if (IsDirty())
        {
            MessageBox.Show(this,
                "清除 ownerToken 会立即修改系统凭据库。请先保存当前配置修改，再执行清除。",
                "请先保存配置", MessageBoxButtons.OK, MessageBoxType.Warning);
            return;
        }

        if (RequiresOwnerToken(_config))
        {
            MessageBox.Show(this,
                "当前配置启用了需要认证的入口。请先关闭 localWebAuth / 公网地址并保存配置，再清除 ownerToken。",
                "不能清除 ownerToken", MessageBoxButtons.OK, MessageBoxType.Warning);
            return;
        }

        var answer = MessageBox.Show(
            this,
            "这会立即从系统凭据库删除当前配置对应的 ownerToken。\n\n是否继续？",
            "清除 ownerToken",
            MessageBoxButtons.YesNo,
            MessageBoxType.Warning,
            MessageBoxDefaultButton.No);
        if (answer != DialogResult.Yes) return;

        try
        {
            OwnerTokenStore.Delete(_path);
            _ownerToken = null;
            RefreshTokenStatus();
            SetStatus("已从系统凭据库清除 ownerToken");
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"清除 ownerToken 失败：{error.Message}", "清除失败",
                MessageBoxButtons.OK, MessageBoxType.Warning);
        }
    }

    private static bool RequiresOwnerToken(ChatRoomConfig config) =>
        ConfigValidator.AuthenticationUsesOwnerToken(config);

    // ------------------------------------------------------------------- mcp

    private void RefreshServerList()
    {
        var selected = SelectedServerName();
        var rows = _config.Mcp.Servers
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(pair => new ServerGridRow
            {
                Name = pair.Key,
                Transport = pair.Value.TransportType,
                Target = pair.Value.Describe(),
            })
            .ToList();

        _serversView.DataStore = rows;
        if (selected is not null)
        {
            var index = rows.FindIndex(row => row.Name == selected);
            if (index >= 0) _serversView.SelectedRow = index;
        }

        UpdateServerButtons();
    }

    private string? SelectedServerName() =>
        (_serversView.SelectedItem as ServerGridRow)?.Name;

    private void UpdateServerButtons()
    {
        var selected = _serversView.SelectedItem is ServerGridRow;
        _editServerButton.Enabled = selected;
        _removeServerButton.Enabled = selected;
    }

    private void AddServer()
    {
        var existing = new HashSet<string>(_config.Mcp.Servers.Keys, StringComparer.Ordinal);
        using var dialog = new McpServerDialog(null, null, existing);
        if (!dialog.ShowModal(this) || dialog.ServerConfig is null) return;

        _config.Mcp.Servers[dialog.ServerName] = dialog.ServerConfig;
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
        if (!dialog.ShowModal(this) || dialog.ServerConfig is null) return;

        if (dialog.ServerName != name) _config.Mcp.Servers.Remove(name);
        _config.Mcp.Servers[dialog.ServerName] = dialog.ServerConfig;
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
            MessageBoxType.Question,
            MessageBoxDefaultButton.No);
        if (answer != DialogResult.Yes) return;

        _config.Mcp.Servers.Remove(name);
        RefreshServerList();
        SetStatus($"已删除 MCP 服务“{name}”，保存后生效");
    }

    private void SelectServer(string name)
    {
        var rows = (_serversView.DataStore ?? Enumerable.Empty<object>())
            .OfType<ServerGridRow>()
            .ToList();
        var index = rows.FindIndex(row => row.Name == name);
        if (index >= 0)
        {
            _serversView.SelectedRow = index;
            _serversView.ScrollToRow(index);
        }

        UpdateServerButtons();
    }

    // -------------------------------------------------------------- list edits

    private void AddRoot()
    {
        using var dialog = new SelectFolderDialog { Title = "选择工作区根目录" };
        if (dialog.ShowDialog(this) != DialogResult.Ok) return;
        var values = ListValues(_rootsList);
        if (values.Contains(dialog.Directory, StringComparer.Ordinal)) return;
        values.Add(dialog.Directory);
        SetListValues(_rootsList, values, values.Count - 1);
    }

    private void EditRoot()
    {
        if (_rootsList.SelectedIndex < 0) return;
        var index = _rootsList.SelectedIndex;
        var values = ListValues(_rootsList);
        using var dialog = new SelectFolderDialog
        {
            Title = "选择工作区根目录",
            Directory = values[index],
        };
        if (dialog.ShowDialog(this) != DialogResult.Ok) return;
        values[index] = dialog.Directory;
        SetListValues(_rootsList, values, index);
    }

    private void AddRedirectHost() => PromptForValue("添加重定向主机", "主机名：", string.Empty, value =>
    {
        var values = ListValues(_redirectHostsList);
        if (values.Contains(value, StringComparer.Ordinal)) return;
        values.Add(value);
        SetListValues(_redirectHostsList, values, values.Count - 1);
    });

    private void EditRedirectHost()
    {
        if (_redirectHostsList.SelectedIndex < 0) return;
        var index = _redirectHostsList.SelectedIndex;
        var values = ListValues(_redirectHostsList);
        var current = values[index];
        PromptForValue("编辑重定向主机", "主机名：", current, value =>
        {
            values[index] = value;
            SetListValues(_redirectHostsList, values, index);
        });
    }

    private void RemoveSelected(ListBox list, string label)
    {
        if (list.SelectedIndex < 0) return;
        var index = list.SelectedIndex;
        var values = ListValues(list);
        if (index >= values.Count) return;
        var value = values[index];
        var answer = MessageBox.Show(
            this,
            $"确定要删除{label}“{value}”吗？",
            $"删除{label}",
            MessageBoxButtons.YesNo,
            MessageBoxType.Question,
            MessageBoxDefaultButton.No);
        if (answer != DialogResult.Yes) return;

        values.RemoveAt(index);
        SetListValues(list, values, values.Count == 0 ? -1 : Math.Min(index, values.Count - 1));
    }

    private void PromptForValue(string title, string label, string initial, Action<string> apply)
    {
        using var dialog = new TextInputDialog(title, label, initial);
        if (!dialog.ShowModal(this)) return;
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
                FileName = Path.GetFileName(target.Text ?? string.Empty),
                Directory = ToDirectoryUri(SafeDirectory(target.Text ?? string.Empty)),
            };
            dialog.Filters.Add(new FileFilter("SQLite 数据库", ".sqlite", ".db"));
            dialog.Filters.Add(new FileFilter("所有文件", ".*"));
            if (dialog.ShowDialog(this) == DialogResult.Ok)
                target.Text = dialog.FileName;
            return;
        }

        using var browser = new SelectFolderDialog
        {
            Title = "选择目录",
            Directory = SafeDirectory(target.Text ?? string.Empty),
        };
        if (browser.ShowDialog(this) == DialogResult.Ok)
            target.Text = browser.Directory;
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
            ? "已载入默认配置；ownerToken 已存在于系统凭据库"
            : "已载入默认配置；ownerToken 尚未生成");
    }

    private void OpenConfiguration()
    {
        if (!ConfirmDiscardChanges("打开其他配置文件")) return;

        using var dialog = new OpenFileDialog
        {
            Title = "打开 ChatRoom 配置文件",
            Directory = ToDirectoryUri(SafeDirectory(_path)),
        };
        dialog.Filters.Add(new FileFilter("JSON 配置", ".json"));
        dialog.Filters.Add(new FileFilter("所有文件", ".*"));
        if (dialog.ShowDialog(this) != DialogResult.Ok) return;
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
            MessageBox.Show(this, message, "保存成功", MessageBoxButtons.OK, MessageBoxType.Information);
        }
        catch (ConfigChangedOnDiskException error)
        {
            var answer = MessageBox.Show(
                this,
                $"{error.Message}\n\n是否重新加载磁盘上的内容？",
                "文件已被修改",
                MessageBoxButtons.YesNo,
                MessageBoxType.Warning,
                MessageBoxDefaultButton.No);
            if (answer == DialogResult.Yes) LoadConfiguration(_path, createWhenMissing: false);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "保存失败", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    private void SaveConfigurationAs()
    {
        if (!ApplyUiToConfig()) return;

        using var dialog = new SaveFileDialog
        {
            Title = "另存为",
            FileName = Path.GetFileName(_path),
            Directory = ToDirectoryUri(SafeDirectory(_path)),
        };
        dialog.Filters.Add(new FileFilter("JSON 配置", ".json"));
        dialog.Filters.Add(new FileFilter("所有文件", ".*"));
        if (dialog.ShowDialog(this) != DialogResult.Ok) return;

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
            SetStatus($"已另存为 {_path}；ownerToken 由系统凭据库按配置路径管理");
        }
        catch (Exception error)
        {
            if (rollbackTargetToken && targetPath is not null)
            {
                try { OwnerTokenStore.Delete(targetPath); }
                catch { }
            }

            MessageBox.Show(this, error.Message, "保存失败", MessageBoxButtons.OK, MessageBoxType.Error);
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
                MessageBoxType.Error);
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
                MessageBoxType.Question,
                MessageBoxDefaultButton.No);
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
                    ? $"无法读取系统凭据库，而当前配置需要 ownerToken：{credentialError}"
                    : $"暂时无法读取系统凭据库；当前纯本地配置不依赖 ownerToken：{credentialError}"));
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
                errors > 0 ? MessageBoxType.Warning : MessageBoxType.Information);
        }

        SetStatus(errors == 0 ? $"校验通过（警告 {warnings} 项）" : $"校验未通过：{errors} 项错误");
    }

    private void RenderIssues()
    {
        _issuesView.DataStore = _issues
            .OrderBy(issue => issue.Severity)
            .Select(issue => new IssueGridRow
            {
                Severity = issue.Severity == IssueSeverity.Error ? "错误" : "警告",
                Path = issue.Path,
                Message = issue.Message,
                IsError = issue.Severity == IssueSeverity.Error,
            })
            .ToList();
    }

    private void OpenContainingFolder()
    {
        var directory = Path.GetDirectoryName(_path);
        if (directory is null || !Directory.Exists(directory)) return;
        try
        {
            PlatformShell.OpenDirectory(directory);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "无法打开目录", MessageBoxButtons.OK, MessageBoxType.Warning);
        }
    }

    private void OnFormClosing(object? sender, CancelEventArgs e)
    {
        if (!IsDirty()) return;
        var answer = MessageBox.Show(
            this,
            "有未保存的修改，确定要放弃并退出吗？",
            "未保存的修改",
            MessageBoxButtons.YesNo,
            MessageBoxType.Warning,
            MessageBoxDefaultButton.No);
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
            MessageBoxType.Warning,
            MessageBoxDefaultButton.No);
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
        Title = $"{name} — ChatRoom 配置编辑器";
    }

    private void SetStatus(string message) => _statusLabel.Text = message;

    private static double Clamp(int value, double min, double max) =>
        Math.Min(Math.Max(value, min), max);

    private static string? NullIfBlank(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static List<string> ListValues(ListBox list) =>
        (list.DataStore ?? Enumerable.Empty<object>())
        .Select(item => item?.ToString() ?? string.Empty)
        .ToList();

    private static void SetListValues(ListBox list, IEnumerable<string> values, int selectedIndex = -1)
    {
        var data = values.ToList();
        list.DataStore = data;
        list.SelectedIndex = selectedIndex >= 0 && selectedIndex < data.Count ? selectedIndex : -1;
    }

    private static string SafeDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) return path;
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

    private static Uri ToDirectoryUri(string directory) =>
        new(Path.GetFullPath(directory) + Path.DirectorySeparatorChar);
}

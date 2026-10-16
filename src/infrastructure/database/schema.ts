export const DATABASE_SCHEMA_VERSION = 7;

export const DATABASE_SCHEMA = `
  CREATE TABLE IF NOT EXISTS workspace_blacklist (
    path_key TEXT PRIMARY KEY,
    root TEXT NOT NULL
  );

  CREATE TABLE IF NOT EXISTS operations (
    operation_id TEXT PRIMARY KEY,
    plugin_id TEXT NOT NULL,
    source TEXT NOT NULL,
    action TEXT NOT NULL,
    status TEXT NOT NULL,
    process_id TEXT,
    input_json TEXT NOT NULL,
    output_json TEXT NOT NULL,
    error_json TEXT NOT NULL,
    input_truncated INTEGER NOT NULL DEFAULT 0,
    output_truncated INTEGER NOT NULL DEFAULT 0,
    started_at TEXT NOT NULL,
    finished_at TEXT,
    duration_ms INTEGER
  );
  CREATE INDEX IF NOT EXISTS operations_started_idx ON operations(started_at DESC);
  CREATE INDEX IF NOT EXISTS operations_plugin_idx ON operations(plugin_id, started_at DESC);
  CREATE INDEX IF NOT EXISTS operations_status_idx ON operations(status, started_at DESC);

  CREATE TABLE IF NOT EXISTS oauth_clients (
    client_id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    redirect_uris_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    disabled_at TEXT,
    note TEXT NOT NULL DEFAULT ''
  );
  CREATE TABLE IF NOT EXISTS oauth_codes (
    code_hash TEXT PRIMARY KEY,
    client_id TEXT NOT NULL REFERENCES oauth_clients(client_id) ON DELETE CASCADE,
    redirect_uri TEXT NOT NULL,
    code_challenge TEXT NOT NULL,
    scope TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    used_at TEXT
  );
  CREATE INDEX IF NOT EXISTS oauth_code_expiry_idx ON oauth_codes(expires_at);
  CREATE TABLE IF NOT EXISTS oauth_tokens (
    token_hash TEXT PRIMARY KEY,
    client_id TEXT NOT NULL REFERENCES oauth_clients(client_id) ON DELETE CASCADE,
    scope TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    revoked_at TEXT,
    created_at TEXT NOT NULL
  );
  CREATE INDEX IF NOT EXISTS oauth_token_expiry_idx ON oauth_tokens(expires_at);
  CREATE TABLE IF NOT EXISTS oauth_refresh_tokens (
    token_hash TEXT PRIMARY KEY,
    client_id TEXT NOT NULL REFERENCES oauth_clients(client_id) ON DELETE CASCADE,
    scope TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    revoked_at TEXT,
    created_at TEXT NOT NULL
  );
  CREATE INDEX IF NOT EXISTS oauth_refresh_expiry_idx ON oauth_refresh_tokens(expires_at);

  CREATE TABLE IF NOT EXISTS web_sessions (
    token_hash TEXT PRIMARY KEY,
    expires_at TEXT NOT NULL,
    created_at TEXT NOT NULL
  );
  CREATE INDEX IF NOT EXISTS web_session_expiry_idx ON web_sessions(expires_at);

  CREATE TABLE IF NOT EXISTS passkeys (
    credential_id TEXT PRIMARY KEY,
    public_key BLOB NOT NULL,
    counter INTEGER NOT NULL,
    transports_json TEXT NOT NULL,
    device_type TEXT NOT NULL CHECK(device_type IN ('singleDevice','multiDevice')),
    backed_up INTEGER NOT NULL,
    name TEXT NOT NULL,
    created_at TEXT NOT NULL,
    last_used_at TEXT NOT NULL
  );
  CREATE INDEX IF NOT EXISTS passkeys_last_used_idx ON passkeys(last_used_at DESC);

  CREATE TABLE IF NOT EXISTS computer_settings (
    id INTEGER PRIMARY KEY CHECK(id = 1),
    enabled INTEGER NOT NULL DEFAULT 0,
    remote_access INTEGER NOT NULL DEFAULT 1,
    updated_at TEXT NOT NULL
  );

  CREATE TABLE IF NOT EXISTS mcp_tool_settings (
    tool_name TEXT PRIMARY KEY,
    enabled INTEGER NOT NULL CHECK(enabled IN (0, 1)),
    updated_at TEXT NOT NULL
  );

  CREATE TABLE IF NOT EXISTS agent_sessions (
    id TEXT PRIMARY KEY,
    workspace_root TEXT NOT NULL,
    provider TEXT NOT NULL,
    status TEXT NOT NULL CHECK(status IN ('idle','running','waiting_input','error')),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
  );
  CREATE INDEX IF NOT EXISTS agent_sessions_updated_idx ON agent_sessions(updated_at DESC);

  CREATE TABLE IF NOT EXISTS agent_turns (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL REFERENCES agent_sessions(id) ON DELETE CASCADE,
    provider_turn_id TEXT,
    provider TEXT NOT NULL,
    model TEXT,
    reasoning_effort TEXT,
    reasoning_summary TEXT CHECK(reasoning_summary IN ('auto','concise','detailed','none')),
    service_tier TEXT,
    approval_policy_json TEXT,
    approvals_reviewer TEXT CHECK(approvals_reviewer IN ('user','auto_review','guardian_subagent')),
    permission_mode TEXT CHECK(permission_mode IN ('default','acceptEdits','bypassPermissions','plan','dontAsk','auto','read-only','workspace-write','danger-full-access')),
    usage_json TEXT,
    origin INTEGER NOT NULL CHECK(origin IN (0,1)),
    status TEXT NOT NULL CHECK(status IN ('running','completed','interrupted','failed')),
    error_json TEXT NOT NULL,
    started_at TEXT NOT NULL,
    completed_at TEXT
  );
  CREATE INDEX IF NOT EXISTS agent_turns_session_idx ON agent_turns(session_id, started_at ASC);

  CREATE TABLE IF NOT EXISTS agent_items (
    id TEXT PRIMARY KEY,
    turn_id TEXT NOT NULL REFERENCES agent_turns(id) ON DELETE CASCADE,
    provider_item_id TEXT,
    type TEXT NOT NULL,
    sequence INTEGER NOT NULL,
    status TEXT NOT NULL CHECK(status IN ('running','completed','waiting','interrupted','failed')),
    payload_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
  );
  CREATE INDEX IF NOT EXISTS agent_items_turn_idx ON agent_items(turn_id, sequence ASC);

  CREATE TABLE IF NOT EXISTS agent_session_providers (
    session_id TEXT NOT NULL REFERENCES agent_sessions(id) ON DELETE CASCADE,
    provider TEXT NOT NULL,
    provider_session_id TEXT NOT NULL,
    model TEXT,
    reasoning_effort TEXT,
    reasoning_summary TEXT CHECK(reasoning_summary IN ('auto','concise','detailed','none')),
    service_tier TEXT,
    approval_policy_json TEXT,
    approvals_reviewer TEXT CHECK(approvals_reviewer IN ('user','auto_review','guardian_subagent')),
    permission_mode TEXT CHECK(permission_mode IN ('default','acceptEdits','bypassPermissions','plan','dontAsk','auto','read-only','workspace-write','danger-full-access')),
    token_usage_json TEXT,
    continuity_context TEXT NOT NULL DEFAULT '',
    synced_through_turn_id TEXT REFERENCES agent_turns(id) ON DELETE SET NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(session_id, provider)
  );
  CREATE TABLE IF NOT EXISTS mcp_server_settings (
    server_name TEXT PRIMARY KEY,
    enabled INTEGER NOT NULL CHECK(enabled IN (0, 1)),
    updated_at TEXT NOT NULL
  );

  CREATE TABLE IF NOT EXISTS oauth_registration_settings (
    id INTEGER PRIMARY KEY CHECK(id = 1),
    allow_new_registrations INTEGER NOT NULL DEFAULT 1 CHECK(allow_new_registrations IN (0, 1)),
    updated_at TEXT NOT NULL
  );

`;

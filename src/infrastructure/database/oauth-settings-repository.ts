import type { AppDatabase } from "./app-database.js";

export class OAuthSettingsRepository {
  constructor(private readonly database: AppDatabase) {}

  getRegistrationEnabled(): boolean {
    const row = this.database.raw
      .prepare(
        "SELECT allow_new_registrations FROM oauth_registration_settings WHERE id = 1",
      )
      .get() as { allow_new_registrations: number } | undefined;
    // Default to open registration so upgrades never silently change behavior.
    return row ? Boolean(row.allow_new_registrations) : true;
  }

  setRegistrationEnabled(enabled: boolean): void {
    this.database.raw
      .prepare(
        `
      INSERT INTO oauth_registration_settings(id, allow_new_registrations, updated_at)
      VALUES(1, ?, ?)
      ON CONFLICT(id) DO UPDATE SET
        allow_new_registrations = excluded.allow_new_registrations,
        updated_at = excluded.updated_at
    `,
      )
      .run(enabled ? 1 : 0, new Date().toISOString());
  }
}

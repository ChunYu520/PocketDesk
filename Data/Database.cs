using Microsoft.Data.Sqlite;

namespace PocketDesk;

/// <summary>SQLite 初始化、schema 版本与迁移备份。</summary>
public static class Database
{
    public const int CurrentSchemaVersion = 1;

    private static string _connectionString = string.Empty;

    public static bool IsInitialized => !string.IsNullOrEmpty(_connectionString);

    public static void Initialize()
    {
        AppPaths.EnsureCreated();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();

        using var connection = Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA synchronous=NORMAL;");
        Execute(connection, "PRAGMA foreign_keys=ON;");

        var version = ReadVersion(connection);
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"数据库版本 {version} 高于当前应用支持的 {CurrentSchemaVersion}，请升级 PocketDesk 后再试。");
        }

        if (version == 0)
        {
            CreateSchema(connection);
            SetVersion(connection, CurrentSchemaVersion);
            LogService.Info("数据库已创建（schema v1）");
            return;
        }

        if (version < CurrentSchemaVersion)
        {
            BackupDatabase(version);
            Migrate(connection, version);
        }
    }

    public static SqliteConnection Open()
    {
        if (string.IsNullOrEmpty(_connectionString))
        {
            throw new InvalidOperationException("数据库尚未初始化。");
        }

        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys=ON;");
        return connection;
    }

    public static string BackupDirFor(string label) => AppPaths.BackupDir;

    private static int ReadVersion(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT version FROM schema_version LIMIT 1;";
            var value = command.ExecuteScalar();
            return value is null or DBNull ? 0 : Convert.ToInt32(value);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static void SetVersion(SqliteConnection connection, int version)
    {
        Execute(connection, "DELETE FROM schema_version;");
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO schema_version(version) VALUES ($v);";
        command.Parameters.AddWithValue("$v", version);
        command.ExecuteNonQuery();
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS boxes (
                id              TEXT PRIMARY KEY,
                name            TEXT NOT NULL,
                type            INTEGER NOT NULL,
                target_path     TEXT NULL,
                appearance_json TEXT NOT NULL DEFAULT '{}',
                window_json     TEXT NULL,
                z_order         INTEGER NOT NULL DEFAULT 0,
                is_pinned       INTEGER NOT NULL DEFAULT 0,
                is_visible      INTEGER NOT NULL DEFAULT 1,
                sort_field      TEXT NOT NULL DEFAULT 'name',
                sort_desc       INTEGER NOT NULL DEFAULT 0,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS items (
                id             TEXT PRIMARY KEY,
                box_id         TEXT NOT NULL,
                kind           INTEGER NOT NULL,
                display_name   TEXT NOT NULL,
                source_path    TEXT NULL,
                stored_path    TEXT NULL,
                icon_cache_key TEXT NULL,
                sort_index     REAL NOT NULL DEFAULT 0,
                added_at       TEXT NOT NULL,
                original_box_id TEXT NULL,
                is_link        INTEGER NOT NULL DEFAULT 0,
                modified_at    TEXT NULL,
                size_bytes     INTEGER NOT NULL DEFAULT 0,
                shortcut_target TEXT NULL,
                FOREIGN KEY (box_id) REFERENCES boxes(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_items_box ON items(box_id);
            CREATE INDEX IF NOT EXISTS idx_items_display ON items(box_id, display_name);

            CREATE TABLE IF NOT EXISTS journal (
                id           TEXT PRIMARY KEY,
                op_type      INTEGER NOT NULL,
                box_id       TEXT NULL,
                item_id      TEXT NULL,
                from_path    TEXT NULL,
                to_path      TEXT NULL,
                state        INTEGER NOT NULL,
                created_at   TEXT NOT NULL,
                completed_at TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_journal_state ON journal(state);

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
    }

    private static void Migrate(SqliteConnection connection, int fromVersion)
    {
        // 目前仅 v1。后续版本在此按 fromVersion 逐步升级，每一步都必须幂等。
        var version = fromVersion;
        if (version < 1)
        {
            CreateSchema(connection);
            version = 1;
        }

        // if (version < 2) { ...; version = 2; }
        SetVersion(connection, version);
        LogService.Info($"数据库迁移完成：{fromVersion} -> {version}");
    }

    private static void BackupDatabase(int version)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.BackupDir);
            var target = Path.Combine(
                AppPaths.BackupDir,
                $"db-v{version}-{DateTime.Now:yyyyMMdd-HHmmss}.bak");
            using var source = new SqliteConnection(_connectionString);
            source.Open();
            using var command = source.CreateCommand();
            command.CommandText = "VACUUM INTO $target;";
            command.Parameters.AddWithValue("$target", target);
            command.ExecuteNonQuery();
            source.Close();

            foreach (var old in Directory.EnumerateFiles(AppPaths.BackupDir, "*.bak")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(3))
            {
                try
                {
                    File.Delete(old);
                }
                catch
                {
                    // ignore
                }
            }

            LogService.Info($"已备份数据库到 {target}");
        }
        catch (Exception ex)
        {
            LogService.Error("数据库备份失败", ex);
            // 备份失败不阻断迁移，但记录日志。
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

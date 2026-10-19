using Microsoft.Data.Sqlite;

namespace DistributedHls.Core;

// Every method opens its own connection and every write runs in one IMMEDIATE transaction. Times are Unix milliseconds, GUIDs "N".
public sealed partial class JobStore
{
    private const int SchemaVersion = 2;

    private const string Schema = """
        CREATE TABLE sources(item_id TEXT PRIMARY KEY, library_id TEXT NOT NULL, path TEXT NOT NULL, length INTEGER NOT NULL,
          modified_ticks INTEGER NOT NULL, sha256 TEXT NULL);
        CREATE INDEX ix_sources_sha ON sources(sha256);
        CREATE TABLE media(sha256 TEXT PRIMARY KEY, media_uuid TEXT NOT NULL UNIQUE, library_id TEXT NOT NULL, probe TEXT NOT NULL,
          orphaned_at INTEGER NULL, created_at INTEGER NOT NULL);
        CREATE TABLE renditions(sha256 TEXT NOT NULL, key TEXT NOT NULL, state INTEGER NOT NULL,  -- 0 Missing, 2 Published, 3 Failed
          job_id TEXT NULL, published_job_id TEXT NULL, rel_dir TEXT NULL, summary TEXT NULL, profile TEXT NULL, published_at INTEGER NULL,
          PRIMARY KEY(sha256, key));
        CREATE TABLE jobs(id TEXT PRIMARY KEY, work_key TEXT NOT NULL UNIQUE, kind INTEGER NOT NULL, item_id TEXT NULL, sha256 TEXT NULL,
          payload TEXT NOT NULL, status INTEGER NOT NULL, worker TEXT NULL, lease_token TEXT NULL, lease_until INTEGER NULL,
          attempt INTEGER NOT NULL DEFAULT 0, error TEXT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
        CREATE INDEX ix_jobs_status ON jobs(status, created_at);
        CREATE INDEX ix_jobs_sha ON jobs(sha256);
        CREATE TABLE job_outputs(job_id TEXT NOT NULL, key TEXT NOT NULL, rel_dir TEXT NOT NULL, summary TEXT NOT NULL, files TEXT NOT NULL,
          PRIMARY KEY(job_id, key));
        CREATE TABLE version_items(item_id TEXT NOT NULL, quality INTEGER NOT NULL, version_id TEXT NOT NULL, signature TEXT NOT NULL,
          PRIMARY KEY(item_id, quality));
        CREATE TABLE deletions(rel_dir TEXT PRIMARY KEY, not_before INTEGER NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
          reason TEXT NOT NULL, last_error TEXT NULL);
        CREATE TABLE legacy_items(item_id TEXT PRIMARY KEY);
        """;

    private readonly string _connectionString;

    // Every method takes its time as an explicit parameter, so the clock is accepted only to keep the constructor signature.
    // The constructor never throws. The media source decorator depends on this store, so a database that cannot be opened would keep
    // Jellyfin from starting; the reason is kept in InitError instead and every call fails with it.
    public JobStore(string databasePath, TimeProvider? clock = null)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true }.ToString();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
            using var db = Open();
            using var cmd = db.CreateCommand();
            // journal_mode cannot change inside a transaction.
            Sql(cmd, "PRAGMA journal_mode=WAL;").ExecuteNonQuery();
            // The schema of a newer plugin is never touched: creating the tables again would fail on the ones that exist.
            var version = (long)Sql(cmd, "PRAGMA user_version;").ExecuteScalar()!;
            if (version > SchemaVersion) InitError = NewerSchema(version);
            if (version >= SchemaVersion) return;
            using var tx = db.BeginTransaction(deferred: false);
            cmd.Transaction = tx;
            // Checked again under the write lock: another instance may have created the schema in the meantime.
            version = (long)Sql(cmd, "PRAGMA user_version;").ExecuteScalar()!;
            if (version > SchemaVersion) InitError = NewerSchema(version);
            if (version >= SchemaVersion) return;
            List<string> legacy = [];
            if (version == 0 && (long)Sql(cmd, "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='published_assets');").ExecuteScalar()! == 1)
            {
                // Version 1: only the item ids survive (their generated version items are cleaned up later); its output directories are not deleted.
                legacy = Rows(Sql(cmd, "SELECT item_id FROM sources;"), r => r.GetString(0));
                Sql(cmd, """
                    DROP TABLE IF EXISTS jobs; DROP TABLE IF EXISTS probes; DROP TABLE IF EXISTS sources; DROP TABLE IF EXISTS assets;
                    DROP TABLE IF EXISTS published_assets; DROP TABLE IF EXISTS retired_assets; DROP TABLE IF EXISTS retired_directories;
                    """).ExecuteNonQuery();
            }
            Sql(cmd, Schema).ExecuteNonQuery();
            foreach (var item in legacy)
                if (Guid.TryParse(item, out var id)) Sql(cmd, "INSERT OR IGNORE INTO legacy_items(item_id) VALUES($item);", ("$item", Id(id))).ExecuteNonQuery();
            Sql(cmd, $"PRAGMA user_version={SchemaVersion};").ExecuteNonQuery();
            tx.Commit();
        }
        catch (Exception ex)
        {
            InitError = $"无法打开 HLS 数据库 {databasePath}：{ex.Message}";
        }
    }

    // Null while the database is usable; otherwise why it is not, and every method throws InvalidOperationException with this text.
    public string? InitError { get; private set; }

    private static string NewerSchema(long version) => $"HLS 数据库版本 {version} 高于本插件支持的 {SchemaVersion}，可能装回了旧版本插件";

    private SqliteConnection Open()
    {
        if (InitError is not null) throw new InvalidOperationException(InitError);
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    // Runs body inside one IMMEDIATE transaction: committed when body returns, rolled back when it throws.
    private T Write<T>(Func<SqliteCommand, T> body)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        var result = body(cmd);
        tx.Commit();
        return result;
    }

    private void Write(Action<SqliteCommand> body) => Write(cmd => { body(cmd); return true; });

    private T Read<T>(Func<SqliteCommand, T> body)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        return body(cmd);
    }

    // Reuses the command for the next statement; null values are bound as SQL NULL.
    private static SqliteCommand Sql(SqliteCommand cmd, string text, params (string Name, object? Value)[] parameters)
    {
        cmd.CommandText = text;
        cmd.Parameters.Clear();
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    // Reads every row before returning, so the same connection can write afterwards.
    private static List<T> Rows<T>(SqliteCommand cmd, Func<SqliteDataReader, T> map)
    {
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    private static string Id(Guid id) => id.ToString("N");
    private static long Ms(DateTimeOffset time) => time.ToUnixTimeMilliseconds();
    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static Guid? OptionalId(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Guid.Parse(r.GetString(i));

    // Runs inside the caller's transaction. Only directories of the generated layout are ever queued; a queued
    // directory keeps its earliest not_before.
    private static void QueueDeletion(SqliteCommand cmd, string relDir, DateTimeOffset notBefore, string reason)
    {
        if (!RenditionPaths.IsValidRelativeDirectory(relDir)) throw new ArgumentException($"删除队列只接受档位目录：{relDir}。");
        using var insert = cmd.Connection!.CreateCommand();
        insert.Transaction = cmd.Transaction;
        Sql(insert, """
            INSERT INTO deletions(rel_dir,not_before,reason) VALUES($rel,$notBefore,$reason)
            ON CONFLICT(rel_dir) DO UPDATE SET not_before=MIN(deletions.not_before,excluded.not_before);
            """, ("$rel", relDir), ("$notBefore", Ms(notBefore)), ("$reason", reason)).ExecuteNonQuery();
    }
}

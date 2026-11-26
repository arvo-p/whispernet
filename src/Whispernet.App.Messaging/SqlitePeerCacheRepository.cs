using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Whispernet.Core;

namespace Whispernet.App.Messaging;

/// <summary>
/// Implémentation SQLite native multiplateforme (Windows / Linux / Raspberry Pi) de <see cref="IPeerCacheRepository"/>.
/// Utilise directement l'API C standard de SQLite3 (<c>libsqlite3</c> / <c>e_sqlite3</c> / <c>sqlite3</c>)
/// pour ne nécessiter aucune dépendance NuGet lourde tout en stockant une vraie base SQLite relationnelle.
/// </summary>
public sealed class SqlitePeerCacheRepository : IPeerCacheRepository
{
    private readonly string _databasePath;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SqlitePeerCacheRepository(string databasePath){
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken ct = default){
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try{
            string? dir = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(dir)){
                Directory.CreateDirectory(dir);
            }

            using var db = SqliteNativeConnection.Open(_databasePath);
            db.ExecuteNonQuery("""
                CREATE TABLE IF NOT EXISTS peer_cache (
                    node_id TEXT PRIMARY KEY,
                    role INTEGER NOT NULL,
                    endpoint_ticket TEXT NULL,
                    last_seen_utc TEXT NOT NULL,
                    last_attempt_utc TEXT NULL,
                    success_count INTEGER NOT NULL DEFAULT 0,
                    consecutive_failures INTEGER NOT NULL DEFAULT 0,
                    total_failure_count INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_peer_cache_priority
                ON peer_cache (role DESC, consecutive_failures ASC, last_seen_utc DESC, success_count DESC);
                """);
        }
        finally{
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<CachedPeerEntry>> LoadCachedPeersAsync(int maxLimit = 100, CancellationToken ct = default){
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try{
            using var db = SqliteNativeConnection.Open(_databasePath);
            string sql = $"""
                SELECT node_id, role, endpoint_ticket, last_seen_utc, last_attempt_utc,
                       success_count, consecutive_failures, total_failure_count
                FROM peer_cache
                ORDER BY
                    CASE WHEN role = {(int)NodeRole.MeetingPoint} THEN 1 ELSE 0 END DESC,
                    consecutive_failures ASC,
                    last_seen_utc DESC,
                    success_count DESC
                LIMIT {Math.Max(1, maxLimit)};
                """;

            var entries = new List<CachedPeerEntry>();
            db.ExecuteReader(sql, row =>
            {
				string nodeId = row[0];
                if (string.IsNullOrWhiteSpace(nodeId)) return;

                NodeRole role = int.TryParse(row[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int r)
                    ? (NodeRole)r : NodeRole.Peer;

                string? ticket = string.IsNullOrEmpty(row[2]) ? null : row[2];

                DateTimeOffset lastSeen = DateTimeOffset.TryParse(row[3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ls) ? ls : DateTimeOffset.UtcNow;

                DateTimeOffset? lastAttempt = !string.IsNullOrEmpty(row[4])
                    && DateTimeOffset.TryParse(row[4], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var la)
                    ? la
                    : null;

                int successCount = int.TryParse(row[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sc) ? sc : 0;
                int consecutiveFailures = int.TryParse(row[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cf) ? cf : 0;
                int totalFailures = int.TryParse(row[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tf) ? tf : 0;

                entries.Add(new CachedPeerEntry(
                    NodeId: new NodeId(nodeId),
                    Role: role,
                    EndpointTicket: ticket,
                    LastSeenUtc: lastSeen,
                    SuccessCount: successCount,
                    ConsecutiveFailures: consecutiveFailures,
                    TotalFailureCount: totalFailures,
                    LastAttemptUtc: lastAttempt));
            });

            return entries;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task ApplyCacheUpdateAsync( IEnumerable<CachedPeerEntry> upsertedEntries, IEnumerable<NodeId> evictedNodes, CancellationToken ct = default){
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try{
            using var db = SqliteNativeConnection.Open(_databasePath);
            db.ExecuteNonQuery("BEGIN TRANSACTION;");
            try{
                foreach (NodeId evicted in evictedNodes){
                    // Les Points de Meeting (role = 1) sont protégés contre toute suppression accidentelle
                    string delSql = $"DELETE FROM peer_cache WHERE node_id = {SqlLiteral(evicted.Value)} AND role != {(int)NodeRole.MeetingPoint};";
                    db.ExecuteNonQuery(delSql);
                }

                foreach (CachedPeerEntry entry in upsertedEntries){
                    string upsertSql = $"""
                        INSERT INTO peer_cache (
                            node_id, role, endpoint_ticket, last_seen_utc, last_attempt_utc,
                            success_count, consecutive_failures, total_failure_count
                        )
                        VALUES (
                            {SqlLiteral(entry.NodeId.Value)},
                            {(int)entry.Role},
                            {SqlNullableLiteral(entry.EndpointTicket)},
                            {SqlLiteral(entry.LastSeenUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))},
                            {SqlNullableLiteral(entry.LastAttemptUtc?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))},
                            {entry.SuccessCount},
                            {entry.ConsecutiveFailures},
                            {entry.TotalFailureCount}
                        )
                        ON CONFLICT(node_id) DO UPDATE SET
                            role = excluded.role,
                            endpoint_ticket = COALESCE(excluded.endpoint_ticket, peer_cache.endpoint_ticket),
                            last_seen_utc = excluded.last_seen_utc,
                            last_attempt_utc = excluded.last_attempt_utc,
                            success_count = excluded.success_count,
                            consecutive_failures = excluded.consecutive_failures,
                            total_failure_count = excluded.total_failure_count;
                        """;
                    db.ExecuteNonQuery(upsertSql);
                }

                db.ExecuteNonQuery("COMMIT;");
            }
            catch{
                db.ExecuteNonQuery("ROLLBACK;");
                throw;
            }
        }
        finally{
            _mutex.Release();
        }
    }

    public async Task<int> PruneExpiredAndFailedPeersAsync(
        DateTimeOffset nowUtc,
        TimeSpan maxAge,
        int maxConsecutiveFailures = 5,
        CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = SqliteNativeConnection.Open(_databasePath);
            string cutoffIso = (nowUtc - maxAge).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            string sql = $"""
                DELETE FROM peer_cache
                WHERE role != {(int)NodeRole.MeetingPoint}
                  AND (consecutive_failures >= {maxConsecutiveFailures}
                       OR last_seen_utc < {SqlLiteral(cutoffIso)});
                """;
            db.ExecuteNonQuery(sql);
            return db.GetChangesCount();
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static string SqlLiteral(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string SqlNullableLiteral(string? value)
        => value is null ? "NULL" : SqlLiteral(value);
}

/// <summary>
/// Wrapper P/Invoke minimaliste autour de la bibliothèque partagée C SQLite3
/// (<c>libsqlite3.so</c> sous Linux/Raspberry Pi, <c>sqlite3.dll</c> / <c>winsqlite3.dll</c> sous Windows).
/// </summary>
internal sealed class SqliteNativeConnection : IDisposable
{
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_DONE = 101;

    private IntPtr _db;

    static SqliteNativeConnection()
    {
        NativeLibrary.SetDllImportResolver(typeof(SqliteNativeConnection).Assembly, ResolveSqliteLibrary);
    }

    private static IntPtr ResolveSqliteLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "sqlite3")
        {
            return IntPtr.Zero;
        }

        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "e_sqlite3.dll", "sqlite3.dll", "winsqlite3.dll" }
            : OperatingSystem.IsMacOS()
                ? new[] { "libsqlite3.dylib" }
                : new[] { "libsqlite3.so.0", "libsqlite3.so", "e_sqlite3" };

        foreach (string candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out IntPtr handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private SqliteNativeConnection(IntPtr db) => _db = db;

    public static SqliteNativeConnection Open(string path)
    {
        int rc = sqlite3_open(path, out IntPtr db);
        if (rc != SQLITE_OK)
        {
            string err = GetErrorMessage(db);
            if (db != IntPtr.Zero)
            {
                sqlite3_close(db);
            }
            throw new InvalidOperationException($"Échec d'ouverture SQLite ('{path}', code {rc}): {err}");
        }

        return new SqliteNativeConnection(db);
    }

    public void ExecuteNonQuery(string sql)
    {
        int rc = sqlite3_exec(_db, sql, IntPtr.Zero, IntPtr.Zero, out IntPtr errMsgPtr);
        if (rc != SQLITE_OK)
        {
            string err = errMsgPtr != IntPtr.Zero
                ? Marshal.PtrToStringUTF8(errMsgPtr) ?? $"SQLite error {rc}"
                : GetErrorMessage(_db);
            if (errMsgPtr != IntPtr.Zero)
            {
                sqlite3_free(errMsgPtr);
            }
            throw new InvalidOperationException($"Erreur SQLite ({rc}): {err}");
        }
    }

    public void ExecuteReader(string sql, Action<string?[]> onRow)
    {
        int rc = sqlite3_prepare_v2(_db, sql, -1, out IntPtr stmt, IntPtr.Zero);
        if (rc != SQLITE_OK)
        {
            throw new InvalidOperationException($"Erreur sqlite3_prepare_v2 ({rc}): {GetErrorMessage(_db)}");
        }

        try
        {
            int colCount = sqlite3_column_count(stmt);
            while (true)
            {
                int stepRc = sqlite3_step(stmt);
                if (stepRc == SQLITE_ROW)
                {
                    var row = new string?[colCount];
                    for (int i = 0; i < colCount; i++)
                    {
                        IntPtr textPtr = sqlite3_column_text(stmt, i);
                        row[i] = textPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(textPtr) : null;
                    }
                    onRow(row);
                }
                else if (stepRc == SQLITE_DONE)
                {
                    break;
                }
                else
                {
                    throw new InvalidOperationException($"Erreur sqlite3_step ({stepRc}): {GetErrorMessage(_db)}");
                }
            }
        }
        finally
        {
            sqlite3_finalize(stmt);
        }
    }

    public int GetChangesCount() => sqlite3_changes(_db);

    private static string GetErrorMessage(IntPtr db)
    {
        if (db == IntPtr.Zero)
        {
            return "No database handle";
        }
        IntPtr ptr = sqlite3_errmsg(db);
        return ptr != IntPtr.Zero ? Marshal.PtrToStringUTF8(ptr) ?? "Unknown SQLite error" : "Unknown SQLite error";
    }

    public void Dispose()
    {
        if (_db != IntPtr.Zero)
        {
            sqlite3_close(_db);
            _db = IntPtr.Zero;
        }
    }

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr ppDb);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(
        IntPtr db,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        IntPtr callback,
        IntPtr arg,
        out IntPtr errMsg);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(IntPtr ptr);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(
        IntPtr db,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string zSql,
        int nByte,
        out IntPtr ppStmt,
        IntPtr pzTail);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_count(IntPtr stmt);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_changes(IntPtr db);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr db);
}

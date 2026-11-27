using System.Globalization;
using System.Text.Json;
using Whispernet.Core;

namespace Whispernet.App.Messaging;

/// <summary>
/// Persistance SQLite native de l'historique des événements de chat (<see cref="ChatEvent"/>)
/// réutilisant <see cref="SqliteNativeConnection"/> sans dépendance NuGet externe.
/// </summary>
public sealed class SqliteChatHistoryRepository
{
    private readonly string _databasePath;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SqliteChatHistoryRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? dir = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var db = SqliteNativeConnection.Open(_databasePath);
            db.ExecuteNonQuery("""
                CREATE TABLE IF NOT EXISTS chat_events (
                    event_id TEXT PRIMARY KEY,
                    room_id TEXT NOT NULL,
                    author_pub_key_b64 TEXT NOT NULL,
                    parents_json TEXT NOT NULL,
                    logical_clock INTEGER NOT NULL,
                    timestamp_ms INTEGER NOT NULL,
                    payload_b64 TEXT NOT NULL,
                    signature_b64 TEXT NOT NULL,
                    author_name TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX IF NOT EXISTS idx_chat_events_room_clock
                ON chat_events (room_id, logical_clock ASC, timestamp_ms ASC, event_id ASC);
                """);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public Task UpsertEventAsync(ChatEvent evt, string authorName = "", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return UpsertBatchAsync(new[] { (evt, authorName) }, ct);
    }

    public Task UpsertBatchAsync(IEnumerable<ChatEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        return UpsertBatchAsync(events.Select(static e => (e, string.Empty)), ct);
    }

    public async Task UpsertBatchAsync(
        IEnumerable<(ChatEvent Event, string AuthorName)> events,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        List<(ChatEvent Event, string AuthorName)> list = events.ToList();
        if (list.Count == 0)
        {
            return;
        }

        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = SqliteNativeConnection.Open(_databasePath);
            db.ExecuteNonQuery("BEGIN TRANSACTION;");
            try
            {
                foreach ((ChatEvent evt, string authorName) in list)
                {
                    string parentsJson = JsonSerializer.Serialize(
                        evt.Parents.Select(static p => Convert.ToHexStringLower(p)).ToArray());

                    string sql = $"""
                        INSERT INTO chat_events (
                            event_id, room_id, author_pub_key_b64, parents_json,
                            logical_clock, timestamp_ms, payload_b64, signature_b64, author_name
                        ) VALUES (
                            {SqlLiteral(evt.EventIdHex)},
                            {SqlLiteral(evt.RoomIdHex)},
                            {SqlLiteral(Convert.ToBase64String(evt.AuthorPublicKey))},
                            {SqlLiteral(parentsJson)},
                            {evt.LogicalClock.ToString(CultureInfo.InvariantCulture)},
                            {evt.TimestampMs.ToString(CultureInfo.InvariantCulture)},
                            {SqlLiteral(Convert.ToBase64String(evt.Payload))},
                            {SqlLiteral(Convert.ToBase64String(evt.Signature))},
                            {SqlLiteral(authorName ?? string.Empty)}
                        )
                        ON CONFLICT(event_id) DO UPDATE SET
                            author_name = CASE
                                WHEN excluded.author_name <> '' THEN excluded.author_name
                                ELSE chat_events.author_name
                            END;
                        """;
                    db.ExecuteNonQuery(sql);
                }
                db.ExecuteNonQuery("COMMIT;");
            }
            catch
            {
                db.ExecuteNonQuery("ROLLBACK;");
                throw;
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<(ChatEvent Event, string AuthorName)>> LoadAllEventsAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = SqliteNativeConnection.Open(_databasePath);
            const string sql = """
                SELECT event_id, room_id, author_pub_key_b64, parents_json,
                       logical_clock, timestamp_ms, payload_b64, signature_b64, author_name
                FROM chat_events
                ORDER BY room_id ASC, logical_clock ASC, timestamp_ms ASC, event_id ASC;
                """;

            var results = new List<(ChatEvent Event, string AuthorName)>();
            db.ExecuteReader(sql, row =>
            {
                if (row[0] is null || row[1] is null || row[2] is null || row[3] is null ||
                    row[4] is null || row[5] is null || row[6] is null || row[7] is null)
                {
                    return;
                }

                byte[] eventId = Convert.FromHexString(row[0]!);
                byte[] roomId = Convert.FromHexString(row[1]!);
                byte[] authorPubKey = Convert.FromBase64String(row[2]!);
                string[] parentHexes = JsonSerializer.Deserialize<string[]>(row[3]!) ?? Array.Empty<string>();
                List<byte[]> parents = parentHexes.Select(static h => Convert.FromHexString(h)).ToList();
                ulong logicalClock = ulong.Parse(row[4]!, CultureInfo.InvariantCulture);
                ulong timestampMs = ulong.Parse(row[5]!, CultureInfo.InvariantCulture);
                byte[] payload = Convert.FromBase64String(row[6]!);
                byte[] signature = Convert.FromBase64String(row[7]!);
                string authorName = row[8] ?? string.Empty;

                var evt = new ChatEvent(
                    EventId: eventId,
                    RoomId: roomId,
                    AuthorPublicKey: authorPubKey,
                    Parents: parents,
                    LogicalClock: logicalClock,
                    TimestampMs: timestampMs,
                    Payload: payload,
                    Signature: signature);

                results.Add((evt, authorName));
            });

            return results;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static string SqlLiteral(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

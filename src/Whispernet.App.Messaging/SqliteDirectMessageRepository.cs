using System.Globalization;
using System.Text.Json;
using Whispernet.Core;

namespace Whispernet.App.Messaging;

/// <summary>
/// Persistance SQLite des messages 1-on-1 (déduplication par UUID), du profil horaire d'activité des pairs
/// (jours ouvrés vs week-end), des prédictibilités PRoPHET et des bundles DTN aveugles en transit.
/// </summary>
public sealed class SqliteDirectMessageRepository
{
    private readonly string _databasePath;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SqliteDirectMessageRepository(string databasePath)
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
                CREATE TABLE IF NOT EXISTS direct_messages (
                    message_uuid TEXT PRIMARY KEY,
                    peer_author_id TEXT NOT NULL,
                    is_outgoing INTEGER NOT NULL,
                    content TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    sent_at_utc TEXT NOT NULL,
                    delivered_at_utc TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_dm_peer_time
                ON direct_messages (peer_author_id, sent_at_utc ASC);

                CREATE TABLE IF NOT EXISTS peer_activity_timetable (
                    author_id TEXT PRIMARY KEY,
                    node_id TEXT NULL,
                    weekday_hours TEXT NOT NULL,
                    weekend_hours TEXT NOT NULL,
                    total_samples INTEGER NOT NULL DEFAULT 0,
                    last_updated_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS prophet_predictability (
                    peer_id TEXT NOT NULL,
                    target_id TEXT NOT NULL,
                    p_value REAL NOT NULL,
                    last_updated_utc TEXT NOT NULL,
                    PRIMARY KEY (peer_id, target_id)
                );

                CREATE TABLE IF NOT EXISTS dtn_relay_bundles (
                    bundle_id TEXT PRIMARY KEY,
                    message_uuid TEXT NOT NULL,
                    blind_tag TEXT NOT NULL,
                    epoch INTEGER NOT NULL,
                    payload_json TEXT NOT NULL,
                    expires_at_utc TEXT NOT NULL,
                    hop_count INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_dtn_blind_tag
                ON dtn_relay_bundles (blind_tag);
                """);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Insère un message 1-on-1 de manière idempotente. Retourne false si l'UUID existe déjà (déduplication).
    /// </summary>
    public async Task<bool> TryInsertMessageAsync(DirectMessageRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = SqliteNativeConnection.Open(_databasePath);
            string sql = $"""
                INSERT OR IGNORE INTO direct_messages
                    (message_uuid, peer_author_id, is_outgoing, content, status, sent_at_utc, delivered_at_utc)
                VALUES (
                    {SqlLiteral(record.MessageUuid.ToString("D"))},
                    {SqlLiteral(record.PeerAuthorId.Value)},
                    {(record.IsOutgoing ? 1 : 0)},
                    {SqlLiteral(record.Content)},
                    {(int)record.Status},
                    {SqlLiteral(record.SentAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))},
                    {SqlNullableLiteral(record.DeliveredAtUtc?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}
                );
                """;
            db.ExecuteNonQuery(sql);
            return db.GetChangesCount() > 0;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task UpdateMessageStatusAsync(
        Guid messageUuid,
        DirectMessageStatus status,
        DateTimeOffset? deliveredAtUtc = null,
        CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = SqliteNativeConnection.Open(_databasePath);
            string sql = $"""
                UPDATE direct_messages
                SET status = {(int)status},
                    delivered_at_utc = COALESCE({SqlNullableLiteral(deliveredAtUtc?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}, delivered_at_utc)
                WHERE message_uuid = {SqlLiteral(messageUuid.ToString("D"))};
                """;
            db.ExecuteNonQuery(sql);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<DirectMessageRecord>> LoadAllMessagesAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = SqliteNativeConnection.Open(_databasePath);
            var results = new List<DirectMessageRecord>();
            db.ExecuteReader("""
                SELECT message_uuid, peer_author_id, is_outgoing, content, status, sent_at_utc, delivered_at_utc
                FROM direct_messages
                ORDER BY sent_at_utc ASC;
                """, row =>
            {
                if (!Guid.TryParse(row[0], out Guid uuid))
                {
                    return;
                }
                var peerId = new AuthorId(row[1] ?? string.Empty);
                bool isOut = row[2] == "1";
                string content = row[3] ?? string.Empty;
                DirectMessageStatus status = int.TryParse(row[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int st)
                    ? (DirectMessageStatus)st
                    : DirectMessageStatus.Sending;
                DateTimeOffset sentAt = DateTimeOffset.TryParse(row[5], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var sa)
                    ? sa
                    : DateTimeOffset.UtcNow;
                DateTimeOffset? deliveredAt = !string.IsNullOrEmpty(row[6]) &&
                    DateTimeOffset.TryParse(row[6], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var da)
                    ? da
                    : null;

                results.Add(new DirectMessageRecord(uuid, peerId, isOut, content, status, sentAt, deliveredAt));
            });
            return results;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task UpsertActivityProfileAsync(PeerActivityProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = SqliteNativeConnection.Open(_databasePath);
            string wkJson = JsonSerializer.Serialize(profile.WeekdayHours);
            string weJson = JsonSerializer.Serialize(profile.WeekendHours);
            string sql = $"""
                INSERT INTO peer_activity_timetable
                    (author_id, node_id, weekday_hours, weekend_hours, total_samples, last_updated_utc)
                VALUES (
                    {SqlLiteral(profile.AuthorId.Value)},
                    {SqlNullableLiteral(profile.LastKnownNodeId?.Value)},
                    {SqlLiteral(wkJson)},
                    {SqlLiteral(weJson)},
                    {profile.TotalSamples},
                    {SqlLiteral(profile.LastUpdatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}
                )
                ON CONFLICT(author_id) DO UPDATE SET
                    node_id = COALESCE(excluded.node_id, peer_activity_timetable.node_id),
                    weekday_hours = excluded.weekday_hours,
                    weekend_hours = excluded.weekend_hours,
                    total_samples = excluded.total_samples,
                    last_updated_utc = excluded.last_updated_utc;
                """;
            db.ExecuteNonQuery(sql);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<PeerActivityProfile>> LoadActivityProfilesAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = SqliteNativeConnection.Open(_databasePath);
            var list = new List<PeerActivityProfile>();
            db.ExecuteReader("""
                SELECT author_id, node_id, weekday_hours, weekend_hours, total_samples, last_updated_utc
                FROM peer_activity_timetable;
                """, row =>
            {
                var authorId = new AuthorId(row[0] ?? string.Empty);
                string? nodeStr = row[1];
                NodeId? nodeId = string.IsNullOrWhiteSpace(nodeStr) ? null : new NodeId(nodeStr);
                double[]? wk = JsonSerializer.Deserialize<double[]>(row[2] ?? "[]");
                double[]? we = JsonSerializer.Deserialize<double[]>(row[3] ?? "[]");
                int samples = int.TryParse(row[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s : 0;
                DateTimeOffset updated = DateTimeOffset.TryParse(row[5], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                    ? dt
                    : DateTimeOffset.UtcNow;
                list.Add(new PeerActivityProfile(authorId, nodeId, wk, we, samples, updated));
            });
            return list;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task UpsertProphetAsync(
        AuthorId peerId,
        AuthorId targetId,
        double pValue,
        DateTimeOffset updatedUtc,
        CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = SqliteNativeConnection.Open(_databasePath);
            string sql = $"""
                INSERT INTO prophet_predictability (peer_id, target_id, p_value, last_updated_utc)
                VALUES (
                    {SqlLiteral(peerId.Value)},
                    {SqlLiteral(targetId.Value)},
                    {pValue.ToString("G17", CultureInfo.InvariantCulture)},
                    {SqlLiteral(updatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}
                )
                ON CONFLICT(peer_id, target_id) DO UPDATE SET
                    p_value = excluded.p_value,
                    last_updated_utc = excluded.last_updated_utc;
                """;
            db.ExecuteNonQuery(sql);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<(AuthorId PeerId, AuthorId TargetId, double PValue, DateTimeOffset UpdatedUtc)>> LoadProphetAsync(
        CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = SqliteNativeConnection.Open(_databasePath);
            var list = new List<(AuthorId, AuthorId, double, DateTimeOffset)>();
            db.ExecuteReader("""
                SELECT peer_id, target_id, p_value, last_updated_utc
                FROM prophet_predictability;
                """, row =>
            {
                var peer = new AuthorId(row[0] ?? string.Empty);
                var target = new AuthorId(row[1] ?? string.Empty);
                double p = double.TryParse(row[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double pv) ? pv : 0.0;
                DateTimeOffset dt = DateTimeOffset.TryParse(row[3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
                    ? d
                    : DateTimeOffset.UtcNow;
                list.Add((peer, target, p, dt));
            });
            return list;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task UpsertDtnBundleAsync(DtnBundle bundle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = SqliteNativeConnection.Open(_databasePath);
            string json = JsonSerializer.Serialize(bundle);
            string sql = $"""
                INSERT OR IGNORE INTO dtn_relay_bundles
                    (bundle_id, message_uuid, blind_tag, epoch, payload_json, expires_at_utc, hop_count)
                VALUES (
                    {SqlLiteral(bundle.BundleId.ToString("D"))},
                    {SqlLiteral(bundle.MessageUuid.ToString("D"))},
                    {SqlLiteral(bundle.BlindRoutingTagHex)},
                    {bundle.Epoch.ToString(CultureInfo.InvariantCulture)},
                    {SqlLiteral(json)},
                    {SqlLiteral(bundle.ExpiresAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))},
                    {bundle.HopCount.ToString(CultureInfo.InvariantCulture)}
                );
                """;
            db.ExecuteNonQuery(sql);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<DtnBundle>> QueryDtnBundlesByBlindTagsAsync(
        IReadOnlyList<string> blindTagsHex,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        if (blindTagsHex.Count == 0)
        {
            return Array.Empty<DtnBundle>();
        }

        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = SqliteNativeConnection.Open(_databasePath);
            string inList = string.Join(",", blindTagsHex.Select(SqlLiteral));
            string nowIso = nowUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            var list = new List<DtnBundle>();
            db.ExecuteReader($"""
                SELECT payload_json
                FROM dtn_relay_bundles
                WHERE blind_tag IN ({inList})
                  AND expires_at_utc > {SqlLiteral(nowIso)};
                """, row =>
            {
                string json = row[0] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(json)) return;
                DtnBundle? b = JsonSerializer.Deserialize<DtnBundle>(json);
                if (b is not null)
                {
                    list.Add(b);
                }
            });
            return list;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<DtnBundle>> LoadActiveDtnBundlesAsync(
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = SqliteNativeConnection.Open(_databasePath);
            string nowIso = nowUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            db.ExecuteNonQuery($"DELETE FROM dtn_relay_bundles WHERE expires_at_utc <= {SqlLiteral(nowIso)};");

            var list = new List<DtnBundle>();
            db.ExecuteReader("""
                SELECT payload_json
                FROM dtn_relay_bundles;
                """, row =>
            {
                string json = row[0] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(json)) return;
                DtnBundle? b = JsonSerializer.Deserialize<DtnBundle>(json);
                if (b is not null)
                {
                    list.Add(b);
                }
            });
            return list;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task DeleteDtnBundleByMessageUuidAsync(Guid messageUuid, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = SqliteNativeConnection.Open(_databasePath);
            db.ExecuteNonQuery($"DELETE FROM dtn_relay_bundles WHERE message_uuid = {SqlLiteral(messageUuid.ToString("D"))};");
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

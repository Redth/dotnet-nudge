using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DotnetNudge.Core;

public sealed class NudgeState : IDisposable
{
    readonly SqliteConnection connection;
    readonly TimeProvider clock;

    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable("NUDGE_STATE_DIR") is { Length: > 0 } path
            ? WorkspaceDiscovery.Canonical(path)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dotnet-nudge");

    public NudgeState(string directory, TimeProvider? clock = null)
    {
        Directory.CreateDirectory(directory);
        this.clock = clock ?? TimeProvider.System;
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "state.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);
            INSERT INTO schema_version SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_version);
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT version FROM schema_version";
        if (Convert.ToInt32(command.ExecuteScalar()) != Contract.Version)
            throw new InvalidDataException("Unsupported dotnet-nudge state schema; preserve the database and use a compatible version.");
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS snapshots (
              worktree TEXT PRIMARY KEY, repository TEXT NOT NULL, report TEXT NOT NULL, scanned INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS decisions (
              repository TEXT NOT NULL, subject TEXT NOT NULL, category TEXT NOT NULL,
              action TEXT NOT NULL, candidate TEXT, revision TEXT, until_time INTEGER,
              recorded INTEGER NOT NULL,
              PRIMARY KEY(repository, subject, category, action)
            );
            CREATE TABLE IF NOT EXISTS deliveries (
              repository TEXT NOT NULL, subject TEXT NOT NULL, revision TEXT NOT NULL,
              owner TEXT NOT NULL, lease_until INTEGER NOT NULL, delivered INTEGER,
              PRIMARY KEY(repository, subject, revision)
            );
            CREATE TABLE IF NOT EXISTS metadata (
              source TEXT PRIMARY KEY, body TEXT NOT NULL, etag TEXT, refreshed INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS scan_jobs (
              worktree TEXT PRIMARY KEY, requested INTEGER NOT NULL, status TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public void Save(ScanReport report)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO snapshots(worktree, repository, report, scanned) VALUES ($w,$r,$body,$time)
            ON CONFLICT(worktree) DO UPDATE SET repository=$r, report=$body, scanned=$time
            """;
        command.Parameters.AddWithValue("$w", report.Workspace.WorktreeId);
        command.Parameters.AddWithValue("$r", report.Workspace.RepositoryId);
        command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(report, Contract.Json));
        command.Parameters.AddWithValue("$time", report.ScannedAt.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM scan_jobs WHERE worktree=$w";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public ScanReport? Latest(string worktree)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT report FROM snapshots WHERE worktree=$w";
        command.Parameters.AddWithValue("$w", worktree);
        return command.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<ScanReport>(json, Contract.Json) : null;
    }

    public StatusReport Status(string repository, string worktree)
    {
        var report = Latest(worktree);
        if (report is null)
            return new(Contract.Version, repository, worktree, null, [],
                [new("scan", Assessment.Unknown, "No scan recorded; run scan to establish coverage.")]);
        using var job = connection.CreateCommand();
        job.CommandText = "SELECT status FROM scan_jobs WHERE worktree=$w";
        job.Parameters.AddWithValue("$w", worktree);
        var pending = job.ExecuteScalar() is string status
            ? new Coverage("scan-job", Assessment.Partial,
                $"A {status} scan is queued; no background worker is available in this foundation.")
            : null;
        return new(Contract.Version, repository, worktree, report.ScannedAt,
            report.Findings.Select(f =>
            {
                var reason = Suppression(repository, f) ?? DeliverySuppression(repository, f);
                return new FindingStatus(f, reason is null, reason);
            }).ToArray(), pending is null ? report.Coverage : report.Coverage.Append(pending).ToArray());
    }

    string? DeliverySuppression(string repository, Finding finding)
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT subject,revision,lease_until,delivered FROM deliveries
            WHERE repository=$r AND (subject=$s OR delivered>$day)
            """;
        command.Parameters.AddWithValue("$r", repository);
        command.Parameters.AddWithValue("$s", finding.SubjectKey);
        command.Parameters.AddWithValue("$day", now - 86400);
        var sameSubject = false;
        var repositoryCooldown = false;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var subject = reader.GetString(0);
            var revision = reader.GetString(1);
            var leaseUntil = reader.GetInt64(2);
            var delivered = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
            if (subject == finding.SubjectKey && revision == finding.Revision)
            {
                if (delivered > now - 7 * 86400) return "delivered-recently";
                if (delivered is null && leaseUntil > now) return "delivery-leased";
            }
            if (subject == finding.SubjectKey && delivered > now - 7 * 86400)
                sameSubject = true;
            if (delivered > now - 86400) repositoryCooldown = true;
        }
        if (finding.Priority is Priority.Medium or Priority.Low)
            return sameSubject ? "subject-cooldown" : repositoryCooldown ? "repository-cooldown" : null;
        return null;
    }

    public string? Suppression(string repository, Finding finding)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT subject, action, candidate, revision, until_time FROM decisions
            WHERE repository=$r AND (subject=$s OR (subject='*' AND category=$c))
            """;
        command.Parameters.AddWithValue("$r", repository);
        command.Parameters.AddWithValue("$s", finding.SubjectKey);
        command.Parameters.AddWithValue("$c", finding.Category);
        var disabled = false;
        var snoozed = false;
        var skipped = false;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var subject = reader.GetString(0);
            var action = reader.GetString(1);
            var candidate = reader.IsDBNull(2) ? null : reader.GetString(2);
            var revision = reader.IsDBNull(3) ? null : reader.GetString(3);
            var until = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4);
            if (action == "disable-category" && subject == "*") disabled = true;
            if (action == "snooze" && until > clock.GetUtcNow().ToUnixTimeSeconds()) snoozed = true;
            if (action == "skip-version" && candidate == finding.Candidate && revision == finding.Revision)
                skipped = true;
        }
        return disabled ? "category-disabled" : snoozed ? "snoozed" :
            skipped ? "version-skipped" : null;
    }

    public DecisionResult Decide(string repository, string worktree, string findingId,
        string revision, DecisionAction action, DateTimeOffset? until)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT report FROM snapshots WHERE worktree=$w AND repository=$r";
        command.Parameters.AddWithValue("$w", worktree);
        command.Parameters.AddWithValue("$r", repository);
        var report = command.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<ScanReport>(json, Contract.Json) : null;
        var finding = report?.Findings.SingleOrDefault(f => f.Id == findingId);
        if (finding is null || finding.Revision != revision)
            throw new InvalidOperationException("Finding missing or revision superseded; run status and decide on current evidence.");
        if (action == DecisionAction.Snooze && (until is null || until <= clock.GetUtcNow()))
            throw new ArgumentException("Snooze requires a future --until timestamp.");
        if (action != DecisionAction.Snooze && until is not null)
            throw new ArgumentException("--until is only valid for snooze.");
        var name = action switch
        {
            DecisionAction.Snooze => "snooze",
            DecisionAction.SkipVersion => "skip-version",
            DecisionAction.DisableCategory => "disable-category",
            DecisionAction.EnableCategory => "enable-category",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        command.Parameters.AddWithValue("$s", action is DecisionAction.DisableCategory or DecisionAction.EnableCategory
            ? "*" : finding.SubjectKey);
        command.Parameters.AddWithValue("$c", finding.Category);
        if (action == DecisionAction.EnableCategory)
        {
            command.CommandText = """
                DELETE FROM decisions WHERE repository=$r AND subject=$s AND category=$c
                AND action='disable-category'
                """;
        }
        else
        {
            command.CommandText = """
                INSERT INTO decisions(repository,subject,category,action,candidate,revision,until_time,recorded)
                VALUES($r,$s,$c,$a,$candidate,$revision,$until,$now)
                ON CONFLICT(repository,subject,category,action)
                DO UPDATE SET candidate=$candidate,revision=$revision,until_time=$until,recorded=$now
                """;
            command.Parameters.AddWithValue("$a", name);
            command.Parameters.AddWithValue("$candidate", (object?)finding.Candidate ?? DBNull.Value);
            command.Parameters.AddWithValue("$revision", finding.Revision);
            command.Parameters.AddWithValue("$until", until?.ToUnixTimeSeconds() is long epoch ? epoch : DBNull.Value);
            command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeSeconds());
        }
        command.ExecuteNonQuery();
        transaction.Commit();
        return new(Contract.Version, findingId, revision, action, clock.GetUtcNow(), until);
    }

    public bool TryLease(string repository, Finding finding, string owner, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        using var transaction = connection.BeginTransaction();
        if (Suppression(repository, finding) is not null) return false;
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (finding.Priority is Priority.Medium or Priority.Low)
        {
            command.CommandText = """
                SELECT COUNT(*) FROM deliveries WHERE repository=$r AND delivered IS NOT NULL
                AND (owner=$owner OR delivered>$day OR (subject=$s AND delivered>$week))
                """;
            command.Parameters.AddWithValue("$r", repository);
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$s", finding.SubjectKey);
            command.Parameters.AddWithValue("$day", now - 86400);
            command.Parameters.AddWithValue("$week", now - 7 * 86400);
            if (Convert.ToInt64(command.ExecuteScalar()) > 0) return false;
            command.Parameters.Clear();
        }
        command.CommandText = """
            INSERT INTO deliveries(repository,subject,revision,owner,lease_until,delivered)
            VALUES($r,$s,$v,$owner,$until,NULL)
            ON CONFLICT(repository,subject,revision) DO UPDATE
              SET owner=$owner,lease_until=$until
              WHERE deliveries.lease_until <= $now AND
                (deliveries.delivered IS NULL OR deliveries.delivered <= $retry)
            """;
        command.Parameters.AddWithValue("$r", repository);
        command.Parameters.AddWithValue("$s", finding.SubjectKey);
        command.Parameters.AddWithValue("$v", finding.Revision);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$until", now + (long)duration.TotalSeconds);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$retry", now - 7 * 86400);
        var acquired = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return acquired;
    }

    public void MarkDelivered(string repository, Finding finding, string owner)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE deliveries SET delivered=$now WHERE repository=$r AND subject=$s AND revision=$v
            AND owner=$owner AND lease_until>$now AND delivered IS NULL
            """;
        command.Parameters.AddWithValue("$r", repository);
        command.Parameters.AddWithValue("$s", finding.SubjectKey);
        command.Parameters.AddWithValue("$v", finding.Revision);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeSeconds());
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Delivery lease expired or owned by another session.");
    }

    public void Release(string owner)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE deliveries SET lease_until=0 WHERE owner=$owner AND delivered IS NULL";
        command.Parameters.AddWithValue("$owner", owner);
        command.ExecuteNonQuery();
    }

    public void QueueScan(string worktree)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scan_jobs(worktree,requested,status) VALUES($w,$now,'pending')
            ON CONFLICT(worktree) DO UPDATE SET requested=$now,status='pending'
            """;
        command.Parameters.AddWithValue("$w", worktree);
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    public (string Body, string? Etag, DateTimeOffset Refreshed)? Cached(string source)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body,etag,refreshed FROM metadata WHERE source=$source";
        command.Parameters.AddWithValue("$source", source);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2))) : null;
    }

    public void Cache(string source, string body, string? etag)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO metadata(source,body,etag,refreshed) VALUES($source,$body,$etag,$now)
            ON CONFLICT(source) DO UPDATE SET body=$body,etag=$etag,refreshed=$now
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$etag", (object?)etag ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    public void Dispose() => connection.Dispose();
}

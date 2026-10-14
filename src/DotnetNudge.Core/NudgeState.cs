using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DotnetNudge.Core;

public sealed class NudgeState : IDisposable
{
    const int StateSchemaVersion = 3;
    public string DirectoryPath { get; }
    readonly SqliteConnection connection;
    readonly TimeProvider clock;

    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable("NUDGE_STATE_DIR") is { Length: > 0 } path
            ? WorkspaceDiscovery.Canonical(path)
            : Environment.GetEnvironmentVariable("NUDGE_PLUGIN_DATA") is { Length: > 0 } pluginData
                ? WorkspaceDiscovery.Canonical(pluginData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dotnet-nudge");

    public NudgeState(string directory, TimeProvider? clock = null)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        this.clock = clock ?? TimeProvider.System;
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "state.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);
            INSERT INTO schema_version SELECT 3 WHERE NOT EXISTS (SELECT 1 FROM schema_version);
            """;
            command.ExecuteNonQuery();
            command.CommandText = "SELECT version FROM schema_version";
            var version = Convert.ToInt32(command.ExecuteScalar());
            if (version is not (1 or 2 or StateSchemaVersion))
                throw new InvalidDataException("Unsupported dotnet-nudge state schema; preserve the database and use a compatible version.");
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS workspace_snapshots (
              workspace TEXT PRIMARY KEY, worktree TEXT NOT NULL, repository TEXT NOT NULL,
              report TEXT NOT NULL, scanned INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS decisions (
              repository TEXT NOT NULL, subject TEXT NOT NULL, category TEXT NOT NULL,
              action TEXT NOT NULL, candidate TEXT, revision TEXT, until_time INTEGER,
              recorded INTEGER NOT NULL,
              PRIMARY KEY(repository, subject, category, action)
            );
            CREATE TABLE IF NOT EXISTS version_skips (
              repository TEXT NOT NULL, subject TEXT NOT NULL, category TEXT NOT NULL,
              candidate TEXT NOT NULL, revision TEXT NOT NULL, recorded INTEGER NOT NULL,
              PRIMARY KEY(repository, subject, category, candidate, revision)
            );
            CREATE TABLE IF NOT EXISTS deliveries (
              repository TEXT NOT NULL, subject TEXT NOT NULL, revision TEXT NOT NULL,
              owner TEXT NOT NULL, lease_until INTEGER NOT NULL, delivered INTEGER,
              PRIMARY KEY(repository, subject, revision)
            );
            CREATE TABLE IF NOT EXISTS metadata (
              source TEXT PRIMARY KEY, body TEXT NOT NULL, etag TEXT, refreshed INTEGER NOT NULL,
              fetched INTEGER
            );
            CREATE TABLE IF NOT EXISTS workspace_scan_jobs (
              workspace TEXT PRIMARY KEY, requested INTEGER NOT NULL, status TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS workspace_registration (
              workspace TEXT PRIMARY KEY, root TEXT NOT NULL, repository TEXT NOT NULL,
              worktree TEXT NOT NULL, last_seen INTEGER NOT NULL, active_until INTEGER NOT NULL,
              fingerprint TEXT
            );
            CREATE TABLE IF NOT EXISTS workspace_sessions (
              workspace TEXT NOT NULL, owner TEXT NOT NULL, active_until INTEGER NOT NULL,
              PRIMARY KEY(workspace,owner)
            );
            """;
            command.ExecuteNonQuery();
            if (version == 1) MigrateV1();
            AddJobColumn("owner", "TEXT");
            AddJobColumn("lease_until", "INTEGER NOT NULL DEFAULT 0");
            AddJobColumn("attempts", "INTEGER NOT NULL DEFAULT 0");
            AddJobColumn("error", "TEXT");
            AddJobColumn("next_retry", "INTEGER NOT NULL DEFAULT 0");
            AddJobColumn("stale", "INTEGER NOT NULL DEFAULT 0");
            using (var columns = connection.CreateCommand())
            {
                columns.CommandText = "PRAGMA table_info(metadata)";
                var hasFetched = false;
                using (var reader = columns.ExecuteReader())
                    while (reader.Read()) hasFetched |= reader.GetString(1) == "fetched";
                if (!hasFetched)
                {
                    columns.CommandText = "ALTER TABLE metadata ADD COLUMN fetched INTEGER";
                    columns.ExecuteNonQuery();
                }
                columns.CommandText = "UPDATE metadata SET fetched=refreshed WHERE fetched IS NULL";
                columns.ExecuteNonQuery();
            }
            using (var columns = connection.CreateCommand())
            {
                columns.CommandText = "PRAGMA table_info(workspace_registration)";
                using var reader = columns.ExecuteReader();
                var hasFingerprint = false;
                while (reader.Read())
                    hasFingerprint |= reader.GetString(1) == "fingerprint";
                reader.Close();
                if (!hasFingerprint)
                {
                    columns.CommandText = "ALTER TABLE workspace_registration ADD COLUMN fingerprint TEXT";
                    columns.ExecuteNonQuery();
                }
            }
            if (version < 3)
            {
                command.CommandText = """
                    INSERT OR IGNORE INTO workspace_registration(workspace,root,repository,worktree,last_seen,active_until)
                    SELECT workspace,json_extract(report,'$.workspace.root'),repository,worktree,scanned,0
                    FROM workspace_snapshots
                    WHERE json_valid(report) AND json_extract(report,'$.workspace.root') IS NOT NULL;
                    UPDATE schema_version SET version=3;
                    """;
                command.ExecuteNonQuery();
            }
            command.CommandText = """
                INSERT OR IGNORE INTO version_skips(repository,subject,category,candidate,revision,recorded)
                SELECT repository,subject,category,candidate,revision,recorded FROM decisions
                WHERE action='skip-version' AND candidate IS NOT NULL AND revision IS NOT NULL;
                DELETE FROM decisions WHERE action='skip-version'
                AND candidate IS NOT NULL AND revision IS NOT NULL;
                """;
            command.ExecuteNonQuery();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    void AddJobColumn(string name, string type)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(workspace_scan_jobs)";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (reader.GetString(1) == name) return;
        command.CommandText = $"ALTER TABLE workspace_scan_jobs ADD COLUMN {name} {type}";
        command.ExecuteNonQuery();
    }

    void MigrateV1()
    {
        using var transaction = connection.BeginTransaction();
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT length(CAST(report AS BLOB)), report, scanned FROM snapshots";
        using (var rows = read.ExecuteReader())
        {
            while (rows.Read())
            {
                if (rows.GetInt64(0) > 32 * 1024 * 1024)
                    throw new InvalidDataException("Legacy scan report exceeds 32 MiB; preserve state.");
                var report = JsonSerializer.Deserialize<ScanReport>(rows.GetString(1), Contract.Json)
                    ?? throw new InvalidDataException("Cannot migrate a null scan report.");
                var workspace = report.Workspace with
                {
                    WorkspaceId = WorkspaceDiscovery.ScopeId(report.Workspace.Root)
                };
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO workspace_snapshots(workspace,worktree,repository,report,scanned)
                    VALUES($s,$w,$r,$body,$time)
                    """;
                insert.Parameters.AddWithValue("$s", workspace.WorkspaceId);
                insert.Parameters.AddWithValue("$w", workspace.WorktreeId);
                insert.Parameters.AddWithValue("$r", workspace.RepositoryId);
                insert.Parameters.AddWithValue("$body", SerializeReport(report with { Workspace = workspace }));
                insert.Parameters.AddWithValue("$time", rows.GetInt64(2));
                insert.ExecuteNonQuery();
            }
        }
        using var jobs = connection.CreateCommand();
        jobs.Transaction = transaction;
        jobs.CommandText = """
            INSERT INTO workspace_scan_jobs(workspace,requested,status)
            SELECT COALESCE(s.workspace,j.worktree),j.requested,j.status FROM scan_jobs j
            LEFT JOIN workspace_snapshots s ON s.worktree=j.worktree
            """;
        jobs.ExecuteNonQuery();
        jobs.CommandText = "UPDATE schema_version SET version=2";
        jobs.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Save(ScanReport report)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO workspace_snapshots(workspace,worktree,repository,report,scanned)
            VALUES ($s,$w,$r,$body,$time)
            ON CONFLICT(workspace) DO UPDATE SET worktree=$w,repository=$r,report=$body,scanned=$time
            """;
        command.Parameters.AddWithValue("$s", report.Workspace.WorkspaceId);
        command.Parameters.AddWithValue("$w", report.Workspace.WorktreeId);
        command.Parameters.AddWithValue("$r", report.Workspace.RepositoryId);
        command.Parameters.AddWithValue("$body", SerializeReport(report));
        command.Parameters.AddWithValue("$time", report.ScannedAt.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
        command.CommandText = """
            DELETE FROM workspace_scan_jobs WHERE workspace=$s AND requested <= $time
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public ScanReport? Latest(string workspace)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT length(CAST(report AS BLOB)),report FROM workspace_snapshots WHERE workspace=$s";
        command.Parameters.AddWithValue("$s", workspace);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetInt64(0) > 32 * 1024 * 1024)
            throw new InvalidDataException("Cached scan report exceeds 32 MiB; preserve state and rescan.");
        return JsonSerializer.Deserialize<ScanReport>(reader.GetString(1), Contract.Json);
    }

    public StatusReport Status(string repository, string worktree, string workspace)
    {
        var report = Latest(workspace);
        using var job = connection.CreateCommand();
        job.CommandText = "SELECT status,error,stale FROM workspace_scan_jobs WHERE workspace=$s";
        job.Parameters.AddWithValue("$s", workspace);
        using var row = job.ExecuteReader();
        var pending = row.Read()
            ? new Coverage("scan-job", row.GetString(0) == "failed" ? Assessment.Blocked : Assessment.Partial,
                row.GetString(0) == "failed"
                    ? $"Scan failed: {(row.IsDBNull(1) ? "unknown error" : row.GetString(1))}; request a new scan."
                    : $"A {row.GetString(0)} scan is queued; start MCP or run scan explicitly.")
            : null;
        var stale = pending is not null && row.GetInt64(2) != 0;
        if (report is null)
            return new(Contract.Version, repository, worktree, workspace, null, [],
                pending is null
                    ? [new("scan", Assessment.Unknown, "No scan recorded; run scan to establish coverage.")]
                    : [new("scan", Assessment.Unknown, "No scan recorded; run scan to establish coverage."), pending]);
        return new(Contract.Version, repository, worktree, workspace, report.ScannedAt,
            report.Findings.Select(f =>
            {
                var reason = !stale ? Suppression(repository, f) ?? DeliverySuppression(repository, f)
                    : "scan-pending";
                return new FindingStatus(f, reason is null, reason);
            }).ToArray(), pending is null ? report.Coverage : stale
                ? report.Coverage.Append(new Coverage("snapshot", Assessment.Stale,
                    "A newer workspace scan is pending or failed; cached findings are not delivered."))
                    .Append(pending).ToArray()
                : report.Coverage.Append(pending).ToArray());
    }

    string? DeliverySuppression(string repository, Finding finding)
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT subject,revision,lease_until,delivered FROM deliveries
            WHERE repository=$r AND (subject=$s OR delivered>$day OR
              (delivered IS NULL AND lease_until>$now))
            """;
        command.Parameters.AddWithValue("$r", repository);
        command.Parameters.AddWithValue("$s", finding.SubjectKey);
        command.Parameters.AddWithValue("$day", now - 86400);
        command.Parameters.AddWithValue("$now", now);
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
            if (subject == finding.SubjectKey &&
                (delivered > now - 7 * 86400 || (delivered is null && leaseUntil > now)))
                sameSubject = true;
            if (delivered > now - 86400 || (delivered is null && leaseUntil > now))
                repositoryCooldown = true;
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
            UNION ALL
            SELECT subject, 'skip-version', candidate, revision, NULL FROM version_skips
            WHERE repository=$r AND subject=$s AND category=$c
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

    public DecisionResult Decide(string repository, string workspace, string findingId,
        string revision, DecisionAction action, DateTimeOffset? until)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT length(CAST(report AS BLOB)),report FROM workspace_snapshots
            WHERE workspace=$workspace AND repository=$r
            """;
        command.Parameters.AddWithValue("$workspace", workspace);
        command.Parameters.AddWithValue("$r", repository);
        ScanReport? report = null;
        using (var row = command.ExecuteReader())
            if (row.Read())
            {
                if (row.GetInt64(0) > 32 * 1024 * 1024)
                    throw new InvalidDataException("Cached scan report exceeds 32 MiB; preserve state and rescan.");
                report = JsonSerializer.Deserialize<ScanReport>(row.GetString(1), Contract.Json);
            }
        var finding = report?.Findings.SingleOrDefault(f => f.Id == findingId);
        if (finding is null || finding.Revision != revision)
            throw new InvalidOperationException("Finding missing or revision superseded; run status and decide on current evidence.");
        if (action == DecisionAction.Snooze && (until is null || until <= clock.GetUtcNow()))
            throw new ArgumentException("Snooze requires a future --until timestamp.");
        if (action != DecisionAction.Snooze && until is not null)
            throw new ArgumentException("--until is only valid for snooze.");
        if (action == DecisionAction.SkipVersion && finding.Candidate is null)
            throw new ArgumentException("Cannot skip a finding without a candidate version.");
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
        else if (action == DecisionAction.SkipVersion)
        {
            command.CommandText = """
                INSERT INTO version_skips(repository,subject,category,candidate,revision,recorded)
                VALUES($r,$s,$c,$candidate,$revision,$now)
                ON CONFLICT(repository,subject,category,candidate,revision)
                DO UPDATE SET recorded=$now
                """;
            command.Parameters.AddWithValue("$candidate", finding.Candidate!);
            command.Parameters.AddWithValue("$revision", finding.Revision);
            command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeSeconds());
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
                SELECT COUNT(*) FROM deliveries WHERE repository=$r
                AND (
                  (delivered IS NOT NULL AND
                    (owner=$owner OR delivered>$day OR (subject=$s AND delivered>$week)))
                  OR (delivered IS NULL AND lease_until>$now)
                )
                """;
            command.Parameters.AddWithValue("$r", repository);
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$s", finding.SubjectKey);
            command.Parameters.AddWithValue("$day", now - 86400);
            command.Parameters.AddWithValue("$week", now - 7 * 86400);
            command.Parameters.AddWithValue("$now", now);
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

    public void Register(string root, TimeSpan activeFor, string owner = "manual")
    {
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("Workspace admission requires an absolute root.", nameof(root));
        root = WorkspaceDiscovery.Canonical(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var (repository, worktree) = WorkspaceDiscovery.Identities(root);
        var workspace = WorkspaceDiscovery.ScopeId(root);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO workspace_registration(workspace,root,repository,worktree,last_seen,active_until)
            VALUES($s,$root,$repository,$worktree,$now,$active)
            ON CONFLICT(workspace) DO UPDATE SET root=$root,repository=$repository,
            worktree=$worktree,last_seen=$now,active_until=$active
            """;
        command.Parameters.AddWithValue("$s", workspace);
        command.Parameters.AddWithValue("$root", root);
        command.Parameters.AddWithValue("$repository", repository);
        command.Parameters.AddWithValue("$worktree", worktree);
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$active", clock.GetUtcNow().Add(activeFor).ToUnixTimeSeconds());
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO workspace_sessions(workspace,owner,active_until) VALUES($s,$owner,$active)
            ON CONFLICT(workspace,owner) DO UPDATE SET active_until=$active
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Close(string root, string owner = "manual")
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM workspace_sessions WHERE workspace=$s AND owner=$owner";
        command.Parameters.AddWithValue("$s", WorkspaceDiscovery.ScopeId(root));
        command.Parameters.AddWithValue("$owner", owner);
        command.ExecuteNonQuery();
    }

    public void QueueScan(string workspace)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workspace_scan_jobs(workspace,requested,status)
            SELECT $s,$now,'pending' WHERE NOT EXISTS (
              SELECT 1 FROM workspace_snapshots snapshot
              JOIN workspace_registration registration ON registration.workspace=snapshot.workspace
              WHERE snapshot.workspace=$s AND registration.fingerprint IS NOT NULL)
            ON CONFLICT(workspace) DO NOTHING
            """;
        command.Parameters.AddWithValue("$s", workspace);
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    public void RequestScan(string workspace)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workspace_scan_jobs(workspace,requested,status) VALUES($s,$now,'pending')
            ON CONFLICT(workspace) DO UPDATE SET status='pending',requested=$now,
            attempts=0,error=NULL,next_retry=0,stale=1 WHERE status='failed'
            """;
        command.Parameters.AddWithValue("$s", workspace);
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    public void Reconcile()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT workspace,root,fingerprint FROM workspace_registration r WHERE EXISTS
            (SELECT 1 FROM workspace_sessions s WHERE s.workspace=r.workspace AND s.active_until>$now)
            """;
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeSeconds());
        var registered = new List<(string Workspace, string Root, string? Fingerprint)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                registered.Add((reader.GetString(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
        foreach (var (workspace, root, previous) in registered)
        {
            if (!Directory.Exists(root) || WorkspaceDiscovery.ScopeId(root) != workspace) continue;
            var current = WorkspaceFingerprint(root);
            if (current == previous) continue;
            using var transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            command.Parameters.Clear();
            command.CommandText = """
                UPDATE workspace_registration SET fingerprint=$fingerprint WHERE workspace=$s
                AND (fingerprint=$previous OR (fingerprint IS NULL AND $previous IS NULL))
                """;
            command.Parameters.AddWithValue("$s", workspace);
            command.Parameters.AddWithValue("$fingerprint", current);
            command.Parameters.AddWithValue("$previous", (object?)previous ?? DBNull.Value);
            if (command.ExecuteNonQuery() == 1)
            {
                command.CommandText = """
                    INSERT INTO workspace_scan_jobs(workspace,requested,status,stale)
                    VALUES($s,$now,'pending',1)
                    ON CONFLICT(workspace) DO UPDATE SET
                    requested=MAX(workspace_scan_jobs.requested+1,$now),
                    status=CASE WHEN status='running' THEN 'running' ELSE 'pending' END,
                    attempts=CASE WHEN status='running' THEN attempts ELSE 0 END,
                    error=NULL,next_retry=0,stale=1
                    """;
                command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeSeconds());
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public static string WorkspaceFingerprint(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        void Walk(string directory, int depth)
        {
            if (depth > 20) return;
            foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                if (++count > 10000) throw new InvalidDataException("Workspace fingerprint exceeds 10,000 files.");
                var name = Path.GetFileName(path);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                    !(name is "global.json" or "project.assets.json" or "packages.lock.json"
                    or "Directory.Packages.props" or "NuGet.Config" or "nuget.config"
                    or "dotnet-tools.json" or "Directory.Build.props" or "Directory.Build.targets"
                    or "HEAD" or "index"
                    || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))) continue;
                var info = new FileInfo(path);
                var line = $"{Path.GetRelativePath(root, path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}\n";
                hash.AppendData(Encoding.UTF8.GetBytes(line));
            }
            foreach (var child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                var name = Path.GetFileName(child);
                if (name is "node_modules" or "bin" or ".vs" or ".idea" or "vendor")
                    continue;
                if (name == ".git")
                {
                    foreach (var gitMetadata in new[] { "HEAD", "index" })
                    {
                        var file = Path.Combine(child, gitMetadata);
                        if (!File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                            continue;
                        var info = new FileInfo(file);
                        hash.AppendData(Encoding.UTF8.GetBytes(
                            $".git/{gitMetadata}|{info.Length}|{info.LastWriteTimeUtc.Ticks}\n"));
                    }
                    continue;
                }
                Walk(child, depth + 1);
            }
        }
        Walk(root, 0);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public string? AdmittedRoot(string workspace)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT root FROM workspace_registration WHERE workspace=$s";
        command.Parameters.AddWithValue("$s", workspace);
        if (command.ExecuteScalar() is not string root) return null;
        return Directory.Exists(root) && WorkspaceDiscovery.ScopeId(root) == workspace ? root : null;
    }

    public ScanJob? LeaseScan(string owner, string? workspace = null)
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT j.workspace,r.root,j.requested,j.attempts,r.fingerprint
            FROM workspace_scan_jobs j
            JOIN workspace_registration r ON r.workspace=j.workspace
            WHERE EXISTS(SELECT 1 FROM workspace_sessions s
              WHERE s.workspace=r.workspace AND s.active_until>$now)
            AND ($scope IS NULL OR j.workspace=$scope)
            AND j.next_retry<=$now AND j.attempts<3
            AND (j.status='pending' OR (j.status='running' AND j.lease_until<=$now))
            ORDER BY j.requested LIMIT 1
            """;
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$scope", (object?)workspace ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        ScanJob? job = reader.Read()
            ? new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3) + 1,
                reader.IsDBNull(4) ? null : reader.GetString(4))
            : null;
        reader.Close();
        if (job is null) return null;
        command.CommandText = """
            UPDATE workspace_scan_jobs SET status='running',owner=$owner,lease_until=$until,
            attempts=attempts+1 WHERE workspace=$s
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$until", now + 120);
        command.Parameters.AddWithValue("$s", job.Workspace);
        command.ExecuteNonQuery();
        transaction.Commit();
        return job;
    }

    public void FinishScan(ScanJob job, string owner, ScanReport? report, string? error,
        string? inputFingerprint = null)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM workspace_scan_jobs WHERE workspace=$s AND owner=$owner
            AND status='running' AND lease_until>$now
            """;
        command.Parameters.AddWithValue("$s", job.Workspace);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeSeconds());
        if (Convert.ToInt32(command.ExecuteScalar()) != 1)
            throw new InvalidOperationException("Scan lease expired or belongs to another worker.");
        if (report is not null)
        {
            if (report.Workspace.WorkspaceId != job.Workspace || report.Workspace.Root != job.Root)
                throw new InvalidDataException("Scan result does not match admitted scope.");
            command.CommandText = """
                INSERT INTO workspace_snapshots(workspace,worktree,repository,report,scanned)
                VALUES($s,$worktree,$repository,$body,$scanned)
                ON CONFLICT(workspace) DO UPDATE SET worktree=$worktree,repository=$repository,
                report=$body,scanned=$scanned
                """;
            command.Parameters.AddWithValue("$worktree", report.Workspace.WorktreeId);
            command.Parameters.AddWithValue("$repository", report.Workspace.RepositoryId);
            command.Parameters.AddWithValue("$body", SerializeReport(report));
            command.Parameters.AddWithValue("$scanned", report.ScannedAt.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
            if (inputFingerprint is not null)
            {
                command.CommandText = """
                    UPDATE workspace_registration SET fingerprint=$fingerprint
                    WHERE workspace=$s AND
                    (fingerprint=$previous OR (fingerprint IS NULL AND $previous IS NULL))
                    """;
                command.Parameters.AddWithValue("$fingerprint", inputFingerprint);
                command.Parameters.AddWithValue("$previous",
                    (object?)job.PreviousFingerprint ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }
        command.CommandText = report is not null
            ? "DELETE FROM workspace_scan_jobs WHERE workspace=$s AND owner=$owner AND requested<=$requested"
            : """
              UPDATE workspace_scan_jobs SET status=$status,error=$error,
              next_retry=$retry,owner=NULL,lease_until=0,stale=1 WHERE workspace=$s AND owner=$owner
              """;
        command.Parameters.AddWithValue("$requested", job.Requested);
        if (report is null)
        {
            command.Parameters.AddWithValue("$status", job.Attempts >= 3 ? "failed" : "pending");
            command.Parameters.AddWithValue("$error", error is { Length: > 500 } ? error[..500] : error ?? "Scan failed.");
            command.Parameters.AddWithValue("$retry", clock.GetUtcNow().AddSeconds(30 * job.Attempts).ToUnixTimeSeconds());
        }
        command.ExecuteNonQuery();
        if (report is not null)
        {
            command.CommandText = """
                UPDATE workspace_scan_jobs SET status='pending',owner=NULL,lease_until=0,attempts=0
                WHERE workspace=$s AND owner=$owner AND requested>$requested
                """;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public (string Body, string? Etag, DateTimeOffset Refreshed, DateTimeOffset Fetched)? Cached(string source)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body,etag,refreshed,fetched FROM metadata WHERE source=$source";
        command.Parameters.AddWithValue("$source", source);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3))) : null;
    }

    public sealed record ScanJob(string Workspace, string Root, long Requested, int Attempts,
        string? PreviousFingerprint);

    static string SerializeReport(ScanReport report)
    {
        var body = JsonSerializer.Serialize(report, Contract.Json);
        if (Encoding.UTF8.GetByteCount(body) > 32 * 1024 * 1024)
            throw new InvalidDataException("Scan report exceeds 32 MiB; narrow the admitted workspace.");
        return body;
    }

    public void Cache(string source, string body, string? etag, bool revalidated = false)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO metadata(source,body,etag,refreshed,fetched)
            VALUES($source,$body,$etag,$now,$now)
            ON CONFLICT(source) DO UPDATE SET body=$body,etag=$etag,refreshed=$now,
            fetched=CASE WHEN $revalidated=1 THEN metadata.fetched ELSE $now END
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$etag", (object?)etag ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$revalidated", revalidated ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public void Dispose() => connection.Dispose();
}

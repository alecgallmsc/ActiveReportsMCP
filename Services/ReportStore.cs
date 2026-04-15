using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RdlxMcpServer.Models;

namespace RdlxMcpServer.Services;

public sealed class ReportStore
{
    private readonly ConcurrentDictionary<string, ReportRecord> _reports = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ReportStore> _logger;
    private readonly string _rootPath;

    public ReportStore(ILogger<ReportStore> logger)
    {
        _logger = logger;
        _rootPath = Environment.GetEnvironmentVariable("RDLX_STORE_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "data", "reports");
        Directory.CreateDirectory(_rootPath);
    }

    public (ReportRecord Report, ReportVersionRecord Version) CreateReport(
        string name,
        string reportType,
        string canonicalRdlx,
        string canonicalHash,
        string createdBy,
        string reason)
    {
        var reportId = $"rpt_{Guid.NewGuid().ToString("N")[..8]}";
        var report = new ReportRecord
        {
            ReportId = reportId,
            Name = name,
            ReportType = reportType,
            NextVersion = 1
        };

        var version = CreateVersionInternal(report, canonicalRdlx, canonicalHash, createdBy, reason);
        _reports[reportId] = report;
        PersistManifest(report);
        return (report, version);
    }

    public bool TryGetVersion(string reportId, string versionId, out ReportRecord? report, out ReportVersionRecord? version)
    {
        report = null;
        version = null;

        if (!_reports.TryGetValue(reportId, out var foundReport))
        {
            return false;
        }

        lock (foundReport.SyncRoot)
        {
            if (!foundReport.Versions.TryGetValue(versionId, out var foundVersion))
            {
                return false;
            }

            report = foundReport;
            version = foundVersion;
            return true;
        }
    }

    public bool TryGetLatestVersion(string reportId, out ReportRecord? report, out ReportVersionRecord? version)
    {
        report = null;
        version = null;

        if (!_reports.TryGetValue(reportId, out var foundReport))
        {
            return false;
        }

        lock (foundReport.SyncRoot)
        {
            if (foundReport.Versions.Count == 0)
            {
                return false;
            }

            var latest = foundReport.Versions.Values
                .OrderByDescending(v => v.CreatedAtUtc)
                .First();

            report = foundReport;
            version = latest;
            return true;
        }
    }

    public bool IsLatestVersion(ReportRecord report, string versionId)
    {
        lock (report.SyncRoot)
        {
            var latest = report.Versions.Values
                .OrderByDescending(v => v.CreatedAtUtc)
                .FirstOrDefault();
            return latest is not null && string.Equals(latest.VersionId, versionId, StringComparison.OrdinalIgnoreCase);
        }
    }

    public ReportVersionRecord CreateVersionFromBase(
        string reportId,
        string baseVersionId,
        string canonicalRdlx,
        string canonicalHash,
        string createdBy,
        string reason,
        bool requireLatest = true)
    {
        if (!_reports.TryGetValue(reportId, out var report))
        {
            throw new KeyNotFoundException($"Unknown reportId '{reportId}'.");
        }

        lock (report.SyncRoot)
        {
            if (!report.Versions.ContainsKey(baseVersionId))
            {
                throw new KeyNotFoundException($"Unknown baseVersionId '{baseVersionId}' for report '{reportId}'.");
            }

            if (requireLatest && !IsLatestVersion(report, baseVersionId))
            {
                throw new InvalidOperationException("CONFLICT_ERROR: baseVersionId is not the latest version.");
            }

            var created = CreateVersionInternal(report, canonicalRdlx, canonicalHash, createdBy, reason);
            PersistManifest(report);
            return created;
        }
    }

    public void MarkSaved(string reportId, string versionId, string? saveComment)
    {
        if (!_reports.TryGetValue(reportId, out var report))
        {
            return;
        }

        lock (report.SyncRoot)
        {
            if (!report.Versions.TryGetValue(versionId, out var version))
            {
                return;
            }

            version.IsSaved = true;
            version.SaveComment = saveComment;
            PersistManifest(report);
        }
    }

    public string GetVersionFilePath(string reportId, string versionId)
    {
        return Path.Combine(_rootPath, reportId, $"{versionId}.rdlx");
    }

    private ReportVersionRecord CreateVersionInternal(
        ReportRecord report,
        string canonicalRdlx,
        string canonicalHash,
        string createdBy,
        string reason)
    {
        var versionId = $"v_{report.NextVersion:D4}";
        report.NextVersion += 1;

        var version = new ReportVersionRecord
        {
            VersionId = versionId,
            Rdlx = canonicalRdlx,
            CanonicalHash = canonicalHash,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBy = createdBy,
            Reason = reason,
            IsSaved = false
        };

        report.Versions[versionId] = version;
        PersistVersion(report.ReportId, version);
        _logger.LogInformation("Stored report {ReportId} version {VersionId}", report.ReportId, versionId);
        return version;
    }

    private void PersistVersion(string reportId, ReportVersionRecord version)
    {
        var reportDir = Path.Combine(_rootPath, reportId);
        Directory.CreateDirectory(reportDir);

        var versionPath = GetVersionFilePath(reportId, version.VersionId);
        File.WriteAllText(versionPath, version.Rdlx);
    }

    private void PersistManifest(ReportRecord report)
    {
        var reportDir = Path.Combine(_rootPath, report.ReportId);
        Directory.CreateDirectory(reportDir);

        var manifestPath = Path.Combine(reportDir, "manifest.json");
        var payload = new
        {
            report.ReportId,
            report.Name,
            report.ReportType,
            Versions = report.Versions.Values
                .OrderBy(v => v.VersionId)
                .Select(v => new
                {
                    v.VersionId,
                    v.CanonicalHash,
                    v.CreatedAtUtc,
                    v.CreatedBy,
                    v.Reason,
                    v.IsSaved,
                    v.SaveComment
                })
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(manifestPath, json);
    }
}

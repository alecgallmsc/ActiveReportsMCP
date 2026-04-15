using GrapeCity.ActiveReports;
using GrapeCity.ActiveReports.PageReportModel;
using Microsoft.Extensions.Logging;
using RdlxMcpServer.Models;

namespace RdlxMcpServer.Services;

public sealed class RuntimeVerificationService
{
    private readonly ILogger<RuntimeVerificationService> _logger;

    public RuntimeVerificationService(ILogger<RuntimeVerificationService> logger)
    {
        _logger = logger;
    }

    public RuntimeVerificationReport Verify(string reportPath, string? requestedMode)
    {
        var mode = NormalizeMode(requestedMode);
        var diagnostics = new List<DiagnosticEntry>();

        if (!File.Exists(reportPath))
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "runtime",
                Severity = "Error",
                Code = "RUNTIME_LOAD_ERROR",
                Message = $"Report path not found: {reportPath}"
            });

            return CreateResult(mode, "none", diagnostics);
        }

        PageReport? pageReport = null;

        try
        {
            pageReport = new PageReport(new FileInfo(reportPath));
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "runtime",
                Severity = "Information",
                Code = "RUNTIME_LOAD_OK",
                Message = "PageReport loaded successfully through ActiveReports package API."
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load report for runtime verification: {ReportPath}", reportPath);
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "runtime",
                Severity = "Error",
                Code = ClassifyLoadErrorCode(ex),
                Message = ex.Message
            });

            return CreateResult(mode, "load_only", diagnostics);
        }

        if (mode is "validate" or "full")
        {
            RunValidation(pageReport, diagnostics);
        }

        if (mode is "run_smoke" or "full")
        {
            RunSmoke(pageReport, diagnostics);
        }

        var coverage = mode switch
        {
            "full" => "load+validate+run",
            "validate" => "load+validate",
            "run_smoke" => "load+run",
            _ => "load_only"
        };

        return CreateResult(mode, coverage, diagnostics);
    }

    private static string NormalizeMode(string? requestedMode)
    {
        if (string.IsNullOrWhiteSpace(requestedMode))
        {
            return "load_only";
        }

        var normalized = requestedMode.Trim().ToLowerInvariant();
        return normalized is "load_only" or "validate" or "run_smoke" or "full"
            ? normalized
            : "load_only";
    }

    private static void RunValidation(PageReport pageReport, List<DiagnosticEntry> diagnostics)
    {
        try
        {
            var context = new ValidationContext(ValidationMode.BeforeProcessing);
            var entries = pageReport.Report.Validate(context);
            if (entries is null || entries.Length == 0)
            {
                diagnostics.Add(new DiagnosticEntry
                {
                    Stage = "runtime",
                    Severity = "Information",
                    Code = "RUNTIME_VALIDATE_OK",
                    Message = "No runtime validation entries were returned."
                });
                return;
            }

            diagnostics.AddRange(entries.Select(entry => new DiagnosticEntry
            {
                Stage = "runtime",
                Severity = MapSeverity(entry.Severity),
                Code = "RUNTIME_VALIDATION_ENTRY",
                Message = entry.Message,
                Owner = entry.Owner?.ToString()
            }));
        }
        catch (Exception ex)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "runtime",
                Severity = "Error",
                Code = "RUNTIME_VALIDATE_ERROR",
                Message = ex.Message
            });
        }
    }

    private static void RunSmoke(PageReport pageReport, List<DiagnosticEntry> diagnostics)
    {
        try
        {
            pageReport.Run();
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "runtime",
                Severity = "Information",
                Code = "RUNTIME_RUN_OK",
                Message = "PageReport.Run() completed successfully."
            });
        }
        catch (Exception ex)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "runtime",
                Severity = "Error",
                Code = "RUNTIME_RUN_ERROR",
                Message = ex.Message
            });
        }
    }

    private static RuntimeVerificationReport CreateResult(
        string mode,
        string coverage,
        List<DiagnosticEntry> diagnostics)
    {
        return new RuntimeVerificationReport
        {
            Success = diagnostics.All(d => !string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase)),
            Mode = mode,
            Coverage = coverage,
            Diagnostics = diagnostics
        };
    }

    private static string MapSeverity(Severity severity)
    {
        return severity switch
        {
            Severity.Error => "Error",
            Severity.Warning => "Warning",
            _ => "Information"
        };
    }

    private static string ClassifyLoadErrorCode(Exception ex)
    {
        if (ex is ReportException)
        {
            return "RUNTIME_LOAD_ERROR";
        }

        if (ex.GetType().Name.Contains("License", StringComparison.OrdinalIgnoreCase))
        {
            return "RUNTIME_VERIFY_UNAVAILABLE";
        }

        return "RUNTIME_LOAD_ERROR";
    }
}

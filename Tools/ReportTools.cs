using System.ComponentModel;
using ModelContextProtocol.Server;
using RdlxMcpServer.Models;
using RdlxMcpServer.Services;

namespace RdlxMcpServer.Tools;

[McpServerToolType]
public sealed class ReportTools
{
    [McpServerTool, Description("Create a new RDLX report from a template path or generated skeleton.")]
    public static ToolResult report_create(
        ReportStore store,
        RdlxDocumentService documents,
        RdlxValidationService validation,
        [Description("Human-friendly report name.")] string name,
        [Description("Report type label, e.g. PageReport.")] string reportType = "PageReport",
        [Description("Optional path to existing .rdlx template.")] string? templatePath = null,
        [Description("Optional page settings map: PageWidth, PageHeight, Margin.")] Dictionary<string, string>? pageSettings = null,
        [Description("Author or actor identifier for audit trail.")] string createdBy = "mcp-user")
    {
        try
        {
            var baseRdlx = string.IsNullOrWhiteSpace(templatePath)
                ? documents.CreateSkeleton(name, reportType, pageSettings)
                : File.ReadAllText(templatePath);

            var canonical = documents.Canonicalize(baseRdlx);
            var hash = RdlxDocumentService.ComputeHash(canonical);

            var created = store.CreateReport(name, reportType, canonical, hash, createdBy, "create");
            var report = validation.Validate(canonical, ValidationLevel.Full);

            return new ToolResult
            {
                Ok = report.BlockingCount == 0,
                Message = "Report created.",
                ReportId = created.Report.ReportId,
                VersionId = created.Version.VersionId,
                Diagnostics = report.Diagnostics,
                Artifacts = new Dictionary<string, object?>
                {
                    ["canonicalHash"] = created.Version.CanonicalHash,
                    ["filePath"] = store.GetVersionFilePath(created.Report.ReportId, created.Version.VersionId),
                    ["validation"] = new
                    {
                        report.BlockingCount,
                        report.WarningsCount,
                        report.ConfidenceScore
                    }
                }
            };
        }
        catch (Exception ex)
        {
            return ErrorResult("create", ex.Message, "CREATE_ERROR");
        }
    }

    [McpServerTool, Description("Return a normalized report structure tree.")]
    public static ToolResult report_get_structure(
        ReportStore store,
        RdlxDocumentService documents,
        [Description("Report identifier.")] string reportId,
        [Description("Specific version identifier. If omitted, latest version is used.")] string? versionId = null)
    {
        var located = GetVersion(store, reportId, versionId);
        if (!located.Ok)
        {
            return located.Result;
        }

        var structure = documents.BuildStructure(located.Version!.Rdlx);
        return new ToolResult
        {
            Ok = true,
            Message = "Report structure loaded.",
            ReportId = reportId,
            VersionId = located.Version.VersionId,
            Artifacts = new Dictionary<string, object?>
            {
                ["structure"] = structure
            }
        };
    }

    [McpServerTool, Description("Apply layout patch operations and create a new version.")]
    public static ToolResult report_patch_layout(
        ReportStore store,
        RdlxDocumentService documents,
        RdlxValidationService validation,
        [Description("Report identifier.")] string reportId,
        [Description("Current base version identifier.")] string baseVersionId,
        [Description("Layout operations to apply.")] List<LayoutOperation> operations,
        [Description("Author or actor identifier for audit trail.")] string createdBy = "mcp-user")
    {
        if (!store.TryGetVersion(reportId, baseVersionId, out var report, out var baseVersion) || report is null || baseVersion is null)
        {
            return ErrorResult(reportId, "Unknown reportId/baseVersionId combination.", "NOT_FOUND");
        }

        if (!store.IsLatestVersion(report, baseVersionId))
        {
            return ErrorResult(reportId, "CONFLICT_ERROR: baseVersionId is not the latest version.", "CONFLICT_ERROR");
        }

        try
        {
            var patch = documents.ApplyLayoutOperations(baseVersion.Rdlx, operations);
            var canonical = documents.Canonicalize(patch.Rdlx);
            var hash = RdlxDocumentService.ComputeHash(canonical);
            var created = store.CreateVersionFromBase(reportId, baseVersionId, canonical, hash, createdBy, "layout_patch");
            var validationReport = validation.Validate(canonical, ValidationLevel.Full);

            var diagnostics = patch.Diagnostics
                .Concat(validationReport.Diagnostics)
                .ToList();

            return new ToolResult
            {
                Ok = validationReport.BlockingCount == 0,
                Message = "Layout patch applied.",
                ReportId = reportId,
                VersionId = created.VersionId,
                Diagnostics = diagnostics,
                Artifacts = new Dictionary<string, object?>
                {
                    ["canonicalHash"] = hash,
                    ["filePath"] = store.GetVersionFilePath(reportId, created.VersionId),
                    ["validation"] = new
                    {
                        validationReport.BlockingCount,
                        validationReport.WarningsCount,
                        validationReport.ConfidenceScore
                    }
                }
            };
        }
        catch (Exception ex)
        {
            return ErrorResult(reportId, ex.Message, "PATCH_ERROR");
        }
    }

    [McpServerTool, Description("Apply data source/dataset/parameter patch operations and create a new version.")]
    public static ToolResult report_patch_data(
        ReportStore store,
        RdlxDocumentService documents,
        RdlxValidationService validation,
        [Description("Report identifier.")] string reportId,
        [Description("Current base version identifier.")] string baseVersionId,
        [Description("Data operations to apply.")] List<DataOperation> dataOps,
        [Description("Author or actor identifier for audit trail.")] string createdBy = "mcp-user")
    {
        if (!store.TryGetVersion(reportId, baseVersionId, out var report, out var baseVersion) || report is null || baseVersion is null)
        {
            return ErrorResult(reportId, "Unknown reportId/baseVersionId combination.", "NOT_FOUND");
        }

        if (!store.IsLatestVersion(report, baseVersionId))
        {
            return ErrorResult(reportId, "CONFLICT_ERROR: baseVersionId is not the latest version.", "CONFLICT_ERROR");
        }

        try
        {
            var patch = documents.ApplyDataOperations(baseVersion.Rdlx, dataOps);
            var canonical = documents.Canonicalize(patch.Rdlx);
            var hash = RdlxDocumentService.ComputeHash(canonical);
            var created = store.CreateVersionFromBase(reportId, baseVersionId, canonical, hash, createdBy, "data_patch");
            var validationReport = validation.Validate(canonical, ValidationLevel.Full);

            var diagnostics = patch.Diagnostics
                .Concat(validationReport.Diagnostics)
                .ToList();

            return new ToolResult
            {
                Ok = validationReport.BlockingCount == 0,
                Message = "Data patch applied.",
                ReportId = reportId,
                VersionId = created.VersionId,
                Diagnostics = diagnostics,
                Artifacts = new Dictionary<string, object?>
                {
                    ["canonicalHash"] = hash,
                    ["filePath"] = store.GetVersionFilePath(reportId, created.VersionId),
                    ["validation"] = new
                    {
                        validationReport.BlockingCount,
                        validationReport.WarningsCount,
                        validationReport.ConfidenceScore
                    }
                }
            };
        }
        catch (Exception ex)
        {
            return ErrorResult(reportId, ex.Message, "PATCH_ERROR");
        }
    }

    [McpServerTool, Description("Applies style tokens and per-control styling operations.")]
    public static ToolResult report_patch_style(
        ReportStore store,
        RdlxDocumentService documents,
        RdlxValidationService validation,
        LayoutIntelligenceService layoutIntelligence,
        [Description("Report identifier.")] string reportId,
        [Description("Current base version identifier.")] string baseVersionId,
        [Description("Targets to style (targetRef or selector).")]
        List<StyleTarget> targets,
        [Description("Style operations (property/value pairs).")]
        List<StyleOperation> styleOps,
        [Description("Author or actor identifier for audit trail.")] string createdBy = "mcp-user")
    {
        if (!store.TryGetVersion(reportId, baseVersionId, out var report, out var baseVersion) || report is null || baseVersion is null)
        {
            return ErrorResult(reportId, "Unknown reportId/baseVersionId combination.", "NOT_FOUND");
        }

        if (!store.IsLatestVersion(report, baseVersionId))
        {
            return ErrorResult(reportId, "CONFLICT_ERROR: baseVersionId is not the latest version.", "CONFLICT_ERROR");
        }

        try
        {
            var patch = layoutIntelligence.ApplyStylePatch(baseVersion.Rdlx, targets, styleOps);
            var canonical = documents.Canonicalize(patch.Rdlx);
            var hash = RdlxDocumentService.ComputeHash(canonical);
            var created = store.CreateVersionFromBase(reportId, baseVersionId, canonical, hash, createdBy, "style_patch");
            var validationReport = validation.Validate(canonical, ValidationLevel.Full);

            var diagnostics = patch.Diagnostics.Concat(validationReport.Diagnostics).ToList();
            return new ToolResult
            {
                Ok = validationReport.BlockingCount == 0,
                Message = "Style patch applied.",
                ReportId = reportId,
                VersionId = created.VersionId,
                Diagnostics = diagnostics,
                Artifacts = new Dictionary<string, object?>
                {
                    ["canonicalHash"] = hash,
                    ["filePath"] = store.GetVersionFilePath(reportId, created.VersionId),
                    ["appliedTargets"] = targets.Count,
                    ["appliedStyleOps"] = styleOps.Count
                }
            };
        }
        catch (Exception ex)
        {
            return ErrorResult(reportId, ex.Message, "STYLE_PATCH_ERROR");
        }
    }

    [McpServerTool, Description("Applies value formatting patterns by field references.")]
    public static ToolResult report_patch_formatting(
        ReportStore store,
        RdlxDocumentService documents,
        RdlxValidationService validation,
        LayoutIntelligenceService layoutIntelligence,
        [Description("Report identifier.")] string reportId,
        [Description("Current base version identifier.")] string baseVersionId,
        [Description("Formatting rules by field reference.")] List<FormatRule> formatRules,
        [Description("Author or actor identifier for audit trail.")] string createdBy = "mcp-user")
    {
        if (!store.TryGetVersion(reportId, baseVersionId, out var report, out var baseVersion) || report is null || baseVersion is null)
        {
            return ErrorResult(reportId, "Unknown reportId/baseVersionId combination.", "NOT_FOUND");
        }

        if (!store.IsLatestVersion(report, baseVersionId))
        {
            return ErrorResult(reportId, "CONFLICT_ERROR: baseVersionId is not the latest version.", "CONFLICT_ERROR");
        }

        try
        {
            var patch = layoutIntelligence.ApplyFormattingPatch(baseVersion.Rdlx, formatRules);
            var canonical = documents.Canonicalize(patch.Rdlx);
            var hash = RdlxDocumentService.ComputeHash(canonical);
            var created = store.CreateVersionFromBase(reportId, baseVersionId, canonical, hash, createdBy, "formatting_patch");
            var validationReport = validation.Validate(canonical, ValidationLevel.Full);

            var diagnostics = patch.Diagnostics.Concat(validationReport.Diagnostics).ToList();
            return new ToolResult
            {
                Ok = validationReport.BlockingCount == 0,
                Message = "Formatting patch applied.",
                ReportId = reportId,
                VersionId = created.VersionId,
                Diagnostics = diagnostics,
                Artifacts = new Dictionary<string, object?>
                {
                    ["canonicalHash"] = hash,
                    ["filePath"] = store.GetVersionFilePath(reportId, created.VersionId),
                    ["appliedFormatRules"] = formatRules.Count
                }
            };
        }
        catch (Exception ex)
        {
            return ErrorResult(reportId, ex.Message, "FORMATTING_PATCH_ERROR");
        }
    }

    [McpServerTool, Description("Returns a normalized, geometry-focused layout model for AI reasoning.")]
    public static ToolResult report_extract_layout_model(
        ReportStore store,
        LayoutIntelligenceService layoutIntelligence,
        [Description("Report identifier.")] string reportId,
        [Description("Version identifier.")] string versionId)
    {
        if (!store.TryGetVersion(reportId, versionId, out _, out var version) || version is null)
        {
            return ErrorResult(reportId, "Unknown reportId/versionId combination.", "NOT_FOUND");
        }

        var model = layoutIntelligence.ExtractLayoutModel(version.Rdlx);
        return new ToolResult
        {
            Ok = true,
            Message = "Layout model extracted.",
            ReportId = reportId,
            VersionId = versionId,
            Artifacts = new Dictionary<string, object?>
            {
                ["layoutModel"] = model,
                ["controlCount"] = model.Controls.Count,
                ["alignmentGroupCount"] = model.AlignmentGroups.Count
            }
        };
    }

    [McpServerTool, Description("Scores layout quality using XML-first geometry and semantic heuristics.")]
    public static ToolResult report_layout_score(
        ReportStore store,
        LayoutIntelligenceService layoutIntelligence,
        [Description("Report identifier.")] string reportId,
        [Description("Version identifier.")] string versionId,
        [Description("Optional rule-pack version marker.")] string? rulePackVersion = null)
    {
        if (!store.TryGetVersion(reportId, versionId, out _, out var version) || version is null)
        {
            return ErrorResult(reportId, "Unknown reportId/versionId combination.", "NOT_FOUND");
        }

        var score = layoutIntelligence.ScoreLayout(version.Rdlx);
        return new ToolResult
        {
            Ok = score.Score >= 80,
            Message = "Layout score generated.",
            ReportId = reportId,
            VersionId = versionId,
            Diagnostics = score.Issues.Select(issue => new DiagnosticEntry
            {
                Stage = "layout",
                Severity = issue.Severity,
                Code = issue.IssueCode,
                Message = issue.Message,
                Owner = issue.Targets.FirstOrDefault()
            }).ToList(),
            Artifacts = new Dictionary<string, object?>
            {
                ["rulePackVersion"] = rulePackVersion ?? "layout-v1.1",
                ["layoutScore"] = score
            }
        };
    }

    [McpServerTool, Description("Runs bounded deterministic auto-refinement to improve layout score.")]
    public static ToolResult report_auto_refine_layout(
        ReportStore store,
        RdlxDocumentService documents,
        RdlxValidationService validation,
        LayoutIntelligenceService layoutIntelligence,
        [Description("Report identifier.")] string reportId,
        [Description("Current base version identifier.")] string baseVersionId,
        [Description("Maximum refinement iterations.")] int maxIterations = 3,
        [Description("Target layout score threshold.")] int targetScore = 80,
        [Description("Author or actor identifier for audit trail.")] string createdBy = "mcp-user")
    {
        if (!store.TryGetVersion(reportId, baseVersionId, out var report, out var baseVersion) || report is null || baseVersion is null)
        {
            return ErrorResult(reportId, "Unknown reportId/baseVersionId combination.", "NOT_FOUND");
        }

        if (!store.IsLatestVersion(report, baseVersionId))
        {
            return ErrorResult(reportId, "CONFLICT_ERROR: baseVersionId is not the latest version.", "CONFLICT_ERROR");
        }

        try
        {
            var boundedIterations = Math.Clamp(maxIterations, 1, 10);
            var boundedTarget = Math.Clamp(targetScore, 40, 100);
            var refined = layoutIntelligence.AutoRefineLayout(baseVersion.Rdlx, boundedIterations, boundedTarget);

            var canonical = documents.Canonicalize(refined.Rdlx);
            var hash = RdlxDocumentService.ComputeHash(canonical);
            var created = store.CreateVersionFromBase(reportId, baseVersionId, canonical, hash, createdBy, "auto_refine_layout");
            var validationReport = validation.Validate(canonical, ValidationLevel.Full);

            var diagnostics = refined.Diagnostics.Concat(validationReport.Diagnostics).ToList();
            return new ToolResult
            {
                Ok = validationReport.BlockingCount == 0 && refined.FinalScore >= boundedTarget,
                Message = "Auto layout refinement completed.",
                ReportId = reportId,
                VersionId = created.VersionId,
                Diagnostics = diagnostics,
                Artifacts = new Dictionary<string, object?>
                {
                    ["canonicalHash"] = hash,
                    ["filePath"] = store.GetVersionFilePath(reportId, created.VersionId),
                    ["initialScore"] = refined.InitialScore,
                    ["finalScore"] = refined.FinalScore,
                    ["targetScore"] = boundedTarget,
                    ["iterationsApplied"] = refined.IterationsApplied,
                    ["iterations"] = refined.Iterations
                }
            };
        }
        catch (Exception ex)
        {
            return ErrorResult(reportId, ex.Message, "AUTO_REFINE_ERROR");
        }
    }

    [McpServerTool, Description("Run parse/profile/schema/lint checks and optional package-based runtime verification.")]
    public static ToolResult report_validate(
        ReportStore store,
        RdlxValidationService validation,
        RuntimeVerificationService runtimeVerification,
        [Description("Report identifier.")] string reportId,
        [Description("Version identifier.")] string versionId,
        [Description("Validation level: full, lint, parse_only.")] string validationLevel = "full",
        [Description("Include package-based runtime verification.")] bool includeRuntime = true)
    {
        if (!store.TryGetVersion(reportId, versionId, out _, out var version) || version is null)
        {
            return ErrorResult(reportId, "Unknown reportId/versionId combination.", "NOT_FOUND");
        }

        var level = ParseValidationLevel(validationLevel);
        var report = validation.Validate(version.Rdlx, level);
        var diagnostics = new List<DiagnosticEntry>(report.Diagnostics);

        RuntimeVerificationReport? runtimeReport = null;
        if (includeRuntime)
        {
            runtimeReport = runtimeVerification.Verify(store.GetVersionFilePath(reportId, versionId), "validate");
            diagnostics.AddRange(runtimeReport.Diagnostics);
        }

        return new ToolResult
        {
            Ok = diagnostics.All(d => !string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase)),
            Message = "Validation complete.",
            ReportId = reportId,
            VersionId = versionId,
            Diagnostics = diagnostics,
            Artifacts = new Dictionary<string, object?>
            {
                ["validation"] = new
                {
                    report.BlockingCount,
                    report.WarningsCount,
                    report.InfoCount,
                    report.ConfidenceScore
                },
                ["runtime"] = runtimeReport
            }
        };
    }

    [McpServerTool, Description("Run lint-focused checks with rule-pack metadata.")]
    public static ToolResult report_lint(
        ReportStore store,
        RdlxValidationService validation,
        [Description("Report identifier.")] string reportId,
        [Description("Version identifier.")] string versionId,
        [Description("Optional rule-pack version marker.")] string? rulePackVersion = null)
    {
        if (!store.TryGetVersion(reportId, versionId, out _, out var version) || version is null)
        {
            return ErrorResult(reportId, "Unknown reportId/versionId combination.", "NOT_FOUND");
        }

        var report = validation.Validate(version.Rdlx, ValidationLevel.Lint);
        return new ToolResult
        {
            Ok = report.BlockingCount == 0,
            Message = "Lint checks complete.",
            ReportId = reportId,
            VersionId = versionId,
            Diagnostics = report.Diagnostics,
            Artifacts = new Dictionary<string, object?>
            {
                ["rulePackVersion"] = rulePackVersion ?? "default-v1",
                ["blockingCount"] = report.BlockingCount,
                ["warningsCount"] = report.WarningsCount,
                ["confidenceScore"] = report.ConfidenceScore
            }
        };
    }

    [McpServerTool, Description("Canonicalize, verify, and save a report version when blocking checks are clear.")]
    public static ToolResult report_save_canonical(
        ReportStore store,
        RdlxDocumentService documents,
        RdlxValidationService validation,
        RuntimeVerificationService runtimeVerification,
        [Description("Report identifier.")] string reportId,
        [Description("Version identifier to save.")] string versionId,
        [Description("Optional save comment.")] string? saveComment = null,
        [Description("Include runtime verification in save gate.")] bool includeRuntime = true,
        [Description("Author or actor identifier for audit trail.")] string createdBy = "mcp-user")
    {
        if (!store.TryGetVersion(reportId, versionId, out var report, out var version) || report is null || version is null)
        {
            return ErrorResult(reportId, "Unknown reportId/versionId combination.", "NOT_FOUND");
        }

        var canonical = documents.Canonicalize(version.Rdlx);
        var hash = RdlxDocumentService.ComputeHash(canonical);

        var validationReport = validation.Validate(canonical, ValidationLevel.Full);
        var diagnostics = new List<DiagnosticEntry>(validationReport.Diagnostics);
        RuntimeVerificationReport? runtimeReport = null;
        if (includeRuntime)
        {
            runtimeReport = runtimeVerification.Verify(store.GetVersionFilePath(reportId, versionId), "full");
            diagnostics.AddRange(runtimeReport.Diagnostics);
        }

        var hasBlocking = diagnostics.Any(d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        if (hasBlocking)
        {
            return new ToolResult
            {
                Ok = false,
                Message = "Save blocked by validation errors.",
                ReportId = reportId,
                VersionId = versionId,
                Diagnostics = diagnostics,
                Artifacts = new Dictionary<string, object?>
                {
                    ["saveBlocked"] = true,
                    ["canonicalHash"] = hash,
                    ["runtime"] = runtimeReport
                }
            };
        }

        var savedVersion = store.CreateVersionFromBase(reportId, versionId, canonical, hash, createdBy, "save_canonical");
        store.MarkSaved(reportId, savedVersion.VersionId, saveComment);

        return new ToolResult
        {
            Ok = true,
            Message = "Canonical save completed.",
            ReportId = reportId,
            VersionId = savedVersion.VersionId,
            Diagnostics = diagnostics,
            Artifacts = new Dictionary<string, object?>
            {
                ["canonicalHash"] = savedVersion.CanonicalHash,
                ["filePath"] = store.GetVersionFilePath(reportId, savedVersion.VersionId),
                ["saveComment"] = saveComment,
                ["runtime"] = runtimeReport,
                ["verificationStatus"] = "unchecked"
            }
        };
    }

    [McpServerTool, Description("Diff two report versions and return a semantic summary.")]
    public static ToolResult report_diff_versions(
        ReportStore store,
        RdlxDocumentService documents,
        [Description("Report identifier.")] string reportId,
        [Description("From version identifier.")] string fromVersionId,
        [Description("To version identifier.")] string toVersionId)
    {
        if (!store.TryGetVersion(reportId, fromVersionId, out _, out var fromVersion) || fromVersion is null)
        {
            return ErrorResult(reportId, "Unknown fromVersionId.", "NOT_FOUND");
        }

        if (!store.TryGetVersion(reportId, toVersionId, out _, out var toVersion) || toVersion is null)
        {
            return ErrorResult(reportId, "Unknown toVersionId.", "NOT_FOUND");
        }

        var diff = documents.BuildDiffSummary(fromVersion.Rdlx, toVersion.Rdlx);
        return new ToolResult
        {
            Ok = true,
            Message = "Version diff generated.",
            ReportId = reportId,
            VersionId = toVersionId,
            Artifacts = new Dictionary<string, object?>
            {
                ["diff"] = diff
            }
        };
    }

    [McpServerTool, Description("Generate a manual review checklist and risk summary for designer handoff.")]
    public static ToolResult report_handoff_summary(
        ReportStore store,
        RdlxValidationService validation,
        RuntimeVerificationService runtimeVerification,
        [Description("Report identifier.")] string reportId,
        [Description("Version identifier.")] string versionId)
    {
        if (!store.TryGetVersion(reportId, versionId, out _, out var version) || version is null)
        {
            return ErrorResult(reportId, "Unknown reportId/versionId combination.", "NOT_FOUND");
        }

        var lint = validation.Validate(version.Rdlx, ValidationLevel.Full);
        var runtime = runtimeVerification.Verify(store.GetVersionFilePath(reportId, versionId), "full");
        var diagnostics = lint.Diagnostics.Concat(runtime.Diagnostics).ToList();

        var checklist = new[]
        {
            "Open the saved .rdlx in ActiveReports designer.",
            "Verify all datasets and data source bindings resolve as expected.",
            "Preview key report pages for layout overlaps and truncation.",
            "Confirm expression outputs for sample parameters.",
            "If manual fixes are applied, save as a new tracked baseline version."
        };

        return new ToolResult
        {
            Ok = diagnostics.All(d => !string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase)),
            Message = "Handoff summary generated.",
            ReportId = reportId,
            VersionId = versionId,
            Diagnostics = diagnostics,
            Artifacts = new Dictionary<string, object?>
            {
                ["confidenceScore"] = lint.ConfidenceScore,
                ["filePath"] = store.GetVersionFilePath(reportId, versionId),
                ["checklist"] = checklist
            }
        };
    }

    [McpServerTool, Description("Run package-based ActiveReports runtime verification for supported checks.")]
    public static ToolResult report_runtime_verify(
        ReportStore store,
        RuntimeVerificationService runtimeVerification,
        [Description("Report identifier.")] string reportId,
        [Description("Version identifier.")] string versionId,
        [Description("Mode: load_only, validate, run_smoke, full.")] string mode = "load_only")
    {
        if (!store.TryGetVersion(reportId, versionId, out _, out var version) || version is null)
        {
            return ErrorResult(reportId, "Unknown reportId/versionId combination.", "NOT_FOUND");
        }

        var runtime = runtimeVerification.Verify(store.GetVersionFilePath(reportId, versionId), mode);
        return new ToolResult
        {
            Ok = runtime.Success,
            Message = "Runtime verification completed.",
            ReportId = reportId,
            VersionId = versionId,
            Diagnostics = runtime.Diagnostics,
            Artifacts = new Dictionary<string, object?>
            {
                ["mode"] = runtime.Mode,
                ["coverage"] = runtime.Coverage,
                ["filePath"] = store.GetVersionFilePath(reportId, versionId)
            }
        };
    }

    private static ValidationLevel ParseValidationLevel(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "parse_only" => ValidationLevel.ParseOnly,
            "lint" => ValidationLevel.Lint,
            _ => ValidationLevel.Full
        };
    }

    private static (bool Ok, ToolResult Result, ReportVersionRecord? Version) GetVersion(ReportStore store, string reportId, string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            if (!store.TryGetLatestVersion(reportId, out _, out var latest) || latest is null)
            {
                return (false, ErrorResult(reportId, "Unknown reportId.", "NOT_FOUND"), null);
            }

            return (true, SuccessShell(reportId, latest.VersionId), latest);
        }

        if (!store.TryGetVersion(reportId, versionId, out _, out var version) || version is null)
        {
            return (false, ErrorResult(reportId, "Unknown reportId/versionId combination.", "NOT_FOUND"), null);
        }

        return (true, SuccessShell(reportId, versionId), version);
    }

    private static ToolResult ErrorResult(string? reportId, string message, string code)
    {
        return new ToolResult
        {
            Ok = false,
            Message = message,
            ReportId = reportId,
            Diagnostics =
            [
                new DiagnosticEntry
                {
                    Stage = "tool",
                    Severity = "Error",
                    Code = code,
                    Message = message
                }
            ]
        };
    }

    private static ToolResult SuccessShell(string reportId, string versionId)
    {
        return new ToolResult
        {
            Ok = true,
            ReportId = reportId,
            VersionId = versionId
        };
    }
}

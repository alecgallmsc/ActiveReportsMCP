using System.ComponentModel;
using ModelContextProtocol.Server;
using RdlxMcpServer.Models;
using RdlxMcpServer.Services;

namespace RdlxMcpServer.Tools;

[McpServerToolType]
public sealed class ReportTools
{
    [McpServerTool, Description("Create a new RDLX report at a local output path.")]
    public static ToolResult report_create(
        RdlxDocumentService documents,
        RdlxValidationService validation,
        [Description("Human-friendly report name.")] string name,
        [Description("Absolute or relative output .rdlx path.")] string outputPath,
        [Description("Report type label, e.g. PageReport.")] string reportType = "PageReport",
        [Description("Optional path to existing .rdlx template.")] string? templatePath = null,
        [Description("Optional page settings map: PageWidth, PageHeight, Margin.")] Dictionary<string, string>? pageSettings = null,
        [Description("Author or actor identifier for audit trail.")] string createdBy = "mcp-user")
    {
        try
        {
            var baseRdlx = string.IsNullOrWhiteSpace(templatePath)
                ? documents.CreateSkeleton(name, reportType, pageSettings)
                : File.ReadAllText(ToAbsolutePath(templatePath));

            var canonical = documents.Canonicalize(baseRdlx);
            var hash = RdlxDocumentService.ComputeHash(canonical);
            var reportPath = WriteReport(canonical, outputPath);
            var report = validation.Validate(canonical, ValidationLevel.Full);

            return new ToolResult
            {
                Ok = report.BlockingCount == 0,
                Message = "Report created.",
                ReportPath = reportPath,
                Diagnostics = report.Diagnostics,
                Artifacts = BuildArtifacts(reportPath, hash, createdBy, new
                {
                    report.BlockingCount,
                    report.WarningsCount,
                    report.ConfidenceScore
                })
            };
        }
        catch (Exception ex)
        {
            return ErrorResult(outputPath, ex.Message, "CREATE_ERROR");
        }
    }

    [McpServerTool, Description("Return a normalized report structure tree from a local report path.")]
    public static ToolResult report_get_structure(
        RdlxDocumentService documents,
        [Description("Absolute or relative .rdlx path.")] string reportPath)
    {
        var loaded = TryLoadReport(reportPath);
        if (!loaded.Ok)
        {
            return loaded.Result;
        }

        var structure = documents.BuildStructure(loaded.Rdlx!);
        return new ToolResult
        {
            Ok = true,
            Message = "Report structure loaded.",
            ReportPath = loaded.Path,
            Artifacts = new Dictionary<string, object?>
            {
                ["filePath"] = loaded.Path,
                ["structure"] = structure
            }
        };
    }

    [McpServerTool, Description("Apply layout patch operations to a local report path.")]
    public static ToolResult report_patch_layout(
        RdlxDocumentService documents,
        RdlxValidationService validation,
        [Description("Absolute or relative .rdlx path.")] string reportPath,
        [Description("Layout operations to apply.")] List<LayoutOperation> operations,
        [Description("Optional output .rdlx path. Defaults to in-place overwrite.")] string? outputPath = null,
        [Description("Author or actor identifier for audit trail.")] string createdBy = "mcp-user")
    {
        return PatchReport(
            reportPath,
            outputPath,
            createdBy,
            rdlx => documents.ApplyLayoutOperations(rdlx, operations),
            documents,
            validation,
            "Layout patch applied.",
            "PATCH_ERROR");
    }

    [McpServerTool, Description("Apply data source/dataset/parameter patch operations to a local report path.")]
    public static ToolResult report_patch_data(
        RdlxDocumentService documents,
        RdlxValidationService validation,
        [Description("Absolute or relative .rdlx path.")] string reportPath,
        [Description("Data operations to apply.")] List<DataOperation> dataOps,
        [Description("Optional output .rdlx path. Defaults to in-place overwrite.")] string? outputPath = null,
        [Description("Author or actor identifier for audit trail.")] string createdBy = "mcp-user")
    {
        return PatchReport(
            reportPath,
            outputPath,
            createdBy,
            rdlx => documents.ApplyDataOperations(rdlx, dataOps),
            documents,
            validation,
            "Data patch applied.",
            "PATCH_ERROR");
    }

    [McpServerTool, Description("Apply style operations to controls in a local report path.")]
    public static ToolResult report_patch_style(
        RdlxDocumentService documents,
        RdlxValidationService validation,
        LayoutIntelligenceService layoutIntelligence,
        [Description("Absolute or relative .rdlx path.")] string reportPath,
        [Description("Targets to style (targetRef or selector).")]
        List<StyleTarget> targets,
        [Description("Style operations (property/value pairs).")]
        List<StyleOperation> styleOps,
        [Description("Optional output .rdlx path. Defaults to in-place overwrite.")] string? outputPath = null,
        [Description("Author or actor identifier for audit trail.")] string createdBy = "mcp-user")
    {
        return PatchReport(
            reportPath,
            outputPath,
            createdBy,
            rdlx => layoutIntelligence.ApplyStylePatch(rdlx, targets, styleOps),
            documents,
            validation,
            "Style patch applied.",
            "STYLE_PATCH_ERROR",
            extraArtifacts: new Dictionary<string, object?>
            {
                ["appliedTargets"] = targets.Count,
                ["appliedStyleOps"] = styleOps.Count
            });
    }

    [McpServerTool, Description("Apply value formatting rules by field references to a local report path.")]
    public static ToolResult report_patch_formatting(
        RdlxDocumentService documents,
        RdlxValidationService validation,
        LayoutIntelligenceService layoutIntelligence,
        [Description("Absolute or relative .rdlx path.")] string reportPath,
        [Description("Formatting rules by field reference.")] List<FormatRule> formatRules,
        [Description("Optional output .rdlx path. Defaults to in-place overwrite.")] string? outputPath = null,
        [Description("Author or actor identifier for audit trail.")] string createdBy = "mcp-user")
    {
        return PatchReport(
            reportPath,
            outputPath,
            createdBy,
            rdlx => layoutIntelligence.ApplyFormattingPatch(rdlx, formatRules),
            documents,
            validation,
            "Formatting patch applied.",
            "FORMATTING_PATCH_ERROR",
            extraArtifacts: new Dictionary<string, object?>
            {
                ["appliedFormatRules"] = formatRules.Count
            });
    }

    [McpServerTool, Description("Return normalized geometry/style model for AI reasoning from a local report path.")]
    public static ToolResult report_extract_layout_model(
        LayoutIntelligenceService layoutIntelligence,
        [Description("Absolute or relative .rdlx path.")] string reportPath)
    {
        var loaded = TryLoadReport(reportPath);
        if (!loaded.Ok)
        {
            return loaded.Result;
        }

        var model = layoutIntelligence.ExtractLayoutModel(loaded.Rdlx!);
        return new ToolResult
        {
            Ok = true,
            Message = "Layout model extracted.",
            ReportPath = loaded.Path,
            Artifacts = new Dictionary<string, object?>
            {
                ["filePath"] = loaded.Path,
                ["layoutModel"] = model,
                ["controlCount"] = model.Controls.Count,
                ["alignmentGroupCount"] = model.AlignmentGroups.Count
            }
        };
    }

    [McpServerTool, Description("Score layout quality from local report XML geometry and semantic heuristics.")]
    public static ToolResult report_layout_score(
        LayoutIntelligenceService layoutIntelligence,
        [Description("Absolute or relative .rdlx path.")] string reportPath,
        [Description("Optional rule-pack version marker.")] string? rulePackVersion = null)
    {
        var loaded = TryLoadReport(reportPath);
        if (!loaded.Ok)
        {
            return loaded.Result;
        }

        var score = layoutIntelligence.ScoreLayout(loaded.Rdlx!);
        return new ToolResult
        {
            Ok = score.Score >= 80,
            Message = "Layout score generated.",
            ReportPath = loaded.Path,
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
                ["filePath"] = loaded.Path,
                ["rulePackVersion"] = rulePackVersion ?? "layout-v1.1",
                ["layoutScore"] = score
            }
        };
    }

    [McpServerTool, Description("Run bounded deterministic auto-refinement to improve layout score on local report XML.")]
    public static ToolResult report_auto_refine_layout(
        RdlxDocumentService documents,
        RdlxValidationService validation,
        LayoutIntelligenceService layoutIntelligence,
        [Description("Absolute or relative .rdlx path.")] string reportPath,
        [Description("Maximum refinement iterations.")] int maxIterations = 3,
        [Description("Target layout score threshold.")] int targetScore = 80,
        [Description("Optional output .rdlx path. Defaults to in-place overwrite.")] string? outputPath = null,
        [Description("Author or actor identifier for audit trail.")] string createdBy = "mcp-user")
    {
        var loaded = TryLoadReport(reportPath);
        if (!loaded.Ok)
        {
            return loaded.Result;
        }

        try
        {
            var boundedIterations = Math.Clamp(maxIterations, 1, 10);
            var boundedTarget = Math.Clamp(targetScore, 40, 100);
            var refined = layoutIntelligence.AutoRefineLayout(loaded.Rdlx!, boundedIterations, boundedTarget);

            var canonical = documents.Canonicalize(refined.Rdlx);
            var hash = RdlxDocumentService.ComputeHash(canonical);
            var targetPath = WriteReport(canonical, outputPath ?? loaded.Path!);
            var validationReport = validation.Validate(canonical, ValidationLevel.Full);

            var diagnostics = refined.Diagnostics.Concat(validationReport.Diagnostics).ToList();
            return new ToolResult
            {
                Ok = validationReport.BlockingCount == 0 && refined.FinalScore >= boundedTarget,
                Message = "Auto layout refinement completed.",
                ReportPath = targetPath,
                Diagnostics = diagnostics,
                Artifacts = BuildArtifacts(targetPath, hash, createdBy, new
                {
                    refined.InitialScore,
                    refined.FinalScore,
                    TargetScore = boundedTarget,
                    refined.IterationsApplied,
                    refined.Iterations
                })
            };
        }
        catch (Exception ex)
        {
            return ErrorResult(reportPath, ex.Message, "AUTO_REFINE_ERROR");
        }
    }

    [McpServerTool, Description("Run parse/profile/schema/lint checks and optional runtime verification for a local report path.")]
    public static ToolResult report_validate(
        RdlxValidationService validation,
        RuntimeVerificationService runtimeVerification,
        [Description("Absolute or relative .rdlx path.")] string reportPath,
        [Description("Validation level: full, lint, parse_only.")] string validationLevel = "full",
        [Description("Include package-based runtime verification.")] bool includeRuntime = true)
    {
        var loaded = TryLoadReport(reportPath);
        if (!loaded.Ok)
        {
            return loaded.Result;
        }

        var level = ParseValidationLevel(validationLevel);
        var report = validation.Validate(loaded.Rdlx!, level);
        var diagnostics = new List<DiagnosticEntry>(report.Diagnostics);

        RuntimeVerificationReport? runtimeReport = null;
        if (includeRuntime)
        {
            runtimeReport = runtimeVerification.Verify(loaded.Path!, "validate");
            diagnostics.AddRange(runtimeReport.Diagnostics);
        }

        return new ToolResult
        {
            Ok = diagnostics.All(d => !string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase)),
            Message = "Validation complete.",
            ReportPath = loaded.Path,
            Diagnostics = diagnostics,
            Artifacts = new Dictionary<string, object?>
            {
                ["filePath"] = loaded.Path,
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

    [McpServerTool, Description("Run lint-focused checks with rule-pack metadata for a local report path.")]
    public static ToolResult report_lint(
        RdlxValidationService validation,
        [Description("Absolute or relative .rdlx path.")] string reportPath,
        [Description("Optional rule-pack version marker.")] string? rulePackVersion = null)
    {
        var loaded = TryLoadReport(reportPath);
        if (!loaded.Ok)
        {
            return loaded.Result;
        }

        var report = validation.Validate(loaded.Rdlx!, ValidationLevel.Lint);
        return new ToolResult
        {
            Ok = report.BlockingCount == 0,
            Message = "Lint checks complete.",
            ReportPath = loaded.Path,
            Diagnostics = report.Diagnostics,
            Artifacts = new Dictionary<string, object?>
            {
                ["filePath"] = loaded.Path,
                ["rulePackVersion"] = rulePackVersion ?? "default-v1",
                ["blockingCount"] = report.BlockingCount,
                ["warningsCount"] = report.WarningsCount,
                ["confidenceScore"] = report.ConfidenceScore
            }
        };
    }

    [McpServerTool, Description("Canonicalize and save a local report path when checks are clear.")]
    public static ToolResult report_save_canonical(
        RdlxDocumentService documents,
        RdlxValidationService validation,
        RuntimeVerificationService runtimeVerification,
        [Description("Absolute or relative .rdlx path.")] string reportPath,
        [Description("Optional output .rdlx path. Defaults to in-place overwrite.")] string? outputPath = null,
        [Description("Optional save comment.")] string? saveComment = null,
        [Description("Include runtime verification in save gate.")] bool includeRuntime = true,
        [Description("Author or actor identifier for audit trail.")] string createdBy = "mcp-user")
    {
        var loaded = TryLoadReport(reportPath);
        if (!loaded.Ok)
        {
            return loaded.Result;
        }

        var canonical = documents.Canonicalize(loaded.Rdlx!);
        var hash = RdlxDocumentService.ComputeHash(canonical);

        var validationReport = validation.Validate(canonical, ValidationLevel.Full);
        var diagnostics = new List<DiagnosticEntry>(validationReport.Diagnostics);
        RuntimeVerificationReport? runtimeReport = null;

        if (includeRuntime)
        {
            runtimeReport = runtimeVerification.Verify(loaded.Path!, "full");
            diagnostics.AddRange(runtimeReport.Diagnostics);
        }

        var hasBlocking = diagnostics.Any(d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        if (hasBlocking)
        {
            return new ToolResult
            {
                Ok = false,
                Message = "Save blocked by validation errors.",
                ReportPath = loaded.Path,
                Diagnostics = diagnostics,
                Artifacts = new Dictionary<string, object?>
                {
                    ["filePath"] = loaded.Path,
                    ["saveBlocked"] = true,
                    ["canonicalHash"] = hash,
                    ["runtime"] = runtimeReport
                }
            };
        }

        var targetPath = WriteReport(canonical, outputPath ?? loaded.Path!);
        return new ToolResult
        {
            Ok = true,
            Message = "Canonical save completed.",
            ReportPath = targetPath,
            Diagnostics = diagnostics,
            Artifacts = BuildArtifacts(targetPath, hash, createdBy, new
            {
                saveComment,
                runtime = runtimeReport,
                verificationStatus = "unchecked"
            })
        };
    }

    [McpServerTool, Description("Diff two local report paths and return a semantic summary.")]
    public static ToolResult report_diff_versions(
        RdlxDocumentService documents,
        [Description("From .rdlx path.")] string fromReportPath,
        [Description("To .rdlx path.")] string toReportPath)
    {
        var fromLoaded = TryLoadReport(fromReportPath);
        if (!fromLoaded.Ok)
        {
            return fromLoaded.Result;
        }

        var toLoaded = TryLoadReport(toReportPath);
        if (!toLoaded.Ok)
        {
            return toLoaded.Result;
        }

        var diff = documents.BuildDiffSummary(fromLoaded.Rdlx!, toLoaded.Rdlx!);
        return new ToolResult
        {
            Ok = true,
            Message = "Version diff generated.",
            ReportPath = toLoaded.Path,
            Artifacts = new Dictionary<string, object?>
            {
                ["fromPath"] = fromLoaded.Path,
                ["toPath"] = toLoaded.Path,
                ["diff"] = diff
            }
        };
    }

    [McpServerTool, Description("Generate a manual review checklist and risk summary for a local report path.")]
    public static ToolResult report_handoff_summary(
        RdlxValidationService validation,
        RuntimeVerificationService runtimeVerification,
        [Description("Absolute or relative .rdlx path.")] string reportPath)
    {
        var loaded = TryLoadReport(reportPath);
        if (!loaded.Ok)
        {
            return loaded.Result;
        }

        var lint = validation.Validate(loaded.Rdlx!, ValidationLevel.Full);
        var runtime = runtimeVerification.Verify(loaded.Path!, "full");
        var diagnostics = lint.Diagnostics.Concat(runtime.Diagnostics).ToList();

        var checklist = new[]
        {
            "Open the saved .rdlx in ActiveReports designer.",
            "Verify all datasets and data source bindings resolve as expected.",
            "Preview key report pages for layout overlaps and truncation.",
            "Confirm expression outputs for sample parameters.",
            "If manual fixes are applied, save as a new local revision."
        };

        return new ToolResult
        {
            Ok = diagnostics.All(d => !string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase)),
            Message = "Handoff summary generated.",
            ReportPath = loaded.Path,
            Diagnostics = diagnostics,
            Artifacts = new Dictionary<string, object?>
            {
                ["filePath"] = loaded.Path,
                ["confidenceScore"] = lint.ConfidenceScore,
                ["checklist"] = checklist
            }
        };
    }

    [McpServerTool, Description("Run package-based ActiveReports runtime verification for a local report path.")]
    public static ToolResult report_runtime_verify(
        RuntimeVerificationService runtimeVerification,
        [Description("Absolute or relative .rdlx path.")] string reportPath,
        [Description("Mode: load_only, validate, run_smoke, full.")] string mode = "load_only")
    {
        var loaded = TryLoadReport(reportPath);
        if (!loaded.Ok)
        {
            return loaded.Result;
        }

        var runtime = runtimeVerification.Verify(loaded.Path!, mode);
        return new ToolResult
        {
            Ok = runtime.Success,
            Message = "Runtime verification completed.",
            ReportPath = loaded.Path,
            Diagnostics = runtime.Diagnostics,
            Artifacts = new Dictionary<string, object?>
            {
                ["filePath"] = loaded.Path,
                ["mode"] = runtime.Mode,
                ["coverage"] = runtime.Coverage
            }
        };
    }

    [McpServerTool, Description("Inspect database schema metadata with explicit consent; returns tables and columns only.")]
    public static ToolResult report_inspect_schema(
        SchemaInspectionService schemaInspection,
        [Description("Absolute or relative .rdlx path containing the target data source.")] string reportPath,
        [Description("Optional data source name. Defaults to first report data source.")] string? dataSourceName = null,
        [Description("Explicit user confirmation to allow metadata-only schema inspection.")] bool confirm = false,
        [Description("Optional table allowlist (table or schema.table names).")]
        List<string>? tableAllowList = null,
        [Description("Maximum tables/views to return.")] int maxTables = 50,
        [Description("Maximum columns to return per table/view.")] int maxColumnsPerTable = 100)
    {
        var loaded = TryLoadReport(reportPath);
        if (!loaded.Ok)
        {
            return loaded.Result;
        }

        return schemaInspection.InspectFromReport(
            loaded.Path!,
            loaded.Rdlx!,
            dataSourceName,
            confirm,
            tableAllowList,
            maxTables,
            maxColumnsPerTable);
    }

    private static ToolResult PatchReport(
        string reportPath,
        string? outputPath,
        string createdBy,
        Func<string, PatchResult> patcher,
        RdlxDocumentService documents,
        RdlxValidationService validation,
        string successMessage,
        string errorCode,
        Dictionary<string, object?>? extraArtifacts = null)
    {
        var loaded = TryLoadReport(reportPath);
        if (!loaded.Ok)
        {
            return loaded.Result;
        }

        try
        {
            var patch = patcher(loaded.Rdlx!);
            var canonical = documents.Canonicalize(patch.Rdlx);
            var hash = RdlxDocumentService.ComputeHash(canonical);
            var targetPath = WriteReport(canonical, outputPath ?? loaded.Path!);
            var validationReport = validation.Validate(canonical, ValidationLevel.Full);

            var diagnostics = patch.Diagnostics.Concat(validationReport.Diagnostics).ToList();
            var artifacts = BuildArtifacts(targetPath, hash, createdBy, new
            {
                validation = new
                {
                    validationReport.BlockingCount,
                    validationReport.WarningsCount,
                    validationReport.ConfidenceScore
                }
            });

            if (extraArtifacts is not null)
            {
                foreach (var entry in extraArtifacts)
                {
                    artifacts[entry.Key] = entry.Value;
                }
            }

            return new ToolResult
            {
                Ok = validationReport.BlockingCount == 0,
                Message = successMessage,
                ReportPath = targetPath,
                Diagnostics = diagnostics,
                Artifacts = artifacts
            };
        }
        catch (Exception ex)
        {
            return ErrorResult(reportPath, ex.Message, errorCode);
        }
    }

    private static (bool Ok, ToolResult Result, string? Rdlx, string? Path) TryLoadReport(string reportPath)
    {
        try
        {
            var absolutePath = ToAbsolutePath(reportPath);
            if (!File.Exists(absolutePath))
            {
                return (false, ErrorResult(absolutePath, "Report path not found.", "NOT_FOUND"), null, null);
            }

            var rdlx = File.ReadAllText(absolutePath);
            return (true, SuccessShell(absolutePath), rdlx, absolutePath);
        }
        catch (Exception ex)
        {
            return (false, ErrorResult(reportPath, ex.Message, "LOAD_ERROR"), null, null);
        }
    }

    private static string WriteReport(string rdlx, string reportPath)
    {
        var absolutePath = ToAbsolutePath(reportPath);
        var directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(absolutePath, rdlx);
        return absolutePath;
    }

    private static Dictionary<string, object?> BuildArtifacts(string reportPath, string canonicalHash, string createdBy, object? extra = null)
    {
        var artifacts = new Dictionary<string, object?>
        {
            ["filePath"] = reportPath,
            ["canonicalHash"] = canonicalHash,
            ["createdBy"] = createdBy,
            ["savedAtUtc"] = DateTimeOffset.UtcNow
        };

        if (extra is not null)
        {
            artifacts["details"] = extra;
        }

        return artifacts;
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

    private static string ToAbsolutePath(string path)
    {
        return Path.GetFullPath(path);
    }

    private static ToolResult ErrorResult(string? reportPath, string message, string code)
    {
        return new ToolResult
        {
            Ok = false,
            Message = message,
            ReportPath = reportPath,
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

    private static ToolResult SuccessShell(string reportPath)
    {
        return new ToolResult
        {
            Ok = true,
            ReportPath = reportPath
        };
    }
}

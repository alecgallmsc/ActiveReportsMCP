using System.Text.Json;
using Microsoft.Extensions.Logging;
using RdlxMcpServer.Models;
using RdlxMcpServer.Tools;

namespace RdlxMcpServer.Services;

public sealed class SelfTestRunner
{
    private readonly ReportStore _store;
    private readonly RdlxDocumentService _documents;
    private readonly RdlxValidationService _validation;
    private readonly RuntimeVerificationService _runtime;
    private readonly LayoutIntelligenceService _layoutIntelligence;
    private readonly ILogger<SelfTestRunner> _logger;

    public SelfTestRunner(
        ReportStore store,
        RdlxDocumentService documents,
        RdlxValidationService validation,
        RuntimeVerificationService runtime,
        LayoutIntelligenceService layoutIntelligence,
        ILogger<SelfTestRunner> logger)
    {
        _store = store;
        _documents = documents;
        _validation = validation;
        _runtime = runtime;
        _layoutIntelligence = layoutIntelligence;
        _logger = logger;
    }

    public async Task<int> RunAsync(bool includeRuntimeChecks, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        var events = new List<object>();

        var create = ReportTools.report_create(
            _store,
            _documents,
            _validation,
            name: "PoC_Invoice_SelfTest",
            reportType: "PageReport",
            templatePath: null,
            pageSettings: new Dictionary<string, string>
            {
                ["PageWidth"] = "8.5in",
                ["PageHeight"] = "11in",
                ["Margin"] = "0.5in"
            },
            createdBy: "self-test");

        events.Add(new { step = "report_create", create.Ok, create.Message, create.ReportId, create.VersionId });
        if (!create.Ok || string.IsNullOrWhiteSpace(create.ReportId) || string.IsNullOrWhiteSpace(create.VersionId))
        {
            return PersistAndExit(events, 1);
        }

        var patchData = ReportTools.report_patch_data(
            _store,
            _documents,
            _validation,
            reportId: create.ReportId,
            baseVersionId: create.VersionId,
            dataOps:
            [
                new DataOperation
                {
                    Op = "upsert_data_source",
                    Name = "MainData",
                    DataProvider = "SQL",
                    ConnectionString = "Data Source=(local);Initial Catalog=Demo;Integrated Security=True"
                },
                new DataOperation
                {
                    Op = "upsert_dataset",
                    Name = "InvoiceData",
                    DataSourceName = "MainData",
                    CommandText = "SELECT InvoiceId, CustomerName, TotalAmount FROM Invoices",
                    Fields = ["InvoiceId", "CustomerName", "TotalAmount"]
                }
            ],
            createdBy: "self-test");

        events.Add(new { step = "report_patch_data", patchData.Ok, patchData.Message, patchData.VersionId });
        if (!patchData.Ok || string.IsNullOrWhiteSpace(patchData.VersionId))
        {
            return PersistAndExit(events, 2);
        }

        var patchLayout = ReportTools.report_patch_layout(
            _store,
            _documents,
            _validation,
            reportId: create.ReportId,
            baseVersionId: patchData.VersionId,
            operations:
            [
                new LayoutOperation
                {
                    Op = "add_table",
                    Name = "tblInvoiceByCustomer",
                    X = "0.2in",
                    Y = "0.7in",
                    Width = "6.8in",
                    Height = "2.0in",
                    ValueExpression = "dataset=InvoiceData;groupBy=CustomerName;columns=InvoiceId,CustomerName,TotalAmount"
                }
            ],
            createdBy: "self-test");

        events.Add(new { step = "report_patch_layout", patchLayout.Ok, patchLayout.Message, patchLayout.VersionId });
        if (!patchLayout.Ok || string.IsNullOrWhiteSpace(patchLayout.VersionId))
        {
            return PersistAndExit(events, 3);
        }

        var patchStyle = ReportTools.report_patch_style(
            _store,
            _documents,
            _validation,
            _layoutIntelligence,
            reportId: create.ReportId,
            baseVersionId: patchLayout.VersionId,
            targets:
            [
                new StyleTarget { TargetRef = "textbox:txtTitle" },
                new StyleTarget { Selector = "type:Table" }
            ],
            styleOps:
            [
                new StyleOperation { Property = "fontWeight", Value = "Bold" },
                new StyleOperation { Property = "fontSize", Value = "11pt" },
                new StyleOperation { Property = "paddingLeft", Value = "2pt" }
            ],
            createdBy: "self-test");

        events.Add(new { step = "report_patch_style", patchStyle.Ok, patchStyle.Message, patchStyle.VersionId });
        if (!patchStyle.Ok || string.IsNullOrWhiteSpace(patchStyle.VersionId))
        {
            return PersistAndExit(events, 31);
        }

        var patchFormatting = ReportTools.report_patch_formatting(
            _store,
            _documents,
            _validation,
            _layoutIntelligence,
            reportId: create.ReportId,
            baseVersionId: patchStyle.VersionId,
            formatRules:
            [
                new FormatRule { FieldRef = "TotalAmount", FormatString = "C2", Locale = "en-US", NullDisplay = "N/A" }
            ],
            createdBy: "self-test");

        events.Add(new { step = "report_patch_formatting", patchFormatting.Ok, patchFormatting.Message, patchFormatting.VersionId });
        if (!patchFormatting.Ok || string.IsNullOrWhiteSpace(patchFormatting.VersionId))
        {
            return PersistAndExit(events, 32);
        }

        var layoutModel = ReportTools.report_extract_layout_model(
            _store,
            _layoutIntelligence,
            reportId: create.ReportId,
            versionId: patchFormatting.VersionId);

        events.Add(new { step = "report_extract_layout_model", layoutModel.Ok, layoutModel.Message });

        var layoutScore = ReportTools.report_layout_score(
            _store,
            _layoutIntelligence,
            reportId: create.ReportId,
            versionId: patchFormatting.VersionId,
            rulePackVersion: "layout-v1.1");

        events.Add(new
        {
            step = "report_layout_score",
            layoutScore.Ok,
            layoutScore.Message,
            score = layoutScore.Artifacts.TryGetValue("layoutScore", out var scoreObj) ? scoreObj : null
        });

        var refine = ReportTools.report_auto_refine_layout(
            _store,
            _documents,
            _validation,
            _layoutIntelligence,
            reportId: create.ReportId,
            baseVersionId: patchFormatting.VersionId,
            maxIterations: 2,
            targetScore: 75,
            createdBy: "self-test");

        events.Add(new { step = "report_auto_refine_layout", refine.Ok, refine.Message, refine.VersionId });
        if (!refine.Ok || string.IsNullOrWhiteSpace(refine.VersionId))
        {
            return PersistAndExit(events, 33);
        }

        var validate = ReportTools.report_validate(
            _store,
            _validation,
            _runtime,
            reportId: create.ReportId,
            versionId: refine.VersionId,
            validationLevel: "full",
            includeRuntime: includeRuntimeChecks);

        events.Add(new
        {
            step = "report_validate",
            validate.Ok,
            validate.Message,
            blocking = validate.Diagnostics.Count(d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase))
        });

        if (includeRuntimeChecks)
        {
            var runtime = ReportTools.report_runtime_verify(
                _store,
                _runtime,
                reportId: create.ReportId,
                versionId: refine.VersionId,
                mode: "validate");

            events.Add(new
            {
                step = "report_runtime_verify",
                runtime.Ok,
                runtime.Message,
                errors = runtime.Diagnostics.Count(d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase))
            });
        }

        var save = ReportTools.report_save_canonical(
            _store,
            _documents,
            _validation,
            _runtime,
            reportId: create.ReportId,
            versionId: refine.VersionId,
            saveComment: "self-test canonical save",
            includeRuntime: false,
            createdBy: "self-test");

        events.Add(new { step = "report_save_canonical", save.Ok, save.Message, save.VersionId });
        if (!save.Ok)
        {
            return PersistAndExit(events, 4);
        }

        var handoff = ReportTools.report_handoff_summary(
            _store,
            _validation,
            _runtime,
            reportId: create.ReportId,
            versionId: save.VersionId ?? refine.VersionId);

        events.Add(new { step = "report_handoff_summary", handoff.Ok, handoff.Message });

        return PersistAndExit(events, 0);
    }

    private int PersistAndExit(List<object> events, int exitCode)
    {
        var output = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            exitCode,
            events
        };

        var outputPath = Path.Combine(AppContext.BaseDirectory, "data", "self-test-last.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));

        _logger.LogInformation("Self-test completed with exit code {ExitCode}. Output: {OutputPath}", exitCode, outputPath);
        return exitCode;
    }
}

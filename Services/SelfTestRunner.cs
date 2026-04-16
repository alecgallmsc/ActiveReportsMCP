using System.Text.Json;
using Microsoft.Extensions.Logging;
using RdlxMcpServer.Models;
using RdlxMcpServer.Tools;

namespace RdlxMcpServer.Services;

public sealed class SelfTestRunner
{
    private readonly RdlxDocumentService _documents;
    private readonly RdlxValidationService _validation;
    private readonly RuntimeVerificationService _runtime;
    private readonly LayoutIntelligenceService _layoutIntelligence;
    private readonly ILogger<SelfTestRunner> _logger;

    public SelfTestRunner(
        RdlxDocumentService documents,
        RdlxValidationService validation,
        RuntimeVerificationService runtime,
        LayoutIntelligenceService layoutIntelligence,
        ILogger<SelfTestRunner> logger)
    {
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
        var reportPath = Path.Combine(AppContext.BaseDirectory, "data", "self-test", "report.rdlx");

        var create = ReportTools.report_create(
            _documents,
            _validation,
            name: "PoC_Invoice_SelfTest",
            outputPath: reportPath,
            reportType: "PageReport",
            pageSettings: new Dictionary<string, string>
            {
                ["PageWidth"] = "8.5in",
                ["PageHeight"] = "11in",
                ["Margin"] = "0.5in"
            },
            createdBy: "self-test");

        events.Add(new { step = "report_create", create.Ok, create.Message, create.ReportPath });
        if (!create.Ok || string.IsNullOrWhiteSpace(create.ReportPath))
        {
            return PersistAndExit(events, 1);
        }

        var patchData = ReportTools.report_patch_data(
            _documents,
            _validation,
            reportPath: create.ReportPath,
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

        events.Add(new { step = "report_patch_data", patchData.Ok, patchData.Message, patchData.ReportPath });
        if (!patchData.Ok || string.IsNullOrWhiteSpace(patchData.ReportPath))
        {
            return PersistAndExit(events, 2);
        }

        var patchLayout = ReportTools.report_patch_layout(
            _documents,
            _validation,
            reportPath: patchData.ReportPath,
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
                    ValueExpression = "dataset=InvoiceData;groupBy=CustomerName;columns=InvoiceId,CustomerName,TotalAmount;headerexpr3=Invoice Total;groupfooterexpr1=SubTotal;groupfooterexpr3==\"SubTotal: \" & Sum(Fields!TotalAmount.Value);footerexpr1=Grand Total;footerexpr3==\"Grand Total: \" & Sum(Fields!TotalAmount.Value)"
                },
                new LayoutOperation
                {
                    Op = "add_chart",
                    Name = "chtInvoiceTotals",
                    X = "0.2in",
                    Y = "3.1in",
                    Width = "6.8in",
                    Height = "2.4in",
                    ValueExpression = "dataset=InvoiceData;chartType=Column;category=CustomerName;values=TotalAmount;aggregate=Sum;title=Invoice Totals by Customer"
                }
            ],
            createdBy: "self-test");

        events.Add(new { step = "report_patch_layout", patchLayout.Ok, patchLayout.Message, patchLayout.ReportPath });
        if (!patchLayout.Ok || string.IsNullOrWhiteSpace(patchLayout.ReportPath))
        {
            return PersistAndExit(events, 3);
        }

        var patchStyle = ReportTools.report_patch_style(
            _documents,
            _validation,
            _layoutIntelligence,
            reportPath: patchLayout.ReportPath,
            targets:
            [
                new StyleTarget { TargetRef = "textbox:txtTitle" },
                new StyleTarget { Selector = "type:Table" },
                new StyleTarget { Selector = "type:Chart" }
            ],
            styleOps:
            [
                new StyleOperation { Property = "fontWeight", Value = "Bold" },
                new StyleOperation { Property = "fontSize", Value = "11pt" },
                new StyleOperation { Property = "paddingLeft", Value = "2pt" }
            ],
            createdBy: "self-test");

        events.Add(new { step = "report_patch_style", patchStyle.Ok, patchStyle.Message, patchStyle.ReportPath });
        if (!patchStyle.Ok || string.IsNullOrWhiteSpace(patchStyle.ReportPath))
        {
            return PersistAndExit(events, 31);
        }

        var patchFormatting = ReportTools.report_patch_formatting(
            _documents,
            _validation,
            _layoutIntelligence,
            reportPath: patchStyle.ReportPath,
            formatRules:
            [
                new FormatRule { FieldRef = "TotalAmount", FormatString = "C2", Locale = "en-US", NullDisplay = "N/A" }
            ],
            createdBy: "self-test");

        events.Add(new { step = "report_patch_formatting", patchFormatting.Ok, patchFormatting.Message, patchFormatting.ReportPath });
        if (!patchFormatting.Ok || string.IsNullOrWhiteSpace(patchFormatting.ReportPath))
        {
            return PersistAndExit(events, 32);
        }

        var layoutModel = ReportTools.report_extract_layout_model(
            _layoutIntelligence,
            reportPath: patchFormatting.ReportPath);

        events.Add(new { step = "report_extract_layout_model", layoutModel.Ok, layoutModel.Message });

        var layoutScore = ReportTools.report_layout_score(
            _layoutIntelligence,
            reportPath: patchFormatting.ReportPath,
            rulePackVersion: "layout-v1.1");

        events.Add(new
        {
            step = "report_layout_score",
            layoutScore.Ok,
            layoutScore.Message,
            score = layoutScore.Artifacts.TryGetValue("layoutScore", out var scoreObj) ? scoreObj : null
        });

        var refine = ReportTools.report_auto_refine_layout(
            _documents,
            _validation,
            _layoutIntelligence,
            reportPath: patchFormatting.ReportPath,
            maxIterations: 2,
            targetScore: 75,
            createdBy: "self-test");

        events.Add(new { step = "report_auto_refine_layout", refine.Ok, refine.Message, refine.ReportPath });
        if (!refine.Ok || string.IsNullOrWhiteSpace(refine.ReportPath))
        {
            return PersistAndExit(events, 33);
        }

        var validate = ReportTools.report_validate(
            _validation,
            _runtime,
            reportPath: refine.ReportPath,
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
                _runtime,
                reportPath: refine.ReportPath,
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
            _documents,
            _validation,
            _runtime,
            reportPath: refine.ReportPath,
            saveComment: "self-test canonical save",
            includeRuntime: false,
            createdBy: "self-test");

        events.Add(new { step = "report_save_canonical", save.Ok, save.Message, save.ReportPath });
        if (!save.Ok || string.IsNullOrWhiteSpace(save.ReportPath))
        {
            return PersistAndExit(events, 4);
        }

        var handoff = ReportTools.report_handoff_summary(
            _validation,
            _runtime,
            reportPath: save.ReportPath);

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

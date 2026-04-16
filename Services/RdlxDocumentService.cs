using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using RdlxMcpServer.Models;

namespace RdlxMcpServer.Services;

public sealed partial class RdlxDocumentService
{
    public const string DefaultNamespaceUri = "http://schemas.microsoft.com/sqlserver/reporting/2008/01/reportdefinition";
    private const string DesignerNamespaceUri = "http://schemas.microsoft.com/SQLServer/reporting/reportdesigner";

    public string CreateSkeleton(string name, string reportType, Dictionary<string, string>? pageSettings)
    {
        var pageWidth = GetSetting(pageSettings, "PageWidth", "8.5in");
        var pageHeight = GetSetting(pageSettings, "PageHeight", "11in");
        var margin = GetSetting(pageSettings, "Margin", "0.5in");

        XNamespace ns = DefaultNamespaceUri;
        XNamespace rd = DesignerNamespaceUri;

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "Report",
                new XAttribute("Name", NormalizeName(name, "Report")),
                new XAttribute(XNamespace.Xmlns + "rd", rd),
                new XElement(ns + "AutoRefresh", "0"),
                new XElement(ns + "DataSources"),
                new XElement(ns + "DataSets"),
                new XElement(ns + "Body",
                    new XElement(ns + "ReportItems",
                        new XElement(ns + "Textbox",
                            new XAttribute("Name", "txtTitle"),
                            new XElement(ns + "Top", "0.1in"),
                            new XElement(ns + "Left", "0.1in"),
                            new XElement(ns + "Height", "0.3in"),
                            new XElement(ns + "Width", "3in"),
                            new XElement(ns + "Value", $"=\"{EscapeExpressionLiteral(name)}\""))),
                    new XElement(ns + "Height", "2in")),
                new XElement(ns + "Width", "6.5in"),
                new XElement(ns + "Page",
                    new XElement(ns + "PageHeight", pageHeight),
                    new XElement(ns + "PageWidth", pageWidth),
                    new XElement(ns + "LeftMargin", margin),
                    new XElement(ns + "RightMargin", margin),
                    new XElement(ns + "TopMargin", margin),
                    new XElement(ns + "BottomMargin", margin)),
                new XElement(ns + "Language", "en-US"),
                new XElement(ns + "ConsumeContainerWhitespace", "true")));

        return Canonicalize(document.ToString(SaveOptions.DisableFormatting));
    }

    public PatchResult ApplyLayoutOperations(string rdlx, IReadOnlyList<LayoutOperation> operations)
    {
        var diagnostics = new List<DiagnosticEntry>();
        var document = XDocument.Parse(rdlx, LoadOptions.None);
        var root = document.Root;
        var reportItems = EnsureReportItemsContainer(root);

        foreach (var op in operations)
        {
            switch (op.Op.Trim().ToLowerInvariant())
            {
                case "add_textbox":
                    AddTextbox(reportItems, op, diagnostics);
                    EnsureBodyHeight(root, op.Y, op.Height);
                    break;
                case "update_textbox_value":
                    UpdateTextboxValue(reportItems, op, diagnostics);
                    break;
                case "move_item":
                    MoveItem(root, reportItems, op, diagnostics);
                    break;
                case "add_table":
                    AddTable(root, reportItems, op, diagnostics);
                    break;
                case "add_tablix":
                    AddTablix(root, reportItems, op, diagnostics);
                    break;
                case "add_chart":
                    AddChart(root, reportItems, op, diagnostics);
                    break;
                default:
                    diagnostics.Add(new DiagnosticEntry
                    {
                        Stage = "lint",
                        Severity = "Warning",
                        Code = "OP_UNSUPPORTED",
                        Message = $"Unsupported layout operation '{op.Op}'.",
                        Owner = op.TargetRef ?? op.Name
                    });
                    break;
            }
        }

        return new PatchResult
        {
            Rdlx = Canonicalize(document.ToString(SaveOptions.DisableFormatting)),
            Diagnostics = diagnostics
        };
    }

    public PatchResult ApplyDataOperations(string rdlx, IReadOnlyList<DataOperation> operations)
    {
        var diagnostics = new List<DiagnosticEntry>();
        var document = XDocument.Parse(rdlx, LoadOptions.None);
        var root = document.Root ?? throw new InvalidOperationException("Invalid RDLX: missing root element.");
        var ns = root.Name.Namespace;

        var dataSources = EnsureChild(root, ns + "DataSources");
        var dataSets = EnsureChild(root, ns + "DataSets");

        foreach (var op in operations)
        {
            switch (op.Op.Trim().ToLowerInvariant())
            {
                case "upsert_data_source":
                    UpsertDataSource(dataSources, ns, op);
                    break;
                case "upsert_dataset":
                    UpsertDataSet(dataSets, ns, op);
                    break;
                case "add_parameter":
                    AddParameter(root, ns, op);
                    break;
                default:
                    diagnostics.Add(new DiagnosticEntry
                    {
                        Stage = "lint",
                        Severity = "Warning",
                        Code = "OP_UNSUPPORTED",
                        Message = $"Unsupported data operation '{op.Op}'.",
                        Owner = op.TargetRef ?? op.Name
                    });
                    break;
            }
        }

        return new PatchResult
        {
            Rdlx = Canonicalize(document.ToString(SaveOptions.DisableFormatting)),
            Diagnostics = diagnostics
        };
    }

    public ReportStructure BuildStructure(string rdlx)
    {
        var document = XDocument.Parse(rdlx, LoadOptions.None);
        var root = document.Root ?? throw new InvalidOperationException("Invalid RDLX: missing root element.");
        var ns = root.Name.Namespace;

        var reportItems = root
            .Element(ns + "Body")?
            .Element(ns + "ReportItems")?
            .Elements()
            .ToList() ?? [];

        var nodes = reportItems.Select(item => new StructureNode
        {
            Ref = $"{item.Name.LocalName.ToLowerInvariant()}:{item.Attribute("Name")?.Value ?? "unnamed"}",
            Type = item.Name.LocalName,
            Name = item.Attribute("Name")?.Value ?? "unnamed",
            X = item.Element(ns + "Left")?.Value,
            Y = item.Element(ns + "Top")?.Value,
            Width = item.Element(ns + "Width")?.Value,
            Height = item.Element(ns + "Height")?.Value
        }).ToList();

        return new ReportStructure
        {
            NamespaceUri = root.Name.NamespaceName,
            ReportItemCount = nodes.Count,
            Items = nodes
        };
    }

    public string Canonicalize(string rdlx)
    {
        var document = XDocument.Parse(rdlx, LoadOptions.None);
        if (document.Root is null)
        {
            throw new InvalidOperationException("Invalid RDLX: missing root element.");
        }

        CanonicalizeElement(document.Root);

        var settings = new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(false),
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace
        };

        using var stringWriter = new StringWriter();
        using var xmlWriter = XmlWriter.Create(stringWriter, settings);
        document.Save(xmlWriter);
        xmlWriter.Flush();
        return stringWriter.ToString();
    }

    public static string ComputeHash(string canonicalRdlx)
    {
        var bytes = Encoding.UTF8.GetBytes(canonicalRdlx);
        var hashBytes = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public object BuildDiffSummary(string fromRdlx, string toRdlx)
    {
        var fromCanonical = Canonicalize(fromRdlx);
        var toCanonical = Canonicalize(toRdlx);

        var fromDoc = XDocument.Parse(fromCanonical, LoadOptions.None);
        var toDoc = XDocument.Parse(toCanonical, LoadOptions.None);

        var fromNames = GetReportItemNames(fromDoc.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toNames = GetReportItemNames(toDoc.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = toNames.Except(fromNames).OrderBy(x => x).ToArray();
        var removed = fromNames.Except(toNames).OrderBy(x => x).ToArray();

        return new
        {
            changed = !string.Equals(fromCanonical, toCanonical, StringComparison.Ordinal),
            fromHash = ComputeHash(fromCanonical),
            toHash = ComputeHash(toCanonical),
            addedReportItems = added,
            removedReportItems = removed,
            addedCount = added.Length,
            removedCount = removed.Length
        };
    }

    private static string GetSetting(Dictionary<string, string>? settings, string key, string fallback)
    {
        if (settings is not null && settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return fallback;
    }

    private static string NormalizeName(string? input, string fallback)
    {
        var raw = string.IsNullOrWhiteSpace(input) ? fallback : input.Trim();
        var sanitized = Regex.Replace(raw, "[^A-Za-z0-9_]", "_");
        if (char.IsDigit(sanitized[0]))
        {
            sanitized = $"R_{sanitized}";
        }

        return sanitized;
    }

    private static string EscapeExpressionLiteral(string input)
    {
        return input.Replace("\"", "\"\"");
    }

    private static XElement EnsureReportItemsContainer(XElement? root)
    {
        if (root is null)
        {
            throw new InvalidOperationException("Invalid RDLX: missing root element.");
        }

        var ns = root.Name.Namespace;
        var body = EnsureChild(root, ns + "Body");
        return EnsureChild(body, ns + "ReportItems");
    }

    private static XElement EnsureChild(XElement parent, XName childName)
    {
        var child = parent.Element(childName);
        if (child is not null)
        {
            return child;
        }

        child = new XElement(childName);
        parent.Add(child);
        return child;
    }

    private static void AddTextbox(XElement reportItems, LayoutOperation op, List<DiagnosticEntry> diagnostics)
    {
        var root = reportItems.Parent?.Parent;
        var ns = reportItems.Name.Namespace;
        var textBoxName = NormalizeName(op.Name, $"txt_{Guid.NewGuid().ToString("N")[..8]}");

        if (reportItems.Elements().Any(x => string.Equals(x.Attribute("Name")?.Value, textBoxName, StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "lint",
                Severity = "Warning",
                Code = "NAME_COLLISION",
                Message = $"Textbox '{textBoxName}' already exists. Creating a unique name.",
                Owner = textBoxName
            });

            textBoxName = $"{textBoxName}_{Guid.NewGuid().ToString("N")[..4]}";
        }

        var textbox = new XElement(ns + "Textbox",
            new XAttribute("Name", textBoxName),
            new XElement(ns + "Top", op.Y ?? "0.2in"),
            new XElement(ns + "Left", op.X ?? "0.2in"),
            new XElement(ns + "Height", op.Height ?? "0.25in"),
            new XElement(ns + "Width", op.Width ?? "2in"),
            new XElement(ns + "Value", op.ValueExpression ?? "=\"\""));

        if (root is not null)
        {
            EnsureFitsPrintableWidth(root, textbox, diagnostics, textBoxName, null);
        }

        reportItems.Add(textbox);
    }

    private static void AddTable(XElement? root, XElement reportItems, LayoutOperation op, List<DiagnosticEntry> diagnostics)
    {
        if (root is null)
        {
            throw new InvalidOperationException("Invalid RDLX: missing root element.");
        }

        var ns = reportItems.Name.Namespace;
        var options = ParseOperationOptions(op.ValueExpression);
        var tableName = NormalizeName(op.Name, $"tbl_{Guid.NewGuid().ToString("N")[..8]}");

        if (reportItems.Elements().Any(x => string.Equals(x.Attribute("Name")?.Value, tableName, StringComparison.OrdinalIgnoreCase)))
        {
            tableName = $"{tableName}_{Guid.NewGuid().ToString("N")[..4]}";
        }

        var datasetName = ResolveDataSetName(root, ns, options);
        if (datasetName is null)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "schema",
                Severity = "Error",
                Code = "DATASET_NOT_FOUND",
                Message = "No dataset found for add_table operation. Provide dataset=<DataSetName> in valueExpression.",
                Owner = tableName
            });
            return;
        }

        var columns = ResolveColumns(root, ns, datasetName, options);
        if (columns.Count == 0)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "schema",
                Severity = "Error",
                Code = "COLUMNS_NOT_FOUND",
                Message = "No columns found for add_table operation.",
                Owner = tableName
            });
            return;
        }

        var groupBy = ResolveGroupBy(options);
        var table = BuildTable(root, ns, tableName, datasetName, columns, groupBy, op, diagnostics, options);
        reportItems.Add(table);
        EnsureBodyHeight(root, table.Element(ns + "Top")?.Value, table.Element(ns + "Height")?.Value);
    }

    private static void AddTablix(XElement? root, XElement reportItems, LayoutOperation op, List<DiagnosticEntry> diagnostics)
    {
        if (root is null)
        {
            throw new InvalidOperationException("Invalid RDLX: missing root element.");
        }

        var ns = reportItems.Name.Namespace;
        var options = ParseOperationOptions(op.ValueExpression);
        var tableName = NormalizeName(op.Name, $"tbl_{Guid.NewGuid().ToString("N")[..8]}");

        if (reportItems.Elements().Any(x => string.Equals(x.Attribute("Name")?.Value, tableName, StringComparison.OrdinalIgnoreCase)))
        {
            tableName = $"{tableName}_{Guid.NewGuid().ToString("N")[..4]}";
        }

        var datasetName = ResolveDataSetName(root, ns, options);
        if (datasetName is null)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "schema",
                Severity = "Error",
                Code = "DATASET_NOT_FOUND",
                Message = "No dataset found for add_table operation. Provide dataset=<DataSetName> in valueExpression.",
                Owner = tableName
            });
            return;
        }

        var columns = ResolveColumns(root, ns, datasetName, options);
        if (columns.Count == 0)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "schema",
                Severity = "Error",
                Code = "COLUMNS_NOT_FOUND",
                Message = "No columns found for add_table operation.",
                Owner = tableName
            });
            return;
        }

        var groupBy = ResolveGroupBy(options);
        var tablix = BuildTablix(root, ns, tableName, datasetName, columns, groupBy, op, diagnostics, options);
        reportItems.Add(tablix);
        EnsureBodyHeight(root, tablix.Element(ns + "Top")?.Value, tablix.Element(ns + "Height")?.Value);
    }

    private static void AddChart(XElement? root, XElement reportItems, LayoutOperation op, List<DiagnosticEntry> diagnostics)
    {
        if (root is null)
        {
            throw new InvalidOperationException("Invalid RDLX: missing root element.");
        }

        var ns = reportItems.Name.Namespace;
        var options = ParseOperationOptions(op.ValueExpression);
        var chartName = NormalizeName(op.Name, $"cht_{Guid.NewGuid().ToString("N")[..8]}");

        if (reportItems.Elements().Any(x => string.Equals(x.Attribute("Name")?.Value, chartName, StringComparison.OrdinalIgnoreCase)))
        {
            chartName = $"{chartName}_{Guid.NewGuid().ToString("N")[..4]}";
        }

        var datasetName = ResolveDataSetName(root, ns, options);
        if (datasetName is null)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "schema",
                Severity = "Error",
                Code = "DATASET_NOT_FOUND",
                Message = "No dataset found for add_chart operation. Provide dataset=<DataSetName> in valueExpression.",
                Owner = chartName
            });
            return;
        }

        var columns = ResolveColumns(root, ns, datasetName, options);
        if (columns.Count == 0)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "schema",
                Severity = "Error",
                Code = "COLUMNS_NOT_FOUND",
                Message = "No columns found for add_chart operation.",
                Owner = chartName
            });
            return;
        }

        var requestedType = GetOption(options, "chartType", "type");
        var chartType = ResolveChartType(requestedType);
        if (!string.IsNullOrWhiteSpace(requestedType)
            && !string.Equals(chartType, requestedType, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "lint",
                Severity = "Warning",
                Code = "CHART_TYPE_NORMALIZED",
                Message = $"Chart type '{requestedType}' was normalized to '{chartType}'.",
                Owner = chartName
            });
        }

        var aggregateFunction = ResolveAggregateFunction(GetOption(options, "aggregate", "agg"));
        var bindings = ResolveChartValueBindings(columns, options, aggregateFunction, diagnostics, chartName);
        if (bindings.Count == 0)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "schema",
                Severity = "Error",
                Code = "CHART_VALUES_NOT_FOUND",
                Message = "No chart value bindings could be resolved.",
                Owner = chartName
            });
            return;
        }

        var seriesExpression = ResolveChartSeriesExpression(options);
        if (!string.IsNullOrWhiteSpace(seriesExpression) && bindings.Count > 1)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "lint",
                Severity = "Warning",
                Code = "CHART_DYNAMIC_SERIES_MULTI_VALUE",
                Message = "Dynamic series grouping only supports one value expression. Using the first configured value.",
                Owner = chartName
            });
            bindings = [bindings[0]];
        }

        var categoryExpression = ResolveChartCategoryExpression(columns, bindings, options, diagnostics, chartName);
        var legendVisible = ParseBooleanOption(GetOption(options, "legend", "showLegend"), fallback: true);
        var palette = GetOption(options, "palette") ?? "Default";

        var fit = GetPrintableFitBounds(root, options);
        var requestedWidth = ParseMeasurementAsInches(op.Width) ?? 4.8;
        var chartWidth = fit.AvailableWidth is > 0
            ? Math.Min(requestedWidth, fit.AvailableWidth.Value)
            : requestedWidth;

        var chart = BuildChart(
            root,
            ns,
            chartName,
            datasetName,
            chartType,
            categoryExpression,
            seriesExpression,
            bindings,
            legendVisible,
            palette,
            op,
            chartWidth,
            diagnostics,
            options);

        reportItems.Add(chart);
        EnsureBodyHeight(root, chart.Element(ns + "Top")?.Value, chart.Element(ns + "Height")?.Value);
    }

    private static XElement BuildChart(
        XElement root,
        XNamespace ns,
        string chartName,
        string dataSetName,
        string chartType,
        string categoryExpression,
        string? seriesExpression,
        IReadOnlyList<(string Label, string ValueExpression)> bindings,
        bool legendVisible,
        string palette,
        LayoutOperation op,
        double chartWidth,
        List<DiagnosticEntry> diagnostics,
        IReadOnlyDictionary<string, string> options)
    {
        var top = op.Y ?? "1.2in";
        var left = op.X ?? "0.2in";
        var height = string.IsNullOrWhiteSpace(op.Height) ? "2.4in" : op.Height;

        var chartData = new XElement(ns + "ChartData",
            bindings.Select(binding =>
                new XElement(ns + "ChartSeries",
                    new XElement(ns + "DataPoints",
                        new XElement(ns + "DataPoint",
                            new XElement(ns + "DataLabel"),
                            new XElement(ns + "DataValues",
                                new XElement(ns + "DataValue",
                                    new XElement(ns + "Value", binding.ValueExpression))),
                            new XElement(ns + "Marker"))))));

        XElement seriesGroupings;
        if (string.IsNullOrWhiteSpace(seriesExpression))
        {
            seriesGroupings = new XElement(ns + "SeriesGroupings",
                new XElement(ns + "SeriesGrouping",
                    new XElement(ns + "StaticSeries",
                        bindings.Select(binding =>
                            new XElement(ns + "StaticMember",
                                new XElement(ns + "Label", binding.Label))))));
        }
        else
        {
            var seriesField = DeriveFieldNameFromExpression(seriesExpression) ?? "Series";
            seriesGroupings = new XElement(ns + "SeriesGroupings",
                new XElement(ns + "SeriesGrouping",
                    new XElement(ns + "DynamicSeries",
                        new XElement(ns + "Grouping",
                            new XAttribute("Name", NormalizeName($"{chartName}_{seriesField}_SeriesGroup", "SeriesGroup")),
                            new XElement(ns + "GroupExpressions",
                                new XElement(ns + "GroupExpression", seriesExpression)),
                            new XElement(ns + "Label", seriesExpression)),
                        new XElement(ns + "Label", seriesExpression))));
        }

        var title = GetOption(options, "title");
        var chart = new XElement(ns + "Chart",
            new XAttribute("Name", chartName),
            new XElement(ns + "CategoryAxis",
                new XElement(ns + "Axis",
                    new XElement(ns + "Title"),
                    new XElement(ns + "Visible", "true"))),
            new XElement(ns + "CategoryGroupings",
                new XElement(ns + "CategoryGrouping",
                    new XElement(ns + "DynamicCategories",
                        new XElement(ns + "Grouping",
                            new XAttribute("Name", NormalizeName($"{chartName}_CategoryGroup", "CategoryGroup")),
                            new XElement(ns + "GroupExpressions",
                                new XElement(ns + "GroupExpression", categoryExpression)),
                            new XElement(ns + "Label", categoryExpression)),
                        new XElement(ns + "Label", categoryExpression)))),
            chartData,
            new XElement(ns + "Top", top),
            new XElement(ns + "Left", left),
            new XElement(ns + "Width", FormatInches(chartWidth)),
            new XElement(ns + "Height", height),
            new XElement(ns + "DataSetName", dataSetName),
            new XElement(ns + "Legend",
                new XElement(ns + "Position", "RightCenter"),
                new XElement(ns + "Visible", legendVisible ? "true" : "false")),
            new XElement(ns + "Palette", palette),
            new XElement(ns + "PlotArea",
                new XElement(ns + "Style",
                    new XElement(ns + "BackgroundColor", "Transparent"))),
            seriesGroupings,
            new XElement(ns + "Subtype", "Plain"),
            new XElement(ns + "Title",
                new XElement(ns + "Caption", string.IsNullOrWhiteSpace(title) ? "" : title),
                new XElement(ns + "Style",
                    new XElement(ns + "FontSize", "9pt"),
                    new XElement(ns + "TextAlign", "Center"))),
            new XElement(ns + "Type", chartType),
            new XElement(ns + "ValueAxis",
                new XElement(ns + "Axis",
                    new XElement(ns + "Title"),
                    new XElement(ns + "Visible", "true"))),
            new XElement(ns + "Style",
                new XElement(ns + "FontFamily", "Arial"),
                new XElement(ns + "FontSize", "9pt")));

        EnsureFitsPrintableWidth(root, chart, diagnostics, chartName, options);
        return chart;
    }

    private static List<(string Label, string ValueExpression)> ResolveChartValueBindings(
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<string, string> options,
        string aggregateFunction,
        List<DiagnosticEntry> diagnostics,
        string owner)
    {
        var configured = GetOption(options, "values", "value");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var bindings = configured
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select((token, index) => BuildChartBindingFromToken(token, aggregateFunction, index + 1))
                .Where(binding => !string.IsNullOrWhiteSpace(binding.ValueExpression))
                .DistinctBy(binding => binding.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (bindings.Count > 0)
            {
                return bindings;
            }
        }

        var numericCandidates = columns.Where(LooksNumericFieldName).ToList();
        var fallbackColumns = numericCandidates.Count > 0
            ? numericCandidates
            : columns.Skip(1).Take(3).ToList();

        if (fallbackColumns.Count == 0 && columns.Count > 0)
        {
            fallbackColumns = [columns[0]];
        }

        var result = fallbackColumns
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select((column, index) => BuildChartBindingFromToken(column, aggregateFunction, index + 1))
            .ToList();

        if (result.Count > 0)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "layout",
                Severity = "Information",
                Code = "CHART_VALUES_INFERRED",
                Message = "Chart value series were inferred from dataset fields.",
                Owner = owner
            });
        }

        return result;
    }

    private static (string Label, string ValueExpression) BuildChartBindingFromToken(string token, string aggregateFunction, int index)
    {
        var trimmed = token.Trim();
        if (trimmed.StartsWith('='))
        {
            var labelFromExpression = DeriveFieldNameFromExpression(trimmed);
            var label = string.IsNullOrWhiteSpace(labelFromExpression)
                ? $"Series {index}"
                : TitleizeColumnName(labelFromExpression);
            return (label, trimmed);
        }

        var fieldName = NormalizeName(trimmed, $"Value{index}");
        return (TitleizeColumnName(fieldName), BuildAggregateFieldExpression(fieldName, aggregateFunction));
    }

    private static string ResolveChartCategoryExpression(
        IReadOnlyList<string> columns,
        IReadOnlyList<(string Label, string ValueExpression)> bindings,
        IReadOnlyDictionary<string, string> options,
        List<DiagnosticEntry> diagnostics,
        string owner)
    {
        var configuredCategory = GetOption(options, "category", "categories", "categoryField");
        if (!string.IsNullOrWhiteSpace(configuredCategory))
        {
            return AsFieldExpression(configuredCategory, "Category");
        }

        var valueFields = bindings
            .Select(binding => DeriveFieldNameFromExpression(binding.ValueExpression))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var categoryField = columns.FirstOrDefault(column => !valueFields.Contains(column))
            ?? columns.FirstOrDefault()
            ?? "Category";

        diagnostics.Add(new DiagnosticEntry
        {
            Stage = "layout",
            Severity = "Information",
            Code = "CHART_CATEGORY_INFERRED",
            Message = $"Chart category was inferred as '{categoryField}'.",
            Owner = owner
        });

        return AsFieldExpression(categoryField, "Category");
    }

    private static string? ResolveChartSeriesExpression(IReadOnlyDictionary<string, string> options)
    {
        var configured = GetOption(options, "series", "seriesGroup", "seriesField");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        return AsFieldExpression(configured, "Series");
    }

    private static string ResolveChartType(string? chartType)
    {
        return chartType?.Trim().ToLowerInvariant() switch
        {
            "column" or "clusteredcolumn" => "Column",
            "bar" => "Bar",
            "line" => "Line",
            "area" => "Area",
            "pie" => "Pie",
            "doughnut" or "donut" => "Doughnut",
            "scatter" or "point" => "Scatter",
            "bubble" => "Bubble",
            "stock" => "Stock",
            "" or null => "Column",
            _ => "Column"
        };
    }

    private static string ResolveAggregateFunction(string? aggregate)
    {
        return aggregate?.Trim().ToLowerInvariant() switch
        {
            "sum" => "Sum",
            "avg" or "average" => "Avg",
            "min" => "Min",
            "max" => "Max",
            "count" => "Count",
            "countdistinct" => "CountDistinct",
            "first" => "First",
            "last" => "Last",
            _ => "Sum"
        };
    }

    private static bool ParseBooleanOption(string? value, bool fallback)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "y" => true,
            "false" or "0" or "no" or "n" => false,
            _ => fallback
        };
    }

    private static string? GetOption(IReadOnlyDictionary<string, string> options, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string BuildAggregateFieldExpression(string fieldName, string aggregateFunction)
    {
        return $"={aggregateFunction}(Fields!{fieldName}.Value)";
    }

    private static string AsFieldExpression(string token, string fallbackField)
    {
        var trimmed = token.Trim();
        if (trimmed.StartsWith('='))
        {
            return trimmed;
        }

        var fieldName = NormalizeName(trimmed, fallbackField);
        return $"=Fields!{fieldName}.Value";
    }

    private static bool LooksNumericFieldName(string fieldName)
    {
        var normalized = fieldName.Trim().ToLowerInvariant();
        return normalized.Contains("amount", StringComparison.Ordinal)
            || normalized.Contains("total", StringComparison.Ordinal)
            || normalized.Contains("price", StringComparison.Ordinal)
            || normalized.Contains("cost", StringComparison.Ordinal)
            || normalized.Contains("revenue", StringComparison.Ordinal)
            || normalized.Contains("sales", StringComparison.Ordinal)
            || normalized.Contains("qty", StringComparison.Ordinal)
            || normalized.Contains("quantity", StringComparison.Ordinal)
            || normalized.Contains("count", StringComparison.Ordinal)
            || normalized.Contains("score", StringComparison.Ordinal)
            || normalized.Contains("rate", StringComparison.Ordinal)
            || normalized.Contains("percent", StringComparison.Ordinal)
            || normalized.Contains("pct", StringComparison.Ordinal);
    }

    private static void UpdateTextboxValue(XElement reportItems, LayoutOperation op, List<DiagnosticEntry> diagnostics)
    {
        var ns = reportItems.Name.Namespace;
        var targetName = ResolveTargetName(op.TargetRef ?? op.Name);
        var textbox = reportItems.Elements(ns + "Textbox")
            .FirstOrDefault(x => string.Equals(x.Attribute("Name")?.Value, targetName, StringComparison.OrdinalIgnoreCase));

        if (textbox is null)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "schema",
                Severity = "Error",
                Code = "TARGET_NOT_FOUND",
                Message = $"Could not find textbox '{targetName}'.",
                Owner = targetName
            });
            return;
        }

        EnsureChild(textbox, ns + "Value").Value = op.ValueExpression ?? "=\"\"";
    }

    private static void MoveItem(XElement? root, XElement reportItems, LayoutOperation op, List<DiagnosticEntry> diagnostics)
    {
        var ns = reportItems.Name.Namespace;
        var targetName = ResolveTargetName(op.TargetRef ?? op.Name);
        var item = reportItems.Elements()
            .FirstOrDefault(x => string.Equals(x.Attribute("Name")?.Value, targetName, StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "schema",
                Severity = "Error",
                Code = "TARGET_NOT_FOUND",
                Message = $"Could not find report item '{targetName}'.",
                Owner = targetName
            });
            return;
        }

        if (!string.IsNullOrWhiteSpace(op.X))
        {
            EnsureChild(item, ns + "Left").Value = op.X;
        }

        if (!string.IsNullOrWhiteSpace(op.Y))
        {
            EnsureChild(item, ns + "Top").Value = op.Y;
        }

        if (root is not null)
        {
            EnsureFitsPrintableWidth(root, item, diagnostics, targetName, null);
        }
    }

    private static void UpsertDataSource(XElement dataSources, XNamespace ns, DataOperation op)
    {
        var name = NormalizeName(op.Name, "DataSource1");
        var existing = dataSources.Elements(ns + "DataSource")
            .FirstOrDefault(x => string.Equals(x.Attribute("Name")?.Value, name, StringComparison.OrdinalIgnoreCase));

        var dataSource = existing ?? new XElement(ns + "DataSource", new XAttribute("Name", name));
        var connectionProperties = EnsureChild(dataSource, ns + "ConnectionProperties");
        EnsureChild(connectionProperties, ns + "DataProvider").Value = op.DataProvider ?? "SQL";
        EnsureChild(connectionProperties, ns + "ConnectString").Value = op.ConnectionString ?? "";

        if (existing is null)
        {
            dataSources.Add(dataSource);
        }
    }

    private static void UpsertDataSet(XElement dataSets, XNamespace ns, DataOperation op)
    {
        var name = NormalizeName(op.Name, "DataSet1");
        var existing = dataSets.Elements(ns + "DataSet")
            .FirstOrDefault(x => string.Equals(x.Attribute("Name")?.Value, name, StringComparison.OrdinalIgnoreCase));

        var dataSet = existing ?? new XElement(ns + "DataSet", new XAttribute("Name", name));
        var query = EnsureChild(dataSet, ns + "Query");
        EnsureChild(query, ns + "DataSourceName").Value = NormalizeName(op.DataSourceName, "DataSource1");
        EnsureChild(query, ns + "CommandText").Value = op.CommandText ?? "SELECT 1 AS Value";

        if (op.Fields is { Count: > 0 })
        {
            var fields = EnsureChild(dataSet, ns + "Fields");
            fields.RemoveNodes();
            foreach (var fieldName in op.Fields)
            {
                if (string.IsNullOrWhiteSpace(fieldName))
                {
                    continue;
                }

                var normalized = NormalizeName(fieldName, "Field");
                fields.Add(new XElement(ns + "Field",
                    new XAttribute("Name", normalized),
                    new XElement(ns + "DataField", fieldName)));
            }
        }

        if (existing is null)
        {
            dataSets.Add(dataSet);
        }
    }

    private static void AddParameter(XElement root, XNamespace ns, DataOperation op)
    {
        var parameters = EnsureChild(root, ns + "ReportParameters");
        var name = NormalizeName(op.Name, "Parameter1");

        if (parameters.Elements(ns + "ReportParameter")
            .Any(x => string.Equals(x.Attribute("Name")?.Value, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var parameter = new XElement(ns + "ReportParameter",
            new XAttribute("Name", name),
            new XElement(ns + "DataType", string.IsNullOrWhiteSpace(op.ParameterType) ? "String" : op.ParameterType),
            new XElement(ns + "Prompt", name));

        if (op.DefaultValue is not null)
        {
            parameter.Add(
                new XElement(ns + "DefaultValue",
                    new XElement(ns + "Values",
                        new XElement(ns + "Value", op.DefaultValue))));
        }

        parameters.Add(parameter);
    }

    private static string ResolveTargetName(string? targetRef)
    {
        if (string.IsNullOrWhiteSpace(targetRef))
        {
            return "";
        }

        var trimmed = targetRef.Trim();
        var idx = trimmed.IndexOf(':');
        return idx > -1 ? trimmed[(idx + 1)..] : trimmed;
    }

    private static Dictionary<string, string> ParseOperationOptions(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        var segments = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            var idx = segment.IndexOf('=');
            if (idx < 1 || idx == segment.Length - 1)
            {
                continue;
            }

            var key = segment[..idx].Trim();
            var value = segment[(idx + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0)
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    private static string? ResolveDataSetName(XElement root, XNamespace ns, IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("dataset", out var explicitDataset) && !string.IsNullOrWhiteSpace(explicitDataset))
        {
            return NormalizeName(explicitDataset, explicitDataset);
        }

        if (options.TryGetValue("datasetname", out var explicitDatasetName) && !string.IsNullOrWhiteSpace(explicitDatasetName))
        {
            return NormalizeName(explicitDatasetName, explicitDatasetName);
        }

        var first = root.Element(ns + "DataSets")?
            .Elements(ns + "DataSet")
            .Select(x => x.Attribute("Name")?.Value)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        return first;
    }

    private static List<string> ResolveColumns(
        XElement root,
        XNamespace ns,
        string dataSetName,
        IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("columns", out var configuredColumns) && !string.IsNullOrWhiteSpace(configuredColumns))
        {
            return configuredColumns
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var fields = root.Element(ns + "DataSets")?
            .Elements(ns + "DataSet")
            .FirstOrDefault(ds => string.Equals(ds.Attribute("Name")?.Value, dataSetName, StringComparison.OrdinalIgnoreCase))?
            .Element(ns + "Fields")?
            .Elements(ns + "Field")
            .Select(field => field.Attribute("Name")?.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToList();

        return fields ?? [];
    }

    private static string? ResolveGroupBy(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("groupby", out var groupBy) || string.IsNullOrWhiteSpace(groupBy))
        {
            return null;
        }

        var trimmed = groupBy.Trim();
        return trimmed.StartsWith('=')
            ? trimmed
            : $"=Fields!{trimmed}.Value";
    }

    private static XElement BuildTablix(
        XElement root,
        XNamespace ns,
        string tableName,
        string dataSetName,
        IReadOnlyList<string> columns,
        string? groupByExpression,
        LayoutOperation op,
        List<DiagnosticEntry> diagnostics,
        IReadOnlyDictionary<string, string> options)
    {
        var fit = GetPrintableFitBounds(root, options);
        var requestedWidth = ParseMeasurementAsInches(op.Width) ?? (columns.Count * 1.8);
        var tableWidth = fit.AvailableWidth is > 0
            ? Math.Min(requestedWidth, fit.AvailableWidth.Value)
            : requestedWidth;
        var columnWidth = tableWidth / Math.Max(columns.Count, 1);

        var tablixColumns = new XElement(ns + "TablixColumns",
            columns.Select(_ =>
                new XElement(ns + "TablixColumn",
                    new XElement(ns + "Width", FormatInches(columnWidth)))));

        var headerRow = BuildTablixRow(ns, columns.Select(TitleizeColumnName), $"{tableName}_hdr");

        XElement tablixRows;
        XElement rowHierarchy;

        if (!string.IsNullOrWhiteSpace(groupByExpression))
        {
            var groupFieldName = DeriveFieldNameFromExpression(groupByExpression) ?? "Group";
            var groupLabelExpression = groupByExpression;
            var groupRowValues = columns.Select((column, idx) =>
                idx == 0
                    ? groupLabelExpression
                    : "=\"\"").ToArray();
            var detailValues = columns.Select(column => $"=Fields!{column}.Value").ToArray();

            var groupRow = BuildTablixRow(ns, groupRowValues, $"{tableName}_grp");
            var detailRow = BuildTablixRow(ns, detailValues, $"{tableName}_dtl");

            tablixRows = new XElement(ns + "TablixRows", headerRow, groupRow, detailRow);

            var detailsMember = new XElement(ns + "TablixMember",
                new XElement(ns + "Group",
                    new XAttribute("Name", NormalizeName($"{tableName}_details", "Details"))));

            var groupHeaderMember = new XElement(ns + "TablixMember",
                new XElement(ns + "KeepWithGroup", "After"),
                new XElement(ns + "RepeatOnNewPage", "true"));

            var categoryGroupMember = new XElement(ns + "TablixMember",
                new XElement(ns + "Group",
                    new XAttribute("Name", NormalizeName($"{tableName}_{groupFieldName}_grp", "grp")),
                    new XElement(ns + "GroupExpressions",
                        new XElement(ns + "GroupExpression", groupByExpression))),
                new XElement(ns + "SortExpressions",
                    new XElement(ns + "SortExpression",
                        new XElement(ns + "Value", groupByExpression))),
                new XElement(ns + "TablixMembers", groupHeaderMember, detailsMember));

            rowHierarchy = new XElement(ns + "TablixRowHierarchy",
                new XElement(ns + "TablixMembers",
                    new XElement(ns + "TablixMember",
                        new XElement(ns + "KeepWithGroup", "After"),
                        new XElement(ns + "RepeatOnNewPage", "true")),
                    categoryGroupMember));
        }
        else
        {
            var detailValues = columns.Select(column => $"=Fields!{column}.Value").ToArray();
            var detailRow = BuildTablixRow(ns, detailValues, $"{tableName}_dtl");
            tablixRows = new XElement(ns + "TablixRows", headerRow, detailRow);

            rowHierarchy = new XElement(ns + "TablixRowHierarchy",
                new XElement(ns + "TablixMembers",
                    new XElement(ns + "TablixMember",
                        new XElement(ns + "KeepWithGroup", "After"),
                        new XElement(ns + "RepeatOnNewPage", "true")),
                    new XElement(ns + "TablixMember",
                        new XElement(ns + "Group",
                            new XAttribute("Name", NormalizeName($"{tableName}_details", "Details"))))));
        }

        var columnHierarchy = new XElement(ns + "TablixColumnHierarchy",
            new XElement(ns + "TablixMembers",
                columns.Select(_ => new XElement(ns + "TablixMember"))));

        if (!columns.Any(column => string.Equals(column, DeriveFieldNameFromExpression(groupByExpression), StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(groupByExpression))
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "lint",
                Severity = "Warning",
                Code = "GROUP_FIELD_NOT_IN_COLUMNS",
                Message = "Group field is not included in table columns; first column is used for group header rendering.",
                Owner = tableName
            });
        }

        var tablix = new XElement(ns + "Tablix",
            new XAttribute("Name", tableName),
            new XElement(ns + "Top", op.Y ?? "0.8in"),
            new XElement(ns + "Left", op.X ?? "0.2in"),
            // Keep design-time container compact. Row height is controlled by TableRow entries.
            new XElement(ns + "Height", string.IsNullOrWhiteSpace(groupByExpression) ? "0.5in" : "0.75in"),
            new XElement(ns + "Width", FormatInches(tableWidth)),
            new XElement(ns + "DataSetName", dataSetName),
            new XElement(ns + "TablixBody", tablixColumns, tablixRows),
            columnHierarchy,
            rowHierarchy);

        EnsureFitsPrintableWidth(root, tablix, diagnostics, tableName, options);

        return tablix;
    }

    private static XElement BuildTable(
        XElement root,
        XNamespace ns,
        string tableName,
        string dataSetName,
        IReadOnlyList<string> columns,
        string? groupByExpression,
        LayoutOperation op,
        List<DiagnosticEntry> diagnostics,
        IReadOnlyDictionary<string, string> options)
    {
        var fit = GetPrintableFitBounds(root, options);
        var requestedWidth = ParseMeasurementAsInches(op.Width) ?? (columns.Count * 1.8);
        var tableWidth = fit.AvailableWidth is > 0
            ? Math.Min(requestedWidth, fit.AvailableWidth.Value)
            : requestedWidth;
        var columnWidth = tableWidth / Math.Max(columns.Count, 1);

        var tableColumns = new XElement(ns + "TableColumns",
            columns.Select(_ =>
                new XElement(ns + "TableColumn",
                    new XElement(ns + "Width", FormatInches(columnWidth)))));

        var headerRow = BuildTableRow(ns, columns.Select(TitleizeColumnName), $"{tableName}_hdr");
        var detailValues = columns.Select(column => $"=Fields!{column}.Value").ToArray();
        var detailRow = BuildTableRow(ns, detailValues, $"{tableName}_dtl");

        var header = new XElement(ns + "Header",
            new XElement(ns + "TableRows", headerRow),
            new XElement(ns + "RepeatOnNewPage", "true"));

        var details = new XElement(ns + "Details",
            new XElement(ns + "TableRows", detailRow));

        XElement? tableGroups = null;
        if (!string.IsNullOrWhiteSpace(groupByExpression))
        {
            var groupFieldName = DeriveFieldNameFromExpression(groupByExpression) ?? "Group";
            var groupRowValues = columns.Select((_, idx) => idx == 0 ? groupByExpression : "=\"\"");
            var groupHeaderRow = BuildTableRow(ns, groupRowValues, $"{tableName}_grp");

            tableGroups = new XElement(ns + "TableGroups",
                new XElement(ns + "TableGroup",
                    new XElement(ns + "Grouping",
                        new XAttribute("Name", NormalizeName($"{tableName}_{groupFieldName}_grp", "grp")),
                        new XElement(ns + "GroupExpressions",
                            new XElement(ns + "GroupExpression", groupByExpression))),
                    new XElement(ns + "Header",
                        new XElement(ns + "TableRows", groupHeaderRow),
                        new XElement(ns + "RepeatOnNewPage", "true"))));

            if (!columns.Any(column => string.Equals(column, groupFieldName, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(new DiagnosticEntry
                {
                    Stage = "lint",
                    Severity = "Warning",
                    Code = "GROUP_FIELD_NOT_IN_COLUMNS",
                    Message = "Group field is not included in table columns; first column is used for group header rendering.",
                    Owner = tableName
                });
            }
        }

        var table = new XElement(ns + "Table",
            new XAttribute("Name", tableName),
            new XElement(ns + "Top", op.Y ?? "0.8in"),
            new XElement(ns + "Left", op.X ?? "0.2in"),
            new XElement(ns + "Height", string.IsNullOrWhiteSpace(groupByExpression) ? "0.5in" : "0.75in"),
            new XElement(ns + "Width", FormatInches(tableWidth)),
            new XElement(ns + "DataSetName", dataSetName),
            tableColumns,
            header,
            tableGroups,
            details);

        EnsureFitsPrintableWidth(root, table, diagnostics, tableName, options);

        return table;
    }

    private static XElement BuildTablixRow(XNamespace ns, IEnumerable<string> values, string namePrefix)
    {
        var cells = values.Select((value, index) =>
            new XElement(ns + "TablixCell",
                new XElement(ns + "CellContents",
                    new XElement(ns + "Textbox",
                        new XAttribute("Name", NormalizeName($"{namePrefix}_{index + 1}", "cell")),
                        new XElement(ns + "CanGrow", "true"),
                        new XElement(ns + "KeepTogether", "true"),
                        new XElement(ns + "Value", value)))));

        return new XElement(ns + "TablixRow",
            new XElement(ns + "Height", "0.25in"),
            new XElement(ns + "TablixCells", cells));
    }

    private static XElement BuildTableRow(XNamespace ns, IEnumerable<string> values, string namePrefix)
    {
        var cells = values.Select((value, index) =>
            new XElement(ns + "TableCell",
                new XElement(ns + "ReportItems",
                    new XElement(ns + "Textbox",
                        new XAttribute("Name", NormalizeName($"{namePrefix}_{index + 1}", "cell")),
                        new XElement(ns + "CanGrow", "true"),
                        new XElement(ns + "KeepTogether", "true"),
                        new XElement(ns + "Value", value)))));

        return new XElement(ns + "TableRow",
            new XElement(ns + "Height", "0.25in"),
            new XElement(ns + "TableCells", cells));
    }

    private static string TitleizeColumnName(string column)
    {
        if (string.IsNullOrWhiteSpace(column))
        {
            return "";
        }

        var withSpaces = Regex.Replace(column, "([a-z0-9])([A-Z])", "$1 $2");
        return withSpaces.Replace("_", " ");
    }

    private static string? DeriveFieldNameFromExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        var match = Regex.Match(expression, "Fields!([A-Za-z_][A-Za-z0-9_]*)\\.");
        return match.Success ? match.Groups[1].Value : null;
    }
}

using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using RdlxMcpServer.Models;

namespace RdlxMcpServer.Services;

public sealed partial class RdlxValidationService
{
    private static readonly HashSet<string> AllowedNamespaces =
    [
        "http://schemas.microsoft.com/sqlserver/reporting/2008/01/reportdefinition",
        "http://schemas.microsoft.com/sqlserver/reporting/2010/01/reportdefinition",
        "http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition"
    ];

    private static readonly HashSet<string> DimensionElements =
    [
        "Top",
        "Left",
        "Width",
        "Height",
        "PageWidth",
        "PageHeight",
        "LeftMargin",
        "RightMargin",
        "TopMargin",
        "BottomMargin"
    ];

    public ValidationReport Validate(string rdlx, ValidationLevel level = ValidationLevel.Full)
    {
        var diagnostics = new List<DiagnosticEntry>();
        XDocument document;

        try
        {
            document = XDocument.Parse(rdlx, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "parse",
                Severity = "Error",
                Code = "PARSE_ERROR",
                Message = ex.Message,
                Owner = $"line:{ex.LineNumber}"
            });
            return BuildReport(diagnostics);
        }

        var root = document.Root;
        if (root is null)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "parse",
                Severity = "Error",
                Code = "PARSE_ERROR",
                Message = "RDLX document has no root element."
            });
            return BuildReport(diagnostics);
        }

        ValidateProfile(root, diagnostics);

        if (level == ValidationLevel.ParseOnly)
        {
            return BuildReport(diagnostics);
        }

        ValidateSchema(root, diagnostics);
        ValidateLint(root, diagnostics, level);

        return BuildReport(diagnostics);
    }

    private static void ValidateProfile(XElement root, List<DiagnosticEntry> diagnostics)
    {
        if (!string.Equals(root.Name.LocalName, "Report", StringComparison.Ordinal))
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "profile",
                Severity = "Error",
                Code = "PROFILE_ERROR",
                Message = "Root element must be 'Report'.",
                Owner = root.Name.LocalName
            });
        }

        if (!AllowedNamespaces.Contains(root.Name.NamespaceName))
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "profile",
                Severity = "Error",
                Code = "PROFILE_ERROR",
                Message = $"Unsupported report namespace '{root.Name.NamespaceName}'."
            });
        }
    }

    private static void ValidateSchema(XElement root, List<DiagnosticEntry> diagnostics)
    {
        var ns = root.Name.Namespace;

        RequireElement(root, ns + "Body", diagnostics, "SCHEMA_ERROR", "Body");
        RequireElement(root, ns + "Width", diagnostics, "SCHEMA_ERROR", "Width");

        var body = root.Element(ns + "Body");
        if (body is not null)
        {
            RequireElement(body, ns + "ReportItems", diagnostics, "SCHEMA_ERROR", "Body/ReportItems");
            RequireElement(body, ns + "Height", diagnostics, "SCHEMA_ERROR", "Body/Height");
        }

        var page = root.Element(ns + "Page");
        if (page is null)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "schema",
                Severity = "Error",
                Code = "SCHEMA_ERROR",
                Message = "Missing required element 'Page'.",
                Owner = "Page"
            });
        }
        else
        {
            RequireElement(page, ns + "PageWidth", diagnostics, "SCHEMA_ERROR", "Page/PageWidth");
            RequireElement(page, ns + "PageHeight", diagnostics, "SCHEMA_ERROR", "Page/PageHeight");
        }

        var reportItems = body?.Element(ns + "ReportItems")?.Elements() ?? [];
        var duplicateNames = reportItems
            .Select(item => item.Attribute("Name")?.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);

        foreach (var duplicate in duplicateNames)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "schema",
                Severity = "Error",
                Code = "SCHEMA_ERROR",
                Message = $"Duplicate report item Name '{duplicate}'.",
                Owner = duplicate
            });
        }
    }

    private static void ValidateLint(XElement root, List<DiagnosticEntry> diagnostics, ValidationLevel level)
    {
        var ns = root.Name.Namespace;
        var datasets = root.Element(ns + "DataSets")?.Elements(ns + "DataSet").ToList() ?? [];
        var fieldNames = datasets
            .SelectMany(ds => ds.Element(ns + "Fields")?.Elements(ns + "Field") ?? [])
            .Select(field => field.Attribute("Name")?.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var element in root.Descendants())
        {
            if (DimensionElements.Contains(element.Name.LocalName)
                && !string.IsNullOrWhiteSpace(element.Value)
                && !MeasurementRegex().IsMatch(element.Value.Trim()))
            {
                diagnostics.Add(new DiagnosticEntry
                {
                    Stage = "lint",
                    Severity = "Warning",
                    Code = "LINT_DIMENSION_FORMAT",
                    Message = $"Value '{element.Value}' is not a standard measurement string.",
                    Owner = element.Name.LocalName
                });
            }
        }

        var values = root.Descendants(ns + "Value");
        foreach (var valueNode in values)
        {
            var value = valueNode.Value.Trim();
            if (!value.StartsWith("=", StringComparison.Ordinal))
            {
                continue;
            }

            var matches = FieldReferenceRegex().Matches(value);
            foreach (Match match in matches)
            {
                var referencedField = match.Groups[1].Value;
                if (fieldNames.Count == 0)
                {
                    diagnostics.Add(new DiagnosticEntry
                    {
                        Stage = "lint",
                        Severity = "Warning",
                        Code = "LINT_NO_DATASET_FIELDS",
                        Message = "Expression references fields but no dataset fields are defined.",
                        Owner = referencedField
                    });
                    continue;
                }

                if (!fieldNames.Contains(referencedField))
                {
                    diagnostics.Add(new DiagnosticEntry
                    {
                        Stage = "lint",
                        Severity = level == ValidationLevel.Lint ? "Error" : "Warning",
                        Code = "LINT_UNKNOWN_FIELD_REFERENCE",
                        Message = $"Expression references unknown field '{referencedField}'.",
                        Owner = referencedField
                    });
                }
            }
        }
    }

    private static void RequireElement(
        XElement parent,
        XName childName,
        List<DiagnosticEntry> diagnostics,
        string code,
        string owner)
    {
        if (parent.Element(childName) is not null)
        {
            return;
        }

        diagnostics.Add(new DiagnosticEntry
        {
            Stage = "schema",
            Severity = "Error",
            Code = code,
            Message = $"Missing required element '{childName.LocalName}'.",
            Owner = owner
        });
    }

    private static ValidationReport BuildReport(List<DiagnosticEntry> diagnostics)
    {
        var blocking = diagnostics.Count(d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        var warnings = diagnostics.Count(d => string.Equals(d.Severity, "Warning", StringComparison.OrdinalIgnoreCase));
        var info = diagnostics.Count(d => string.Equals(d.Severity, "Information", StringComparison.OrdinalIgnoreCase));

        var confidence = 100 - (blocking * 20) - (warnings * 5);
        if (confidence < 0)
        {
            confidence = 0;
        }

        return new ValidationReport
        {
            BlockingCount = blocking,
            WarningsCount = warnings,
            InfoCount = info,
            ConfidenceScore = confidence,
            Diagnostics = diagnostics
        };
    }

    [GeneratedRegex("^\\d+(\\.\\d+)?(in|cm|mm|pt|pc)$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MeasurementRegex();

    [GeneratedRegex("Fields!([A-Za-z_][A-Za-z0-9_]*)\\.", RegexOptions.Compiled)]
    private static partial Regex FieldReferenceRegex();
}

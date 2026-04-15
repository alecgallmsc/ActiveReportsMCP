using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RdlxMcpServer.Models;

namespace RdlxMcpServer.Services;

public sealed partial class RdlxDocumentService
{
    private static (double? AvailableWidth, bool IsGalleyMode) GetPrintableFitBounds(
        XElement root,
        IReadOnlyDictionary<string, string>? options)
    {
        // Galley-style layouts intentionally allow horizontal overflow.
        var isGalley = IsGalleyMode(root, options);
        if (isGalley)
        {
            return (null, true);
        }

        var ns = root.Name.Namespace;
        var page = root.Element(ns + "Page");
        var pageWidth = ParseMeasurementAsInches(page?.Element(ns + "PageWidth")?.Value);
        var leftMargin = ParseMeasurementAsInches(page?.Element(ns + "LeftMargin")?.Value) ?? 0;
        var rightMargin = ParseMeasurementAsInches(page?.Element(ns + "RightMargin")?.Value) ?? 0;

        if (pageWidth is null)
        {
            return (null, false);
        }

        var available = Math.Max(0.1, pageWidth.Value - leftMargin - rightMargin);
        return (available, false);
    }

    private static bool IsGalleyMode(XElement root, IReadOnlyDictionary<string, string>? options)
    {
        if (options is not null
            && options.TryGetValue("viewmode", out var viewMode)
            && viewMode.Equals("galley", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (options is not null
            && options.TryGetValue("mode", out var mode)
            && mode.Equals("galley", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ns = root.Name.Namespace;
        var interactiveHeight = root.Element(ns + "InteractiveHeight")?.Value;
        if (!string.IsNullOrWhiteSpace(interactiveHeight)
            && interactiveHeight.Equals("0in", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static void EnsureFitsPrintableWidth(
        XElement root,
        XElement element,
        List<DiagnosticEntry> diagnostics,
        string? owner,
        IReadOnlyDictionary<string, string>? options)
    {
        var bounds = GetPrintableFitBounds(root, options);
        if (bounds.IsGalleyMode || bounds.AvailableWidth is null)
        {
            return;
        }

        var ns = root.Name.Namespace;
        var leftNode = EnsureChild(element, ns + "Left");
        var widthNode = EnsureChild(element, ns + "Width");

        var left = ParseMeasurementAsInches(leftNode.Value) ?? 0;
        var width = ParseMeasurementAsInches(widthNode.Value);
        if (width is null)
        {
            return;
        }

        var available = bounds.AvailableWidth.Value;
        var changed = false;

        if (width > available)
        {
            width = available;
            widthNode.Value = FormatInches(width.Value);
            changed = true;
        }

        if (left < 0)
        {
            left = 0;
            leftNode.Value = FormatInches(left);
            changed = true;
        }

        if (left + width > available)
        {
            left = Math.Max(0, available - width.Value);
            leftNode.Value = FormatInches(left);
            changed = true;
        }

        if (changed)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "layout",
                Severity = "Information",
                Code = "AUTO_FIT_PAGE_WIDTH",
                Message = "Control was auto-fitted within effective printable page width.",
                Owner = owner
            });
        }
    }

    private static double? ParseMeasurementAsInches(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var match = Regex.Match(trimmed, "^(?<n>\\d+(\\.\\d+)?)(?<u>in|cm|mm|pt|pc)$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var n = double.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        var unit = match.Groups["u"].Value.ToLowerInvariant();
        return unit switch
        {
            "in" => n,
            "cm" => n / 2.54,
            "mm" => n / 25.4,
            "pt" => n / 72.0,
            "pc" => n / 6.0,
            _ => null
        };
    }

    private static string FormatInches(double inches)
    {
        return $"{inches.ToString("0.###", CultureInfo.InvariantCulture)}in";
    }

    private static void EnsureBodyHeight(XElement? root, string? top, string? height)
    {
        if (root is null)
        {
            return;
        }

        var ns = root.Name.Namespace;
        var body = root.Element(ns + "Body");
        if (body is null)
        {
            return;
        }

        var topIn = ParseMeasurementAsInches(top);
        var heightIn = ParseMeasurementAsInches(height);
        if (topIn is null || heightIn is null)
        {
            return;
        }

        var required = topIn.Value + heightIn.Value + 0.2;
        var heightNode = EnsureChild(body, ns + "Height");
        var current = ParseMeasurementAsInches(heightNode.Value) ?? 0;
        if (required > current)
        {
            heightNode.Value = FormatInches(required);
        }
    }

    private static IEnumerable<string> GetReportItemNames(XElement? root)
    {
        if (root is null)
        {
            return [];
        }

        var ns = root.Name.Namespace;
        var reportItems = root.Element(ns + "Body")?.Element(ns + "ReportItems")?.Elements();
        if (reportItems is null)
        {
            return [];
        }

        return reportItems
            .Select(x => x.Attribute("Name")?.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>();
    }

    private static void CanonicalizeElement(XElement element)
    {
        foreach (var child in element.Elements())
        {
            CanonicalizeElement(child);
        }

        SortNamedChildrenIfApplicable(element);

        var sortedAttributes = element.Attributes()
            .OrderBy(a => a.IsNamespaceDeclaration ? 0 : 1)
            .ThenBy(a => a.Name.NamespaceName, StringComparer.Ordinal)
            .ThenBy(a => a.Name.LocalName, StringComparer.Ordinal)
            .ToArray();

        element.ReplaceAttributes(sortedAttributes);
    }

    private static void SortNamedChildrenIfApplicable(XElement element)
    {
        var sortableContainers = new HashSet<string>(StringComparer.Ordinal)
        {
            "DataSources",
            "DataSets",
            "ReportItems",
            "ReportParameters"
        };

        if (!sortableContainers.Contains(element.Name.LocalName))
        {
            return;
        }

        var orderedChildren = element.Elements()
            .OrderBy(e => e.Name.LocalName, StringComparer.Ordinal)
            .ThenBy(e => e.Attribute("Name")?.Value ?? string.Empty, StringComparer.Ordinal)
            .ToArray();

        element.ReplaceNodes(orderedChildren);
    }
}

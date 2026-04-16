using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RdlxMcpServer.Models;

namespace RdlxMcpServer.Services;

public sealed partial class LayoutIntelligenceService
{
    private static Dictionary<string, string> ExtractStyles(XElement element, XNamespace ns)
    {
        var styles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var style = element.Element(ns + "Style");
        if (style is not null)
        {
            foreach (var child in style.Elements())
            {
                if (child.HasElements)
                {
                    foreach (var grandChild in child.Elements())
                    {
                        styles[$"{child.Name.LocalName}.{grandChild.Name.LocalName}"] = grandChild.Value;
                    }
                }
                else
                {
                    styles[child.Name.LocalName] = child.Value;
                }
            }
        }

        var format = element.Element(ns + "Format")?.Value;
        if (!string.IsNullOrWhiteSpace(format))
        {
            styles["Format"] = format;
        }

        return styles;
    }

    private static bool Overlaps(LayoutModelControl a, LayoutModelControl b)
    {
        var ax1 = ParseInchesOrZero(a.X);
        var ay1 = ParseInchesOrZero(a.Y);
        var ax2 = ax1 + ParseInchesOrZero(a.Width);
        var ay2 = ay1 + ParseInchesOrZero(a.Height);

        var bx1 = ParseInchesOrZero(b.X);
        var by1 = ParseInchesOrZero(b.Y);
        var bx2 = bx1 + ParseInchesOrZero(b.Width);
        var by2 = by1 + ParseInchesOrZero(b.Height);

        return ax1 < bx2 && ax2 > bx1 && ay1 < by2 && ay2 > by1;
    }

    private static bool ContainsFormatLiteral(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        return expression.Contains("Format(", StringComparison.OrdinalIgnoreCase)
            || expression.Contains("CStr(", StringComparison.OrdinalIgnoreCase)
            || expression.Contains("#,", StringComparison.OrdinalIgnoreCase)
            || expression.Contains("$", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NeedsInlineNumericFormatting(string? expression, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        // Only rewrite mixed/labeled expressions where Style.Format cannot format embedded numbers.
        return expression.Contains('&')
            && ExpressionReferencesField(expression, fieldName);
    }

    private static bool ExpressionReferencesField(string expression, string fieldName)
    {
        if (expression.Contains($"Fields!{fieldName}.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var escaped = Regex.Escape(fieldName);
        return Regex.IsMatch(
            expression,
            $"\\b(?:Sum|Avg|Min|Max|Count|CountDistinct|First|Last)\\s*\\(\\s*{escaped}\\s*\\)",
            RegexOptions.IgnoreCase);
    }

    private static bool TryApplyInlineNumericFormatting(string expression, string fieldName, string formatString, out string formatted)
    {
        formatted = expression;
        var terms = SplitTopLevelConcatenationTerms(expression);
        if (terms.Count < 2)
        {
            return false;
        }

        var changed = false;
        for (var i = 0; i < terms.Count; i++)
        {
            var rewritten = TryRewriteConcatenatedTerm(terms[i], fieldName, formatString, i == 0, out var replacement);
            if (!rewritten)
            {
                continue;
            }

            terms[i] = replacement;
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        formatted = "=" + string.Join(" & ", terms);
        return true;
    }

    private static bool TryRewriteConcatenatedTerm(string rawTerm, string fieldName, string formatString, bool isFirstTerm, out string rewritten)
    {
        rewritten = rawTerm;
        var term = rawTerm.Trim();
        if (term.Length == 0)
        {
            return false;
        }

        if (isFirstTerm && term.StartsWith('='))
        {
            term = term[1..].TrimStart();
        }

        if (term.StartsWith("Format(", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var escapedField = Regex.Escape(fieldName);
        var fieldPattern = $"^Fields!{escapedField}\\.Value$";
        if (Regex.IsMatch(term, fieldPattern, RegexOptions.IgnoreCase))
        {
            rewritten = BuildFormatWrapper(term, formatString);
            return true;
        }

        var aggregateMatch = Regex.Match(
            term,
            $"^(?<func>Sum|Avg|Min|Max|Count|CountDistinct|First|Last)\\s*\\(\\s*(?<arg>Fields!{escapedField}\\.Value|{escapedField})\\s*\\)$",
            RegexOptions.IgnoreCase);
        if (!aggregateMatch.Success)
        {
            return false;
        }

        var arg = aggregateMatch.Groups["arg"].Value;
        if (string.Equals(arg, fieldName, StringComparison.OrdinalIgnoreCase))
        {
            arg = $"Fields!{fieldName}.Value";
        }

        var aggregateExpression = $"{aggregateMatch.Groups["func"].Value}({arg})";
        rewritten = BuildFormatWrapper(aggregateExpression, formatString);
        return true;
    }

    private static List<string> SplitTopLevelConcatenationTerms(string expression)
    {
        var terms = new List<string>();
        var start = 0;
        var depth = 0;
        var inString = false;

        for (var i = 0; i < expression.Length; i++)
        {
            var ch = expression[i];
            if (ch == '"')
            {
                if (inString && i + 1 < expression.Length && expression[i + 1] == '"')
                {
                    i++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (ch == '(')
            {
                depth++;
                continue;
            }

            if (ch == ')')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (ch == '&' && depth == 0)
            {
                terms.Add(expression[start..i].Trim());
                start = i + 1;
            }
        }

        terms.Add(expression[start..].Trim());
        return terms;
    }

    private static string BuildFormatWrapper(string expression, string formatString)
    {
        var escapedFormat = formatString.Replace("\"", "\"\"");
        return $"Format({expression}, \"{escapedFormat}\")";
    }

    private static string ResolveFieldName(string fieldRef)
    {
        var match = Regex.Match(fieldRef, "Fields!([A-Za-z_][A-Za-z0-9_]*)\\.", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : fieldRef.Trim();
    }

    private static bool TryParseInches(string? value, out double inches)
    {
        inches = ParseInchesOrZero(value);
        return !string.IsNullOrWhiteSpace(value) && inches > 0;
    }

    private static double ParseInchesOrZero(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var match = Regex.Match(value.Trim(), "^(?<n>\\d+(\\.\\d+)?)(?<u>in|cm|mm|pt|pc)$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return 0;
        }

        var number = double.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        return match.Groups["u"].Value.ToLowerInvariant() switch
        {
            "in" => number,
            "cm" => number / 2.54,
            "mm" => number / 25.4,
            "pt" => number / 72.0,
            "pc" => number / 6.0,
            _ => 0
        };
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private static string FormatInches(double inches)
    {
        return $"{inches.ToString("0.###", CultureInfo.InvariantCulture)}in";
    }

    private static void EnsureTextboxHeightFitsFont(XElement textbox, XNamespace ns, string fontSize)
    {
        var fontInches = ParseInchesOrZero(fontSize);
        if (fontInches <= 0)
        {
            return;
        }

        var desiredHeight = Math.Max(0.25, fontInches * 1.65);
        var heightNode = EnsureChild(textbox, ns + "Height");
        var currentHeight = ParseInchesOrZero(heightNode.Value);
        if (desiredHeight > currentHeight)
        {
            heightNode.Value = FormatInches(desiredHeight);
        }
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
}

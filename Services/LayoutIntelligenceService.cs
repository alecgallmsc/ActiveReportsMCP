using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RdlxMcpServer.Models;

namespace RdlxMcpServer.Services;

public sealed partial class LayoutIntelligenceService
{
    private readonly RdlxDocumentService _documents;

    public LayoutIntelligenceService(RdlxDocumentService documents)
    {
        _documents = documents;
    }

    public PatchResult ApplyStylePatch(string rdlx, IReadOnlyList<StyleTarget> targets, IReadOnlyList<StyleOperation> styleOps)
    {
        var diagnostics = new List<DiagnosticEntry>();
        var document = XDocument.Parse(rdlx, LoadOptions.None);
        var root = document.Root ?? throw new InvalidOperationException("Invalid RDLX: missing root element.");
        var ns = root.Name.Namespace;

        var matches = ResolveTargets(root, ns, targets).ToList();
        if (matches.Count == 0)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "style",
                Severity = "Warning",
                Code = "STYLE_NO_TARGETS",
                Message = "No controls matched style patch targets."
            });
        }

        foreach (var element in matches)
        {
            var style = EnsureChild(element, ns + "Style");
            foreach (var op in styleOps)
            {
                ApplyStyleOperation(element, style, ns, op, diagnostics, element.Attribute("Name")?.Value);
            }
        }

        return new PatchResult
        {
            Rdlx = _documents.Canonicalize(document.ToString(SaveOptions.DisableFormatting)),
            Diagnostics = diagnostics
        };
    }

    public PatchResult ApplyFormattingPatch(string rdlx, IReadOnlyList<FormatRule> formatRules)
    {
        var diagnostics = new List<DiagnosticEntry>();
        var document = XDocument.Parse(rdlx, LoadOptions.None);
        var root = document.Root ?? throw new InvalidOperationException("Invalid RDLX: missing root element.");
        var ns = root.Name.Namespace;

        var textboxes = root.Descendants(ns + "Textbox").ToList();
        foreach (var rule in formatRules)
        {
            var fieldName = ResolveFieldName(rule.FieldRef);
            var matched = textboxes.Where(tb =>
            {
                var value = tb.Element(ns + "Value")?.Value ?? string.Empty;
                return value.Contains($"Fields!{fieldName}.", StringComparison.OrdinalIgnoreCase);
            }).ToList();

            if (matched.Count == 0)
            {
                diagnostics.Add(new DiagnosticEntry
                {
                    Stage = "formatting",
                    Severity = "Warning",
                    Code = "FORMAT_FIELD_NOT_FOUND",
                    Message = $"No textbox expressions reference field '{fieldName}'.",
                    Owner = fieldName
                });
                continue;
            }

            foreach (var textbox in matched)
            {
                // Keep format style-only for compatibility with current designer/runtime behavior.
                textbox.Element(ns + "Format")?.Remove();
                EnsureChild(EnsureChild(textbox, ns + "Style"), ns + "Format").Value = rule.FormatString;
                if (!string.IsNullOrWhiteSpace(rule.Locale))
                {
                    EnsureChild(textbox, ns + "Language").Value = rule.Locale;
                }

                if (!string.IsNullOrWhiteSpace(rule.NullDisplay))
                {
                    diagnostics.Add(new DiagnosticEntry
                    {
                        Stage = "formatting",
                        Severity = "Information",
                        Code = "FORMAT_NULLDISPLAY_IGNORED",
                        Message = $"NullDisplay '{rule.NullDisplay}' noted for '{fieldName}' but base expression was kept simple.",
                        Owner = textbox.Attribute("Name")?.Value
                    });
                }
            }
        }

        return new PatchResult
        {
            Rdlx = _documents.Canonicalize(document.ToString(SaveOptions.DisableFormatting)),
            Diagnostics = diagnostics
        };
    }

    public LayoutModel ExtractLayoutModel(string rdlx)
    {
        var document = XDocument.Parse(rdlx, LoadOptions.None);
        var root = document.Root ?? throw new InvalidOperationException("Invalid RDLX: missing root element.");
        var ns = root.Name.Namespace;

        var controls = root
            .Descendants()
            .Where(e => e.Attribute("Name") is not null
                && (e.Name.LocalName is "Textbox" or "Table" or "Tablix" or "Chart" or "Rectangle" or "Image"))
            .Select(e => new LayoutModelControl
            {
                Ref = $"{e.Name.LocalName.ToLowerInvariant()}:{e.Attribute("Name")!.Value}",
                Type = e.Name.LocalName,
                Name = e.Attribute("Name")!.Value,
                ParentType = e.Parent?.Name.LocalName,
                X = e.Element(ns + "Left")?.Value,
                Y = e.Element(ns + "Top")?.Value,
                Width = e.Element(ns + "Width")?.Value,
                Height = e.Element(ns + "Height")?.Value,
                ValueExpression = e.Element(ns + "Value")?.Value,
                Styles = ExtractStyles(e, ns)
            })
            .ToList();

        var alignments = controls
            .Where(c => TryParseInches(c.X, out _))
            .GroupBy(c => Math.Round(ParseInchesOrZero(c.X), 2).ToString("0.00", CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToDictionary(
                group => $"left_{group.Key}",
                group => group.Select(c => c.Ref).ToList(),
                StringComparer.OrdinalIgnoreCase);

        return new LayoutModel
        {
            PageWidth = root.Element(ns + "Page")?.Element(ns + "PageWidth")?.Value,
            PageHeight = root.Element(ns + "Page")?.Element(ns + "PageHeight")?.Value,
            LeftMargin = root.Element(ns + "Page")?.Element(ns + "LeftMargin")?.Value,
            RightMargin = root.Element(ns + "Page")?.Element(ns + "RightMargin")?.Value,
            Controls = controls,
            AlignmentGroups = alignments
        };
    }

    public LayoutScoreReport ScoreLayout(string rdlx)
    {
        var model = ExtractLayoutModel(rdlx);
        var issues = new List<LayoutScoreIssue>();

        var alignmentScore = 25;
        var spacingScore = 20;
        var densityScore = 20;
        var styleScore = 20;
        var semanticsScore = 15;

        var positioned = model.Controls
            .Where(c => TryParseInches(c.X, out _) && TryParseInches(c.Y, out _) && TryParseInches(c.Width, out _) && TryParseInches(c.Height, out _))
            .ToList();

        for (var i = 0; i < positioned.Count; i++)
        {
            for (var j = i + 1; j < positioned.Count; j++)
            {
                if (Overlaps(positioned[i], positioned[j]))
                {
                    densityScore -= 10;
                    issues.Add(new LayoutScoreIssue
                    {
                        IssueCode = "DENSITY_OVERLAP_RISK",
                        Severity = "Warning",
                        Message = "Two controls overlap in coordinate space.",
                        Targets = [positioned[i].Ref, positioned[j].Ref],
                        SuggestedOps =
                        [
                            new LayoutOperation
                            {
                                Op = "move_item",
                                TargetRef = positioned[j].Ref,
                                Y = FormatInches(ParseInchesOrZero(positioned[i].Y) + ParseInchesOrZero(positioned[i].Height) + 0.05)
                            }
                        ]
                    });
                }
            }
        }

        var pageWidth = ParseInchesOrZero(model.PageWidth);
        var maxRight = pageWidth - ParseInchesOrZero(model.LeftMargin) - ParseInchesOrZero(model.RightMargin);
        foreach (var control in positioned)
        {
            var right = ParseInchesOrZero(control.X) + ParseInchesOrZero(control.Width);
            if (maxRight > 0 && right > maxRight + 0.01)
            {
                densityScore -= 5;
                issues.Add(new LayoutScoreIssue
                {
                    IssueCode = "DENSITY_PAGE_OVERFLOW",
                    Severity = "Warning",
                    Message = "Control extends beyond printable page width.",
                    Targets = [control.Ref],
                    SuggestedOps =
                    [
                        new LayoutOperation
                        {
                            Op = "move_item",
                            TargetRef = control.Ref,
                            X = FormatInches(Math.Max(0.1, maxRight - ParseInchesOrZero(control.Width)))
                        }
                    ]
                });
            }
        }

        var leftGroups = positioned
            .GroupBy(c => Math.Round(ParseInchesOrZero(c.X), 2))
            .Count();
        if (positioned.Count >= 4 && leftGroups > Math.Max(2, positioned.Count / 2))
        {
            alignmentScore -= 8;
            var medianLeft = positioned.Select(c => ParseInchesOrZero(c.X)).Order().ElementAt(positioned.Count / 2);
            var target = positioned.OrderByDescending(c => Math.Abs(ParseInchesOrZero(c.X) - medianLeft)).First();
            issues.Add(new LayoutScoreIssue
            {
                IssueCode = "ALIGN_LEFT_MISMATCH",
                Severity = "Warning",
                Message = "Controls have inconsistent left alignment anchors.",
                Targets = positioned.Select(c => c.Ref).Take(4).ToList(),
                SuggestedOps =
                [
                    new LayoutOperation
                    {
                        Op = "move_item",
                        TargetRef = target.Ref,
                        X = FormatInches(medianLeft)
                    }
                ]
            });
        }

        var ySorted = positioned.OrderBy(c => ParseInchesOrZero(c.Y)).ToList();
        if (ySorted.Count >= 3)
        {
            var gaps = new List<double>();
            for (var i = 1; i < ySorted.Count; i++)
            {
                gaps.Add(ParseInchesOrZero(ySorted[i].Y) - ParseInchesOrZero(ySorted[i - 1].Y));
            }

            var gapSpread = gaps.Max() - gaps.Min();
            if (gapSpread > 0.25)
            {
                spacingScore -= 8;
                issues.Add(new LayoutScoreIssue
                {
                    IssueCode = "SPACING_RHYTHM_INCONSISTENT",
                    Severity = "Warning",
                    Message = "Vertical spacing rhythm is inconsistent.",
                    Targets = ySorted.Select(c => c.Ref).Take(5).ToList()
                });
            }
        }

        var fontSets = model.Controls
            .Where(c => c.Type == "Textbox")
            .Select(c => c.Styles.TryGetValue("FontSize", out var size) ? size : "default")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (fontSets > 3)
        {
            styleScore -= 7;
            issues.Add(new LayoutScoreIssue
            {
                IssueCode = "STYLE_FONT_VARIANCE",
                Severity = "Warning",
                Message = "Too many distinct font sizes detected for textboxes.",
                Targets = model.Controls.Where(c => c.Type == "Textbox").Select(c => c.Ref).Take(5).ToList()
            });
        }

        var numericValueTextboxes = model.Controls
            .Where(c => c.Type == "Textbox"
                && !string.IsNullOrWhiteSpace(c.ValueExpression)
                && c.ValueExpression.Contains("Fields!", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(c.ValueExpression, "(Price|Amount|Total|Stock|Qty|Cost)", RegexOptions.IgnoreCase))
            .ToList();
        foreach (var textbox in numericValueTextboxes)
        {
            if (!textbox.Styles.ContainsKey("Format") && !ContainsFormatLiteral(textbox.ValueExpression))
            {
                semanticsScore -= 3;
                issues.Add(new LayoutScoreIssue
                {
                    IssueCode = "SEMANTIC_MISSING_FORMAT",
                    Severity = "Warning",
                    Message = "Likely numeric field textbox has no explicit format.",
                    Targets = [textbox.Ref]
                });
            }
        }

        alignmentScore = Clamp(alignmentScore, 0, 25);
        spacingScore = Clamp(spacingScore, 0, 20);
        densityScore = Clamp(densityScore, 0, 20);
        styleScore = Clamp(styleScore, 0, 20);
        semanticsScore = Clamp(semanticsScore, 0, 15);

        return new LayoutScoreReport
        {
            AlignmentScore = alignmentScore,
            SpacingScore = spacingScore,
            DensityScore = densityScore,
            StyleScore = styleScore,
            SemanticsScore = semanticsScore,
            Score = alignmentScore + spacingScore + densityScore + styleScore + semanticsScore,
            Issues = issues
        };
    }

    public AutoRefineResult AutoRefineLayout(string rdlx, int maxIterations, int targetScore)
    {
        var diagnostics = new List<DiagnosticEntry>();
        var iterations = new List<object>();
        var current = rdlx;
        var initial = ScoreLayout(current);
        var currentScore = initial.Score;
        var applied = 0;

        for (var i = 0; i < maxIterations && currentScore < targetScore; i++)
        {
            var score = ScoreLayout(current);
            var candidate = score.Issues.FirstOrDefault(issue => issue.SuggestedOps.Count > 0);
            if (candidate is null)
            {
                diagnostics.Add(new DiagnosticEntry
                {
                    Stage = "layout",
                    Severity = "Information",
                    Code = "AUTO_REFINE_NO_ACTIONABLE_ISSUES",
                    Message = "No actionable layout issues with suggested operations were found."
                });
                break;
            }

            var patch = _documents.ApplyLayoutOperations(current, candidate.SuggestedOps);
            current = patch.Rdlx;
            applied += 1;
            currentScore = ScoreLayout(current).Score;
            diagnostics.AddRange(patch.Diagnostics);
            iterations.Add(new
            {
                iteration = i + 1,
                issueCode = candidate.IssueCode,
                issueMessage = candidate.Message,
                opsApplied = candidate.SuggestedOps.Count,
                scoreAfter = currentScore
            });
        }

        return new AutoRefineResult
        {
            Rdlx = current,
            InitialScore = initial.Score,
            FinalScore = currentScore,
            IterationsApplied = applied,
            Iterations = iterations,
            Diagnostics = diagnostics
        };
    }

    private static IEnumerable<XElement> ResolveTargets(XElement root, XNamespace ns, IReadOnlyList<StyleTarget> targets)
    {
        if (targets.Count == 0)
        {
            return root.Descendants(ns + "Textbox");
        }

        var allNamed = root.Descendants().Where(e => e.Attribute("Name") is not null).ToList();
        var selected = new List<XElement>();

        foreach (var target in targets)
        {
            if (!string.IsNullOrWhiteSpace(target.TargetRef))
            {
                var name = target.TargetRef.Contains(':', StringComparison.Ordinal)
                    ? target.TargetRef[(target.TargetRef.IndexOf(':') + 1)..]
                    : target.TargetRef;
                selected.AddRange(allNamed.Where(e => string.Equals(e.Attribute("Name")?.Value, name, StringComparison.OrdinalIgnoreCase)));
            }

            if (!string.IsNullOrWhiteSpace(target.Selector))
            {
                var selector = target.Selector.Trim();
                if (string.Equals(selector, "all_textboxes", StringComparison.OrdinalIgnoreCase))
                {
                    selected.AddRange(root.Descendants(ns + "Textbox"));
                }
                else if (selector.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
                {
                    var type = selector[5..];
                    selected.AddRange(allNamed.Where(e => string.Equals(e.Name.LocalName, type, StringComparison.OrdinalIgnoreCase)));
                }
                else if (selector.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    var name = selector[5..];
                    selected.AddRange(allNamed.Where(e => string.Equals(e.Attribute("Name")?.Value, name, StringComparison.OrdinalIgnoreCase)));
                }
            }
        }

        return selected.DistinctBy(e => e.Attribute("Name")?.Value ?? e.GetHashCode().ToString(CultureInfo.InvariantCulture));
    }

    private static void ApplyStyleOperation(XElement element, XElement style, XNamespace ns, StyleOperation op, List<DiagnosticEntry> diagnostics, string? owner)
    {
        var prop = NormalizeStyleProperty(op.Property);
        switch (prop)
        {
            case "fontfamily":
                EnsureChild(style, ns + "FontFamily").Value = op.Value;
                break;
            case "fontsize":
                EnsureChild(style, ns + "FontSize").Value = op.Value;
                if (string.Equals(element.Name.LocalName, "Textbox", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureTextboxHeightFitsFont(element, ns, op.Value);
                }
                break;
            case "fontweight":
                EnsureChild(style, ns + "FontWeight").Value = op.Value;
                break;
            case "fontstyle":
                EnsureChild(style, ns + "FontStyle").Value = op.Value;
                break;
            case "textdecoration":
                EnsureChild(style, ns + "TextDecoration").Value = op.Value;
                break;
            case "color":
                EnsureChild(style, ns + "Color").Value = op.Value;
                break;
            case "backgroundcolor":
                EnsureChild(style, ns + "BackgroundColor").Value = op.Value;
                break;
            case "backgroundimage":
                EnsureChild(style, ns + "BackgroundImage").Value = op.Value;
                break;
            case "backgroundgradienttype":
                EnsureChild(style, ns + "BackgroundGradientType").Value = op.Value;
                break;
            case "backgroundgradientendcolor":
                EnsureChild(style, ns + "BackgroundGradientEndColor").Value = op.Value;
                break;
            case "textalign":
                EnsureChild(style, ns + "TextAlign").Value = op.Value;
                break;
            case "verticalalign":
                EnsureChild(style, ns + "VerticalAlign").Value = op.Value;
                break;
            case "direction":
                EnsureChild(style, ns + "Direction").Value = op.Value;
                break;
            case "writingmode":
                EnsureChild(style, ns + "WritingMode").Value = op.Value;
                break;
            case "unicodebidi":
                EnsureChild(style, ns + "UnicodeBiDi").Value = op.Value;
                break;
            case "lineheight":
                EnsureChild(style, ns + "LineHeight").Value = op.Value;
                break;
            case "language":
                EnsureChild(element, ns + "Language").Value = op.Value;
                break;
            case "calendar":
                EnsureChild(style, ns + "Calendar").Value = op.Value;
                break;
            case "numerallanguage":
                EnsureChild(style, ns + "NumeralLanguage").Value = op.Value;
                break;
            case "numeralvariant":
                EnsureChild(style, ns + "NumeralVariant").Value = op.Value;
                break;
            case "format":
                EnsureChild(style, ns + "Format").Value = op.Value;
                break;
            case "white-space":
            case "whitespace":
                EnsureChild(style, ns + "WhiteSpace").Value = op.Value;
                break;
            case "paddingleft":
                EnsureChild(style, ns + "PaddingLeft").Value = op.Value;
                break;
            case "paddingright":
                EnsureChild(style, ns + "PaddingRight").Value = op.Value;
                break;
            case "paddingtop":
                EnsureChild(style, ns + "PaddingTop").Value = op.Value;
                break;
            case "paddingbottom":
                EnsureChild(style, ns + "PaddingBottom").Value = op.Value;
                break;
            case "padding":
                EnsureChild(style, ns + "PaddingLeft").Value = op.Value;
                EnsureChild(style, ns + "PaddingRight").Value = op.Value;
                EnsureChild(style, ns + "PaddingTop").Value = op.Value;
                EnsureChild(style, ns + "PaddingBottom").Value = op.Value;
                break;
            case "bordercolor":
                EnsureChild(EnsureChild(style, ns + "Border"), ns + "Color").Value = op.Value;
                break;
            case "borderstyle":
                EnsureChild(EnsureChild(style, ns + "Border"), ns + "Style").Value = op.Value;
                break;
            case "borderwidth":
                EnsureChild(EnsureChild(style, ns + "Border"), ns + "Width").Value = op.Value;
                break;
            case "leftbordercolor":
                EnsureChild(EnsureChild(style, ns + "LeftBorder"), ns + "Color").Value = op.Value;
                break;
            case "leftborderstyle":
                EnsureChild(EnsureChild(style, ns + "LeftBorder"), ns + "Style").Value = op.Value;
                break;
            case "leftborderwidth":
                EnsureChild(EnsureChild(style, ns + "LeftBorder"), ns + "Width").Value = op.Value;
                break;
            case "rightbordercolor":
                EnsureChild(EnsureChild(style, ns + "RightBorder"), ns + "Color").Value = op.Value;
                break;
            case "rightborderstyle":
                EnsureChild(EnsureChild(style, ns + "RightBorder"), ns + "Style").Value = op.Value;
                break;
            case "rightborderwidth":
                EnsureChild(EnsureChild(style, ns + "RightBorder"), ns + "Width").Value = op.Value;
                break;
            case "topbordercolor":
                EnsureChild(EnsureChild(style, ns + "TopBorder"), ns + "Color").Value = op.Value;
                break;
            case "topborderstyle":
                EnsureChild(EnsureChild(style, ns + "TopBorder"), ns + "Style").Value = op.Value;
                break;
            case "topborderwidth":
                EnsureChild(EnsureChild(style, ns + "TopBorder"), ns + "Width").Value = op.Value;
                break;
            case "bottombordercolor":
                EnsureChild(EnsureChild(style, ns + "BottomBorder"), ns + "Color").Value = op.Value;
                break;
            case "bottomborderstyle":
                EnsureChild(EnsureChild(style, ns + "BottomBorder"), ns + "Style").Value = op.Value;
                break;
            case "bottomborderwidth":
                EnsureChild(EnsureChild(style, ns + "BottomBorder"), ns + "Width").Value = op.Value;
                break;
            default:
                diagnostics.Add(new DiagnosticEntry
                {
                    Stage = "style",
                    Severity = "Warning",
                    Code = "STYLE_PROPERTY_UNSUPPORTED",
                    Message = $"Unsupported style property '{op.Property}'.",
                    Owner = owner
                });
                break;
        }
    }

    private static string NormalizeStyleProperty(string property)
    {
        var trimmed = property.Trim().ToLowerInvariant();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        return trimmed
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);
    }

}

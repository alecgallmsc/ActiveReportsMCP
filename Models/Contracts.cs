using System.Text.Json.Serialization;

namespace RdlxMcpServer.Models;

public sealed class ToolResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("reportPath")]
    public string? ReportPath { get; init; }

    [JsonPropertyName("diagnostics")]
    public List<DiagnosticEntry> Diagnostics { get; init; } = [];

    [JsonPropertyName("artifacts")]
    public Dictionary<string, object?> Artifacts { get; init; } = new(StringComparer.Ordinal);
}

public sealed class DiagnosticEntry
{
    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }
}

public sealed class LayoutOperation
{
    public required string Op { get; init; }
    public string? ParentRef { get; init; }
    public string? TargetRef { get; init; }
    public string? Name { get; init; }
    public string? X { get; init; }
    public string? Y { get; init; }
    public string? Width { get; init; }
    public string? Height { get; init; }
    public string? ValueExpression { get; init; }
}

public sealed class DataOperation
{
    public required string Op { get; init; }
    public string? Name { get; init; }
    public string? TargetRef { get; init; }
    public string? DataSourceName { get; init; }
    public string? ConnectionString { get; init; }
    public string? DataProvider { get; init; }
    public string? CommandText { get; init; }
    public string? ParameterType { get; init; }
    public string? DefaultValue { get; init; }
    public List<string>? Fields { get; init; }
}

public sealed class StyleTarget
{
    public string? TargetRef { get; init; }
    public string? Selector { get; init; }
}

public sealed class StyleOperation
{
    public required string Property { get; init; }
    public required string Value { get; init; }
}

public sealed class FormatRule
{
    public required string FieldRef { get; init; }
    public required string FormatString { get; init; }
    public string? Locale { get; init; }
    public string? NullDisplay { get; init; }
}

public sealed class ReportStructure
{
    public string NamespaceUri { get; init; } = string.Empty;
    public int ReportItemCount { get; init; }
    public List<StructureNode> Items { get; init; } = [];
}

public sealed class StructureNode
{
    public required string Ref { get; init; }
    public required string Type { get; init; }
    public required string Name { get; init; }
    public string? X { get; init; }
    public string? Y { get; init; }
    public string? Width { get; init; }
    public string? Height { get; init; }
}

public sealed class ValidationReport
{
    public int BlockingCount { get; init; }
    public int WarningsCount { get; init; }
    public int InfoCount { get; init; }
    public int ConfidenceScore { get; init; }
    public List<DiagnosticEntry> Diagnostics { get; init; } = [];
}

public sealed class RuntimeVerificationReport
{
    public bool Success { get; init; }
    public string Mode { get; init; } = "load_only";
    public string Coverage { get; init; } = "none";
    public List<DiagnosticEntry> Diagnostics { get; init; } = [];
}

public sealed class PatchResult
{
    public required string Rdlx { get; init; }
    public List<DiagnosticEntry> Diagnostics { get; init; } = [];
}

public sealed class LayoutModelControl
{
    public required string Ref { get; init; }
    public required string Type { get; init; }
    public required string Name { get; init; }
    public string? ParentType { get; init; }
    public string? X { get; init; }
    public string? Y { get; init; }
    public string? Width { get; init; }
    public string? Height { get; init; }
    public Dictionary<string, string> Styles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ValueExpression { get; init; }
}

public sealed class LayoutModel
{
    public string? PageWidth { get; init; }
    public string? PageHeight { get; init; }
    public string? LeftMargin { get; init; }
    public string? RightMargin { get; init; }
    public List<LayoutModelControl> Controls { get; init; } = [];
    public Dictionary<string, List<string>> AlignmentGroups { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LayoutScoreIssue
{
    public required string IssueCode { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public List<string> Targets { get; init; } = [];
    public List<LayoutOperation> SuggestedOps { get; init; } = [];
}

public sealed class LayoutScoreReport
{
    public int Score { get; init; }
    public int AlignmentScore { get; init; }
    public int SpacingScore { get; init; }
    public int DensityScore { get; init; }
    public int StyleScore { get; init; }
    public int SemanticsScore { get; init; }
    public List<LayoutScoreIssue> Issues { get; init; } = [];
}

public sealed class AutoRefineResult
{
    public required string Rdlx { get; init; }
    public required int InitialScore { get; init; }
    public required int FinalScore { get; init; }
    public required int IterationsApplied { get; init; }
    public List<object> Iterations { get; init; } = [];
    public List<DiagnosticEntry> Diagnostics { get; init; } = [];
}

public enum ValidationLevel
{
    Full,
    Lint,
    ParseOnly
}

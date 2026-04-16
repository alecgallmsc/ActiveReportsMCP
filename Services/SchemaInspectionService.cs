using System.Data;
using System.Data.Common;
using System.Xml.Linq;
using RdlxMcpServer.Models;

namespace RdlxMcpServer.Services;

public sealed class SchemaInspectionService
{
    private static readonly string[] SchemaVisibility =
    [
        "table and view names",
        "column names",
        "column data types and nullability",
        "column ordinal and size metadata"
    ];

    public ToolResult InspectFromReport(
        string reportPath,
        string reportRdlx,
        string? dataSourceName,
        bool confirm,
        IReadOnlyList<string>? tableAllowList,
        int maxTables,
        int maxColumnsPerTable)
    {
        var boundedMaxTables = Math.Clamp(maxTables, 1, 200);
        var boundedMaxColumns = Math.Clamp(maxColumnsPerTable, 1, 300);

        var sourceResult = TryResolveDataSource(reportRdlx, dataSourceName);
        if (!sourceResult.Ok || sourceResult.DataSource is null)
        {
            return new ToolResult
            {
                Ok = false,
                Message = sourceResult.Message,
                ReportPath = reportPath,
                Diagnostics = sourceResult.Diagnostics,
                Artifacts = new Dictionary<string, object?>
                {
                    ["filePath"] = reportPath
                }
            };
        }

        var source = sourceResult.DataSource;
        var consentArtifacts = BuildConsentArtifacts(source, tableAllowList, boundedMaxTables, boundedMaxColumns);

        if (!confirm)
        {
            return new ToolResult
            {
                Ok = false,
                Message = "Schema inspection requires explicit user confirmation.",
                ReportPath = reportPath,
                Diagnostics =
                [
                    new DiagnosticEntry
                    {
                        Stage = "consent",
                        Severity = "Warning",
                        Code = "CONSENT_REQUIRED",
                        Message = "Confirm schema inspection to allow metadata-only database access."
                    }
                ],
                Artifacts = consentArtifacts
            };
        }

        try
        {
            using var connection = CreateConnection(source.DataProvider, source.ConnectionString, out var runtimeProvider);
            connection.Open();

            var tableRows = connection.GetSchema("Tables");
            var tableMetadata = ExtractTables(tableRows, tableAllowList);
            var columnsByTable = ExtractColumns(connection.GetSchema("Columns"));

            var orderedTables = tableMetadata
                .OrderBy(t => t.SchemaName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.TableName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var selectedTables = orderedTables.Take(boundedMaxTables).ToList();
            var tableEntries = new List<object>(selectedTables.Count);
            var totalColumnCount = 0;
            var tableTruncated = orderedTables.Count > selectedTables.Count;
            var columnsTruncated = false;

            foreach (var table in selectedTables)
            {
                var key = BuildKey(table.SchemaName, table.TableName);
                columnsByTable.TryGetValue(key, out var columns);
                columns ??= [];

                var orderedColumns = columns
                    .OrderBy(c => c.OrdinalPosition)
                    .ThenBy(c => c.ColumnName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var selectedColumns = orderedColumns.Take(boundedMaxColumns).ToList();
                if (orderedColumns.Count > selectedColumns.Count)
                {
                    columnsTruncated = true;
                }

                totalColumnCount += selectedColumns.Count;
                tableEntries.Add(new
                {
                    schema = table.SchemaName,
                    name = table.TableName,
                    type = table.TableType,
                    columns = selectedColumns.Select(c => new
                    {
                        name = c.ColumnName,
                        typeName = c.TypeName,
                        dataType = c.DataType,
                        nullable = c.IsNullable,
                        ordinal = c.OrdinalPosition,
                        maxLength = c.MaxLength,
                        numericPrecision = c.NumericPrecision,
                        numericScale = c.NumericScale
                    }).ToList(),
                    columnCount = orderedColumns.Count
                });
            }

            var diagnostics = new List<DiagnosticEntry>();
            if (tableTruncated)
            {
                diagnostics.Add(new DiagnosticEntry
                {
                    Stage = "schema",
                    Severity = "Warning",
                    Code = "SCHEMA_TABLE_LIMIT_REACHED",
                    Message = $"Returned first {boundedMaxTables} tables/views out of {orderedTables.Count}."
                });
            }

            if (columnsTruncated)
            {
                diagnostics.Add(new DiagnosticEntry
                {
                    Stage = "schema",
                    Severity = "Warning",
                    Code = "SCHEMA_COLUMN_LIMIT_REACHED",
                    Message = $"One or more tables exceeded maxColumnsPerTable={boundedMaxColumns}; output was truncated."
                });
            }

            var artifacts = BuildConsentArtifacts(source, tableAllowList, boundedMaxTables, boundedMaxColumns);
            artifacts["consentRequired"] = false;
            artifacts["runtimeProvider"] = runtimeProvider;
            artifacts["schemaOnly"] = true;
            artifacts["tableCount"] = selectedTables.Count;
            artifacts["columnCount"] = totalColumnCount;
            artifacts["tables"] = tableEntries;

            return new ToolResult
            {
                Ok = true,
                Message = "Schema inspection completed.",
                ReportPath = reportPath,
                Diagnostics = diagnostics,
                Artifacts = artifacts
            };
        }
        catch (Exception ex)
        {
            return new ToolResult
            {
                Ok = false,
                Message = "Schema inspection failed.",
                ReportPath = reportPath,
                Diagnostics =
                [
                    new DiagnosticEntry
                    {
                        Stage = "schema",
                        Severity = "Error",
                        Code = "SCHEMA_INSPECTION_ERROR",
                        Message = SanitizeExceptionMessage(ex.Message)
                    }
                ],
                Artifacts = BuildConsentArtifacts(source, tableAllowList, boundedMaxTables, boundedMaxColumns)
            };
        }
    }

    private static (bool Ok, string Message, List<DiagnosticEntry> Diagnostics, DataSourceInfo? DataSource) TryResolveDataSource(
        string reportRdlx,
        string? dataSourceName)
    {
        var diagnostics = new List<DiagnosticEntry>();

        try
        {
            var root = XElement.Parse(reportRdlx);
            var ns = root.Name.Namespace;
            var sources = root.Element(ns + "DataSources")?
                .Elements(ns + "DataSource")
                .ToList() ?? [];

            if (sources.Count == 0)
            {
                diagnostics.Add(new DiagnosticEntry
                {
                    Stage = "schema",
                    Severity = "Error",
                    Code = "DATASOURCE_NOT_FOUND",
                    Message = "No data source exists in the report. Add a data source before schema inspection."
                });
                return (false, "No data source available for schema inspection.", diagnostics, null);
            }

            XElement? selected;
            if (string.IsNullOrWhiteSpace(dataSourceName))
            {
                selected = sources[0];
            }
            else
            {
                selected = sources.FirstOrDefault(ds =>
                    string.Equals(ds.Attribute("Name")?.Value, dataSourceName.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            if (selected is null)
            {
                diagnostics.Add(new DiagnosticEntry
                {
                    Stage = "schema",
                    Severity = "Error",
                    Code = "DATASOURCE_NOT_FOUND",
                    Message = $"Data source '{dataSourceName}' was not found in report XML."
                });
                return (false, "Data source not found.", diagnostics, null);
            }

            var name = selected.Attribute("Name")?.Value ?? "DataSource1";
            var connectionProperties = selected.Element(ns + "ConnectionProperties");
            var provider = connectionProperties?.Element(ns + "DataProvider")?.Value?.Trim();
            var connectionString = connectionProperties?.Element(ns + "ConnectString")?.Value?.Trim();

            if (string.IsNullOrWhiteSpace(provider))
            {
                diagnostics.Add(new DiagnosticEntry
                {
                    Stage = "schema",
                    Severity = "Error",
                    Code = "DATAPROVIDER_MISSING",
                    Message = $"Data source '{name}' does not define ConnectionProperties/DataProvider."
                });
                return (false, "Data provider is missing from the selected data source.", diagnostics, null);
            }

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                diagnostics.Add(new DiagnosticEntry
                {
                    Stage = "schema",
                    Severity = "Error",
                    Code = "CONNECTSTRING_MISSING",
                    Message = $"Data source '{name}' does not define ConnectionProperties/ConnectString."
                });
                return (false, "Connection string is missing from the selected data source.", diagnostics, null);
            }

            return (true, "Data source resolved.", diagnostics, new DataSourceInfo(name, provider, connectionString));
        }
        catch (Exception ex)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Stage = "schema",
                Severity = "Error",
                Code = "RDLX_PARSE_ERROR",
                Message = SanitizeExceptionMessage(ex.Message)
            });
            return (false, "Unable to parse report XML for schema inspection.", diagnostics, null);
        }
    }

    private static Dictionary<string, object?> BuildConsentArtifacts(
        DataSourceInfo source,
        IReadOnlyList<string>? tableAllowList,
        int maxTables,
        int maxColumnsPerTable)
    {
        return new Dictionary<string, object?>
        {
            ["consentRequired"] = true,
            ["schemaOnly"] = true,
            ["requestedDataSource"] = source.Name,
            ["dataProvider"] = source.DataProvider,
            ["connectionTarget"] = SummarizeConnectionTarget(source.ConnectionString),
            ["canAccess"] = SchemaVisibility,
            ["cannotAccess"] = new[]
            {
                "table row values",
                "INSERT/UPDATE/DELETE operations",
                "DDL operations (CREATE/ALTER/DROP)",
                "stored procedure execution"
            },
            ["ifDeclinedAskFor"] = new[]
            {
                "data provider type (for example OLEDB, ODBC, SQL)",
                "connection string or shared data source reference",
                "target tables/views",
                "required fields and field types",
                "intended joins, filters, and aggregations"
            },
            ["limits"] = new
            {
                maxTables,
                maxColumnsPerTable,
                tableAllowList = tableAllowList ?? []
            }
        };
    }

    private static DbConnection CreateConnection(string dataProvider, string connectionString, out string runtimeProvider)
    {
        var normalized = dataProvider.Trim().ToUpperInvariant();
        var candidates = normalized switch
        {
            "ODBC" => new[]
            {
                "System.Data.Odbc.OdbcConnection, System.Data.Odbc"
            },
            "OLEDB" => new[]
            {
                "System.Data.OleDb.OleDbConnection, System.Data.OleDb"
            },
            "SQL" or "SQLCLIENT" => new[]
            {
                "Microsoft.Data.SqlClient.SqlConnection, Microsoft.Data.SqlClient",
                "System.Data.SqlClient.SqlConnection, System.Data.SqlClient"
            },
            _ =>
            [
                "System.Data.Odbc.OdbcConnection, System.Data.Odbc",
                "System.Data.OleDb.OleDbConnection, System.Data.OleDb",
                "Microsoft.Data.SqlClient.SqlConnection, Microsoft.Data.SqlClient",
                "System.Data.SqlClient.SqlConnection, System.Data.SqlClient"
            ]
        };

        foreach (var typeName in candidates)
        {
            var type = Type.GetType(typeName, throwOnError: false, ignoreCase: false);
            if (type is null || !typeof(DbConnection).IsAssignableFrom(type))
            {
                continue;
            }

            if (Activator.CreateInstance(type) is not DbConnection connection)
            {
                continue;
            }

            connection.ConnectionString = connectionString;
            runtimeProvider = type.FullName ?? type.Name;
            return connection;
        }

        throw new NotSupportedException($"No compatible provider runtime found for DataProvider='{dataProvider}'.");
    }

    private static List<TableMeta> ExtractTables(DataTable tableRows, IReadOnlyList<string>? tableAllowList)
    {
        var allowSet = BuildAllowSet(tableAllowList);
        var tables = new List<TableMeta>();

        foreach (DataRow row in tableRows.Rows)
        {
            var tableName = ReadString(row, "TABLE_NAME", "table_name");
            if (string.IsNullOrWhiteSpace(tableName))
            {
                continue;
            }

            var tableType = ReadString(row, "TABLE_TYPE", "table_type") ?? "TABLE";
            if (!IsAllowedTableType(tableType))
            {
                continue;
            }

            if (tableName.StartsWith("MSys", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var schemaName = ReadString(row, "TABLE_SCHEMA", "table_schema", "OWNER", "owner");
            if (!IsAllowedTable(allowSet, schemaName, tableName))
            {
                continue;
            }

            tables.Add(new TableMeta(schemaName, tableName, tableType));
        }

        return tables;
    }

    private static Dictionary<string, List<ColumnMeta>> ExtractColumns(DataTable columnRows)
    {
        var columnsByTable = new Dictionary<string, List<ColumnMeta>>(StringComparer.OrdinalIgnoreCase);

        foreach (DataRow row in columnRows.Rows)
        {
            var tableName = ReadString(row, "TABLE_NAME", "table_name");
            var columnName = ReadString(row, "COLUMN_NAME", "column_name");
            if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(columnName))
            {
                continue;
            }

            var schemaName = ReadString(row, "TABLE_SCHEMA", "table_schema", "OWNER", "owner");
            var key = BuildKey(schemaName, tableName);
            if (!columnsByTable.TryGetValue(key, out var columns))
            {
                columns = [];
                columnsByTable[key] = columns;
            }

            columns.Add(new ColumnMeta(
                ColumnName: columnName,
                TypeName: ReadString(row, "TYPE_NAME", "type_name"),
                DataType: ReadString(row, "DATA_TYPE", "data_type"),
                IsNullable: ReadString(row, "IS_NULLABLE", "is_nullable"),
                OrdinalPosition: ReadInt(row, "ORDINAL_POSITION", "ordinal_position") ?? int.MaxValue,
                MaxLength: ReadInt(row, "CHARACTER_MAXIMUM_LENGTH", "character_maximum_length", "COLUMN_SIZE", "column_size"),
                NumericPrecision: ReadInt(row, "NUMERIC_PRECISION", "numeric_precision"),
                NumericScale: ReadInt(row, "NUMERIC_SCALE", "numeric_scale")));
        }

        return columnsByTable;
    }

    private static HashSet<string> BuildAllowSet(IReadOnlyList<string>? tableAllowList)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tableAllowList is null)
        {
            return set;
        }

        foreach (var table in tableAllowList)
        {
            if (string.IsNullOrWhiteSpace(table))
            {
                continue;
            }

            set.Add(table.Trim());
        }

        return set;
    }

    private static bool IsAllowedTable(HashSet<string> allowSet, string? schemaName, string tableName)
    {
        if (allowSet.Count == 0)
        {
            return true;
        }

        var schemaQualified = string.IsNullOrWhiteSpace(schemaName)
            ? tableName
            : $"{schemaName}.{tableName}";

        return allowSet.Contains(tableName) || allowSet.Contains(schemaQualified);
    }

    private static bool IsAllowedTableType(string tableType)
    {
        return tableType.Trim().ToUpperInvariant() switch
        {
            "TABLE" => true,
            "BASE TABLE" => true,
            "VIEW" => true,
            _ => false
        };
    }

    private static string BuildKey(string? schemaName, string tableName)
    {
        return string.IsNullOrWhiteSpace(schemaName)
            ? tableName
            : $"{schemaName}.{tableName}";
    }

    private static object SummarizeConnectionTarget(string connectionString)
    {
        try
        {
            var builder = new DbConnectionStringBuilder
            {
                ConnectionString = connectionString
            };

            var server = ReadFromBuilder(builder, "Data Source", "Server", "Addr", "Address", "Network Address");
            var database = ReadFromBuilder(builder, "Initial Catalog", "Database");
            var file = ReadFromBuilder(builder, "Dbq", "File Name", "Data Source");

            return new
            {
                server,
                database,
                fileName = string.IsNullOrWhiteSpace(file) ? null : Path.GetFileName(file)
            };
        }
        catch
        {
            return new
            {
                server = (string?)null,
                database = (string?)null,
                fileName = (string?)null
            };
        }
    }

    private static string? ReadFromBuilder(DbConnectionStringBuilder builder, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!builder.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            var text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string? ReadString(DataRow row, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            if (!row.Table.Columns.Contains(name))
            {
                continue;
            }

            var value = row[name];
            if (value is DBNull || value is null)
            {
                continue;
            }

            var text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static int? ReadInt(DataRow row, params string[] columnNames)
    {
        var text = ReadString(row, columnNames);
        if (text is null)
        {
            return null;
        }

        if (int.TryParse(text, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string SanitizeExceptionMessage(string message)
    {
        return message.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private sealed record DataSourceInfo(string Name, string DataProvider, string ConnectionString);
    private sealed record TableMeta(string? SchemaName, string TableName, string TableType);
    private sealed record ColumnMeta(
        string ColumnName,
        string? TypeName,
        string? DataType,
        string? IsNullable,
        int OrdinalPosition,
        int? MaxLength,
        int? NumericPrecision,
        int? NumericScale);
}

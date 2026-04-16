using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var parsed = ParseArgs(args);
            if (string.IsNullOrWhiteSpace(parsed.ConnectionString))
            {
                WriteFailure("ARGUMENT_ERROR", "Missing required --connection-string argument.");
                return 2;
            }

            var maxTables = Clamp(parsed.MaxTables, 1, 200);
            var maxColumns = Clamp(parsed.MaxColumns, 1, 300);
            var allowSet = BuildAllowSet(parsed.TableAllowList);

            using (var connection = new OleDbConnection(parsed.ConnectionString))
            {
                connection.Open();

                var tableRows = connection.GetSchema("Tables");

                var tables = ExtractTables(tableRows, allowSet)
                    .OrderBy(t => t.SchemaName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(t => t.TableName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var selectedTables = tables.Take(maxTables).ToList();
                var columnsByTable = FetchScopedColumns(connection, selectedTables, out var columnScopeFallbackUsed);
                var tableLimitReached = tables.Count > selectedTables.Count;
                var columnLimitReached = false;

                var xml = new StringBuilder();
                xml.Append("<SchemaProbe success=\"true\"");
                xml.Append(" runtimeProvider=\"System.Data.OleDb.OleDbConnection\"");
                xml.Append(" totalTables=\"").Append(tables.Count.ToString(CultureInfo.InvariantCulture)).Append("\"");
                xml.Append(" returnedTables=\"").Append(selectedTables.Count.ToString(CultureInfo.InvariantCulture)).Append("\"");
                xml.Append(" tableLimitReached=\"").Append(tableLimitReached ? "true" : "false").Append("\"");

                xml.Append(">");

                foreach (var table in selectedTables)
                {
                    List<ColumnMeta> columns;
                    if (!columnsByTable.TryGetValue(BuildKey(table.SchemaName, table.TableName), out columns))
                    {
                        columns = new List<ColumnMeta>();
                    }

                    var orderedColumns = columns
                        .OrderBy(c => c.OrdinalPosition)
                        .ThenBy(c => c.ColumnName, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var selectedColumns = orderedColumns.Take(maxColumns).ToList();
                    if (orderedColumns.Count > selectedColumns.Count)
                    {
                        columnLimitReached = true;
                    }

                    xml.Append("<Table");
                    xml.Append(" schema=\"").Append(Escape(table.SchemaName ?? string.Empty)).Append("\"");
                    xml.Append(" name=\"").Append(Escape(table.TableName)).Append("\"");
                    xml.Append(" type=\"").Append(Escape(table.TableType)).Append("\"");
                    xml.Append(" totalColumns=\"").Append(orderedColumns.Count.ToString(CultureInfo.InvariantCulture)).Append("\"");
                    xml.Append(">");

                    foreach (var column in selectedColumns)
                    {
                        xml.Append("<Column");
                        xml.Append(" name=\"").Append(Escape(column.ColumnName)).Append("\"");
                        xml.Append(" typeName=\"").Append(Escape(column.TypeName ?? string.Empty)).Append("\"");
                        xml.Append(" dataType=\"").Append(Escape(column.DataType ?? string.Empty)).Append("\"");
                        xml.Append(" nullable=\"").Append(Escape(column.IsNullable ?? string.Empty)).Append("\"");
                        xml.Append(" ordinal=\"").Append(column.OrdinalPosition.ToString(CultureInfo.InvariantCulture)).Append("\"");
                        xml.Append(" maxLength=\"").Append((column.MaxLength ?? 0).ToString(CultureInfo.InvariantCulture)).Append("\"");
                        xml.Append(" numericPrecision=\"").Append((column.NumericPrecision ?? 0).ToString(CultureInfo.InvariantCulture)).Append("\"");
                        xml.Append(" numericScale=\"").Append((column.NumericScale ?? 0).ToString(CultureInfo.InvariantCulture)).Append("\"");
                        xml.Append("/>");
                    }

                    xml.Append("</Table>");
                }

                xml.Append("<Flags");
                xml.Append(" columnLimitReached=\"").Append(columnLimitReached ? "true" : "false").Append("\"");
                xml.Append(" columnScopeFallbackUsed=\"").Append(columnScopeFallbackUsed ? "true" : "false").Append("\"");
                xml.Append("/>");
                xml.Append("</SchemaProbe>");

                Console.Out.Write(xml.ToString());
                return 0;
            }
        }
        catch (Exception ex)
        {
            WriteFailure("SCHEMA_INSPECTION_ERROR", ex.Message);
            return 1;
        }
    }

    private static ParsedArgs ParseArgs(string[] args)
    {
        var parsed = new ParsedArgs();

        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key == "--connection-string" && i + 1 < args.Length)
            {
                parsed.ConnectionString = args[++i];
            }
            else if (key == "--table-allow-list" && i + 1 < args.Length)
            {
                parsed.TableAllowList = args[++i];
            }
            else if (key == "--max-tables" && i + 1 < args.Length)
            {
                int value;
                if (int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    parsed.MaxTables = value;
                }
            }
            else if (key == "--max-columns" && i + 1 < args.Length)
            {
                int value;
                if (int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    parsed.MaxColumns = value;
                }
            }
        }

        return parsed;
    }

    private static HashSet<string> BuildAllowSet(string csv)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(csv))
        {
            return set;
        }

        foreach (var part in csv.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                set.Add(trimmed);
            }
        }

        return set;
    }

    private static List<TableMeta> ExtractTables(DataTable tableRows, HashSet<string> allowSet)
    {
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
        var keysByTableName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

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

            List<ColumnMeta> columns;
            if (!columnsByTable.TryGetValue(key, out columns))
            {
                columns = new List<ColumnMeta>();
                columnsByTable[key] = columns;
            }

            HashSet<string> fullKeys;
            if (!keysByTableName.TryGetValue(tableName, out fullKeys))
            {
                fullKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                keysByTableName[tableName] = fullKeys;
            }

            fullKeys.Add(key);

            columns.Add(new ColumnMeta(
                columnName,
                ReadString(row, "TYPE_NAME", "type_name"),
                ReadString(row, "DATA_TYPE", "data_type"),
                ReadString(row, "IS_NULLABLE", "is_nullable"),
                ReadInt(row, "ORDINAL_POSITION", "ordinal_position") ?? int.MaxValue,
                ReadInt(row, "CHARACTER_MAXIMUM_LENGTH", "character_maximum_length", "COLUMN_SIZE", "column_size"),
                ReadInt(row, "NUMERIC_PRECISION", "numeric_precision"),
                ReadInt(row, "NUMERIC_SCALE", "numeric_scale")));
        }

        foreach (var entry in keysByTableName)
        {
            if (columnsByTable.ContainsKey(entry.Key) || entry.Value.Count != 1)
            {
                continue;
            }

            var fullKey = entry.Value.First();
            List<ColumnMeta> resolvedColumns;
            if (columnsByTable.TryGetValue(fullKey, out resolvedColumns))
            {
                columnsByTable[entry.Key] = resolvedColumns;
            }
        }

        return columnsByTable;
    }

    private static Dictionary<string, List<ColumnMeta>> FetchScopedColumns(
        OleDbConnection connection,
        IReadOnlyList<TableMeta> selectedTables,
        out bool usedFullScanFallback)
    {
        usedFullScanFallback = false;
        var scopedColumns = new Dictionary<string, List<ColumnMeta>>(StringComparer.OrdinalIgnoreCase);
        if (selectedTables.Count == 0)
        {
            return scopedColumns;
        }

        try
        {
            foreach (var table in selectedTables)
            {
                var restrictions = BuildColumnRestrictions(table.SchemaName, table.TableName);
                var scopedResponse = ExtractColumns(connection.GetSchema("Columns", restrictions));

                if (scopedResponse.Count == 0)
                {
                    continue;
                }

                if (!IsScopedResultForTable(scopedResponse.Keys, table.SchemaName, table.TableName))
                {
                    throw new InvalidOperationException("Provider returned unscoped column metadata.");
                }

                scopedColumns[BuildKey(table.SchemaName, table.TableName)] = ResolveColumnsForTable(
                    scopedResponse,
                    table.SchemaName,
                    table.TableName);
            }

            return scopedColumns;
        }
        catch
        {
            usedFullScanFallback = true;
            var fullScan = ExtractColumns(connection.GetSchema("Columns"));

            var filtered = new Dictionary<string, List<ColumnMeta>>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in selectedTables)
            {
                filtered[BuildKey(table.SchemaName, table.TableName)] = ResolveColumnsForTable(
                    fullScan,
                    table.SchemaName,
                    table.TableName);
            }

            return filtered;
        }
    }

    private static string[] BuildColumnRestrictions(string schemaName, string tableName)
    {
        return new[]
        {
            null,
            string.IsNullOrWhiteSpace(schemaName) ? null : schemaName,
            tableName,
            null
        };
    }

    private static bool IsScopedResultForTable(
        IEnumerable<string> keys,
        string schemaName,
        string tableName)
    {
        foreach (var key in keys)
        {
            if (!MatchesTableKey(key, schemaName, tableName))
            {
                return false;
            }
        }

        return true;
    }

    private static List<ColumnMeta> ResolveColumnsForTable(
        IDictionary<string, List<ColumnMeta>> columnsByTable,
        string schemaName,
        string tableName)
    {
        return columnsByTable
            .Where(entry => MatchesTableKey(entry.Key, schemaName, tableName))
            .SelectMany(entry => entry.Value)
            .GroupBy(column => column.OrdinalPosition + ":" + column.ColumnName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool MatchesTableKey(string key, string schemaName, string tableName)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
        {
            return string.Equals(key, tableName, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(key, BuildKey(schemaName, tableName), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedTable(HashSet<string> allowSet, string schemaName, string tableName)
    {
        if (allowSet.Count == 0)
        {
            return true;
        }

        var qualified = string.IsNullOrWhiteSpace(schemaName) ? tableName : schemaName + "." + tableName;
        return allowSet.Contains(tableName) || allowSet.Contains(qualified);
    }

    private static bool IsAllowedTableType(string tableType)
    {
        var normalized = tableType.Trim().ToUpperInvariant();
        return normalized == "TABLE" || normalized == "BASE TABLE" || normalized == "VIEW";
    }

    private static string BuildKey(string schemaName, string tableName)
    {
        return string.IsNullOrWhiteSpace(schemaName) ? tableName : schemaName + "." + tableName;
    }

    private static string ReadString(DataRow row, params string[] names)
    {
        foreach (var name in names)
        {
            if (!row.Table.Columns.Contains(name))
            {
                continue;
            }

            var value = row[name];
            if (value == DBNull.Value || value == null)
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

    private static int? ReadInt(DataRow row, params string[] names)
    {
        var text = ReadString(row, names);
        int parsed;
        if (string.IsNullOrWhiteSpace(text) || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
        {
            return null;
        }

        return parsed;
    }

    private static string Escape(string value)
    {
        return SecurityElement.Escape(value) ?? string.Empty;
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

    private static void WriteFailure(string code, string message)
    {
        Console.Out.Write("<SchemaProbe success=\"false\" code=\"");
        Console.Out.Write(Escape(code));
        Console.Out.Write("\" message=\"");
        Console.Out.Write(Escape(message));
        Console.Out.Write("\"/>");
    }

    private sealed class ParsedArgs
    {
        public string ConnectionString;
        public string TableAllowList;
        public int MaxTables = 50;
        public int MaxColumns = 100;
    }

    private sealed class TableMeta
    {
        public TableMeta(string schemaName, string tableName, string tableType)
        {
            SchemaName = schemaName;
            TableName = tableName;
            TableType = tableType;
        }

        public string SchemaName { get; private set; }
        public string TableName { get; private set; }
        public string TableType { get; private set; }
    }

    private sealed class ColumnMeta
    {
        public ColumnMeta(string columnName, string typeName, string dataType, string isNullable, int ordinalPosition, int? maxLength, int? numericPrecision, int? numericScale)
        {
            ColumnName = columnName;
            TypeName = typeName;
            DataType = dataType;
            IsNullable = isNullable;
            OrdinalPosition = ordinalPosition;
            MaxLength = maxLength;
            NumericPrecision = numericPrecision;
            NumericScale = numericScale;
        }

        public string ColumnName { get; private set; }
        public string TypeName { get; private set; }
        public string DataType { get; private set; }
        public string IsNullable { get; private set; }
        public int OrdinalPosition { get; private set; }
        public int? MaxLength { get; private set; }
        public int? NumericPrecision { get; private set; }
        public int? NumericScale { get; private set; }
    }
}

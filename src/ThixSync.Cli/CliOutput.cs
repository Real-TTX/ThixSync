using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThixSync.Cli;

/// <summary>Tables for people and JSON for scripts (camelCase, enums as words, indented).</summary>
internal static class CliOutput
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static void WriteJson<T>(TextWriter writer, T value) => writer.WriteLine(JsonSerializer.Serialize(value, Json));

    /// <summary>Columns padded to the widest cell; the last column is not padded.</summary>
    public static void WriteTable(TextWriter writer, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var widths = headers.Select((header, column) => rows.Select(row => row[column].Length).Append(header.Length).Max()).ToArray();
        WriteRow(writer, headers, widths);
        WriteRow(writer, widths.Select(width => new string('─', width)).ToList(), widths);
        foreach (var row in rows)
        {
            WriteRow(writer, row, widths);
        }
    }

    private static void WriteRow(TextWriter writer, IReadOnlyList<string> cells, int[] widths)
    {
        var line = string.Join("  ", cells.Select((cell, column) => column == cells.Count - 1 ? cell : cell.PadRight(widths[column])));
        writer.WriteLine(line.TrimEnd());
    }
}

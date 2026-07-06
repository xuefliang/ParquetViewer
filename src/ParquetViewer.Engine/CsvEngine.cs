using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ParquetViewer.Engine
{
    public class CsvEngine : IParquetEngine
    {
        public List<string> Fields { get; }
        public long RecordCount { get; }
        public int NumberOfPartitions => 1;
        public Dictionary<string, string> CustomMetadata => new();
        public IParquetMetadata Metadata { get; }
        public string Path { get; }

        private readonly string _filePath;
        private readonly Encoding _encoding;

        public CsvEngine(string filePath)
        {
            _filePath = filePath;
            Path = filePath;
            _encoding = DetectEncoding(filePath);

            using var reader = CreateReader(filePath);
            Fields = ReadCsvHeaders(reader);
            RecordCount = CountDataRows(reader);
            Metadata = new CsvParquetMetadata(RecordCount, Fields.Count);
        }

        private StreamReader CreateReader(string path)
        {
            return new StreamReader(path, _encoding, detectEncodingFromByteOrderMarks: false);
        }

        private static Encoding DetectEncoding(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);

            byte[] bom = new byte[4];
            int bytesRead = fs.Read(bom, 0, 4);

            if (bytesRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                return Encoding.UTF8;
            if (bytesRead >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                return Encoding.Unicode;
            if (bytesRead >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
                return Encoding.BigEndianUnicode;

            // No BOM: try UTF-8 first, fall back to system ANSI (GBK on Chinese Windows)
            if (IsValidUtf8(filePath))
                return Encoding.UTF8;

            return Encoding.GetEncoding(0);
        }

        private static bool IsValidUtf8(string filePath)
        {
            try
            {
                byte[] buffer = File.ReadAllBytes(filePath);
                int i = 0;
                while (i < buffer.Length)
                {
                    if (buffer[i] <= 0x7F)
                    {
                        i++;
                    }
                    else if (buffer[i] >= 0xC2 && buffer[i] <= 0xDF)
                    {
                        if (i + 1 >= buffer.Length || buffer[i + 1] < 0x80 || buffer[i + 1] > 0xBF)
                            return false;
                        i += 2;
                    }
                    else if (buffer[i] == 0xE0)
                    {
                        if (i + 2 >= buffer.Length || buffer[i + 1] < 0xA0 || buffer[i + 1] > 0xBF || buffer[i + 2] < 0x80 || buffer[i + 2] > 0xBF)
                            return false;
                        i += 3;
                    }
                    else if (buffer[i] >= 0xE1 && buffer[i] <= 0xEF)
                    {
                        if (i + 2 >= buffer.Length || buffer[i + 1] < 0x80 || buffer[i + 1] > 0xBF || buffer[i + 2] < 0x80 || buffer[i + 2] > 0xBF)
                            return false;
                        i += 3;
                    }
                    else if (buffer[i] == 0xF0)
                    {
                        if (i + 3 >= buffer.Length || buffer[i + 1] < 0x90 || buffer[i + 1] > 0xBF || buffer[i + 2] < 0x80 || buffer[i + 2] > 0xBF || buffer[i + 3] < 0x80 || buffer[i + 3] > 0xBF)
                            return false;
                        i += 4;
                    }
                    else if (buffer[i] >= 0xF1 && buffer[i] <= 0xF3)
                    {
                        if (i + 3 >= buffer.Length || buffer[i + 1] < 0x80 || buffer[i + 1] > 0xBF || buffer[i + 2] < 0x80 || buffer[i + 2] > 0xBF || buffer[i + 3] < 0x80 || buffer[i + 3] > 0xBF)
                            return false;
                        i += 4;
                    }
                    else if (buffer[i] == 0xF4)
                    {
                        if (i + 3 >= buffer.Length || buffer[i + 1] < 0x80 || buffer[i + 1] > 0x8F || buffer[i + 2] < 0x80 || buffer[i + 2] > 0xBF || buffer[i + 3] < 0x80 || buffer[i + 3] > 0xBF)
                            return false;
                        i += 4;
                    }
                    else
                    {
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<string> ReadCsvHeaders(StreamReader reader)
        {
            if (reader.EndOfStream)
                return new List<string>();

            var headerLine = reader.ReadLine() ?? string.Empty;
            return ParseCsvLine(headerLine).Select(h => h.Trim('"', ' ')).ToList();
        }

        private static long CountDataRows(StreamReader reader)
        {
            long count = 0;
            while (!reader.EndOfStream)
            {
                reader.ReadLine();
                count++;
            }
            return count;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var current = new System.Text.StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            fields.Add(current.ToString());
            return fields;
        }

        public Task<Func<bool, DataTable>> ReadRowsAsync(List<string> selectedFields, int offset, int recordCount,
            CancellationToken cancellationToken, IProgress<int>? progress = null)
        {
            return Task.FromResult<Func<bool, DataTable>>((showProgress) =>
            {
                var dataTable = new DataTable();
                var columns = Fields.Where(f => selectedFields.Contains(f)).ToList();
                foreach (var col in columns)
                {
                    dataTable.Columns.Add(col, typeof(string));
                }

                using var reader = CreateReader(_filePath);
                reader.ReadLine();

                long currentRow = 0;
                long rowsAdded = 0;

                while (!reader.EndOfStream)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var line = reader.ReadLine();
                    if (line == null)
                        break;

                    if (currentRow < offset)
                    {
                        currentRow++;
                        continue;
                    }

                    if (rowsAdded >= recordCount)
                        break;

                    var values = ParseCsvLine(line);
                    var row = dataTable.NewRow();
                    for (int i = 0; i < columns.Count; i++)
                    {
                        var fieldIndex = Fields.IndexOf(columns[i]);
                        if (fieldIndex >= 0 && fieldIndex < values.Count)
                        {
                            row[columns[i]] = values[fieldIndex].Trim('"');
                        }
                    }
                    dataTable.Rows.Add(row);
                    rowsAdded++;
                    currentRow++;
                    progress?.Report((int)rowsAdded);
                }

                return dataTable;
            });
        }

        public Task WriteDataToParquetFileAsync(DataTable dataTable, string path,
            CancellationToken cancellationToken, IProgress<int> progress,
            Dictionary<string, string>? customMetadata)
        {
            throw new NotSupportedException("CSV engine does not support writing to Parquet files.");
        }

        public void Dispose()
        {
        }

        private class CsvParquetMetadata : IParquetMetadata
        {
            public int ParquetVersion => 0;
            public int RowGroupCount => 0;
            public int RowCount { get; }
            public string CreatedBy => "ParquetViewer CSV Engine";
            public ICollection<IRowGroupMetadata> RowGroups => Array.Empty<IRowGroupMetadata>();
            public IParquetSchemaElement SchemaTree { get; }

            public CsvParquetMetadata(long rowCount, int fieldCount)
            {
                RowCount = (int)rowCount;
                SchemaTree = new CsvSchemaElement(fieldCount);
            }
        }

        private class CsvSchemaElement : IParquetSchemaElement
        {
            public string Path => "csv_schema";
            public ICollection<IParquetSchemaElement> Children { get; }
            public Type ClrType => typeof(string);
            public FieldTypeId FieldType => FieldTypeId.Struct;
            public RepetitionTypeId? RepetitionType => RepetitionTypeId.Required;
            public bool IsPrimitive => false;
            public string? Type => null;
            public int? TypeLength => null;
            public int? NumChildren => Children.Count;
            public string? ConvertedType => null;
            public int? Scale => null;
            public int? Precision => null;
            public object? LogicalType => null;

            public CsvSchemaElement(int fieldCount)
            {
                var children = new List<IParquetSchemaElement>();
                for (int i = 0; i < fieldCount; i++)
                {
                    children.Add(new CsvSchemaChildElement($"field_{i}"));
                }
                Children = children;
            }

            public IParquetSchemaElement GetChildCI(string name) => Children.FirstOrDefault(c => c.Path.Equals(name, StringComparison.InvariantCultureIgnoreCase))
                ?? throw new ArgumentException($"Child '{name}' not found");

            public IParquetSchemaElement GetChild(string name) => Children.FirstOrDefault(c => c.Path.Equals(name))
                ?? throw new ArgumentException($"Child '{name}' not found");

            public IParquetSchemaElement GetListField() => throw new NotSupportedException();
            public IParquetSchemaElement GetListItemField() => throw new NotSupportedException();
            public IParquetSchemaElement GetSingleOrByName(string name) => GetChild(name);
            public IParquetSchemaElement GetMapKeyValueField() => throw new NotSupportedException();
            public IParquetSchemaElement GetMapKeyField() => throw new NotSupportedException();
            public IParquetSchemaElement GetMapValueField() => throw new NotSupportedException();
        }

        private class CsvSchemaChildElement : IParquetSchemaElement
        {
            public string Path { get; }
            public ICollection<IParquetSchemaElement> Children => Array.Empty<IParquetSchemaElement>();
            public Type ClrType => typeof(string);
            public FieldTypeId FieldType => FieldTypeId.Primitive;
            public RepetitionTypeId? RepetitionType => RepetitionTypeId.Optional;
            public bool IsPrimitive => true;
            public string? Type => "BYTE_ARRAY";
            public int? TypeLength => null;
            public int? NumChildren => 0;
            public string? ConvertedType => "UTF8";
            public int? Scale => null;
            public int? Precision => null;
            public object? LogicalType => "String";

            public CsvSchemaChildElement(string name)
            {
                Path = name;
            }

            public IParquetSchemaElement GetChildCI(string name) => throw new NotSupportedException();
            public IParquetSchemaElement GetChild(string name) => throw new NotSupportedException();
            public IParquetSchemaElement GetListField() => throw new NotSupportedException();
            public IParquetSchemaElement GetListItemField() => throw new NotSupportedException();
            public IParquetSchemaElement GetSingleOrByName(string name) => this;
            public IParquetSchemaElement GetMapKeyValueField() => throw new NotSupportedException();
            public IParquetSchemaElement GetMapKeyField() => throw new NotSupportedException();
            public IParquetSchemaElement GetMapValueField() => throw new NotSupportedException();
        }
    }
}

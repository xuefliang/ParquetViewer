# CSV Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add ability to open and display `.csv` files in ParquetViewer

**Architecture:** Create a `CsvEngine` implementing `IParquetEngine` so CSV files flow through the existing open/load pipeline. Only the engine creation point in `OpenFieldSelectionDialog` needs a branch for `.csv` extension. No new projects, no external dependencies — .NET's built-in `TextFieldParser` (Microsoft.VisualBasic.FileIO) handles CSV parsing.

**Tech Stack:** .NET 10 WinForms, `Microsoft.VisualBasic.FileIO.TextFieldParser` (built-in)

---

### Task 1: Create `CsvEngine` class

**Files:**
- Create: `src/ParquetViewer.Engine/CsvEngine.cs`

Implements `IParquetEngine` for CSV files. On construction, reads the CSV header and counts rows. `ReadRowsAsync` returns a deferred function that parses the CSV and builds a `DataTable`.

Key design:
- `Fields` → header columns from first row
- `RecordCount` → row count (file line count - 1)
- `NumberOfPartitions` → 1
- `CustomMetadata` → empty `Dictionary`
- `Metadata` → return a minimal `ParquetMetadata` stub or `null`-safe object (needs `IParquetMetadata` with empty lists)
- `WriteDataToParquetFileAsync` → throw `NotSupportedException`
- `ReadRowsAsync` → use `TextFieldParser` to read CSV, apply offset/limit, return `Func<bool, DataTable>` (same pattern as other engines)
- `Path` → the file path

```
src/ParquetViewer.Engine/CsvEngine.cs
```

- [ ] **Step 1: Create `CsvEngine.cs`**

```csharp
using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
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

        public CsvEngine(string filePath)
        {
            _filePath = filePath;
            Path = filePath;

            using var reader = new StreamReader(filePath);
            Fields = ReadCsvHeaders(reader);
            RecordCount = CountDataRows(reader);
            Metadata = new CsvParquetMetadata();
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

                using var reader = new StreamReader(_filePath);
                reader.ReadLine(); // skip header

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
                    for (int i = 0; i < Math.Min(columns.Count, values.Count); i++)
                    {
                        // Map by column name index
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
            public int RowGroupCount => 0;
            public long FileCreatedBy => 0;
            public long FileCreatedByAppVersion => 0;
            public int NumberOfRowGroups => 0;
            public IReadOnlyList<IParquetSchemaElement> Schema => Array.Empty<IParquetSchemaElement>();
            public IReadOnlyList<string> RowGroups => Array.Empty<string>();
        }
    }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/ParquetViewer.sln --configuration Debug 2>&1 | Select-String -NotMatch "warning CS"` (or similar)
Expected: Build succeeds

- [ ] **Step 3: Commit**

```bash
git add src/ParquetViewer.Engine/CsvEngine.cs
git commit -m "feat: add CsvEngine implementing IParquetEngine"
```

---

### Task 2: Modify `OpenFieldSelectionDialog` to route CSV files

**Files:**
- Modify: `src/ParquetViewer/MainForm.cs:192-278`

In `OpenFieldSelectionDialog`, before attempting to create a ParquetNET engine, check if `OpenFileOrFolderPath` ends with `.csv`. If so, create a `CsvEngine` directly.

- [ ] **Step 1: Add CSV branch in `OpenFieldSelectionDialog`**

```csharp
if (this._openParquetEngine == null)
{
    try
    {
        // Route CSV files to CsvEngine
        if (this.OpenFileOrFolderPath.EndsWith(".csv", StringComparison.InvariantCultureIgnoreCase))
        {
            this._openParquetEngine = new CsvEngine(this.OpenFileOrFolderPath);
        }
        else
        {
            this._openParquetEngine = await Engine.ParquetNET.ParquetEngine.OpenFileOrFolderAsync(this.OpenFileOrFolderPath, default);
        }
    }
```

Also add `using ParquetViewer.Engine;` at top of file.

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/ParquetViewer.sln --configuration Debug`
Expected: Build succeeds

- [ ] **Step 3: Commit**

```bash
git add src/ParquetViewer/MainForm.cs
git commit -m "feat: route CSV files to CsvEngine in OpenFieldSelectionDialog"
```

---

### Task 3: Update file open dialog filter to include CSV

**Files:**
- Modify: `src/ParquetViewer/MainForm.resx:551-553`

Change the `openParquetFileDialog.Filter` resource to include CSV.

- [ ] **Step 1: Update filter string in resx**

Change:
```xml
<data name="openParquetFileDialog.Filter" xml:space="preserve">
    <value>Parquet Files|*.parquet;*.snappy;*.gz;*.gzip</value>
</data>
```
To:
```xml
<data name="openParquetFileDialog.Filter" xml:space="preserve">
    <value>Supported Files|*.parquet;*.snappy;*.gz;*.gzip;*.csv|Parquet Files|*.parquet;*.snappy;*.gz;*.gzip|CSV Files|*.csv</value>
</data>
```

- [ ] **Step 2: Update also the localization resource files**

Check and update the filter in localized `.resx` files (`.tr.resx`, `.zh-CN.resx`, `.zh-TW.resx`) if they override the filter.

- [ ] **Step 3: Commit**

```bash
git add src/ParquetViewer/MainForm.resx
git commit -m "feat: add CSV to file open dialog filter"
```

---

### Task 4: Handle CSV in the performance tooltip

**Files:**
- Modify: `src/ParquetViewer/MainForm.cs:408`

Update the engine name display in the performance tooltip (line 408) to include `CsvEngine`:

```csharp
$"Engine: {(engine is Engine.ParquetNET.ParquetEngine ? "ParquetNET" : engine is Engine.DuckDB.ParquetEngine ? "DuckDB" : "CSV")}";
```

- [ ] **Step 1: Apply the change**

- [ ] **Step 2: Commit**

```bash
git add src/ParquetViewer/MainForm.cs
git commit -m "feat: show CSV engine name in performance tooltip"
```

---

### Task 5: Update `FileOpenEvent` for CSV

**Files:**
- Modify: `src/ParquetViewer/MainForm.cs:414-417`

In the `FileOpenEvent.FireAndForget` call (line 414-417), the engine type is set. For CSV, don't fire analytics (or add a new engine type).

Simplest approach: skip analytics when engine is `CsvEngine` by adding a check before the `FileOpenEvent.FireAndForget` call:

```csharp
if (wasSuccessful && engine is not CsvEngine)
{
    var engineType = this._openParquetEngine is Engine.ParquetNET.ParquetEngine
        ? FileOpenEvent.ParquetEngineTypeId.ParquetNET
        : FileOpenEvent.ParquetEngineTypeId.DuckDB;
    FileOpenEvent.FireAndForget(...);
}
```

- [ ] **Step 1: Apply the change**

- [ ] **Step 2: Commit**

```bash
git add src/ParquetViewer/MainForm.cs
git commit -m "feat: skip analytics for CSV file opens"
```

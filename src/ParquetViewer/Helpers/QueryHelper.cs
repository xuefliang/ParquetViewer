using System.Data;
using System.Text.RegularExpressions;

namespace ParquetViewer.Helpers
{
    public static class QueryHelper
    {
        private static readonly Regex _validColumnNameRegex = new("^[a-zA-Z0-9_]+$");

        /// <summary>
        /// Wraps column names that contain non-ASCII characters or punctuation in brackets so that
        /// DataView <see cref="DataView.RowFilter"/> expressions can reference them.
        /// Replacements are only made outside of single-quoted string literals, and occurrences that
        /// are already bracketed or form part of a larger identifier are left unchanged.
        /// </summary>
        public static string AutoBracketColumnNames(string queryText, IEnumerable<string> columnNames)
        {
            ArgumentNullException.ThrowIfNull(queryText);
            ArgumentNullException.ThrowIfNull(columnNames);

            List<string> columnsToBracket = columnNames
                .Where(name => !string.IsNullOrWhiteSpace(name)
                    && !_validColumnNameRegex.IsMatch(name)
                    && !name.StartsWith('['))
                .OrderByDescending(name => name.Length)
                .ToList();

            if (columnsToBracket.Count == 0)
            {
                return queryText;
            }

            //Even indices are outside single-quoted string literals.
            string[] parts = queryText.Split('\'');
            for (int columnIndex = 0; columnIndex < columnsToBracket.Count; columnIndex++)
            {
                string columnName = columnsToBracket[columnIndex];
                Regex columnNameRegex = BuildColumnNameRegex(columnName);

                for (int partIndex = 0; partIndex < parts.Length; partIndex += 2)
                {
                    parts[partIndex] = columnNameRegex.Replace(parts[partIndex], $"[{columnName}]");
                }
            }

            return string.Join('\'', parts);
        }

        private static Regex BuildColumnNameRegex(string columnName)
        {
            string escapedColumnName = Regex.Escape(columnName);
            return new Regex($@"(?<!\[|\w){escapedColumnName}(?!\w|\])");
        }
    }
}

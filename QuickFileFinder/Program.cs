using Spectre.Console;
using System.CommandLine;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QuickFileFinder
{
    internal class Program
    {
        static async Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand("在指定目录中搜索文本");

            var textArgument = new Argument<string>("text")
            {
                Description = "要搜索的文本"
            };

            var directoryArgument = new Argument<string>("directory")
            {
                Description = "搜索目录",
                DefaultValueFactory = _ => Directory.GetCurrentDirectory()
            };

            // 添加大小写敏感选项
            var caseSensitiveOption = new Option<bool>("--case-sensitive", "-c")
            {
                Description = "区分大小写搜索",
                DefaultValueFactory = _ => false
            };

            rootCommand.Add(textArgument);
            rootCommand.Add(directoryArgument);
            rootCommand.Add(caseSensitiveOption);

            rootCommand.SetAction(async parseResult =>
            {
                await SearchHandlerAsync(parseResult.GetValue(textArgument), parseResult.GetValue(directoryArgument), parseResult.GetValue(caseSensitiveOption));
            });

            var parseResult = rootCommand.Parse(args);
            return await parseResult.InvokeAsync();
        }

        private static async Task SearchHandlerAsync(
            string searchText,
            string directory,
            bool caseSensitive)
        {
            AnsiConsole.MarkupLine($"[green]正在搜索:[/] '{searchText}'");
            AnsiConsole.MarkupLine($"[green]搜索目录:[/] '{directory}'");
            AnsiConsole.MarkupLine($"[green]大小写敏感:[/] {(caseSensitive ? "是" : "否")}");
            AnsiConsole.MarkupLine($"[green]搜索范围:[/] 文件/文件夹名称, .json 文件, .db/.sqlite 文件");

            var searchResults = new List<SearchResult>();
            var cancellationTokenSource = new CancellationTokenSource();

            var comparisonType = caseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var unifiedTask = ctx.AddTask("[blue]正在搜索所有文件...[/]");

                    await Task.Run(() =>
                    {
                        UnifiedFileSearch(searchText, directory, comparisonType, searchResults, unifiedTask);
                        unifiedTask.StopTask();
                    }, cancellationTokenSource.Token);
                });

            DisplayResults(searchResults);
        }

        private static void UnifiedFileSearch(
            string searchText,
            string directory,
            StringComparison comparisonType,
            List<SearchResult> results,
            ProgressTask progress)
        {
            try
            {
                var allEntries = Directory.EnumerateFileSystemEntries(
                    directory,
                    "*",
                    new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true
                    }).ToList();

                int totalEntries = allEntries.Count;

                for (int i = 0; i < totalEntries; i++)
                {
                    var entry = allEntries[i];

                    // 检查名称匹配（文件和目录）
                    CheckNameMatch(searchText, entry, comparisonType, results);

                    // 如果是文件，根据扩展名调用不同的处理器
                    if (File.Exists(entry))
                    {
                        var extension = Path.GetExtension(entry).ToLowerInvariant();

                        // JSON文件处理
                        if (extension == ".json")
                        {
                            SearchInJsonFile(searchText, entry, comparisonType, results);
                        }
                        // 数据库文件处理
                        else if (extension == ".db" ||
                                 extension == ".sqlite" ||
                                 extension == ".sqlite3")
                        {
                            SearchInDatabaseFile(searchText, entry, comparisonType, results);
                        }
                    }

                    progress.Value = (i + 1) / (double)totalEntries * 100;
                }
            }
            catch (UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine("[yellow]警告: 某些目录访问被拒绝[/]");
            }
            catch (DirectoryNotFoundException)
            {
                AnsiConsole.MarkupLine("[red]错误: 目录不存在[/]");
            }
        }

        private static void CheckNameMatch(
            string searchText,
            string entryPath,
            StringComparison comparisonType,
            List<SearchResult> results)
        {
            var entryName = Path.GetFileName(entryPath);

            if (entryName.Contains(searchText, comparisonType))
            {
                var isDirectory = Directory.Exists(entryPath);
                results.Add(new SearchResult
                {
                    Path = entryPath,
                    MatchType = "名称匹配",
                    AdditionalInfo = isDirectory ? $"目录: {entryName}" : $"文件: {entryName}"
                });
            }
        }

        private static void SearchInJsonFile(
            string searchText,
            string filePath,
            StringComparison comparisonType,
            List<SearchResult> results)
        {
            try
            {
                // 检查文件大小，避免处理超大文件
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > 50 * 1024 * 1024) // 50MB 限制
                {
                    return;
                }

                var lines = File.ReadAllLines(filePath);
                var lineMatches = new List<string>();

                for (int lineNumber = 0; lineNumber < lines.Length; lineNumber++)
                {
                    if (lines[lineNumber].Contains(searchText, comparisonType))
                    {
                        lineMatches.Add($"第 {lineNumber + 1} 行");

                        // 尝试解析 JSON 结构
                        try
                        {
                            var lineContent = lines[lineNumber].Trim();
                            if (lineContent.StartsWith('{') || lineContent.StartsWith('['))
                            {
                                using var document = JsonDocument.Parse(lineContent);
                                var jsonInfo = new List<string>();
                                ExtractJsonPaths(document.RootElement, "", jsonInfo);

                                if (jsonInfo.Count != 0)
                                {
                                    lineMatches[^1] += $" (JSON路径: {string.Join(", ", jsonInfo)})";
                                }
                            }
                        }
                        catch (JsonException)
                        {
                            // 不是完整 JSON，继续搜索
                        }
                    }
                }

                if (lineMatches.Count != 0)
                {
                    results.Add(new SearchResult
                    {
                        Path = filePath,
                        MatchType = "JSON内容匹配",
                        AdditionalInfo = string.Join("; ", lineMatches)
                    });
                }
            }
            catch (IOException)
            {
                // 忽略无法读取的文件
            }
            catch (JsonException)
            {
                // JSON 解析失败，按普通文本文件处理
                SearchInTextFile(searchText, filePath, comparisonType, results, "JSON");
            }
        }

        private static void SearchInTextFile(
            string searchText,
            string filePath,
            StringComparison comparisonType,
            List<SearchResult> results,
            string fileType)
        {
            try
            {
                var lines = File.ReadAllLines(filePath);
                var lineMatches = new List<string>();

                for (int lineNumber = 0; lineNumber < lines.Length; lineNumber++)
                {
                    if (lines[lineNumber].Contains(searchText, comparisonType))
                    {
                        lineMatches.Add($"第 {lineNumber + 1} 行");
                    }
                }

                if (lineMatches.Count != 0)
                {
                    results.Add(new SearchResult
                    {
                        Path = filePath,
                        MatchType = $"{fileType}内容匹配",
                        AdditionalInfo = string.Join("; ", lineMatches)
                    });
                }
            }
            catch (IOException)
            {
                // 忽略无法读取的文件
            }
        }

        private static void ExtractJsonPaths(JsonElement element, string currentPath, List<string> paths)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        ExtractJsonPaths(property.Value,
                            string.IsNullOrEmpty(currentPath) ? property.Name : $"{currentPath}.{property.Name}",
                            paths);
                    }
                    break;

                case JsonValueKind.Array:
                    int index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        ExtractJsonPaths(item, $"{currentPath}[{index}]", paths);
                        index++;
                    }
                    break;

                default:
                    paths.Add(currentPath);
                    break;
            }
        }

        private static void SearchInDatabaseFile(
            string searchText,
            string filePath,
            StringComparison comparisonType,
            List<SearchResult> results)
        {
            try
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = filePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Shared
                }.ToString();

                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                var tableMatches = new List<string>();

                // 获取所有表
                var tableCommand = connection.CreateCommand();
                tableCommand.CommandText =
                    "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";

                using var tableReader = tableCommand.ExecuteReader();

                while (tableReader.Read())
                {
                    var tableName = tableReader.GetString(0);

                    // 检查表名是否匹配
                    if (tableName.Contains(searchText, comparisonType))
                    {
                        tableMatches.Add($"表名匹配: {tableName}");
                    }

                    // 获取表结构
                    var schemaCommand = connection.CreateCommand();
                    schemaCommand.CommandText = $"PRAGMA table_info({tableName})";

                    using var schemaReader = schemaCommand.ExecuteReader();
                    var columns = new List<(string Name, string Type)>();

                    while (schemaReader.Read())
                    {
                        var columnName = schemaReader.GetString(1);
                        var columnType = schemaReader.GetString(2);
                        columns.Add((columnName, columnType));

                        // 检查列名是否匹配
                        if (columnName.Contains(searchText, comparisonType))
                        {
                            tableMatches.Add($"列名匹配: {tableName}.{columnName}");
                        }
                    }

                    // 搜索每个文本列的内容
                    foreach (var (columnName, columnType) in columns)
                    {
                        // 只搜索可能是文本类型的列
                        if (IsTextColumn(columnType))
                        {
                            try
                            {
                                var searchCommand = connection.CreateCommand();

                                if (comparisonType == StringComparison.OrdinalIgnoreCase)
                                {
                                    // 不区分大小写搜索
                                    searchCommand.CommandText = $@"
                                        SELECT COUNT(*) as MatchCount 
                                        FROM [{tableName}] 
                                        WHERE [{columnName}] LIKE @searchPattern COLLATE NOCASE";
                                }
                                else
                                {
                                    // 区分大小写搜索
                                    searchCommand.CommandText = $@"
                                        SELECT COUNT(*) as MatchCount 
                                        FROM [{tableName}] 
                                        WHERE [{columnName}] LIKE @searchPattern";
                                }

                                searchCommand.Parameters.AddWithValue("@searchPattern", $"%{searchText}%");

                                var matchCount = Convert.ToInt32(searchCommand.ExecuteScalar());

                                if (matchCount > 0)
                                {
                                    tableMatches.Add($"表: {tableName}, 列: {columnName}, 匹配数: {matchCount}");
                                }
                            }
                            catch (SqliteException)
                            {
                                // 忽略无法搜索的列
                            }
                        }
                    }
                }

                if (tableMatches.Count != 0)
                {
                    results.Add(new SearchResult
                    {
                        Path = filePath,
                        MatchType = "数据库内容匹配",
                        AdditionalInfo = string.Join("; ", tableMatches)
                    });
                }

                connection.Close();
            }
            catch (SqliteException)
            {
                // 忽略无法打开的数据库文件
            }
        }

        private static bool IsTextColumn(string columnType)
        {
            if (string.IsNullOrEmpty(columnType))
                return true; // SQLite 默认类型为 TEXT

            var typeLower = columnType.ToLowerInvariant();
            return typeLower.Contains("text") ||
                   typeLower.Contains("char") ||
                   typeLower.Contains("clob") ||
                   typeLower == ""; // 无类型声明时默认为 TEXT
        }

        private static void DisplayResults(List<SearchResult> results)
        {
            if (results.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]未找到匹配项[/]");
                return;
            }

            var groupedResults = results.GroupBy(r => r.MatchType).ToList();

            AnsiConsole.MarkupLine($"\n[bold underline]搜索结果:[/] ([green]{results.Count}[/] 个匹配项)");

            foreach (var group in groupedResults)
            {
                AnsiConsole.MarkupLine($"\n[bold]{group.Key}:[/] ([yellow]{group.Count()}[/] 个结果)");

                var table = new Table();
                table.AddColumn(new TableColumn("路径").LeftAligned());
                table.AddColumn(new TableColumn("匹配信息").LeftAligned());

                foreach (var result in group)
                {
                    table.AddRow(
                        new Markup(result.Path.EscapeMarkup()),
                        new Markup(result.AdditionalInfo?.EscapeMarkup() ?? ""));
                }

                AnsiConsole.Write(table);
            }

            AnsiConsole.MarkupLine($"\n[green]✅ 搜索完成! 总计找到 {results.Count} 个匹配项[/]");
        }

        private class SearchResult
        {
            public string Path { get; set; } = string.Empty;
            public string MatchType { get; set; } = string.Empty;
            public string? AdditionalInfo { get; set; }
        }
    }
}
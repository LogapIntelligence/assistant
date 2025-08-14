using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace assistant
{
    public class CodeEditApplicator
    {
        private readonly double _minimumSimilarity;

        public CodeEditApplicator(double minimumSimilarity = 0.90)
        {
            _minimumSimilarity = minimumSimilarity;
        }

        public ApplyEditResult ApplyEdits(string fileContent, List<CodeEdit> edits)
        {
            var result = new ApplyEditResult
            {
                Success = true,
                AppliedEdits = new List<AppliedEdit>(),
                OriginalContent = fileContent,
                ModifiedContent = fileContent
            };

            if (edits == null || !edits.Any())
            {
                result.Success = false;
                result.Message = "No edits to apply";
                return result;
            }

            // Sort edits by their position in the file (reverse order to maintain positions)
            var sortedEdits = SortEditsByPosition(fileContent, edits);

            // Create a local variable to work with
            string modifiedContent = result.ModifiedContent;

            foreach (var edit in sortedEdits)
            {
                // Pass the local variable by reference
                var appliedEdit = ApplySingleEdit(ref modifiedContent, edit);
                result.AppliedEdits.Add(appliedEdit);

                if (!appliedEdit.Success)
                {
                    result.Success = false;
                }
            }

            // Assign the modified content back to the property
            result.ModifiedContent = modifiedContent;

            result.Message = GenerateSummary(result.AppliedEdits);
            return result;
        }

        private List<CodeEdit> SortEditsByPosition(string content, List<CodeEdit> edits)
        {
            var editsWithPosition = new List<(CodeEdit edit, int position)>();

            foreach (var edit in edits)
            {
                var match = FindBestMatch(content, edit.Source);
                if (match != null)
                {
                    editsWithPosition.Add((edit, match.Position));
                }
                else
                {
                    editsWithPosition.Add((edit, -1));
                }
            }

            // Sort by position in reverse order (bottom to top) to maintain positions
            return editsWithPosition
                .OrderByDescending(e => e.position)
                .Select(e => e.edit)
                .ToList();
        }

        private AppliedEdit ApplySingleEdit(ref string content, CodeEdit edit)
        {
            var appliedEdit = new AppliedEdit
            {
                Edit = edit,
                Type = edit.Type
            };

            try
            {
                // Clean up escaped characters in source and new content
                var cleanSource = UnescapeString(edit.Source);
                var cleanNew = UnescapeString(edit.New ?? "");

                // Find the best match for the source text
                var match = FindBestMatch(content, cleanSource);

                if (match == null)
                {
                    appliedEdit.Success = false;
                    appliedEdit.Message = $"Could not find source text (searched for: '{TruncateString(cleanSource, 50)}')";
                    return appliedEdit;
                }

                appliedEdit.MatchSimilarity = match.Similarity;
                appliedEdit.MatchedText = match.MatchedText;

                // Apply the edit based on type
                switch (edit.Type?.ToLower())
                {
                    case "replace":
                        content = ReplaceText(content, match, cleanNew);
                        appliedEdit.Success = true;
                        appliedEdit.Message = $"Replaced text at position {match.Position}";
                        break;

                    case "append":
                        content = AppendText(content, match, cleanNew);
                        appliedEdit.Success = true;
                        appliedEdit.Message = $"Appended text after position {match.Position + match.Length}";
                        break;

                    case "remove":
                        content = RemoveText(content, match);
                        appliedEdit.Success = true;
                        appliedEdit.Message = $"Removed text at position {match.Position}";
                        break;

                    default:
                        appliedEdit.Success = false;
                        appliedEdit.Message = $"Unknown edit type: {edit.Type}";
                        break;
                }
            }
            catch (Exception ex)
            {
                appliedEdit.Success = false;
                appliedEdit.Message = $"Error applying edit: {ex.Message}";
            }

            return appliedEdit;
        }

        private string ReplaceText(string content, TextMatch match, string newText)
        {
            return content.Substring(0, match.Position) +
                   newText +
                   content.Substring(match.Position + match.Length);
        }

        private string AppendText(string content, TextMatch match, string newText)
        {
            var insertPosition = match.Position + match.Length;

            // Add newline if not present
            if (!string.IsNullOrEmpty(newText) && !newText.StartsWith("\n") && !newText.StartsWith("\r\n"))
            {
                newText = Environment.NewLine + newText;
            }

            return content.Insert(insertPosition, newText);
        }

        private string RemoveText(string content, TextMatch match)
        {
            return content.Remove(match.Position, match.Length);
        }

        private TextMatch FindBestMatch(string content, string searchText)
        {
            if (string.IsNullOrEmpty(searchText))
                return null;

            // First try exact match
            var exactIndex = content.IndexOf(searchText, StringComparison.Ordinal);
            if (exactIndex >= 0)
            {
                return new TextMatch
                {
                    Position = exactIndex,
                    Length = searchText.Length,
                    Similarity = 1.0,
                    MatchedText = searchText
                };
            }

            // Try fuzzy matching
            return FindFuzzyMatch(content, searchText);
        }

        private TextMatch FindFuzzyMatch(string content, string searchText)
        {
            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var searchLines = searchText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            TextMatch bestMatch = null;
            double bestSimilarity = 0;

            // If searching for a single line
            if (searchLines.Length == 1)
            {
                var position = 0;
                foreach (var line in lines)
                {
                    var similarity = CalculateSimilarity(NormalizeWhitespace(line), NormalizeWhitespace(searchLines[0]));
                    if (similarity > bestSimilarity && similarity >= _minimumSimilarity)
                    {
                        bestSimilarity = similarity;
                        bestMatch = new TextMatch
                        {
                            Position = position,
                            Length = line.Length,
                            Similarity = similarity,
                            MatchedText = line
                        };
                    }
                    position += line.Length + Environment.NewLine.Length;
                }
            }
            else
            {
                // Multi-line search - look for matching block
                for (int i = 0; i <= lines.Length - searchLines.Length; i++)
                {
                    var blockSimilarity = CalculateBlockSimilarity(lines, i, searchLines);
                    if (blockSimilarity > bestSimilarity && blockSimilarity >= _minimumSimilarity)
                    {
                        bestSimilarity = blockSimilarity;

                        // Calculate position and length
                        var position = 0;
                        for (int j = 0; j < i; j++)
                        {
                            position += lines[j].Length + Environment.NewLine.Length;
                        }

                        var length = 0;
                        for (int j = 0; j < searchLines.Length; j++)
                        {
                            length += lines[i + j].Length;
                            if (j < searchLines.Length - 1)
                                length += Environment.NewLine.Length;
                        }

                        bestMatch = new TextMatch
                        {
                            Position = position,
                            Length = length,
                            Similarity = blockSimilarity,
                            MatchedText = string.Join(Environment.NewLine, lines.Skip(i).Take(searchLines.Length))
                        };
                    }
                }
            }

            return bestMatch;
        }

        private double CalculateBlockSimilarity(string[] contentLines, int startIndex, string[] searchLines)
        {
            if (startIndex + searchLines.Length > contentLines.Length)
                return 0;

            double totalSimilarity = 0;
            for (int i = 0; i < searchLines.Length; i++)
            {
                totalSimilarity += CalculateSimilarity(
                    NormalizeWhitespace(contentLines[startIndex + i]),
                    NormalizeWhitespace(searchLines[i])
                );
            }

            return totalSimilarity / searchLines.Length;
        }

        private double CalculateSimilarity(string str1, string str2)
        {
            if (string.IsNullOrEmpty(str1) && string.IsNullOrEmpty(str2))
                return 1.0;
            if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2))
                return 0.0;
            if (str1.Equals(str2, StringComparison.Ordinal))
                return 1.0;

            // Use Levenshtein distance for similarity calculation
            var distance = LevenshteinDistance(str1, str2);
            var maxLength = Math.Max(str1.Length, str2.Length);

            return 1.0 - (double)distance / maxLength;
        }

        private int LevenshteinDistance(string s1, string s2)
        {
            int[,] d = new int[s1.Length + 1, s2.Length + 1];

            for (int i = 0; i <= s1.Length; i++)
                d[i, 0] = i;
            for (int j = 0; j <= s2.Length; j++)
                d[0, j] = j;

            for (int i = 1; i <= s1.Length; i++)
            {
                for (int j = 1; j <= s2.Length; j++)
                {
                    int cost = (s1[i - 1] == s2[j - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost
                    );
                }
            }

            return d[s1.Length, s2.Length];
        }

        private string NormalizeWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // Normalize multiple spaces to single space
            text = Regex.Replace(text, @"\s+", " ");

            // Trim leading and trailing whitespace
            return text.Trim();
        }

        private string UnescapeString(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // Unescape common escaped characters
            text = text.Replace("\\n", "\n")
                      .Replace("\\r", "\r")
                      .Replace("\\t", "\t")
                      .Replace("\\\"", "\"")
                      .Replace("\\'", "'")
                      .Replace("\\\\", "\\");

            return text;
        }

        private string TruncateString(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;

            return text.Substring(0, maxLength) + "...";
        }

        private string GenerateSummary(List<AppliedEdit> appliedEdits)
        {
            var successful = appliedEdits.Count(e => e.Success);
            var failed = appliedEdits.Count(e => !e.Success);

            var summary = new StringBuilder();
            summary.AppendLine($"Applied {successful} of {appliedEdits.Count} edits successfully");

            if (failed > 0)
            {
                summary.AppendLine($"\nFailed edits:");
                foreach (var edit in appliedEdits.Where(e => !e.Success))
                {
                    summary.AppendLine($"  • {edit.Edit.Reason}: {edit.Message}");
                }
            }

            var avgSimilarity = appliedEdits
                .Where(e => e.MatchSimilarity.HasValue)
                .Select(e => e.MatchSimilarity.Value)
                .DefaultIfEmpty(0)
                .Average();

            if (avgSimilarity < 1.0 && avgSimilarity > 0)
            {
                summary.AppendLine($"\nAverage match similarity: {avgSimilarity:P0}");
            }

            return summary.ToString();
        }
    }

    public class ApplyEditResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string OriginalContent { get; set; }
        public string ModifiedContent { get; set; }
        public List<AppliedEdit> AppliedEdits { get; set; }
    }

    public class AppliedEdit
    {
        public CodeEdit Edit { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Type { get; set; }
        public double? MatchSimilarity { get; set; }
        public string MatchedText { get; set; }
    }

    public class TextMatch
    {
        public int Position { get; set; }
        public int Length { get; set; }
        public double Similarity { get; set; }
        public string MatchedText { get; set; }
    }
}
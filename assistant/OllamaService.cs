using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

public class OllamaService
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl = "http://localhost:11434";
    private readonly string _model = "qwen3-coder";
    private readonly JsonSerializerOptions _jsonOptions;
    private System.Timers.Timer _updateTimer;
    private string _pendingContent;
    private readonly object _contentLock = new object();

    public OllamaService()
    {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(10);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task<bool> CheckConnection()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/tags");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // Improved streaming method with better parsing and smoother updates
    public async Task StreamCodeCompletion(
        string userPrompt,
        FileContext primaryFile,
        List<FileContext> contextFiles,
        Action<string> onToken,
        Action<string> onIntro,
        Action<string> onSummary,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var systemPrompt = GetStreamingSystemPrompt();
            var userContent = BuildStreamingUserPrompt(userPrompt, primaryFile, contextFiles);

            var requestBody = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userContent }
                },
                stream = true,
                options = new
                {
                    temperature = 0.1,
                    top_p = 0.9,
                    num_predict = 16000,
                    seed = 42
                }
            };

            var json = JsonSerializer.Serialize(requestBody, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
            {
                Content = content
            };

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            // Setup timer for smooth updates (every 50ms)
            _updateTimer = new System.Timers.Timer(50);
            bool updatePending = false;
            string lastUpdate = "";

            _updateTimer.Elapsed += (sender, e) =>
            {
                lock (_contentLock)
                {
                    if (!string.IsNullOrEmpty(_pendingContent) && _pendingContent != lastUpdate)
                    {
                        onToken?.Invoke(_pendingContent);
                        lastUpdate = _pendingContent;
                    }
                }
            };

            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var reader = new StreamReader(stream))
            {
                var fullBuffer = new StringBuilder();
                var codeBuffer = new StringBuilder();
                var currentSection = StreamSection.Intro;
                bool inCodeBlock = false;
                int codeBlockCount = 0;
                string introText = "";
                string summaryText = "";

                _updateTimer.Start();

                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var streamResponse = JsonSerializer.Deserialize<OllamaStreamResponse>(line, _jsonOptions);
                        if (streamResponse?.Message?.Content != null)
                        {
                            var token = streamResponse.Message.Content;
                            fullBuffer.Append(token);

                            // Process the full buffer to detect sections
                            var fullText = fullBuffer.ToString();

                            // Parse and handle the response more robustly
                            ParseStreamContent(
                                fullText,
                                ref currentSection,
                                ref inCodeBlock,
                                ref codeBlockCount,
                                ref introText,
                                ref summaryText,
                                codeBuffer,
                                onIntro);

                            // Update pending content for smooth streaming
                            if (currentSection == StreamSection.Code && inCodeBlock)
                            {
                                lock (_contentLock)
                                {
                                    _pendingContent = CleanCodeContent(codeBuffer.ToString());
                                }
                            }
                        }

                        if (streamResponse?.Done == true)
                        {
                            // Ensure final update
                            _updateTimer.Stop();

                            // Final update with complete content
                            if (codeBuffer.Length > 0)
                            {
                                var finalCode = CleanCodeContent(codeBuffer.ToString());
                                onToken?.Invoke(finalCode);
                            }

                            // Extract and send summary if present
                            if (!string.IsNullOrEmpty(summaryText))
                            {
                                onSummary?.Invoke(summaryText.Trim());
                            }
                            else if (currentSection == StreamSection.Summary)
                            {
                                // Try to extract summary from the end of fullBuffer
                                var fullText = fullBuffer.ToString();
                                var lastCodeBlock = fullText.LastIndexOf("```");
                                if (lastCodeBlock >= 0)
                                {
                                    var possibleSummary = fullText.Substring(lastCodeBlock + 3).Trim();
                                    if (!string.IsNullOrEmpty(possibleSummary))
                                    {
                                        onSummary?.Invoke(possibleSummary);
                                    }
                                }
                            }
                            break;
                        }
                    }
                    catch (JsonException)
                    {
                        // Skip malformed JSON lines
                        continue;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Streaming error: {ex.Message}", ex);
        }
        finally
        {
            _updateTimer?.Stop();
            _updateTimer?.Dispose();
            _pendingContent = null;
        }
    }

    private void ParseStreamContent(
        string fullText,
        ref StreamSection currentSection,
        ref bool inCodeBlock,
        ref int codeBlockCount,
        ref string introText,
        ref string summaryText,
        StringBuilder codeBuffer,
        Action<string> onIntro)
    {
        // Split by code blocks more reliably
        var codeBlockPattern = @"```(?:[a-zA-Z]+\s*)?"; // Matches ``` with optional language identifier
        var matches = Regex.Matches(fullText, codeBlockPattern);

        if (matches.Count == 0)
        {
            // No code blocks yet, still in intro
            if (currentSection == StreamSection.Intro)
            {
                introText = fullText.Trim();
            }
            return;
        }

        // We have at least one code block marker
        if (matches.Count >= 1 && currentSection == StreamSection.Intro)
        {
            // Extract intro text (everything before first ```)
            introText = fullText.Substring(0, matches[0].Index).Trim();
            if (!string.IsNullOrEmpty(introText))
            {
                onIntro?.Invoke(introText);
                currentSection = StreamSection.Code;
                inCodeBlock = true;
            }

            // Start capturing code after the first ```
            var codeStart = matches[0].Index + matches[0].Length;

            if (matches.Count >= 2)
            {
                // We have a closing ``` - extract code between them
                var codeEnd = matches[1].Index;
                var code = fullText.Substring(codeStart, codeEnd - codeStart);
                codeBuffer.Clear();
                codeBuffer.Append(code);

                // If there's text after the closing ```, it's the summary
                if (matches[1].Index + 3 < fullText.Length)
                {
                    summaryText = fullText.Substring(matches[1].Index + 3).Trim();
                    currentSection = StreamSection.Summary;
                    inCodeBlock = false;
                }
            }
            else
            {
                // Code block not closed yet, capture everything after opening ```
                if (codeStart < fullText.Length)
                {
                    var code = fullText.Substring(codeStart);
                    codeBuffer.Clear();
                    codeBuffer.Append(code);
                }
            }
        }
        else if (currentSection == StreamSection.Code)
        {
            // We're already in code section, update the buffer
            if (matches.Count >= 2)
            {
                // Code block is complete
                var codeStart = matches[0].Index + matches[0].Length;
                var codeEnd = matches[1].Index;
                var code = fullText.Substring(codeStart, codeEnd - codeStart);
                codeBuffer.Clear();
                codeBuffer.Append(code);

                // Extract summary
                if (matches[1].Index + 3 < fullText.Length)
                {
                    summaryText = fullText.Substring(matches[1].Index + 3).Trim();
                    currentSection = StreamSection.Summary;
                    inCodeBlock = false;
                }
            }
            else
            {
                // Still in open code block
                var codeStart = matches[0].Index + matches[0].Length;
                if (codeStart < fullText.Length)
                {
                    var code = fullText.Substring(codeStart);
                    codeBuffer.Clear();
                    codeBuffer.Append(code);
                }
            }
        }
    }

    private string CleanCodeContent(string code)
    {
        if (string.IsNullOrEmpty(code))
            return code;

        // Remove any leading language identifiers (in case they slipped through)
        var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        if (lines.Length > 0)
        {
            var firstLine = lines[0].Trim().ToLower();
            // Check if first line is just a language identifier
            var commonLangs = new[] { "csharp", "cs", "cshtml", "html", "javascript", "js", "typescript", "ts",
                                      "json", "xml", "sql", "css", "python", "java", "cpp", "c++" };

            if (commonLangs.Contains(firstLine))
            {
                // Skip the first line
                lines = lines.Skip(1).ToArray();
                code = string.Join(Environment.NewLine, lines);
            }
        }

        // Trim any trailing ``` that might have been included
        code = Regex.Replace(code, @"```\s*$", "");

        return code;
    }

    private string GetStreamingSystemPrompt()
    {
        return @"You are a code completion assistant. You will receive a file to modify and context files for reference.

CRITICAL INSTRUCTIONS:
1. Output format must be EXACTLY:
   - Brief intro (1-2 sentences explaining what you'll do)
   - Complete code file wrapped in ``` markers
   - Brief summary (1-2 sentences of what was changed)

2. The code block must contain the ENTIRE updated file
3. Do not include language specifier after ``` (no ```csharp, just ```)
4. Preserve all original formatting and structure except where changes are needed
5. Apply ALL requested changes accurately
6. Make sure to output the COMPLETE file, do not truncate

EXAMPLE OUTPUT:
I'll add the new validation method to the Product model.
```
[COMPLETE FILE CONTENT HERE - ENTIRE FILE FROM START TO END]
```
Added validation method and updated the constructor to use it.

IMPORTANT: Output ONLY in this format. The code block must contain the COMPLETE file.";
    }

    private string BuildStreamingUserPrompt(string userPrompt, FileContext primaryFile, List<FileContext> contextFiles)
    {
        var prompt = new StringBuilder();

        // Add primary file
        prompt.AppendLine($"PRIMARY FILE TO MODIFY: {primaryFile.FileName}");
        prompt.AppendLine("Current content:");
        prompt.AppendLine("```");
        prompt.AppendLine(primaryFile.Content);
        prompt.AppendLine("```");
        prompt.AppendLine();

        // Add context files if any
        if (contextFiles != null && contextFiles.Any())
        {
            prompt.AppendLine("CONTEXT FILES (for reference only, do not modify):");
            foreach (var contextFile in contextFiles)
            {
                prompt.AppendLine($"\n--- {contextFile.FileName} ---");
                prompt.AppendLine("```");
                prompt.AppendLine(contextFile.Content);
                prompt.AppendLine("```");
            }
            prompt.AppendLine();
        }

        prompt.AppendLine($"USER REQUEST: {userPrompt}");
        prompt.AppendLine("\nPlease provide the COMPLETE updated file content. Do not truncate.");

        return prompt.ToString();
    }

    public DiffSummary GetDiffSummary(string original, string modified)
    {
        var originalLines = original.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var modifiedLines = modified.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        var summary = new DiffSummary
        {
            OriginalLineCount = originalLines.Length,
            ModifiedLineCount = modifiedLines.Length,
            LinesAdded = Math.Max(0, modifiedLines.Length - originalLines.Length),
            LinesRemoved = Math.Max(0, originalLines.Length - modifiedLines.Length)
        };

        int minLength = Math.Min(originalLines.Length, modifiedLines.Length);
        for (int i = 0; i < minLength; i++)
        {
            if (originalLines[i] != modifiedLines[i])
            {
                summary.LinesChanged++;
            }
        }

        return summary;
    }

    private enum StreamSection
    {
        Intro,
        Code,
        Summary
    }
}

// Response Models
public class OllamaStreamResponse
{
    public OllamaMessage Message { get; set; }
    public bool Done { get; set; }
}

public class FileContext
{
    public string FilePath { get; set; }
    public string FileName { get; set; }
    public string Content { get; set; }
    public string Language { get; set; }
    public bool IsPrimary { get; set; }
}

public class DiffSummary
{
    public int OriginalLineCount { get; set; }
    public int ModifiedLineCount { get; set; }
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public int LinesChanged { get; set; }
}

public class FileReplacementResult
{
    public bool Success { get; set; }
    public string Error { get; set; }
    public string UpdatedContent { get; set; }
    public string Summary { get; set; }
    public List<string> Changes { get; set; }
}

public class OllamaMessage
{
    public string Content { get; set; }
}
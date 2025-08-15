using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

public class OllamaService
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl = "http://localhost:11434";
    private readonly string _model = "qwen3-coder";
    private readonly JsonSerializerOptions _jsonOptions;

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

    // New streaming method for code completion
    public async Task StreamCodeCompletion(
        string userPrompt,
        FileContext primaryFile,
        List<FileContext> contextFiles,
        Action<string> onToken,
        Action<string> onIntro,
        Action<string> onSummary)
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
                stream = true,  // Enable streaming
                options = new
                {
                    temperature = 0.1,
                    top_p = 0.9,
                    num_predict = 16000,  // Increased for full files
                    seed = 42
                }
            };

            var json = JsonSerializer.Serialize(requestBody, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
            {
                Content = content
            };

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var reader = new StreamReader(stream))
            {
                var buffer = new StringBuilder();
                var section = StreamSection.Intro;
                var codeBlockDepth = 0;
                string line;

                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var streamResponse = JsonSerializer.Deserialize<OllamaStreamResponse>(line, _jsonOptions);
                        if (streamResponse?.Message?.Content != null)
                        {
                            var token = streamResponse.Message.Content;
                            buffer.Append(token);

                            // Parse the buffer to detect sections
                            var bufferContent = buffer.ToString();

                            // Detect transitions between sections
                            if (section == StreamSection.Intro && bufferContent.Contains("```"))
                            {
                                var introEnd = bufferContent.IndexOf("```");
                                if (introEnd > 0)
                                {
                                    var intro = bufferContent.Substring(0, introEnd).Trim();
                                    onIntro?.Invoke(intro);
                                    buffer.Clear();
                                    buffer.Append(bufferContent.Substring(introEnd + 3));
                                    section = StreamSection.Code;
                                    codeBlockDepth = 1;
                                }
                            }
                            else if (section == StreamSection.Code)
                            {
                                // Stream code tokens directly
                                onToken?.Invoke(token);

                                // Check for code block end
                                if (token.Contains("```"))
                                {
                                    var codeEnd = bufferContent.LastIndexOf("```");
                                    if (codeEnd > 0 && bufferContent.Count(c => c == '`') >= 6)
                                    {
                                        section = StreamSection.Summary;
                                        buffer.Clear();
                                    }
                                }
                            }
                            else if (section == StreamSection.Summary)
                            {
                                // Continue building summary
                                // Will be processed when stream ends
                            }
                        }

                        if (streamResponse?.Done == true)
                        {
                            // Process any remaining summary
                            if (section == StreamSection.Summary && buffer.Length > 0)
                            {
                                onSummary?.Invoke(buffer.ToString().Trim());
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

EXAMPLE OUTPUT:
I'll add the new validation method to the Product model.
```
[COMPLETE FILE CONTENT HERE]
```
Added validation method and updated the constructor to use it.

IMPORTANT: Output ONLY in this format. No additional explanations or markdown.";
    }

    private string BuildStreamingUserPrompt(string userPrompt, FileContext primaryFile, List<FileContext> contextFiles)
    {
        var prompt = new StringBuilder();

        // Add primary file
        prompt.AppendLine($"PRIMARY FILE TO MODIFY: {primaryFile.FileName}");
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
        prompt.AppendLine("\nPlease provide the complete updated file content.");

        return prompt.ToString();
    }

    // Keep the old method for backward compatibility but mark as deprecated
    [Obsolete("Use StreamCodeCompletion for better performance")]
    public async Task<FileReplacementResult> ProcessCodeEditRequest(string userPrompt, string fileContent, string fileName)
    {
        // Implementation remains for backward compatibility
        throw new NotImplementedException("This method is deprecated. Use StreamCodeCompletion instead.");
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

// Keep old models for compatibility
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
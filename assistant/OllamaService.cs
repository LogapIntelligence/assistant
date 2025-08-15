using System;
using System.Collections.Generic;
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
        _httpClient.Timeout = TimeSpan.FromMinutes(5);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
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

    public async Task<FileReplacementResult> ProcessCodeEditRequest(string userPrompt, string fileContent, string fileName)
    {
        try
        {
            var requestBody = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = GetSystemPrompt() },
                    new { role = "user", content = BuildUserPrompt(userPrompt, fileContent, fileName) }
                },
                stream = false,
                format = GetResponseSchema(),
                options = new
                {
                    temperature = 0.1,
                    top_p = 0.9,
                    num_predict = 8000,  // Increased for full file content
                    seed = 42
                }
            };

            var json = JsonSerializer.Serialize(requestBody, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_baseUrl}/api/chat", content);

            if (!response.IsSuccessStatusCode)
            {
                return new FileReplacementResult
                {
                    Success = false,
                    Error = $"API Error: {response.StatusCode}"
                };
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            var ollamaResponse = JsonSerializer.Deserialize<OllamaChatResponse>(responseJson, _jsonOptions);

            if (ollamaResponse?.Message?.Content != null)
            {
                var replacementResponse = JsonSerializer.Deserialize<CodeReplacementResponse>(
                    ollamaResponse.Message.Content, _jsonOptions);

                // Validate and clean the response
                replacementResponse = PostProcessResponse(replacementResponse);

                return new FileReplacementResult
                {
                    Success = true,
                    UpdatedContent = replacementResponse.UpdatedFile,
                    Summary = replacementResponse.Summary,
                    Changes = replacementResponse.Changes
                };
            }

            return new FileReplacementResult
            {
                Success = false,
                Error = "No response from model"
            };
        }
        catch (JsonException jsonEx)
        {
            return new FileReplacementResult
            {
                Success = false,
                Error = $"Model returned invalid JSON: {jsonEx.Message}"
            };
        }
        catch (Exception ex)
        {
            return new FileReplacementResult
            {
                Success = false,
                Error = $"Unexpected error: {ex.Message}"
            };
        }
    }

    private CodeReplacementResponse PostProcessResponse(CodeReplacementResponse response)
    {
        // Clean up any potential formatting issues
        if (!string.IsNullOrEmpty(response.UpdatedFile))
        {
            // Remove any potential markdown code blocks if present
            response.UpdatedFile = response.UpdatedFile.Trim();

            // Remove ```csharp or ``` markers if they exist
            if (response.UpdatedFile.StartsWith("```"))
            {
                var lines = response.UpdatedFile.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                if (lines.Length > 2)
                {
                    // Remove first and last line if they are code block markers
                    var firstLine = lines[0].Trim();
                    var lastLine = lines[lines.Length - 1].Trim();

                    if (firstLine.StartsWith("```") && lastLine == "```")
                    {
                        response.UpdatedFile = string.Join(Environment.NewLine,
                            lines.Skip(1).Take(lines.Length - 2));
                    }
                }
            }
        }

        // Ensure changes list is not null
        if (response.Changes == null)
        {
            response.Changes = new List<string>();
        }

        return response;
    }

    private string GetSystemPrompt()
    {
        return @"You are a code editing assistant. You will receive a file and a modification request.
Your task is to return the COMPLETE updated file content with all requested changes applied.

CRITICAL INSTRUCTIONS:
1. Return ONLY valid JSON in the specified format
2. The 'updatedFile' field must contain the ENTIRE file content after modifications
3. Do not use markdown code blocks in the updatedFile field
4. Preserve all original formatting, indentation, and structure except where changes are needed
5. Apply ALL requested changes accurately
6. Include a clear summary of what was changed
7. List specific changes made in the 'changes' array

RESPONSE FORMAT:
{
  ""updatedFile"": ""<COMPLETE file content with all changes applied>"",
  ""summary"": ""<Brief summary of all changes made>"",
  ""changes"": [
    ""<Specific change 1>"",
    ""<Specific change 2>"",
    ...
  ]
}

IMPORTANT:
- The updatedFile field contains the ENTIRE file, not just the changed parts
- Maintain exact indentation and formatting from the original file
- Do not add explanatory comments unless specifically requested
- Ensure the code remains syntactically correct after changes";
    }

    private string BuildUserPrompt(string userPrompt, string fileContent, string fileName)
    {
        var prompt = new StringBuilder();

        prompt.AppendLine($"File: {fileName}");
        prompt.AppendLine("\nCurrent file content:");
        prompt.AppendLine("```");
        prompt.AppendLine(fileContent);
        prompt.AppendLine("```");
        prompt.AppendLine($"\nRequested modification: {userPrompt}");
        prompt.AppendLine("\nPlease return the COMPLETE updated file with all requested changes applied.");
        prompt.AppendLine("Remember to return ONLY valid JSON with the entire file content in the 'updatedFile' field.");

        return prompt.ToString();
    }

    private object GetResponseSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                updatedFile = new
                {
                    type = "string",
                    description = "The complete file content with all modifications applied"
                },
                summary = new
                {
                    type = "string",
                    description = "Brief summary of all changes made"
                },
                changes = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "List of specific changes made to the file"
                }
            },
            required = new[] { "updatedFile", "summary", "changes" }
        };
    }

    // Method to compare original and modified content to generate a diff summary
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

        // Simple diff detection (could be enhanced with more sophisticated algorithm)
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
}

// Response Models
public class CodeReplacementResponse
{
    [JsonPropertyName("updatedFile")]
    public string UpdatedFile { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; }

    [JsonPropertyName("changes")]
    public List<string> Changes { get; set; } = new List<string>();
}

public class FileReplacementResult
{
    public bool Success { get; set; }
    public string Error { get; set; }
    public string UpdatedContent { get; set; }
    public string Summary { get; set; }
    public List<string> Changes { get; set; }
}

public class DiffSummary
{
    public int OriginalLineCount { get; set; }
    public int ModifiedLineCount { get; set; }
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public int LinesChanged { get; set; }
}

// Internal Models (unchanged)
internal class OllamaChatResponse
{
    public OllamaMessage Message { get; set; }
}

internal class OllamaMessage
{
    public string Content { get; set; }
}

// Legacy models - kept for backward compatibility but deprecated
[Obsolete("Use FileReplacementResult instead")]
public class EditResult
{
    public bool Success { get; set; }
    public string Error { get; set; }
    public List<CodeEdit> Edits { get; set; }
    public string Summary { get; set; }
}

[Obsolete("No longer used in complete replacement mode")]
public class CodeEdit
{
    [JsonPropertyName("source")]
    public string Source { get; set; }

    [JsonPropertyName("new")]
    public string New { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; }
}

[Obsolete("Use CodeReplacementResponse instead")]
public class CodeEditResponse
{
    [JsonPropertyName("edits")]
    public List<CodeEdit> Edits { get; set; } = new List<CodeEdit>();

    [JsonPropertyName("summary")]
    public string Summary { get; set; }
}
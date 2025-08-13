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
    private readonly string _model = "phi4-mini";
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

    public async Task<EditResult> ProcessCodeEditRequest(string userPrompt, string fileContent, string fileName)
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
                    num_predict = 3000,
                    seed = 42
                }
            };

            var json = JsonSerializer.Serialize(requestBody, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_baseUrl}/api/chat", content);

            if (!response.IsSuccessStatusCode)
            {
                return new EditResult
                {
                    Success = false,
                    Error = $"API Error: {response.StatusCode}"
                };
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            var ollamaResponse = JsonSerializer.Deserialize<OllamaChatResponse>(responseJson, _jsonOptions);

            if (ollamaResponse?.Message?.Content != null)
            {
                var editResponse = JsonSerializer.Deserialize<CodeEditResponse>(
                    ollamaResponse.Message.Content, _jsonOptions);

                // Post-process to validate and fix common issues
                editResponse = PostProcessResponse(editResponse, fileContent);

                return new EditResult
                {
                    Success = true,
                    Edits = editResponse.Edits,
                    Summary = editResponse.Summary
                };
            }

            return new EditResult
            {
                Success = false,
                Error = "No response from model"
            };
        }
        catch (JsonException jsonEx)
        {
            return new EditResult
            {
                Success = false,
                Error = $"Model returned invalid JSON: {jsonEx.Message}"
            };
        }
        catch (Exception ex)
        {
            return new EditResult
            {
                Success = false,
                Error = $"Unexpected error: {ex.Message}"
            };
        }
    }

    private CodeEditResponse PostProcessResponse(CodeEditResponse response, string fileContent)
    {
        foreach (var edit in response.Edits)
        {
            // Fix common issue: Append operations including source in new field
            if (edit.Type == "Append" && !string.IsNullOrEmpty(edit.New))
            {
                // Remove source line if it appears at the start of new field
                if (edit.New.TrimStart().StartsWith(edit.Source.Trim()))
                {
                    var sourceLength = edit.Source.Trim().Length;
                    var startIndex = edit.New.IndexOf(edit.Source.Trim());
                    if (startIndex >= 0)
                    {
                        edit.New = edit.New.Substring(startIndex + sourceLength).TrimStart('\n', '\r');
                    }
                }
            }

            // Ensure Remove operations have empty new field
            if (edit.Type == "Remove")
            {
                edit.New = "";
            }

            // Validate source exists in file
            if (!fileContent.Contains(edit.Source))
            {
                // Log warning or handle as needed
                Console.WriteLine($"Warning: Source not found in file for edit: {edit.Reason}");
            }
        }

        return response;
    }

    private string GetSystemPrompt()
    {
        return @"You are a code editing assistant. Respond with ONLY valid JSON.

RESPONSE FORMAT:
{
  ""edits"": [
    {
      ""source"": ""<EXACT text to match>"",
      ""new"": ""<new content - see rules below>"",
      ""type"": ""<Replace|Append|Remove>"",
      ""reason"": ""<brief explanation>""
    }
  ],
  ""summary"": ""<overall summary>""
}

EDIT TYPE RULES - FOLLOW EXACTLY:

1. **Replace**: Replaces source with new content
   - source: The exact text to replace
   - new: The complete replacement text
   
2. **Append**: Adds new content AFTER source (source stays unchanged)
   - source: The single line after which to append
   - new: ONLY the new content to add (DO NOT include the source line)
   
3. **Remove**: Deletes the source
   - source: The exact text to remove
   - new: MUST be empty string """"

CRITICAL FOR APPEND:
The 'new' field must contain ONLY the content being added, NOT the source line.
✓ CORRECT: source: ""<h1>Title</h1>"", new: ""<button>Click</button>""
✗ WRONG: source: ""<h1>Title</h1>"", new: ""<h1>Title</h1>\n<button>Click</button>""

SOURCE SELECTION STRATEGY:
- APPEND: Use single line as source (the line you're adding after)
- REPLACE: Use entire logical block when modifying internals
- REMOVE: Remove complete logical units

MATCHING RULES:
- Copy source text EXACTLY including all spaces and indentation
- Never paraphrase or approximate
- Match quotes and special characters precisely";
    }

    private string BuildUserPrompt(string userPrompt, string fileContent, string fileName)
    {
        var prompt = new StringBuilder();

        prompt.AppendLine($"File: {fileName}");
        prompt.AppendLine("\n```");
        prompt.AppendLine(fileContent);
        prompt.AppendLine("```\n");
        prompt.AppendLine($"Request: {userPrompt}");
        prompt.AppendLine("\nREMINDER - Edit Type Rules:");
        prompt.AppendLine("• Replace: new = complete replacement text");
        prompt.AppendLine("• Append: new = ONLY the added content (NOT including source line)");
        prompt.AppendLine("• Remove: new = empty string \"\"");
        prompt.AppendLine("\nProvide JSON with exact source matches from the code above.");

        return prompt.ToString();
    }

    private object GetResponseSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                edits = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            source = new { type = "string", description = "Exact text to match from the file" },
                            @new = new { type = "string", description = "For Replace: replacement text, For Append: ONLY new content to add, For Remove: empty string" },
                            type = new
                            {
                                type = "string",
                                @enum = new[] { "Replace", "Append", "Remove" },
                                description = "Edit operation type"
                            },
                            reason = new { type = "string", description = "Brief explanation of the change" }
                        },
                        required = new[] { "source", "new", "type", "reason" }
                    },
                    minItems = 1
                },
                summary = new { type = "string", description = "Overall summary of all changes" }
            },
            required = new[] { "edits", "summary" }
        };
    }

    // Additional helper method to validate edits before applying
    public bool ValidateEdit(CodeEdit edit, string fileContent)
    {
        // Check if source exists in file
        if (!fileContent.Contains(edit.Source))
        {
            return false;
        }

        // Validate based on type
        switch (edit.Type)
        {
            case "Append":
                // New field should not contain the source
                if (!string.IsNullOrEmpty(edit.New) && edit.New.Contains(edit.Source))
                {
                    Console.WriteLine("Warning: Append operation contains source in new field");
                    return false;
                }
                break;

            case "Remove":
                // New field must be empty
                if (!string.IsNullOrEmpty(edit.New))
                {
                    Console.WriteLine("Warning: Remove operation has non-empty new field");
                    return false;
                }
                break;

            case "Replace":
                // New field should have content (unless intentionally replacing with empty)
                // This is valid, just log if empty
                if (string.IsNullOrEmpty(edit.New))
                {
                    Console.WriteLine("Info: Replace operation with empty new field");
                }
                break;

            default:
                Console.WriteLine($"Warning: Unknown edit type: {edit.Type}");
                return false;
        }

        return true;
    }

    // Method to apply edits to file content
    public string ApplyEdits(string fileContent, List<CodeEdit> edits)
    {
        var result = fileContent;

        // Sort edits by position in file (reverse order to maintain positions)
        var sortedEdits = edits.OrderByDescending(e => fileContent.IndexOf(e.Source)).ToList();

        foreach (var edit in sortedEdits)
        {
            if (!ValidateEdit(edit, result))
            {
                Console.WriteLine($"Skipping invalid edit: {edit.Reason}");
                continue;
            }

            switch (edit.Type)
            {
                case "Replace":
                    result = result.Replace(edit.Source, edit.New);
                    break;

                case "Append":
                    var index = result.IndexOf(edit.Source);
                    if (index >= 0)
                    {
                        var insertPosition = index + edit.Source.Length;
                        // Add newline if not present
                        var newContent = edit.New.StartsWith("\n") ? edit.New : "\n" + edit.New;
                        result = result.Insert(insertPosition, newContent);
                    }
                    break;

                case "Remove":
                    result = result.Replace(edit.Source, "");
                    break;
            }
        }

        return result;
    }
}

// Response Models
public class CodeEditResponse
{
    [JsonPropertyName("edits")]
    public List<CodeEdit> Edits { get; set; } = new List<CodeEdit>();

    [JsonPropertyName("summary")]
    public string Summary { get; set; }
}

public class CodeEdit
{
    [JsonPropertyName("source")]
    public string Source { get; set; }

    [JsonPropertyName("new")]
    public string New { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } // Replace, Append, Remove

    [JsonPropertyName("reason")]
    public string Reason { get; set; }
}

// Result Models
public class EditResult
{
    public bool Success { get; set; }
    public string Error { get; set; }
    public List<CodeEdit> Edits { get; set; }
    public string Summary { get; set; }
}

// Internal Models
internal class OllamaChatResponse
{
    public OllamaMessage Message { get; set; }
}

internal class OllamaMessage
{
    public string Content { get; set; }
}
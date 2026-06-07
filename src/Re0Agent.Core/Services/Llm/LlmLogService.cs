using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Re0Agent.Core.Services.Llm;

public sealed class LlmLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string AgentName { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string UserPrompt { get; set; } = string.Empty;
    public string ResponseContent { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public double LatencyMs { get; set; }
}

public sealed class LlmLogService
{
    private readonly ConcurrentQueue<LlmLogEntry> _logs = new();
    
    public IReadOnlyList<LlmLogEntry> Logs => _logs.Reverse().ToList(); // Show latest logs first

    // Manual Form-Filling background operation states
    public bool IsManualFormFilling { get; set; }
    public string? ManualFormFillingStatus { get; set; }
    public string? ManualFormFillingError { get; set; }

    public void Log(LlmLogEntry entry)
    {
        _logs.Enqueue(entry);
        while (_logs.Count > 150) // Cap memory usage at 150 logs
        {
            _logs.TryDequeue(out _);
        }
    }

    public void Clear()
    {
        while (_logs.TryDequeue(out _)) { }
    }
}

namespace LsMonitoring.Core.Models;

public sealed record NodeInfo(int NodeId, string? Model = null, string? Label = null);

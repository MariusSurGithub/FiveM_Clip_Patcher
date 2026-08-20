namespace FiveMClipPatcher.Models;

public enum PatchMode
{
    Null,
    Placeholder
}

public sealed record PatchOptions
{
    public required string InputPath { get; init; }
    public IReadOnlyList<string>? SelectedFiles { get; init; }
    public required IReadOnlyList<string> Patterns { get; init; }
    public bool Recursive { get; init; }
    public bool CaseInsensitive { get; init; }
    public PatchMode Mode { get; init; } = PatchMode.Null;
    public string Placeholder { get; init; } = "REMOVED";
    public IReadOnlyList<string> Extensions { get; init; } = [".clip"];
    public required string BackupBasePath { get; init; }
    public bool DryRun { get; init; }
}

public sealed class PatchHit
{
    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public required string MatchedText { get; init; }
    public required string Pattern { get; init; }
    public required long Offset { get; init; }
    public required int Length { get; init; }
}

public sealed class PatchRunResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public int FilesProcessed { get; init; }
    public int FilesPatched { get; init; }
    public int PatternsPatched { get; init; }
    public string? RunDirectory { get; init; }
    public IReadOnlyList<PatchHit> Hits { get; init; } = [];
}

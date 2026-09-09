using DevClean.Models;

namespace DevClean.AI;

public interface IAiSafetyAnalyzer
{
    Task<SafetyAnalysis> AnalyzeAsync(
        FileItem file,
        CancellationToken cancellationToken = default);
}
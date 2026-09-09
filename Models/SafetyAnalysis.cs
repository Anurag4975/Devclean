namespace DevClean.Models;

public class SafetyAnalysis
{
    public SafetyLevel Level { get; set; }

    public int Confidence { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string Explanation { get; set; } = string.Empty;

    public string Risk { get; set; } = string.Empty;

    public List<string> Evidence { get; set; } = [];
}
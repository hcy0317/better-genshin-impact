namespace BetterGenshinImpact.GameTask.AutoFight.Model;

internal enum ESkillReadiness { Unknown, Cooldown, Ready }

// Values only: no borrowed image, Mat, predictor, UI state or lifetime ownership escapes the frame.
internal readonly record struct ESkillClassification(string? Label, double Confidence, string? Code, ESkillReadiness State);
internal readonly record struct ESkillObservation(double Cooldown, bool? Ready, ESkillClassification? Classification = null);

namespace ApexLab.Domain.Laps;

public enum LapBoundaryCompleteness
{
    Complete = 0,
    LeadingPartial = 1,
    TrailingPartial = 2,
    Incoherent = 3,
}

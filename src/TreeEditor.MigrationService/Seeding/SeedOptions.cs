namespace TreeEditor.MigrationService.Seeding;

/// <summary>Sample tree settings. The AppHost sets <c>Seed__Size</c> from its <c>seed-size</c> parameter.</summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>Number of elements in the sample tree.</summary>
    public int Size { get; set; } = SampleTree.DefaultSize;
}

namespace TreeEditor.MigrationService.Seeding;

/// <summary>One generated element, written to <c>seed_nodes</c>.</summary>
public sealed record SeedRow(Guid Id, Guid? ParentId, Guid[] Ancestors, string Value);

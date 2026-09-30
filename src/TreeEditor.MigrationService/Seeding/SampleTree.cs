using TreeEditor.Domain;

namespace TreeEditor.MigrationService.Seeding;

/// <summary>
/// Small fixed sample tree: 3 roots, up to 5 levels, fixed GUID v7 ids.
/// Placeholder until the generated seed replaces it.
/// </summary>
public static class SampleTree
{
    public static readonly Guid BooksId = Id(0x01);
    public static readonly Guid ElectronicsId = Id(0x02);
    public static readonly Guid GardenId = Id(0x03);

    /// <summary>Roots in listing order (lowercase value, then id).</summary>
    public static IReadOnlyList<Guid> RootIds { get; } = [BooksId, ElectronicsId, GardenId];

    public static IReadOnlyList<SeedRow> Rows { get; } = Build();

    private static List<SeedRow> Build()
    {
        var rows = new List<SeedRow>();

        SeedRow Root(Guid id, string value)
        {
            var row = new SeedRow(id, null, Ancestry.ForRoot(id), value);
            rows.Add(row);
            return row;
        }

        SeedRow Child(SeedRow parent, int n, string value)
        {
            var id = Id(n);
            var row = new SeedRow(id, parent.Id, Ancestry.ForChild(parent.Ancestors, id), value);
            rows.Add(row);
            return row;
        }

        var electronics = Root(ElectronicsId, "Electronics");
        var computers = Child(electronics, 0x10, "Computers");
        var laptops = Child(computers, 0x11, "Laptops");
        var gaming = Child(laptops, 0x12, "Gaming laptops");
        Child(gaming, 0x13, "17-inch");
        Child(laptops, 0x14, "ultrabooks");
        Child(computers, 0x15, "Desktops");
        var phones = Child(electronics, 0x16, "phones");
        Child(phones, 0x17, "Android");
        Child(phones, 0x18, "iPhone");

        var books = Root(BooksId, "Books");
        var fiction = Child(books, 0x20, "Fiction");
        var fantasy = Child(fiction, 0x21, "Fantasy");
        Child(fantasy, 0x22, "Epic fantasy");
        Child(fiction, 0x23, "Science fiction");
        Child(books, 0x24, "Non-fiction");

        Root(GardenId, "Garden");

        return rows;
    }

    private static Guid Id(int n) => Guid.Parse($"0199a000-0000-7000-8000-{n:x12}");
}

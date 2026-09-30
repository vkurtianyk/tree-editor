using Bogus;
using TreeEditor.Domain;

namespace TreeEditor.MigrationService.Seeding;

/// <summary>
/// Generates the deterministic sample tree: the same ids and values on every run and machine.
/// <para>
/// Shape: <see cref="RootCount"/> roots; a chain <see cref="ChainDepth"/> levels deep; <see cref="WideCount"/> wide
/// elements with 5k–10k children; everything else at most <see cref="MaxDepth"/> levels deep with 0–20 children.
/// </para>
/// <para>
/// The fixed part (roots, their children, the chain, the wide elements and their children) is generated first,
/// so its ids and values are the same at every size; the size only decides how far the rest grows.
/// </para>
/// </summary>
public static class SampleTree
{
    public const int DefaultSize = 100_000;

    /// <summary>Leaves room for the rest of the tree next to the fixed part (at most about 30k elements).</summary>
    public const int MinSize = 50_000;

    public const int MaxSize = 1_000_000;

    public const int RootCount = 5;

    /// <summary>Levels of the deep chain, counting its root as level 1.</summary>
    public const int ChainDepth = 200;

    /// <summary>Depth limit for every element that isn't on the deep chain.</summary>
    public const int MaxDepth = 12;

    public const int WideCount = 3;

    public const int MinWideChildren = 5_000;

    public const int MaxWideChildren = 10_000;

    public const int MaxChildren = 20;

    public const int MaxValueLength = 255;

    private const int RandomSeed = 20_260_930;

    private const int MinRootChildren = 10;

    // Base timestamp of the generated GUID v7 ids; row i gets IdEpoch + i ms.
    private static readonly DateTimeOffset IdEpoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<SeedRow> Generate(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, MinSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, MaxSize);
        return new Generator(size).Run();
    }

    /// <summary>Not thread-safe (one Bogus randomizer); one instance per generated tree.</summary>
    private sealed class Generator(int size)
    {
        private readonly Faker faker = new() { Random = new Randomizer(RandomSeed) };
        private readonly List<SeedRow> rows = new(size);

        public List<SeedRow> Run()
        {
            // Fixed part: roots and their children. Root 0 holds the deep chain, roots 1..WideCount a wide element each.
            var roots = AddChildren(null, RootCount, DistinctDepartment());
            var frontiers = new List<SeedRow>[RootCount];
            for (var r = 0; r < RootCount; r++)
            {
                var children = AddChildren(roots[r], faker.Random.Int(MinRootChildren, MaxChildren), faker.Commerce.ProductName);
                var special = r <= WideCount ? 1 : 0;
                frontiers[r] = children.Skip(special).ToList();
                if (r == 0)
                {
                    AddChain(children[0]);
                }
                else if (special == 1)
                {
                    // Leaves only, so the wide element shows as one long, flat list.
                    AddChildren(children[0], faker.Random.Int(MinWideChildren, MaxWideChildren), PersonName);
                }
            }

            // The rest, grown under the roots in equal shares until the tree reaches its size.
            var remaining = size - rows.Count;
            for (var r = 0; r < RootCount; r++)
            {
                Grow(frontiers[r], remaining / RootCount + (r < remaining % RootCount ? 1 : 0));
            }

            return rows;
        }

        private void AddChain(SeedRow start)
        {
            var parent = start;
            for (var depth = start.Ancestors.Length + 1; depth <= ChainDepth; depth++)
            {
                var level = depth;
                parent = AddChildren(parent, 1, () => $"Level {level} {faker.Commerce.ProductName()}")[0];
            }
        }

        /// <summary>
        /// Repeatedly picks a random unexpanded element and gives it 1–20 children, so depths vary up to
        /// <see cref="MaxDepth"/>; elements never picked stay leaves.
        /// </summary>
        private void Grow(List<SeedRow> frontier, int budget)
        {
            while (budget > 0)
            {
                if (frontier.Count == 0)
                {
                    throw new InvalidOperationException($"The sample tree ran out of elements to expand with {budget} elements left.");
                }

                var pick = faker.Random.Int(0, frontier.Count - 1);
                var parent = frontier[pick];
                frontier[pick] = frontier[^1];
                frontier.RemoveAt(frontier.Count - 1);

                var children = AddChildren(parent, Math.Min(faker.Random.Int(1, MaxChildren), budget), faker.Commerce.ProductName);
                budget -= children.Count;
                if (parent.Ancestors.Length + 1 < MaxDepth)
                {
                    frontier.AddRange(children);
                }
            }
        }

        /// <summary>Adds all children of one parent at once, so sibling values are made unique in one place.</summary>
        private List<SeedRow> AddChildren(SeedRow? parent, int count, Func<string> value)
        {
            var siblingValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var children = new List<SeedRow>(count);
            for (var i = 0; i < count; i++)
            {
                var id = NextId();
                var row = new SeedRow(
                    id,
                    parent?.Id,
                    parent is null ? Ancestry.ForRoot(id) : Ancestry.ForChild(parent.Ancestors, id),
                    Unique(siblingValues, Normalize(value())));
                rows.Add(row);
                children.Add(row);
            }

            return children;
        }

        /// <summary>Redraws repeated department names, so root names read cleanly without <c> (n)</c> suffixes.</summary>
        private Func<string> DistinctDepartment()
        {
            var drawn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return () =>
            {
                string department;
                do
                {
                    department = faker.Commerce.Department(1);
                }
                while (!drawn.Add(department));

                return department;
            };
        }

        private string PersonName() => $"{faker.Name.FirstName()} {faker.Name.LastName()}";

        /// <summary>A GUID v7 whose timestamp follows the row order and whose random bits come from the seeded randomizer.</summary>
        private Guid NextId()
        {
            Span<byte> bytes = stackalloc byte[16];
            faker.Random.Bytes(16).CopyTo(bytes);
            var milliseconds = IdEpoch.AddMilliseconds(rows.Count).ToUnixTimeMilliseconds();
            for (var i = 0; i < 6; i++)
            {
                bytes[i] = (byte)(milliseconds >> (8 * (5 - i)));
            }

            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70); // version 7
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant
            return new Guid(bytes, bigEndian: true);
        }
    }

    private static string Normalize(string value)
    {
        value = value.Trim();
        return value.Length > MaxValueLength ? value[..MaxValueLength].TrimEnd() : value;
    }

    /// <summary>On a case-insensitive clash with a sibling, appends the first free <c> (n)</c>, shortening the base to fit.</summary>
    private static string Unique(HashSet<string> siblingValues, string value)
    {
        var candidate = value;
        for (var n = 1; !siblingValues.Add(candidate); n++)
        {
            var suffix = $" ({n})";
            var baseValue = value.Length + suffix.Length > MaxValueLength
                ? value[..(MaxValueLength - suffix.Length)].TrimEnd()
                : value;
            candidate = baseValue + suffix;
        }

        return candidate;
    }
}

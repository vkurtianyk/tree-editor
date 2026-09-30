using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace TreeEditor.Data;

public sealed class TreeDbContext(DbContextOptions<TreeDbContext> options) : DbContext(options)
{
    /// <summary>Aspire connection name of the database.</summary>
    public const string ConnectionName = "treedb";

    /// <summary>B-tree on <c>(parent_id, lower(value), id)</c>: children lookups and keyset order. Raw SQL in the migration.</summary>
    public const string ChildrenOrderIndex = "ix_nodes_parent_id_lower_value_id";

    /// <summary>Partial unique <c>(parent_id, lower(value)) NULLS NOT DISTINCT WHERE NOT is_deleted</c>. Raw SQL in the migration.</summary>
    public const string UniqueLiveSiblingValueIndex = "ux_nodes_parent_id_lower_value";

    public DbSet<Node> Nodes => Set<Node>();

    public DbSet<SeedNode> SeedNodes => Set<SeedNode>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // parent_id is the leading column of ChildrenOrderIndex; no separate FK index.
        configurationBuilder.Conventions.Remove<ForeignKeyIndexConvention>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Node>(node =>
        {
            node.ToTable("nodes");
            MapShape(node);
            node.Property(n => n.Version).IsRowVersion(); // xmin system column
            node.HasOne<Node>().WithMany()
                .HasForeignKey(n => n.ParentId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_nodes_parent_id");

            // Modelled (not raw SQL) so Npgsql translates Ancestors.Contains(x) to `ancestors @> ARRAY[x]`.
            node.HasIndex(n => n.Ancestors).HasMethod("gin").HasDatabaseName("ix_nodes_ancestors");
        });

        modelBuilder.Entity<SeedNode>(seed =>
        {
            seed.ToTable("seed_nodes");
            MapShape(seed);
        });
    }

    private static void MapShape<T>(EntityTypeBuilder<T> e) where T : class
    {
        e.Property<Guid>("Id").HasColumnName("id").ValueGeneratedNever();
        e.HasKey("Id").HasName($"pk_{e.Metadata.GetTableName()}");
        e.Property<Guid?>("ParentId").HasColumnName("parent_id");
        e.Property<Guid[]>("Ancestors").HasColumnName("ancestors").IsRequired();
        e.Property<string>("Value").HasColumnName("value").HasMaxLength(255).IsRequired();
        e.Property<bool>("IsDeleted").HasColumnName("is_deleted");
    }
}

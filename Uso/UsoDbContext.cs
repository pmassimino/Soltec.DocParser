using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Soltec.DocParser.Uso
{
    public sealed class UsoDbContext : DbContext
    {
        public UsoDbContext(DbContextOptions<UsoDbContext> options) : base(options)
        {
        }

        public DbSet<RegistroUso> RegistrosUso => Set<RegistroUso>();
        public DbSet<RegistroUsoIa> LlamadasIa => Set<RegistroUsoIa>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(UsoDbContext).Assembly);
        }
    }

    public sealed class RegistroUsoConfiguration : IEntityTypeConfiguration<RegistroUso>
    {
        public void Configure(EntityTypeBuilder<RegistroUso> builder)
        {
            builder.ToTable("RegistrosUso");
            builder.HasKey(r => r.Id);
            // SQLite no guarda el DateTimeKind: se vuelve a marcar como UTC al leer.
            builder.Property(r => r.FechaUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            builder.Property(r => r.NombreCliente).HasMaxLength(200);
            builder.Property(r => r.TipoArchivo).HasMaxLength(10).IsRequired();
            builder.Property(r => r.NombreArchivo).HasMaxLength(260);
            builder.Property(r => r.Resultado).HasMaxLength(50).IsRequired();
            builder.Property(r => r.ProveedorIa).HasMaxLength(30);
            builder.HasIndex(r => r.FechaUtc);
            builder.HasIndex(r => new { r.IdUsuario, r.FechaUtc });
            builder.HasMany(r => r.LlamadasIa)
                .WithOne()
                .HasForeignKey(l => l.RegistroUsoId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }

    public sealed class RegistroUsoIaConfiguration : IEntityTypeConfiguration<RegistroUsoIa>
    {
        public void Configure(EntityTypeBuilder<RegistroUsoIa> builder)
        {
            builder.ToTable("RegistrosUsoIa");
            builder.HasKey(l => l.Id);
            builder.Property(l => l.Proveedor).HasMaxLength(30).IsRequired();
            builder.Property(l => l.Modelo).HasMaxLength(100).IsRequired();
        }
    }
}

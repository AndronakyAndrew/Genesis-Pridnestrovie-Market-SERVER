using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class ProjectRoleAssignmentConfiguration : IEntityTypeConfiguration<ProjectRoleAssignment>
{
    public void Configure(EntityTypeBuilder<ProjectRoleAssignment> b)
    {
        b.ToTable("project_role_assignments", t =>
        {
            // Строка есть только у служебных ролей: «User» — это отсутствие строки.
            // Иначе появились бы два способа сказать одно и то же.
            t.HasCheckConstraint("ck_project_role_assignments_role",
                "\"Role\" IN ('Moderator', 'Admin', 'Owner')");
        });

        b.HasKey(a => a.Id);

        b.Property(a => a.ProjectKey).HasMaxLength(40).IsRequired();

        // Строкой, как и прежняя колонка users.Role: имена ролей — общий язык с Genesis Ops.
        b.Property(a => a.Role).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Одна роль на пользователя в проекте.
        b.HasIndex(a => new { a.UserId, a.ProjectKey }).IsUnique();

        // Владелец — ровно один на проект. Держит БД, а не только прикладная проверка:
        // две одновременные CLI-команды не создадут двух владельцев.
        b.HasIndex(a => a.ProjectKey)
            .IsUnique()
            .HasFilter("\"Role\" = 'Owner'")
            .HasDatabaseName("ux_project_role_assignments_single_owner");

        b.HasOne(a => a.User)
            .WithMany(u => u.ProjectRoles)
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

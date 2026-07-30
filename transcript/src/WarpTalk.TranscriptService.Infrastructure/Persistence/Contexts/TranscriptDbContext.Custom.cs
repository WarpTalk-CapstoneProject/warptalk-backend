using Microsoft.EntityFrameworkCore;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

public partial class TranscriptDbContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {

    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

/// <inheritdoc />
public class MeetingMinutesShareRepository : IMeetingMinutesShareRepository
{
    private readonly TranslationRoomDbContext _context;

    public MeetingMinutesShareRepository(TranslationRoomDbContext context)
    {
        _context = context;
    }

    public async Task<MeetingMinutesShareLink?> GetByRoomIdAsync(Guid roomId, CancellationToken ct = default)
    {
        return await _context.MeetingMinutesShareLinks
            .FirstOrDefaultAsync(link => link.TranslationRoomId == roomId, ct);
    }

    public async Task<MeetingMinutesShareLink?> GetByTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        return await _context.MeetingMinutesShareLinks
            .FirstOrDefaultAsync(link => link.Token == token, ct);
    }

    public async Task AddLinkAsync(MeetingMinutesShareLink link, CancellationToken ct = default)
    {
        await _context.MeetingMinutesShareLinks.AddAsync(link, ct);
    }

    public async Task<List<MeetingMinutesShareGrant>> GetGrantsAsync(Guid roomId, CancellationToken ct = default)
    {
        return await _context.MeetingMinutesShareGrants
            .Where(grant => grant.TranslationRoomId == roomId)
            .OrderBy(grant => grant.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> HasGrantAsync(Guid roomId, string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;

        var normalised = Normalise(email);
        return await _context.MeetingMinutesShareGrants
            .AnyAsync(grant => grant.TranslationRoomId == roomId && grant.Email == normalised, ct);
    }

    public async Task AddGrantAsync(MeetingMinutesShareGrant grant, CancellationToken ct = default)
    {
        grant.Email = Normalise(grant.Email);
        await _context.MeetingMinutesShareGrants.AddAsync(grant, ct);
    }

    public async Task RemoveGrantAsync(Guid roomId, string email, CancellationToken ct = default)
    {
        var normalised = Normalise(email);
        var existing = await _context.MeetingMinutesShareGrants
            .FirstOrDefaultAsync(grant => grant.TranslationRoomId == roomId && grant.Email == normalised, ct);

        // Silent when they were not on the list: removing somebody twice is the same outcome as
        // removing them once, and a caller retrying must not get an error for it.
        if (existing != null) _context.MeetingMinutesShareGrants.Remove(existing);
    }

    /// <summary>The one place email case is decided, so writes and lookups cannot disagree.</summary>
    private static string Normalise(string email) => email.Trim().ToLowerInvariant();
}

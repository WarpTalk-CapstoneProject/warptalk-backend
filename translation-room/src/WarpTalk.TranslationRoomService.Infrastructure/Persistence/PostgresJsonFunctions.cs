using System;

namespace WarpTalk.TranslationRoomService.Infrastructure.Persistence;

/// <summary>
/// Postgres functions this service needs in a query and that Npgsql does not surface.
///
/// WHY THIS EXISTS
///     The workspace minutes library searches a title that lives INSIDE a jsonb column. Reaching
///     it needs <c>jsonb_extract_path_text</c>, and Npgsql 10's <c>EF.Functions</c> carries only
///     the containment and existence operators — <c>JsonContains</c>, <c>JsonExists</c>,
///     <c>JsonTypeof</c> — none of which can answer a substring match.
///
///     The alternatives were worse. Storing the title in its own column denormalises a value the
///     document already carries and creates a second thing that can disagree with it. Remapping
///     <c>MeetingMinutes.Content</c> from <c>string</c> to <c>JsonDocument</c> would give up the
///     verbatim round-trip the secretary's editor depends on — the reason that column is a string
///     is so a field the server has never heard of survives a save.
///
///     Mapping the built-in function costs no schema change at all: nothing is added to the
///     database, and the SQL produced is the one somebody would write by hand.
///
/// NEVER CALLED IN MEMORY
///     Each method throws. They exist to be recognised inside an expression tree and replaced by
///     SQL; reaching the body means the call escaped into LINQ-to-Objects, where it would silently
///     return the wrong answer if it returned one at all.
/// </summary>
public static class PostgresJsonFunctions
{
    /// <summary>
    /// <c>jsonb_extract_path_text(json, key)</c> — one top-level key of a jsonb document as text,
    /// or NULL when the key is absent.
    ///
    /// NULL is the wanted answer for a missing key: <c>NULL LIKE '%x%'</c> is NULL, so a document
    /// with no such key fails a search clause rather than matching everything.
    /// </summary>
    public static string? JsonbExtractPathText(string json, string key)
        => throw new InvalidOperationException(
            $"{nameof(JsonbExtractPathText)} is a database function and only runs inside a query.");
}

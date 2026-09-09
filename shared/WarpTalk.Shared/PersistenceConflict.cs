using System;
using System.Data.Common;

namespace WarpTalk.Shared;

/// <summary>
/// WT-601: tells a UNIQUE-constraint rejection apart from a genuine server fault.
///
/// A write that a unique index refused is a statement about the DATA — this row is already here —
/// and belongs in front of the person who sent it, as 409 with the offending value named. Landing
/// it in a service's catch-all instead turns it into 500 "Something went wrong on the server",
/// which sends the reader looking for an outage over a spreadsheet that lists a word twice.
///
/// Detected by SQLSTATE, through <see cref="DbException.SqlState"/> — a BCL property every
/// provider fills in — so this needs no reference to Npgsql.
/// </summary>
public static class PersistenceConflict
{
    /// <summary>PostgreSQL <c>unique_violation</c>. The same code in every SQL-standard engine.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>PostgreSQL <c>string_data_right_truncation</c> — a value longer than its column.</summary>
    private const string ValueTooLong = "22001";

    private const int MaxDepth = 8;

    /// <summary>True when <paramref name="exception"/> is a unique-index rejection.</summary>
    public static bool IsUniqueViolation(Exception? exception)
        => HasSqlState(exception, UniqueViolation);

    /// <summary>True when <paramref name="exception"/> is a value that did not fit its column.</summary>
    public static bool IsValueTooLong(Exception? exception)
        => HasSqlState(exception, ValueTooLong);

    private static bool HasSqlState(Exception? exception, string sqlState)
    {
        var depth = 0;
        for (var current = exception; current is not null && depth < MaxDepth; current = current.InnerException, depth++)
        {
            if (current is DbException db && db.SqlState == sqlState)
            {
                return true;
            }
        }

        return false;
    }
}

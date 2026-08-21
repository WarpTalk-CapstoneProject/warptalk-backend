using System.Security.Cryptography;
using System.Text;

namespace WarpTalk.TranscriptService.Application.Services;

/// <summary>
/// The dedup key for a translation's text.
///
/// <c>translation_contents_dedup_idx</c> is unique on (workspace_id, text_hash, target_language),
/// so this value decides whether two identical sentences share a row or silently create a
/// duplicate that violates the index. Migration 017 backfilled the column with Postgres'
/// <c>md5(translated_text)</c> — lowercase hex of the UTF-8 bytes — and every writer has to agree
/// with that exactly.
///
/// It lives here rather than beside its first caller because there are now two writers: the Redis
/// consumer persisting a machine translation, and the correction service storing a translation a
/// person typed. Two private copies of one index's key is a drift waiting to happen, and the way
/// it would surface is a duplicate-key exception on a path nobody associates with hashing.
/// </summary>
public static class TranslationTextHash
{
    public static string Of(string text) =>
        System.Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));
}

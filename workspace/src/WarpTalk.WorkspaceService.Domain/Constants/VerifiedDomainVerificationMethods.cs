namespace WarpTalk.WorkspaceService.Domain.Constants;

/// <summary>
/// The trust tier behind a verified domain row — what actually backs the claim that this
/// workspace owns the domain. WT-157 leaves real DNS verification undecided, so these are
/// the two forms of evidence available without new schema.
/// </summary>
public static class VerifiedDomainVerificationMethods
{
    /// <summary>The domain matches the claiming account's own email domain. Self-evidencing:
    /// nobody types a value, so nothing to consent to.</summary>
    public const string OwnerEmail = "owner_email";

    /// <summary>The Owner asserts ownership of a domain that is not their own account's. Backed
    /// by nothing but that assertion, so it requires explicit, recorded consent.</summary>
    public const string SelfAsserted = "self_asserted";

    /// <summary>Reserved for WT-157. Not issued by any path yet.</summary>
    public const string DnsTxt = "dns_txt";
}

using Dcms.Shared.Data.Social;

namespace Dcms.AdminApi.Social;

/// <summary>
/// The permissions each login path asks Meta for, and what each one buys.
///
/// <para>Kept in one place because these strings are the difference between a working
/// connection and a 400 at consent time, and because every one of them has to be cleared in
/// Meta's App Review before non-test accounts can grant it. Adding a scope here is therefore
/// a process commitment, not just a code change.</para>
/// </summary>
public static class MetaScopes
{
    /// <summary>
    /// Facebook Login for Business. <c>pages_show_list</c> enumerates the Pages,
    /// <c>pages_read_engagement</c> reads their posts, <c>instagram_basic</c> reaches the
    /// linked Instagram account's media, and <c>instagram_manage_insights</c> is what the
    /// /stories endpoint requires — stories are the only reason it is here.
    /// </summary>
    public static readonly string[] Facebook =
    [
        "pages_show_list",
        "pages_read_engagement",
        "instagram_basic",
        "instagram_manage_insights",
    ];

    /// <summary>
    /// Instagram Login. Reaches media on a professional account with no Facebook Page, but
    /// there is no stories equivalent on this path — that is a platform limitation, not an
    /// omission, and the admin UI has to say so rather than showing a toggle that does nothing.
    /// </summary>
    public static readonly string[] InstagramLogin =
    [
        "instagram_business_basic",
    ];

    public static string[] For(MetaProvider provider) =>
        provider == MetaProvider.Facebook ? Facebook : InstagramLogin;

    /// <summary>Stories require the Page-linked path; see the scope list above.</summary>
    public static bool SupportsStories(MetaProvider provider) => provider == MetaProvider.Facebook;
}

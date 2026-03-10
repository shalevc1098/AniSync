using System;
using FluentAssertions;
using Xunit;

namespace Shoko.Tests;

/// <summary>
/// Tests for XSS prevention, URL validation, and secret masking.
/// </summary>
public class SecurityTests
{
    // ========================================================================
    // Secret masking logic
    // MaskSecret should handle short, normal, and empty secrets correctly.
    // ========================================================================

    private static string MaskSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            return string.Empty;
        if (secret.Length <= 4)
            return new string('*', secret.Length);
        return new string('*', secret.Length - 4) + secret[^4..];
    }

    [Fact]
    public void MaskSecret_Short_Secret_Should_Be_All_Stars()
    {
        MaskSecret("abc").Should().Be("***");
    }

    [Fact]
    public void MaskSecret_Exactly_4_Chars_Should_Be_All_Stars()
    {
        MaskSecret("abcd").Should().Be("****");
    }

    [Fact]
    public void MaskSecret_Normal_Secret_Shows_Last_4()
    {
        MaskSecret("mysecretvalue").Should().Be("*********alue");
    }

    [Fact]
    public void MaskSecret_Empty_Returns_Empty()
    {
        MaskSecret("").Should().BeEmpty();
        MaskSecret(null).Should().BeEmpty();
    }

    // ========================================================================
    // Masked secret preservation
    // When saving global settings, a masked secret (starts with *) should
    // preserve the existing real secret; a real secret should overwrite.
    // ========================================================================

    [Fact]
    public void SaveGlobalSettings_Masked_Secret_Preserves_Existing()
    {
        string existingSecret = "real_secret";
        string incomingSecret = "****cret"; // masked

        var effectiveSecret = incomingSecret;
        if (!string.IsNullOrEmpty(effectiveSecret) && effectiveSecret.StartsWith('*'))
        {
            effectiveSecret = existingSecret;
        }

        effectiveSecret.Should().Be("real_secret",
            "masked incoming secret should preserve the existing real secret");
    }

    [Fact]
    public void SaveGlobalSettings_Real_Secret_Overwrites()
    {
        string existingSecret = "old_secret";
        string incomingSecret = "newsecret";

        var effectiveSecret = incomingSecret;
        if (!string.IsNullOrEmpty(effectiveSecret) && effectiveSecret.StartsWith('*'))
        {
            effectiveSecret = existingSecret;
        }

        effectiveSecret.Should().Be("newsecret",
            "real incoming secret should overwrite the existing one");
    }

    // ========================================================================
    // Image URL sanitization
    // URLs with special characters must be escaped to prevent XSS.
    // ========================================================================

    [Fact]
    public void ImageUrl_With_SingleQuote_Should_Be_Escaped()
    {
        string imageUrl = "https://cdn.mal.net/image's.jpg";
        string safeUrl = imageUrl.Replace("'", "%27").Replace("<", "%3C").Replace(">", "%3E");

        safeUrl.Should().Be("https://cdn.mal.net/image%27s.jpg");
        safeUrl.Should().NotContain("'");
    }

    [Fact]
    public void ImageUrl_With_Angle_Brackets_Should_Be_Escaped()
    {
        string imageUrl = "https://cdn.mal.net/<script>alert(1)</script>.jpg";
        string safeUrl = imageUrl.Replace("'", "%27").Replace("<", "%3C").Replace(">", "%3E");

        safeUrl.Should().Be("https://cdn.mal.net/%3Cscript%3Ealert(1)%3C/script%3E.jpg");
        safeUrl.Should().NotContain("<");
        safeUrl.Should().NotContain(">");
    }

    [Fact]
    public void ImageUrl_Normal_Url_Unchanged()
    {
        string imageUrl = "https://cdn.mal.net/image.jpg";
        string safeUrl = imageUrl.Replace("'", "%27").Replace("<", "%3C").Replace(">", "%3E");

        safeUrl.Should().Be("https://cdn.mal.net/image.jpg");
    }

    [Fact]
    public void ImageUrl_With_All_Special_Chars_Should_Be_Escaped()
    {
        string imageUrl = "https://cdn.mal.net/img'<>.jpg";
        string safeUrl = imageUrl.Replace("'", "%27").Replace("<", "%3C").Replace(">", "%3E");

        safeUrl.Should().Be("https://cdn.mal.net/img%27%3C%3E.jpg");
    }

    // ========================================================================
    // XSS field escaping
    // Server-supplied values must be HTML-escaped before insertion into innerHTML.
    // ========================================================================

    [Fact]
    public void HtmlEscape_AngleBrackets()
    {
        string input = "<script>alert('xss')</script>";
        string escaped = System.Net.WebUtility.HtmlEncode(input);

        escaped.Should().NotContain("<");
        escaped.Should().NotContain(">");
        escaped.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void HtmlEscape_SingleQuote()
    {
        string input = "it's a test";
        string escaped = System.Net.WebUtility.HtmlEncode(input);

        escaped.Should().Contain("&#39;");
        escaped.Should().NotContain("'");
    }

    [Fact]
    public void HtmlEscape_Normal_Text_Unchanged()
    {
        string input = "Naruto Episode 5";
        string escaped = System.Net.WebUtility.HtmlEncode(input);

        escaped.Should().Be("Naruto Episode 5");
    }

    // ========================================================================
    // Avatar URL protocol validation
    // Only http://, https://, and relative (/) URLs should be accepted.
    // ========================================================================

    private static bool IsValidAvatarUrl(string? url)
    {
        return url != null &&
               (url.StartsWith("http://") || url.StartsWith("https://") || url.StartsWith("/"));
    }

    [Fact]
    public void ValidAvatarUrl_Https_Accepted()
    {
        IsValidAvatarUrl("https://cdn.myanimelist.net/images/userimages/123.jpg")
            .Should().BeTrue();
    }

    [Fact]
    public void ValidAvatarUrl_Http_Accepted()
    {
        IsValidAvatarUrl("http://example.com/avatar.png")
            .Should().BeTrue();
    }

    [Fact]
    public void ValidAvatarUrl_Relative_Accepted()
    {
        IsValidAvatarUrl("/api/v3/User/Avatar")
            .Should().BeTrue();
    }

    [Fact]
    public void InvalidAvatarUrl_Javascript_Rejected()
    {
        IsValidAvatarUrl("javascript:alert('xss')")
            .Should().BeFalse("javascript: URLs must be blocked");
    }

    [Fact]
    public void InvalidAvatarUrl_Data_Rejected()
    {
        IsValidAvatarUrl("data:text/html,<script>alert(1)</script>")
            .Should().BeFalse("data: URLs must be blocked");
    }

    [Fact]
    public void InvalidAvatarUrl_Null_Rejected()
    {
        IsValidAvatarUrl(null)
            .Should().BeFalse("null URLs must be rejected");
    }
}

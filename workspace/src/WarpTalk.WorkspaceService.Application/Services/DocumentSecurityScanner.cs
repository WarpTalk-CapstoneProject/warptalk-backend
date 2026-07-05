using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Services;

/// <summary>
/// Application service performing Regex-based PII scans and case-insensitive DLP checks on raw text.
/// </summary>
public class DocumentSecurityScanner : IDocumentSecurityScanner
{
    private static readonly Regex EmailRegex = new(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PhoneRegex = new(@"\b(?:\+?84|0)\d{9,10}\b", RegexOptions.Compiled);
    private static readonly Regex SsnRegex = new(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled);
    private static readonly Regex VnIdRegex = new(@"\b\d{12}\b|\b\d{9}\b", RegexOptions.Compiled);

    public DocumentSecurityScanResult Scan(string content, bool piiEnabled, bool dlpEnabled, List<string>? keywordsBlacklist)
    {
        bool piiDetected = false;
        bool dlpDetected = false;

        // PII Scan (GDPR, HIPAA, local security compliance)
        if (piiEnabled)
        {
            if (EmailRegex.IsMatch(content) ||
                PhoneRegex.IsMatch(content) ||
                SsnRegex.IsMatch(content) ||
                VnIdRegex.IsMatch(content))
            {
                piiDetected = true;
            }
        }

        // DLP Scan (Case-insensitive keyword blocking)
        if (dlpEnabled && keywordsBlacklist != null)
        {
            foreach (var keyword in keywordsBlacklist)
            {
                if (!string.IsNullOrWhiteSpace(keyword) && content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    dlpDetected = true;
                    break;
                }
            }
        }

        bool violationFound = piiDetected || dlpDetected;

        return new DocumentSecurityScanResult(violationFound, piiDetected, dlpDetected);
    }
}

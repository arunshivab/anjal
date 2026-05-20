# DmarcDetail

**Namespace:** `Anjal.Auth`

Detailed result of a DMARC evaluation.

## Members

- **AlignedDomain** *(property)* - The domain that was successfully aligned (empty if neither aligned).
- **DkimAligned** *(property)* - True if DKIM passed and the signing domain aligned with the From domain.
- **DkimAlignment** *(property)* - DKIM alignment mode (adkim= tag). Default Relaxed.
- **Explanation** *(property)* - Human-readable explanation suitable for the Authentication-Results comment.
- **FromDomain** *(property)* - Organizational domain from the From header that was used to look up the DMARC record.
- **Policy** *(property)* - The published policy (p= tag), if a record was found.
- **Result** *(property)* - The verdict.
- **SpfAligned** *(property)* - True if SPF passed and the MAIL FROM domain aligned with the From domain.
- **SpfAlignment** *(property)* - SPF alignment mode (aspf= tag). Default Relaxed.

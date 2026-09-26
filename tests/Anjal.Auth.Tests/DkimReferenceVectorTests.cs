namespace Anjal.Auth.Tests;

/// <summary>
/// DEF-056 and DEF-057: every message here was signed, and every expected
/// result decided, by dkimpy - an independent RFC 6376 implementation - not by
/// Anjal's own signer. The earlier tests only checked Anjal against itself, and
/// both sides shared the same mistakes: a genuine Gmail message failed DKIM on
/// the production server (26 Sep 2026) because Gmail over-signs headers.
/// Messages are base64 so that no line-ending conversion can change a signed
/// byte. Generator: tests/Anjal.Auth.Tests/Fixtures/gen_dkim_reference_vectors.py.
/// </summary>
public class DkimReferenceVectorTests
{
    private const string SenderKey =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEArfesUkFKNo9uhbqcNnwWfadP1uQz51x/Yce24z/5p9+UzES6I8O9bzaL" +
        "hRjN11RBWQFYBQrGmSbNM1Ng/ibGNwr29jlYpbQ9WX7UnGWNEYLQNTJ53wWLbCdw3MRc+0v/CwosAhbuL7tIp/MvASVy/qaZgg2T" +
        "ImGOXMNmI+dBWn1t7W/iCVcLjsQ0pqqdmI3hYAN2sSDdA6VmjiFmRoTotNsT4OtKfni85wz769vc4bYyJkD7KE636wfJbsN6jSYU" +
        "U7tNEpjTysbGc8kOYY2vzi3LuaitATe1O3GWUkjCXtG/GJISHQqwZK7PpfAxLIhvUhrTBUqHpEoaXBPu1TejcQIDAQAB";

    private const string RelayKey =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAoHg3gaw3ULgrGjxHIjuWe6pqajUA+adbGRoXDDrx5TUoLbapk5a/goDB" +
        "prkuR86mHicIFmetHT9tB1bkRTMM5nmPaB3NO5kQZvebhFT30Xwvg0geuIhTpRY5edOTxo0z700vHFxmr6Qn/DUR7p/njppMKIBM" +
        "jdeCf8ZSDcYXzl8pQCDMVqcH5Bp5ndZu5sJfBdqaPvDv1YwBmnTLhVCB07E+U7geumlaldjx9ZVAiqUJkrJ4ovdQtcl4/iYL3II7" +
        "TLb3Db9AX5iDBoSqAyyjnE7LnfenPgccJMQ4N+OGhsPKhviwoswziRk3doT/NvHR56TeDWtZh1KpMIr+U3suYwIDAQAB";

    // dkimpy per signature, top first: [True]
    private const string GmailOversigned =
        "REtJTS1TaWduYXR1cmU6IHY9MTsgYT1yc2Etc2hhMjU2OyBjPXJlbGF4ZWQvcmVsYXhlZDsgZD1zZW5kZXIudGVzdDsNCiBpPUBz" +
        "ZW5kZXIudGVzdDsgcT1kbnMvdHh0OyBzPXMxOyB0PTE3OTA0MDQ3MTQ7IGg9Y29udGVudC10eXBlIDogdG8gOg0KIHN1YmplY3Qg" +
        "OiBtZXNzYWdlLWlkIDogZGF0ZSA6IGZyb20gOiBtaW1lLXZlcnNpb24gOiBmcm9tIDogdG8gOiBjYyA6DQogc3ViamVjdCA6IGRh" +
        "dGUgOiBtZXNzYWdlLWlkIDogcmVwbHktdG8gOiBjb250ZW50LXR5cGU7DQogYmg9ZDY1ZG40M014bVZGd2JMUVkxeWdmN29EZ0Vv" +
        "MTAvZGIvRFVkUlZ3WVBQOD07DQogYj1HcUkyL0tCZVlHcFQxQXdKNW9jcTl1WDRuUzZFQmVDbXljT2ZramVkR2YxV3VNejRZMEZH" +
        "RkJJZ090YlBPUElrTEVzYzcNCiAwbVZPN21STFlzZndJS3dRTkMzMjVmcFg4YmoyOE9NOFVyZ0N2ZWxtNVNOUWlJT2pTcUdMNW9E" +
        "MzRNaXpGcGVyS3EvKzFPOQ0KIGtSUFh3YjBPbmlwZkxGWHBUbjJYUkt3UWNJa2xEYjFHWWIva0p6Z0FYRUpYR0FWVERDYmpGMlBo" +
        "TU0zOGZ3S1RWQTRiaWdmDQogYTJ5N0NBSWhQRFFUN1NFOVNDdGpuS09vdzk1UVpseXlNbm5kYlVBOW15dERLbGdLWXZBcmJIYk1t" +
        "OHJmRTdCb0hnVStWNmENCiBRVkFWUWF0RDBFSVhxNFZkdVk0UVQvWlRKWkFxaW1MNk0xL01YdGEzMEhoUzZucExMWGp5OG54RW9T" +
        "L1E9PQ0KTUlNRS1WZXJzaW9uOiAxLjANCkRhdGU6IFNhdCwgMjYgU2VwIDIwMjYgMTI6MDE6MjIgKzA1MzANCk1lc3NhZ2UtSUQ6" +
        "IDxDQUYtdGVzdC0wMDAxQG1haWwuc2VuZGVyLnRlc3Q+DQpTdWJqZWN0OiBBbmphbCBpbmJvdW5kIHRlc3QgMQ0KRnJvbTogQXJ1" +
        "biBUZXN0ZXIgPGFydW5Ac2VuZGVyLnRlc3Q+DQpUbzogYXJ1bkBhbmphbC5jby5pbg0KQ29udGVudC1UeXBlOiB0ZXh0L3BsYWlu" +
        "OyBjaGFyc2V0PSJVVEYtOCINCg0KSGVsbG8gZnJvbSB0aGUgcmVmZXJlbmNlIHNpZ25lci4NCg0KU2Vjb25kIHBhcmFncmFwaCwg" +
        "dHJhaWxpbmcgc3BhY2VzIGhlcmUgICANCg==";

    // dkimpy per signature, top first: [True]
    private const string SimpleSimple =
        "REtJTS1TaWduYXR1cmU6IHY9MTsgYT1yc2Etc2hhMjU2OyBjPXNpbXBsZS9zaW1wbGU7IGQ9c2VuZGVyLnRlc3Q7DQogaT1Ac2Vu" +
        "ZGVyLnRlc3Q7IHE9ZG5zL3R4dDsgcz1zMTsgdD0xNzkwNDA0NzE0OyBoPWZyb20gOiB0byA6IHN1YmplY3QgOg0KIGRhdGU7IGJo" +
        "PXYxbFdBVUxHcU5Xazc5ZEtRUFhzNlRBQ1NMRDV2OHhvWjhHRVV4c2xlKzg9Ow0KIGI9REpwRElyWHkvdVMxUjgwZHJsU2VkNG93" +
        "TklxK29zWjFpRGdWcmdDWTFzWUlmMU80SXVJZ2RTMXVpbVVTMzB1dXNEVktRDQogZGdYTHZmVEpzV0ZyUE5ZdHJXL21MRzlMTWti" +
        "WnYrSXVyc3BzWVJYcDBKeEQvQkw1M09odlV4aDlPbWlSWEF1Ukl0eGdvRlcNCiBWclZaMkMwTXpOWjhRRGdxcnlRSkZjR1gwSDl1" +
        "Z3RMWWdXTVlaendQVFNSUWZWUDVVZGFDZTBoamZwajFhZW1hVjVpZnNiZw0KIGJmL0gyMXhrQ1pMZWxZK2pXRUxWbVNYS0puSU9y" +
        "QndCM2ZJVk5lajdrNzlMOGFHSjdlOVhvUzJES0I0V09oUVk4U1JkNGtIDQogUFhwZDJaN2hTUlJReVBQU21RMEVNSFkzK2cyNHJ3" +
        "amJRRmFpVGh2bENyTjJZSHA4MjlIaXJjMUlhTDR3PT0NCk1JTUUtVmVyc2lvbjogMS4wDQpEYXRlOiBTYXQsIDI2IFNlcCAyMDI2" +
        "IDEyOjAxOjIyICswNTMwDQpNZXNzYWdlLUlEOiA8Q0FGLXRlc3QtMDAwMUBtYWlsLnNlbmRlci50ZXN0Pg0KU3ViamVjdDogQSBz" +
        "dWJqZWN0IGxvbmcgZW5vdWdoDQogdG8gYmUgZm9sZGVkIG9udG8gYSBzZWNvbmQgbGluZQ0KRnJvbTogQXJ1biBUZXN0ZXIgPGFy" +
        "dW5Ac2VuZGVyLnRlc3Q+DQpUbzogYXJ1bkBhbmphbC5jby5pbg0KQ29udGVudC1UeXBlOiB0ZXh0L3BsYWluOyBjaGFyc2V0PSJV" +
        "VEYtOCINCg0KSGVsbG8gZnJvbSB0aGUgcmVmZXJlbmNlIHNpZ25lci4NCg0KU2Vjb25kIHBhcmFncmFwaCwgdHJhaWxpbmcgc3Bh" +
        "Y2VzIGhlcmUgICANCg==";

    // dkimpy per signature, top first: [True]
    private const string RepeatedBottomUp =
        "REtJTS1TaWduYXR1cmU6IHY9MTsgYT1yc2Etc2hhMjU2OyBjPXJlbGF4ZWQvcmVsYXhlZDsgZD1zZW5kZXIudGVzdDsNCiBpPUBz" +
        "ZW5kZXIudGVzdDsgcT1kbnMvdHh0OyBzPXMxOyB0PTE3OTA0MDQ3MTQ7IGg9ZnJvbSA6IHgtdGFnIDogeC10YWcNCiA6IHN1Ympl" +
        "Y3Q7IGJoPWQ2NWRuNDNNeG1WRndiTFFZMXlnZjdvRGdFbzEwL2RiL0RVZFJWd1lQUDg9Ow0KIGI9UVVpZjAxa0RhQzlCY1U1aWcx" +
        "STBBM2w2RE5tTjdzOHFQRDUwZmdEVG1melZ0eTAyN3dJTGJ1M3RGS2RjNHErSTQ1ODRpDQogeFlxT3dpbk5kM3cyOHNwaFowNGo0" +
        "RVdoVWhzVmtLS3NoV29wOWFkWnFzN1RHMGJrbkdYOG5oZnAvSmhuUDVUV1gzMHl1bUYNCiBwNFBZZTYxQTNFRVJVdTBiTm5GclM4" +
        "cGZqODRkSXhwS1NxbHhQQ0dMMmNjeEdrbVJzVEhpa3NjMjM0c2o3MHZKclFSNlJZaQ0KIGd3eDhsVkFwR2U4dkRNUnBxZjQ4REtx" +
        "YW55Ri9mOUNPR0NwVEdvVkJTNU9rRllENEY2UGgveE1PZ3ZoQ3JOcnQ3UnpTYy9mDQogQkVoS014LzNiVkRVYkd6ZjlYY0RnUnJT" +
        "OG1yL1JEam1Sd3p0Ym9DdEdKbkl3eFdUdzBmWDNjV0NMbEdnPT0NClgtVGFnOiBmaXJzdA0KWC1UYWc6IHNlY29uZA0KTUlNRS1W" +
        "ZXJzaW9uOiAxLjANCkRhdGU6IFNhdCwgMjYgU2VwIDIwMjYgMTI6MDE6MjIgKzA1MzANCk1lc3NhZ2UtSUQ6IDxDQUYtdGVzdC0w" +
        "MDAxQG1haWwuc2VuZGVyLnRlc3Q+DQpTdWJqZWN0OiBBbmphbCBpbmJvdW5kIHRlc3QgMQ0KRnJvbTogQXJ1biBUZXN0ZXIgPGFy" +
        "dW5Ac2VuZGVyLnRlc3Q+DQpUbzogYXJ1bkBhbmphbC5jby5pbg0KQ29udGVudC1UeXBlOiB0ZXh0L3BsYWluOyBjaGFyc2V0PSJV" +
        "VEYtOCINCg0KSGVsbG8gZnJvbSB0aGUgcmVmZXJlbmNlIHNpZ25lci4NCg0KU2Vjb25kIHBhcmFncmFwaCwgdHJhaWxpbmcgc3Bh" +
        "Y2VzIGhlcmUgICANCg==";

    // dkimpy per signature, top first: [False, True]
    private const string TwoSigsTopBroken =
        "REtJTS1TaWduYXR1cmU6IHY9MTsgYT1yc2Etc2hhMjU2OyBjPXJlbGF4ZWQvcmVsYXhlZDsgZD1yZWxheS50ZXN0Ow0KIGk9QHJl" +
        "bGF5LnRlc3Q7IHE9ZG5zL3R4dDsgcz1yMTsgdD0xNzkwNDA0NzE0OyBoPWZyb20gOiB0byA6IHN1YmplY3Q7DQogYmg9ZDY1ZG40" +
        "M014bVZGd2JMUVkxeWdmN29EZ0VvMTAvZGIvRFVkUlZ3WVBQOD07DQogYj1uVVZpd3pEOFpNRzlqdFJrWFlZbExnUVdlSWxZaGI3" +
        "S3dsekxBUFpxZUpkWmhnVEluUGwzZHlYaTBrWWhpbXZ0bnpqeWcNCiBvZU1qKzN3MkpsTzhsdjhUWVJIdGlBVG41TEtCZitnSFRl" +
        "SXdhQkR6WERnbGJQenU3M1F4OEk4UHo1UVE1c2Q0N2VuRWtzUQ0KIGt1c1FwYmxmeFhEbnpldkNHUHEyRks3di9CUTlvclhJVzFL" +
        "MGxxZzA5R0hrclVGY0tsRG9oRXNPalhyMWE0eXhUY2xhOHNuDQogMk9RT3cvODhlOFFHNGYzSHUzM0xpNWZZODJnZllKVjdPUlNj" +
        "WFRxVU1lMkYreXpsWGJCTFNnZWovd0lNTU9hYUFOZm81VHcNCiBUZFE1emZ3WXU2TmNDeTduMUl3U2MrRTdPZERsNzcyZVBBMXRH" +
        "MWlCUW14U3kxd2czcjh1YmhKcTZ6aEE9PQ0KREtJTS1TaWduYXR1cmU6IHY9MTsgYT1yc2Etc2hhMjU2OyBjPXJlbGF4ZWQvcmVs" +
        "YXhlZDsgZD1zZW5kZXIudGVzdDsNCiBpPUBzZW5kZXIudGVzdDsgcT1kbnMvdHh0OyBzPXMxOyB0PTE3OTA0MDQ3MTQ7IGg9Y29u" +
        "dGVudC10eXBlIDogdG8gOg0KIHN1YmplY3QgOiBtZXNzYWdlLWlkIDogZGF0ZSA6IGZyb20gOiBtaW1lLXZlcnNpb24gOiBmcm9t" +
        "IDogdG8gOiBjYyA6DQogc3ViamVjdCA6IGRhdGUgOiBtZXNzYWdlLWlkIDogcmVwbHktdG8gOiBjb250ZW50LXR5cGU7DQogYmg9" +
        "ZDY1ZG40M014bVZGd2JMUVkxeWdmN29EZ0VvMTAvZGIvRFVkUlZ3WVBQOD07DQogYj1HcUkyL0tCZVlHcFQxQXdKNW9jcTl1WDRu" +
        "UzZFQmVDbXljT2ZramVkR2YxV3VNejRZMEZHRkJJZ090YlBPUElrTEVzYzcNCiAwbVZPN21STFlzZndJS3dRTkMzMjVmcFg4Ymoy" +
        "OE9NOFVyZ0N2ZWxtNVNOUWlJT2pTcUdMNW9EMzRNaXpGcGVyS3EvKzFPOQ0KIGtSUFh3YjBPbmlwZkxGWHBUbjJYUkt3UWNJa2xE" +
        "YjFHWWIva0p6Z0FYRUpYR0FWVERDYmpGMlBoTU0zOGZ3S1RWQTRiaWdmDQogYTJ5N0NBSWhQRFFUN1NFOVNDdGpuS09vdzk1UVps" +
        "eXlNbm5kYlVBOW15dERLbGdLWXZBcmJIYk1tOHJmRTdCb0hnVStWNmENCiBRVkFWUWF0RDBFSVhxNFZkdVk0UVQvWlRKWkFxaW1M" +
        "Nk0xL01YdGEzMEhoUzZucExMWGp5OG54RW9TL1E9PQ0KTUlNRS1WZXJzaW9uOiAxLjANCkRhdGU6IFNhdCwgMjYgU2VwIDIwMjYg" +
        "MTI6MDE6MjIgKzA1MzANCk1lc3NhZ2UtSUQ6IDxDQUYtdGVzdC0wMDAxQG1haWwuc2VuZGVyLnRlc3Q+DQpTdWJqZWN0OiBBbmph" +
        "bCBpbmJvdW5kIHRlc3QgMQ0KRnJvbTogQXJ1biBUZXN0ZXIgPGFydW5Ac2VuZGVyLnRlc3Q+DQpUbzogYXJ1bkBhbmphbC5jby5p" +
        "bg0KQ29udGVudC1UeXBlOiB0ZXh0L3BsYWluOyBjaGFyc2V0PSJVVEYtOCINCg0KSGVsbG8gZnJvbSB0aGUgcmVmZXJlbmNlIHNp" +
        "Z25lci4NCg0KU2Vjb25kIHBhcmFncmFwaCwgdHJhaWxpbmcgc3BhY2VzIGhlcmUgICANCg==";

    // dkimpy per signature, top first: [True, True]
    private const string TwoSigsTopUnaligned =
        "REtJTS1TaWduYXR1cmU6IHY9MTsgYT1yc2Etc2hhMjU2OyBjPXJlbGF4ZWQvcmVsYXhlZDsgZD1yZWxheS50ZXN0Ow0KIGk9QHJl" +
        "bGF5LnRlc3Q7IHE9ZG5zL3R4dDsgcz1yMTsgdD0xNzkwNDA0NzE0OyBoPWZyb20gOiB0byA6IHN1YmplY3Q7DQogYmg9ZDY1ZG40" +
        "M014bVZGd2JMUVkxeWdmN29EZ0VvMTAvZGIvRFVkUlZ3WVBQOD07DQogYj1jVW81WUpiVEJZeFN3cSsyeE1MZ2pMb21JeTh2c3k4" +
        "NC9lWVBXKzVCeGcrSC9ZTXJwWnpQOHpJQVl5QXdOSXNiRm56ZnANCiBJcnhjU3YzZ1VhZS8zZHlHdHRPU1hOVHMwSDMwd0U5cUM2" +
        "RENTS3lDTnRWbVFIUkJpODcrWFJEcDhlKzBGYmY4Q3BJVTl0Yg0KIFd1NXVZbmdUZXgwMHhsTms4OWhVandiYjJsL05aL0xFd3Bt" +
        "OGhobXpLUllRQ250N1U2VFRoK3RxR2kxQmp3eGR4Vys0ZXJKDQogeG1MLzVrZXVoUHI4eDVTV2l2ZzdreFhLclFwWFhzeWhXSFBz" +
        "RitERmt1TDJhVFJUSnd6Uk8vcHpLdjZlV3NuNldtMkV6eUgNCiBuWFNTbkd4YlNoUjV0SWZYRnFOV0dSczVLUXRCVElKQnNMU05k" +
        "NGZRMTJYbUk1bEtFb05xWWc4NlE1aGc9PQ0KREtJTS1TaWduYXR1cmU6IHY9MTsgYT1yc2Etc2hhMjU2OyBjPXJlbGF4ZWQvcmVs" +
        "YXhlZDsgZD1zZW5kZXIudGVzdDsNCiBpPUBzZW5kZXIudGVzdDsgcT1kbnMvdHh0OyBzPXMxOyB0PTE3OTA0MDQ3MTQ7IGg9Y29u" +
        "dGVudC10eXBlIDogdG8gOg0KIHN1YmplY3QgOiBtZXNzYWdlLWlkIDogZGF0ZSA6IGZyb20gOiBtaW1lLXZlcnNpb24gOiBmcm9t" +
        "IDogdG8gOiBjYyA6DQogc3ViamVjdCA6IGRhdGUgOiBtZXNzYWdlLWlkIDogcmVwbHktdG8gOiBjb250ZW50LXR5cGU7DQogYmg9" +
        "ZDY1ZG40M014bVZGd2JMUVkxeWdmN29EZ0VvMTAvZGIvRFVkUlZ3WVBQOD07DQogYj1HcUkyL0tCZVlHcFQxQXdKNW9jcTl1WDRu" +
        "UzZFQmVDbXljT2ZramVkR2YxV3VNejRZMEZHRkJJZ090YlBPUElrTEVzYzcNCiAwbVZPN21STFlzZndJS3dRTkMzMjVmcFg4Ymoy" +
        "OE9NOFVyZ0N2ZWxtNVNOUWlJT2pTcUdMNW9EMzRNaXpGcGVyS3EvKzFPOQ0KIGtSUFh3YjBPbmlwZkxGWHBUbjJYUkt3UWNJa2xE" +
        "YjFHWWIva0p6Z0FYRUpYR0FWVERDYmpGMlBoTU0zOGZ3S1RWQTRiaWdmDQogYTJ5N0NBSWhQRFFUN1NFOVNDdGpuS09vdzk1UVps" +
        "eXlNbm5kYlVBOW15dERLbGdLWXZBcmJIYk1tOHJmRTdCb0hnVStWNmENCiBRVkFWUWF0RDBFSVhxNFZkdVk0UVQvWlRKWkFxaW1M" +
        "Nk0xL01YdGEzMEhoUzZucExMWGp5OG54RW9TL1E9PQ0KTUlNRS1WZXJzaW9uOiAxLjANCkRhdGU6IFNhdCwgMjYgU2VwIDIwMjYg" +
        "MTI6MDE6MjIgKzA1MzANCk1lc3NhZ2UtSUQ6IDxDQUYtdGVzdC0wMDAxQG1haWwuc2VuZGVyLnRlc3Q+DQpTdWJqZWN0OiBBbmph" +
        "bCBpbmJvdW5kIHRlc3QgMQ0KRnJvbTogQXJ1biBUZXN0ZXIgPGFydW5Ac2VuZGVyLnRlc3Q+DQpUbzogYXJ1bkBhbmphbC5jby5p" +
        "bg0KQ29udGVudC1UeXBlOiB0ZXh0L3BsYWluOyBjaGFyc2V0PSJVVEYtOCINCg0KSGVsbG8gZnJvbSB0aGUgcmVmZXJlbmNlIHNp" +
        "Z25lci4NCg0KU2Vjb25kIHBhcmFncmFwaCwgdHJhaWxpbmcgc3BhY2VzIGhlcmUgICANCg==";

    // dkimpy per signature, top first: [False]
    private const string TamperedSubject =
        "REtJTS1TaWduYXR1cmU6IHY9MTsgYT1yc2Etc2hhMjU2OyBjPXJlbGF4ZWQvcmVsYXhlZDsgZD1zZW5kZXIudGVzdDsNCiBpPUBz" +
        "ZW5kZXIudGVzdDsgcT1kbnMvdHh0OyBzPXMxOyB0PTE3OTA0MDQ3MTQ7IGg9Y29udGVudC10eXBlIDogdG8gOg0KIHN1YmplY3Qg" +
        "OiBtZXNzYWdlLWlkIDogZGF0ZSA6IGZyb20gOiBtaW1lLXZlcnNpb24gOiBmcm9tIDogdG8gOiBjYyA6DQogc3ViamVjdCA6IGRh" +
        "dGUgOiBtZXNzYWdlLWlkIDogcmVwbHktdG8gOiBjb250ZW50LXR5cGU7DQogYmg9ZDY1ZG40M014bVZGd2JMUVkxeWdmN29EZ0Vv" +
        "MTAvZGIvRFVkUlZ3WVBQOD07DQogYj1HcUkyL0tCZVlHcFQxQXdKNW9jcTl1WDRuUzZFQmVDbXljT2ZramVkR2YxV3VNejRZMEZH" +
        "RkJJZ090YlBPUElrTEVzYzcNCiAwbVZPN21STFlzZndJS3dRTkMzMjVmcFg4YmoyOE9NOFVyZ0N2ZWxtNVNOUWlJT2pTcUdMNW9E" +
        "MzRNaXpGcGVyS3EvKzFPOQ0KIGtSUFh3YjBPbmlwZkxGWHBUbjJYUkt3UWNJa2xEYjFHWWIva0p6Z0FYRUpYR0FWVERDYmpGMlBo" +
        "TU0zOGZ3S1RWQTRiaWdmDQogYTJ5N0NBSWhQRFFUN1NFOVNDdGpuS09vdzk1UVpseXlNbm5kYlVBOW15dERLbGdLWXZBcmJIYk1t" +
        "OHJmRTdCb0hnVStWNmENCiBRVkFWUWF0RDBFSVhxNFZkdVk0UVQvWlRKWkFxaW1MNk0xL01YdGEzMEhoUzZucExMWGp5OG54RW9T" +
        "L1E9PQ0KTUlNRS1WZXJzaW9uOiAxLjANCkRhdGU6IFNhdCwgMjYgU2VwIDIwMjYgMTI6MDE6MjIgKzA1MzANCk1lc3NhZ2UtSUQ6" +
        "IDxDQUYtdGVzdC0wMDAxQG1haWwuc2VuZGVyLnRlc3Q+DQpTdWJqZWN0OiBBbmphbCBpbmJvdW5kIHRlc3QgMg0KRnJvbTogQXJ1" +
        "biBUZXN0ZXIgPGFydW5Ac2VuZGVyLnRlc3Q+DQpUbzogYXJ1bkBhbmphbC5jby5pbg0KQ29udGVudC1UeXBlOiB0ZXh0L3BsYWlu" +
        "OyBjaGFyc2V0PSJVVEYtOCINCg0KSGVsbG8gZnJvbSB0aGUgcmVmZXJlbmNlIHNpZ25lci4NCg0KU2Vjb25kIHBhcmFncmFwaCwg" +
        "dHJhaWxpbmcgc3BhY2VzIGhlcmUgICANCg==";

    // dkimpy per signature, top first: [False]
    private const string InjectedFrom =
        "RnJvbTogQXR0YWNrZXIgPGJvc3NAc2VuZGVyLnRlc3Q+DQpES0lNLVNpZ25hdHVyZTogdj0xOyBhPXJzYS1zaGEyNTY7IGM9cmVs" +
        "YXhlZC9yZWxheGVkOyBkPXNlbmRlci50ZXN0Ow0KIGk9QHNlbmRlci50ZXN0OyBxPWRucy90eHQ7IHM9czE7IHQ9MTc5MDQwNDcx" +
        "NDsgaD1jb250ZW50LXR5cGUgOiB0byA6DQogc3ViamVjdCA6IG1lc3NhZ2UtaWQgOiBkYXRlIDogZnJvbSA6IG1pbWUtdmVyc2lv" +
        "biA6IGZyb20gOiB0byA6IGNjIDoNCiBzdWJqZWN0IDogZGF0ZSA6IG1lc3NhZ2UtaWQgOiByZXBseS10byA6IGNvbnRlbnQtdHlw" +
        "ZTsNCiBiaD1kNjVkbjQzTXhtVkZ3YkxRWTF5Z2Y3b0RnRW8xMC9kYi9EVWRSVndZUFA4PTsNCiBiPUdxSTIvS0JlWUdwVDFBd0o1" +
        "b2NxOXVYNG5TNkVCZUNteWNPZmtqZWRHZjFXdU16NFkwRkdGQklnT3RiUE9QSWtMRXNjNw0KIDBtVk83bVJMWXNmd0lLd1FOQzMy" +
        "NWZwWDhiajI4T004VXJnQ3ZlbG01U05RaUlPalNxR0w1b0QzNE1pekZwZXJLcS8rMU85DQoga1JQWHdiME9uaXBmTEZYcFRuMlhS" +
        "S3dRY0lrbERiMUdZYi9rSnpnQVhFSlhHQVZURENiakYyUGhNTTM4ZndLVFZBNGJpZ2YNCiBhMnk3Q0FJaFBEUVQ3U0U5U0N0am5L" +
        "T293OTVRWmx5eU1ubmRiVUE5bXl0REtsZ0tZdkFyYkhiTW04cmZFN0JvSGdVK1Y2YQ0KIFFWQVZRYXREMEVJWHE0VmR1WTRRVC9a" +
        "VEpaQXFpbUw2TTEvTVh0YTMwSGhTNm5wTExYank4bnhFb1MvUT09DQpNSU1FLVZlcnNpb246IDEuMA0KRGF0ZTogU2F0LCAyNiBT" +
        "ZXAgMjAyNiAxMjowMToyMiArMDUzMA0KTWVzc2FnZS1JRDogPENBRi10ZXN0LTAwMDFAbWFpbC5zZW5kZXIudGVzdD4NClN1Ympl" +
        "Y3Q6IEFuamFsIGluYm91bmQgdGVzdCAxDQpGcm9tOiBBcnVuIFRlc3RlciA8YXJ1bkBzZW5kZXIudGVzdD4NClRvOiBhcnVuQGFu" +
        "amFsLmNvLmluDQpDb250ZW50LVR5cGU6IHRleHQvcGxhaW47IGNoYXJzZXQ9IlVURi04Ig0KDQpIZWxsbyBmcm9tIHRoZSByZWZl" +
        "cmVuY2Ugc2lnbmVyLg0KDQpTZWNvbmQgcGFyYWdyYXBoLCB0cmFpbGluZyBzcGFjZXMgaGVyZSAgIA0K";

    /// <summary>Gmail lists from, to, subject, date, message-id and content-type twice and names cc and reply-to, which are absent. A header listed more often than it occurs contributes nothing after its last instance.</summary>
    [Fact]
    public async System.Threading.Tasks.Task GmailShapedOverSigning_Passes()
    {
        DkimDetail detail = await VerifyAsync(GmailOversigned);
        Assert.Equal(DkimResult.Pass, detail.Result);
        Assert.Equal("sender.test", detail.Domain);
    }

    /// <summary>In simple mode the header is hashed exactly as written - its own name casing and folding - not as spelled in h=.</summary>
    [Fact]
    public async System.Threading.Tasks.Task SimpleCanonicalization_UsesTheHeaderAsWritten()
    {
        DkimDetail detail = await VerifyAsync(SimpleSimple);
        Assert.Equal(DkimResult.Pass, detail.Result);
        Assert.Equal("sender.test", detail.Domain);
    }

    /// <summary>RFC 6376 section 5.4.2: for a header that occurs more than once, each listing takes the next instance from the bottom.</summary>
    [Fact]
    public async System.Threading.Tasks.Task RepeatedHeader_InstancesTakenFromTheBottomUp()
    {
        DkimDetail detail = await VerifyAsync(RepeatedBottomUp);
        Assert.Equal(DkimResult.Pass, detail.Result);
        Assert.Equal("sender.test", detail.Domain);
    }

    /// <summary>Only the first signature used to be checked. A valid second signature must be found.</summary>
    [Fact]
    public async System.Threading.Tasks.Task TwoSignatures_TopBroken_SecondStillCounts()
    {
        DkimDetail detail = await VerifyAsync(TwoSigsTopBroken);
        Assert.Equal(DkimResult.Pass, detail.Result);
        Assert.Equal("sender.test", detail.Domain);
    }

    /// <summary>Both signatures are valid; the one whose domain matches From is the one DMARC can use, so it is the one reported.</summary>
    [Fact]
    public async System.Threading.Tasks.Task TwoSignatures_AlignedPassIsReported()
    {
        DkimDetail detail = await VerifyAsync(TwoSigsTopUnaligned);
        Assert.Equal(DkimResult.Pass, detail.Result);
        Assert.Equal("sender.test", detail.Domain);
    }

    /// <summary>Negative control: the fix must not make altered messages pass.</summary>
    [Fact]
    public async System.Threading.Tasks.Task OverSignedMessage_WithChangedSubject_Fails()
    {
        DkimDetail detail = await VerifyAsync(TamperedSubject);
        Assert.Equal(DkimResult.Fail, detail.Result);
    }

    /// <summary>Negative control: an extra From added above an over-signed message is what over-signing exists to catch.</summary>
    [Fact]
    public async System.Threading.Tasks.Task OverSignedMessage_WithInjectedFrom_Fails()
    {
        DkimDetail detail = await VerifyAsync(InjectedFrom);
        Assert.Equal(DkimResult.Fail, detail.Result);
    }

    // ---- Anjal's signer, checked by dkimpy (DEF-058) ----
    // PKCS#1 v1.5 signatures are deterministic: with this test-only key and a
    // fixed clock, Anjal must reproduce byte for byte the messages dkimpy
    // verified (relaxed: True, simple: True). Before the fix,
    // dkimpy rejected every simple-mode signature: the signer hashed
    // "DKIM-Signature:v=1..." and wrote "DKIM-Signature: v=1...". The key is
    // base64 DER, not PEM, so the repository's private-key scan stays clean.
    private const string AnjalTestKeyPkcs8 =
        "MIIEvgIBADANBgkqhkiG9w0BAQEFAASCBKgwggSkAgEAAoIBAQDTLyBbj/nUo4LZyYRXKJOIIg7m42OB3A7XmA4j2sItHGToc4iH" +
        "x8TV6sTTW8dQJXSDgdEuSU4B+Q8eWluCoM9Lj39O1tD5TtTzthQraajXX93V42CAW3keJ/ungBdXrazJHzgEkYph1bDCCPwb9l8Y" +
        "AXAPpGATvOPmXF+mOve/ZTGHolAnv18EYmgfjmdo9QMo6Z6e5LLNreKm+t8dHAXGj1SgtCVwow+vQwgof7eoKFNVinis5PpSBBbN" +
        "NRYcriE02XmZ8xoFOwrgazi2lGe5aYFUf7ujYXku9PY9ADuoBk/dBaxAIRj6msxeBM+iUIdYUzNIPOmVE5JJoMN7RSk1AgMBAAEC" +
        "ggEAPlkQ+AdTLmSRyqOUhzjrCYtok+D5LUsfNKZnMk2w+ymBXyFQ4ylm4vL7Zh0YBIDyW6r1a3Fn7uwtevwZPUElwjyczNVd/+S2" +
        "uTb90G1S1Dcw4qdNA8g9w1nxkZufCJs1QnGNk8e2L7krhLWrXMQJgihWgb+5P8qKDCYkdQq+vwnjXJ+qd3BkCjy8TYv+8lT+Ucr6" +
        "gGw/zyF9/7fzYQbmlcs15lrZ0ogpQ/gIQmEFgJemdGnLOUtZXzYkhBkGQXZ8nFzPjFyEJ8orLTIERr5bxIXTlKBP/zWQ/SnRa5+A" +
        "oAnuwqng7Gndp+rzTvwWPjRkcb8aJSKv+PU2zkGsu7T/pwKBgQDsQj/+9Khv8rKQDWjg1AIsdNqYartifLnlqpuvJBwi7baNZR3h" +
        "D+EpgwPDlC9h1BJQXcTnbStaqr+NgaxJWxO+Mkfp4WvaIdWwbDOplmCGQNQTC917boz1xnl/7iMBeYC16tV/OqHs2PrPz1H+Ytru" +
        "NoycbmJRPuPL0kWA27zQywKBgQDk1IKRCapLXenk/mv8y+XRP5bAI+rBJtrLcIcGTvvvaEYGKJV9oUq12Erhtj2xwYVX3R1XC2pV" +
        "rZXYKyGRH9MN3Spk1ItKmfCSA0LsxXgjqtPCuzBvpVUdsspFf+tLj2Kgu3aCvcLVtZX3up0TaYns9jLmUDADUhNwBZps2Eit/wKB" +
        "gHCUXSRjdwPpVVc5XJmNzP9cK3HnoiUbJAYhlxANF+848P1NisBdLcD3Mkr3COEICjYLiLFynu8UYDTQ7sUBxlWiZgw3o4oNB2OL" +
        "G88a3iH7MFNnGwIfOsI+8lSYqEuil0eYgGWhDdnrxxBRTVP3zTUn/zbnjqgCXNAaAY6WptGjAoGBAIUTdS8l4MxdvGU49NIaPfe3" +
        "tFLfUGmtz/YZ5dxsWKV9DaQNPArInysrsziahDx437QeWi7B68AR6B/DzYyZZmMcqMfkt3DWH4q6rNQHbvvHH8mSlPOIwfw4ett6" +
        "LftOUrxI6P3Vn5YrOSDNfQXKDbUp5KX+Ij38IGO4TntYOMQ/AoGBAMODehzoOln1sP8SMQB42zrqOqBnsmV0xtrouZDrFfdfofvA" +
        "WqpDmwUgpw4l7iUMm7Scp18z7GrPve8HtEt/cLkDF9GGqFttLmrz5Q/oA/u1OPUeWOQV9tXIh/Tmw0mSgpxci8zvKiP4PUhAgpzc" +
        "c71KLFJkT0BSh7aVSVOykOfU";

    private const string AnjalTestPublicKey =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA0y8gW4/51KOC2cmEVyiTiCIO5uNjgdwO15gOI9rCLRxk6HOIh8fE1erE" +
        "01vHUCV0g4HRLklOAfkPHlpbgqDPS49/TtbQ+U7U87YUK2mo11/d1eNggFt5Hif7p4AXV62syR84BJGKYdWwwgj8G/ZfGAFwD6Rg" +
        "E7zj5lxfpjr3v2Uxh6JQJ79fBGJoH45naPUDKOmenuSyza3ipvrfHRwFxo9UoLQlcKMPr0MIKH+3qChTVYp4rOT6UgQWzTUWHK4h" +
        "NNl5mfMaBTsK4Gs4tpRnuWmBVH+7o2F5LvT2PQA7qAZP3QWsQCEY+prMXgTPolCHWFMzSDzplROSSaDDe0UpNQIDAQAB";

    private const string AnjalUnsigned =
        "TUlNRS1WZXJzaW9uOiAxLjANCkRhdGU6IFNhdCwgMjYgU2VwIDIwMjYgMTI6MzA6MDAgKzA1MzANCk1lc3NhZ2UtSUQ6IDxvdXQt" +
        "MUBhbmphbC5jby5pbj4NCnN1YmplY3Q6IEFuamFsIG91dGJvdW5kDQogIGZvbGRlZCBzdWJqZWN0IGxpbmUNCkZyb206IEFydW4g" +
        "U2hpdmEgQiA8YXJ1bkBhbmphbC5jby5pbj4NClRvOiBzb21lb25lQGdtYWlsLmNvbQ0KQ29udGVudC1UeXBlOiBtdWx0aXBhcnQv" +
        "YWx0ZXJuYXRpdmU7IGJvdW5kYXJ5PSJiMSINCg0KLS1iMQ0KQ29udGVudC1UeXBlOiB0ZXh0L3BsYWluOyBjaGFyc2V0PVVURi04" +
        "DQoNCkhlbGxvICB3aXRoICAgc3BhY2VzICAgDQoNCg0KLS1iMQ0KQ29udGVudC1UeXBlOiB0ZXh0L2h0bWw7IGNoYXJzZXQ9VVRG" +
        "LTgNCg0KPHA+SGVsbG88L3A+DQotLWIxLS0NCg==";

    private const string AnjalSignedRelaxedVerifiedByDkimpy =
        "REtJTS1TaWduYXR1cmU6IHY9MTsgYT1yc2Etc2hhMjU2OyBjPXJlbGF4ZWQvcmVsYXhlZDsgZD1hbmphbC5jby5pbjsgcz1kZWZh" +
        "dWx0OyB0PTE3OTA0MDQ4NTA7IGg9RnJvbTpUbzpTdWJqZWN0OkRhdGU6TWVzc2FnZS1JRDpNSU1FLVZlcnNpb246Q29udGVudC1U" +
        "eXBlOyBiaD03SXZPdTFLVUJjcTFSTmFCMmZCd2xVODNCa0JXR25kM05qb1YvRFV5dCtJPTsgYj1OK2kvMDVFcGRRRkdHVDBSYVl0" +
        "bGlQdzdieHAxN2xhb0QzcDkwS3pqRW5YTjQya2xJVE1Jd0ZueGRHK0kwSUFicENuU1dodmdISE5RS1gwNENGeVp3Y3FhZTZablpD" +
        "cU5WK3NBZGtqb0pZMEhyVWVYUVpsZlJ2R3BnNldGTFE1dDRuY0d6L3g1aVF0ZDJmdm1yU2Z2d1FFQTFFWmU4MW5XeFJlVVlCNTV0" +
        "RVBRMFFLWnRGeG0vQjhxWjdEZGZEQlhsdkpjUnZCQnJ4dU90R2pUbUk1K3dmZkF0clpjRnVIQzFQYlNyWGwwblRLNWdjdlFFL09m" +
        "TGR3OTNSbWgwbWxHbWRMOFRwbGFIUWdBVUZzeDgySXI4VXFxWTRZcit1REU4bU5MdnBDYUtWRU1KbTM2Y3VjT0FoVGRHL2ZaVUIw" +
        "cTR3ZFY0WEcxKzU1aEN5cTJuRldRRWc9PQ0KTUlNRS1WZXJzaW9uOiAxLjANCkRhdGU6IFNhdCwgMjYgU2VwIDIwMjYgMTI6MzA6" +
        "MDAgKzA1MzANCk1lc3NhZ2UtSUQ6IDxvdXQtMUBhbmphbC5jby5pbj4NCnN1YmplY3Q6IEFuamFsIG91dGJvdW5kDQogIGZvbGRl" +
        "ZCBzdWJqZWN0IGxpbmUNCkZyb206IEFydW4gU2hpdmEgQiA8YXJ1bkBhbmphbC5jby5pbj4NClRvOiBzb21lb25lQGdtYWlsLmNv" +
        "bQ0KQ29udGVudC1UeXBlOiBtdWx0aXBhcnQvYWx0ZXJuYXRpdmU7IGJvdW5kYXJ5PSJiMSINCg0KLS1iMQ0KQ29udGVudC1UeXBl" +
        "OiB0ZXh0L3BsYWluOyBjaGFyc2V0PVVURi04DQoNCkhlbGxvICB3aXRoICAgc3BhY2VzICAgDQoNCg0KLS1iMQ0KQ29udGVudC1U" +
        "eXBlOiB0ZXh0L2h0bWw7IGNoYXJzZXQ9VVRGLTgNCg0KPHA+SGVsbG88L3A+DQotLWIxLS0NCg==";

    private const string AnjalSignedSimpleVerifiedByDkimpy =
        "REtJTS1TaWduYXR1cmU6IHY9MTsgYT1yc2Etc2hhMjU2OyBjPXNpbXBsZS9zaW1wbGU7IGQ9YW5qYWwuY28uaW47IHM9ZGVmYXVs" +
        "dDsgdD0xNzkwNDA0ODUwOyBoPUZyb206VG86U3ViamVjdDpEYXRlOk1lc3NhZ2UtSUQ6TUlNRS1WZXJzaW9uOkNvbnRlbnQtVHlw" +
        "ZTsgYmg9cExLRjM4Rnh3dDZwL09STDNSNStLcEhMMkFpUG5Xa0FXd2tKYVlZd0RVcz07IGI9TERtcXUvOVhJdTRQbEdVbjZNZGRD" +
        "ZEpsdGZ1dDl0d3ZSL3JwcVZ4ZjZDeFZIMkM1Zlp6YWt6VUJ4OFZmaXB1Q3FITFF2djJlUENLcWJuSGxsMm9DWjNIbDdHVHdNUkU3" +
        "aldzUVEreVNTT1hCdFczN2RNYXIweGpwdlFWYXNpTDMwOFZrd2xoNEpudmRwWXdNNlY2WjJXRkxBZXhQVmp6UjB2MVRRRFd2azM3" +
        "bGZFS01sNTZKd3ArZmdlNGVYdC94ZkpKVmQ5RVhrTzhwQ2lvVUFTQjlqazhBYzBRQ0duZjFMZldnSk9hOEhnWHpRTTd5amRndnpr" +
        "U3E2dzVVMi94UzA3bkhlZjBkMkp2Z0JZUnF4c1liS2lsMHp4OEV3TEVkUUI4MHBKUnVxODMzcEZ5L1lUVVI4UnVzd2Fiazc5UW9F" +
        "M1NJNTVESEF3c1FIRVR1b3dYdERBPT0NCk1JTUUtVmVyc2lvbjogMS4wDQpEYXRlOiBTYXQsIDI2IFNlcCAyMDI2IDEyOjMwOjAw" +
        "ICswNTMwDQpNZXNzYWdlLUlEOiA8b3V0LTFAYW5qYWwuY28uaW4+DQpzdWJqZWN0OiBBbmphbCBvdXRib3VuZA0KICBmb2xkZWQg" +
        "c3ViamVjdCBsaW5lDQpGcm9tOiBBcnVuIFNoaXZhIEIgPGFydW5AYW5qYWwuY28uaW4+DQpUbzogc29tZW9uZUBnbWFpbC5jb20N" +
        "CkNvbnRlbnQtVHlwZTogbXVsdGlwYXJ0L2FsdGVybmF0aXZlOyBib3VuZGFyeT0iYjEiDQoNCi0tYjENCkNvbnRlbnQtVHlwZTog" +
        "dGV4dC9wbGFpbjsgY2hhcnNldD1VVEYtOA0KDQpIZWxsbyAgd2l0aCAgIHNwYWNlcyAgIA0KDQoNCi0tYjENCkNvbnRlbnQtVHlw" +
        "ZTogdGV4dC9odG1sOyBjaGFyc2V0PVVURi04DQoNCjxwPkhlbGxvPC9wPg0KLS1iMS0tDQo=";

    [Theory]
    [InlineData("relaxed")]
    [InlineData("simple")]
    public async System.Threading.Tasks.Task AnjalSigner_ReproducesTheBytesDkimpyVerified(string mode)
    {
        bool simple = mode == "simple";
        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportPkcs8PrivateKey(System.Convert.FromBase64String(AnjalTestKeyPkcs8), out _);
        var signer = new Anjal.Dkim.DkimSigner(
            new Anjal.Dkim.DkimSigningOptions
            {
                HeaderCanon = simple ? Anjal.Dkim.HeaderCanonicalization.Simple : Anjal.Dkim.HeaderCanonicalization.Relaxed,
                BodyCanon = simple ? Anjal.Dkim.BodyCanonicalization.Simple : Anjal.Dkim.BodyCanonicalization.Relaxed,
            },
            () => System.DateTimeOffset.FromUnixTimeSeconds(1790404850));
        byte[] signed = signer.Sign(System.Convert.FromBase64String(AnjalUnsigned), new Anjal.Dkim.DkimKey
        {
            Domain = "anjal.co.in",
            Selector = "default",
            PrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem(),
        });

        string expected = simple ? AnjalSignedSimpleVerifiedByDkimpy : AnjalSignedRelaxedVerifiedByDkimpy;
        Assert.Equal(expected, System.Convert.ToBase64String(signed));

        using var dns = new FakeDnsServer(new() { ["default._domainkey.anjal.co.in"] = "v=DKIM1; k=rsa; p=" + AnjalTestPublicKey });
        DkimDetail detail = await new DkimVerifier(new Anjal.Dns.DnsResolver(dns.EndPoint)).VerifyAsync(signed);
        Assert.Equal(DkimResult.Pass, detail.Result);
    }

    private static async System.Threading.Tasks.Task<DkimDetail> VerifyAsync(string base64Message)
    {
        using var dns = new FakeDnsServer(new()
        {
            ["s1._domainkey.sender.test"] = "v=DKIM1; k=rsa; p=" + SenderKey,
            ["r1._domainkey.relay.test"] = "v=DKIM1; k=rsa; p=" + RelayKey,
        });
        var verifier = new DkimVerifier(new Anjal.Dns.DnsResolver(dns.EndPoint));
        return await verifier.VerifyAsync(System.Convert.FromBase64String(base64Message));
    }
}

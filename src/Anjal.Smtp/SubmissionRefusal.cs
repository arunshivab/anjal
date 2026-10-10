namespace Anjal.Smtp;

/// <summary>A submission the server refused (rc.15, item 59).</summary>
/// <param name="Username">The user name that signed in, or tried to.</param>
/// <param name="Reason">Why, in a few words, for example "wrong password".</param>
/// <param name="RemoteAddress">The client's address.</param>
public sealed record SubmissionRefusal(string Username, string Reason, string RemoteAddress);

namespace Anjal.Webmail.Services;

/// <summary>One section of a legal text.</summary>
/// <param name="Heading">Its heading.</param>
/// <param name="Paragraphs">Its paragraphs, plain text.</param>
public sealed record LegalSection(string Heading, IReadOnlyList<string> Paragraphs);

/// <summary>
/// The Terms of Service and the Disclaimer (rc.22, item 6): a sample, written for the owner to
/// have vetted by a lawyer before go-live. Shown at /terms and /disclaimer, sent in the Terms
/// email, and accepted at registration or at the first sign-in. The version changes whenever the
/// text does, so who accepted which text is on record.
/// </summary>
public static class LegalText
{
    /// <summary>The version of the text below. Change it when the text changes.</summary>
    public const string Version = "2026-10-18-draft";

    /// <summary>The provider's name as written in the text, until the company is formed.</summary>
    public const string Provider = "Anjal (the provider)";

    /// <summary>The Terms of Service.</summary>
    public static readonly IReadOnlyList<LegalSection> Terms = new[]
    {
        new LegalSection("1. What Anjal is", new[]
        {
            "Anjal provides a mailbox at an address on a domain served by Anjal, and sends and receives electronic mail on your behalf. It is offered by " + Provider + ", whose registered name and address will be stated here once the company is formed.",
            "Anjal is not a medical records system and not a records-management system. Where an organisation uses Anjal to carry clinical or other regulated information, that organisation remains responsible for that information and for the rules that apply to it.",
        }),
        new LegalSection("2. Who may have an account", new[]
        {
            "An account is for one named person aged 18 or over. You confirm that the date of birth you give is true. An account found to belong to a person under 18 is closed.",
            "An organisation's accounts are made by that organisation's administrator, who chooses the username. An individual's account is made only by Anjal's user management, and an individual account is never moved into an organisation.",
        }),
        new LegalSection("3. Your account", new[]
        {
            "You keep your password to yourself and turn on two-step sign-in where it is offered. You are responsible for what is done from your account until you tell Anjal, or your organisation's administrator, that someone else has access to it.",
            "Anjal may ask you to change your password, and may sign you out of devices, to protect the account.",
        }),
        new LegalSection("4. Acceptable use", new[]
        {
            "You will not use Anjal to send unsolicited bulk mail, to deceive, to harass, to carry programs or scripts meant to harm, or to break any law of India or of the place where the recipient is.",
            "Anjal may refuse, hold or discard mail that breaks these rules, and may suspend an account used to break them. Where it can, Anjal tells you what it did and why.",
        }),
        new LegalSection("5. Your mail and your data", new[]
        {
            "Your mail belongs to you. Anjal's staff do not read it, and an organisation's administrators cannot read it; what administrators can see is listed in the console and written to an activity log.",
            "Every message received or sent is kept exactly as received or sent, as legal evidence, for the period set by your organisation (three years by default) and then erased. This copy exists so that disputes can be settled on what was actually sent; it is not used for anything else.",
            "Anjal keeps the personal data needed to run your account: your name, phone number, date of birth, personal email address, and sign-in records. Under the Digital Personal Data Protection Act, 2023, you may download everything Anjal holds about you, and you may ask for your account to be deleted, from Settings. A deleted address is never given to anyone else.",
            "Anjal is hosted in India. Backups may be kept with a second provider; the backup location is stated in the operator's documents.",
        }),
        new LegalSection("6. Availability", new[]
        {
            "Anjal is run with care but without a guarantee of uninterrupted service. Planned maintenance is announced in advance where possible. Delivery to another provider depends on that provider accepting the mail; Anjal reports what happened to each message but cannot promise that every provider will accept it.",
        }),
        new LegalSection("7. Fees", new[]
        {
            "The service is free for the period stated when the account was made. Fees after that period, if any, are announced at least sixty days before they apply, and an account may be closed before then at no cost.",
        }),
        new LegalSection("8. Ending the service", new[]
        {
            "You may close your account at any time from Settings. Anjal may close an account for breach of these terms, for non-payment after notice, or when the service ends, giving at least thirty days' notice except where the breach is serious.",
            "When an account is closed, its mail and files are erased within thirty days; evidence copies are kept for their period and then erased.",
        }),
        new LegalSection("9. Liability", new[]
        {
            "To the extent permitted by law, the provider's liability for any loss arising from the service is limited to the fees paid for the twelve months before the loss, and the provider is not liable for indirect loss, loss of profit or loss of data that was not also kept elsewhere by you.",
            "Nothing in these terms limits liability that cannot be limited under Indian law.",
        }),
        new LegalSection("10. Changes and law", new[]
        {
            "These terms may change. A changed version is sent to your personal email address and shown at your next sign-in, where you are asked to accept it; you may close the account instead.",
            "These terms are governed by the laws of India. The courts at the provider's registered office (to be stated once the company is formed) have jurisdiction.",
        }),
    };

    /// <summary>The Disclaimer.</summary>
    public static readonly IReadOnlyList<LegalSection> Disclaimer = new[]
    {
        new LegalSection("Disclaimer", new[]
        {
            "Anjal is provided as it is. The provider does not warrant that the service will be free of errors or interruptions, that mail will reach every recipient, or that mail from every sender will be received.",
            "Anjal checks incoming mail for spam and for viruses with the tools stated in its documentation. No check catches everything: a message that passes the checks may still be harmful, and a message that fails them may still be genuine. You remain responsible for what you open and what you send.",
            "Anjal is not a medical records system. Clinical information sent by mail is carried as mail; the organisation sending it remains responsible for whether it may be sent, to whom, and how it is protected on the receiving side.",
            "Lajna, Anjal's helper, drafts and answers from rules, not from a language model, and may be wrong. Read what it writes before you send it.",
            "Figures, dashboards and reputation checks in Anjal are drawn from Anjal's own records and from public services; they are an aid, not a guarantee.",
            "This text is a draft awaiting legal review.",
        }),
    };
}

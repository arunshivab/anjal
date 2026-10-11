namespace Anjal.Webmail.Services;

/// <summary>One mail template: a covering mail, never a form (item 62).</summary>
/// <param name="Key">A stable key, for example "work-hr/leave-manager".</param>
/// <param name="Group">The group it is listed under.</param>
/// <param name="Name">Its name in the list.</param>
/// <param name="Subject">The subject, with blanks such as {Name}.</param>
/// <param name="Body">The text, with blanks; lines separated by \n.</param>
/// <param name="Background">A suggested mail background (a picture's name), or empty.</param>
public sealed record MailTemplate(string Key, string Group, string Name, string Subject, string Body, string Background = "");

/// <summary>
/// Anjal's starter set (SPEC-11 item 62, "Template catalogue"): 62 templates
/// in 9 groups. Blanks in braces are filled where Anjal knows them - {Name}
/// from the first recipient's contact, {Your name} from the profile,
/// {Organisation} from the organisation - and asked for otherwise. The
/// English set is here; the other five languages follow the owner's check
/// of the translations, like every other word on the screens.
/// </summary>
public static class TemplateCatalogue
{
    /// <summary>The festivals offered for "Festival wishes", with the line each brings.</summary>
    public static readonly IReadOnlyList<(string Name, string Line)> Festivals = new[]
    {
        ("Diwali", "May the festival of lights bring you joy, good health and prosperity."),
        ("Pongal", "May the harvest festival fill your home with plenty and your year with sweetness."),
        ("Onam", "May Onam bring you a full harvest of happiness and togetherness."),
        ("Eid", "May this Eid bring peace, blessings and happiness to you and your family."),
        ("Christmas", "May the season bring you peace, warmth and time with the people you love."),
        ("New Year", "May the new year bring you good health, new beginnings and many reasons to smile."),
        ("Holi", "May the festival of colours fill your life with joy and bright days."),
        ("Navratri", "May the nine nights bring you strength, devotion and happiness."),
        ("Guru Nanak Jayanti", "May the teachings of Guru Nanak bring you peace and light."),
        ("Ugadi", "May the new year bring you a good balance of everything sweet in life."),
    };

    /// <summary>The groups, in the order shown.</summary>
    public static readonly IReadOnlyList<string> Groups = new[]
    {
        "Work: people and HR", "Work: meetings and projects", "Work: customers and suppliers", "School and children",
        "Bank and money", "Complaints about services", "Government and official", "Greetings", "Invitations",
    };

    /// <summary>Every template, by group.</summary>
    public static readonly IReadOnlyList<MailTemplate> All = Build();

    /// <summary>Find a template by its key.</summary>
    /// <param name="key">The key.</param>
    public static MailTemplate? Find(string? key) => All.FirstOrDefault(t => t.Key == key);

    /// <summary>The blanks a text uses, in the order they first appear, each once.</summary>
    /// <param name="text">The text.</param>
    public static IReadOnlyList<string> BlanksIn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<string>();
        int i = 0;
        while ((i = text.IndexOf('{', i)) >= 0)
        {
            int end = text.IndexOf('}', i + 1);
            if (end < 0)
            {
                break;
            }
            string blank = text[(i + 1)..end];
            if (blank.Length > 0 && blank.Length <= 40 && !blank.Contains('{', StringComparison.Ordinal) && !found.Contains(blank))
            {
                found.Add(blank);
            }
            i = end + 1;
        }
        return found;
    }

    /// <summary>Fill blanks; a blank with no value is left as written, so it is plain to see.</summary>
    /// <param name="text">The text.</param>
    /// <param name="values">The values, by blank name.</param>
    public static string Fill(string text, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(values);
        string result = text;
        foreach (KeyValuePair<string, string> kv in values)
        {
            if (kv.Value.Trim().Length > 0)
            {
                result = result.Replace("{" + kv.Key + "}", kv.Value.Trim(), StringComparison.Ordinal);
            }
        }
        return result;
    }

    private static List<MailTemplate> Build()
    {
        var t = new List<MailTemplate>();
        void Add(string group, string key, string name, string subject, string body, string background = "") =>
            t.Add(new MailTemplate(key, group, name, subject, body, background));

        string g = Groups[0];
        Add(g, "hr/job-application", "Job application", "Application for the post of {Post}",
            "Dear {Name},\n\nI am writing to apply for the post of {Post} at {Their organisation}, as advertised on {Where you saw it}. My CV is attached.\n\nI have {Years} years of experience in {Field}, most recently as {Current role}. I would welcome the chance to discuss how I can contribute to your team.\n\nThank you for considering my application.\n\nYours sincerely,\n{Your name}\n{Your phone}");
        Add(g, "hr/resignation", "Resignation", "Resignation from the post of {Post}",
            "Dear {Name},\n\nPlease accept this mail as formal notice of my resignation from the post of {Post}. In line with my notice period, my last working day will be {Last day}.\n\nI am grateful for the opportunities I have had at {Organisation}, and I will do everything I can to hand over my work smoothly.\n\nWith thanks,\n{Your name}");
        Add(g, "hr/leave-manager", "Leave request to my manager", "Leave request: {From date} to {To date}",
            "Dear {Name},\n\nI would like to request leave from {From date} to {To date} ({Days} days) for {Reason}.\n\nDuring this time, {Colleague} has agreed to look after {What}. I will make sure everything urgent is finished before I go.\n\nPlease let me know if this is acceptable.\n\nThank you,\n{Your name}");
        Add(g, "hr/leave-department", "Leave information to my department", "On leave: {From date} to {To date}",
            "Dear all,\n\nI will be on leave from {From date} to {To date}. In my absence, please contact {Colleague} for anything urgent.\n\nI will reply to other mail when I return on {Return date}.\n\nThank you,\n{Your name}");
        Add(g, "hr/joining", "Joining announcement", "Welcome {New colleague} to {Team}",
            "Dear all,\n\nPlease join me in welcoming {New colleague}, who joins {Team} as {Role} from {Date}.\n\n{New colleague} comes to us from {Previous place} and will be working on {Work}. Do drop by and say hello.\n\nRegards,\n{Your name}");
        Add(g, "hr/farewell", "Farewell message", "Thank you and goodbye",
            "Dear colleagues,\n\nToday is my last day at {Organisation}. Thank you for your support, your patience and the many good conversations over the years.\n\nI will miss working with you. You can stay in touch with me at {Personal address}.\n\nWith warm wishes,\n{Your name}");
        Add(g, "hr/relieving-letter", "Relieving letter request", "Request for my relieving letter",
            "Dear {Name},\n\nMy last working day was {Last day}. I would be grateful if my relieving letter could be issued, and sent to this address.\n\nMy employee number is {Employee number}.\n\nThank you,\n{Your name}");
        Add(g, "hr/experience-certificate", "Experience certificate request", "Request for an experience certificate",
            "Dear {Name},\n\nI would like to request an experience certificate for my service at {Organisation} from {Joining date} to {Last day}, as {Post}.\n\nIt is needed for {Purpose}. Please let me know if anything else is required from me.\n\nThank you,\n{Your name}");
        Add(g, "hr/work-from-home", "Work from home request", "Request to work from home on {Date}",
            "Dear {Name},\n\nI would like to work from home on {Date} because {Reason}. I will be available on phone and mail during working hours, and my planned work for the day is {Work}.\n\nPlease let me know if that is fine.\n\nThank you,\n{Your name}");
        Add(g, "hr/reference", "Reference request", "Would you be a reference for me?",
            "Dear {Name},\n\nI am applying for the post of {Post} at {Their organisation}, and I would be grateful if you would act as a reference for me, given our work together on {Work}.\n\nThey may contact you by {When}. I am happy to share my CV and the job description.\n\nThank you for considering it,\n{Your name}");

        g = Groups[1];
        Add(g, "meet/invitation", "Meeting invitation", "Meeting: {Topic} on {Date}",
            "Dear {Name},\n\nI would like to invite you to a meeting on {Topic}.\n\nDate: {Date}\nTime: {Time}\nPlace: {Place}\n\nThe agenda is {Agenda}. Please let me know if you can attend.\n\nRegards,\n{Your name}");
        Add(g, "meet/reschedule", "Meeting reschedule", "New time for {Topic}",
            "Dear {Name},\n\nThe meeting on {Topic}, planned for {Old date}, has moved to {New date} at {Time}, in {Place}.\n\nSorry for the change. Please let me know if the new time does not suit you.\n\nRegards,\n{Your name}");
        Add(g, "meet/minutes", "Meeting minutes", "Minutes: {Topic}, {Date}",
            "Dear all,\n\nPlease find the minutes of the meeting on {Topic}, held on {Date}.\n\nDecisions:\n- {Decision}\n\nActions:\n- {Action} (by {Person}, due {Due date})\n\nPlease send corrections by {Date for corrections}.\n\nRegards,\n{Your name}");
        Add(g, "meet/follow-up", "Follow-up after a meeting", "Following up: {Topic}",
            "Dear {Name},\n\nThank you for your time today. As agreed, {Next step}.\n\nI will {What you will do} by {Date}. Please let me know if I have missed anything.\n\nRegards,\n{Your name}");
        Add(g, "meet/status", "Project status update", "{Project}: status for the week of {Week}",
            "Dear {Name},\n\nHere is this week's update on {Project}.\n\nDone: {Done}\nNext: {Next}\nNeeds attention: {Risk}\n\nOverall we are {On track or not}.\n\nRegards,\n{Your name}");
        Add(g, "meet/deadline", "Deadline reminder", "Reminder: {Task} due {Due date}",
            "Dear {Name},\n\nA gentle reminder that {Task} is due on {Due date}. If anything is holding it up, please tell me so we can sort it out together.\n\nThank you,\n{Your name}");
        Add(g, "meet/handover", "Handover note before leave", "Handover while I am away ({From date} to {To date})",
            "Dear {Name},\n\nThank you for covering for me from {From date} to {To date}. Here is what is open:\n\n- {Item}: {Status}, contact {Contact}\n\nFiles are in {Where}. I will be back on {Return date}.\n\nWith thanks,\n{Your name}");

        g = Groups[2];
        Add(g, "cust/quotation", "Quotation request", "Request for a quotation: {Item}",
            "Dear {Name},\n\nWe would like a quotation for {Item}, quantity {Quantity}, delivered to {Place} by {Date}.\n\nPlease include taxes, delivery charges and your payment terms.\n\nRegards,\n{Your name}\n{Organisation}");
        Add(g, "cust/po-follow-up", "Purchase order follow-up", "Purchase order {PO number}: delivery date",
            "Dear {Name},\n\nWe sent purchase order {PO number} on {Date}. Could you please confirm the delivery date?\n\nRegards,\n{Your name}\n{Organisation}");
        Add(g, "cust/payment-reminder", "Payment reminder", "Payment reminder: invoice {Invoice number}",
            "Dear {Name},\n\nThis is a reminder that invoice {Invoice number} for {Amount}, dated {Invoice date}, was due on {Due date}. If you have already paid, please ignore this mail and accept our thanks.\n\nRegards,\n{Your name}\n{Organisation}");
        Add(g, "cust/invoice", "Invoice submission", "Invoice {Invoice number} for {Work}",
            "Dear {Name},\n\nPlease find invoice {Invoice number} for {Work} attached, for {Amount}, payable by {Due date}.\n\nBank details are on the invoice. Please let me know if you need anything else.\n\nRegards,\n{Your name}\n{Organisation}");
        Add(g, "cust/supplier-complaint", "Complaint to a supplier", "Problem with order {Order number}",
            "Dear {Name},\n\nWe received order {Order number} on {Date}, and {Problem}.\n\nPlease {What you want} by {Date to fix}. Photographs are attached.\n\nRegards,\n{Your name}\n{Organisation}");
        Add(g, "cust/thanks", "Thank you to a customer", "Thank you, {Name}",
            "Dear {Name},\n\nThank you for choosing {Organisation} for {Work}. It was a pleasure working with you.\n\nIf there is anything more we can do, please write to us at any time.\n\nWith thanks,\n{Your name}");

        g = Groups[3];
        Add(g, "school/leave", "Leave letter for my child", "Leave for {Child's name}, class {Class}",
            "Dear {Name},\n\nMy child {Child's name} of class {Class} will not be able to attend school from {From date} to {To date} because of {Reason}.\n\nPlease grant leave for these days. We will make sure the work missed is completed.\n\nThank you,\n{Your name}");
        Add(g, "school/meet-teacher", "Request to meet the class teacher", "Request to meet about {Child's name}",
            "Dear {Name},\n\nI would like to meet you to talk about {Child's name}'s {Topic}. Would {Date} at {Time} suit you? I can also come at another time convenient to you.\n\nThank you,\n{Your name}");
        Add(g, "school/fee-receipt", "Fee receipt request", "Fee receipt for {Child's name}",
            "Dear {Name},\n\nI paid the fees for {Child's name}, class {Class}, for {Term} on {Date}. Could you please send the receipt to this address?\n\nThank you,\n{Your name}");
        Add(g, "school/tc", "Transfer certificate request", "Transfer certificate for {Child's name}",
            "Dear {Name},\n\nWe are moving to {New city}, and {Child's name} of class {Class} will join a new school from {Date}. Please issue the transfer certificate.\n\nThank you for everything the school has done for our child.\n\nRegards,\n{Your name}");
        Add(g, "school/bus", "School bus route change", "Bus route change for {Child's name}",
            "Dear {Name},\n\nFrom {Date}, {Child's name} of class {Class} will need to be picked up from {New stop} instead of {Old stop}.\n\nPlease let me know if this is possible.\n\nThank you,\n{Your name}");
        Add(g, "school/illness", "Informing the school of an illness", "{Child's name} is unwell",
            "Dear {Name},\n\n{Child's name} of class {Class} is unwell with {Illness} and will stay home until {Return date}, on the doctor's advice.\n\nWe will share the medical certificate when they return.\n\nThank you,\n{Your name}");

        g = Groups[4];
        Add(g, "bank/open-account", "Application for opening an account (form attached)", "Application to open a {Account type} account",
            "Dear Sir or Madam,\n\nPlease find attached my completed application form to open a {Account type} account at your {Branch} branch, with copies of my identity and address documents.\n\nPlease let me know if anything else is needed.\n\nYours faithfully,\n{Your name}\n{Your phone}");
        Add(g, "bank/address-change", "Change of address (form attached)", "Change of address for account ending {Last digits}",
            "Dear Sir or Madam,\n\nI have moved, and I request that the address on my account ending {Last digits} be changed. The completed form and proof of my new address are attached.\n\nYours faithfully,\n{Your name}");
        Add(g, "bank/kyc", "KYC documents (attached)", "KYC documents for account ending {Last digits}",
            "Dear Sir or Madam,\n\nAs requested, my KYC documents for the account ending {Last digits} are attached: {Documents}.\n\nPlease confirm once they are updated.\n\nYours faithfully,\n{Your name}");
        Add(g, "bank/statement", "Statement request", "Statement for {From date} to {To date}",
            "Dear Sir or Madam,\n\nPlease send the statement of my account ending {Last digits} for {From date} to {To date} to this address.\n\nYours faithfully,\n{Your name}");
        Add(g, "bank/cheque-book", "Cheque book request", "Cheque book for account ending {Last digits}",
            "Dear Sir or Madam,\n\nPlease issue a new cheque book for my account ending {Last digits}, to be sent to my registered address.\n\nYours faithfully,\n{Your name}");
        Add(g, "bank/block-card", "Lost or stolen card: block it", "Urgent: please block my card ending {Last digits}",
            "Dear Sir or Madam,\n\nMy card ending {Last digits} was lost or stolen on {Date}. Please block it at once and issue a replacement.\n\nI have also called your helpline. Please confirm when the card is blocked.\n\nYours faithfully,\n{Your name}\n{Your phone}");
        Add(g, "bank/dispute", "Disputed transaction", "Disputed transaction of {Amount} on {Date}",
            "Dear Sir or Madam,\n\nI do not recognise a transaction of {Amount} on {Date} on my account ending {Last digits}, described as {Description}.\n\nPlease investigate and reverse it. I did not authorise it.\n\nYours faithfully,\n{Your name}\n{Your phone}");

        g = Groups[5];
        Add(g, "complain/isp", "Internet service provider", "No internet since {Date}: account {Account number}",
            "Dear Sir or Madam,\n\nMy internet connection (account {Account number}) has not worked since {Date}. I have restarted the equipment and the problem continues.\n\nPlease send a technician and tell me when to expect them.\n\nRegards,\n{Your name}\n{Your phone}");
        Add(g, "complain/mobile", "Mobile phone operator", "Problem with my mobile number {Number}",
            "Dear Sir or Madam,\n\nSince {Date}, my number {Number} has {Problem}. Please resolve this and confirm by reply.\n\nRegards,\n{Your name}");
        Add(g, "complain/electricity", "Electricity supply", "Power problem at {Address}, consumer number {Consumer number}",
            "Dear Sir or Madam,\n\nThere has been {Problem} at {Address} (consumer number {Consumer number}) since {Date}.\n\nPlease attend to it urgently and let me know the expected time.\n\nRegards,\n{Your name}\n{Your phone}");
        Add(g, "complain/water", "Water supply", "Water supply problem at {Address}",
            "Dear Sir or Madam,\n\nThe water supply at {Address} has been {Problem} since {Date}. Please look into it.\n\nRegards,\n{Your name}\n{Your phone}");
        Add(g, "complain/courier", "Courier or delivery", "Delivery {Tracking number} not received",
            "Dear Sir or Madam,\n\nThe shipment {Tracking number}, due on {Date}, has not reached me, though it shows as {Status}.\n\nPlease find out where it is and deliver it, or return it to the sender.\n\nRegards,\n{Your name}\n{Your phone}");
        Add(g, "complain/refund", "Online order refund", "Refund for order {Order number}",
            "Dear Sir or Madam,\n\nI returned order {Order number} on {Date}, and the refund of {Amount} has not reached me.\n\nPlease process it and confirm the date.\n\nRegards,\n{Your name}");
        Add(g, "complain/society", "Housing society", "{Problem} in {Building}",
            "Dear {Name},\n\nI would like to bring to the committee's notice that {Problem} in {Building}, since {Date}.\n\nPlease take it up at the earliest.\n\nRegards,\n{Your name}\nFlat {Flat number}");

        g = Groups[6];
        Add(g, "gov/rti", "RTI application (attached)", "Application under the RTI Act, 2005",
            "Dear Public Information Officer,\n\nPlease find attached my application under the Right to Information Act, 2005, seeking information on {Subject}. The fee of {Fee} is paid by {How paid}.\n\nYours faithfully,\n{Your name}\n{Your address}");
        Add(g, "gov/certificate", "Certificate application (attached)", "Application for a {Certificate}",
            "Dear Sir or Madam,\n\nPlease find attached my application for a {Certificate}, with the supporting documents: {Documents}.\n\nPlease let me know if anything more is needed.\n\nYours faithfully,\n{Your name}\n{Your phone}");
        Add(g, "gov/address-change", "Address change application (attached)", "Change of address in {Record}",
            "Dear Sir or Madam,\n\nI request that my address in {Record} be changed to my new address. The application and proof of address are attached.\n\nYours faithfully,\n{Your name}");
        Add(g, "gov/grievance", "Grievance to the municipal office", "Grievance: {Problem} at {Place}",
            "Dear Sir or Madam,\n\nI wish to report {Problem} at {Place}, which has continued since {Date} and affects {Who}.\n\nPlease take action. Photographs are attached.\n\nYours faithfully,\n{Your name}\n{Your phone}");
        Add(g, "gov/follow-up", "Follow-up on a pending application", "Status of my application {Application number}",
            "Dear Sir or Madam,\n\nI applied for {What} on {Date} (application {Application number}) and have not heard back. Could you please tell me its status?\n\nYours faithfully,\n{Your name}");

        g = Groups[7];
        Add(g, "greet/festival", "Festival wishes", "Happy {Festival}, {Name}",
            "Dear {Name},\n\nWishing you and your family a very happy {Festival}. {Festival line}\n\nWarm regards,\n{Your name}", "Kolam");
        Add(g, "greet/birthday", "Birthday", "Happy birthday, {Name}!",
            "Dear {Name},\n\nWishing you a very happy birthday. May the year ahead bring you good health, happiness and everything you hope for.\n\nWarm wishes,\n{Your name}", "Confetti");
        Add(g, "greet/work-anniversary", "Work anniversary", "Happy work anniversary, {Name}",
            "Dear {Name},\n\nCongratulations on completing {Years} years with {Organisation}. Thank you for all you bring to the team.\n\nBest wishes,\n{Your name}");
        Add(g, "greet/wedding", "Wedding wishes", "Congratulations on your wedding",
            "Dear {Name},\n\nCongratulations to you and {Partner's name} on your wedding. Wishing you a lifetime of love and happiness together.\n\nWith warm wishes,\n{Your name}", "Leaves");
        Add(g, "greet/congratulations", "Congratulations", "Congratulations, {Name}!",
            "Dear {Name},\n\nCongratulations on {Achievement}. It is well deserved.\n\nBest wishes,\n{Your name}");
        Add(g, "greet/new-baby", "New baby", "Congratulations on your little one",
            "Dear {Name},\n\nCongratulations on the arrival of your baby. Wishing your family every happiness.\n\nWarm wishes,\n{Your name}");
        Add(g, "greet/get-well", "Get well soon", "Get well soon, {Name}",
            "Dear {Name},\n\nI was sorry to hear you are unwell. Wishing you a quick and full recovery. Take care, and do not worry about work.\n\nWarm wishes,\n{Your name}");
        Add(g, "greet/condolence", "Condolence", "With deepest sympathy",
            "Dear {Name},\n\nI am deeply sorry to hear of the passing of {Person}. My thoughts are with you and your family.\n\nIf there is anything I can do, please tell me.\n\nWith sympathy,\n{Your name}");
        Add(g, "greet/thank-you", "Thank you", "Thank you, {Name}",
            "Dear {Name},\n\nThank you so much for {What}. It meant a great deal.\n\nWith thanks,\n{Your name}");

        g = Groups[8];
        Add(g, "invite/event", "Event invitation", "You are invited: {Event} on {Date}",
            "Dear {Name},\n\nYou are invited to {Event}.\n\nDate: {Date}\nTime: {Time}\nPlace: {Place}\n\nPlease reply by {Reply by} to let us know if you can come.\n\nWarm regards,\n{Your name}");
        Add(g, "invite/wedding", "Wedding invitation", "Wedding of {Couple}",
            "Dear {Name},\n\nWith great joy, we invite you to the wedding of {Couple} on {Date} at {Place}. The ceremony begins at {Time}.\n\nYour presence and blessings would mean a great deal to us.\n\nWith warm regards,\n{Your name}", "Kolam");
        Add(g, "invite/housewarming", "Housewarming", "Housewarming on {Date}",
            "Dear {Name},\n\nWe have moved into our new home and would love to celebrate with you at our housewarming on {Date} at {Time}.\n\nAddress: {Address}\n\nWarm regards,\n{Your name}");
        Add(g, "invite/birthday-party", "Birthday party", "{Person}'s birthday party on {Date}",
            "Dear {Name},\n\nPlease join us to celebrate {Person}'s birthday on {Date} at {Time}, at {Place}.\n\nDo let us know if you can come.\n\nWarm regards,\n{Your name}", "Confetti");
        Add(g, "invite/team-outing", "Team outing", "Team outing on {Date}",
            "Dear all,\n\nWe are planning a team outing to {Place} on {Date}, leaving at {Time}. Please reply by {Reply by} so we can plan travel and food.\n\nRegards,\n{Your name}");
        return t;
    }
}

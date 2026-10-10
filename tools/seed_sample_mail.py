#!/usr/bin/env python3
"""Fill a local test mailbox with realistic sample mail (rc.11, D-112).

For local testing only - never against a live server. Works through Anjal's
own front doors: the admin API creates the tenant, domain and mailbox when
missing and sets the password; every message is delivered by SMTP, so it
passes through Anjal's real checks (spam score, Junk, sender checks).
Optionally spreads arrival times over the last ten days (--backdate-db),
so the day groups, "Yesterday" and the dashboard have something to show.

The local server needs greylisting off and DNS scoring off:
    ANJAL_GREYLIST=false  ANJAL_SPAM_DNS=false  ANJAL_API_PORT=5081

Usage:
    python tools/seed_sample_mail.py --token <API token> --password "<new password>"
        [--address arun@qa.test] [--tenant qa] [--smtp 127.0.0.1:2525]
        [--api http://127.0.0.1:5081] [--backdate-db anjal_qa] [--only "Equipment policy"]
"""

import argparse
import base64
import json
import os
import smtplib
import subprocess
import sys
import urllib.error
import urllib.request
from email.message import EmailMessage
from email.utils import formataddr, format_datetime
from datetime import datetime, timedelta, timezone

IST = timezone(timedelta(hours=5, minutes=30))

PEOPLE = {
    "quality": ("Quality committee", "quality@{d}"),
    "accounts": ("Accounts", "accounts@{d}"),
    "meera": ("Meera Iyer", "meera.iyer@{d}"),
    "ravi": ("Ravi Kumar", "ravi.kumar@{d}"),
    "it": ("IT service desk", "it.desk@{d}"),
    "board": ("Board office", "board@{d}"),
    "hr": ("Human resources", "hr@{d}"),
    "lab": ("Results service", "results@lab-partner.example"),
    "training": ("Training team", "training@{d}"),
    "priya": ("பிரியா ராமன்", "priya.raman@{d}"),
    "anil": ("അനിൽ മേനോൻ", "anil.menon@kerala-clinic.example"),
    "suresh": ("सुरेश पाटील", "suresh.patil@pune-diagnostics.example"),
    "kinjal": ("કિંજલ શાહ", "kinjal.shah@ahmedabad-imaging.example"),
    "vendor": ("MedEquip Supplies", "orders@medequip.example"),
    "bank": ("Bank alerts", "alerts@bank.example"),
    "auditor": ("External auditor", "audit@firm.example"),
    "spam1": ("Lottery Winner Desk", "prize@win-big.example"),
    "spam2": ("Crypto Profits", "offers@quick-returns.example"),
    "news": ("Healthcare Weekly", "newsletter@hc-weekly.example"),
}

TINY_PDF = (b"%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n"
            b"3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 100]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n")
TINY_PNG = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==")


def messages(you, domain):
    """The sample set: (key, from, to, cc, subject, text, html, attachments, days_ago, hour)."""
    p = {k: (n, a.format(d=domain)) for k, (n, a) in PEOPLE.items()}
    many = [("Colleague %d" % i, "colleague%d@%s" % (i, domain)) for i in range(1, 25)] + \
           [("Ward sister %d" % i, "ward%d@partner-hospital.example" % i) for i in range(1, 6)]
    me = [("Arun", you)]
    out = []

    def add(frm, to, subject, text, cc=(), html=None, att=(), days=0, hour=9):
        out.append((p[frm], to, list(cc), subject, text, html, list(att), days, hour))

    add("quality", me + [p["ravi"]], "Agenda for Friday's review",
        "Dear colleagues,\n\nPlease find the updated agenda for Friday's quality review attached. Items 3 and 4 need decisions "
        "before the external audit, so please read the two short papers in advance.\n\nWe meet in the second-floor board room at 15:00.\n\nQuality committee",
        cc=[p["board"], p["auditor"]], att=[("Agenda-quality-review.pdf", TINY_PDF, "application/pdf")], days=0, hour=9)
    add("accounts", me, "Invoice 2026-114: payment due 15 October",
        "Hello,\n\nThe September invoice is attached for approval. Payment is due on 15 October.\n\nRegards,\nAccounts",
        att=[("Invoice-2026-114.pdf", TINY_PDF, "application/pdf")], days=0, hour=8)
    add("meera", me, "Revised duty roster for October",
        "Hi Arun,\n\nTwo changes from last week, marked in yellow in the attached roster. Please confirm by Thursday.\n\nThanks,\nMeera",
        att=[("Roster-October.pdf", TINY_PDF, "application/pdf")], days=0, hour=7)
    add("priya", me, "நாளை காலை 10 மணிக்கு கூட்டம்",
        "வணக்கம்,\n\nநாளை காலை 10 மணிக்கு வாராந்திர கூட்டம் நடைபெறும். அறிக்கையை முன்கூட்டியே படிக்கவும்.\n\nநன்றி,\nபிரியா", days=0, hour=6)
    add("it", me, "Planned maintenance, Saturday 22:00",
        "Systems will be unavailable for about forty minutes on Saturday from 22:00. Mail sent during that time will be delivered afterwards.", days=1, hour=18)
    add("board", me, "Minutes of the 30 September meeting",
        "Draft minutes for your corrections by Monday.", att=[("Minutes-30-Sep.pdf", TINY_PDF, "application/pdf")], days=1, hour=11)
    add("anil", me, "ഓണാശംസകൾ - യോഗം നാളെ രാവിലെ പത്തിന്",
        "നമസ്കാരം,\n\nയോഗം നാളെ രാവിലെ പത്തിന് നടക്കും. എല്ലാവർക്കും ഓണാശംസകൾ!\n\nഅനിൽ", days=1, hour=10)
    add("lab", me, "Your report is ready",
        "Sign in to the portal to view it. This message does not contain any results.", days=2, hour=12)
    add("training", many[:12] + me, "Fire safety refresher: book a slot",
        "Sessions every Thursday in October. Please book through the training calendar.", days=3, hour=10)
    add("suresh", me, "उद्या सकाळी दहा वाजता बैठक आहे",
        "नमस्कार,\n\nउद्या सकाळी दहा वाजता बैठक आहे. कृपया वेळेवर उपस्थित राहा.\n\nसुरेश", days=3, hour=16)
    add("hr", many + me, "Diwali holiday schedule - दीपावली की हार्दिक शुभकामनाएँ",
        "Dear all,\n\nThe hospital remains open through Diwali. The holiday rota is attached.\n\nदीपावली की हार्दिक शुभकामनाएँ!\n\nHuman resources",
        att=[("Holiday-rota.pdf", TINY_PDF, "application/pdf")], days=4, hour=9)
    add("kinjal", me, "આવતીકાલે સવારે 10 વાગ્યે મીટિંગ છે",
        "નમસ્તે,\n\nઆવતીકાલે સવારે 10 વાગ્યે મીટિંગ છે. દિવાળીની શુભકામનાઓ!\n\nકિંજલ", days=4, hour=15)
    add("vendor", me, "Order 7781 dispatched",
        "Your order of infusion pumps has been dispatched. Expected delivery: 9 October.",
        html="<p>Your order of <b>infusion pumps</b> has been dispatched.</p><table border='1' cellpadding='6'><tr><th>Item</th><th>Qty</th></tr>"
             "<tr><td>Infusion pump IP-200</td><td>4</td></tr><tr><td>Giving sets</td><td>200</td></tr></table><p>Expected delivery: <b>9 October</b>.</p>",
        days=5, hour=13)
    add("bank", me, "Account statement for September",
        "Your September statement is ready. For your security, we never ask for passwords by e-mail.", days=6, hour=7)
    add("news", me, "This week in healthcare: digital records, new tariffs",
        "Read this week's stories in your browser.",
        html="<h2 style='color:#0B4F7A'>Healthcare Weekly</h2><p><b>Digital records:</b> what changes for hospitals.</p>"
             "<p><b>New tariffs:</b> the revised schedule explained.</p><p><img src='https://hc-weekly.example/banner.png' alt='banner' width='300'></p>",
        days=6, hour=6)
    add("auditor", me, "Documents needed before the audit",
        "Please share the infection-control register, the equipment calibration log and the training records by 10 October.",
        cc=[p["quality"]], days=7, hour=14)
    add("ravi", me, "Re: Radiotherapy machine QA results",
        "Arun,\n\nThe monthly QA passed on all checks. Output within 0.8%. Full sheet attached.\n\nRavi",
        att=[("QA-results.png", TINY_PNG, "image/png")], days=8, hour=17)
    add("spam1", me, "CONGRATULATIONS!!! You have WON Rs 50,00,000",
        "Dear winner, claim your prize now! Send your bank details and a processing fee to receive your money today. Click here to claim!!!", days=2, hour=3)
    add("spam2", me, "Double your money in 7 days - guaranteed",
        "Invest now and get guaranteed 100% returns. Limited time offer. Click the link and send your password to verify.", days=5, hour=2)
    # rc.15 (owner, 7 Oct): one long letter, to try scrolling inside the letter pane.
    sections = [
        ("Purpose", "This policy sets out how the hospital keeps clinical equipment safe, accurate and ready for use, and who is responsible at each step."),
        ("Scope", "It covers every item of clinical equipment owned, leased or loaned to the hospital, in every ward, theatre, laboratory and outpatient area."),
        ("Inventory", "Each item carries an asset tag and an entry in the equipment register: make, model, serial number, location, owner department and service history."),
        ("Acceptance testing", "New equipment is checked by biomedical engineering before first use. Nothing is used on a patient until it has passed and been entered in the register."),
        ("Planned maintenance", "Every item has a maintenance interval set by its manufacturer or by risk. The schedule is published monthly and overdue items are reported to the department head."),
        ("Calibration", "Measuring equipment is calibrated against traceable standards at the intervals in Annex B. Certificates are kept for the life of the item plus three years."),
        ("Breakdowns", "A fault is reported at once through the helpdesk. The item is labelled out of use and removed from service until it is repaired and checked."),
        ("Recalls and alerts", "Manufacturer recalls and national safety alerts are acted on within the time they give. The action taken is recorded against each affected item."),
        ("Training", "Only staff trained on an item may use it. Training records are kept by each department and reviewed at appraisal."),
        ("Cleaning", "Equipment is cleaned between patients as its instructions require. Shared equipment carries a cleaned-on label with the date and initials."),
        ("Loans", "Loaned equipment is checked on arrival and before return. The lender's instructions travel with it."),
        ("Disposal", "Equipment at the end of its life is removed from the register, data is wiped where it holds any, and it is disposed of by an approved contractor."),
        ("Audit", "Biomedical engineering audits the register twice a year. Findings go to the quality committee with an action plan and dates."),
        ("Responsibilities", "Department heads own their equipment; biomedical engineering maintains it; users report faults and keep it clean; the quality committee oversees the whole."),
    ]
    long_text = "Dear colleagues,\n\nPlease read the revised equipment policy below before Friday's review. Changes from the last version are in sections 5, 6 and 8.\n\n"
    long_html = "<p>Dear colleagues,</p><p>Please read the revised equipment policy below before Friday's review. Changes from the last version are in sections 5, 6 and 8.</p>"
    for n, (head, body) in enumerate(sections, 1):
        detail = ("In practice this means the ward checks the register each Monday, raises any gaps with biomedical engineering the same day, "
                  "and records what was done. Where a step cannot be completed, the reason is written down and the department head is told.")
        long_text += "%d. %s\n\n%s %s\n\n" % (n, head, body, detail)
        long_html += "<h3>%d. %s</h3><p>%s</p><p>%s</p>" % (n, head, body, detail)
    long_html += ("<h3>Annex B: calibration intervals</h3><table border='1' cellpadding='6'><tr><th>Equipment</th><th>Interval</th></tr>"
                  "<tr><td>Infusion pumps</td><td>12 months</td></tr><tr><td>Defibrillators</td><td>6 months</td></tr>"
                  "<tr><td>Patient monitors</td><td>12 months</td></tr><tr><td>Weighing scales</td><td>12 months</td></tr>"
                  "<tr><td>Radiotherapy dosimeters</td><td>24 months</td></tr></table><p>Quality committee</p>")
    long_text += "Annex B: calibration intervals - infusion pumps 12 months; defibrillators 6 months; patient monitors 12 months; weighing scales 12 months; radiotherapy dosimeters 24 months.\n\nQuality committee"
    add("quality", me, "Equipment policy, revised: please read before Friday", long_text, html=long_html, days=0, hour=10)
    # More ordinary mail across the ten days, so the list is long enough to scroll.
    for i in range(1, 31):
        who = ["meera", "ravi", "quality", "accounts", "it", "board", "training", "hr"][i % 8]
        subject = ["Ward round notes", "Pharmacy stock check", "Shift handover", "Equipment request", "Patient feedback summary",
                   "Committee reminder", "Leave application", "Visitor policy update", "Lab turnaround times", "Parking passes"][i % 10]
        add(who, me, "%s - %d" % (subject, i), "Brief note number %d about %s. Nothing urgent.\n\nThanks" % (i, subject.lower()),
            days=i % 10, hour=8 + (i % 9))
    return out


def api(url, token, method="GET", body=None):
    req = urllib.request.Request(url, method=method, headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"},
                                 data=json.dumps(body).encode() if body is not None else None)
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            return r.status, json.loads(r.read() or b"null")
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"null")


def main():
    a = argparse.ArgumentParser(description="Fill a LOCAL test mailbox with sample mail.")
    a.add_argument("--token", required=True)
    a.add_argument("--password", default=None, help="the mailbox password (required unless --backdate-only)")
    a.add_argument("--address", default="arun@qa.test")
    a.add_argument("--tenant", default="qa")
    a.add_argument("--smtp", default="127.0.0.1:2525")
    a.add_argument("--api", default="http://127.0.0.1:5081")
    a.add_argument("--backdate-db", default=None, help="local database name; spreads arrival times over ten days (uses psql)")
    a.add_argument("--backdate-only", action="store_true", help="only spread the arrival times of messages already delivered")
    a.add_argument("--only", help="send only the messages whose subject contains this text (e.g. \"Equipment policy\")")
    args = a.parse_args()
    domain = args.address.split("@", 1)[1]

    # 1. Tenant, domain and mailbox (created when missing; the password is always set).
    if args.backdate_only:
        args.password = None
    if api(f"{args.api}/api/tenants/{args.tenant}", args.token)[0] == 404:
        print("tenant:", api(f"{args.api}/api/tenants", args.token, "POST", {"slug": args.tenant, "displayName": args.tenant.upper(), "enabled": True})[0])
    api(f"{args.api}/api/tenant-domains", args.token, "POST", {"tenantSlug": args.tenant, "domain": domain})
    status, existing = api(f"{args.api}/api/mailboxes/{args.address}", args.token)
    body = {"tenantSlug": args.tenant, "address": args.address, "password": args.password,
            "displayName": (existing or {}).get("displayName") or "Arun Shiva B", "enabled": True,
            "quotaBytes": (existing or {}).get("quotaBytes") or 0}  # 0: Anjal's default (1 GiB)
    status, result = (200, None) if args.backdate_only else api(f"{args.api}/api/mailboxes", args.token, "POST", body)
    if status >= 300:
        sys.exit(f"mailbox: refused ({status}): {result}")
    print(f"mailbox {args.address}: ready")

    # 2. Deliver every message by SMTP.
    host, port = args.smtp.split(":")
    now = datetime.now(IST)
    sent = []

    def when_for(n, days, hour):
        w = (now - timedelta(days=days)).replace(hour=hour, minute=(n * 7) % 60, second=0, microsecond=0)
        return w if w <= now else now - timedelta(minutes=n)

    if args.backdate_only:
        sent = [(n, when_for(n, days, hour)) for n, (_, _, _, _, _, _, _, days, hour) in enumerate(messages(args.address, domain), 1)]
    with (smtplib.SMTP(host, int(port), timeout=30) if not args.backdate_only else open(os.devnull)) as s:
        for n, (frm, to, cc, subject, text, html, att, days, hour) in enumerate(messages(args.address, domain), 1):
            if args.only and args.only.lower() not in subject.lower():
                continue
            if args.backdate_only:
                break
            m = EmailMessage()
            when = when_for(n, days, hour)
            m["From"] = formataddr(frm)
            m["To"] = ", ".join(formataddr(t) for t in to)
            if cc:
                m["Cc"] = ", ".join(formataddr(c) for c in cc)
            m["Subject"] = subject
            m["Date"] = format_datetime(when)
            m["Message-ID"] = f"<seed-{n}@anjal.sample>"
            m.set_content(text)
            if html:
                m.add_alternative(html, subtype="html")
            for name, data, ctype in att:
                main_type, sub_type = ctype.split("/")
                m.add_attachment(data, maintype=main_type, subtype=sub_type, filename=name)
            rcpt = [args.address]
            try:
                s.send_message(m, from_addr=frm[1], to_addrs=rcpt)
                sent.append((n, when))
            except smtplib.SMTPException as e:
                print(f"  message {n} refused: {e}")
    if not args.backdate_only:
        print(f"delivered {len(sent)} messages")

    # 3. Optional: arrival times as in the Date line, so the days are spread (local test database only).
    if args.backdate_db:
        sql = "".join(f"UPDATE messages SET received_at = '{w.isoformat()}' WHERE message_id IN ('seed-{n}@anjal.sample', '<seed-{n}@anjal.sample>');\n" for n, w in sent)
        r = subprocess.run(["psql", "-h", "localhost", "-U", "postgres", "-d", args.backdate_db, "-q", "-v", "ON_ERROR_STOP=1"], input=sql, text=True,
                           capture_output=True, env=dict(os.environ))
        print("arrival times spread over ten days" if r.returncode == 0 else "backdating failed: " + r.stderr.strip()[:300])


if __name__ == "__main__":
    main()

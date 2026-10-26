using System.Runtime.CompilerServices;

namespace Anjal.Mailbox.Tests;

/// <summary>rc.25: mailbox keys in tests are 1024-bit RSA, so that making many mailboxes stays quick; the format is the same.</summary>
internal static class MailboxTestKeySize
{
#pragma warning disable CA2255 // A test assembly sets its own test-wide value once.
    [ModuleInitializer]
    internal static void Smaller() => Anjal.Store.MailKeys.TestKeyBits = 1024;
#pragma warning restore CA2255
}

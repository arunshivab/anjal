namespace Anjal.Mailbox.Tests;

/// <summary>
/// rc.30: a mail file another process holds for a moment (on Windows, an antivirus scan or the indexer) is renamed
/// once it is let go, instead of the webmail answering with a server error; any other error is passed on at once.
/// </summary>
public class Rc30MaildirFilesTests
{
    private static readonly int[] TwoWaits = new[] { 25, 50 };

    // Windows' "The process cannot access the file because it is being used by another process" (32).
    private static System.IO.IOException Held() => new("held", unchecked((int)0x80070020));

    [Fact]
    public void AFileHeldForAMoment_IsRenamedOnceLetGo()
    {
        int tries = 0;
        var waits = new List<int>();
        MaildirFiles.Move("a", "b", true, (s, t, o) =>
        {
            tries++;
            if (tries < 3)
            {
                throw Held();
            }
        }, waits.Add);
        Assert.Equal(3, tries);
        Assert.Equal(TwoWaits, waits);
    }

    [Fact]
    public void AFileHeldTooLong_PassesTheErrorOn()
    {
        int tries = 0;
        var waits = new List<int>();
        Assert.Throws<System.IO.IOException>(() => MaildirFiles.Move("a", "b", true, (s, t, o) =>
        {
            tries++;
            throw Held();
        }, waits.Add));
        Assert.Equal(MaildirFiles.Attempts, tries);
        Assert.Equal(MaildirFiles.Attempts - 1, waits.Count);
        Assert.True(waits.Sum() < 1000);
    }

    [Fact]
    public void OtherErrors_AreNotWaitedFor()
    {
        foreach (System.IO.IOException error in new System.IO.IOException[]
        {
            new System.IO.FileNotFoundException("gone"),
            new System.IO.DirectoryNotFoundException("gone"),
            new("disk full", unchecked((int)0x80070070)),
        })
        {
            int tries = 0;
            Assert.ThrowsAny<System.IO.IOException>(() => MaildirFiles.Move("a", "b", true, (s, t, o) =>
            {
                tries++;
                throw error;
            }, _ => { }));
            Assert.Equal(1, tries);
            Assert.False(MaildirFiles.IsHeldByAnother(error));
        }
        Assert.True(MaildirFiles.IsHeldByAnother(Held()));
        Assert.True(MaildirFiles.IsHeldByAnother(new System.IO.IOException("locked", unchecked((int)0x80070021))));
    }

    [Fact]
    public void ARealRename_StillWorks()
    {
        string dir = Path.Combine(Path.GetTempPath(), "anjal-rc30-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a");
            string b = Path.Combine(dir, "b");
            File.WriteAllText(a, "mail");
            MaildirFiles.Move(a, b, overwrite: true);
            Assert.False(File.Exists(a));
            Assert.Equal("mail", File.ReadAllText(b));
            MaildirFiles.Delete(b);
            Assert.False(File.Exists(b));
        }
        finally
        {
            TestFolders.Delete(dir);
        }
    }
}

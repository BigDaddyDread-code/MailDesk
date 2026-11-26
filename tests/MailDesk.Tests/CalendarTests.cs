using MailDesk.Calendar;
using MailDesk.Core.Models;
using MailDesk.Core.Storage;
using MailDesk.Mail;
using MimeKit;

namespace MailDesk.Tests;

public sealed class IcsCodecTests
{
    // Shaped like an Exchange/Teams invite: Windows time zone, folded lines, organizer, required and optional attendees.
    private const string ExchangeInvite = """
        BEGIN:VCALENDAR
        METHOD:REQUEST
        PRODID:Microsoft Exchange Server 2010
        VERSION:2.0
        BEGIN:VTIMEZONE
        TZID:New Zealand Standard Time
        BEGIN:STANDARD
        DTSTART:16010101T030000
        TZOFFSETFROM:+1300
        TZOFFSETTO:+1200
        RRULE:FREQ=YEARLY;INTERVAL=1;BYDAY=1SU;BYMONTH=4
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:16010101T020000
        TZOFFSETFROM:+1200
        TZOFFSETTO:+1300
        RRULE:FREQ=YEARLY;INTERVAL=1;BYDAY=-1SU;BYMONTH=9
        END:DAYLIGHT
        END:VTIMEZONE
        BEGIN:VEVENT
        ORGANIZER;CN=Org Aniser:mailto:org@example.test
        ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE;CN=Long Name That F
         olds:mailto:folded@example.test
        ATTENDEE;ROLE=OPT-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE;CN=Opt:mailto:opt@example.test
        DESCRIPTION;LANGUAGE=en-US:Microsoft Teams meeting\nJoin: https://teams.microsoft
         .com/meet/123456?p=abc\nMeeting ID: 123 456
        UID:040000008200E00074C5B7101A82E0080000000000000000000000000000000000000001
        SUMMARY;LANGUAGE=en-US:Interview with Example Corp
        DTSTART;TZID=New Zealand Standard Time:20261116T160000
        DTEND;TZID=New Zealand Standard Time:20261116T170000
        SEQUENCE:1
        STATUS:CONFIRMED
        LOCATION;LANGUAGE=en-US:Microsoft Teams Meeting
        X-MICROSOFT-SKYPETEAMSMEETINGURL:https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc%40thread.v2/0
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Parses_exchange_invite_with_windows_time_zone_and_folded_lines()
    {
        var items = IcsCodec.Parse(ExchangeInvite, accountId: 1, sourceMessageId: 42);
        var item = Assert.Single(items);

        Assert.Equal("Interview with Example Corp", item.Summary);
        Assert.Equal("org@example.test", item.OrganizerEmail);
        Assert.Equal("Org Aniser", item.OrganizerName);
        Assert.Equal(1, item.Sequence);
        Assert.Equal(42, item.SourceMessageId);
        Assert.Equal("Microsoft Teams Meeting", item.Location);
        Assert.StartsWith("https://teams.microsoft.com/l/meetup-join/", item.JoinUrl);
        Assert.False(item.AllDay);
        Assert.False(item.IsRecurring);

        // 16:00 New Zealand daylight time in November is 03:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), item.StartUtcMs);
        Assert.Equal(60 * 60 * 1000, item.EndUtcMs - item.StartUtcMs);

        Assert.Equal(2, item.Attendees.Count);
        Assert.Equal("folded@example.test", item.Attendees[0].Email);
        Assert.Equal("Long Name That Folds", item.Attendees[0].Name);
        Assert.False(item.Attendees[0].Optional);
        Assert.True(item.Attendees[1].Optional);
        Assert.Contains("BEGIN:VEVENT", item.RawIcs);
        Assert.Contains("BEGIN:VTIMEZONE", item.RawIcs);
    }

    [Fact]
    public void Non_calendar_text_yields_nothing()
    {
        Assert.Empty(IcsCodec.Parse("hello", 1));
        Assert.Empty(IcsCodec.Parse("", 1));
    }

    [Fact]
    public void Weekly_recurrence_expands_into_a_window()
    {
        const string weekly = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:test
            BEGIN:VEVENT
            UID:weekly-1
            SUMMARY:Stand-up
            DTSTART:20260105T090000Z
            DTEND:20260105T091500Z
            RRULE:FREQ=WEEKLY;BYDAY=MO
            END:VEVENT
            END:VCALENDAR
            """;
        var item = Assert.Single(IcsCodec.Parse(weekly, 1));
        Assert.True(item.IsRecurring);

        var from = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var occurrences = IcsCodec.Occurrences([item], from, to);
        Assert.Equal(4, occurrences.Count); // Mondays 2, 9, 16, 23 Feb 2026
        Assert.All(occurrences, o => Assert.Equal(DayOfWeek.Monday, o.Start.UtcDateTime.DayOfWeek));
        Assert.All(occurrences, o => Assert.Equal(TimeSpan.FromMinutes(15), o.End - o.Start));
    }

    [Fact]
    public void Reply_echoes_uid_and_sequence_with_our_participation_status()
    {
        var item = Assert.Single(IcsCodec.Parse(ExchangeInvite, 1));
        var reply = IcsCodec.BuildReply(item, "me@mine.test", "Me Myself", RsvpStatus.Accepted);

        Assert.Contains("METHOD:REPLY", reply);
        Assert.Contains("UID:040000008200E00074C5B7101A82E0080000000000000000000000000000000000000001", reply.Replace("\r\n ", ""));
        Assert.Contains("SEQUENCE:1", reply);
        Assert.Contains("PARTSTAT=ACCEPTED", reply);
        Assert.Contains("mailto:me@mine.test", reply);
        Assert.Contains("ORGANIZER", reply);
        Assert.DoesNotContain("opt@example.test", reply);
        Assert.Equal("Accepted: Interview with Example Corp", IcsCodec.ResponseSubject(item, RsvpStatus.Accepted));
    }

    [Fact]
    public void Request_carries_organizer_and_attendees_and_round_trips()
    {
        var start = new DateTimeOffset(2026, 12, 3, 14, 0, 0, TimeSpan.FromHours(13));
        var item = new CalendarItem
        {
            AccountId = 1, Uid = IcsCodec.NewUid("me@mine.test"), Summary = "Planning", Location = "Room 2", Description = "Bring the numbers.",
            StartUtcMs = start.ToUnixTimeMilliseconds(), EndUtcMs = start.AddMinutes(45).ToUnixTimeMilliseconds(),
            OrganizerEmail = "me@mine.test", OrganizerName = "Me Myself",
            Attendees = [new CalendarAttendee("pat@example.test", "Pat", "NEEDS-ACTION", false)],
        };
        Assert.EndsWith("@mine.test", item.Uid);

        var ics = IcsCodec.BuildRequest(item);
        var unfolded = ics.Replace("\r\n ", "");
        Assert.Contains("METHOD:REQUEST", unfolded);
        Assert.Contains("ORGANIZER;CN=Me Myself:mailto:me@mine.test", unfolded);
        Assert.Contains("RSVP=TRUE", unfolded);
        Assert.Contains("mailto:pat@example.test", unfolded);

        var parsed = Assert.Single(IcsCodec.Parse(ics, 1));
        Assert.Equal(item.Uid, parsed.Uid);
        Assert.Equal(item.StartUtcMs, parsed.StartUtcMs);
        Assert.Equal(item.EndUtcMs, parsed.EndUtcMs);
        Assert.Equal("Room 2", parsed.Location);
        Assert.Single(parsed.Attendees);

        item.Attendees = [];
        Assert.Contains("METHOD:PUBLISH", IcsCodec.BuildRequest(item));
        Assert.Contains("Planning", IcsCodec.InvitationText(item, "Me Myself"));
    }

    [Fact]
    public void Calendar_part_is_extracted_from_mime()
    {
        var message = new MimeMessage();
        var text = new TextPart("plain") { Text = "You are invited." };
        var cal = new TextPart("calendar") { Text = ExchangeInvite };
        cal.ContentType.Parameters.Add("method", "REQUEST");
        message.Body = new Multipart("alternative") { text, cal };

        var ics = CalendarExtractor.Extract(message);
        Assert.NotNull(ics);
        Assert.Contains("BEGIN:VCALENDAR", ics);

        var plain = new MimeMessage { Body = new TextPart("plain") { Text = "no invite" } };
        Assert.Null(CalendarExtractor.Extract(plain));
    }
}

public sealed class CalendarStoreTests : IAsyncLifetime
{
    private string _path = "";
    private SqliteMailStore _store = null!;
    private long _accountId;

    public async Task InitializeAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"maildesk-test-{Guid.NewGuid():N}.db");
        _store = new SqliteMailStore(_path);
        await _store.InitializeAsync();
        var account = new Account { Name = "T", Email = "t@x", ImapHost = "h", Username = "u", ProtectedPassword = "p" };
        _accountId = await _store.UpsertAccountAsync(account);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch { }
        return Task.CompletedTask;
    }

    private CalendarItem Item(int sequence, string summary) => new()
    {
        AccountId = _accountId, Uid = "uid-1", Sequence = sequence, Summary = summary,
        StartUtcMs = 1_000_000, EndUtcMs = 2_000_000, OrganizerEmail = "o@x.test",
        Attendees = [new CalendarAttendee("a@x.test", "A", "NEEDS-ACTION", false)],
    };

    [Fact]
    public async Task Upsert_keeps_newest_sequence_and_preserves_response()
    {
        var first = await _store.UpsertCalendarItemAsync(Item(1, "v1"));
        await _store.SetCalendarResponseAsync(first.Id, RsvpStatus.Accepted);

        var updated = await _store.UpsertCalendarItemAsync(Item(2, "v2"));
        Assert.Equal(first.Id, updated.Id);
        Assert.Equal(RsvpStatus.Accepted, updated.MyResponse);

        var stale = await _store.UpsertCalendarItemAsync(Item(1, "old"));
        Assert.Equal("v2", stale.Summary);

        var loaded = await _store.GetCalendarItemAsync(_accountId, "uid-1");
        Assert.Equal("v2", loaded!.Summary);
        Assert.Single(loaded.Attendees);

        var inWindow = await _store.GetCalendarItemsAsync(_accountId, 0, 5_000_000);
        Assert.Single(inWindow);
        var outside = await _store.GetCalendarItemsAsync(_accountId, 3_000_000, 5_000_000);
        Assert.Empty(outside);

        await _store.DeleteCalendarItemAsync(first.Id);
        Assert.Null(await _store.GetCalendarItemAsync(_accountId, "uid-1"));
    }

    [Fact]
    public async Task Body_round_trip_keeps_calendar_text()
    {
        var folder = new MailFolder { AccountId = _accountId, FullName = "INBOX", DisplayName = "Inbox", Kind = FolderKind.Inbox };
        await _store.UpsertFolderAsync(folder);
        await _store.UpsertMessagesAsync([new MessageSummary { FolderId = folder.Id, Uid = 1, Subject = "Invite" }]);
        var msg = (await _store.GetMessagesAsync(folder.Id)).Single();
        await _store.SaveBodyAsync(new MessageBody { MessageId = msg.Id, TextBody = "t", CalendarIcs = "BEGIN:VCALENDAR\nEND:VCALENDAR" });
        var body = await _store.GetBodyAsync(msg.Id);
        Assert.Equal("BEGIN:VCALENDAR\nEND:VCALENDAR", body!.CalendarIcs);
    }
}

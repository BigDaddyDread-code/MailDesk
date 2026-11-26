using MimeKit;

namespace MailDesk.Mail;

/// <summary>Finds iCalendar content in a message: a text/calendar part, or an attachment named *.ics.</summary>
public static class CalendarExtractor
{
    public static string? Extract(MimeMessage message)
    {
        foreach (var part in message.BodyParts)
        {
            if (part is TextPart tp && tp.ContentType.IsMimeType("text", "calendar"))
                return tp.Text;
        }
        foreach (var part in message.BodyParts)
        {
            if (part is MimePart mp && (mp.FileName?.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) == true
                                        || mp.ContentType.IsMimeType("application", "ics")))
            {
                using var ms = new MemoryStream();
                mp.Content.DecodeTo(ms);
                var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                if (text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) return text;
            }
        }
        return null;
    }
}

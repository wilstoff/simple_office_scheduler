using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using SimpleOfficeScheduler.Models;
using GraphEvent = Microsoft.Graph.Models.Event;

namespace SimpleOfficeScheduler.Services.Calendar;

public class GraphCalendarService : ICalendarInviteService
{
    private readonly GraphServiceClient _graphClient;
    private readonly GraphApiSettings _settings;
    private readonly ILogger<GraphCalendarService> _logger;

    public GraphCalendarService(IOptions<GraphApiSettings> settings, ILogger<GraphCalendarService> logger)
    {
        _logger = logger;
        _settings = settings.Value;

        var credential = new ClientSecretCredential(
            _settings.TenantId,
            _settings.ClientId,
            _settings.ClientSecret);
        _graphClient = new GraphServiceClient(credential);
    }

    private static string BuildMeetingBody(IReadOnlyList<EventSignup> signups)
    {
        var withMessages = signups.Where(s => !string.IsNullOrWhiteSpace(s.Message)).ToList();
        if (withMessages.Count == 0) return "";
        var items = string.Join("", withMessages.Select(s =>
            $"<li>{System.Net.WebUtility.HtmlEncode(s.User.DisplayName)} - {System.Net.WebUtility.HtmlEncode(s.Message)}</li>"));
        return $"<ul>{items}</ul>";
    }

    public async Task<string> CreateMeetingAsync(EventOccurrence occurrence, AppUser owner, AppUser signee, IReadOnlyList<EventSignup> allSignups)
    {
        var targetEmail = _settings.TargetMailbox;

        var graphEvent = new GraphEvent
        {
            Subject = occurrence.Event.Title,
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = BuildMeetingBody(allSignups)
            },
            Start = new DateTimeTimeZone
            {
                DateTime = occurrence.StartTime.ToDateTimeUnspecified().ToString("yyyy-MM-ddTHH:mm:ss"),
                TimeZone = occurrence.Event.TimeZoneId
            },
            End = new DateTimeTimeZone
            {
                DateTime = occurrence.EndTime.ToDateTimeUnspecified().ToString("yyyy-MM-ddTHH:mm:ss"),
                TimeZone = occurrence.Event.TimeZoneId
            },
            IsOnlineMeeting = true,
            OnlineMeetingProvider = OnlineMeetingProviderType.TeamsForBusiness,
            Attendees = new List<Attendee>
            {
                new()
                {
                    EmailAddress = new EmailAddress { Address = owner.Email, Name = owner.DisplayName },
                    Type = AttendeeType.Required
                },
                new()
                {
                    EmailAddress = new EmailAddress { Address = signee.Email, Name = signee.DisplayName },
                    Type = AttendeeType.Required
                }
            }
        };

        var created = await _graphClient.Users[targetEmail].Events.PostAsync(graphEvent);
        _logger.LogInformation("Created Teams meeting {GraphEventId} for event '{Title}' on calendar {TargetCalendar}",
            created?.Id, occurrence.Event.Title, targetEmail);

        return created?.Id ?? throw new InvalidOperationException("Graph API did not return an event ID.");
    }

    public async Task AddAttendeeAsync(string graphEventId, AppUser owner, AppUser newSignee, IReadOnlyList<EventSignup> allSignups)
    {
        var targetEmail = _settings.TargetMailbox;

        var existing = await _graphClient.Users[targetEmail].Events[graphEventId].GetAsync();
        if (existing is null) return;

        var attendees = existing.Attendees?.ToList() ?? new List<Attendee>();
        attendees.Add(new Attendee
        {
            EmailAddress = new EmailAddress { Address = newSignee.Email, Name = newSignee.DisplayName },
            Type = AttendeeType.Required
        });

        await _graphClient.Users[targetEmail].Events[graphEventId].PatchAsync(new GraphEvent
        {
            Attendees = attendees,
            Body = new ItemBody { ContentType = BodyType.Html, Content = BuildMeetingBody(allSignups) }
        });

        _logger.LogInformation("Added attendee {Email} to Teams meeting {GraphEventId}",
            newSignee.Email, graphEventId);
    }

    public async Task RemoveAttendeeAsync(string graphEventId, AppUser attendeeToRemove, IReadOnlyList<EventSignup> remainingSignups)
    {
        var targetEmail = _settings.TargetMailbox;

        var existing = await _graphClient.Users[targetEmail].Events[graphEventId].GetAsync();
        if (existing is null) return;

        var attendees = existing.Attendees?.ToList() ?? new List<Attendee>();
        attendees.RemoveAll(a =>
            string.Equals(a.EmailAddress?.Address, attendeeToRemove.Email, StringComparison.OrdinalIgnoreCase));

        await _graphClient.Users[targetEmail].Events[graphEventId].PatchAsync(new GraphEvent
        {
            Attendees = attendees,
            Body = new ItemBody { ContentType = BodyType.Html, Content = BuildMeetingBody(remainingSignups) }
        });

        _logger.LogInformation("Removed attendee {Email} from Teams meeting {GraphEventId}",
            attendeeToRemove.Email, graphEventId);
    }

    public async Task CancelMeetingAsync(string graphEventId, AppUser owner)
    {
        var targetEmail = _settings.TargetMailbox;

        await _graphClient.Users[targetEmail].Events[graphEventId].Cancel.PostAsync(
            new Microsoft.Graph.Users.Item.Events.Item.Cancel.CancelPostRequestBody
            {
                Comment = "This event has been cancelled."
            });

        _logger.LogInformation("Cancelled Teams meeting {GraphEventId}", graphEventId);
    }

    public async Task<string> CreateMeetingForContributorsAsync(EventOccurrence occurrence, AppUser owner, IReadOnlyList<AppUser> contributors)
    {
        var targetEmail = _settings.TargetMailbox;

        var attendees = new List<Attendee>
        {
            new()
            {
                EmailAddress = new EmailAddress { Address = owner.Email, Name = owner.DisplayName },
                Type = AttendeeType.Required
            }
        };
        attendees.AddRange(contributors.Select(c => new Attendee
        {
            EmailAddress = new EmailAddress { Address = c.Email, Name = c.DisplayName },
            Type = AttendeeType.Required
        }));

        var graphEvent = new GraphEvent
        {
            Subject = occurrence.DisplayName,
            Start = new DateTimeTimeZone
            {
                DateTime = occurrence.StartTime.ToDateTimeUnspecified().ToString("yyyy-MM-ddTHH:mm:ss"),
                TimeZone = occurrence.Event.TimeZoneId
            },
            End = new DateTimeTimeZone
            {
                DateTime = occurrence.EndTime.ToDateTimeUnspecified().ToString("yyyy-MM-ddTHH:mm:ss"),
                TimeZone = occurrence.Event.TimeZoneId
            },
            IsOnlineMeeting = true,
            OnlineMeetingProvider = OnlineMeetingProviderType.TeamsForBusiness,
            Attendees = attendees
        };

        var created = await _graphClient.Users[targetEmail].Events.PostAsync(graphEvent);
        _logger.LogInformation("Created Teams meeting {GraphEventId} for '{Title}' with {Count} contributors",
            created?.Id, occurrence.DisplayName, contributors.Count);

        return created?.Id ?? throw new InvalidOperationException("Graph API did not return an event ID.");
    }

    public async Task UpdateMeetingAttendeesAsync(string graphEventId, AppUser owner, IReadOnlyList<AppUser> contributors)
    {
        var targetEmail = _settings.TargetMailbox;

        var attendees = new List<Attendee>
        {
            new()
            {
                EmailAddress = new EmailAddress { Address = owner.Email, Name = owner.DisplayName },
                Type = AttendeeType.Required
            }
        };
        attendees.AddRange(contributors.Select(c => new Attendee
        {
            EmailAddress = new EmailAddress { Address = c.Email, Name = c.DisplayName },
            Type = AttendeeType.Required
        }));

        await _graphClient.Users[targetEmail].Events[graphEventId].PatchAsync(new GraphEvent
        {
            Attendees = attendees
        });

        _logger.LogInformation("Updated attendees for Teams meeting {GraphEventId} with {Count} contributors",
            graphEventId, contributors.Count);
    }
}

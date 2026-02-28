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

    public async Task<string> CreateMeetingAsync(EventOccurrence occurrence, AppUser owner, AppUser signee)
    {
        var targetEmail = _settings.TargetMailbox;

        var graphEvent = new GraphEvent
        {
            Subject = occurrence.Event.Title,
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = occurrence.Event.Description ?? ""
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

    public async Task AddAttendeeAsync(string graphEventId, AppUser owner, AppUser newSignee)
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
            Attendees = attendees
        });

        _logger.LogInformation("Added attendee {Email} to Teams meeting {GraphEventId}",
            newSignee.Email, graphEventId);
    }

    public async Task RemoveAttendeeAsync(string graphEventId, AppUser attendeeToRemove)
    {
        var targetEmail = _settings.TargetMailbox;

        var existing = await _graphClient.Users[targetEmail].Events[graphEventId].GetAsync();
        if (existing is null) return;

        var attendees = existing.Attendees?.ToList() ?? new List<Attendee>();
        attendees.RemoveAll(a =>
            string.Equals(a.EmailAddress?.Address, attendeeToRemove.Email, StringComparison.OrdinalIgnoreCase));

        await _graphClient.Users[targetEmail].Events[graphEventId].PatchAsync(new GraphEvent
        {
            Attendees = attendees
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
}

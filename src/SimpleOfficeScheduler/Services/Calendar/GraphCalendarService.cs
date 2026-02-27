using Azure.Core;
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

        TokenCredential credential;
        if (_settings.UseDelegatedAuth)
        {
            credential = new UsernamePasswordCredential(
                _settings.ServiceAccountEmail,
                _settings.ServiceAccountPassword,
                _settings.TenantId,
                _settings.ClientId);
            _logger.LogInformation("Graph calendar using delegated auth (ROPC) with service account {Email}",
                _settings.ServiceAccountEmail);
        }
        else
        {
            credential = new ClientSecretCredential(
                _settings.TenantId,
                _settings.ClientId,
                _settings.ClientSecret);
            _logger.LogInformation("Graph calendar using application auth (client credentials)");
        }

        _graphClient = new GraphServiceClient(credential);
    }

    /// <summary>
    /// Returns the email of the mailbox to target for calendar operations.
    /// In delegated mode, this is the service account. In application mode, this is the event owner.
    /// </summary>
    internal string GetCalendarTargetEmail(AppUser owner)
    {
        return _settings.UseDelegatedAuth
            ? _settings.ServiceAccountEmail
            : owner.Email;
    }

    public async Task<string> CreateMeetingAsync(EventOccurrence occurrence, AppUser owner, AppUser signee)
    {
        var targetEmail = GetCalendarTargetEmail(owner);

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
        var targetEmail = GetCalendarTargetEmail(owner);

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

    public async Task CancelMeetingAsync(string graphEventId, AppUser owner)
    {
        var targetEmail = GetCalendarTargetEmail(owner);

        await _graphClient.Users[targetEmail].Events[graphEventId].Cancel.PostAsync(
            new Microsoft.Graph.Users.Item.Events.Item.Cancel.CancelPostRequestBody
            {
                Comment = "This event has been cancelled."
            });

        _logger.LogInformation("Cancelled Teams meeting {GraphEventId}", graphEventId);
    }
}

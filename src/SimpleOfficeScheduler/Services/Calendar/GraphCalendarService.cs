using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using NodaTime;
using SimpleOfficeScheduler.Models;
using AppEvent = SimpleOfficeScheduler.Models.Event;
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

    public async Task UpdateMeetingSubjectAsync(string graphEventId, string subject)
    {
        var targetEmail = _settings.TargetMailbox;

        await _graphClient.Users[targetEmail].Events[graphEventId].PatchAsync(new GraphEvent
        {
            Subject = subject
        });

        _logger.LogInformation("Updated subject for Teams meeting {GraphEventId} to '{Subject}'",
            graphEventId, subject);
    }

    // ── Recurring series (workshops) ────────────────────────────────

    private static List<Attendee> RequiredAttendees(IEnumerable<AppUser> users) =>
        users.Select(u => new Attendee
        {
            EmailAddress = new EmailAddress { Address = u.Email, Name = u.DisplayName },
            Type = AttendeeType.Required
        }).ToList();

    public async Task<string> CreateSeriesAsync(AppEvent evt, IReadOnlyList<AppUser> owners, LocalDate windowEnd)
    {
        var targetEmail = _settings.TargetMailbox;

        var graphEvent = new GraphEvent
        {
            Subject = evt.Title,
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = string.IsNullOrWhiteSpace(evt.Description)
                    ? ""
                    : System.Net.WebUtility.HtmlEncode(evt.Description)
            },
            Start = new DateTimeTimeZone
            {
                DateTime = evt.StartTime.ToDateTimeUnspecified().ToString("yyyy-MM-ddTHH:mm:ss"),
                TimeZone = evt.TimeZoneId
            },
            End = new DateTimeTimeZone
            {
                DateTime = evt.EndTime.ToDateTimeUnspecified().ToString("yyyy-MM-ddTHH:mm:ss"),
                TimeZone = evt.TimeZoneId
            },
            Recurrence = GraphRecurrenceMapper.Map(evt, windowEnd),
            IsOnlineMeeting = true,
            OnlineMeetingProvider = OnlineMeetingProviderType.TeamsForBusiness,
            Attendees = RequiredAttendees(owners)
        };

        var created = await _graphClient.Users[targetEmail].Events.PostAsync(graphEvent);
        _logger.LogInformation("Created Teams series {GraphEventId} for workshop '{Title}' with {Count} owners through {WindowEnd}",
            created?.Id, evt.Title, owners.Count, windowEnd);

        return created?.Id ?? throw new InvalidOperationException("Graph API did not return an event ID.");
    }

    public async Task ExtendSeriesRangeAsync(string graphSeriesId, AppEvent evt, LocalDate newWindowEnd)
    {
        var targetEmail = _settings.TargetMailbox;

        // Patching the recurrence re-sends the update to the resource attendee, which is what makes
        // the room re-evaluate the newly added dates against its booking window.
        await _graphClient.Users[targetEmail].Events[graphSeriesId].PatchAsync(new GraphEvent
        {
            Recurrence = GraphRecurrenceMapper.Map(evt, newWindowEnd)
        });

        _logger.LogInformation("Extended Teams series {GraphEventId} range to {WindowEnd}",
            graphSeriesId, newWindowEnd);
    }

    public async Task<string?> GetInstanceIdAsync(string graphSeriesId, LocalDateTime occurrenceStart, string timeZoneId)
    {
        var targetEmail = _settings.TargetMailbox;

        // Query a one-day window around the occurrence and match on local start time. Graph wants
        // the window in the same timezone the series was created with.
        var windowStart = occurrenceStart.Date.At(LocalTime.Midnight);
        var windowEnd = windowStart.PlusDays(1);

        var instances = await _graphClient.Users[targetEmail].Events[graphSeriesId].Instances
            .GetAsync(config =>
            {
                config.QueryParameters.StartDateTime = windowStart.ToDateTimeUnspecified().ToString("yyyy-MM-ddTHH:mm:ss");
                config.QueryParameters.EndDateTime = windowEnd.ToDateTimeUnspecified().ToString("yyyy-MM-ddTHH:mm:ss");
                config.Headers.Add("Prefer", $"outlook.timezone=\"{timeZoneId}\"");
            });

        var target = occurrenceStart.ToDateTimeUnspecified().ToString("yyyy-MM-ddTHH:mm:ss");
        var match = instances?.Value?.FirstOrDefault(i =>
            i.Start?.DateTime is not null && i.Start.DateTime.StartsWith(target, StringComparison.Ordinal));

        if (match is null)
        {
            _logger.LogWarning("No Teams series instance found for series {GraphEventId} at {Start}",
                graphSeriesId, occurrenceStart);
        }

        return match?.Id;
    }

    public async Task PatchInstanceAttendeesAsync(string instanceId, IReadOnlyList<AppUser> owners, IReadOnlyList<EventSignup> signups)
    {
        var targetEmail = _settings.TargetMailbox;

        var attendees = RequiredAttendees(owners);
        attendees.AddRange(signups
            .Where(s => s.User is not null && owners.All(o => !string.Equals(o.Email, s.User.Email, StringComparison.OrdinalIgnoreCase)))
            .Select(s => new Attendee
            {
                EmailAddress = new EmailAddress { Address = s.User.Email, Name = s.User.DisplayName },
                Type = AttendeeType.Optional
            }));

        await _graphClient.Users[targetEmail].Events[instanceId].PatchAsync(new GraphEvent
        {
            Attendees = attendees,
            Body = new ItemBody { ContentType = BodyType.Html, Content = BuildMeetingBody(signups) }
        });

        _logger.LogInformation("Patched attendees on Teams series instance {InstanceId}: {OwnerCount} owners, {SignupCount} signups",
            instanceId, owners.Count, signups.Count);
    }

    public async Task UpdateSeriesOwnersAsync(string graphSeriesId, IReadOnlyList<AppUser> owners)
    {
        var targetEmail = _settings.TargetMailbox;

        await _graphClient.Users[targetEmail].Events[graphSeriesId].PatchAsync(new GraphEvent
        {
            Attendees = RequiredAttendees(owners)
        });

        _logger.LogInformation("Updated owners on Teams series {GraphEventId} to {Count} attendees",
            graphSeriesId, owners.Count);
    }
}

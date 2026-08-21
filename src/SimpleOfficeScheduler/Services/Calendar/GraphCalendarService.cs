using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using NodaTime;
using SimpleOfficeScheduler.Models;
using AppEvent = SimpleOfficeScheduler.Models.Event;
using AppRoom = SimpleOfficeScheduler.Models.Room;
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

    /// <summary>
    /// A room is booked by adding its mailbox as a resource attendee and naming it as the location.
    /// This needs no permission beyond the Calendars.ReadWrite the app already has on the target
    /// mailbox; the room's own booking attendant decides whether to accept.
    /// </summary>
    private static Attendee ResourceAttendee(AppRoom room) => new()
    {
        EmailAddress = new EmailAddress { Address = room.Email, Name = room.DisplayName },
        Type = AttendeeType.Resource
    };

    private static Location RoomLocation(AppRoom room) => new()
    {
        DisplayName = room.DisplayName,
        LocationEmailAddress = room.Email,
        LocationType = LocationType.ConferenceRoom
    };

    private static List<Attendee> RequiredAttendees(IEnumerable<AppUser> users) =>
        users.Select(u => new Attendee
        {
            EmailAddress = new EmailAddress { Address = u.Email, Name = u.DisplayName },
            Type = AttendeeType.Required
        }).ToList();

    public async Task<string> CreateSeriesAsync(AppEvent evt, IReadOnlyList<AppUser> owners, LocalDate windowEnd, AppRoom? room)
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

        if (room is not null)
        {
            graphEvent.Attendees!.Add(ResourceAttendee(room));
            graphEvent.Location = RoomLocation(room);
        }

        var created = await _graphClient.Users[targetEmail].Events.PostAsync(graphEvent);
        _logger.LogInformation("Created Teams series {GraphEventId} for workshop '{Title}' with {Count} owners through {WindowEnd}",
            created?.Id, evt.Title, owners.Count, windowEnd);

        return created?.Id ?? throw new InvalidOperationException("Graph API did not return an event ID.");
    }

    public async Task UpdateSeriesScheduleAsync(string graphSeriesId, AppEvent evt, LocalDate windowEnd)
    {
        var targetEmail = _settings.TargetMailbox;

        // Sending Recurrence on an event that has none converts a single meeting into a series,
        // which is what happens when a workshop is made recurring after it was created.
        await _graphClient.Users[targetEmail].Events[graphSeriesId].PatchAsync(new GraphEvent
        {
            Subject = evt.Title,
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
            Recurrence = GraphRecurrenceMapper.Map(evt, windowEnd)
        });

        _logger.LogInformation("Updated Teams series {GraphEventId} to '{Title}' {Start}-{End}, recurring={Recurring}, through {WindowEnd}",
            graphSeriesId, evt.Title, evt.StartTime, evt.EndTime, evt.Recurrence is not null, windowEnd);
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

    /// <summary>
    /// Builds the attendee list for one series instance: the booked room carried over from the
    /// instance as it stands, the owners as required, and the current signups as optional.
    ///
    /// Carrying the resource attendee over is the point. Patching an instance replaces its whole
    /// attendee list, so sending only people cancels the room's hold on that single date while the
    /// rest of the series keeps it. People are rebuilt from scratch rather than merged so that
    /// someone who cancelled actually comes off the instance.
    /// </summary>
    internal static List<Attendee> MergeInstanceAttendees(
        IEnumerable<Attendee>? existing,
        IReadOnlyList<AppUser> owners,
        IReadOnlyList<EventSignup> signups)
    {
        var attendees = (existing ?? Enumerable.Empty<Attendee>())
            .Where(a => a.Type == AttendeeType.Resource)
            .ToList();

        attendees.AddRange(RequiredAttendees(owners));

        attendees.AddRange(signups
            .Where(s => s.User is not null
                && owners.All(o => !string.Equals(o.Email, s.User.Email, StringComparison.OrdinalIgnoreCase)))
            .Select(s => new Attendee
            {
                EmailAddress = new EmailAddress { Address = s.User.Email, Name = s.User.DisplayName },
                Type = AttendeeType.Optional
            }));

        return attendees;
    }

    public async Task PatchInstanceAttendeesAsync(string instanceId, IReadOnlyList<AppUser> owners, IReadOnlyList<EventSignup> signups)
    {
        var targetEmail = _settings.TargetMailbox;

        // Read the instance first so the room booked on the series survives the patch.
        var existing = await _graphClient.Users[targetEmail].Events[instanceId].GetAsync();
        var attendees = MergeInstanceAttendees(existing?.Attendees, owners, signups);

        await _graphClient.Users[targetEmail].Events[instanceId].PatchAsync(new GraphEvent
        {
            Attendees = attendees,
            Body = new ItemBody { ContentType = BodyType.Html, Content = BuildMeetingBody(signups) }
        });

        _logger.LogInformation(
            "Patched attendees on Teams series instance {InstanceId}: {OwnerCount} owners, {SignupCount} signups, {ResourceCount} resources kept",
            instanceId, owners.Count, signups.Count, attendees.Count(a => a.Type == AttendeeType.Resource));
    }

    public async Task UpdateSeriesRoomAsync(string graphSeriesId, AppRoom? room)
    {
        var targetEmail = _settings.TargetMailbox;

        var existing = await _graphClient.Users[targetEmail].Events[graphSeriesId].GetAsync();
        if (existing is null) return;

        // Drop any previous resource attendee, then add the new one.
        var attendees = (existing.Attendees ?? new List<Attendee>())
            .Where(a => a.Type != AttendeeType.Resource)
            .ToList();

        if (room is not null)
            attendees.Add(ResourceAttendee(room));

        await _graphClient.Users[targetEmail].Events[graphSeriesId].PatchAsync(new GraphEvent
        {
            Attendees = attendees,
            Location = room is not null ? RoomLocation(room) : new Location { DisplayName = "" }
        });

        _logger.LogInformation("Set room on Teams series {GraphEventId} to {Room}",
            graphSeriesId, room?.Email ?? "(none)");
    }

    public async Task<RoomBookingOutcome?> GetRoomResponseAsync(string graphEventId, string roomEmail)
    {
        var targetEmail = _settings.TargetMailbox;

        var existing = await _graphClient.Users[targetEmail].Events[graphEventId].GetAsync();
        var resource = existing?.Attendees?.FirstOrDefault(a =>
            string.Equals(a.EmailAddress?.Address, roomEmail, StringComparison.OrdinalIgnoreCase));

        if (resource is null)
        {
            return new RoomBookingOutcome
            {
                Status = RoomBookingStatus.Failed,
                Error = "The room is not on the meeting."
            };
        }

        return resource.Status?.Response switch
        {
            ResponseType.Accepted or ResponseType.TentativelyAccepted =>
                new RoomBookingOutcome { Status = RoomBookingStatus.Booked },
            ResponseType.Declined =>
                new RoomBookingOutcome
                {
                    Status = RoomBookingStatus.Declined,
                    Error = "The room declined the booking. It is most likely already reserved, or "
                        + "the date is outside its booking window."
                },
            // NotResponded / None: the booking attendant has not replied yet.
            _ => null
        };
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

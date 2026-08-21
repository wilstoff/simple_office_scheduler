using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using NodaTime;
using SimpleOfficeScheduler.Models;
using AppRoom = SimpleOfficeScheduler.Models.Room;
using GraphRoom = Microsoft.Graph.Models.Room;

namespace SimpleOfficeScheduler.Services.Rooms;

/// <summary>
/// Reads rooms and their free/busy from Microsoft Graph, falling back to the configured list.
///
/// Two different permissions are involved, and only one of them is at risk from an Exchange
/// Application Access Policy:
///
/// - Listing rooms uses GET /places/microsoft.graph.room and needs Place.Read.All (application,
///   admin consent). Place objects are not mailbox-scoped, so a RestrictAccess policy does not
///   block this.
/// - Free/busy uses POST /users/{TargetMailbox}/calendar/getSchedule and needs Calendars.Read
///   (application). This IS mailbox-scoped, so a policy that covers only the target mailbox may not
///   reach room mailboxes. Graph reports that per schedule via ScheduleInformation.Error rather
///   than failing the whole call, and a room it cannot read comes back all-free. Reading the error
///   is what separates "free" from "not allowed to look"; guessing from an all-zeros view would
///   also hide the grid whenever every room genuinely is free, which is when it is most useful.
/// </summary>
public class GraphRoomService : IRoomService
{
    private readonly GraphServiceClient _graphClient;
    private readonly GraphApiSettings _settings;
    private readonly IRoomService _fallback;
    private readonly ILogger<GraphRoomService> _logger;

    private IReadOnlyList<AppRoom>? _cachedRooms;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public GraphRoomService(
        IOptions<GraphApiSettings> settings,
        ConfigRoomService fallback,
        ILogger<GraphRoomService> logger)
    {
        _settings = settings.Value;
        _fallback = fallback;
        _logger = logger;

        var credential = new ClientSecretCredential(
            _settings.TenantId,
            _settings.ClientId,
            _settings.ClientSecret);
        _graphClient = new GraphServiceClient(credential);
    }

    public async Task<IReadOnlyList<AppRoom>> GetRoomsAsync(CancellationToken ct = default)
    {
        if (_cachedRooms is not null) return _cachedRooms;

        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_cachedRooms is not null) return _cachedRooms;

            try
            {
                var rooms = string.IsNullOrWhiteSpace(_settings.RoomListEmail)
                    ? await FetchAllRoomsAsync(ct)
                    : await FetchRoomListAsync(_settings.RoomListEmail, ct);

                if (rooms.Count == 0)
                {
                    _logger.LogWarning("Graph returned no rooms; falling back to the configured room list.");
                    _cachedRooms = await _fallback.GetRoomsAsync(ct);
                }
                else
                {
                    _logger.LogInformation("Loaded {Count} rooms from Graph.", rooms.Count);
                    _cachedRooms = rooms;
                }
            }
            catch (Exception ex)
            {
                // Most likely the Place.Read.All permission has not been consented to.
                _logger.LogError(ex, "Failed to read rooms from Graph; falling back to the configured room list.");
                _cachedRooms = await _fallback.GetRoomsAsync(ct);
            }

            return _cachedRooms;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task<List<AppRoom>> FetchAllRoomsAsync(CancellationToken ct)
    {
        var response = await _graphClient.Places.GraphRoom.GetAsync(cancellationToken: ct);
        return (response?.Value ?? new List<GraphRoom>())
            .Select(MapRoom).ToList();
    }

    private async Task<List<AppRoom>> FetchRoomListAsync(string roomListEmail, CancellationToken ct)
    {
        var response = await _graphClient.Places[roomListEmail].GraphRoomList.Rooms
            .GetAsync(cancellationToken: ct);
        return (response?.Value ?? new List<GraphRoom>())
            .Select(MapRoom).ToList();
    }

    private static AppRoom MapRoom(GraphRoom r) => new()
    {
        Email = r.EmailAddress ?? "",
        DisplayName = string.IsNullOrWhiteSpace(r.DisplayName) ? r.EmailAddress ?? "" : r.DisplayName,
        Capacity = r.Capacity,
        Building = r.Building,
        FloorLabel = r.FloorLabel
    };

    public async Task<IReadOnlyList<RoomAvailability>?> GetAvailabilityAsync(
        IReadOnlyList<string> roomEmails,
        LocalDateTime start,
        LocalDateTime end,
        string timeZoneId,
        CancellationToken ct = default)
    {
        if (roomEmails.Count == 0) return null;

        try
        {
            var body = new Microsoft.Graph.Users.Item.Calendar.GetSchedule.GetSchedulePostRequestBody
            {
                Schedules = roomEmails.ToList(),
                StartTime = new DateTimeTimeZone
                {
                    DateTime = start.ToDateTimeUnspecified().ToString("yyyy-MM-ddTHH:mm:ss"),
                    TimeZone = timeZoneId
                },
                EndTime = new DateTimeTimeZone
                {
                    DateTime = end.ToDateTimeUnspecified().ToString("yyyy-MM-ddTHH:mm:ss"),
                    TimeZone = timeZoneId
                },
                AvailabilityViewInterval = 30
            };

            var response = await _graphClient.Users[_settings.TargetMailbox].Calendar.GetSchedule
                .PostAsGetSchedulePostResponseAsync(body, cancellationToken: ct);

            var items = response?.Value ?? new List<ScheduleInformation>();

            if (!HasUsableAvailability(items))
            {
                _logger.LogWarning(
                    "getSchedule could not read any of the {Count} requested rooms. The app's Calendars.Read "
                    + "permission most likely does not reach room mailboxes under the current Application "
                    + "Access Policy. Hiding the availability grid.",
                    roomEmails.Count);
                return null;
            }

            // Rooms Graph could not read are dropped rather than reported as free.
            return items
                .Where(i => i.Error is null)
                .Select(i => new RoomAvailability
                {
                    Email = i.ScheduleId ?? "",
                    AvailabilityView = i.AvailabilityView ?? "",
                    IsBusy = IsBusy(i.AvailabilityView ?? "")
                }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read room availability from Graph. Hiding the availability grid.");
            return null;
        }
    }

    /// <summary>
    /// True when Graph could actually read at least one of the requested schedules. A schedule the
    /// app is not permitted to read comes back with an Error set, so an empty result or one where
    /// every entry errored means there is no availability data to show.
    /// </summary>
    internal static bool HasUsableAvailability(IReadOnlyCollection<ScheduleInformation> schedules) =>
        schedules.Count > 0 && schedules.Any(s => s.Error is null);

    /// <summary>Graph encodes free as '0'; anything else is some degree of busy.</summary>
    internal static bool IsBusy(string availabilityView) =>
        availabilityView.Any(c => c != '0');
}
